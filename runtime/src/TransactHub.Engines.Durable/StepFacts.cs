using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Engines;
using TransactHub.Store;

namespace TransactHub.Engines.Durable;

/// <summary>
/// Each step a worker answered, as a fact on the payment's chain (kind <c>step</c>, code the outcome or
/// <c>error</c>): the operations read model's timeline (R-05-50). It is told by every engine's worker router
/// (<see cref="WorkerRouter.Answered"/>), so the four engines record the same thing. It keeps only what an
/// operator needs and the workflow state already holds without personal data: the step, the activity, the
/// attempt, the outcome and reason code, the error's name, the header's ids and the facts the step wrote.
/// Never the payload, never an error's text (it can quote a value).
/// The facts of one payment go in order on its own lane, several in one save when the items of a large file answer side by
/// side, and no payment waits for another's (I-78, <see cref="FactWriter"/>).
/// </summary>
public sealed class StepFacts(StoreOptions options, TimeProvider? clock = null)
{
    readonly ConcurrentDictionary<string, (string id, string kind)?> subjects = new();
    readonly FactWriter writer = new(options, clock);
    // originalPaymentId: the earlier payment a refund order names by id (R-02-20), an id, never personal data.
    static readonly string[] HeaderKeys = ["scheme", "direction", "kind", "segment", "fileId", "endToEndId", "originalPaymentId", "leg", "slot"];

    /// <summary>The recorder installed in this process, if any.</summary>
    public static StepFacts? Current { get; private set; }

    /// <summary>
    /// Set by a host that restarted (Issue I-89): an attempt its predecessor had answered just before it stopped may be answered
    /// again, and a step the payment's chain already records for that instance, step, call, attempt and item is not recorded twice.
    /// </summary>
    volatile bool skipRecorded;
    readonly ConcurrentDictionary<string, HashSet<string>> recorded = new();

    public void SkipRecorded() => skipRecorded = true;

    static string KeyOf(JsonObject d, string code) =>
        $"{J.S(d, "instance")}|{J.S(d, "step")}|{J.Num(d, "call")}|{J.Num(d, "attempt")}|{J.S(d, "level")}|{J.Num(d, "item")}|{code}";

    async Task<bool> AlreadyRecorded(string subject, JsonObject data, string code)
    {
        if (!skipRecorded) return false;
        if (!recorded.TryGetValue(subject, out var keys))
        {
            await using var db = StoreDbContext.Open(options);
            var rows = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subject && f.Kind == "step").Select(f => new { f.Code, f.DataJson }).ToListAsync();
            keys = rows.Select(r => KeyOf(JsonNode.Parse(r.DataJson) as JsonObject ?? new JsonObject(), r.Code ?? "")).ToHashSet();
            recorded[subject] = keys;
        }
        lock (keys) return !keys.Add(KeyOf(data, code));
    }

    /// <summary>Installs the recorder on every worker router of this process.</summary>
    public static StepFacts Install(StoreOptions options, TimeProvider? clock = null)
    {
        var s = new StepFacts(options, clock);
        Current = s;
        WorkerRouter.Answered = async (req, res) =>
        {
            try { await s.Record(req, res); }
            catch (Exception e) { Console.Error.WriteLine($"[store] a step fact was not recorded: {e.GetType().Name}"); }
        };
        return s;
    }

    /// <summary>The payment or bulk an engine instance's payload was accepted as: its own, else its root's.</summary>
    (string id, string kind)? SubjectOf(StoreDbContext db, string instance)
    {
        var candidates = new List<string> { instance };
        for (var i = instance.IndexOf('/'); i > 0; i = instance.IndexOf('/', i + 1)) candidates.Add(instance[..i]);
        foreach (var c in candidates.Distinct())
        {
            var hit = subjects.GetOrAdd(c, key =>
            {
                var r = $"{key}#ctx";
                var f = db.Facts.AsNoTracking().Where(x => x.SubjectId == r && x.Code == "accepted").OrderByDescending(x => x.Seq).FirstOrDefault();
                if (f is null) return null;
                var d = JsonNode.Parse(f.DataJson) as JsonObject;
                return J.S(d, "bulk") is string b ? (b, "bulk") : J.S(d, "payment") is string p ? (p, "payment") : null;
            });
            if (hit is not null)
            {
                // The instance's own answer from now on: a leg's attempts do not ask the database again for each one (I-78).
                if (c != instance) subjects[instance] = hit;
                return hit;
            }
            subjects.TryRemove(c, out _); // not accepted yet: ask again next time
        }
        return null;
    }

    public async Task Record(JsonObject req, JsonObject res)
    {
        var instance = J.S(req, "instance") ?? "";
        {
            (string id, string kind)? found;
            if (subjects.TryGetValue(instance, out var known) && known is not null) found = known;
            else using (var db = StoreDbContext.Open(options)) found = SubjectOf(db, instance);
            if (found is not { } subject) return;
            var ctx = J.P(req, "ctx") as JsonObject;
            var header = new JsonObject();
            foreach (var k in HeaderKeys) if (J.S(J.P(ctx, "header"), k) is string v) header[k] = v;
            var kind = J.S(res, "kind") ?? "result";
            var data = new JsonObject
            {
                // The workflow the step ran in: the payment's platform workflow at its root (R-05-61, an action's when.workflow).
                ["instance"] = instance, ["workflow"] = J.S(req, "workflow"), ["step"] = J.S(req, "step"), ["activity"] = J.S(req, "activity"), ["release"] = J.S(req, "release"),
                ["call"] = J.Num(req, "call"), ["attempt"] = J.Num(req, "attempt"), ["level"] = J.S(req, "level"), ["header"] = header,
            };
            // The item of a forEach an attempt belongs to, where the engine names it (the Flowable binding does).
            if (J.Num(req, "item") is double item) data["item"] = (int)item;
            if (J.P(ctx, "status") is JsonNode status) data["status"] = J.Clone(status);
            if (kind == "result")
            {
                data["outcome"] = J.S(res, "outcome");
                if (J.S(res, "reasonCode") is string rc) data["reasonCode"] = rc;
                // The step's writes to the workflow's state (facts, status, message references: never personal data,
                // R-09-10); an item's own facts are in its payload.
                if (J.S(req, "level") != "item")
                {
                    var writes = new JsonObject();
                    foreach (var (p, v) in J.Obj(J.P(res, "writes")).ToList())
                        if (ClaimCheckStore.IsStatePath(p) && !p.StartsWith("header", StringComparison.Ordinal)) writes[p] = J.Clone(v);
                    if (writes.Count > 0) data["writes"] = writes;
                }
            }
            else if (kind == "error") data["error"] = new JsonObject { ["name"] = J.S(res, "name"), ["retryable"] = J.P(res, "retryable")?.DeepClone() };
            var code = kind == "result" ? J.S(res, "outcome") ?? "result" : kind;
            if (await AlreadyRecorded(subject.id, data, code)) return;
            await writer.Append(subject.id, subject.kind, new NewFact("step", code, data, Actor: "engine"));
        }
    }
}
