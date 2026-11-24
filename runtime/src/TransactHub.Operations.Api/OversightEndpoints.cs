using Microsoft.EntityFrameworkCore;
using TransactHub.Operations;
using TransactHub.Store;

namespace TransactHub.Operations.Api;

/// <summary>
/// The fraud alert list (R-05-90), the bank's ledger accounts (R-05-91) and the exchange rates' staleness (R-05-92), under
/// <c>/ops/api</c>, each for the tenants the caller holds a role for. None of them is offered to a channel: the channel's API is
/// <c>/api/channel</c> (ChannelEndpoints), which names none of them.
/// </summary>
public static class OversightEndpoints
{
    public sealed record AlertAction(string? Action, string? Remarks);

    static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);
    static OperatorIdentity? Who(HttpContext http) => http.RequestServices.GetRequiredService<IOperatorIdentityResolver>().Resolve(http.User);
    static string? Q(HttpContext http, string k) => http.Request.Query.TryGetValue(k, out var v) && v.ToString().Length > 0 ? v.ToString() : null;

    static async Task<List<string>> Tenants(OperatorIdentity who, StoreOptions ops, string role)
    {
        await using var o = OpsDbContext.Open(ops);
        return (who.AllTenants ? await o.Subjects.AsNoTracking().Select(s => s.Tenant).Distinct().ToListAsync() : who.Tenants.ToList()).Where(t => who.Has(t, role)).ToList();
    }

    public static void MapOversight(this WebApplication app, StoreOptions store, StoreOptions ops)
    {
        var api = app.MapGroup("/ops/api");
        var alerts = new FraudAlerts(store, ops);

        // ------------------------------------------------------------------ fraud alerts (R-05-90)
        api.MapGet("/alerts", async (HttpContext http, WorkQueue work) =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var tenants = await Tenants(who, ops, OperatorRoles.Operator);
            if (Q(http, "tenant") is { } t) { if (!tenants.Contains(t)) return Problem(403, $"No role for tenant {t}"); tenants = [t]; }
            var list = await alerts.List(tenants, http.RequestAborted);
            var open = Q(http, "state") != "all";
            var shown = list.Where(a => !open || FraudAlerts.Open(a)).ToList();
            var states = await work.States(shown.Select(a => new WorkItem(WorkItem.Alert, a.Id)).ToList(), http.RequestAborted);
            return Results.Ok(new
            {
                total = shown.Count, open = list.Count(FraudAlerts.Open),
                rows = shown.Select(a => FraudAlerts.Show(a, states[new WorkItem(WorkItem.Alert, a.Id)])),
            });
        });
        // Releasing or refusing the payment: the payment's own action (approve or decline its hold), with the view's maker-checker.
        api.MapPost("/alerts/{id}/payment-action", async (string id, AlertAction body, HttpContext http, OperatorActions actions, Projector p) =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            try
            {
                var r = await alerts.Act(who, actions, id, body.Action ?? "", body.Remarks, http.RequestAborted);
                await p.CatchUp(http.RequestAborted);
                return Results.Ok(r);
            }
            catch (ActionRefused e) { return Results.Json(new { error = e.Message, code = e.Code }, statusCode: e.Status); }
        });

        // ------------------------------------------------------------------ the payments whose file is undelivered (R-05-103)
        // A file the File API kept and could not deliver: the send said it was taken, the delivery says it never left (Issue I-337).
        var undelivered = new UndeliveredFiles(ops);
        api.MapGet("/undelivered", async (HttpContext http) =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var tenants = await Tenants(who, ops, OperatorRoles.Operator);
            if (Q(http, "tenant") is { } t) { if (!tenants.Contains(t)) return Problem(403, $"No role for tenant {t}"); tenants = [t]; }
            var rows = await undelivered.List(tenants, http.RequestAborted);
            return Results.Ok(new { total = rows.Count, undelivered = rows.Count(r => r.State == UndeliveredFiles.Undelivered), rows = rows.Select(UndeliveredFiles.Show) });
        });

        // ------------------------------------------------------------------ the bank's ledger accounts (R-05-91)
        api.MapGet("/ledger-accounts", async (HttpContext http, LedgerAccounts ledger) =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var tenants = await Tenants(who, ops, OperatorRoles.Supervisor);
            if (tenants.Count == 0) return Problem(403, "The bank's ledger accounts are for supervisors");
            if (Q(http, "tenant") is { } t) { if (!tenants.Contains(t)) return Problem(403, $"No supervisor role for tenant {t}"); tenants = [t]; }
            var banks = new List<object>();
            foreach (var t2 in tenants) banks.Add(await ledger.For(t2, http.RequestAborted));
            return Results.Ok(new { banks });
        });

        // ------------------------------------------------------------------ the exchange rates' staleness (R-05-92)
        api.MapGet("/fx-rates", async (HttpContext http, FxRateStatus fx) =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var tenants = await Tenants(who, ops, OperatorRoles.Operator);
            if (Q(http, "tenant") is { } t) { if (!tenants.Contains(t)) return Problem(403, $"No role for tenant {t}"); tenants = [t]; }
            var banks = new List<System.Text.Json.Nodes.JsonObject>();
            foreach (var t2 in tenants) banks.Add(await fx.For(t2, http.RequestAborted));
            return Results.Ok(new { stale = banks.Any(b => (bool?)b["stale"] == true), banks });
        });
    }
}
