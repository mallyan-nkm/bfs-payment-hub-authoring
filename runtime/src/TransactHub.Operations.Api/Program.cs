using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
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
builder.Services.AddSingleton(sp => new OperatorActions(store, ops, views, sp.GetRequiredService<IEngineSignals>(), clock));
builder.Services.AddHostedService<ProjectionService>();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

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
        timeline = timeline.Select(t => new { t.Seq, t.At, t.Kind, t.Code, t.Instance, t.Step, t.Activity, t.Actor, data = JsonNode.Parse(t.DataJson) }),
        documents = docs.Select(d => new { d.Id, d.Kind, d.Direction, d.FileName, d.Size, d.ContentHash, d.At }),
        holds, actions = acts, offers = await actions.Offers(id),
    });
});

// A document as it was kept (the old Raw File tab): operators of the payment's tenant only.
api.MapGet("/payments/{id}/documents/{doc}", async (string id, string doc, HttpContext http) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    await using var s = StoreDbContext.Open(store);
    var m = await s.Messages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == doc);
    if (m is null || !who.Has(m.Tenant, OperatorRoles.Operator)) return Problem(404, "No such document for your tenants");
    await using var o = OpsDbContext.Open(ops);
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

// Throw the read model away and replay every fact: a supervisor of any tenant (it changes nothing in the store).
api.MapPost("/rebuild", async (HttpContext http, Projector p) =>
{
    if (Who(http) is not { } who) return Problem(401, "Sign in");
    if (!who.Tenants.Any(t => who.Has(t, OperatorRoles.Supervisor))) return Problem(403, "Rebuilding needs a supervisor");
    await p.Rebuild();
    return Results.Ok(new { lag = await p.Lag() });
});

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

/// <summary>Keeps the read model up with the outbox: a poll every Operations:PollMilliseconds (default 500).</summary>
sealed class ProjectionService(Projector p, IConfiguration cfg, ILogger<ProjectionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var every = TimeSpan.FromMilliseconds(int.TryParse(cfg["Operations:PollMilliseconds"], out var ms) ? ms : 500);
        while (!ct.IsCancellationRequested)
        {
            try { await p.CatchUp(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { log.LogWarning("projection: {Error}", e.Message); }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

public partial class Program;
