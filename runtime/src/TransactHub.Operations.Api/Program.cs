using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using TransactHub.Auth;
using TransactHub.Operations;
using TransactHub.Operations.Api;
using TransactHub.Store;

// The operations console's API (R-05-48 to R-05-50, R-06-24) and its web app (runtime/ops-console). It reads the operations
// read model, which a background projection keeps from the store's outbox, and records operator actions as facts on the
// payment's chain. Configuration:
//   Store:Provider, Store:Connection         the store (as the release API)
//   Operations:Connection                    the read model's database (default: the store's)
//   Operations:Views                         the folder of published artefacts holding the operations views
//   Operations:Console                       the built web app (runtime/ops-console/dist)
//   Auth:Oidc:Issuer, Auth:Oidc:Audience, Auth:Oidc:Authority, Auth:Oidc:SigningKeys, Auth:RoleMap   the bank's identity
//                                            provider and role map (TransactHub.Auth, roles Operator and Supervisor)
//   Auth:Oidc:ClientId, Auth:Oidc:Scopes      the console's public client (code flow with PKCE); TH_STUDIO_ISSUER refused
//   TH_AUTH_DEV=1                             dev sign-in, refused in Production
//   Operations:Registers                     a folder of further register kinds (*.register.json), beside the runtime's own
var builder = WebApplication.CreateBuilder(args);
var provider = Enum.Parse<StoreProvider>(builder.Configuration["Store:Provider"] ?? "SqlServer", ignoreCase: true);
var store = new StoreOptions(provider, builder.Configuration["Store:Connection"] ?? throw new InvalidOperationException("Set Store:Connection"));
var ops = new StoreOptions(provider, builder.Configuration["Operations:Connection"] ?? store.ConnectionString);
var viewsDir = builder.Configuration["Operations:Views"] ?? Find("content-configurations/artefacts") ?? "content-configurations/artefacts";
var views = ViewCatalog.FromFolder(viewsDir);
var clock = TimeProvider.System;
var auth = await builder.AddOperatorAuthentication();
builder.Services.AddSingleton(views);
builder.Services.AddSingleton(new Projector(store, ops, views, clock));
builder.Services.AddSingleton<IEngineSignals>(new OutboxSignals(store, clock));
// Resend (R-05-58): the file-transfer port the runtime sends a kept file on again, a folder the scheme's gateway collects from.
// With the File API (Operations:FileApi:Url), the resend goes through its outbox and is delivered on the bank's binding (R-09-84).
var resendFolder = builder.Configuration["Operations:FileTransfer:Folder"] ?? builder.Configuration["TH_FILE_TRANSFER_DIR"];
var resendPort = FileApiPort.FromConfiguration(k => builder.Configuration[k], "Operations") ?? (resendFolder is { Length: > 0 } ? new FolderFileTransfer(resendFolder) : null);
builder.Services.AddSingleton(sp => new OperatorActions(store, ops, views, sp.GetRequiredService<IEngineSignals>(), clock,
    resendPort is not null ? new FileResend(store, resendPort, clock) : null));
