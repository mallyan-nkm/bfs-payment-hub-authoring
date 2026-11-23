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
    // The same chain the release API asks a bank approver's consent on (ConsentRequests, R-09-111).
    public const string Subject = TransactHub.Api.ConsentRequests.Subject;
    public const string Kind = TransactHub.Api.ConsentRequests.Kind;

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

    /// <summary>The same, filtered by person, action and date for the audit page (R-05-76), newest first.</summary>
    public async Task<List<JsonObject>> Recent(AuditFilter filter, int take = 500, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var q = db.Facts.AsNoTracking().Where(f => f.Kind == Kind || (f.SubjectKind == "release" && f.Kind == "release.verified"));
        if (filter.From is { } from) { var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); q = q.Where(f => f.At >= since); }
        var rows = await q.OrderByDescending(f => f.Id).Take(5000).ToListAsync(ct);
        return rows.Where(filter.Matches).Take(take).Select(Show).ToList();
    }

    /// <summary>
    /// Whether the chains the audit reads verify (R-05-76): the console's own and each release's that holds a service fact,
    /// each numbered without gaps, each hash right and each link right.
    /// </summary>
    public async Task<JsonObject> Verify(CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var subjects = await db.Facts.AsNoTracking().Where(f => f.Kind == Kind || (f.SubjectKind == "release" && f.Kind == "release.verified"))
            .Select(f => f.SubjectId).Distinct().ToListAsync(ct);
        var log = new FactLog(db, clock);
        var chains = new JsonArray();
        var ok = true;
        foreach (var s in subjects.Order(StringComparer.Ordinal))
        {
            var c = await log.Verify(s, ct);
            ok &= c.Ok;
            chains.Add(new JsonObject { ["subject"] = s, ["ok"] = c.Ok, ["facts"] = c.Facts, ["problem"] = c.Problem });
        }
        return new JsonObject { ["ok"] = ok, ["chains"] = chains };
    }

    public static JsonObject Show(FactRow f) => new()
    {
        ["id"] = f.Id, ["subject"] = f.SubjectId, ["subjectKind"] = f.SubjectKind, ["seq"] = f.Seq, ["kind"] = f.Kind, ["code"] = f.Code,
        ["at"] = f.At.ToString("O"), ["actor"] = f.Actor, ["data"] = JsonNode.Parse(f.DataJson), ["hash"] = f.Hash, ["prevHash"] = f.PrevHash,
    };
}
