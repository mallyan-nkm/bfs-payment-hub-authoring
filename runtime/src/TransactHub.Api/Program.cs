using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Api;
using TransactHub.Store;
using TransactHub.Telemetry;
using YamlDotNet.Serialization;

// The runtime's release API (plan W4). A package from the studio is stored once and never changed;
// which release new payments take in an environment is an activation, also never changed. There is
// no update and no delete: PUT, PATCH and DELETE are refused. The store is SQL Server or PostgreSQL,
// chosen by configuration (Store:Provider, Store:Connection).

var builder = WebApplication.CreateBuilder(args);
var provider = Enum.Parse<StoreProvider>(builder.Configuration["Store:Provider"] ?? "SqlServer", ignoreCase: true);
var connection = builder.Configuration["Store:Connection"] ?? throw new InvalidOperationException("Set Store:Connection");
var options = new StoreOptions(provider, connection);
// Environments where a package that carries simulators may be activated (R-07-12: never a promotable one).
var testEnvironments = (builder.Configuration["Releases:TestEnvironments"] ?? "test,dev,kit").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
builder.Services.AddScoped(_ => StoreDbContext.Open(options));
builder.Services.AddSingleton(TimeProvider.System);
// Traces, metrics and logs over OTLP when OTEL_* names an endpoint; /health/live and /health/ready (R-13-01, R-13-07).
builder.Services.AddTransactHubTelemetry("transacthub-api");
builder.Services.AddHealthChecks().AddCheck<StoreReady>("store", tags: [WebTelemetry.Ready]);
var app = builder.Build();
app.MapTransactHubHealth();

if (app.Configuration["Store:Migrate"] != "false")
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<StoreDbContext>().Database.MigrateAsync();
}

static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);

async Task<IResult> StorePackage(StoreDbContext db, TimeProvider clock, Package p, string actor)
{
    var reg = new ReleaseRegistry(db, clock);
    try
    {
        var r = await reg.Store(p.Scope, p.Release, p.CanonicalZip(), p.Manifest, actor);
        if (r.Stored) RuntimeTelemetry.ReleasesStored.Add(1, new KeyValuePair<string, object?>("th.scope", p.Scope));
        var body = new { id = r.Id, scope = p.Scope, release = p.Release, stored = r.Stored, promotable = p.Promotable, hash = ReleaseRegistry.HashOf(p.CanonicalZip()) };
        return r.Stored ? Results.Json(body, statusCode: 201) : Results.Ok(body);
    }
    catch (ReleaseConflict e) { return Problem(409, e.Message); }
}

app.MapPost("/releases", async (HttpRequest req, StoreDbContext db, TimeProvider clock) =>
{
    var actor = req.Query["actor"].ToString();
    if (actor.Length == 0) return Problem(400, "Name the actor: ?actor=…");
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    try { return await StorePackage(db, clock, Package.FromZip(ms.ToArray()), actor); }
    catch (PackageRejected e) { return Problem(422, e.Message); }
});

app.MapGet("/releases", async (string scope, string release, StoreDbContext db) =>
{
    var r = await new ReleaseRegistry(db).Get(scope, release);
    return r is null ? Problem(404, $"No release {scope}@{release}") : Results.Ok(new { r.Id, r.Scope, r.Release, r.ContentHash, manifest = JsonNode.Parse(r.ManifestJson), r.StoredBy, r.StoredAt });
});

// The package as stored, byte for byte: what a deployment runs (plan P6).
app.MapGet("/releases/package", async (string scope, string release, StoreDbContext db) =>
{
    var r = await new ReleaseRegistry(db).Get(scope, release);
    return r is null ? Problem(404, $"No release {scope}@{release}") : Results.File(r.Package, "application/zip", $"{release}.zip");
});

// A bank's configuration values in force at a moment (plan P6): what the workers look up at each attempt, so a value the studio
// publishes with a later validFrom reaches the payments of that date with no deployment.
app.MapGet("/values", async (string tenant, DateTimeOffset? at, StoreDbContext db, TimeProvider clock) =>
    Results.Ok(await BankValues.InForce(db, tenant, at ?? clock.GetUtcNow())));

