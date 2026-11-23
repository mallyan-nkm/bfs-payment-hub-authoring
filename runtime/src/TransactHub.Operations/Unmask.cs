using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// Unmasking on a bank approver's consent (R-05-69, Issues I-100, I-149): an operator of the payment's tenant asks to see one masked
/// field of a payment or of one of its lines, with a reason; a bank approver, another person, consents (R-09-77); from the consent
/// the value is shown to the person who asked, and to nobody else, for the bank's <c>unmaskWindow</c> (P0D, the default, turns
/// unmasking off). The request, the consent and each view are facts on the console's own chain, with ids only: no fact, log or
/// list holds the value. An erased payment is never unmasked (R-05-70).
/// </summary>
public sealed class Unmasking(StoreOptions ops, ViewCatalog views, ConsoleRecord record, Projector projector, OperationsDials dials, Erasure erasure, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>The declared field a request names: <c>subject:&lt;id&gt;</c> or <c>line:&lt;id&gt;</c>, in the platform's view or the payment's scheme's.</summary>
    (OperationsView view, JsonObject decl, bool line)? Field(string scheme, string field)
    {
        var parts = field.Split(':');
        if (parts.Length != 2 || parts[0] is not ("subject" or "line")) return null;
        foreach (var v in views.For(scheme))
        {
            var list = parts[0] == "subject" ? v.Subject?["fields"] as JsonArray : Sources.Path(v.Subject, "lines.fields") as JsonArray;
            if ((list ?? []).OfType<JsonObject>().FirstOrDefault(f => (string?)f["id"] == parts[1]) is { } d) return (v, d, parts[0] == "line");
        }
        return null;
    }

    public async Task<JsonObject> Ask(OperatorIdentity who, string subjectId, string field, int? lineNo, string? why, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var s = await o.Subjects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == subjectId, ct);
        if (s is null || !who.Has(s.Tenant, OperatorRoles.Operator)) throw new ActionRefused(404, $"No payment or bulk {subjectId} for your tenants", OpsCodes.NotFound);
        if (Field(s.Scheme, field) is not { } f) throw new ActionRefused(404, $"No declared field {field} on {subjectId}'s views", OpsCodes.ActionNotOffered);
        var mask = (string?)f.decl["mask"];
        if (mask is null or "none") throw new ActionRefused(409, $"{field} is not masked; there is nothing to unmask", OpsCodes.ActionNotOffered);
        if (f.line && lineNo is null) throw new ActionRefused(400, $"{field} is a line's field: name the line", OpsCodes.ActionNotOffered);
        if (f.line && !await o.Lines.AsNoTracking().AnyAsync(l => l.BulkId == subjectId && l.LineNo == lineNo, ct)) throw new ActionRefused(404, $"{subjectId} has no line {lineNo}", OpsCodes.NotFound);
        if (await erasure.IsErased(subjectId, ct)) throw new ActionRefused(410, $"{subjectId} is erased; nothing of it is unmasked", OpsCodes.ActionNotOffered);
        var window = await dials.For(s.Tenant, null, OperationsDials.UnmaskWindow, clock.GetUtcNow(), ct);
        if (window.Value <= TimeSpan.Zero) throw new ActionRefused(409, $"Unmasking is off: the bank's {window.Parameter} is P0D", OpsCodes.ActionNotOffered);
        return await record.Request(who, ConsoleRecord.Unmask, why, ct, new JsonObject
        {
            ["subject"] = subjectId, ["tenant"] = s.Tenant, ["field"] = field, ["line"] = f.line ? lineNo : null, ["view"] = f.view.Key, ["label"] = (string?)f.decl["label"],
        });
    }

    /// <summary>On the bank approver's consent: the window it opens, from now, for the person who asked only.</summary>
    public async Task<JsonObject> Grant(JsonObject details, string requestedBy, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var window = await dials.For((string)details["tenant"]!, null, OperationsDials.UnmaskWindow, now, ct);
        if (window.Value <= TimeSpan.Zero) throw new ActionRefused(409, $"Unmasking is off: the bank's {window.Parameter} is P0D", OpsCodes.ActionNotOffered);
        return new JsonObject { ["grantedTo"] = requestedBy, ["from"] = now.ToString("O"), ["until"] = (now + window.Value).ToString("O"), ["window"] = OperationsDials.Iso(window.Value) };
    }

    /// <summary>The unmasked value, for the person who asked, while the window is open; each view a fact.</summary>
    public async Task<JsonObject> View(OperatorIdentity who, string actionId, CancellationToken ct = default)
    {
        var facts = await record.Of(actionId, ct);
        var req = facts.FirstOrDefault(f => f.Code == "requested" && (string?)JsonNode.Parse(f.DataJson)?["name"] == ConsoleRecord.Unmask)
            ?? throw new ActionRefused(404, $"No unmask request {actionId}", OpsCodes.NotFound);
        if (req.Actor != who.Actor) throw new ActionRefused(403, "Only the person who asked sees the unmasked value", OpsCodes.Forbidden);
        var done = facts.FirstOrDefault(f => f.Code == "done");
        if (facts.Any(f => f.Code == "rejected")) throw new ActionRefused(403, "The bank approver refused this unmask", OpsCodes.Forbidden);
        if (done is null) throw new ActionRefused(409, "The unmask waits for a bank approver's consent", OpsCodes.ActionNotOffered);
        var result = JsonNode.Parse(done.DataJson)?["result"] as JsonObject ?? new JsonObject();
        var until = DateTimeOffset.Parse((string)result["until"]!, System.Globalization.CultureInfo.InvariantCulture);
        if (clock.GetUtcNow() >= until) throw new ActionRefused(410, $"The window closed at {until:O}; ask again", OpsCodes.ActionNotOffered);
        var d = JsonNode.Parse(req.DataJson)!["details"]!.AsObject();
        var subject = (string)d["subject"]!;
        if (await erasure.IsErased(subject, ct)) throw new ActionRefused(410, $"{subject} is erased; nothing of it is unmasked", OpsCodes.ActionNotOffered);
        await using var o = OpsDbContext.Open(ops);
        var s = await o.Subjects.AsNoTracking().FirstAsync(x => x.Id == subject, ct);
        var field = (string)d["field"]!;
        var f = Field(s.Scheme, field) ?? throw new ActionRefused(404, $"No declared field {field}", OpsCodes.ActionNotOffered);
        var line = (int?)d["line"];
        var value = await projector.Unmasked(subject, (string)f.decl["source"]!, line, ct);
        await record.Note(ConsoleRecord.UnmaskKind, "viewed", new JsonObject { ["action"] = actionId, ["subject"] = subject, ["field"] = field, ["line"] = line, ["by"] = who.DisplayName }, who.Actor, ct);
        return new JsonObject { ["action"] = actionId, ["subject"] = subject, ["field"] = field, ["line"] = line, ["label"] = (string?)f.decl["label"], ["value"] = value, ["until"] = until.ToString("O") };
    }
}
