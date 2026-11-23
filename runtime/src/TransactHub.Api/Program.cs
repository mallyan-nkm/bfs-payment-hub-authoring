using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Api;
using TransactHub.Auth;
using TransactHub.Security;
using TransactHub.Store;
using TransactHub.Telemetry;
using YamlDotNet.Serialization;

// The runtime's release API (plan W4). A package from the studio is stored once and never changed;
// which release new payments take in an environment is an activation, also never changed. There is
// no update and no delete: PUT, PATCH and DELETE are refused. The store is SQL Server or PostgreSQL,
// chosen by configuration (Store:Provider, Store:Connection).
//
// Every package is verified before it is stored or activated (spec R-09-24): it must carry the studio's signature
// (release.json, release.sig) by a key the installation trusts (TH_TRUSTED_SIGNERS, or Signing:TrustedSigners), not
// revoked, over every file. Each verification of a stored release is a fact on its chain. Every endpoint needs a
// caller the bank's identity provider vouches for, with the role the bank's role map gives it (R-09-27, RuntimeAuth). Secrets are read only by reference, through
// the one secret resolver (R-06-22).

var builder = WebApplication.CreateBuilder(args);
var provider = Enum.Parse<StoreProvider>(builder.Configuration["Store:Provider"] ?? "SqlServer", ignoreCase: true);
var connection = builder.Configuration["Store:Connection"] ?? throw new InvalidOperationException("Set Store:Connection");
var options = new StoreOptions(provider, connection);
// Environments where a package that carries simulators may be activated (R-07-12: never a promotable one).
var testEnvironments = (builder.Configuration["Releases:TestEnvironments"] ?? "test,dev,kit").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
// The folders POST /import-branch may read (R-09-58): a checkout of the content branch is under one of them. A path a caller
// names anywhere else on the host is refused, so the API is no way to read the host's files. Without it, no branch is imported.
var importRoots = (builder.Configuration["Releases:ImportRoots"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToArray();
// The host's secrets: env: reads the host's environment (through its configuration), file: a mounted file, vault: a Vault.
var secrets = SecretResolver.FromEnvironment(k => builder.Configuration[k]);
var trustSetting = builder.Configuration["Signing:TrustedSigners"] ?? builder.Configuration["TH_TRUSTED_SIGNERS"]
    ?? throw new InvalidOperationException("Set TH_TRUSTED_SIGNERS (or Signing:TrustedSigners): the signers whose packages this runtime accepts (spec R-09-25)");
// Read once now, so a broken list stops the start; then again at each verification, so a key added, retired or revoked needs no restart.
_ = await TrustedSigners.Load(trustSetting, secrets);
// The one bank this runtime serves, set at install (R-09-76): another bank's content is refused here, whatever the caller may do.
var runtimeBank = RuntimeBank.FromConfiguration(builder.Configuration);
// Where a machine caller (the pipeline) may change which distribution runs (R-09-78): by default the test environments only.
// Elsewhere a distribution's state changes only through the service console, with a bank approver's consent (R-09-77).
var machineActivation = (builder.Configuration["Releases:MachineActivationEnvironments"] is { Length: > 0 } machineEnvs ? machineEnvs : string.Join(",", testEnvironments))
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
builder.Services.AddScoped(_ => StoreDbContext.Open(options));
// The key this host signs the files it keeps with (Issue I-97): by reference, or one made for the host's lifetime.
var fileKey = builder.Configuration["TH_FILE_SIGNING_KEY_REF"] is { Length: > 0 } fileKeyRef
    ? ReleaseSignature.PrivateKeyFromPem(await secrets.Resolve(fileKeyRef))
    : System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
builder.Services.AddSingleton(TimeProvider.System);
await builder.Services.AddRuntimeAuth(builder.Configuration, secrets, ApiAuth.Roles);
// The studio's content repository as a read-only source of signed releases (R-09-30), when one is configured.
if (GitHubPackageSource.FromConfiguration(builder.Configuration, secrets) is { } source) builder.Services.AddSingleton<IPackageSource>(source);
// Traces, metrics and logs over OTLP when OTEL_* names an endpoint; /health/live and /health/ready, anonymous (R-09-31, R-09-36).
builder.Services.AddTransactHubTelemetry("transacthub-api");
builder.Services.AddHealthChecks().AddCheck<StoreReady>("store", tags: [WebTelemetry.Ready]);
var app = builder.Build();
// Security headers on every answer (R-09-60); the front door's volume is not rate limited here.
app.UseRuntimeSecurityHeaders(app.Configuration, limitWrites: false);
app.MapTransactHubHealth();
app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration["Store:Migrate"] != "false")
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<StoreDbContext>().Database.MigrateAsync();
}

