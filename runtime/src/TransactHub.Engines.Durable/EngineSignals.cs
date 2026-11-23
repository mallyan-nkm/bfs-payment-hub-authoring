using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Engines;
using TransactHub.Store;

namespace TransactHub.Engines.Durable;

/// <summary>
/// The engine host's reader of the outbox topic <c>engine-signal</c> (R-05-57, Issue I-99), on the store on either database.
/// <see cref="Follow"/> finds the payment or bulk a root instance's payload was accepted as and records the instance's engine ids
/// on its chain (kind <c>engine</c>, code <c>started</c>: the engine, the instance id, the run id). <see cref="Feed.Poll"/> gives
/// the rows for that payment that no fact records as delivered, in outbox order; <see cref="Feed.Ack"/> records each (kind
/// <c>signal</c>, code <c>delivered</c> or <c>refused</c>, with the row's id). A second delivery of a row records nothing, and a
/// host that restarts reads from the facts which rows are done: idempotent, ordered, resumable.
/// </summary>
public sealed class StoreSignals(StoreOptions store, TimeProvider? clock = null) : ILiveSignals
{
    public const string Topic = "engine-signal";
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Installs the reader for every harness of this process (TH_STORE_PROVIDER), and records each step that parks as the payment's
    /// step fact <c>stuck</c> (once per instance, step and call: an engine that replays its history parks again on replay).
    /// </summary>
    public static StoreSignals Install(StoreOptions store, StepFacts? steps = null)
    {
        var s = new StoreSignals(store);
        LiveSignals.Source = s;
        var facts = steps ?? new StepFacts(store);
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        Interp.Stuck = (instance, step, call) =>
        {
            if (!seen.TryAdd($"{instance}|{step}|{call}", true)) return;
            facts.Record(new JsonObject { ["instance"] = instance, ["step"] = step, ["call"] = call }, new JsonObject { ["kind"] = "stuck" }).GetAwaiter().GetResult();
        };
        // A hold's decision is the hold step's fact, as a worker's answer is any other step's (R-05-60): its outcome, who and when.
        // Once per instance, step and call: an engine that replays its history takes the decision again on replay.
        Interp.Decided = (req, res) =>
        {
            if (!seen.TryAdd($"decided|{J.S(req, "instance")}|{J.S(req, "step")}|{J.Num(req, "call")}", true)) return;
            facts.Record(req, res).GetAwaiter().GetResult();
        };
        return s;
    }

    public async Task<ILiveFeed?> Follow(string payloadRef, string engine, string instance, string? run, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var f = await db.Facts.AsNoTracking().Where(x => x.SubjectId == payloadRef && x.Code == "accepted").OrderByDescending(x => x.Seq).FirstOrDefaultAsync(ct);
        if (f is null) return null;
        var d = JsonNode.Parse(f.DataJson) as JsonObject;
        var (subject, kind) = J.S(d, "bulk") is string b ? (b, "bulk") : J.S(d, "payment") is string p ? (p, "payment") : (null, null);
        if (subject is null) return null;
        await Started(subject, kind!, engine, instance, run, ct);
        return new Feed(store, clock, subject, kind!, engine, instance);
    }

    /// <summary>An instance's engine ids, as a fact on the payment's chain when it starts (Issues I-99, I-101).</summary>
    public async Task Started(string subject, string kind, string engine, string instance, string? run, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        await new FactLog(db, clock).Append(subject, kind, new NewFact("engine", "started",
            new JsonObject { ["engine"] = engine, ["instance"] = instance, ["run"] = run }, Actor: $"engine:{engine}"), ct);
    }

    /// <summary>The rows of one payment, as its host follows them.</summary>
    public sealed class Feed(StoreOptions store, TimeProvider clock, string subject, string kind, string engine, string instance) : ILiveFeed
    {
        public string Subject => subject;
        public string SubjectKind => kind;
        public string Instance => instance;

        public async Task<List<JsonObject>> Poll(CancellationToken ct = default)
        {
            await using var db = StoreDbContext.Open(store);
            var done = await Done(db, ct);
            var rows = await db.Outbox.AsNoTracking().Where(o => o.Topic == Topic && o.Key == subject).OrderBy(o => o.Id).ToListAsync(ct);
            var list = new List<JsonObject>();
            foreach (var r in rows.Where(r => !done.Contains(r.Id)))
            {
                var p = JsonNode.Parse(r.PayloadJson) as JsonObject ?? new JsonObject();
                p["id"] = r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                list.Add(p);
            }
            return list;
        }

        async Task<HashSet<long>> Done(StoreDbContext db, CancellationToken ct)
        {
            var acks = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subject && f.Kind == "signal").Select(f => f.DataJson).ToListAsync(ct);
            return acks.Select(a => (long?)(JsonNode.Parse(a)?["row"]?.GetValue<long>())).Where(x => x is not null).Select(x => x!.Value).ToHashSet();
        }

        public async Task Ack(JsonObject row, string result, CancellationToken ct = default)
        {
            var id = long.Parse(J.S(row, "id")!, System.Globalization.CultureInfo.InvariantCulture);
            await using var db = StoreDbContext.Open(store);
            if ((await Done(db, ct)).Contains(id)) return;
            await new FactLog(db, clock).Append(subject, kind, new NewFact("signal", result == "delivered" ? "delivered" : "refused",
                new JsonObject { ["row"] = id, ["signal"] = J.S(row, "signal"), ["action"] = J.S(row, "action"), ["engine"] = engine, ["instance"] = instance, ["result"] = result },
                Actor: $"engine:{engine}"), ct);
        }
    }
}
