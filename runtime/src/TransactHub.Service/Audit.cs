using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Service;

/// <summary>
/// Every service-console action as a fact (spec R-09-40, R-09-43): who (the verified identity), when (the store's clock) and
/// why (the reason the person gave). A content action is on the release's own chain, beside its verifications; every other
/// action is on the console's chain (<see cref="Subject"/>); an action on a payment is also on the payment's chain.
/// </summary>
public sealed class ServiceAudit(StoreOptions store, TimeProvider clock)
{
    public const string Subject = "service-console";
    public const string Kind = "service";

    public async Task<FactRow> Record(string code, ServiceIdentity who, string? why, JsonObject? data = null, string? release = null, string? payment = null, string? paymentKind = null, CancellationToken ct = default)
    {
        var d = (JsonObject?)data?.DeepClone() ?? new JsonObject();
        d["why"] = why;
        d["by"] = who.DisplayName;
        await using var db = StoreDbContext.Open(store);
        var log = new FactLog(db, clock);
        var fact = await log.Append(release ?? Subject, release is null ? Kind : "release", new NewFact(Kind, code, d, release, who.Actor), ct);
        if (payment is not null) await log.Append(payment, paymentKind ?? "payment", new NewFact(Kind, code, (JsonObject)d.DeepClone(), Actor: who.Actor), ct);
        return fact;
    }

    /// <summary>The console's facts and the releases' service facts, newest first.</summary>
    public async Task<List<JsonObject>> Recent(int take = 200, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var rows = await db.Facts.AsNoTracking().Where(f => f.Kind == Kind || (f.SubjectKind == "release" && f.Kind == "release.verified"))
            .OrderByDescending(f => f.Id).Take(take).ToListAsync(ct);
        return rows.Select(Show).ToList();
    }

    public static JsonObject Show(FactRow f) => new()
    {
        ["id"] = f.Id, ["subject"] = f.SubjectId, ["subjectKind"] = f.SubjectKind, ["seq"] = f.Seq, ["kind"] = f.Kind, ["code"] = f.Code,
        ["at"] = f.At.ToString("O"), ["actor"] = f.Actor, ["data"] = JsonNode.Parse(f.DataJson),
    };
}
