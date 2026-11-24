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
/// Since R-09-250 (owner's decision of 24 November 2026, Issue I-168) a break may also start an order: <c>exception-request</c>, an
/// exception request on <c>p.wf.request.v1</c>, or <c>return</c>, a return; each changes the system, so it waits for the same
/// consent. Which message starts it is the scheme's: the report's reconcile disposition names it (<c>resolutions</c>, lint L62);
/// a scheme that names none is not offered it. On consent the break is resolved with the order to start, which the File API's
/// front door starts as that message, from the break's order document (<see cref="BreakOrder"/>), recording on the run's chain
/// the order started or refused.
/// </summary>
public sealed class BreakQueue(StoreOptions store, TimeProvider? clock = null, BreakQueue.OrderMessages? orders = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public const string Kind = "reconciliation";
    public const string ExceptionRequest = "exception-request", Return = "return";
    /// <summary>The resolutions there are, and whether each changes the system (and so needs a bank approver's consent).</summary>
    public static readonly Dictionary<string, bool> Resolutions = new() { ["explain"] = false, ["adopt-status"] = true, [ExceptionRequest] = true, [Return] = true };
    /// <summary>The resolutions that start an order, by the message the scheme's report names for each (R-09-250).</summary>
    public static readonly string[] Orders = [ExceptionRequest, Return];
    /// <summary>The code of a resolution that starts an order where the scheme's report names no message for it.</summary>
    public const string NoOrderMessage = "ops.BreakNoOrderMessage";

    /// <summary>
    /// The messages a scheme's report names to start each order (its reconcile disposition's <c>resolutions</c>), as the distribution
    /// active for the bank holds them; null when none.
    /// </summary>
    public delegate Task<JsonObject?> OrderMessages(string tenant, string scheme, string? reportMessage, CancellationToken ct);

    /// <summary>The report's resolutions from the scheme manifest of the distribution active for the bank in an environment.</summary>
    public static OrderMessages FromActiveDistribution(StoreOptions store, string? environment) => async (tenant, scheme, report, ct) =>
    {
        if (environment is not { Length: > 0 }) return null;
        await using var db = StoreDbContext.Open(store);
        var dist = await new DistributionRegistry(db).Active(tenant, environment, ct);
        if (dist is null) return null;
        var (b, id) = DistributionRegistry.Split(dist);
        var row = await new ReleaseRegistry(db).Get(DistributionRegistry.Scope(b), id, ct);
        if (row is null || !UploadCatalogue.Schemes(row.Package).TryGetValue(scheme, out var s)) return null;
        var reports = (s["messages"] as JsonArray ?? []).OfType<JsonObject>().Where(m => m["disposition"]?["reconcile"] is JsonObject).ToList();
        // The report the break came from; a break recorded before its run named one, the scheme's only report.
        var m = report is { Length: > 0 } ? reports.FirstOrDefault(x => (string?)x["id"] == report) : reports.Count == 1 ? reports[0] : null;
        return m?["disposition"]?["reconcile"]?["resolutions"]?.DeepClone() as JsonObject;
    };

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

    static JsonObject Show(Break b, JsonObject? resolutions = null) => new()
    {
        ["offered"] = new JsonArray(Offered(b, resolutions).Select(x => (JsonNode?)x).ToArray()),
        ["order"] = b.History.LastOrDefault(h => (string?)h["code"] is "order-started" or "order-refused") is { } o
            ? new JsonObject { ["state"] = (string?)o["code"] == "order-started" ? "started" : "refused", ["payment"] = o["data"]?["payment"]?.DeepClone(), ["code"] = o["data"]?["code"]?.DeepClone(), ["why"] = o["data"]?["why"]?.DeepClone() }
            : b.History.LastOrDefault(h => (string?)h["code"] == "resolved")?["data"]?["order"] is JsonObject due
                ? new JsonObject { ["state"] = "to-start", ["message"] = due["message"]?.DeepClone() } : null,
        ["break"] = b.Id, ["run"] = b.Run, ["state"] = b.State, ["kind"] = b.Data["kind"]?.DeepClone(), ["reference"] = b.Data["reference"]?.DeepClone(),
        ["payment"] = b.Data["payment"]?.DeepClone(), ["expected"] = b.Data["expected"]?.DeepClone(), ["reported"] = b.Data["reported"]?.DeepClone(),
        ["reportedStatus"] = b.Data["reportedStatus"]?.DeepClone(), ["tenant"] = b.Data["tenant"]?.DeepClone(), ["scheme"] = b.Data["scheme"]?.DeepClone(),
        ["file"] = b.Data["file"]?.DeepClone(), ["pending"] = b.Pending?.DeepClone(), ["history"] = new JsonArray(b.History.Select(x => (JsonNode)x.DeepClone()).ToArray()),
    };

    /// <summary>What may resolve an open break: an explanation always; the scheme's status where the break names one; an order the report names a message for.</summary>
    static IEnumerable<string> Offered(Break b, JsonObject? resolutions)
    {
        if (b.State != "open") yield break;
        yield return "explain";
        if ((string?)b.Data["payment"] is not null && (string?)b.Data["reportedStatus"] is not null) yield return "adopt-status";
        foreach (var o in Orders) if ((string?)resolutions?[o] is { Length: > 0 }) yield return o;
    }

    async Task<JsonObject?> ResolutionsOf(Break b, CancellationToken ct) => orders is null ? null
        : await orders((string?)b.Data["tenant"] ?? "", (string?)b.Data["scheme"] ?? "", (string?)b.Data["message"], ct);

    /// <summary>The breaks of the tenants the person may see; open and pending first.</summary>
    public async Task<JsonArray> List(OperatorIdentity who, bool all = false, CancellationToken ct = default)
    {
        var rows = (await All(ct)).Where(b => Sees(who, (string?)b.Data["tenant"] ?? "")).Where(b => all || b.State != "resolved")
            .OrderBy(b => b.State == "resolved").ThenBy(b => b.Run, StringComparer.Ordinal).ThenBy(b => (int?)b.Data["line"]).ToList();
        var cache = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        var shown = new List<JsonNode>();
        foreach (var b in rows)
        {
            var key = $"{(string?)b.Data["tenant"]}|{(string?)b.Data["scheme"]}|{(string?)b.Data["message"]}";
            if (!cache.TryGetValue(key, out var r)) cache[key] = r = b.State == "open" ? await ResolutionsOf(b, ct) : null;
            shown.Add(Show(b, r));
        }
        return new JsonArray(shown.ToArray());
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
        // An order the scheme's report names a message for (R-09-250): the message is the scheme's, read now and kept with the request.
        if (Orders.Contains(resolution))
        {
            if ((string?)(await ResolutionsOf(b, ct))?[resolution] is not { Length: > 0 } message)
                throw new ActionRefused(409, $"The {(string?)b.Data["scheme"]} report names no message that starts an {resolution} for a break", NoOrderMessage);
            data["message"] = message;
        }
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
        // An order (R-09-250): the break is resolved with the order to start; the File API's front door starts it, as the scheme's
        // message, and records on the run's chain that it started or why it did not.
        if (Orders.Contains(resolution))
        {
            await Append(b.Run, "resolved", new JsonObject
            {
                ["break"] = id, ["resolution"] = resolution, ["why"] = (string?)b.Pending["why"], ["consent"] = reason, ["by"] = who.DisplayName, ["requestedBy"] = (string?)asked["actor"],
                ["order"] = new JsonObject { ["message"] = b.Pending["message"]?.DeepClone(), ["state"] = "to-start" },
            }, who.Actor, ct);
            return new JsonObject { ["break"] = id, ["status"] = "resolved", ["resolution"] = resolution, ["order"] = (string?)b.Pending["message"] };
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

/// <summary>
/// A break's order (R-09-250, Issue I-168): the platform's document of a break that a bank approver consented to resolve by an exception
/// request or a return, as the File API hands it to the scheme's message that starts the order (<c>th-break-order/1</c>, JSON). It names
/// the break, its run and report file, the payment it concerns, the reference, what the store expected and what the report said, and
/// who asked and consented, by their names as the facts record them. The scheme's intake map of that message reads it.
/// </summary>
public static class BreakOrder
{
    public const string Format = "th-break-order/1";

    public static JsonObject Of(string run, JsonObject breakData, JsonObject resolved) => new()
    {
        ["format"] = Format, ["resolution"] = resolved["resolution"]?.DeepClone(), ["break"] = breakData["break"]?.DeepClone(), ["run"] = run,
        ["breakKind"] = breakData["kind"]?.DeepClone(), ["reference"] = breakData["reference"]?.DeepClone(), ["payment"] = breakData["payment"]?.DeepClone(),
        ["expected"] = breakData["expected"]?.DeepClone(), ["reported"] = breakData["reported"]?.DeepClone(), ["reportedStatus"] = breakData["reportedStatus"]?.DeepClone(),
        ["file"] = breakData["file"]?.DeepClone(), ["tenant"] = breakData["tenant"]?.DeepClone(), ["scheme"] = breakData["scheme"]?.DeepClone(),
        ["why"] = resolved["why"]?.DeepClone(), ["consent"] = resolved["consent"]?.DeepClone(), ["requestedBy"] = resolved["requestedBy"]?.DeepClone(), ["consentedBy"] = resolved["by"]?.DeepClone(),
    };

    /// <summary>
    /// The orders consented to and not yet started or refused, each with its run, its break's data and the resolution's fact: what the
    /// File API starts. A second pass finds none it handled, from the facts, so the start is once per break.
    /// </summary>
    public static async Task<List<(string Run, JsonObject Break, JsonObject Resolved)>> Pending(StoreOptions store, string? tenant = null, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectKind == SchemeReconciliation.SubjectKind && f.Kind == BreakQueue.Kind
            && (f.Code == "break" || f.Code == "resolved" || f.Code == "order-started" || f.Code == "order-refused")).OrderBy(f => f.Seq).ToListAsync(ct);
        var breaks = new Dictionary<string, (string run, JsonObject data)>(StringComparer.Ordinal);
        var todo = new Dictionary<string, (string run, JsonObject resolved)>(StringComparer.Ordinal);
        foreach (var f in facts)
        {
            if (JsonNode.Parse(f.DataJson) is not JsonObject d || (string?)d["break"] is not { } id) continue;
            switch (f.Code)
            {
                case "break": breaks[id] = (f.SubjectId, d); break;
                case "resolved" when d["order"] is JsonObject: todo[id] = (f.SubjectId, d); break;
                case "order-started" or "order-refused": todo.Remove(id); break;
            }
        }
        return todo.Where(t => breaks.ContainsKey(t.Key) && (tenant is null || (string?)breaks[t.Key].data["tenant"] == tenant))
            .Select(t => (t.Value.run, breaks[t.Key].data, t.Value.resolved)).ToList();
    }

    /// <summary>Records what became of a break's order on its run's chain: started, with the payment, or refused, with the code and why.</summary>
    public static async Task Record(StoreOptions store, string run, string breakId, bool started, JsonObject data, TimeProvider? clock = null, CancellationToken ct = default)
    {
        data["break"] = breakId;
        await using var db = StoreDbContext.Open(store);
        await new FactLog(db, clock ?? TimeProvider.System).Append(run, SchemeReconciliation.SubjectKind, new NewFact(BreakQueue.Kind, started ? "order-started" : "order-refused", data, Actor: "file-api:reconcile"), ct);
    }
}
