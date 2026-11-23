using System.Text.Json.Nodes;
using TransactHub.Store;

namespace TransactHub.Service.Api;

public sealed record Reasoned(string? Reason);
public sealed record TagAsk(string Tag, string? Reason);
public sealed record ActionBody(string Action, string? Target, string? Reason, JsonObject? Parameters);
public sealed record Decision(bool Approve, string? Reason);

/// <summary>The routes under <c>/service/api</c>. Every one needs a verified caller with a service role (R-06-25).</summary>
public static class ServiceEndpoints
{
    static IResult Refusal(ServiceRefused e) => Results.Json(new { error = e.Message, code = e.Code, runbook = ErrorEntry.RunbookLink(e.Code) }, statusCode: e.Status);

    static ServiceIdentity Who(HttpContext http, string role = ServiceRoles.Engineer)
    {
        var who = ServiceIdentity.From(http.User) ?? throw new ServiceRefused(401, ErrorCodes.Unauthenticated, "Sign in");
        if (!who.Has(role)) throw new ServiceRefused(403, ErrorCodes.Forbidden, $"The service console needs the {role} role from the bank's role map");
        return who;
    }

    /// <summary>Runs a handler, turning a refusal into its status, its code and its runbook link.</summary>
    static async Task<IResult> Guard(Func<Task<IResult>> f)
    {
        try { return await f(); }
        catch (ServiceRefused e) { return Refusal(e); }
    }

    static string Need(string? reason, string what) => string.IsNullOrWhiteSpace(reason) ? throw new ServiceRefused(400, ErrorCodes.RemarksRequired, $"{what} needs a reason") : reason;