static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);

/// <summary>Verifies a package and records the verification on its release's chain when <paramref name="record"/> (R-09-24).</summary>
async Task<Verification> Verify(StoreDbContext db, TimeProvider clock, Package p, string purpose, string actor, bool record)
{
    var v = p.VerifySignature(await TrustedSigners.Load(trustSetting, secrets));
    if (record)
        await new FactLog(db, clock).Append(p.ReleaseId, "release", new NewFact("release.verified", purpose, new JsonObject
        {
            ["keyId"] = v.KeyId, ["statementSha256"] = v.StatementSha256, ["changeSet"] = v.ChangeSet, ["signedAt"] = v.SignedAt.ToString("O"),
            ["files"] = v.Files, ["contentHash"] = p.ContentHash,
        }, p.ReleaseId, actor));
    return v;
}

async Task<Outcome> StorePackage(StoreDbContext db, TimeProvider clock, Package p, string actor, string purpose)
{
    // A package exported for a tenant is that bank's: another bank's is refused (R-09-76). Shared content names no tenant.
    if (p.Tenant is { Length: > 0 } tenant && runtimeBank.Refusal(tenant, p.ReleaseId) is { } wrongBank) return Outcome.Refused(422, wrongBank);
    Verification v;
    try { v = await Verify(db, clock, p, purpose, actor, record: false); }
    catch (SignatureRejected e) { return Outcome.Refused(422, $"{p.ReleaseId} is refused: {e.Message}"); }
    var reg = new ReleaseRegistry(db, clock);
    try
    {
        var r = await reg.Store(p.Scope, p.Release, p.CanonicalZip(), p.Manifest, actor, contentHash: p.ContentHash);
        if (r.Stored) await Verify(db, clock, p, purpose, actor, record: true);
        if (r.Stored) RuntimeTelemetry.ReleasesStored.Add(1, new KeyValuePair<string, object?>("th.scope", p.Scope));
        return new Outcome(r.Stored ? 201 : 200, new { id = r.Id, scope = p.Scope, release = p.Release, stored = r.Stored, promotable = p.Promotable, hash = p.ContentHash, signedBy = v.KeyId, changeSet = v.ChangeSet }, null);
    }
    catch (ReleaseConflict e) { return Outcome.Refused(409, e.Message); }
}

/// <summary>An activation: the stored package is verified again against the signers trusted now, so a key revoked since it was stored stops it.</summary>
async Task<Outcome> Activate(StoreDbContext db, TimeProvider clock, string environment, string scope, string release, DateTimeOffset effectiveFrom, string actor)
{
    if (MachineRefusal(environment, $"activate {scope}@{release}") is { } consent) return Outcome.Refused(403, consent);
    var reg = new ReleaseRegistry(db, clock);
    var r = await reg.Get(scope, release);
    if (r is null) return Outcome.Refused(404, $"{scope}@{release} is not stored; store it before activating it");
    var manifest = JsonNode.Parse(r.ManifestJson);
    var promotable = manifest?["promotable"]?.GetValue<bool>() == true && manifest?["containsSimulators"]?.GetValue<bool>() != true;
    if (!promotable && !testEnvironments.Contains(environment))
        return Outcome.Refused(409, $"{scope}@{release} is not promotable (it carries simulators or has errors): it may be activated only in {string.Join(", ", testEnvironments)}");
    try { await Verify(db, clock, Package.FromZip(r.Package), $"activation:{environment}", actor, record: true); }
    catch (Exception e) when (e is SignatureRejected or PackageRejected) { return Outcome.Refused(422, $"{scope}@{release} may not be activated: {e.Message}"); }
    await reg.Activate(environment, scope, release, effectiveFrom, actor);
    RuntimeTelemetry.Activations.Add(1, new KeyValuePair<string, object?>("th.environment", environment), new KeyValuePair<string, object?>("th.scope", scope));
    return new Outcome(201, new { environment, scope, release, effectiveFrom }, null);
}