builder.Services.AddSingleton(sp => new HoldExpiry(store, sp.GetRequiredService<IEngineSignals>(), clock));
builder.Services.AddSingleton(new Reports(ops, views));
// The console's own chain (R-09-77, R-05-65): its exports, and the changes of the system it waits for a bank approver's consent to.
var consoleRecord = new ConsoleRecord(store, clock);
builder.Services.AddSingleton(consoleRecord);
// The bank's registers (R-05-62): the kinds the runtime ships and any in Operations:Registers, kept in the store.
builder.Services.AddSingleton(new TransactHub.Operations.Registers.RegisterService(store, TransactHub.Operations.Registers.RegisterCatalog.Load(builder.Configuration["Operations:Registers"]), clock));
// Work management, saved filters, the console's dials, unmasking, turnaround, retention and the fixed reports (R-05-66 to R-05-71).
//   Operations:Notify:Adapter, Operations:Notify:Url   where the console's alerts go (log by default; simulator; webhook)
//   Operations:TatSweepSeconds                          how often turnaround breaches are looked for and alerted (default 60)
//   Operations:Banks (or TH_BANKS_DIR)                  the bank folder whose core-banking binding the channel's enquiries use
//                                                       where no configuration release bound one (R-09-100)
//   Operations:Registers:SystemReadsMasked              true: the register API answers core banking masked (Issue I-149, I-181)
var dials = new OperationsDials(store, views);
var notifier = OpsNotifiers.From(k => builder.Configuration[k], m => Console.Error.WriteLine($"operations alert: {m}"));
builder.Services.AddSingleton(dials);
builder.Services.AddSingleton(notifier);
builder.Services.AddSingleton(new WorkQueue(store, ops, clock));
builder.Services.AddSingleton(new SavedFilters(store, clock));
builder.Services.AddSingleton(new FixedReports(ops, views));
builder.Services.AddSingleton(sp => new Erasure(store, ops, dials, sp.GetRequiredService<ConsoleRecord>(), clock));
builder.Services.AddSingleton(sp => new Unmasking(ops, views, sp.GetRequiredService<ConsoleRecord>(), sp.GetRequiredService<Projector>(), dials, sp.GetRequiredService<Erasure>(), clock));
builder.Services.AddSingleton(sp => new Turnaround(ops, dials, sp.GetRequiredService<ConsoleRecord>(), notifier, clock));
builder.Services.AddHostedService<ProjectionService>();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
// Security headers and the console page's content security policy on every answer (R-09-60).
app.UseRuntimeSecurityHeaders(app.Configuration);

if (app.Configuration["Store:Migrate"] != "false")
{
    await using (var db = StoreDbContext.Open(store)) await db.Database.MigrateAsync();
    await app.Services.GetRequiredService<Projector>().Migrate();
}

// The web app (runtime/ops-console/dist) is public, like any sign-in page: its files hold no data. Served before the
// authentication, and its routes fall back to index.html.
var consoleDir = app.Configuration["Operations:Console"] ?? Find("runtime/ops-console/dist");
var consoleFiles = consoleDir is not null && Directory.Exists(consoleDir) ? new PhysicalFileProvider(Path.GetFullPath(consoleDir)) : null;
if (consoleFiles is not null)
{
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = consoleFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = consoleFiles });
}
app.UseAuthentication();
app.UseAuthorization();

static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);

OperatorIdentity? Who(HttpContext http) => http.RequestServices.GetRequiredService<IOperatorIdentityResolver>().Resolve(http.User);

var api = app.MapGroup("/ops/api");

// How the console signs in: the bank's identity provider (authorisation code with PKCE), or dev mode.
api.MapGet("/auth/config", () => Results.Ok(auth)).AllowAnonymous();
api.MapGet("/health", async (Projector p) => Results.Ok(new { lag = await p.Lag(), views = views.All.Select(v => v.Key) })).AllowAnonymous();

api.MapGet("/me", (HttpContext http) => Who(http) is { } who
    ? Results.Ok(new { who.Subject, who.DisplayName, tenants = who.RolesByTenant.ToDictionary(kv => kv.Key, kv => kv.Value.Order()) })
    : Problem(401, "Sign in"));

// The views, as the console renders them: fields, groups, columns, filters, lines, documents and actions.
api.MapGet("/views", (HttpContext http) => Who(http) is null ? Problem(401, "Sign in") : Results.Ok(views.All.Select(v => v.Doc)));

// What the list can be filtered by, from what is in the read model for the caller's tenants.
api.MapGet("/facets", async (HttpContext http) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    await using var o = OpsDbContext.Open(ops);
    var tenants = await TenantsOf(who, o);
    var subj = o.Subjects.AsNoTracking().Where(x => tenants.Contains(x.Tenant));
    return Results.Ok(new
    {
        tenants,
        schemes = await subj.Select(x => x.Scheme).Distinct().OrderBy(x => x).ToListAsync(),
        directions = await subj.Select(x => x.Direction).Distinct().OrderBy(x => x).ToListAsync(),
        statuses = await subj.Where(x => x.Status != null).Select(x => x.Status!).Distinct().OrderBy(x => x).ToListAsync(),
        outcomes = await subj.Where(x => x.LastOutcome != null).Select(x => x.LastOutcome!).Distinct().OrderBy(x => x).ToListAsync(),
        holdReasons = await subj.Where(x => x.HoldReason != null).Select(x => x.HoldReason!).Distinct().OrderBy(x => x).ToListAsync(),
        legs = await o.Legs.AsNoTracking().Where(l => subj.Any(s => s.Id == l.SubjectId)).Select(l => l.Leg).Distinct().OrderBy(x => x).ToListAsync(),
        legOutcomes = await o.Legs.AsNoTracking().Where(l => subj.Any(s => s.Id == l.SubjectId) && l.LastOutcome != null).Select(l => l.LastOutcome!).Distinct().OrderBy(x => x).ToListAsync(),
    });
});

