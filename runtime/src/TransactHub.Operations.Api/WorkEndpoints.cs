using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Operations;
using TransactHub.Store;

namespace TransactHub.Operations.Api;

/// <summary>
/// The operations console's work management, unmasking, turnaround, retention and fixed reports (R-05-66 to R-05-71), under
/// <c>/ops/api</c>. Each answers only for the tenants the caller holds a role for; every change is a fact, and a change of the
/// system (an unmask, an erasure, a legal hold's release) waits for a bank approver's consent through <c>/ops/api/system-actions</c>.
/// </summary>
public static class WorkEndpoints
{
    public sealed record WorkChange(string? Op, string? Kind, string? Id, string? Assignee, string? AssigneeName, string? Remarks);
    public sealed record BulkWork(string? Op, List<WorkRef>? Items, string? Assignee, string? AssigneeName, string? Remarks);
    public sealed record WorkRef(string? Kind, string? Id);
    public sealed record BulkActions(List<string>? Ids, string? Action, string? Remarks);
    public sealed record BulkDecisions(List<BulkDecisionItem>? Items, bool Approve, string? Remarks);
    public sealed record BulkDecisionItem(string? Payment, string? Action);
    public sealed record FilterBody(string? Name, JsonObject? Query);
    public sealed record UnmaskBody(string? Subject, string? Field, int? Line, string? Remarks);
    public sealed record SubjectBody(string? Subject, string? Remarks);