app.MapPost("/releases", async (HttpRequest req, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    try { return (await StorePackage(db, clock, Package.FromZip(ms.ToArray()), ApiAuth.Actor(user), "upload")).Result; }
    catch (PackageRejected e) { return Problem(422, e.Message); }
}).RequireAuthorization(ApiAuth.Importer);

app.MapGet("/releases", async (string scope, string release, StoreDbContext db) =>
{
    var r = await new ReleaseRegistry(db).Get(scope, release);
    if (r is null) return Problem(404, $"No release {scope}@{release}");
    var facts = (await new FactLog(db).Of(r.Id)).Where(f => f.Kind == "release.verified")
        .Select(f => new { purpose = f.Code, by = f.Actor, at = f.At, detail = JsonNode.Parse(f.DataJson) }).ToList();
    return Results.Ok(new { r.Id, r.Scope, r.Release, r.ContentHash, manifest = JsonNode.Parse(r.ManifestJson), r.StoredBy, r.StoredAt, verifications = facts });
}).RequireAuthorization(ApiAuth.Reader);

// The package as stored, byte for byte, with its signature: what a deployment verifies and runs (plan P6).
app.MapGet("/releases/package", async (string scope, string release, StoreDbContext db) =>
{
    var r = await new ReleaseRegistry(db).Get(scope, release);
    return r is null ? Problem(404, $"No release {scope}@{release}") : Results.File(r.Package, "application/zip", $"{release}.zip");
}).RequireAuthorization(ApiAuth.Reader);

// A bank's configuration values in force at a moment (plan P6): what the workers look up at each attempt, so a value the studio
// publishes with a later validFrom reaches the payments of that date with no deployment. A caller sees only its tenants' values.
app.MapGet("/values", async (string tenant, DateTimeOffset? at, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock) =>
    ApiAuth.Has(user, ApiAuth.ValuesReader, tenant)
        ? Results.Ok(await BankValues.InForce(db, tenant, at ?? clock.GetUtcNow()))
        : Problem(403, $"{ApiAuth.Actor(user)} may not read the values of {tenant}")).RequireAuthorization(ApiAuth.ValuesReader);

// The reference tables of the bank's installed configuration, as of a date (R-01-25, R-09-75): the participant directory, the
// emirates, free zones, purpose codes, the business calendar, the bank's branches. The bank is the one named, or the only one the
// caller's grant gives it.
string? LookupBank(ClaimsPrincipal user, string? bank) => bank is { Length: > 0 } ? bank
    : TransactHub.Auth.RuntimeAuth.RolesByTenant(user).Where(kv => kv.Key != "*" && kv.Value.Contains(ApiAuth.LookupReader)).Select(kv => kv.Key).ToList() is [var only] ? only : null;
app.MapGet("/lookups", async (string? bank, ClaimsPrincipal user, StoreDbContext db, CancellationToken ct) =>
    LookupBank(user, bank) is not { } b ? Problem(400, "Name the bank")
    : !ApiAuth.Has(user, ApiAuth.LookupReader, b) ? Problem(403, $"{ApiAuth.Actor(user)} may not read the lookups of {b}")
    : Results.Ok(new { bank = b, domains = await Lookups.Domains(db, b, ct) })).RequireAuthorization(ApiAuth.LookupReader);
app.MapGet("/lookups/{domain}", async (string domain, string? bank, DateTimeOffset? asOf, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) =>
    LookupBank(user, bank) is not { } b ? Problem(400, "Name the bank")
    : !ApiAuth.Has(user, ApiAuth.LookupReader, b) ? Problem(403, $"{ApiAuth.Actor(user)} may not read the lookups of {b}")
    : await Lookups.At(db, b, domain, asOf ?? clock.GetUtcNow(), ct) is { } r ? Results.Ok(r)
    : Problem(404, $"{b}'s installed configuration has no reference table {domain}")).RequireAuthorization(ApiAuth.LookupReader);

app.MapPost("/activations", async (ActivationRequest a, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock) =>
    (await Activate(db, clock, a.Environment, a.Scope, a.Release, a.EffectiveFrom ?? clock.GetUtcNow(), ApiAuth.Actor(user))).Result).RequireAuthorization(ApiAuth.Activator);

app.MapGet("/activations/active", async (string environment, string scope, DateTimeOffset? at, StoreDbContext db, TimeProvider clock) =>
{
    var release = await new ReleaseRegistry(db, clock).ActiveAt(environment, scope, at ?? clock.GetUtcNow());
    return release is null ? Problem(404, $"No release of {scope} is active in {environment}") : Results.Ok(new { environment, scope, release });
}).RequireAuthorization(ApiAuth.Reader);

