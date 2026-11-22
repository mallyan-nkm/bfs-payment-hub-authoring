using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Activities.Native;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The platform's holds answered by the runtime (R-08-15, R-05-50): when a bench or a deployment gives
/// <c>p.port.operations-queue.v1</c> to the runtime, <c>p.hold.approval.v1</c> and <c>p.hold.repair.v1</c> run here. The hold
/// is opened as a fact on the payment's chain (kind <c>hold</c>, code <c>opened</c>) with an exception item, and the
/// attempt waits for the operator's decision, which the console records on the same chain (<c>decided</c>). The port's
/// <c>decision</c> operation is that fact. A hold that is retried finds its own open hold and goes on waiting for it.
/// The wait is bounded (<see cref="MaxWait"/>): this is the bench's path, the attempt holds a connection while it waits
/// (Issue I-98 records the durable path, an engine await on <c>p.ops.decision</c>).
/// </summary>
public sealed class StoreHold(StoreOptions store, string activity, string kind, string factPath, string[] decisions, TimeProvider? clock = null) : INativeActivity
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public string Name => activity;
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(4);
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromMilliseconds(300);

    public static INativeActivity[] For(StoreOptions store, TimeSpan? maxWait = null) =>
    [
        new StoreHold(store, "p.hold.approval.v1", "approval", "facts.approval", ["approved", "declined"]) { MaxWait = maxWait ?? TimeSpan.FromMinutes(4) },
        new StoreHold(store, "p.hold.repair.v1", "repair", "facts.repair", ["released", "rejected"]) { MaxWait = maxWait ?? TimeSpan.FromMinutes(4) },
    ];

    public NativeResult Run(NativeAttempt a) => RunAsync(a).GetAwaiter().GetResult();

    async Task<NativeResult> RunAsync(NativeAttempt a)
    {
        var tenant = a.Tenant ?? throw new NativeFailure("TenantMissing", $"{activity} needs the tenant it runs for", false);
        var header = a.View["header"] as JsonObject ?? new JsonObject();
        var (subjectId, subjectKind) = await SubjectOf(tenant, header) ?? throw new NativeFailure("NotInStore", $"{activity}: the payment is not in the store", false);
        string holdId;
        await using (var db = StoreDbContext.Open(store))
        {
            var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "hold").OrderBy(f => f.Seq).ToListAsync();
            var closed = facts.Where(f => f.Code != "opened").Select(f => (string?)JsonNode.Parse(f.DataJson)?["hold"]).ToHashSet();
            var open = facts.LastOrDefault(f => f.Code == "opened" && (string?)JsonNode.Parse(f.DataJson)?["activity"] == activity && !closed.Contains((string?)JsonNode.Parse(f.DataJson)?["hold"]));
            if (open is not null) holdId = (string)JsonNode.Parse(open.DataJson)!["hold"]!;
            else
            {
                holdId = Guid.NewGuid().ToString("N");
                var reason = await ReasonOf(db, subjectId);
                db.ExceptionItems.Add(new ExceptionItemRow
                {
                    Id = holdId, Tenant = tenant, SubjectId = subjectId, Code = $"hold.{kind}", Text = reason,
                    DataJson = CanonicalJson.Of(new JsonObject { ["activity"] = activity, ["expiry"] = (string?)a.Params["expiry"] }), RaisedAt = StoreTime.Of(clock.GetUtcNow()),
                });
                await db.SaveChangesAsync();
                await new FactLog(db, clock).Append(subjectId, subjectKind, new NewFact("hold", "opened",
                    new JsonObject { ["hold"] = holdId, ["kind"] = kind, ["activity"] = activity, ["reason"] = reason, ["expiry"] = (string?)a.Params["expiry"] }, Actor: "runtime"));
            }
        }
        var until = clock.GetUtcNow() + MaxWait;
        while (true)
        {
            await using (var db = StoreDbContext.Open(store))
            {
                foreach (var f in await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "hold" && f.Code == "decided").OrderBy(f => f.Seq).ToListAsync())
                {
                    var d = JsonNode.Parse(f.DataJson) as JsonObject;
                    if ((string?)d?["hold"] != holdId) continue;
                    var decision = (string?)d["decision"] ?? "";
                    if (!decisions.Contains(decision)) throw new NativeFailure("BadDecision", $"{activity} takes {string.Join(" or ", decisions)}, not {decision}", false);
                    return new NativeResult(decision, null, new JsonObject
                    {
                        [factPath] = new JsonObject { ["status"] = decision, ["hold"] = holdId, ["by"] = (string?)d["by"], ["at"] = f.At.ToString("O"), ["action"] = (string?)d["action"] },
                    });
                }
                if (clock.GetUtcNow() >= until)
                {
                    await new FactLog(db, clock).Append(subjectId, subjectKind, new NewFact("hold", "expired", new JsonObject { ["hold"] = holdId, ["activity"] = activity }, Actor: "runtime"));
                    return new NativeResult("expired", null, new JsonObject { [factPath] = new JsonObject { ["status"] = "expired", ["hold"] = holdId } });
                }
            }
            await Task.Delay(PollEvery);
        }
    }

    /// <summary>The payment or bulk the hold is for: by the header's file id (a bulk's message id) or end-to-end id, the newest.</summary>
    async Task<(string, string)?> SubjectOf(string tenant, JsonObject header)
    {
        await using var db = StoreDbContext.Open(store);
        var scheme = (string?)header["scheme"] ?? "";
        var fileId = Short((string?)header["fileId"] ?? (string?)header["endToEndId"]);
        if (fileId is not null)
        {
            var b = await db.Bulks.AsNoTracking().Where(x => x.Tenant == tenant && x.Scheme == scheme && x.MessageId == fileId).OrderByDescending(x => x.AcceptedAt).Select(x => x.Id).FirstOrDefaultAsync();
            if (b is not null) return (b, "bulk");
        }
        var e2e = Short((string?)header["endToEndId"]);
        if (e2e is null) return null;
        var p = await db.Payments.AsNoTracking().Where(x => x.Tenant == tenant && x.Scheme == scheme && x.EndToEndId == e2e && x.BulkId == null).OrderByDescending(x => x.AcceptedAt).Select(x => x.Id).FirstOrDefaultAsync();
        return p is null ? null : (p, "payment");
    }

    static string? Short(string? s) => s is null ? null : s.Length <= 35 ? s : s[..35];

    /// <summary>Why the payment is held: the last step before the hold that did not take its primary outcome, with its reason code.</summary>
    static async Task<string?> ReasonOf(StoreDbContext db, string subjectId)
    {
        var steps = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "step").OrderByDescending(f => f.Seq).Take(20).ToListAsync();
        foreach (var s in steps)
        {
            var d = JsonNode.Parse(s.DataJson) as JsonObject;
            var rc = (string?)d?["reasonCode"];
            if (rc is not null || s.Code is "reject" or "repair" or "hold" or "hit" or "rejected" or "unknown" or "unverified")
                return $"{rc ?? s.Code} at {(string?)d?["step"]} ({(string?)d?["activity"]})";
        }
        return null;
    }
}