api.MapGet("/payments", async (HttpContext http) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    var q = http.Request.Query;
    string? S(string k) => q.TryGetValue(k, out var v) && v.ToString().Length > 0 ? v.ToString() : null;
    await using var o = OpsDbContext.Open(ops);
    var tenants = await TenantsOf(who, o);
    if (S("tenant") is { } t) { if (!who.Has(t, OperatorRoles.Operator)) return Problem(403, $"No role for tenant {t}"); tenants = [t]; }
    var x = o.Subjects.AsNoTracking().Where(s => tenants.Contains(s.Tenant));
    if (S("scheme") is { } scheme) x = x.Where(s => s.Scheme == scheme);
    if (S("kind") is { } kind) x = x.Where(s => s.Kind == kind);
    if (S("direction") is { } dir) x = x.Where(s => s.Direction == dir);
    if (S("status") is { } status) x = x.Where(s => s.Status == status);
    if (S("outcome") is { } outcome) x = x.Where(s => s.LastOutcome == outcome);
    if (S("hold") is "open") x = x.Where(s => s.Hold == "open");
    if (S("holdReason") is { } hr) x = x.Where(s => s.HoldReason != null && s.HoldReason.Contains(hr));
    if (S("from") is { } from && DateTimeOffset.TryParse(from, out var f)) x = x.Where(s => s.AcceptedAt >= f);
    if (S("to") is { } to && DateTimeOffset.TryParse(to, out var tt)) x = x.Where(s => s.AcceptedAt < (to.Length == 10 ? tt.AddDays(1) : tt));
    if (S("minAmount") is { } mn && decimal.TryParse(mn, System.Globalization.CultureInfo.InvariantCulture, out var min)) x = x.Where(s => s.Amount >= min);
    if (S("maxAmount") is { } mx && decimal.TryParse(mx, System.Globalization.CultureInfo.InvariantCulture, out var max)) x = x.Where(s => s.Amount <= max);
    if (S("leg") is { } leg) x = x.Where(s => o.Legs.Any(l => l.SubjectId == s.Id && l.Leg == leg));
    if (S("legOutcome") is { } lo) x = x.Where(s => o.Legs.Any(l => l.SubjectId == s.Id && l.LastOutcome == lo));
    if (S("q") is { } text)
        x = x.Where(s => s.Id.Contains(text) || (s.MessageId != null && s.MessageId.Contains(text)) || (s.EndToEndId != null && s.EndToEndId.Contains(text))
            || (s.FileName != null && s.FileName.Contains(text)) || o.Documents.Any(d => d.SubjectId == s.Id && d.FileName != null && d.FileName.Contains(text)));
    // A declared filter: f:<view>:<subject or leg>:<field> = value (exact or, with :min / :max, a range).
    foreach (var (key, value) in q)
    {
        if (!key.StartsWith("f:", StringComparison.Ordinal) || value.ToString().Length == 0) continue;
        var spec = key[2..];
        var v = value.ToString();
        if (spec.EndsWith(":min") && decimal.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var a))
        { var field = spec[..^4]; x = x.Where(s => o.FieldValues.Any(fv => fv.SubjectId == s.Id && fv.Field == field && fv.Number >= a)); }
        else if (spec.EndsWith(":max") && decimal.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var b))
        { var field = spec[..^4]; x = x.Where(s => o.FieldValues.Any(fv => fv.SubjectId == s.Id && fv.Field == field && fv.Number <= b)); }
        else if (spec.EndsWith(":contains"))
        { var field = spec[..^9]; x = x.Where(s => o.FieldValues.Any(fv => fv.SubjectId == s.Id && fv.Field == field && fv.Text != null && fv.Text.Contains(v))); }
        else x = x.Where(s => o.FieldValues.Any(fv => fv.SubjectId == s.Id && fv.Field == spec && fv.Text == v));
    }
    var total = await x.CountAsync();
    var page = Math.Max(1, int.TryParse(S("page"), out var p) ? p : 1);
    var size = Math.Clamp(int.TryParse(S("pageSize"), out var ps) ? ps : 25, 1, 200);
    var rows = await x.OrderByDescending(s => s.AcceptedAt).ThenBy(s => s.Id).Skip((page - 1) * size).Take(size).ToListAsync();
    var ids = rows.Select(r => r.Id).ToList();
    var legs = await o.Legs.AsNoTracking().Where(l => ids.Contains(l.SubjectId)).ToListAsync();
    return Results.Ok(new
    {
        total, page, pageSize = size,
        rows = rows.Select(r => Row(r, legs.Where(l => l.SubjectId == r.Id))),
    });
});

