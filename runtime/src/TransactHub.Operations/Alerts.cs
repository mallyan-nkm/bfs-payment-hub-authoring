using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The fraud alert list (R-05-90; owner's decision of 23 November 2026, docs/OWNER-ACTIONS.md A2, Issue I-189): a payment that the
/// bank's fraud engine found suspect (<c>p.screen.fraud.v1</c>, outcome <c>suspect</c>) is an alert, a work item of kind
/// <c>alert</c> whose id is the payment's, with its reason code and a link to the payment. Its state follows the payment: <c>held</c>
/// while its hold is open, <c>released</c> once the hold was approved, <c>refused</c> once it was declined or expired, or the payment
/// was refused (RJCT). Staff work it as any work item (assign, escalate, release to the pool, note), and an alert that needs no more
/// work is closed with a note. Releasing or refusing the payment is the payment's own action (<c>approve</c>, <c>decline</c>) through
/// <see cref="OperatorActions"/>, with the view's maker-checker: the alert list changes no payment itself. It reads the read model only.
/// Full case management is not here (Issue I-188).
/// </summary>
public sealed class FraudAlerts(StoreOptions store, StoreOptions ops, TimeProvider? clock = null)
{
    public const string Activity = "p.screen.fraud.v1", Suspect = "suspect";
    public const string Held = "held", Released = "released", Refused = "refused";
    /// <summary>The payment's actions an alert offers: release (approve the hold) and refuse (decline it).</summary>
    public static readonly Dictionary<string, string> PaymentActions = new() { ["release"] = "approve", ["refuse"] = "decline" };

    public sealed record Alert(string Id, string Tenant, string Scheme, string Kind, decimal Amount, string Currency, string? Reason, string? Step, DateTimeOffset RaisedAt, string State, string? Status, bool Closed);

    /// <summary>Every alert of the tenants, newest first: one per payment found suspect, as its latest suspect step says.</summary>
    public async Task<List<Alert>> List(IReadOnlyCollection<string> tenants, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var hits = await (from t in o.Timeline.AsNoTracking()
                          join s in o.Subjects.AsNoTracking() on t.SubjectId equals s.Id
                          where t.Kind == "step" && t.Activity == Activity && t.Code == Suspect && tenants.Contains(s.Tenant)
                          orderby t.At descending
                          select new { t, s }).Take(2000).ToListAsync(ct);
        var latest = hits.GroupBy(h => h.s.Id).Select(g => g.First()).ToList();
        var ids = latest.Select(h => h.s.Id).ToList();
        var holds = await o.Holds.AsNoTracking().Where(h => ids.Contains(h.SubjectId)).ToListAsync(ct);
        var closed = await Closed(ids, ct);
        return latest.Select(h =>
        {
            var d = JsonNode.Parse(h.t.DataJson) as JsonObject;
            var reason = (string?)d?["reasonCode"] ?? (string?)d?["writes"]?["facts.screening.fraud"]?["reasonCode"];
            // The hold the suspect step opened: the first one after it.
            var hold = holds.Where(x => x.SubjectId == h.s.Id && x.OpenedAt >= h.t.At).OrderBy(x => x.OpenedAt).FirstOrDefault();
            var state = h.s.Status == "RJCT" ? Refused
                : hold is null || hold.Status == "open" ? Held
                : hold.Decision is "approved" ? Released : Refused;
            return new Alert(h.s.Id, h.s.Tenant, h.s.Scheme, h.s.Kind, h.s.Amount, h.s.Currency, reason, h.t.Step, h.t.At, state, h.s.Status, closed.Contains(h.s.Id));
        }).ToList();
    }

    /// <summary>Whether a payment is an alert, and its tenant.</summary>
    public async Task<string?> TenantOf(string id, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        return await (from t in o.Timeline.AsNoTracking()
                      join s in o.Subjects.AsNoTracking() on t.SubjectId equals s.Id
                      where s.Id == id && t.Kind == "step" && t.Activity == Activity && t.Code == Suspect
                      select s.Tenant).FirstOrDefaultAsync(ct);
    }

    /// <summary>The alerts whose chain has a <c>closed</c> fact.</summary>
    async Task<HashSet<string>> Closed(List<string> ids, CancellationToken ct)
    {
        var chains = ids.ToDictionary(i => new WorkItem(WorkItem.Alert, i).Chain, i => i);
        await using var db = StoreDbContext.Open(store);
        var keys = chains.Keys.ToList();
        var hit = new HashSet<string>();
        foreach (var chunk in keys.Chunk(500))
            foreach (var c in await db.Facts.AsNoTracking().Where(f => f.Kind == WorkQueue.Kind && f.Code == "closed" && chunk.Contains(f.SubjectId)).Select(f => f.SubjectId).ToListAsync(ct))
                hit.Add(chains[c]);
        return hit;
    }

    /// <summary>An alert as the console shows it, with the link to its payment.</summary>
    public static JsonObject Show(Alert a, WorkState? st = null) => new()
    {
        ["item"] = a.Id, ["itemKind"] = WorkItem.Alert, ["tenant"] = a.Tenant, ["scheme"] = a.Scheme, ["payment"] = a.Id, ["link"] = $"/payments/{Uri.EscapeDataString(a.Id)}",
        ["reason"] = a.Reason, ["step"] = a.Step, ["raisedAt"] = a.RaisedAt.ToString("O"), ["state"] = a.State, ["status"] = a.Status, ["closed"] = a.Closed,
        ["amount"] = a.Amount, ["currency"] = a.Currency,
        ["assignee"] = st?.Assignee, ["assigneeName"] = st?.AssigneeName, ["escalated"] = st?.Escalated ?? false, ["level"] = st?.Level ?? 0,
    };

    /// <summary>An alert is open work while its payment is held, or once refused or released until it is closed.</summary>
    public static bool Open(Alert a) => !a.Closed && a.State != Released;

    /// <summary>
    /// The payment's own action from an alert: <c>release</c> asks <c>approve</c>, <c>refuse</c> asks <c>decline</c>, through the
    /// payment's offers, roles and second approver as <see cref="OperatorActions.Request"/> applies them; the alert's chain records that
    /// it was asked, and by whom.
    /// </summary>
    public async Task<JsonObject> Act(OperatorIdentity who, OperatorActions actions, string id, string what, string? remarks, CancellationToken ct = default)
    {
        if (!PaymentActions.TryGetValue(what, out var action)) throw new ActionRefused(400, $"No alert action {what}: write release or refuse", OpsCodes.ActionNotOffered);
        var tenant = await TenantOf(id, ct) ?? throw new ActionRefused(404, $"No alert {id}", OpsCodes.NotFound);
        if (!who.Has(tenant, OperatorRoles.Operator)) throw new ActionRefused(403, $"No operator role for tenant {tenant}", OpsCodes.Forbidden);
        var r = await actions.Request(who, id, action, remarks, ct);
        await using var db = StoreDbContext.Open(store);
        await new FactLog(db, clock).Append(new WorkItem(WorkItem.Alert, id).Chain, WorkQueue.SubjectKind, new NewFact(WorkQueue.Kind, "acted",
            new JsonObject { ["item"] = id, ["itemKind"] = WorkItem.Alert, ["tenant"] = tenant, ["by"] = who.DisplayName, ["why"] = remarks, ["paymentAction"] = action, ["action"] = (string?)r["action"], ["status"] = (string?)r["status"] }, Actor: who.Actor), ct);
        r["alert"] = id;
        r["asked"] = action;
        return r;
    }
}
