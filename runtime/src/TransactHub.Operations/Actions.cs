using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>The operator actions there are (R-05-50). Which apply to a payment is its views' declaration.</summary>
public static class ActionNames
{
    /// <summary>Decisions on a hold: the hold activity's own outcomes.</summary>
    public static readonly Dictionary<string, (string hold, string decision)> HoldDecisions = new()
    {
        ["approve"] = ("approval", "approved"), ["decline"] = ("approval", "declined"),
        ["release"] = ("repair", "released"), ["reject"] = ("repair", "rejected"),
    };
    /// <summary>The fixed signals a workflow may handle (R-05-12).</summary>
    public static readonly string[] Signals = ["cancel", "re-screen", "pause", "resume"];
    /// <summary>
    /// The operator's controls (R-05-55, R-05-58): <c>retry</c> gives a parked step one more attempt, through the engine's host;
    /// <c>resend</c> sends a kept file again on the file-transfer port, by the runtime; <c>requeue</c> runs a dead-lettered step again
    /// with its full retry policy, through the engine's host, on every engine (R-09-91).
    /// </summary>
    public static readonly string[] Controls = ["retry", "resend", "requeue"];
    public static IEnumerable<string> All => HoldDecisions.Keys.Concat(Signals).Concat(Controls);
}

/// <summary>How a signal reaches a running payment's engine. The runtime puts it on the outbox for the engine's host (Issue I-99).</summary>
public interface IEngineSignals
{
    /// <summary>Puts a signal, a hold's decision (<c>decide</c>) or an operator's control on the way to the payment's engine; <paramref name="data"/> is what it carries.</summary>
    Task<string> Send(string tenant, string subjectId, string? instance, string signal, string actionId, CancellationToken ct = default, JsonObject? data = null);
}

/// <summary>A signal as an outbox row (topic <c>engine-signal</c>) for the engine's host to deliver on its own signal path.</summary>
public sealed class OutboxSignals(StoreOptions store, TimeProvider? clock = null) : IEngineSignals
{
    public const string Topic = "engine-signal";
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<string> Send(string tenant, string subjectId, string? instance, string signal, string actionId, CancellationToken ct = default, JsonObject? data = null)
    {
        await using var db = StoreDbContext.Open(store);
        var payload = new JsonObject { ["tenant"] = tenant, ["subject"] = subjectId, ["instance"] = instance, ["signal"] = signal, ["action"] = actionId };
        foreach (var (k, v) in data ?? new JsonObject()) payload[k] = v?.DeepClone();
        db.Outbox.Add(new OutboxRow { Topic = Topic, Key = subjectId, At = StoreTime.Of(clock.GetUtcNow()), PayloadJson = CanonicalJson.Of(payload) });
        await db.SaveChangesAsync(ct);
        return $"{signal} sent to the payment's engine host";
    }
}

public sealed record ActionOffer(string Action, string Label, string Role, string? SecondApprover, bool RemarksRequired, string? Hold, string? Leg);

/// <summary>An operator action refused, with the error dictionary's code set where it is thrown (Issue I-103).</summary>
public sealed class ActionRefused(int status, string message, string code = OpsCodes.ActionNotOffered) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>The operations console's codes, as the platform's error dictionary has them (R-05-51).</summary>
public static class OpsCodes
{
    public const string ActionNotOffered = "ops.ActionNotOffered", RemarksRequired = "ops.RemarksRequired", MakerApproves = "ops.MakerApproves",
        AlreadyDecided = "ops.AlreadyDecided", HoldClosed = "ops.HoldClosed", Forbidden = "auth.Forbidden", NotFound = "ops.UnknownPayment", NoFileKept = "ops.NoFileKept";
}