    static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);
    static OperatorIdentity? Who(HttpContext http) => http.RequestServices.GetRequiredService<IOperatorIdentityResolver>().Resolve(http.User);
    static string? Q(HttpContext http, string k) => http.Request.Query.TryGetValue(k, out var v) && v.ToString().Length > 0 ? v.ToString() : null;

    static async Task<IResult> Guard(Func<Task<IResult>> f)
    {
        try { return await f(); }
        catch (ActionRefused e) { return Results.Json(new { error = e.Message, code = e.Code }, statusCode: e.Status); }
    }

    /// <summary>The tenants the caller holds the operator's role for (every tenant of the read model for "*"), or the one named.</summary>
    static async Task<(List<string>? tenants, IResult? refused)> Tenants(HttpContext http, OperatorIdentity who, StoreOptions ops)
    {
        await using var o = OpsDbContext.Open(ops);
        var all = (who.AllTenants ? await o.Subjects.AsNoTracking().Select(s => s.Tenant).Distinct().ToListAsync() : who.Tenants.ToList()).Where(t => who.Has(t, OperatorRoles.Operator)).ToList();
        if (Q(http, "tenant") is { } t) return who.Has(t, OperatorRoles.Operator) ? ([t], null) : (null, Problem(403, $"No role for tenant {t}"));
        return (all, null);
    }

    static WorkItem Item(string? kind, string? id) =>
        kind is null || id is null || !WorkItem.Kinds.Contains(kind) ? throw new ActionRefused(400, "Name the item: kind (payment, action or alert) and id", OpsCodes.NotFound) : new WorkItem(kind, id);

    public static void MapWork(this WebApplication app, StoreOptions ops)
    {
        var api = app.MapGroup("/ops/api");

        // ------------------------------------------------------------------ work management (R-05-71)
        api.MapGet("/work/queue", (HttpContext http, WorkQueue work) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var (tenants, refused) = await Tenants(http, who, ops);
            if (refused is not null) return refused;
            var answer = await work.Queue(who, tenants!, Q(http, "filter") ?? "all", http.RequestAborted);
            answer["cursor"] = await work.Cursor(http.RequestAborted);
            return Results.Ok(answer);
        }));
        // The live queue: a long poll that answers when anything was written to the store after the cursor.
        api.MapGet("/work/changes", (HttpContext http, WorkQueue work) => Guard(async () =>
        {
            if (Who(http) is null) return Problem(401, "Sign in");
            var after = long.TryParse(Q(http, "after"), out var a) ? a : 0;
            var wait = TimeSpan.FromSeconds(Math.Clamp(int.TryParse(Q(http, "wait"), out var w) ? w : 20, 0, 30));
            var (cursor, changed) = await work.Changes(after, wait, http.RequestAborted);
            return Results.Ok(new { cursor, changed });
        }));
        api.MapGet("/work/{kind}/{id}", (string kind, string id, HttpContext http, WorkQueue work) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var item = Item(kind, id);
            var tenant = await work.TenantOf(item, http.RequestAborted);
            if (!who.Has(tenant, OperatorRoles.Operator)) return Problem(404, $"No {kind} {id} for your tenants");
            var st = await work.State(item, http.RequestAborted);
            return Results.Ok(new { item = id, itemKind = kind, tenant, st.Assignee, st.AssigneeName, st.Escalated, st.Level, history = await work.History(item, http.RequestAborted) });
        }));
        api.MapPost("/work", (WorkChange body, HttpContext http, WorkQueue work) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            return Results.Ok(await work.Change(who, Item(body.Kind, body.Id), body.Op ?? "", body.Assignee, body.AssigneeName, body.Remarks, http.RequestAborted));
        }));
        api.MapPost("/work/bulk", (BulkWork body, HttpContext http, WorkQueue work) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var items = (body.Items ?? []).Select(i => Item(i.Kind, i.Id)).ToList();
            return Results.Ok(await work.Bulk(who, items, body.Op ?? "", body.Assignee, body.AssigneeName, body.Remarks, http.RequestAborted));
        }));

        // Bulk operator actions on a selection (R-05-71): each payment's action as POST /payments/{id}/actions would ask it, each its
        // own fact on its own chain, a second approver's still needed per payment; a refusal stays with its payment.
        api.MapPost("/payments/bulk-actions", (BulkActions body, HttpContext http, OperatorActions actions, Projector p) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var ids = (body.Ids ?? []).Distinct().ToList();
            if (ids.Count == 0 || ids.Count > 500) return Problem(400, "Select between 1 and 500 payments");
            if (string.IsNullOrWhiteSpace(body.Action)) return Problem(400, "Name the action");
            var results = new JsonArray();
            foreach (var id in ids)
            {
                try { results.Add(new JsonObject { ["payment"] = id, ["ok"] = true, ["result"] = await actions.Request(who, id, body.Action, body.Remarks, http.RequestAborted) }); }
                catch (ActionRefused e) { results.Add(new JsonObject { ["payment"] = id, ["ok"] = false, ["status"] = e.Status, ["error"] = e.Message, ["code"] = e.Code }); }
            }
            await p.CatchUp(http.RequestAborted);
            var done = results.Count(r => r!["ok"]!.GetValue<bool>());
            return Results.Ok(new { action = body.Action, done, refused = results.Count - done, results });
        }));
        api.MapPost("/actions/bulk-decision", (BulkDecisions body, HttpContext http, OperatorActions actions, Projector p) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var items = body.Items ?? [];
            if (items.Count == 0 || items.Count > 500) return Problem(400, "Select between 1 and 500 actions");
            var results = new JsonArray();
            foreach (var i in items)
            {
                try { results.Add(new JsonObject { ["payment"] = i.Payment, ["action"] = i.Action, ["ok"] = true, ["result"] = await actions.Decide(who, i.Payment ?? "", i.Action ?? "", body.Approve, body.Remarks, http.RequestAborted) }); }
                catch (ActionRefused e) { results.Add(new JsonObject { ["payment"] = i.Payment, ["action"] = i.Action, ["ok"] = false, ["status"] = e.Status, ["error"] = e.Message, ["code"] = e.Code }); }
            }
            await p.CatchUp(http.RequestAborted);
            var done = results.Count(r => r!["ok"]!.GetValue<bool>());
            return Results.Ok(new { done, refused = results.Count - done, results });
        }));

        // ------------------------------------------------------------------ saved filters (R-05-71)
        api.MapGet("/filters", (HttpContext http, SavedFilters filters) => Guard(async () =>
            Who(http) is not { } who ? Problem(401, "Sign in") : Results.Ok(await filters.List(who, http.RequestAborted))));
        api.MapPost("/filters", (FilterBody body, HttpContext http, SavedFilters filters) => Guard(async () =>
            Who(http) is not { } who ? Problem(401, "Sign in") : Results.Json(await filters.Save(who, body.Name, body.Query, http.RequestAborted), statusCode: 201)));
        api.MapPost("/filters/{id}/remove", (string id, HttpContext http, SavedFilters filters) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            await filters.Remove(who, id, http.RequestAborted);
            return Results.Ok(new { removed = id });
        }));

        // ------------------------------------------------------------------ unmasking (R-05-69)
        api.MapPost("/unmask", (UnmaskBody body, HttpContext http, Unmasking unmask) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            if (body.Subject is null || body.Field is null) return Problem(400, "Name the payment and the field");
            return Results.Json(await unmask.Ask(who, body.Subject, body.Field, body.Line, body.Remarks, http.RequestAborted), statusCode: 202);
        }));
        api.MapGet("/unmask/{id}", (string id, HttpContext http, Unmasking unmask) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await unmask.View(who, id, http.RequestAborted));
        }));

        // ------------------------------------------------------------------ turnaround (R-05-68)
        api.MapGet("/tat/breaches", (HttpContext http, Turnaround tat) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var (tenants, refused) = await Tenants(http, who, ops);
            if (refused is not null) return refused;
            var rows = await tat.Breaches(tenants!, Q(http, "scheme"), Q(http, "open") is "true", http.RequestAborted);
            return Results.Ok(new { total = rows.Count, rows });
        }));

        // ------------------------------------------------------------------ retention, legal holds, erasure (R-05-70)
        api.MapGet("/retention/due", (HttpContext http, Erasure erasure) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var (tenants, refused) = await Tenants(http, who, ops);
            if (refused is not null) return refused;
            if (!tenants!.Any(t => who.Has(t, OperatorRoles.Supervisor) || who.Has(t, OperatorRoles.BankApprover))) return Problem(403, "Retention is for supervisors and bank approvers");
            return Results.Ok(await erasure.Due(tenants!, http.RequestAborted));
        }));
        api.MapPost("/erasures", (SubjectBody body, HttpContext http, Erasure erasure) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            if (body.Subject is null) return Problem(400, "Name the payment");
            return Results.Json(await erasure.AskErase(who, body.Subject, body.Remarks, http.RequestAborted), statusCode: 202);
        }));
        api.MapGet("/legal-holds", (HttpContext http, Erasure erasure) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var holds = (await erasure.Holds(Q(http, "subject"), http.RequestAborted)).Where(h => who.Has(h.Tenant, OperatorRoles.Operator) || who.Has(h.Tenant, OperatorRoles.BankApprover));
            return Results.Ok(holds);
        }));
        api.MapPost("/legal-holds", (SubjectBody body, HttpContext http, Erasure erasure) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            if (body.Subject is null) return Problem(400, "Name the payment");
            return Results.Json(await erasure.PlaceHold(who, body.Subject, body.Remarks, http.RequestAborted), statusCode: 201);
        }));
        api.MapPost("/legal-holds/{id}/release", (string id, Reasoned? body, HttpContext http, Erasure erasure) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            return Results.Json(await erasure.AskRelease(who, id, body?.Remarks, http.RequestAborted), statusCode: 202);
        }));

        // ------------------------------------------------------------------ fixed reports (R-05-67, R-05-65)
        api.MapGet("/reports/fixed", (HttpContext http, FixedReports reports) => Who(http) is null ? Problem(401, "Sign in") : Results.Ok(reports.List()));
        api.MapGet("/reports/fixed/{view}/{id}.csv", (string view, string id, HttpContext http, FixedReports reports, ConsoleRecord record) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var (tenants, refused) = await Tenants(http, who, ops);
            if (refused is not null) return refused;
            if (reports.Find(view, id) is not { } found) return Problem(404, $"No fixed report {view}/{id}");
            DateTimeOffset? D(string k, bool end) => Q(http, k) is { } x && DateTimeOffset.TryParse(x, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? (end && x.Length == 10 ? d.AddDays(1) : d) : null;
            var f = new ReportFilter(null, Q(http, "direction"), Q(http, "status"), D("from", false), D("to", true), Q(http, "kind"));
            var (csv, rows, cut) = await reports.Csv(found.view, found.report, tenants!, f, http.RequestAborted);
            var filter = new JsonObject { ["report"] = $"{view}/{id}" };
            foreach (var (k, v) in http.Request.Query.OrderBy(q => q.Key, StringComparer.Ordinal)) filter[k] = v.ToString();
            var fact = await record.Exported(who, "csv", tenants!, filter, rows, cut, http.RequestAborted);
            http.Response.Headers["x-th-export-fact"] = fact.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.Headers["x-th-rows"] = rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (cut) http.Response.Headers["x-th-cut"] = "true";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"{id}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
        }));
    }
}