api.MapGet("/payments/{id}", async (string id, HttpContext http, OperatorActions actions) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    await using var o = OpsDbContext.Open(ops);
    var s = await o.Subjects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (s is null || !who.Has(s.Tenant, OperatorRoles.Operator)) return Problem(404, $"No payment or bulk {id} for your tenants");
    var legs = await o.Legs.AsNoTracking().Where(l => l.SubjectId == id).OrderBy(l => l.Instance).ToListAsync();
    var lines = await o.Lines.AsNoTracking().Where(l => l.BulkId == id).OrderBy(l => l.LineNo).Take(1000).ToListAsync();
    var timeline = await o.Timeline.AsNoTracking().Where(t => t.SubjectId == id).OrderBy(t => t.Seq).ToListAsync();
    var docs = await o.Documents.AsNoTracking().Where(d => d.SubjectId == id).OrderBy(d => d.At).ToListAsync();
    var holds = await o.Holds.AsNoTracking().Where(h => h.SubjectId == id).OrderBy(h => h.OpenedAt).ToListAsync();
    var acts = await o.Actions.AsNoTracking().Where(a => a.SubjectId == id).OrderBy(a => a.RequestedAt).ToListAsync();
    return Results.Ok(new
    {
        payment = Row(s, legs),
        lines = lines.Select(l => new { l.Id, l.LineNo, l.Amount, l.Currency, fields = JsonNode.Parse(l.FieldsJson) }),
        timeline = timeline.Select(t => new { t.Seq, t.At, t.Kind, t.Code, t.Instance, t.Step, t.Activity, t.Actor, data = JsonNode.Parse(t.DataJson), t.TraceId, trace = TraceLinks.Of(t.TraceId) }),
        documents = docs.Select(d => new { d.Id, d.Kind, d.Direction, d.FileName, d.Size, d.ContentHash, d.At, d.Source, d.Leg, signed = d.SigningKeyId is not null, d.SigningKeyId }),
        holds, actions = acts, offers = await actions.Offers(id),
    });
});

