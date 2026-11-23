using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The operations console's own record (spec R-09-77, R-05-65): its chain of facts in the store, beside the payments' chains.
/// A change of the system the console offers (rebuilding the read model) is asked for by one person and carried out only when
/// a bank approver, another person, consents: both are facts with who, when and why (owner's decision of 23 November 2026).
/// An operator's decision on a payment (approve or decline a hold, retry, repair, cancel) is a business decision on that payment,
/// not a change of the system, and stays with the operators and supervisors (R-05-50). Every export is a fact too: who, when, the
/// filter and the number of rows (Issue I-144).
/// </summary>
public sealed class ConsoleRecord(StoreOptions store, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public const string Subject = "operations-console";
    public const string SubjectKind = "console";
    public const string ConsentKind = "consent";
    public const string ExportKind = "export";

    /// <summary>The changes of the system the console offers, each waiting for a bank approver's consent.</summary>
    public static readonly string[] SystemActions = ["rebuild-read-model"];

    async Task<FactRow> Append(string kind, string code, JsonObject data, string actor, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        return await new FactLog(db, clock).Append(Subject, SubjectKind, new NewFact(kind, code, data, Actor: actor), ct);
    }

    /// <summary>An export: who, when (the fact's time), what filter, how many rows, and whether the list was cut.</summary>
    public Task<FactRow> Exported(OperatorIdentity who, string format, IEnumerable<string> tenants, JsonObject filter, int rows, bool cut, CancellationToken ct = default) =>
        Append(ExportKind, format, new JsonObject
        {
            ["by"] = who.DisplayName, ["tenants"] = new JsonArray(tenants.Order(StringComparer.Ordinal).Select(t => (JsonNode?)t).ToArray()),
            ["filter"] = filter, ["rows"] = rows, ["cut"] = cut,
        }, who.Actor, ct);

    /// <summary>A person asks for a change of the system; it waits for a bank approver's consent.</summary>
    public async Task<JsonObject> Request(OperatorIdentity who, string action, string? reason, CancellationToken ct = default)
    {
        if (!SystemActions.Contains(action)) throw new ActionRefused(404, $"The console offers no system action {action}", OpsCodes.ActionNotOffered);
        if (string.IsNullOrWhiteSpace(reason)) throw new ActionRefused(400, $"{action} needs a reason: who asked and why", OpsCodes.RemarksRequired);
        var id = Guid.NewGuid().ToString("N");
        await Append(ConsentKind, "requested", new JsonObject { ["action"] = id, ["name"] = action, ["why"] = reason, ["by"] = who.DisplayName }, who.Actor, ct);
        return new JsonObject { ["action"] = id, ["name"] = action, ["status"] = "pending-approval", ["needs"] = OperatorRoles.BankApprover };
    }

    /// <summary>The system actions that wait for consent, oldest first.</summary>
    public async Task<JsonArray> Pending(CancellationToken ct = default)
    {
        var facts = await Facts(null, ct);
        return new JsonArray(facts.GroupBy(f => (string?)JsonNode.Parse(f.DataJson)?["action"]).Where(g => g.Count() == 1 && g.First().Code == "requested")
            .Select(g => (JsonNode)Show(g.First())).ToArray());
    }

    /// <summary>
    /// A bank approver consents to (or refuses) a system action another person asked for. On consent <paramref name="run"/> carries
    /// it out; its result is a fact too.
    /// </summary>
    public async Task<JsonObject> Decide(OperatorIdentity who, string actionId, bool approve, string? reason, Func<string, CancellationToken, Task<JsonObject>> run, CancellationToken ct = default)
    {
        if (!who.MayConsent) throw new ActionRefused(403, $"Consenting needs the {OperatorRoles.BankApprover} role", OpsCodes.Forbidden);
        if (string.IsNullOrWhiteSpace(reason)) throw new ActionRefused(400, "A decision needs a reason", OpsCodes.RemarksRequired);
        var facts = await Facts(actionId, ct);
        var req = facts.FirstOrDefault(f => f.Code == "requested") ?? throw new ActionRefused(404, $"No system action {actionId}", OpsCodes.ActionNotOffered);
        if (facts.Count > 1) throw new ActionRefused(409, $"System action {actionId} is already {facts[^1].Code}", OpsCodes.AlreadyDecided);
        if (req.Actor == who.Actor) throw new ActionRefused(403, "The person who asked for a change of the system may not consent to it", OpsCodes.MakerApproves);
        var name = (string)JsonNode.Parse(req.DataJson)!["name"]!;
        await Append(ConsentKind, approve ? "approved" : "rejected", new JsonObject { ["action"] = actionId, ["name"] = name, ["why"] = reason, ["by"] = who.DisplayName, ["requestedBy"] = req.Actor }, who.Actor, ct);
        if (!approve) return new JsonObject { ["action"] = actionId, ["name"] = name, ["status"] = "rejected" };
        var result = await run(name, ct);
        await Append(ConsentKind, "done", new JsonObject { ["action"] = actionId, ["name"] = name, ["result"] = result.DeepClone() }, who.Actor, ct);
        return new JsonObject { ["action"] = actionId, ["name"] = name, ["status"] = "done", ["result"] = result };
    }

    /// <summary>The console's facts of a kind, newest first, for the audit.</summary>
    public async Task<List<JsonObject>> Recent(string kind, int take = 200, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var rows = await db.Facts.AsNoTracking().Where(f => f.SubjectId == Subject && f.Kind == kind).OrderByDescending(f => f.Seq).Take(take).ToListAsync(ct);
        return rows.Select(Show).ToList();
    }

    async Task<List<FactRow>> Facts(string? actionId, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var rows = await db.Facts.AsNoTracking().Where(f => f.SubjectId == Subject && f.Kind == ConsentKind).OrderBy(f => f.Seq).ToListAsync(ct);
        return actionId is null ? rows : rows.Where(f => (string?)JsonNode.Parse(f.DataJson)?["action"] == actionId).ToList();
    }

    static JsonObject Show(FactRow f) => new()
    {
        ["seq"] = f.Seq, ["kind"] = f.Kind, ["code"] = f.Code, ["at"] = f.At.ToString("O"), ["actor"] = f.Actor, ["data"] = JsonNode.Parse(f.DataJson),
    };
}