app.MapPost("/activations", async (ActivationRequest a, StoreDbContext db, TimeProvider clock) =>
{
    if (string.IsNullOrWhiteSpace(a.Actor)) return Problem(400, "Name the actor");
    var r = await new ReleaseRegistry(db, clock).Get(a.Scope, a.Release);
    if (r is null) return Problem(404, $"{a.Scope}@{a.Release} is not stored; store it before activating it");
    var manifest = JsonNode.Parse(r.ManifestJson);
    var promotable = manifest?["promotable"]?.GetValue<bool>() == true && manifest?["containsSimulators"]?.GetValue<bool>() != true;
    if (!promotable && !testEnvironments.Contains(a.Environment))
        return Problem(409, $"{a.Scope}@{a.Release} is not promotable (it carries simulators or has errors): it may be activated only in {string.Join(", ", testEnvironments)}");
    await new ReleaseRegistry(db, clock).Activate(a.Environment, a.Scope, a.Release, a.EffectiveFrom ?? clock.GetUtcNow(), a.Actor);
    RuntimeTelemetry.Activations.Add(1, new KeyValuePair<string, object?>("th.environment", a.Environment), new KeyValuePair<string, object?>("th.scope", a.Scope));
    return Results.Json(new { a.Environment, a.Scope, a.Release, effectiveFrom = a.EffectiveFrom ?? clock.GetUtcNow() }, statusCode: 201);
});

app.MapGet("/activations/active", async (string environment, string scope, DateTimeOffset? at, StoreDbContext db, TimeProvider clock) =>
{
    var release = await new ReleaseRegistry(db, clock).ActiveAt(environment, scope, at ?? clock.GetUtcNow());
    return release is null ? Problem(404, $"No release of {scope} is active in {environment}") : Results.Ok(new { environment, scope, release });
});

// The branch the studio exports to (plan W8): every package under releases/, every activation under
// environments/<env>/activations/ and every bank's configuration values, stored and recorded. Running it again changes nothing.
app.MapPost("/import-branch", async (BranchImport b, StoreDbContext db, TimeProvider clock) =>
{
    if (!Directory.Exists(b.Path)) return Problem(404, $"No directory {b.Path}");
    var stored = new List<object>();
    var problems = new List<string>();
    var releases = Path.Combine(b.Path, "releases");
    if (Directory.Exists(releases))
    {
        // releases/<scope>/<name>/<release>/<target>/
        foreach (var dir in Directory.EnumerateDirectories(releases, "*", SearchOption.AllDirectories).Where(d => File.Exists(Path.Combine(d, "manifest.yaml"))).Order(StringComparer.Ordinal))
        {
            try
            {
                var p = Package.FromDirectory(dir);
                var res = await StorePackage(db, clock, p, b.Actor);
                stored.Add(new { p.Scope, p.Release, status = res is IStatusCodeHttpResult s ? s.StatusCode : 200 });
            }
            catch (PackageRejected e) { problems.Add($"{Path.GetRelativePath(b.Path, dir)}: {e.Message}"); }
        }
    }
    var activated = new List<object>();
    var envs = Path.Combine(b.Path, "environments");
    if (Directory.Exists(envs))
    {
        var reg = new ReleaseRegistry(db, clock);
        foreach (var f in Directory.EnumerateFiles(envs, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var y = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
            var env = y["environment"].ToString()!;
            var from = DateTimeOffset.Parse(y["effectiveFrom"].ToString()!);
            foreach (var item in (List<object>)y["releases"])
            {
                var m = (Dictionary<object, object>)item;
                var name = m["name"].ToString()!;
                var release = m["release"].ToString()!;
                foreach (var r in db.Releases.AsNoTracking().Where(x => x.Scope.StartsWith(name + "/") && x.Release == release).Select(x => x.Scope).ToList())
                {
                    var already = await db.Activations.AnyAsync(a => a.Environment == env && a.Scope == r && a.Release == release && a.EffectiveFrom == StoreTime.Of(from));
                    if (already) continue;
                    await reg.Activate(env, r, release, from, b.Actor);
                    RuntimeTelemetry.Activations.Add(1, new KeyValuePair<string, object?>("th.environment", env), new KeyValuePair<string, object?>("th.scope", r));
                    activated.Add(new { environment = env, scope = r, release, effectiveFrom = from });
                }
            }
        }
    }
    // The banks' configuration sets: each value an effective-dated version (plan P6).
    var values = await BankValues.Import(db, clock, Path.Combine(b.Path, "banks"), b.Actor);
    return Results.Ok(new { stored, activated, values, problems });
});

// Insert-only: nothing is changed or removed in place.
foreach (var path in new[] { "/releases", "/releases/{**rest}", "/activations", "/activations/{**rest}" })
    app.MapMethods(path, new[] { "PUT", "PATCH", "DELETE" }, () => Problem(405, "Releases and activations are insert-only: store a new release, or record a new activation"));

app.Run();

public sealed record ActivationRequest(string Environment, string Scope, string Release, DateTimeOffset? EffectiveFrom, string Actor);
public sealed record BranchImport(string Path, string Actor);

public partial class Program;