    public static void Map(WebApplication app, ServiceAuth.ClientConfig auth)
    {
        var api = app.MapGroup("/service/api");
        api.MapGet("/auth/config", () => Results.Ok(auth)).AllowAnonymous();

        api.MapGet("/me", (Delegate)((HttpContext http) => Guard(() =>
        {
            var who = ServiceIdentity.From(http.User) ?? throw new ServiceRefused(401, ErrorCodes.Unauthenticated, "Sign in");
            return Task.FromResult(Results.Ok(new { who.Subject, who.DisplayName, roles = who.Roles.Order(), tenants = who.Tenants.Order(), service = who.Has(ServiceRoles.Engineer) }));
        })));

        // Health (R-09-39): the latest of each probe, its short history, and the dashboards.
        api.MapGet("/health", (HttpContext http, HealthMonitor m, IConfiguration cfg, ErrorDictionary d) => Guard(async () =>
        {
            Who(http);
            if (m.SampledAt is null || http.Request.Query["refresh"] == "1") await m.Sample(http.RequestAborted);
            var latest = m.Latest();
            return Results.Ok(new
            {
                sampledAt = m.SampledAt,
                status = latest.Any(r => r.Status == "fail") ? "fail" : latest.Any(r => r.Status == "warn") ? "warn" : "ok",
                components = latest.Select(r => new { r.Name, r.Group, r.Status, r.Detail, r.At, r.Ms, r.Code, r.Version, r.Dashboard, runbook = r.Code is null ? null : d.Find(r.Code)?.Link, history = m.History(r.Name).Select(h => new { h.At, h.Status, h.Ms }) }),
                dashboards = cfg.GetSection("Service:Dashboards").GetChildren().ToDictionary(c => c.Key, c => c.Value),
            });
        }));

        // Content (R-09-40).
        api.MapGet("/content/versions", (HttpContext http, ContentService c) => Guard(async () => { Who(http); return Results.Ok(await c.Versions(http.RequestAborted)); }));
        api.MapGet("/content/releases", (HttpContext http, ContentService c) => Guard(async () => { Who(http); return Results.Ok(await c.Releases(http.RequestAborted)); }));
        api.MapGet("/content/open-payments", (HttpContext http, ContentService c) => Guard(async () => { Who(http); return Results.Ok(await c.OpenPayments(http.RequestAborted)); }));
        // The pins with no payment, each with the action that ends it (Issue I-206, R-09-165).
        api.MapGet("/content/stuck-pins", (string? olderThan, HttpContext http, ContentService c) => Guard(async () => { Who(http); return Results.Ok(await c.StuckPins(DistributionRegistry.Duration(olderThan), http.RequestAborted)); }));
        api.MapGet("/content/diff", (string tag, string? environment, HttpContext http, ContentService c) => Guard(async () => { Who(http); return Results.Ok(await c.Diff(tag, environment, http.RequestAborted)); }));
        api.MapPost("/content/verify", (TagAsk b, HttpContext http, ContentService c) => Guard(async () => Results.Ok(await c.Verify(b.Tag, Who(http), b.Reason, http.RequestAborted))));
        // Installing changes the system: verified now, carried out once a bank approver consents (R-09-77).
        api.MapPost("/content/install", (TagAsk b, HttpContext http, ActionBook book) => Guard(async () =>
        {
            var r = await book.Request(Who(http), new ActionAsk("install", b.Tag, Need(b.Reason, "Installing"), null), http.RequestAborted);
            return Results.Json(r, statusCode: (string?)r["status"] == "pending-approval" ? 202 : 200);
        }));

        // Backup and restore (R-09-41).
        api.MapPost("/backup", (Reasoned b, HttpContext http, BackupService s) => Guard(async () =>
            Results.File(await s.Export(Who(http), Need(b.Reason, "A backup"), http.RequestAborted), "application/zip", $"transacthub-backup-{DateTime.UtcNow:yyyyMMddTHHmmss}.zip")));
        api.MapPost("/backup/verify", (HttpContext http, BackupService s) => Guard(async () =>
        {
            Who(http);
            var (doc, v) = await s.Verify(await Body(http), http.RequestAborted);
            return Results.Ok(new { verified = true, signedBy = v.KeyId, signedAt = v.SignedAt, releases = (doc["releases"] as JsonArray)?.Count, activations = (doc["activations"] as JsonArray)?.Count, values = (doc["values"] as JsonArray)?.Count, bindings = (doc["bindings"] as JsonArray)?.Count });
        }));
        // A dry run changes nothing and answers at once. A restore is planned now (a backup that does not verify, or one that may
        // not go into this runtime, is refused at once), kept by its hash, and carried out once a bank approver consents (R-09-77, I-122).
        api.MapPost("/restore", (bool? dryRun, string? reason, HttpContext http, BackupService s, ActionBook book) => Guard(async () =>
        {
            var who = Who(http);
            var why = Need(reason, "A restore");
            var bytes = await Body(http);
            var plan = await s.Restore(bytes, dryRun: true, who, why, http.RequestAborted);
            if (dryRun != false) return Results.Ok(plan);
            if (plan["problems"] is JsonArray { Count: > 0 } problems) throw new ServiceRefused(409, ErrorCodes.RestoreRefused, string.Join("; ", problems.Select(p => (string?)p)));
            var hash = await s.Keep(bytes, http.RequestAborted);
            var r = await book.Request(who, new ActionAsk("restore", hash, why, new JsonObject { ["backup"] = (string?)plan["backup"] }), http.RequestAborted);
            r["plan"] = plan.DeepClone();
            return Results.Json(r, statusCode: 202);
        }));

        // Error and event logs (R-09-42), a payment's trace, and the console's own audit.
        api.MapGet("/logs", (HttpContext http, EventLog l) => Guard(async () =>
        {
            var who = Who(http);
            var q = http.Request.Query;
            string? S(string k) => q.TryGetValue(k, out var v) && v.ToString().Length > 0 ? v.ToString() : null;
            DateTimeOffset? T(string k) => S(k) is { } x && DateTimeOffset.TryParse(x, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            if (S("tenant") is { } tenant && !who.Sees(tenant)) throw new ServiceRefused(403, ErrorCodes.Forbidden, $"No tenant {tenant} for {who.DisplayName}");
            return Results.Ok(await l.Query(who, new LogFilter(S("code"), S("leg"), S("scheme"), S("tenant"), S("payment"), T("from"), T("to"), S("source"), int.TryParse(S("take"), out var n) ? n : 200, S("trace")), http.RequestAborted));
        }));
        api.MapGet("/payments/{id}/trace", (string id, HttpContext http, EventLog l) => Guard(async () =>
            await l.Trace(Who(http), id, http.RequestAborted) is { } t ? Results.Ok(t) : throw new ServiceRefused(404, ErrorCodes.ActionNotOffered, $"No payment {id} for your tenants")));
        // The audit page (R-05-76): newest first, filtered by person, action and date, each row with its hash; and whether the chains verify.
        api.MapGet("/audit", (HttpContext http, ServiceAudit a) => Guard(async () =>
        {
            Who(http);
            var f = AuditFilter.Of(k => http.Request.Query[k].ToString());
            return Results.Ok(f == new AuditFilter() ? await a.Recent(200, http.RequestAborted) : await a.Recent(f, ct: http.RequestAborted));
        }));
        api.MapGet("/audit/chain", (HttpContext http, ServiceAudit a) => Guard(async () => { Who(http); return Results.Ok(await a.Verify(http.RequestAborted)); }));

        // The error dictionary and its runbooks (R-05-52).
        api.MapGet("/errors", (string? q, HttpContext http, ErrorDictionary d) => Guard(() =>
        {
            Who(http);
            var all = d.All.Where(e => q is null || e.Code.Contains(q, StringComparison.OrdinalIgnoreCase) || (e.Title ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(Results.Ok(all.Select(e => new { e.Code, e.Scope, e.Title, e.Category, e.Retryable, iso = (string?)e.Doc["isoReasonCode"], raisedBy = e.Doc["raisedBy"], link = e.Link })));
        }));
        api.MapGet("/errors/{code}", (string code, string? scheme, HttpContext http, ErrorDictionary d) => Guard(() =>
        {
            Who(http);
            var e = d.Find(code, scheme) ?? throw new ServiceRefused(404, ErrorCodes.ActionNotOffered, $"No dictionary entry for {code}");
            var doc = (JsonObject)e.Doc.DeepClone();
            doc["scope"] = e.Scope;
            doc["dictionary"] = $"{e.Dictionary}@{e.Release}";
            doc["actionsOffered"] = new JsonArray(e.Actions.Select(a => ActionBook.Catalogue.FirstOrDefault(x => x.Id == a)).OfType<ActionSpec>()
                .Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["title"] = a.Title, ["risky"] = a.Risky, ["path"] = a.Path, ["missing"] = a.Missing }).ToArray());
            return Task.FromResult(Results.Ok(doc));
        }));

        // Troubleshooting actions (R-09-43).
        api.MapGet("/actions", (Delegate)((HttpContext http) => Guard(() => { Who(http); return Task.FromResult(Results.Ok(ActionBook.Catalogue)); })));
        api.MapGet("/actions/pending", (HttpContext http, ActionBook b) => Guard(async () => { Who(http); return Results.Ok(await b.Pending(http.RequestAborted)); }));
        api.MapPost("/actions", (ActionBody body, HttpContext http, ActionBook b) => Guard(async () =>
        {
            var r = await b.Request(Who(http), new ActionAsk(body.Action, body.Target, body.Reason, body.Parameters), http.RequestAborted);
            return Results.Json(r, statusCode: (string?)r["status"] == "pending-approval" ? 202 : 200);
        }));
        api.MapPost("/actions/{id}/decision", (string id, Decision body, HttpContext http, ActionBook b) => Guard(async () =>
            Results.Ok(await b.Decide(Who(http, ServiceRoles.BankApprover), id, body.Approve, body.Reason, http.RequestAborted))));

        // The assistant (R-09-44).
        api.MapGet("/assistant", (HttpContext http, AssistantService a) => Guard(() => { Who(http); return Task.FromResult(Results.Ok(new { enabled = a.Enabled, model = AssistantService.ModelId })); }));
        api.MapPost("/assistant", (AssistantAsk body, HttpContext http, AssistantService a) => Guard(async () => Results.Ok(await a.Ask(Who(http), body, http.RequestAborted))));

        api.MapMethods("/{**rest}", ["PUT", "PATCH", "DELETE"], () => Results.Json(new { error = "The service console changes the runtime only through its declared actions", code = ErrorCodes.ActionNotOffered }, statusCode: 405)).AllowAnonymous();
    }

    static async Task<byte[]> Body(HttpContext http)
    {
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
        return ms.ToArray();
    }
}