// The branch the studio exports to (plan W8): every package under releases/, every activation under
// environments/<env>/activations/ and every bank's configuration values, stored and recorded. Running it again changes nothing.
// A package that does not verify is a problem and is not stored; an activation needs the activator's role as well.
app.MapPost("/import-branch", async (BranchImport b, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock) =>
{
    // Only a folder under a configured import root, named without a link that leads out of it (R-09-58).
    if (ImportPath(b.Path) is not { } inside) return Problem(403, importRoots.Length == 0
        ? "No branch is imported: Releases:ImportRoots names no folder this API may read"
        : "The branch must be a folder under one of the configured import roots (Releases:ImportRoots)");
    b = b with { Path = inside };
    if (!Directory.Exists(b.Path)) return Problem(404, "No such directory under the import roots");
    var actor = ApiAuth.Actor(user);
    var stored = new List<object>();
    var problems = new List<string>();
    var releases = Path.Combine(b.Path, "releases");
    if (Directory.Exists(releases))
    {
        // releases/<scope>/<name>/<release>/<target>/
        // A bank's distributions and configuration releases are installed by their own kind (POST /sources/install), not here.
        foreach (var dir in Directory.EnumerateDirectories(releases, "*", SearchOption.AllDirectories).Where(d => File.Exists(Path.Combine(d, "manifest.yaml")))
                     .Where(d => !Path.GetRelativePath(releases, d).Replace('\\', '/').StartsWith("distributions/") && !Path.GetRelativePath(releases, d).Replace('\\', '/').StartsWith("configuration/")).Order(StringComparer.Ordinal))
        {
            try
            {
                var p = Package.FromDirectory(dir);
                var res = await StorePackage(db, clock, p, actor, "import-branch");
                if (res.Error is not null && res.Status == 422) problems.Add($"{Path.GetRelativePath(b.Path, dir)}: {res.Error}");
                else stored.Add(new { p.Scope, p.Release, status = res.Status });
            }
            catch (PackageRejected e) { problems.Add($"{Path.GetRelativePath(b.Path, dir)}: {e.Message}"); }
        }
    }
    var activated = new List<object>();
    var envs = Path.Combine(b.Path, "environments");
    if (Directory.Exists(envs))
    {
        var mayActivate = user.IsInRole(ApiAuth.Activator);
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
                    if (!mayActivate) { problems.Add($"{Path.GetRelativePath(b.Path, f)}: {actor} may not activate {r}@{release}: it needs the {ApiAuth.Activator} role"); continue; }
                    var res = await Activate(db, clock, env, r, release, from, actor);
                    if (res.Error is null) activated.Add(new { environment = env, scope = r, release, effectiveFrom = from });
                    else problems.Add($"{Path.GetRelativePath(b.Path, f)}: {res.Error}");
                }
            }
        }
    }
    // The banks' configuration sets: each value an effective-dated version (plan P6).
    var skipped = new List<string>();
    var values = await BankValues.Import(db, clock, Path.Combine(b.Path, "banks"), actor, onlyBank: runtimeBank.Id, skipped: skipped);
    problems.AddRange(skipped);
    return Results.Ok(new { stored, activated, values, problems });
}).RequireAuthorization(ApiAuth.Importer);

string? ImportPath(string? given)
{
    if (string.IsNullOrWhiteSpace(given) || importRoots.Length == 0) return null;
    string full;
    try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(given)); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    var root = importRoots.FirstOrDefault(r => full == r || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    if (root is null) return null;
    // A symbolic link on the way would lead outside the root: refuse it.
    for (var d = new DirectoryInfo(full); d is not null && d.FullName.Length > root.Length; d = d.Parent)
        if (d.Exists && d.LinkTarget is not null) return null;
    return full;
}