// Reports, the dashboard and the CSV export (R-05-59, Issue I-68): from the read model and the views only, for the tenants the
// caller holds the operator's role for; a tenant named must be one of them.
async Task<(List<string>? tenants, ReportFilter filter, IResult? refused)> ReportScope(HttpContext http, OpsDbContext o)
{
    if (Who(http) is not { } who) return (null, new(), Problem(401, "Sign in"));
    var q = http.Request.Query;
    string? S(string k) => q.TryGetValue(k, out var v) && v.ToString().Length > 0 ? v.ToString() : null;
    var tenants = await TenantsOf(who, o);
    if (S("tenant") is { } t) { if (!who.Has(t, OperatorRoles.Operator)) return (null, new(), Problem(403, $"No role for tenant {t}")); tenants = [t]; }
    DateTimeOffset? D(string k, bool end) => S(k) is { } x && DateTimeOffset.TryParse(x, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? (end && x.Length == 10 ? d.AddDays(1) : d) : null;
    return (tenants, new ReportFilter(S("scheme"), S("direction"), S("status"), D("from", false), D("to", true), S("kind")), null);
}

api.MapGet("/reports", async (HttpContext http, Reports reports) =>
{
    await using var o = OpsDbContext.Open(ops);
    var (tenants, _, refused) = await ReportScope(http, o);
    return refused ?? Results.Ok(new { dimensions = Reports.Dimensions, fields = reports.Fields() });
});
api.MapGet("/reports/summary", async (HttpContext http, Reports reports) =>
{
    await using var o = OpsDbContext.Open(ops);
    var (tenants, f, refused) = await ReportScope(http, o);
    if (refused is not null) return refused;
    try { return Results.Ok(await reports.Summary(tenants!, f, http.Request.Query["by"].ToString() is { Length: > 0 } by ? by : "scheme", http.RequestAborted)); }
    catch (ArgumentException e) { return Problem(400, e.Message); }
});
api.MapGet("/reports/dashboard", async (HttpContext http, Reports reports) =>
{
    await using var o = OpsDbContext.Open(ops);
    var (tenants, f, refused) = await ReportScope(http, o);
    return refused ?? Results.Ok(await reports.Dashboard(tenants!, f, http.RequestAborted));
});
api.MapGet("/reports/export.csv", async (HttpContext http, Reports reports, ILoggerFactory logs, ConsoleRecord record) =>
{
    await using var o = OpsDbContext.Open(ops);
    var (tenants, f, refused) = await ReportScope(http, o);
    if (refused is not null) return refused;
    var (csv, rows, cut) = await reports.Csv(tenants!, f, http.RequestAborted);
    // Who exported what is logged with ids only (R-09-35).
    logs.CreateLogger("TransactHub.Operations.Export").LogWarning("Export of {Rows} payment(s) for {Tenants} by {Subject}", rows, string.Join(",", tenants!), Who(http)!.Subject);
    // Every export is an audit fact on the console's chain: who, when, the filter and the row count (R-05-65, Issue I-144).
    var filter = new JsonObject();
    foreach (var (k, v) in http.Request.Query.OrderBy(q => q.Key, StringComparer.Ordinal)) filter[k] = v.ToString();
    var fact = await record.Exported(Who(http)!, "csv", tenants!, filter, rows, cut, http.RequestAborted);
    http.Response.Headers["x-th-export-fact"] = fact.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
    http.Response.Headers["x-th-rows"] = rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
    if (cut) http.Response.Headers["x-th-cut"] = "true";
    return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"payments-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
});

// A document as it was kept (the old Raw File tab): operators of the payment's tenant only.
api.MapGet("/payments/{id}/documents/{doc}", async (string id, string doc, HttpContext http, Erasure erasure) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    // An erased payment's kept files are not given out again (R-05-70, Issue I-180).
    if (await erasure.IsErased(id, http.RequestAborted)) return Problem(410, $"{id} is erased; its documents are not given out");
    await using var s = StoreDbContext.Open(store);
    await using var o = OpsDbContext.Open(ops);
    var m = await s.Messages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == doc);
    if (m is null)
    {
        // A stored file (Issue I-97): its bytes come from the blob table, checked against the hash its row signs.
        var f = await s.StoredFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == doc);
        if (f is null || !who.Has(f.Tenant, OperatorRoles.Operator)) return Problem(404, "No such document for your tenants");
        if (!await o.Documents.AnyAsync(d => d.Id == doc && d.SubjectId == id)) return Problem(404, "The document is not this payment's");
        var c = await s.FileContents.AsNoTracking().FirstOrDefaultAsync(x => x.ContentHash == f.ContentHash);
        if (c is null || FileStore.Sha256Hex(c.Content) != f.ContentHash) return Problem(409, "The file's bytes do not match the hash it was kept with");
        return Results.File(c.Content, "application/octet-stream", f.FileName);
    }
    if (!who.Has(m.Tenant, OperatorRoles.Operator)) return Problem(404, "No such document for your tenants");
    if (!await o.Documents.AnyAsync(d => d.Id == doc && d.SubjectId == id)) return Problem(404, "The document is not this payment's");
    return Results.File(m.Content, "application/octet-stream", m.FileName ?? $"{m.Kind}-{m.Id}");
});

