using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TransactHub.Ports;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The payment events out (R-09-152, R-09-153; owner's decisions of 23 November 2026, docs/OWNER-ACTIONS.md A5): the relay follows
/// the store's transactional outbox, in which every fact is announced in the same save as the fact itself (Facts.cs), and publishes
/// each fact's announcement, in order, through the bank's binding of <c>p.port.event-stream.v1</c>. An event is the fact's
/// reference only (its subject, number, kind, code and time), never its data, so no personal value leaves, masked or not.
/// <para>
/// Delivery is at least once and in order per payment: a batch is published, and only then is its position recorded, as a row of
/// its own (<c>stream_position</c>, insert-only); a restart resumes after the last recorded row, so a batch whose position was not
/// recorded is sent again. The relay publishes an unbroken run of outbox numbers: a missing number (a save still in flight) stops the
/// batch until it arrives, or until the row after it is older than the gap timeout. Then the relay goes on, but it does not forget the
/// number (R-09-175, Issue I-257): the range it passed over is kept with the position, and every later pass looks for it again, so a
/// save that commits after the timeout is published on the next pass, before anything newer. A range is dropped only once it is older
/// than the gap horizon, far longer than any save may take; each range dropped is written in the position row, so it can be looked for.
/// Order per payment holds: a payment's next fact can only be saved once the fact before it has committed (the chain's hash).
/// </para>
/// <para>
/// The binding names the stream and where a new one starts (R-09-176, Issue I-256): <c>stream</c> (default the port's name) names
/// the position, so a bank that wants a new topic to get the history names a new stream, and a topic changed under the same name goes
/// on from its position; <c>from</c> (<c>earliest</c>, the default, or <c>latest</c>) is read only while the stream has no position.
/// <c>gapSeconds</c> (default 10, from 1 to 600) and <c>gapHorizonMinutes</c> (default 1440, from 10 to 10080) are the gap dials.
/// A value outside its bounds is refused, never clamped. With no binding in force the relay waits, keeping its position.
/// </para>
/// </summary>
public sealed class EventStreamRelay(StoreOptions store, string tenant, PortBindings bindings, ISecretResolver? secrets = null, TimeProvider? clock = null) : IDisposable
{
    /// <summary>The stream's name in <c>stream_position</c> when the binding names none: the port's.</summary>
    public const string Stream = EventStreamAdapters.Port;
    public const string StreamSetting = "stream", FromSetting = "from", GapSecondsSetting = "gapSeconds", GapHorizonSetting = "gapHorizonMinutes";
    public const int DefaultGapSeconds = 10, MinGapSeconds = 1, MaxGapSeconds = 600;
    public const int DefaultGapHorizonMinutes = 1440, MinGapHorizonMinutes = 10, MaxGapHorizonMinutes = 10080;
    static readonly Regex StreamName = new("^[a-z0-9][a-z0-9._-]{0,127}$");
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    readonly SemaphoreSlim gate = new(1, 1);
    (PortBinding Binding, IEventStream Stream)? current;
    /// <summary>Overrides the binding's <c>gapSeconds</c> (a test's).</summary>
    public TimeSpan? GapTimeout { get; init; }
    /// <summary>Overrides the binding's <c>gapHorizonMinutes</c> (a test's).</summary>
    public TimeSpan? GapHorizon { get; init; }
    public int BatchSize { get; init; } = 500;

    /// <summary>A stream's dials, read from its binding's settings.</summary>
    public sealed record Dials(string Stream, bool FromLatest, TimeSpan Gap, TimeSpan Horizon);

    public static Dials DialsOf(PortBinding b)
    {
        var name = b.Setting(StreamSetting, Stream);
        if (!StreamName.IsMatch(name)) throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: {StreamSetting} is '{name}', not a name of small letters, digits, dots and dashes");
        var from = b.Setting(FromSetting, "earliest");
        if (from is not ("earliest" or "latest")) throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: {FromSetting} is '{from}', not earliest or latest");
        static int Read(PortBinding b, string key, int fallback, int min, int max)
        {
            var s = b.Setting(key, fallback.ToString(CultureInfo.InvariantCulture));
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
                throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: {key} is '{s}', not a whole number from {min} to {max}");
            return v;
        }
        return new(name, from == "latest", TimeSpan.FromSeconds(Read(b, GapSecondsSetting, DefaultGapSeconds, MinGapSeconds, MaxGapSeconds)),
            TimeSpan.FromMinutes(Read(b, GapHorizonSetting, DefaultGapHorizonMinutes, MinGapHorizonMinutes, MaxGapHorizonMinutes)));
    }

    /// <summary>A range of outbox numbers passed over, and when.</summary>
    public sealed record Gap(long From, long To, DateTimeOffset At);

    /// <summary>The last outbox row published of the default stream, with every row before it but its open gaps (0 before the first).</summary>
    public Task<long> Position(CancellationToken ct = default) => Position(Stream, ct);

    public async Task<long> Position(string stream, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        // By number, or in commit order the last row published (R-09-214): the highest of either a position names.
        var rows = await db.StreamPositions.AsNoTracking().Where(s => s.Stream == stream).Select(s => new { s.Through, s.ThroughId }).ToListAsync(ct);
        return rows.Count == 0 ? 0 : rows.Max(r => Math.Max(r.Through, r.ThroughId ?? 0));
    }

    /// <summary>The ranges a stream has passed over and still looks for.</summary>
    public async Task<IReadOnlyList<Gap>> OpenGaps(string stream = Stream, CancellationToken ct = default) => GapsOf(await LastGaps(stream, ct), "open");

    /// <summary>The ranges a stream's latest position row says it dropped.</summary>
    public async Task<IReadOnlyList<Gap>> DroppedGaps(string stream = Stream, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var all = await db.StreamPositions.AsNoTracking().Where(s => s.Stream == stream && s.GapsJson != null).Select(s => s.GapsJson).ToListAsync(ct);
        return all.SelectMany(j => GapsOf(j, "dropped")).ToList();
    }

    async Task<string?> LastGaps(string stream, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        return await db.StreamPositions.AsNoTracking().Where(s => s.Stream == stream).OrderByDescending(s => s.Id).Select(s => s.GapsJson).FirstOrDefaultAsync(ct);
    }

    static List<Gap> GapsOf(string? json, string which) =>
        json is null || JsonNode.Parse(json)?[which] is not JsonArray a ? []
            : a.OfType<JsonObject>().Select(g => new Gap((long)g["from"]!, (long)g["to"]!, DateTimeOffset.Parse((string)g["at"]!, CultureInfo.InvariantCulture))).ToList();

    static JsonArray Json(IEnumerable<Gap> gs) => new(gs.Select(g => (JsonNode)new JsonObject { ["from"] = g.From, ["to"] = g.To, ["at"] = g.At.ToString("O") }).ToArray());

    (PortBinding Binding, IEventStream Stream)? Bound()
    {
        PortBinding b;
        try { b = bindings.InForce(tenant, EventStreamAdapters.Port, clock.GetUtcNow()); }
        catch (PortBindingException) { return null; }
        if (current is { } c && ReferenceEquals(c.Binding, b)) return c;
        current?.Stream.Dispose();
        current = (b, EventStreamAdapters.Create(b, secrets));
        return current.Value;
    }

    /// <summary>One pass: publishes what is new, then records the position. Returns how many outbox rows it passed or published late.</summary>
    public async Task<int> Poll(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Bound() is not { } bound) return 0;
            var dials = DialsOf(bound.Binding);
            var gapTimeout = GapTimeout ?? dials.Gap;
            var horizon = GapHorizon ?? dials.Horizon;
            await using var db = StoreDbContext.Open(store);
            var now = clock.GetUtcNow();
            var last = await db.StreamPositions.AsNoTracking().Where(s => s.Stream == dials.Stream).OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);
            if (last is null && dials.FromLatest)
            {
                // A new stream that starts at the newest fact: it records where it starts and sends no history (Issue I-256).
                var head = await db.Outbox.AsNoTracking().MaxAsync(x => (long?)x.Id, ct) ?? 0;
                db.StreamPositions.Add(new StreamPositionRow { Stream = dials.Stream, Through = head, Events = 0, At = StoreTime.Of(now) });
                await db.SaveChangesAsync(ct);
                return 0;
            }
            if (CommitOrder) return await PollInCommitOrder(db, bound, dials, last, now, horizon, ct);
            var through = last is null ? 0 : await db.StreamPositions.AsNoTracking().Where(s => s.Stream == dials.Stream).MaxAsync(s => s.Through, ct);
            var open = GapsOf(last?.GapsJson, "open");

            // The saves that committed after the stream had passed them (Issue I-257): published first, before anything newer.
            var late = new List<OutboxRow>();
            foreach (var g in open)
                late.AddRange(await db.Outbox.AsNoTracking().Where(x => x.Id >= g.From && x.Id <= g.To).OrderBy(x => x.Id).ToListAsync(ct));
            late = late.OrderBy(x => x.Id).ToList();
            var found = late.Select(x => x.Id).ToHashSet();
            var still = open.SelectMany(g => Without(g, found)).ToList();
            var dropped = still.Where(g => now - g.At > horizon).ToList();
            still = still.Where(g => now - g.At <= horizon).ToList();

            var rows = await db.Outbox.AsNoTracking().Where(x => x.Id > through).OrderBy(x => x.Id).Take(BatchSize).ToListAsync(ct);
            var run = new List<OutboxRow>();
            var expected = through + 1;
            foreach (var r in rows)
            {
                if (r.Id != expected)
                {
                    if (now - r.At <= gapTimeout) break;
                    still.Add(new Gap(expected, r.Id - 1, now));
                }
                run.Add(r);
                expected = r.Id + 1;
            }
            if (run.Count == 0 && late.Count == 0 && dropped.Count == 0) return 0;
            var events = late.Concat(run).Where(r => r.Topic == Outbox.FactTopic).Select(Of).OfType<PaymentEvent>().ToList();
            if (events.Count > 0) await bound.Stream.Publish(events, ct);
            JsonObject? gaps = still.Count == 0 && dropped.Count == 0 ? null : new JsonObject { ["open"] = Json(still) };
            if (gaps is not null && dropped.Count > 0) gaps["dropped"] = Json(dropped);
            db.StreamPositions.Add(new StreamPositionRow
            {
                Stream = dials.Stream, Through = run.Count > 0 ? run[^1].Id : through, Events = events.Count,
                GapsJson = gaps is null ? null : gaps.ToJsonString(), At = StoreTime.Of(now),
            });
            await db.SaveChangesAsync(ct);
            return run.Count + late.Count;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Publishes in the database's commit order (R-09-214, Issue I-281), the default. A row is published once it is below the oldest
    /// commit version still in flight (SQL Server: <c>MIN_ACTIVE_ROWVERSION()</c>; PostgreSQL: <c>pg_snapshot_xmin</c> of the current
    /// snapshot), in the order of its commit version and number, after the last row the stream published in that order: no row can
    /// later commit below that bound, so no row is ever passed over, however long its save took, and no gap is kept or dropped. The
    /// one exception is the past: a stream that published by number before keeps that number as its floor and still looks for its
    /// open gaps, until each is found or its horizon passes.
    /// </summary>
    public bool CommitOrder { get; init; } = true;


    async Task<int> PollInCommitOrder(StoreDbContext db, (PortBinding Binding, IEventStream Stream) bound, Dials dials, StreamPositionRow? last, DateTimeOffset now, TimeSpan horizon, CancellationToken ct)
    {
        // Where the stream is: its keyset in commit order, and the number below which it published by number (0 for a new stream).
        var floor = last?.Through ?? 0;
        var keyV = last?.ThroughVersion ?? long.MinValue;
        var keyId = last?.ThroughId ?? 0;
        if (last is not null && last.ThroughVersion is null)
        {
            // The first pass in commit order of a stream that published by number: everything above its number is still to come.
            keyV = long.MinValue;
            keyId = 0;
        }
        var open = GapsOf(last?.GapsJson, "open");
        var late = new List<OutboxRow>();
        foreach (var g in open)
            late.AddRange(await db.Outbox.AsNoTracking().Where(x => x.Id >= g.From && x.Id <= g.To && x.CommitVersion < StoreDbContext.OldestInFlight(x.Id)).OrderBy(x => x.Id).ToListAsync(ct));
        late = late.OrderBy(x => x.Id).ToList();
        var found = late.Select(x => x.Id).ToHashSet();
        var still = open.SelectMany(g => Without(g, found)).ToList();
        var dropped = still.Where(g => now - g.At > horizon).ToList();
        still = still.Where(g => now - g.At <= horizon).ToList();
        var v = keyV;
        var i = keyId;
        var rows = await db.Outbox.AsNoTracking()
            .Where(x => x.Id > floor && x.CommitVersion < StoreDbContext.OldestInFlight(x.Id) && (x.CommitVersion > v || (x.CommitVersion == v && x.Id > i)))
            .OrderBy(x => x.CommitVersion).ThenBy(x => x.Id).Take(BatchSize).ToListAsync(ct);
        if (rows.Count == 0 && late.Count == 0 && dropped.Count == 0 && last?.ThroughVersion is not null) return 0;
        var events = late.Concat(rows).Where(r => r.Topic == Outbox.FactTopic).Select(Of).OfType<PaymentEvent>().ToList();
        if (events.Count > 0) await bound.Stream.Publish(events, ct);
        JsonObject? gaps = still.Count == 0 && dropped.Count == 0 ? null : new JsonObject { ["open"] = Json(still) };
        if (gaps is not null && dropped.Count > 0) gaps["dropped"] = Json(dropped);
        db.StreamPositions.Add(new StreamPositionRow
        {
            Stream = dials.Stream, Through = floor, Events = events.Count, GapsJson = gaps is null ? null : gaps.ToJsonString(), At = StoreTime.Of(now),
            ThroughVersion = rows.Count > 0 ? rows[^1].CommitVersion : keyV, ThroughId = rows.Count > 0 ? rows[^1].Id : keyId,
        });
        await db.SaveChangesAsync(ct);
        return rows.Count + late.Count;
    }

    /// <summary>A range without the numbers that were found in it: the pieces still missing.</summary>
    static IEnumerable<Gap> Without(Gap g, HashSet<long> found)
    {
        var from = g.From;
        foreach (var id in found.Where(i => i >= g.From && i <= g.To).Order())
        {
            if (id > from) yield return g with { From = from, To = id - 1 };
            from = id + 1;
        }
        if (from <= g.To) yield return g with { From = from };
    }

    /// <summary>Polls until the outbox is drained, as far as it can be now.</summary>
    public async Task CatchUp(CancellationToken ct = default)
    {
        while (await Poll(ct) > 0) { }
    }

    /// <summary>A fact's announcement as an event: the outbox's reference, never the fact's data.</summary>
    PaymentEvent? Of(OutboxRow r)
    {
        if (JsonNode.Parse(r.PayloadJson) is not JsonObject p || (string?)p["subject"] is not { } subject) return null;
        return new PaymentEvent(r.Id, tenant, subject, (string?)p["subjectKind"] ?? "", (long?)p["seq"] ?? 0, (string?)p["kind"] ?? "", (string?)p["code"] ?? "", r.At);
    }

    public void Dispose()
    {
        current?.Stream.Dispose();
        current = null;
    }
}

