using System.Text.Json.Nodes;
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
/// recorded is sent again and nothing is skipped. The relay publishes only an unbroken run of outbox numbers: a missing number (a
/// save still in flight) stops the batch until it arrives, or until the row after it is older than <see cref="GapTimeout"/>, when it
/// is taken for a save that was rolled back, as the read model's projection takes it (R-05-49). With no binding in force the relay
/// waits, keeping its position.
/// </para>
/// </summary>
public sealed class EventStreamRelay(StoreOptions store, string tenant, PortBindings bindings, ISecretResolver? secrets = null, TimeProvider? clock = null) : IDisposable
{
    /// <summary>The stream's name in <c>stream_position</c>: one position per bank's port, whatever its binding.</summary>
    public const string Stream = EventStreamAdapters.Port;
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    readonly SemaphoreSlim gate = new(1, 1);
    (PortBinding Binding, IEventStream Stream)? current;
    public TimeSpan GapTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int BatchSize { get; init; } = 500;

    /// <summary>The last outbox row published, with every row before it (0 before the first).</summary>
    public async Task<long> Position(CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        return await db.StreamPositions.AsNoTracking().Where(s => s.Stream == Stream).MaxAsync(s => (long?)s.Through, ct) ?? 0;
    }

    IEventStream? Bound()
    {
        PortBinding b;
        try { b = bindings.InForce(tenant, EventStreamAdapters.Port, clock.GetUtcNow()); }
        catch (PortBindingException) { return null; }
        if (current is { } c && ReferenceEquals(c.Binding, b)) return c.Stream;
        current?.Stream.Dispose();
        current = (b, EventStreamAdapters.Create(b, secrets));
        return current.Value.Stream;
    }

    /// <summary>One pass: publishes what is new, then records the position. Returns how many outbox rows it passed.</summary>
    public async Task<int> Poll(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var stream = Bound();
            if (stream is null) return 0;
            await using var db = StoreDbContext.Open(store);
            var through = await db.StreamPositions.AsNoTracking().Where(s => s.Stream == Stream).MaxAsync(s => (long?)s.Through, ct) ?? 0;
            var rows = await db.Outbox.AsNoTracking().Where(x => x.Id > through).OrderBy(x => x.Id).Take(BatchSize).ToListAsync(ct);
            var now = clock.GetUtcNow();
            var run = new List<OutboxRow>();
            var expected = through + 1;
            foreach (var r in rows)
            {
                if (r.Id != expected && now - r.At <= GapTimeout) break;
                run.Add(r);
                expected = r.Id + 1;
            }
            if (run.Count == 0) return 0;
            var events = run.Where(r => r.Topic == Outbox.FactTopic).Select(Of).OfType<PaymentEvent>().ToList();
            if (events.Count > 0) await stream.Publish(events, ct);
            db.StreamPositions.Add(new StreamPositionRow { Stream = Stream, Through = run[^1].Id, Events = events.Count, At = StoreTime.Of(now) });
            await db.SaveChangesAsync(ct);
            return run.Count;
        }
        finally { gate.Release(); }
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