api.MapPost("/payments/{id}/actions", async (string id, ActionRequest body, HttpContext http, OperatorActions actions, Projector p) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    try
    {
        var r = await actions.Request(who, id, body.Action, body.Remarks);
        await p.CatchUp();
        return Results.Ok(r);
    }
    catch (ActionRefused e) { return Problem(e.Status, e.Message); }
});

api.MapPost("/payments/{id}/actions/{action}/decision", async (string id, string action, DecisionRequest body, HttpContext http, OperatorActions actions, Projector p) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    try
    {
        var r = await actions.Decide(who, id, action, body.Approve, body.Remarks);
        await p.CatchUp();
        return Results.Ok(r);
    }
    catch (ActionRefused e) { return Problem(e.Status, e.Message); }
});

// The second approver's queue: actions of the caller's tenants that wait for them.
api.MapGet("/actions/pending", async (HttpContext http) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    await using var o = OpsDbContext.Open(ops);
    var tenants = await TenantsOf(who, o);
    var rows = await o.Actions.AsNoTracking().Where(a => tenants.Contains(a.Tenant) && a.Status == "pending-approval").OrderBy(a => a.RequestedAt).ToListAsync();
    return Results.Ok(rows.Where(a => a.SecondApprover is null || who.Has(a.Tenant, a.SecondApprover)));
});

// Throw the read model away and replay every fact: a supervisor asks (it changes nothing in the store, but it changes the
// system operators read), and it is carried out once a bank approver, another person, consents (R-09-77).
api.MapPost("/rebuild", async (Reasoned? body, HttpContext http, ConsoleRecord record) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    if (!who.Tenants.Any(t => who.Has(t, OperatorRoles.Supervisor))) return Problem(403, "Rebuilding needs a supervisor");
    try { return Results.Json(await record.Request(who, "rebuild-read-model", body?.Remarks), statusCode: 202); }
    catch (ActionRefused e) { return Problem(e.Status, e.Message); }
});
api.MapGet("/system-actions/pending", async (HttpContext http, ConsoleRecord record) =>
    Who(http) is not { } who ? Problem(401, "Sign in") : !who.MayConsent && !who.Tenants.Any(t => who.Has(t, OperatorRoles.Supervisor)) ? Problem(403, "No role for the console's system actions") : Results.Ok(await record.Pending()));
api.MapPost("/system-actions/{id}/decision", async (string id, DecisionRequest body, HttpContext http, ConsoleRecord record, Projector p, Unmasking unmask, Erasure erasure) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    try
    {
        return Results.Ok(await record.Decide(who, id, body.Approve, body.Remarks, async (name, details, requestedBy, ct) =>
        {
            switch (name)
            {
                case ConsoleRecord.Unmask: return await unmask.Grant(details, requestedBy, ct);
                case ConsoleRecord.Erase:
                {
                    var r = await erasure.Erase(details, requestedBy, who.Actor, id, ct);
                    await p.CatchUp(ct);
                    return r;
                }
                case ConsoleRecord.ReleaseLegalHold: return await erasure.Release(details, requestedBy, who.Actor, ct);
            }
            await p.Rebuild(ct);
            var lag = await p.Lag(ct);
            return new JsonObject { ["rebuilt"] = true, ["behind"] = lag.Behind, ["applied"] = lag.Applied };
        }));
    }
    catch (ActionRefused e) { return Problem(e.Status, e.Message); }
});
api.MapGet("/audit", async (HttpContext http, ConsoleRecord record) =>
    Who(http) is not { } who ? Problem(401, "Sign in") : !who.MayConsent && !who.Tenants.Any(t => who.Has(t, OperatorRoles.Supervisor)) ? Problem(403, "The console's audit is for supervisors and bank approvers")
    : Results.Ok(new
    {
        exports = await record.Recent(ConsoleRecord.ExportKind), consents = await record.Recent(ConsoleRecord.ConsentKind),
        unmasks = await record.Recent(ConsoleRecord.UnmaskKind), legalHolds = await record.Recent(ConsoleRecord.LegalHoldKind), turnaround = await record.Recent(ConsoleRecord.TatKind),
    }));