/// <summary>
/// A host's circuit breakers' moves in the store (R-09-156): one <c>breaker_change</c> row per move, written in the order they
/// happened, off the call that moved the breaker. The service console reads the latest of each breaker for its health page.
/// </summary>
public sealed class StoreBreakerJournal(StoreOptions store, string host) : IBreakerJournal
{
    readonly object gate = new();
    Task last = Task.CompletedTask;

    /// <summary>Settles once every move recorded so far is written.</summary>
    public Task Written { get { lock (gate) return last; } }

    public void Record(BreakerChange c)
    {
        lock (gate) last = last.ContinueWith(_ => Write(c), TaskScheduler.Default).Unwrap();
    }

    async Task Write(BreakerChange c)
    {
        try
        {
            await using var db = StoreDbContext.Open(store);
            db.BreakerChanges.Add(new BreakerChangeRow
            {
                Tenant = c.Key.Tenant, Port = c.Key.Port, Adapter = c.Key.Adapter, Target = c.Key.Target, State = Breakers.Name(c.State),
                Failures = c.Failures, Why = c.Why, Host = host, At = StoreTime.Of(c.At),
            });
            await db.SaveChangesAsync();
        }
        catch { /* the breaker works without its journal */ }
    }
}

/// <summary>The breakers the store's journal knows, each as its latest move says.</summary>
public static class Breakers
{
    public static string Name(BreakerState s) => s switch { BreakerState.Open => "open", BreakerState.HalfOpen => "half-open", _ => "closed" };

    public sealed record Seen(string Tenant, string Port, string Adapter, string Target, string State, int Failures, string? Why, string Host, DateTimeOffset At);

    public static async Task<IReadOnlyList<Seen>> Latest(StoreOptions store, string? tenant = null, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var q = db.BreakerChanges.AsNoTracking();
        if (tenant is not null) q = q.Where(x => x.Tenant == tenant);
        var latest = q.GroupBy(x => new { x.Tenant, x.Port, x.Adapter, x.Target }).Select(g => g.Max(x => x.Id));
        var rows = await db.BreakerChanges.AsNoTracking().Where(x => latest.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(ct);
        return rows.Select(r => new Seen(r.Tenant, r.Port, r.Adapter, r.Target, r.State, r.Failures, r.Why, r.Host, r.At)).ToList();
    }
}
