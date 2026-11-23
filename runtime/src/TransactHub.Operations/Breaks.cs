using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The reconciliation break queue of the operations console (R-09-92): every break a scheme report's run recorded
/// (<see cref="SchemeReconciliation"/>), open until it is resolved. A resolution is a fact on the run's chain with who, when and why.
/// <c>explain</c> records the operator's explanation and changes nothing. <c>adopt-status</c> records the scheme's status on the
/// payment's chain, a change of the system: it is asked for by one person and carried out only when a bank approver, another
/// person, consents (R-09-77, owner's decision of 23 November 2026). A break is resolved once.
/// </summary>
public sealed class BreakQueue(StoreOptions store, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public const string Kind = "reconciliation";
    /// <summary>The resolutions there are, and whether each changes the system (and so needs a bank approver's consent).</summary>
    public static readonly Dictionary<string, bool> Resolutions = new() { ["explain"] = false, ["adopt-status"] = true };

    sealed record Break(string Run, string Id, JsonObject Data, string State, JsonObject? Pending, List<JsonObject> History);

    async Task<List<Break>> All(CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectKind == SchemeReconciliation.SubjectKind && f.Kind == Kind).OrderBy(f => f.Seq).ToListAsync(ct);
        var outp = new Dictionary<string, Break>(StringComparer.Ordinal);
        foreach (var f in facts)
        {
            var d = JsonNode.Parse(f.DataJson) as JsonObject ?? new JsonObject();
            var id = (string?)d["break"];
            if (id is null) continue;
            var h = new JsonObject { ["code"] = f.Code, ["at"] = f.At.ToString("O"), ["actor"] = f.Actor, ["data"] = d.DeepClone() };
            if (f.Code == "break") { outp[id] = new Break(f.SubjectId, id, d, "open", null, [h]); continue; }
            if (!outp.TryGetValue(id, out var b)) continue;
            b.History.Add(h);
            outp[id] = f.Code switch
            {
                "resolution-requested" => b with { State = "pending-consent", Pending = d },
                "resolution-refused" => b with { State = "open", Pending = null },
                "resolved" => b with { State = "resolved", Pending = null },
                _ => b,
            };
        }
        return outp.Values.ToList();
    }

    static JsonObject Show(Break b) => new()
    {
        ["break"] = b.Id, ["run"] = b.Run, ["state"] = b.State, ["kind"] = b.Data["kind"]?.DeepClone(), ["reference"] = b.Data["reference"]?.DeepClone(),
        ["payment"] = b.Data["payment"]?.DeepClone(), ["expected"] = b.Data["expected"]?.DeepClone(), ["reported"] = b.Data["reported"]?.DeepClone(),
        ["reportedStatus"] = b.Data["reportedStatus"]?.DeepClone(), ["tenant"] = b.Data["tenant"]?.DeepClone(), ["scheme"] = b.Data["scheme"]?.DeepClone(),
        ["file"] = b.Data["file"]?.DeepClone(), ["pending"] = b.Pending?.DeepClone(), ["history"] = new JsonArray(b.History.Select(x => (JsonNode)x.DeepClone()).ToArray()),
    };

    /// <summary>The breaks of the tenants the person may see; open and pending first.</summary>
    public async Task<JsonArray> List(OperatorIdentity who, bool all = false, CancellationToken ct = default)
    {
        var rows = (await All(ct)).Where(b => Sees(who, (string?)b.Data["tenant"] ?? "")).Where(b => all || b.State != "resolved")
            .OrderBy(b => b.State == "resolved").ThenBy(b => b.Run, StringComparer.Ordinal).ThenBy(b => (int?)b.Data["line"]);
        return new JsonArray(rows.Select(b => (JsonNode)Show(b)).ToArray());
    }

    static bool Sees(OperatorIdentity who, string tenant) => OperatorRoles.All.Any(r => who.Has(tenant, r));

    async Task<Break> One(string id, CancellationToken ct) => (await All(ct)).FirstOrDefault(b => b.Id == id) ?? throw new ActionRefused(404, $"No reconciliation break {id}", OpsCodes.ActionNotOffered);

    async Task Append(string run, string code, JsonObject data, string actor, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        await new FactLog(db, clock).Append(run, SchemeReconciliation.SubjectKind, new NewFact(Kind, code, data, Actor: actor), ct);
    }

    /// <summary>An operator resolves a break: at once when the resolution changes nothing, else it waits for a bank approver's consent.</summary>
    public async Task<JsonObject> Resolve(OperatorIdentity who, string id, string resolution, string? reason, CancellationToken ct = default)
    {
        var b = await One(id, ct);
        var tenant = (string?)b.Data["tenant"] ?? "";
        if (!who.Has(tenant, OperatorRoles.Operator)) throw new ActionRefused(403, $"{who.DisplayName} has no operator role for tenant {tenant}", OpsCodes.Forbidden);
        if (!Resolutions.TryGetValue(resolution, out var changes)) throw new ActionRefused(400, $"No resolution {resolution}: {string.Join(", ", Resolutions.Keys)}", OpsCodes.ActionNotOffered);
        if (string.IsNullOrWhiteSpace(reason)) throw new ActionRefused(400, "A resolution needs a reason", OpsCodes.RemarksRequired);
        if (b.State != "open") throw new ActionRefused(409, $"Break {id} is {b.State}", OpsCodes.AlreadyDecided);
        if (resolution == "adopt-status" && ((string?)b.Data["payment"] is null || (string?)b.Data["reportedStatus"] is null))
            throw new ActionRefused(409, $"Break {id} names no payment or no status to adopt", OpsCodes.ActionNotOffered);
        var data = new JsonObject { ["break"] = id, ["resolution"] = resolution, ["why"] = reason, ["by"] = who.DisplayName };
        if (!changes)
        {
            await Append(b.Run, "resolved", data, who.Actor, ct);
            return new JsonObject { ["break"] = id, ["status"] = "resolved", ["resolution"] = resolution };
        }
        await Append(b.Run, "resolution-requested", data, who.Actor, ct);
        return new JsonObject { ["break"] = id, ["status"] = "pending-consent", ["resolution"] = resolution, ["needs"] = OperatorRoles.BankApprover };
    }

    /// <summary>A bank approver, not the person who asked, consents to (or refuses) a resolution that changes the system; on consent it is carried out.</summary>
    public async Task<JsonObject> Consent(OperatorIdentity who, string id, bool approve, string? reason, CancellationToken ct = default)
    {
        var b = await One(id, ct);
        var tenant = (string?)b.Data["tenant"] ?? "";
        if (!who.Has(tenant, OperatorRoles.BankApprover)) throw new ActionRefused(403, $"Consenting needs the {OperatorRoles.BankApprover} role for tenant {tenant}", OpsCodes.Forbidden);
        if (string.IsNullOrWhiteSpace(reason)) throw new ActionRefused(400, "A decision needs a reason", OpsCodes.RemarksRequired);
        if (b.State != "pending-consent" || b.Pending is null) throw new ActionRefused(409, $"Break {id} waits for no consent", OpsCodes.AlreadyDecided);
        var asked = b.History.Last(h => (string?)h["code"] == "resolution-requested");
        if ((string?)asked["actor"] == who.Actor) throw new ActionRefused(403, "The person who asked for a change of the system may not consent to it", OpsCodes.MakerApproves);
        var resolution = (string)b.Pending["resolution"]!;
        if (!approve)
        {
            await Append(b.Run, "resolution-refused", new JsonObject { ["break"] = id, ["resolution"] = resolution, ["why"] = reason, ["by"] = who.DisplayName, ["requestedBy"] = (string?)asked["actor"] }, who.Actor, ct);
            return new JsonObject { ["break"] = id, ["status"] = "open", ["resolution"] = resolution };
        }
        // The change: the scheme's status recorded on the payment's own chain, as a status fact with where it came from.
        var payment = (string)b.Data["payment"]!;
        var status = (string)b.Data["reportedStatus"]!;
        await using (var db = StoreDbContext.Open(store))
        {
            var kind = await db.Bulks.AsNoTracking().AnyAsync(x => x.Id == payment, ct) ? "bulk" : "payment";
            await new FactLog(db, clock).Append(payment, kind, new NewFact("status", status, new JsonObject { ["from"] = "reconciliation", ["break"] = id, ["file"] = b.Data["file"]?.DeepClone() }, Actor: who.Actor), ct);
        }
        await Append(b.Run, "resolved", new JsonObject
        {
            ["break"] = id, ["resolution"] = resolution, ["why"] = (string?)b.Pending["why"], ["consent"] = reason, ["by"] = who.DisplayName, ["requestedBy"] = (string?)asked["actor"], ["status"] = status,
        }, who.Actor, ct);
        return new JsonObject { ["break"] = id, ["status"] = "resolved", ["resolution"] = resolution, ["payment"] = payment, ["adopted"] = status };
    }
}