// The signed versions the package source offers (R-09-30): each tag, the release it names and whether it is stored here.
app.MapGet("/sources/versions", async (IServiceProvider sp, StoreDbContext db, CancellationToken ct) =>
{
    if (sp.GetService<IPackageSource>() is not { } src) return Problem(404, "No package source is configured (PackageSource:GitHub:Repository)");
    try
    {
        var versions = await src.List(ct);
        var ids = versions.Select(v => v.ReleaseId).ToList();
        var stored = db.Releases.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToHashSet();
        return Results.Ok(versions.Select(v => new { v.Tag, v.Kind, v.Workflow, v.Release, v.Target, v.Commit, releaseId = v.ReleaseId, stored = stored.Contains(v.ReleaseId) }));
    }
    catch (Exception e) when (e is PackageRejected or HttpRequestException or SecretUnavailable) { return Problem(502, $"The package source could not be read: {e.Message}"); }
}).RequireAuthorization(ApiAuth.Reader);

// Installs one version from the package source: downloaded, then verified and stored exactly as an upload is (R-09-24).
// A package that is unsigned, signed by a key not trusted, changed, or not the release its tag names is refused.
app.MapPost("/sources/install", async (SourceInstall s, IServiceProvider sp, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) =>
{
    if (sp.GetService<IPackageSource>() is not { } src) return Problem(404, "No package source is configured (PackageSource:GitHub:Repository)");
    PublishedVersion? v;
    Dictionary<string, byte[]> files;
    try
    {
        v = (await src.List(ct)).FirstOrDefault(x => x.Tag == s.Tag);
        if (v is null) return Problem(404, $"The package source has no version {s.Tag}");
        files = await src.Download(v, ct);
    }
    catch (Exception e) when (e is HttpRequestException or SecretUnavailable) { return Problem(502, $"The package source could not be read: {e.Message}"); }
    catch (PackageRejected e) { return Problem(422, e.Message); }
    // A bank's distribution or configuration release (R-09-49, R-09-51): verified and stored as an upload of its kind is.
    if (v.Kind != ReleaseSignature.WorkflowPackage)
    {
        try
        {
            var bundle = Bundle.FromFiles(files, v.Kind);
            if (bundle.ReleaseId != v.ReleaseId) return Problem(422, $"{s.Tag} names {v.ReleaseId}, but its bundle is {bundle.ReleaseId}");
            var svc = Distributions(db, clock);
            var res = v.Kind == ReleaseSignature.Distribution
                ? await svc.InstallDistribution(bundle, ApiAuth.Actor(user), $"package-source:{s.Tag}", ct)
                : await svc.InstallConfiguration(bundle, ApiAuth.Actor(user), $"package-source:{s.Tag}", ct);
            return Results.Json(res, statusCode: res["stored"]!.GetValue<bool>() ? 201 : 200);
        }
        catch (PackageRejected e) { return Problem(422, $"{s.Tag}: {e.Message}"); }
        catch (BundleRefused e) { return Problem(e.Status, e.Message); }
    }
    Package p;
    try { p = Package.FromFiles(files); }
    catch (PackageRejected e) { return Problem(422, $"{s.Tag}: {e.Message}"); }
    if (p.ReleaseId != v.ReleaseId) return Problem(422, $"{s.Tag} names {v.ReleaseId}, but its package is {p.ReleaseId}");
    return (await StorePackage(db, clock, p, ApiAuth.Actor(user), $"package-source:{s.Tag}")).Result;
}).RequireAuthorization(ApiAuth.Importer);

// A bank's distributions and configuration releases (docs/runtime/PACKAGING-AND-DEPLOYMENT.md, R-09-48 to R-09-57).
DistributionService Distributions(StoreDbContext db, TimeProvider clock) => new(db, clock, ct => TrustedSigners.Load(trustSetting, secrets, ct), testEnvironments, runtimeBank);

// A change of which distribution runs, asked by a machine caller outside the environments it may change (R-09-78).
string? MachineRefusal(string environment, string what) => machineActivation.Contains(environment) ? null
    : $"The release API may not {what} in {environment}: outside {string.Join(", ", machineActivation.Order())} a change to the system is asked for in the service console and carried out once a bank approver consents (R-09-77)";
IResult? NeedsConsent(string environment, string what) => MachineRefusal(environment, what) is { } m ? Problem(403, m) : null;

async Task<IResult> Refusable(Func<Task<IResult>> f)
{
    try { return await f(); }
    catch (PackageRejected e) { return Problem(422, e.Message); }
    catch (BundleRefused e) { return Problem(e.Status, e.Message); }
}

async Task<byte[]> Body(HttpRequest req)
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    return ms.ToArray();
}