// The registers area and the register API (R-05-62 to R-05-64, R-09-73), and the channel enquiry APIs (R-09-74, R-09-100).
app.MapRegisters(app.Configuration["Operations:Registers:SystemReadsMasked"] == "true");
app.MapChannel(ops);
app.MapEnquiries(store, app.Configuration["Operations:Banks"] ?? app.Configuration["TH_BANKS_DIR"]);
// Work management, saved filters, unmasking, turnaround, retention and the fixed reports (R-05-66 to R-05-71).
app.MapWork(ops);

api.MapMethods("/{**rest}", ["PUT", "PATCH", "DELETE"], () => Problem(405, "The operations console changes a payment only through its declared actions")).AllowAnonymous();

if (consoleFiles is { } files)
{
    app.MapFallback(async ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/ops/api")) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(files.GetFileInfo("index.html"));
    }).AllowAnonymous();
}

app.Run();

static object Row(OpsSubject s, IEnumerable<OpsLeg> legs) => new
{
    s.Id, s.Kind, s.Tenant, s.Scheme, s.Direction, s.BulkId, s.MessageId, s.EndToEndId, s.FileName, s.Amount, s.Currency, s.Count,
    s.Status, s.LastOutcome, s.LastStep, s.Hold, s.HoldReason, s.AcceptedAt, s.UpdatedAt, s.View, s.ReleaseId,
    fields = JsonNode.Parse(s.FieldsJson),
    legs = legs.Select(l => new { l.Id, l.Instance, l.Leg, l.Title, l.Direction, l.LastStep, l.LastOutcome, l.UpdatedAt, fields = JsonNode.Parse(l.FieldsJson) }),
};

// The tenants an operator may see: those the role map names, or every tenant in the read model for "*".
static async Task<List<string>> TenantsOf(OperatorIdentity who, OpsDbContext o) =>
    (who.AllTenants ? await o.Subjects.AsNoTracking().Select(s => s.Tenant).Distinct().ToListAsync() : who.Tenants.ToList())
        .Where(t => who.Has(t, OperatorRoles.Operator)).ToList();

static string? Find(string rel)
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
    {
        var p = Path.Combine(d.FullName, rel);
        if (Directory.Exists(p)) return p;
    }
    return null;
}

public sealed record ActionRequest(string Action, string? Remarks);
public sealed record DecisionRequest(bool Approve, string? Remarks);
public sealed record Reasoned(string? Remarks);

/// <summary>Keeps the read model up with the outbox: a poll every Operations:PollMilliseconds (default 500).</summary>
sealed class ProjectionService(Projector p, HoldExpiry expiry, Turnaround tat, IConfiguration cfg, ILogger<ProjectionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var every = TimeSpan.FromMilliseconds(int.TryParse(cfg["Operations:PollMilliseconds"], out var ms) ? ms : 500);
        var sweepEvery = TimeSpan.FromSeconds(int.TryParse(cfg["Operations:HoldSweepSeconds"], out var hs) ? hs : 30);
        var tatEvery = TimeSpan.FromSeconds(int.TryParse(cfg["Operations:TatSweepSeconds"], out var ts) ? ts : 60);
        var swept = DateTimeOffset.MinValue;
        // The first turnaround sweep comes one interval after the start, once the projection has caught up.
        var tatSwept = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await p.CatchUp(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { log.LogWarning("projection: {Error}", e.Message); }
            // A hold whose expiry has passed is expired, and its decision goes to the engine (R-05-56).
            if (DateTimeOffset.UtcNow - swept > sweepEvery)
            {
                swept = DateTimeOffset.UtcNow;
                try { await expiry.Sweep(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { log.LogWarning("hold expiry: {Error}", e.Message); }
            }
            // A payment past its turnaround limit is alerted once, a fact and the notification port (R-05-68).
            if (DateTimeOffset.UtcNow - tatSwept > tatEvery)
            {
                tatSwept = DateTimeOffset.UtcNow;
                try { await tat.Sweep(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { log.LogWarning("turnaround: {Error}", e.Message); }
            }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

public partial class Program;