/// <summary>
/// Operator actions on running payments: offered only as the payment's views declare them, authorised by tenant and role,
/// recorded as facts on the payment's chain with the operator's identity, and, where a view asks for a second approver,
/// carried out only when another person with that role approves (maker-checker).
/// </summary>
public sealed class OperatorActions(StoreOptions store, StoreOptions ops, ViewCatalog views, IEngineSignals signals, TimeProvider? clock = null, FileResend? resend = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>What the views offer for this payment in its present state.</summary>
    public async Task<List<ActionOffer>> Offers(string subjectId, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var subj = await o.Subjects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == subjectId, ct) ?? throw new ActionRefused(404, $"No payment or bulk {subjectId}", OpsCodes.NotFound);
        var open = await OpenHold(subjectId, ct);
        var legs = await o.Legs.AsNoTracking().Where(l => l.SubjectId == subjectId).Select(l => l.Leg).Distinct().ToListAsync(ct);
        var state = await StateOf(subjectId, ct);
        var platform = await PlatformWorkflow(subjectId, ct);
        var offers = new List<ActionOffer>();
        foreach (var v in views.For(subj.Scheme))
        {
            // The payment's own actions, and those of the legs it has.
            var decls = (v.Subject?["actions"] as JsonArray ?? []).OfType<JsonObject>().Select(a => (a, (string?)null))
                .Concat(v.Legs.Where(l => legs.Contains((string?)l["leg"] ?? "")).SelectMany(l => (l["actions"] as JsonArray ?? []).OfType<JsonObject>().Select(a => (a, (string?)l["leg"]))));
            foreach (var (a, leg) in decls)
            {
                var name = (string)a["action"]!;
                if (offers.Any(x => x.Action == name)) continue;
                var when = a["when"] as JsonObject ?? new JsonObject();
                if (ActionNames.HoldDecisions.TryGetValue(name, out var hd) && open?.kind != hd.hold) continue;
                if ((string?)when["hold"] is string wantHold && open?.kind != wantHold) continue;
                // An action for some platform workflows only (R-05-61): offered on a payment that runs one of them. A payment whose
                // steps name no workflow (recorded before they did) is offered it, as before.
                if (when["workflow"] is JsonArray wf && platform is not null && !wf.Any(x => (string?)x == platform)) continue;
                if (when["notStatus"] is JsonArray ns && ns.Any(x => (string?)x == subj.Status)) continue;
                if (when["status"] is JsonArray st && !st.Any(x => (string?)x == subj.Status)) continue;
                // Which states allow what (R-05-58): a signal while the payment runs; retry while a step is parked; resend when a file was sent.
                if (ActionNames.Signals.Contains(name) && state.ended) continue;
                if (name == "retry" && (state.ended || !state.parked)) continue;
                // A dead-lettered step (R-09-91): one whose attempts are spent, parked on any engine or left dead by Flowable.
                if (name == "requeue" && (state.ended || !(state.parked || state.deadLetter))) continue;
                if (name == "resend" && !state.file) continue;
                offers.Add(new ActionOffer(name, (string?)a["label"] ?? name, (string?)a["role"] ?? OperatorRoles.Operator, (string?)a["secondApprover"],
                    (string?)a["remarks"] == "required", ActionNames.HoldDecisions.ContainsKey(name) ? open?.id : null, leg));
            }
        }
        return offers;
    }

    /// <summary>The open hold of a payment, from the store itself (the read model may be a moment behind).</summary>
    async Task<(string id, string kind)?> OpenHold(string subjectId, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "hold").OrderBy(f => f.Seq).ToListAsync(ct);
        var closed = facts.Where(f => f.Code != "opened").Select(f => (string?)JsonNode.Parse(f.DataJson)?["hold"]).ToHashSet();
        var open = facts.LastOrDefault(f => f.Code == "opened" && !closed.Contains((string?)JsonNode.Parse(f.DataJson)?["hold"]));
        if (open is null) return null;
        var d = JsonNode.Parse(open.DataJson)!;
        return ((string)d["hold"]!, (string?)d["kind"] ?? "approval");
    }

    /// <summary>
    /// The platform workflow a payment runs (R-05-61): the workflow its root instance's steps name. The root is the instance with the
    /// shortest path; a leg's instance is under it. Null when no step names one.
    /// </summary>
    async Task<string?> PlatformWorkflow(string subjectId, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var steps = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "step").OrderBy(f => f.Seq).Select(f => f.DataJson).Take(200).ToListAsync(ct);
        return steps.Select(j => JsonNode.Parse(j) as JsonObject).Where(d => d?["workflow"] is not null && d["instance"] is not null)
            .OrderBy(d => ((string)d!["instance"]!).Count(c => c == '/')).Select(d => (string?)d!["workflow"]).FirstOrDefault();
    }

    /// <summary>What the store says of a payment now: whether it has ended, whether its last step is parked, whether a file of it was kept.</summary>
    async Task<(bool ended, bool parked, bool file, bool deadLetter)> StateOf(string subjectId, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var ended = await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == subjectId && f.Kind == "payment.end", ct);
        var lastStep = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "step").OrderByDescending(f => f.Seq).Select(f => f.Code).FirstOrDefaultAsync(ct);
        var file = await db.Messages.AsNoTracking().AnyAsync(m => m.SubjectId == subjectId && m.Direction == "Outward", ct);
        // A Flowable dead-letter job the binding surfaced (engine dead-letter) and no requeue of it delivered since (R-09-91).
        var dead = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && (f.Kind == "engine" && f.Code == "dead-letter" || f.Kind == "engine" && f.Code == "requeued")).OrderByDescending(f => f.Seq).Select(f => f.Code).FirstOrDefaultAsync(ct);
        return (ended, lastStep == "stuck", file, dead == "dead-letter");
    }

    async Task<(string tenant, string kind)> SubjectOf(string subjectId, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var b = await db.Bulks.AsNoTracking().Where(x => x.Id == subjectId).Select(x => x.Tenant).FirstOrDefaultAsync(ct);
        if (b is not null) return (b, "bulk");
        var p = await db.Payments.AsNoTracking().Where(x => x.Id == subjectId).Select(x => x.Tenant).FirstOrDefaultAsync(ct);
        return p is not null ? (p, "payment") : throw new ActionRefused(404, $"No payment or bulk {subjectId}", OpsCodes.NotFound);
    }

    /// <summary>An operator asks for an action. Without a second approver it is carried out at once.</summary>
    public async Task<JsonObject> Request(OperatorIdentity who, string subjectId, string action, string? remarks, CancellationToken ct = default)
    {
        var (tenant, kind) = await SubjectOf(subjectId, ct);
        if (!who.Has(tenant, OperatorRoles.Operator)) throw new ActionRefused(403, $"{who.DisplayName} has no operator role for tenant {tenant}", OpsCodes.Forbidden);
        var offer = (await Offers(subjectId, ct)).FirstOrDefault(x => x.Action == action)
            ?? throw new ActionRefused(409, $"{action} does not apply to this payment now");
        if (!who.Has(tenant, offer.Role)) throw new ActionRefused(403, $"{action} needs the {offer.Role} role for tenant {tenant}", OpsCodes.Forbidden);
        if (offer.RemarksRequired && string.IsNullOrWhiteSpace(remarks)) throw new ActionRefused(400, $"{action} needs remarks", OpsCodes.RemarksRequired);
        var id = Guid.NewGuid().ToString("N");
        await using var db = StoreDbContext.Open(store);
        var log = new FactLog(db, clock);
        var data = new JsonObject { ["action"] = id, ["name"] = action, ["remarks"] = remarks, ["hold"] = offer.Hold, ["leg"] = offer.Leg, ["operatorName"] = who.DisplayName };
        if (offer.SecondApprover is not null) data["secondApprover"] = offer.SecondApprover;
        await log.Append(subjectId, kind, new NewFact("operator", "requested", data, Actor: who.Actor), ct);
        if (offer.SecondApprover is not null)
            return new JsonObject { ["action"] = id, ["status"] = "pending-approval", ["secondApprover"] = offer.SecondApprover };
        var result = await Execute(db, tenant, subjectId, kind, id, action, offer.Hold, who, ct);
        return new JsonObject { ["action"] = id, ["status"] = result.status, ["result"] = result.text };
    }

    /// <summary>A second person approves or rejects an action that waits for one. The maker may not approve their own.</summary>
    public async Task<JsonObject> Decide(OperatorIdentity who, string subjectId, string actionId, bool approve, string? remarks, CancellationToken ct = default)
    {
        var (tenant, kind) = await SubjectOf(subjectId, ct);
        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "operator").OrderBy(f => f.Seq).ToListAsync(ct);
        var mine = facts.Where(f => (string?)JsonNode.Parse(f.DataJson)?["action"] == actionId).ToList();
        var req = mine.FirstOrDefault(f => f.Code == "requested") ?? throw new ActionRefused(404, $"No action {actionId}", OpsCodes.ActionNotOffered);
        if (mine.Count > 1) throw new ActionRefused(409, $"Action {actionId} is already {mine[^1].Code}", OpsCodes.AlreadyDecided);
        var d = (JsonObject)JsonNode.Parse(req.DataJson)!;
        var role = (string?)d["secondApprover"] ?? throw new ActionRefused(409, "This action needs no second approver");
        if (req.Actor == who.Actor) throw new ActionRefused(403, "The person who asked for an action may not approve it", OpsCodes.MakerApproves);
        if (!who.Has(tenant, role)) throw new ActionRefused(403, $"Approving needs the {role} role for tenant {tenant}", OpsCodes.Forbidden);
        var log = new FactLog(db, clock);
        await log.Append(subjectId, kind, new NewFact("operator", approve ? "approved" : "rejected", new JsonObject { ["action"] = actionId, ["remarks"] = remarks }, Actor: who.Actor), ct);
        if (!approve) return new JsonObject { ["action"] = actionId, ["status"] = "rejected" };
        var hold = (string?)d["hold"];
        var result = await Execute(db, tenant, subjectId, kind, actionId, (string)d["name"]!, hold, who, ct);
        return new JsonObject { ["action"] = actionId, ["status"] = result.status, ["result"] = result.text };
    }

    async Task<(string status, string text)> Execute(StoreDbContext db, string tenant, string subjectId, string kind, string id, string action, string? hold, OperatorIdentity who, CancellationToken ct)
    {
        var log = new FactLog(db, clock);
        string status, text;
        if (ActionNames.HoldDecisions.TryGetValue(action, out var hd))
        {
            var open = await OpenHold(subjectId, ct);
            if (open is null || open.Value.id != hold)
            {
                (status, text) = ("refused", "the hold is no longer open");
            }
            else
            {
                // The decision the hold waits for (the operations-queue port's decision), on the payment's chain, and on its way to
                // the engine, which awaits it (R-05-56, Issue I-98).
                await log.Append(subjectId, kind, new NewFact("hold", "decided", new JsonObject { ["hold"] = hold, ["decision"] = hd.decision, ["by"] = who.Actor, ["action"] = id }, Actor: who.Actor), ct);
                await signals.Send(tenant, subjectId, null, "decide", id, ct, new JsonObject { ["hold"] = hold, ["decision"] = hd.decision, ["by"] = who.Actor });
                (status, text) = ("done", $"hold {hd.decision}");
            }
        }
        else if (ActionNames.Signals.Contains(action) || action == "retry" || action == "requeue")
        {
            (status, text) = ("done", await signals.Send(tenant, subjectId, null, action, id, ct));
        }
        else if (action == "resend")
        {
            if (resend is null) (status, text) = ("refused", "no file-transfer port is configured on this host");
            else
            {
                var r = await resend.Resend(subjectId, kind, null, who.Actor, id, ct);
                (status, text) = ("done", (string?)r["result"] ?? "sent");
            }
        }
        else (status, text) = ("refused", $"{action} is not an operator action");
        await log.Append(subjectId, kind, new NewFact("operator", status, new JsonObject { ["action"] = id, ["result"] = text }, Actor: who.Actor), ct);
        return (status, text);
    }
}