app.MapPost("/distributions", async (HttpRequest req, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) => await Refusable(async () =>
{
    var r = await Distributions(db, clock).InstallDistribution(Bundle.FromZip(await Body(req), ReleaseSignature.Distribution), ApiAuth.Actor(user), "upload", ct);
    return Results.Json(r, statusCode: r["stored"]!.GetValue<bool>() ? 201 : 200);
})).RequireAuthorization(ApiAuth.Importer);

app.MapPost("/configuration-releases", async (HttpRequest req, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) => await Refusable(async () =>
{
    var r = await Distributions(db, clock).InstallConfiguration(Bundle.FromZip(await Body(req), ReleaseSignature.ConfigurationRelease), ApiAuth.Actor(user), "upload", ct);
    return Results.Json(r, statusCode: r["stored"]!.GetValue<bool>() ? 201 : 200);
})).RequireAuthorization(ApiAuth.Importer);

app.MapGet("/distributions", async (string? bank, string environment, StoreDbContext db, TimeProvider clock, CancellationToken ct) =>
    Results.Ok(await Distributions(db, clock).List(bank, environment, ct))).RequireAuthorization(ApiAuth.Reader);

app.MapGet("/distributions/active", async (string bank, string environment, StoreDbContext db, TimeProvider clock, CancellationToken ct) =>
    await new DistributionRegistry(db, clock).Active(bank, environment, ct) is { } d ? Results.Ok(new { bank, environment, distribution = d }) : Problem(404, $"No distribution of {bank} is active in {environment}")).RequireAuthorization(ApiAuth.Reader);

// The distribution as stored, byte for byte, with its signature: what an engine host verifies and loads.
app.MapGet("/distributions/package", async (string id, StoreDbContext db, TimeProvider clock, CancellationToken ct) => await Refusable(async () =>
    Results.File((await Distributions(db, clock).Stored(id, ct)).CanonicalZip(), "application/zip", "distribution.zip"))).RequireAuthorization(ApiAuth.Reader);

// A distribution belongs to one bank: the caller needs the activator's role in that bank (R-06-28, R-09-27).
IResult? NotForBank(ClaimsPrincipal user, string role, string? bank, string what) =>
    bank is { Length: > 0 } && !ApiAuth.Has(user, role, bank) ? Problem(403, $"{ApiAuth.Actor(user)} may not {what} for {bank}: it needs the {role} role in that bank") : null;
static string? BankOfDistribution(string? dist)
{
    try { return dist is { Length: > 0 } ? DistributionRegistry.Split(dist).bank : null; }
    catch (DistributionRefused) { return null; }
}

app.MapPost("/distributions/activate", async (DistributionAction a, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) => NotForBank(user, ApiAuth.Activator, BankOfDistribution(a.Id), "activate a distribution") ?? NeedsConsent(a.Environment, $"activate {a.Id}") ?? await Refusable(async () =>
    Results.Json(await Distributions(db, clock).Activate(a.Id ?? "", a.Environment, ApiAuth.Actor(user), a.Why ?? "activate", ct), statusCode: 201))).RequireAuthorization(ApiAuth.Activator);

app.MapPost("/distributions/drain", async (DistributionAction a, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) => NotForBank(user, ApiAuth.Activator, BankOfDistribution(a.Id), "drain a distribution") ?? NeedsConsent(a.Environment, $"drain {a.Id}") ?? await Refusable(async () =>
    Results.Ok(await Distributions(db, clock).Drain(a.Id ?? "", a.Environment, ApiAuth.Actor(user), a.Why ?? "drain", ct)))).RequireAuthorization(ApiAuth.Activator);

app.MapPost("/distributions/remove", async (DistributionAction a, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) => NotForBank(user, ApiAuth.Activator, BankOfDistribution(a.Id), "remove a distribution") ?? NeedsConsent(a.Environment, $"remove {a.Id}") ?? await Refusable(async () =>
    Results.Ok(await Distributions(db, clock).Remove(a.Id ?? "", a.Environment, ApiAuth.Actor(user), a.Why ?? "remove", ct)))).RequireAuthorization(ApiAuth.Activator);

app.MapPost("/distributions/rollback", async (DistributionAction a, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) => NotForBank(user, ApiAuth.Activator, a.Bank, "roll back a distribution") ?? NeedsConsent(a.Environment, $"roll back {a.Bank}") ?? await Refusable(async () =>
    Results.Json(await Distributions(db, clock).Rollback(a.Bank ?? "", a.Environment, ApiAuth.Actor(user), a.Why ?? "rollback", ct), statusCode: 201))).RequireAuthorization(ApiAuth.Activator);

// The front door: a payment is pinned at acceptance to the distribution active for its bank and environment (R-09-53),
// and its end is a fact (R-09-54, Issue I-97). A caller may pin only for its own tenants.
app.MapPost("/payments/pin", async (PaymentPin p, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) =>
    !ApiAuth.Has(user, ApiAuth.Intake, p.Bank) ? Problem(403, $"{ApiAuth.Actor(user)} may not accept payments of {p.Bank}")
    // The intake switch of the payment's scheme (R-09-62): while paused, a new payment is refused, with the code, and not pinned.
    : p.Scheme is { Length: > 0 } scheme && await IntakeSwitch.Paused(db, p.Bank, scheme, ct)
        ? Results.Json(new { error = new IntakePaused(p.Bank, scheme).Message, code = "ops.IntakePaused" }, statusCode: 423)
    // A payment id pinned already by another bank is not this caller's to read or pin.
    : NotForBank(user, ApiAuth.Intake, BankOfDistribution(await new DistributionRegistry(db, clock).PinOf(p.PaymentId, ct)), "accept this payment")
    ?? await Refusable(async () => Results.Ok(new { paymentId = p.PaymentId, distribution = await Distributions(db, clock).Pin(p.PaymentId, p.Bank, p.Environment, ApiAuth.Actor(user), ct) }))).RequireAuthorization(ApiAuth.Intake);

// The intake switch of a bank's scheme (R-09-62), for the front door.
app.MapGet("/intake", async (string tenant, string scheme, ClaimsPrincipal user, StoreDbContext db, CancellationToken ct) =>
    !ApiAuth.Has(user, ApiAuth.Intake, tenant) ? Problem(403, $"{ApiAuth.Actor(user)} may not read the intake of {tenant}")
    : Results.Ok(new { tenant, scheme, intake = await IntakeSwitch.Paused(db, tenant, scheme, ct) ? "paused" : "open" })).RequireAuthorization(ApiAuth.Intake);

// A payment's end is recorded only by the front door of the bank it is pinned to.
app.MapPost("/payments/end", async (PaymentEnd p, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) =>
    NotForBank(user, ApiAuth.Intake, BankOfDistribution(await new DistributionRegistry(db, clock).PinOf(p.PaymentId, ct)), "end this payment") ?? await Refusable(async () =>
    Results.Ok(new { paymentId = p.PaymentId, outcome = p.Outcome, recorded = await Distributions(db, clock).End(p.PaymentId, p.Outcome, ApiAuth.Actor(user), ct) }))).RequireAuthorization(ApiAuth.Intake);

// The files of a payment (Issue I-97, R-09-64): the front door keeps each file it receives for a payment here, signed by this host's key
// (TH_FILE_SIGNING_KEY_REF, a P-256 private key in PEM by reference; without one, a key made for the host's lifetime). Insert-only.
app.MapPost("/payments/files", async (FileKeep f, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) =>
{
    if (!ApiAuth.Has(user, ApiAuth.Intake, f.Tenant)) return Problem(403, $"{ApiAuth.Actor(user)} may not keep files of {f.Tenant}");
    if (runtimeBank.Refusal(f.Tenant, $"A file of {f.Tenant}") is { } wrongBank) return Problem(422, wrongBank);
    if (f.Direction is not ("in" or "out")) return Problem(400, "A file's direction is in or out");
    byte[] content;
    try { content = Convert.FromBase64String(f.ContentBase64); }
    catch (FormatException) { return Problem(400, "contentBase64 is not Base64"); }
    var row = await new FileStore(db, fileKey, clock).Keep(new FileToKeep(f.Tenant, f.Scheme, f.Direction, f.Kind, f.FileName, f.PaymentId, f.Leg, f.Activity), content, ct);
    return Results.Ok(new { id = row.Id, sha256 = row.ContentHash, size = row.Size, signingKeyId = row.SigningKeyId, signature = row.Signature });
}).RequireAuthorization(ApiAuth.Intake);

app.MapGet("/payments/files", async (string tenant, string paymentId, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) =>
    !ApiAuth.Has(user, ApiAuth.Reader, tenant) ? Problem(403, $"{ApiAuth.Actor(user)} may not read the files of {tenant}")
    : Results.Ok((await new FileStore(db, fileKey, clock).Of(tenant, paymentId, ct)).Select(r => new
    {
        r.Id, r.FileName, r.Direction, r.Kind, r.Size, sha256 = r.ContentHash, r.Leg, r.Activity, r.At, r.SigningKeyId, r.Signature,
        verified = r.SigningKeyId == FileSignature.KeyIdOf(fileKey) ? FileSignature.Verify(r, fileKey) : (bool?)null,
    }))).RequireAuthorization(ApiAuth.Reader);

app.MapGet("/payments/pin", async (string paymentId, StoreDbContext db, TimeProvider clock, CancellationToken ct) =>
{
    var reg = new DistributionRegistry(db, clock);
    var facts = await new FactLog(db, clock).Of(paymentId, ct);
    return await reg.PinOf(paymentId, ct) is { } d
        ? Results.Ok(new { paymentId, distribution = d, ended = facts.FirstOrDefault(f => f.Kind == DistributionRegistry.EndKind)?.Code, facts = facts.Select(f => new { f.Kind, f.Code, f.ReleaseId, at = f.At }) })
        : Problem(404, $"{paymentId} is not pinned");
}).RequireAuthorization(ApiAuth.Reader);

// The port binding in force for a bank at a moment (a configuration release's binding with its validFrom, scenario 2).
app.MapGet("/bindings", async (string tenant, string port, DateTimeOffset? at, ClaimsPrincipal user, StoreDbContext db, TimeProvider clock, CancellationToken ct) =>
    !ApiAuth.Has(user, ApiAuth.ValuesReader, tenant) ? Problem(403, $"{ApiAuth.Actor(user)} may not read the bindings of {tenant}")
    : await Distributions(db, clock).BindingAt(tenant, port, at ?? clock.GetUtcNow(), ct) is { } b ? Results.Ok(b) : Problem(404, $"No binding of {port} for {tenant}")).RequireAuthorization(ApiAuth.ValuesReader);

foreach (var path in new[] { "/distributions", "/distributions/{**rest}", "/configuration-releases", "/configuration-releases/{**rest}" })
    app.MapMethods(path, new[] { "PUT", "PATCH", "DELETE" }, () => Problem(405, "Distributions and configuration releases are insert-only: store a new one, or record a new state")).AllowAnonymous();

// Insert-only: nothing is changed or removed in place, whoever asks.
foreach (var path in new[] { "/releases", "/releases/{**rest}", "/activations", "/activations/{**rest}" })
    app.MapMethods(path, new[] { "PUT", "PATCH", "DELETE" }, () => Problem(405, "Releases and activations are insert-only: store a new release, or record a new activation")).AllowAnonymous();

app.Run();

/// <summary>An activation. The actor is the caller's verified identity; a name given here is not used.</summary>
public sealed record ActivationRequest(string Environment, string Scope, string Release, DateTimeOffset? EffectiveFrom, string? Actor = null);
/// <summary>A branch to import. The actor is the caller's verified identity; a name given here is not used.</summary>
public sealed record BranchImport(string Path, string? Actor = null);
/// <summary>A version of the package source to install, by its tag.</summary>
public sealed record SourceInstall(string Tag);
/// <summary>An action on a distribution in an environment: its id (activate, drain, remove) or its bank (rollback), and why.</summary>
public sealed record DistributionAction(string Environment, string? Id = null, string? Bank = null, string? Why = null);
/// <summary>A payment accepted by the front door: pinned to the active distribution of its bank in its environment.</summary>
public sealed record PaymentPin(string PaymentId, string Bank, string Environment, string? Scheme = null);
/// <summary>A payment's end: the outcome its root instance ended with.</summary>
public sealed record PaymentEnd(string PaymentId, string Outcome);
/// <summary>A file to keep for a payment: what it is, whose, and its bytes in Base64.</summary>
public sealed record FileKeep(string Tenant, string Scheme, string Direction, string Kind, string FileName, string? PaymentId, string ContentBase64, string? Leg = null, string? Activity = null);

/// <summary>What storing or activating came to: a status and a body, or a status and the reason it was refused.</summary>
sealed record Outcome(int Status, object? Body, string? Error)
{
    public static Outcome Refused(int status, string error) => new(status, null, error);
    public IResult Result => Error is null ? Results.Json(Body, statusCode: Status) : Results.Json(new { error = Error }, statusCode: Status);
}

public partial class Program;
