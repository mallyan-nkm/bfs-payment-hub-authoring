using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Api;
using TransactHub.Operations;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Service;

/// <summary>How the service console reaches the content it installs, and what it trusts.</summary>
/// <param name="Bank">The one bank this runtime serves (R-09-76): another bank's version is refused at verify, install and activate.</param>
public sealed record ContentSettings(string TrustSetting, ISecretResolver Secrets, IReadOnlySet<string> TestEnvironments, string Environment, RuntimeBank? Bank = null);

/// <summary>
/// Content install, update and upgrade (spec R-09-40, docs/runtime/PACKAGING-AND-DEPLOYMENT.md). GitHub is the artefact
/// repository, read only, through <see cref="IPackageSource"/>: every version of every kind is listed; a workflow package is
/// downloaded, verified against the trusted signers and stored exactly as the release API stores an upload; activation
/// from a date moves the previous release to draining; rollback activates the previous release again; removal is refused
/// while the release is active or a payment pinned to it is open. A bank's distribution and configuration release
/// (R-09-48 to R-09-57) are verified, installed, activated, drained, removed and rolled back through
/// <see cref="DistributionService"/>, and a distribution's open payments are those pinned to it with no end fact.
/// Every action is a fact (<see cref="ServiceAudit"/>).
/// </summary>
public sealed class ContentService(StoreOptions store, StoreOptions ops, IPackageSource? source, ContentSettings settings, ServiceAudit audit, ErrorDictionary dictionary, TimeProvider clock)
{
    /// <summary>ISO 20022 statuses a payment does not leave: how a workflow package's open payments are counted, from the read model.
    /// A distribution's are counted exactly, from its pins and the payments' end facts.</summary>
    public static readonly string[] Terminal = ["ACSC", "ACCC", "RJCT", "CANC"];

    IPackageSource Source => source ?? throw new ServiceRefused(404, ErrorCodes.SourceUnreachable, "No package source is configured (PackageSource:GitHub:Repository)");

    async Task<IReadOnlyList<PublishedVersion>> List(CancellationToken ct)
    {
        try { return await Source.List(ct); }
        catch (Exception e) when (e is PackageRejected or HttpRequestException or SecretUnavailable) { throw new ServiceRefused(502, ErrorCodes.SourceUnreachable, $"The package source could not be read: {e.Message}"); }
    }

    async Task<PublishedVersion> Version(string tag, CancellationToken ct) =>
        (await List(ct)).FirstOrDefault(v => v.Tag == tag) ?? throw new ServiceRefused(404, ErrorCodes.UnknownVersion, $"The package source has no version {tag}");

    /// <summary>Every signed version the source offers, of every kind, with whether and how it is installed here.</summary>
    public async Task<JsonArray> Versions(CancellationToken ct = default)
    {
        var versions = await List(ct);
        var states = await States(ct);
        // A workflow package is offered, never installable, once the bank runs distributions (R-09-114).
        string? beside = null;
        if (settings.Bank is { } bank)
            await using (var db = StoreDbContext.Open(store)) beside = await WorkflowPackages.Refusal(db, bank.Id, "A workflow package", ct);
        return new JsonArray(versions.Select(v =>
        {
            return (JsonNode)new JsonObject
            {
                ["tag"] = v.Tag, ["kind"] = v.Kind, ["workflow"] = v.Workflow, ["release"] = v.Release, ["target"] = v.Target, ["commit"] = v.Commit,
                ["bank"] = v.Kind == ReleaseSignature.WorkflowPackage ? null : v.Workflow,
                ["releaseId"] = v.ReleaseId, ["state"] = states.TryGetValue(v.ReleaseId, out var s) ? s : "offered",
                // Another bank's distribution or configuration release is listed, never installable here (R-09-76).
                ["installable"] = v.Kind == ReleaseSignature.WorkflowPackage ? beside is null : settings.Bank?.Serves(v.Workflow) != false,
                ["notInstallable"] = v.Kind == ReleaseSignature.WorkflowPackage ? beside
                    : settings.Bank is { } own && !own.Serves(v.Workflow) ? $"{v.Workflow}'s: this runtime serves {own.Id} only" : null,
            };
        }).ToArray());
    }

    DistributionService Distributions(StoreDbContext db) => new(db, clock, ct => TrustedSigners.Load(settings.TrustSetting, settings.Secrets, ct), settings.TestEnvironments, settings.Bank);

    /// <summary>Refuses a version whose signed manifest names another bank than this runtime's (R-09-76).</summary>
    void OwnBank(string? bank, string what)
    {
        if (settings.Bank?.Refusal(bank, what) is { } why) throw new ServiceRefused(422, ErrorCodes.WrongBank, why);
    }

    /// <summary>A workflow package is neither installed nor activated beside the bank's distributions (R-09-114, Issue I-212).</summary>
    async Task BesideDistributions(StoreDbContext db, string releaseId, CancellationToken ct)
    {
        if (settings.Bank is { } own && await WorkflowPackages.Refusal(db, own.Id, releaseId, ct) is { } why) throw new ServiceRefused(409, ErrorCodes.KindNotInstallable, why);
    }

    static bool IsBundle(string id) => id.StartsWith(DistributionRegistry.DistPrefix, StringComparison.Ordinal) || id.StartsWith(DistributionRegistry.ConfigPrefix, StringComparison.Ordinal);

    static ServiceRefused Refused(BundleRefused e) => new(e.Status, e.Code is { } own ? own
        : e.Status == 409 && e.Message.Contains("removed") || e.Message.Contains("open payment") || e.Message.Contains("longest declared wait") || e.Message.Contains("is active in") ? ErrorCodes.RemovalRefused
        : e.Status == 409 && e.Message.Contains("already stored") ? ErrorCodes.ReleaseConflict
        : e.Message.Contains("not a dial") || e.Message.Contains("is below") || e.Message.Contains("is above") || e.Message.Contains("not one of") || e.Message.Contains("secret") ? ErrorCodes.PackageRejected
        : e.Status == 404 ? ErrorCodes.UnknownVersion
        : e.Message.Contains("signed by key") && e.Message.Contains("does not trust") ? ErrorCodes.UntrustedKey
        : e.Message.Contains("not signed") ? ErrorCodes.NotSigned
        : e.Message.Contains("does not match") || e.Message.Contains("not in the signed statement") || e.Message.Contains("was changed") ? ErrorCodes.Tampered
        : e.Status == 422 ? ErrorCodes.PackageRejected : ErrorCodes.ActionNotOffered, e.Message);

    /// <summary>Downloads a distribution or a configuration release and verifies it as an install would, storing nothing.</summary>
    public async Task<(Bundle bundle, Verification verification, PublishedVersion version)> CheckBundle(string tag, CancellationToken ct = default)
    {
        var v = await Version(tag, ct);
        Dictionary<string, byte[]> files;
        try { files = await Source.Download(v, ct); }
        catch (Exception e) when (e is HttpRequestException or SecretUnavailable) { throw new ServiceRefused(502, ErrorCodes.SourceUnreachable, $"The package source could not be read: {e.Message}"); }
        catch (PackageRejected e) { throw new ServiceRefused(422, ErrorCodes.PackageRejected, e.Message); }
        Bundle b;
        try { b = Bundle.FromFiles(files, v.Kind); }
        catch (PackageRejected e) { throw new ServiceRefused(422, e.Message.Contains("does not match") || e.Message.Contains("but not in the bundle") ? ErrorCodes.Tampered : ErrorCodes.PackageRejected, $"{tag}: {e.Message}"); }
        if (b.ReleaseId != v.ReleaseId) throw new ServiceRefused(422, ErrorCodes.WrongRelease, $"{tag} names {v.ReleaseId}, but its bundle is {b.ReleaseId}");
        OwnBank(b.Bank, b.ReleaseId);
        try { return (b, b.Verify(await TrustedSigners.Load(settings.TrustSetting, settings.Secrets, ct)), v); }
        catch (SignatureRejected e) { throw new ServiceRefused(422, ErrorCodes.Of(e), $"{b.ReleaseId} is refused: {e.Message}"); }
    }

    /// <summary>Downloads a version and checks it as an install would, storing nothing.</summary>
    public async Task<(Package package, Verification verification, PublishedVersion version)> Check(string tag, CancellationToken ct = default)
    {
        var v = await Version(tag, ct);
        if (v.Kind != ReleaseSignature.WorkflowPackage)
            throw new ServiceRefused(400, ErrorCodes.PackageRejected, $"{tag} is a {v.Kind}, not a workflow package");
        Dictionary<string, byte[]> files;
        try { files = await Source.Download(v, ct); }
        catch (Exception e) when (e is HttpRequestException or SecretUnavailable) { throw new ServiceRefused(502, ErrorCodes.SourceUnreachable, $"The package source could not be read: {e.Message}"); }
        catch (PackageRejected e) { throw new ServiceRefused(422, ErrorCodes.PackageRejected, e.Message); }
        Package p;
        try { p = Package.FromFiles(files); }
        catch (PackageRejected e)
        {
            // The manifest is signed: a file that does not match it, or one it does not list, was changed after signing.
            var changed = e.Message.Contains("does not match its hash") || e.Message.Contains("Files not in the manifest") || e.Message.Contains("is in the manifest but not in the package");
            throw new ServiceRefused(422, changed ? ErrorCodes.Tampered : ErrorCodes.PackageRejected, $"{tag}: {e.Message}");
        }
        if (p.ReleaseId != v.ReleaseId) throw new ServiceRefused(422, ErrorCodes.WrongRelease, $"{tag} names {v.ReleaseId}, but its package is {p.ReleaseId}");
        if (p.Tenant is { Length: > 0 } tenant) OwnBank(tenant, p.ReleaseId);
        await using (var db = StoreDbContext.Open(store)) await BesideDistributions(db, p.ReleaseId, ct);
        try { return (p, p.VerifySignature(await TrustedSigners.Load(settings.TrustSetting, settings.Secrets, ct)), v); }
        catch (SignatureRejected e) { throw new ServiceRefused(422, ErrorCodes.Of(e), $"{p.ReleaseId} is refused: {e.Message}"); }
    }

    /// <summary>Verifies a version on GitHub against the trusted signers; the outcome, either way, is a fact.</summary>
    public async Task<JsonObject> Verify(string tag, ServiceIdentity who, string? why, CancellationToken ct = default)
    {
        try
        {
            if ((await Version(tag, ct)).Kind != ReleaseSignature.WorkflowPackage)
            {
                var (b, bv, _) = await CheckBundle(tag, ct);
                await audit.Record("verify", who, why, new JsonObject { ["tag"] = tag, ["release"] = b.ReleaseId, ["result"] = "verified", ["keyId"] = bv.KeyId, ["files"] = bv.Files }, ct: ct);
                return new JsonObject { ["tag"] = tag, ["releaseId"] = b.ReleaseId, ["kind"] = b.Kind, ["verified"] = true, ["keyId"] = bv.KeyId, ["signedAt"] = bv.SignedAt.ToString("O"), ["changeSet"] = bv.ChangeSet, ["files"] = bv.Files, ["promotable"] = true };
            }
            var (p, v, _) = await Check(tag, ct);
            await audit.Record("verify", who, why, new JsonObject { ["tag"] = tag, ["release"] = p.ReleaseId, ["result"] = "verified", ["keyId"] = v.KeyId, ["files"] = v.Files }, ct: ct);
            return new JsonObject { ["tag"] = tag, ["releaseId"] = p.ReleaseId, ["verified"] = true, ["keyId"] = v.KeyId, ["signedAt"] = v.SignedAt.ToString("O"), ["changeSet"] = v.ChangeSet, ["files"] = v.Files, ["promotable"] = p.Promotable };
        }
        catch (ServiceRefused e)
        {
            await audit.Record("verify", who, why, new JsonObject { ["tag"] = tag, ["result"] = "refused", ["error"] = e.Code, ["message"] = e.Message }, ct: ct);
            throw;
        }
    }

    /// <summary>Installs a version: verified, then stored once, as the release API stores an upload (R-09-24).</summary>
    public async Task<JsonObject> Install(string tag, ServiceIdentity who, string why, CancellationToken ct = default)
    {
        if ((await Version(tag, ct)).Kind != ReleaseSignature.WorkflowPackage) return await InstallBundle(tag, who, why, ct);
        Package p; Verification v;
        try { (p, v, _) = await Check(tag, ct); }
        catch (ServiceRefused e)
        {
            await audit.Record("install", who, why, new JsonObject { ["tag"] = tag, ["result"] = "refused", ["error"] = e.Code, ["message"] = e.Message }, ct: ct);
            throw;
        }
        await using var db = StoreDbContext.Open(store);
        await BesideDistributions(db, p.ReleaseId, ct);
        StoreResult r;
        try { r = await new ReleaseRegistry(db, clock).Store(p.Scope, p.Release, p.CanonicalZip(), p.Manifest, who.Actor, ct, p.ContentHash); }
        catch (TransactHub.Store.ReleaseConflict e) { throw new ServiceRefused(409, ErrorCodes.ReleaseConflict, e.Message); }
        if (r.Stored)
        {
            await new FactLog(db, clock).Append(p.ReleaseId, "release", new NewFact("release.verified", $"package-source:{tag}", new JsonObject
            {
                ["keyId"] = v.KeyId, ["statementSha256"] = v.StatementSha256, ["changeSet"] = v.ChangeSet, ["signedAt"] = v.SignedAt.ToString("O"), ["files"] = v.Files, ["contentHash"] = p.ContentHash,
            }, p.ReleaseId, who.Actor), ct);
            dictionary.AddPackage(p.CanonicalZip(), p.ReleaseId);
        }
        await audit.Record("install", who, why, new JsonObject { ["tag"] = tag, ["result"] = r.Stored ? "installed" : "already installed", ["keyId"] = v.KeyId }, release: p.ReleaseId, ct: ct);
        return new JsonObject { ["releaseId"] = p.ReleaseId, ["stored"] = r.Stored, ["signedBy"] = v.KeyId, ["state"] = "installed", ["promotable"] = p.Promotable };
    }

    async Task<JsonObject> InstallBundle(string tag, ServiceIdentity who, string why, CancellationToken ct)
    {
        Bundle b; Verification v;
        try { (b, v, _) = await CheckBundle(tag, ct); }
        catch (ServiceRefused e)
        {
            await audit.Record("install", who, why, new JsonObject { ["tag"] = tag, ["result"] = "refused", ["error"] = e.Code, ["message"] = e.Message }, ct: ct);
            throw;
        }
        await using var db = StoreDbContext.Open(store);
        JsonObject r;
        // A configuration release installed from the service console is installed on a bank approver's consent (R-09-77): it is put in force at once (R-09-111).
        try { r = b.Kind == ReleaseSignature.Distribution ? await Distributions(db).InstallDistribution(b, who.Actor, $"package-source:{tag}", ct) : await Distributions(db).InstallConfiguration(b, who.Actor, $"package-source:{tag}", putInForce: true, ct, consent: new JsonObject { ["consentedBy"] = who.Actor, ["via"] = "service-console:install" }); }
        catch (BundleRefused e)
        {
            var refused = Refused(e);
            await audit.Record("install", who, why, new JsonObject { ["tag"] = tag, ["result"] = "refused", ["error"] = refused.Code, ["message"] = e.Message }, ct: ct);
            throw refused;
        }
        var stored = r["stored"]!.GetValue<bool>();
        if (stored && b.Kind == ReleaseSignature.Distribution)
            foreach (var (path, bytes) in b.Files.Where(f => f.Key.StartsWith("errors/") && f.Key.EndsWith(".yaml")))
                dictionary.Add(Encoding.UTF8.GetString(bytes), b.ReleaseId);
        await audit.Record("install", who, why, new JsonObject { ["tag"] = tag, ["kind"] = b.Kind, ["result"] = stored ? "installed" : "already installed", ["keyId"] = v.KeyId }, release: b.ReleaseId, ct: ct);
        return r;
    }

    /// <summary>Checks an installed configuration release as putting it in force will (R-09-111): when it is asked for, before any consent.</summary>
    public async Task CheckPutInForce(string releaseId, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        try
        {
            if (await Distributions(db).InForce(releaseId, ct)) throw new ServiceRefused(409, ErrorCodes.AlreadyDecided, $"{releaseId} is in force already");
            await Distributions(db).CheckStoredConfiguration(releaseId, ct);
        }
        catch (BundleRefused e) { throw Refused(e); }
    }

    /// <summary>
    /// Puts an installed configuration release in force (R-09-111): carried out on a bank approver's consent to the request a
    /// service engineer or the release API's pipeline made; its signature and its values' bounds are checked again first.
    /// </summary>
    public async Task<JsonObject> PutInForce(string releaseId, ServiceIdentity who, string why, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        JsonObject r;
        try { r = await Distributions(db).PutConfigurationInForce(releaseId, who.Actor, why, new JsonObject { ["consentedBy"] = who.Actor, ["via"] = "service-console:put-in-force" }, ct); }
        catch (BundleRefused e)
        {
            var refused = Refused(e);
            await audit.Record("put-in-force", who, why, new JsonObject { ["result"] = "refused", ["error"] = refused.Code, ["message"] = e.Message }, release: releaseId, ct: ct);
            throw refused;
        }
        await audit.Record("put-in-force", who, why, new JsonObject { ["result"] = (bool?)r["already"] == true ? "in force already" : "in force", ["values"] = (int?)r["values"], ["bindings"] = (int?)r["bindings"] }, release: releaseId, ct: ct);
        return r;
    }

    /// <summary>
    /// The difference between a distribution or a configuration release and what its bank has active (a distribution) or
    /// installed last (a configuration release): artefacts and their releases, schemes, parameters, values.
    /// </summary>
    async Task<JsonObject> DiffBundle(string tag, string env, CancellationToken ct)
    {
        var (b, _, _) = await CheckBundle(tag, ct);
        await using var db = StoreDbContext.Open(store);
        Bundle? against = null;
        if (b.Kind == ReleaseSignature.Distribution)
        {
            if (await new DistributionRegistry(db, clock).Active(b.Bank, env, ct) is { } active) against = await Distributions(db).Stored(active, ct);
        }
        else
        {
            var rows = await db.Releases.AsNoTracking().Where(r => r.Scope == DistributionRegistry.ConfigScope(b.Bank)).OrderByDescending(r => r.StoredAt).ToListAsync(ct);
            if (rows.FirstOrDefault(r => r.Release != b.Release) is { } last) against = Bundle.FromZip(last.Package, ReleaseSignature.ConfigurationRelease);
        }
        static Dictionary<string, string> Artefacts(Bundle? x) => (x?.Manifest["billOfMaterials"]?["artefacts"] as JsonArray ?? []).OfType<JsonObject>()
            .GroupBy(a => $"{(string?)a["kind"]}:{(string?)a["name"]}").ToDictionary(g => g.Key, g => string.Join(", ", g.Select(a => $"{(string?)a["release"]} {(string?)a["sha256"]}")));
        static Dictionary<string, string> Packs(Bundle? x) => (x?.Manifest["billOfMaterials"]?["packs"] as JsonArray ?? []).OfType<JsonObject>()
            .ToDictionary(a => (string)a["scope"]!, a => $"{(string?)a["release"]}");
        static Dictionary<string, string> Dials(Bundle? x) => ParameterContract.Of(x?.Manifest["parameterContract"]).ToDictionary(d => d.Key, d => CanonicalJson.Of(d.Value));
        static Dictionary<string, string> Values(Bundle? x) => x?.Files.TryGetValue("configuration/configuration-set.yaml", out var t) == true
            ? BankValues.ValuesOf(Encoding.UTF8.GetString(t)).GroupBy(v => v.parameter).ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(v => CanonicalJson.Of(v.value))))
            : [];
        var activeId = against?.ReleaseId;
        return new JsonObject
        {
            ["tag"] = tag, ["releaseId"] = b.ReleaseId, ["kind"] = b.Kind, ["environment"] = env, ["active"] = activeId,
            ["packs"] = Compare(Packs(against), Packs(b)), ["artefacts"] = Compare(Artefacts(against), Artefacts(b)),
            ["parameters"] = b.Kind == ReleaseSignature.Distribution ? Compare(Dials(against), Dials(b)) : Compare(Values(against), Values(b)),
            ["flows"] = new JsonArray(),
        };
    }

    /// <summary>The difference between a version and what is active for its scope: artefacts, versions, parameters.</summary>
    public async Task<JsonObject> Diff(string tag, string? environment = null, CancellationToken ct = default)
    {
        var env = environment ?? settings.Environment;
        if ((await Version(tag, ct)).Kind != ReleaseSignature.WorkflowPackage) return await DiffBundle(tag, env, ct);
        var (p, _, _) = await Check(tag, ct);
        await using var db = StoreDbContext.Open(store);
        var activeRelease = await new ReleaseRegistry(db, clock).ActiveAt(env, p.Scope, clock.GetUtcNow(), ct);
        Package? active = null;
        if (activeRelease is not null && await new ReleaseRegistry(db, clock).Get(p.Scope, activeRelease, ct) is { } row) active = Package.FromZip(row.Package);
        static Dictionary<string, string> Flows(Package? x) => (x?.Manifest["flows"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(f => (string)f["name"]!, f => $"{(string?)f["release"]} {(string?)f["hash"]}");
        static Dictionary<string, string> Files(Package? x) => (x?.Manifest["files"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(f => !((string)f["path"]!).StartsWith("workers/")).ToDictionary(f => Unversioned((string)f["path"]!), f => $"{Path.GetFileName((string)f["path"]!)} {(string?)f["hash"]}");
        var flows = Compare(Flows(active), Flows(p));
        var files = Compare(Files(active), Files(p));
        var parameters = Compare(Parameters(active), Parameters(p));
        return new JsonObject
        {
            ["tag"] = tag, ["releaseId"] = p.ReleaseId, ["environment"] = env, ["active"] = activeRelease is null ? null : $"{p.Scope}@{activeRelease}",
            ["flows"] = flows, ["artefacts"] = files, ["parameters"] = parameters,
        };
    }

    static string Unversioned(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"@[0-9]+\.[0-9]+\.[0-9]+", "");

    /// <summary>Every parameter the package's activities and flows declare, with its type, default and bounds.</summary>
    static Dictionary<string, string> Parameters(Package? p)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (p is null) return d;
        foreach (var (path, bytes) in p.Files.Where(f => f.Key.EndsWith(".yaml") && (f.Key.StartsWith("activities/") || f.Key.StartsWith("workflows/"))))
        {
            if (Yaml.ToJson(Encoding.UTF8.GetString(bytes)) is not JsonObject doc || doc["parameters"] is not JsonObject ps) continue;
            foreach (var (k, v) in ps) d[$"{(string?)doc["name"]}#{k}"] = CanonicalJson.Of(v);
        }
        return d;
    }

    static JsonArray Compare(Dictionary<string, string> before, Dictionary<string, string> after) => new(before.Keys.Union(after.Keys).Order(StringComparer.Ordinal).Select(k =>
    {
        var b = before.GetValueOrDefault(k);
        var a = after.GetValueOrDefault(k);
        var change = b is null ? "added" : a is null ? "removed" : a == b ? "same" : "changed";
        return (JsonNode)new JsonObject { ["key"] = k, ["change"] = change, ["active"] = b, ["offered"] = a };
    }).Where(n => (string?)n!["change"] != "same").ToArray());

    /// <summary>An activation from a date. The stored package is verified again; the release active until then starts draining.</summary>
    public async Task<JsonObject> Activate(string releaseId, string? environment, DateTimeOffset? from, ServiceIdentity who, string why, CancellationToken ct = default)
    {
        var env = environment ?? settings.Environment;
        if (releaseId.StartsWith(DistributionRegistry.DistPrefix, StringComparison.Ordinal)) return await Bundled("activate", releaseId, env, who, why, (svc, c) => svc.Activate(releaseId, env, who.Actor, why, c), ct);
        var (scope, release) = Split(releaseId);
        await using var db = StoreDbContext.Open(store);
        await BesideDistributions(db, releaseId, ct);
        var reg = new ReleaseRegistry(db, clock);
        var row = await reg.Get(scope, release, ct) ?? throw new ServiceRefused(404, ErrorCodes.UnknownVersion, $"{releaseId} is not installed; install it first");
        if (await Removed(db, releaseId, ct)) throw new ServiceRefused(409, ErrorCodes.RemovalRefused, $"{releaseId} was removed; install it again before activating it");
        var p = Package.FromZip(row.Package);
        if (p.Tenant is { Length: > 0 } tenant) OwnBank(tenant, releaseId);
        if (!p.Promotable && !settings.TestEnvironments.Contains(env))
            throw new ServiceRefused(409, ErrorCodes.NotPromotable, $"{releaseId} is not promotable (it carries simulators or has errors): it may be activated only in {string.Join(", ", settings.TestEnvironments)}");
        Verification v;
        try { v = p.VerifySignature(await TrustedSigners.Load(settings.TrustSetting, settings.Secrets, ct)); }
        catch (SignatureRejected e) { throw new ServiceRefused(422, ErrorCodes.Of(e), $"{releaseId} may not be activated: {e.Message}"); }
        var at = from ?? clock.GetUtcNow();
        var previous = await reg.ActiveAt(env, scope, at, ct);
        var log = new FactLog(db, clock);
        await log.Append(releaseId, "release", new NewFact("release.verified", $"activation:{env}", new JsonObject { ["keyId"] = v.KeyId, ["statementSha256"] = v.StatementSha256, ["files"] = v.Files }, releaseId, who.Actor), ct);
        await reg.Activate(env, scope, release, at, who.Actor, ct);
        await audit.Record("activate", who, why, new JsonObject { ["environment"] = env, ["effectiveFrom"] = at.ToString("O"), ["replaces"] = previous }, release: releaseId, ct: ct);
        if (previous is not null && previous != release)
            await audit.Record("draining", who, why, new JsonObject { ["environment"] = env, ["from"] = at.ToString("O"), ["replacedBy"] = releaseId }, release: $"{scope}@{previous}", ct: ct);
        return new JsonObject { ["releaseId"] = releaseId, ["environment"] = env, ["effectiveFrom"] = at.ToString("O"), ["draining"] = previous is null || previous == release ? null : $"{scope}@{previous}" };
    }

    /// <summary>A distribution action, carried out by the distribution service and recorded as the console's fact.</summary>
    async Task<JsonObject> Bundled(string action, string target, string env, ServiceIdentity who, string why, Func<DistributionService, CancellationToken, Task<JsonObject>> f, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        JsonObject r;
        try { r = await f(Distributions(db), ct); }
        catch (BundleRefused e) { throw Refused(e); }
        await audit.Record(action, who, why, new JsonObject { ["environment"] = env, ["result"] = r.DeepClone() }, release: target, ct: ct);
        if (r["draining"] is JsonValue d && (string?)d is { } draining) await audit.Record("draining", who, why, new JsonObject { ["environment"] = env, ["replacedBy"] = (string?)r["active"] }, release: draining, ct: ct);
        return r;
    }

    /// <summary>Draining a distribution by hand: it takes no new payment; its open payments run to their end.</summary>
    public Task<JsonObject> Drain(string dist, string? environment, ServiceIdentity who, string why, CancellationToken ct = default)
    {
        var env = environment ?? settings.Environment;
        if (!dist.StartsWith(DistributionRegistry.DistPrefix, StringComparison.Ordinal)) throw new ServiceRefused(400, ErrorCodes.ActionNotOffered, $"{dist} is not a distribution: a workflow package drains when another release of it is activated");
        return Bundled("drain", dist, env, who, why, (svc, c) => svc.Drain(dist, env, who.Actor, why, c), ct);
    }

    /// <summary>Rollback: the release that was active before the current one, activated again now.</summary>
    public async Task<JsonObject> Rollback(string scope, string? environment, ServiceIdentity who, string why, CancellationToken ct = default)
    {
        var env = environment ?? settings.Environment;
        // A bank's distributions: dist/<bank>.
        if (scope.StartsWith(DistributionRegistry.DistPrefix, StringComparison.Ordinal))
        {
            var bank = scope[DistributionRegistry.DistPrefix.Length..].Split('@')[0];
            return await Bundled("rollback", DistributionRegistry.Scope(bank), env, who, why, (svc, c) => svc.Rollback(bank, env, who.Actor, why, c), ct);
        }
        var now = clock.GetUtcNow();
        await using var db = StoreDbContext.Open(store);
        var current = await new ReleaseRegistry(db, clock).ActiveAt(env, scope, now, ct) ?? throw new ServiceRefused(409, ErrorCodes.ActionNotOffered, $"Nothing of {scope} is active in {env}");
        var t = StoreTime.Of(now);
        var acts = await db.Activations.AsNoTracking().Where(a => a.Environment == env && a.Scope == scope && a.EffectiveFrom <= t).OrderByDescending(a => a.EffectiveFrom).ThenByDescending(a => a.Id).ToListAsync(ct);
        var previous = acts.Select(a => a.Release).FirstOrDefault(r => r != current) ?? throw new ServiceRefused(409, ErrorCodes.ActionNotOffered, $"{scope} has no earlier release in {env} to roll back to");
        var r = await Activate($"{scope}@{previous}", env, now, who, $"rollback: {why}", ct);
        r["rolledBackFrom"] = $"{scope}@{current}";
        return r;
    }

    /// <summary>Removal: refused while the release is active or any payment pinned to it is open. The package stays as a record.</summary>
    public async Task<JsonObject> Remove(string releaseId, ServiceIdentity who, string why, CancellationToken ct = default, string? environment = null)
    {
        if (releaseId.StartsWith(DistributionRegistry.DistPrefix, StringComparison.Ordinal))
        {
            var env = environment ?? settings.Environment;
            return await Bundled("removed", releaseId, env, who, why, (svc, c) => svc.Remove(releaseId, env, who.Actor, why, c), ct);
        }
        var (scope, release) = Split(releaseId);
        await using var db = StoreDbContext.Open(store);
        if (await new ReleaseRegistry(db, clock).Get(scope, release, ct) is null) throw new ServiceRefused(404, ErrorCodes.UnknownVersion, $"{releaseId} is not installed");
        var envs = await db.Activations.AsNoTracking().Where(a => a.Scope == scope).Select(a => a.Environment).Distinct().ToListAsync(ct);
        foreach (var env in envs)
            if (await new ReleaseRegistry(db, clock).ActiveAt(env, scope, clock.GetUtcNow(), ct) == release)
                throw new ServiceRefused(409, ErrorCodes.RemovalRefused, $"{releaseId} is active in {env}: activate another release first");
        var open = (await OpenPayments(ct)).GetValueOrDefault(releaseId);
        if (open > 0) throw new ServiceRefused(409, ErrorCodes.RemovalRefused, $"{releaseId} has {open} open payment(s) pinned to it: it may be removed once they end");
        await audit.Record("removed", who, why, new JsonObject { ["open"] = 0 }, release: releaseId, ct: ct);
        return new JsonObject { ["releaseId"] = releaseId, ["state"] = "removed" };
    }

    static (string scope, string release) Split(string releaseId)
    {
        var i = releaseId.LastIndexOf('@');
        if (i <= 0) throw new ServiceRefused(400, ErrorCodes.UnknownVersion, $"{releaseId} is not a release id (<workflow>/<target>@<release>)");
        return (releaseId[..i], releaseId[(i + 1)..]);
    }

    static Task<bool> Removed(StoreDbContext db, string releaseId, CancellationToken ct) =>
        db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == releaseId && f.Kind == ServiceAudit.Kind && f.Code == "removed", ct);

    /// <summary>
    /// The open payments and bulks per release: those whose status in the read model is not final. A payment pinned by its
    /// release id counts for it; one that names only its workflow counts for that workflow's release active when it was
    /// accepted (the store records what the engine gave it, Issue I-102).
    /// </summary>
    public async Task<Dictionary<string, int>> OpenPayments(CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var open = await o.Subjects.AsNoTracking().Where(s => (s.Kind == "bulk" || s.BulkId == null) && (s.Status == null || !Terminal.Contains(s.Status)))
            .Select(s => new { s.ReleaseId, s.AcceptedAt }).ToListAsync(ct);
        await using var db = StoreDbContext.Open(store);
        var releases = await db.Releases.AsNoTracking().Select(r => new { r.Id, r.Scope }).ToListAsync(ct);
        var acts = await db.Activations.AsNoTracking().ToListAsync(ct);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in open)
        {
            string? id = releases.Any(r => r.Id == s.ReleaseId) ? s.ReleaseId : null;
            if (id is null)
            {
                var a = acts.Where(x => x.Scope.Split('/')[0] == s.ReleaseId && x.EffectiveFrom <= s.AcceptedAt).OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.Id).FirstOrDefault();
                id = a is null ? s.ReleaseId : $"{a.Scope}@{a.Release}";
            }
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        // A distribution's open payments, exactly: pinned at acceptance, with no end fact (R-09-54).
        foreach (var (d, n) in await new DistributionRegistry(db, clock).OpenPayments(ct)) counts[d] = n;
        return counts;
    }

    /// <summary>The state of every installed release in the console's environment (section 9.12 of Part 9).</summary>
    public async Task<Dictionary<string, string>> States(CancellationToken ct = default, string? environment = null)
    {
        var env = environment ?? settings.Environment;
        var now = StoreTime.Of(clock.GetUtcNow());
        await using var db = StoreDbContext.Open(store);
        var releases = await db.Releases.AsNoTracking().Select(r => new { r.Id, r.Scope, r.Release }).ToListAsync(ct);
        var acts = await db.Activations.AsNoTracking().Where(a => a.Environment == env).ToListAsync(ct);
        var removed = await db.Facts.AsNoTracking().Where(f => f.Kind == ServiceAudit.Kind && f.Code == "removed").Select(f => f.SubjectId).ToListAsync(ct);
        var inForce = (await db.Facts.AsNoTracking().Where(f => f.Kind == DistributionRegistry.InForceKind).Select(f => f.SubjectId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var open = await OpenPayments(ct);
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        var dreg = new DistributionRegistry(db, clock);
        foreach (var r in releases)
        {
            if (r.Scope.StartsWith(DistributionRegistry.DistPrefix, StringComparison.Ordinal)) { states[r.Id] = await dreg.Refresh(r.Id, env, "service-console", ct) ?? "installed"; continue; }
            if (r.Scope.StartsWith(DistributionRegistry.ConfigPrefix, StringComparison.Ordinal)) { states[r.Id] = inForce.Contains(r.Id) ? "in-force" : "awaiting-consent"; continue; }
            var mine = acts.Where(a => a.Scope == r.Scope).ToList();
            var active = mine.Where(a => a.EffectiveFrom <= now).OrderByDescending(a => a.EffectiveFrom).ThenByDescending(a => a.Id).FirstOrDefault()?.Release;
            var was = mine.Any(a => a.Release == r.Release && a.EffectiveFrom <= now);
            var scheduled = mine.Any(a => a.Release == r.Release && a.EffectiveFrom > now);
            states[r.Id] = removed.Contains(r.Id) ? "removed"
                : active == r.Release ? "active"
                : was ? (open.GetValueOrDefault(r.Id) > 0 ? "draining" : "drained")
                : scheduled ? "scheduled" : "installed";
        }
        return states;
    }

    /// <summary>Every installed release with its state, open payments, verifications and service facts.</summary>
    public async Task<JsonArray> Releases(CancellationToken ct = default)
    {
        var states = await States(ct);
        var open = await OpenPayments(ct);
        await using var db = StoreDbContext.Open(store);
        var rows = await db.Releases.AsNoTracking().OrderBy(r => r.Scope).ThenBy(r => r.StoredAt).Select(r => new { r.Id, r.Scope, r.Release, r.ContentHash, r.StoredBy, r.StoredAt, r.ManifestJson }).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var facts = await db.Facts.AsNoTracking().Where(f => ids.Contains(f.SubjectId)).OrderBy(f => f.Seq).ToListAsync(ct);
        var acts = await db.Activations.AsNoTracking().Where(a => a.Environment == settings.Environment).ToListAsync(ct);
        return new JsonArray(rows.Select(r =>
        {
            var m = JsonNode.Parse(r.ManifestJson);
            return (JsonNode)new JsonObject
            {
                ["releaseId"] = r.Id, ["scope"] = r.Scope, ["release"] = r.Release,
                ["kind"] = r.Scope.StartsWith(DistributionRegistry.DistPrefix) ? ReleaseSignature.Distribution : r.Scope.StartsWith(DistributionRegistry.ConfigPrefix) ? ReleaseSignature.ConfigurationRelease : ReleaseSignature.WorkflowPackage,
                ["target"] = (string?)m?["target"] ?? (string?)m?["engine"], ["tenant"] = (string?)m?["tenant"] ?? (string?)m?["bank"],
                ["promotable"] = IsBundle(r.Id) || m?["promotable"]?.GetValue<bool>() == true && m?["containsSimulators"]?.GetValue<bool>() != true,
                ["contentHash"] = r.ContentHash, ["storedBy"] = r.StoredBy, ["storedAt"] = r.StoredAt.ToString("O"), ["state"] = states.GetValueOrDefault(r.Id, "installed"),
                ["openPayments"] = open.GetValueOrDefault(r.Id),
                ["longestWait"] = (string?)m?["longestWait"],
                ["activations"] = new JsonArray(acts.Where(a => a.Scope == r.Scope && a.Release == r.Release).Select(a => (JsonNode)new JsonObject { ["environment"] = a.Environment, ["effectiveFrom"] = a.EffectiveFrom.ToString("O"), ["by"] = a.Actor }).ToArray()),
                ["facts"] = new JsonArray(facts.Where(f => f.SubjectId == r.Id).Select(f => (JsonNode)ServiceAudit.Show(f)).ToArray()),
            };
        }).ToArray());
    }

    /// <summary>Verifies a stored package again against the signers trusted now (a troubleshooting action).</summary>
    public async Task<JsonObject> Reverify(string releaseId, ServiceIdentity who, CancellationToken ct = default)
    {
        var (scope, release) = Split(releaseId);
        await using var db = StoreDbContext.Open(store);
        var row = await new ReleaseRegistry(db, clock).Get(scope, release, ct) ?? throw new ServiceRefused(404, ErrorCodes.UnknownVersion, $"{releaseId} is not installed");
        if (IsBundle(releaseId))
        {
            var b = Bundle.FromZip(row.Package, releaseId.StartsWith(DistributionRegistry.DistPrefix) ? ReleaseSignature.Distribution : ReleaseSignature.ConfigurationRelease);
            try
            {
                var bv = b.Verify(await TrustedSigners.Load(settings.TrustSetting, settings.Secrets, ct));
                await new FactLog(db, clock).Append(releaseId, b.Kind == ReleaseSignature.Distribution ? "distribution" : "configuration", new NewFact("release.verified", "service-console:reverify", new JsonObject { ["keyId"] = bv.KeyId, ["statementSha256"] = bv.StatementSha256, ["files"] = bv.Files }, releaseId, who.Actor), ct);
                return new JsonObject { ["releaseId"] = releaseId, ["verified"] = true, ["keyId"] = bv.KeyId };
            }
            catch (SignatureRejected e) { throw new ServiceRefused(422, ErrorCodes.Of(e), $"{releaseId} no longer verifies: {e.Message}"); }
        }
        var p = Package.FromZip(row.Package);
        try
        {
            var v = p.VerifySignature(await TrustedSigners.Load(settings.TrustSetting, settings.Secrets, ct));
            await new FactLog(db, clock).Append(releaseId, "release", new NewFact("release.verified", "service-console:reverify", new JsonObject { ["keyId"] = v.KeyId, ["statementSha256"] = v.StatementSha256, ["files"] = v.Files }, releaseId, who.Actor), ct);
            return new JsonObject { ["releaseId"] = releaseId, ["verified"] = true, ["keyId"] = v.KeyId };
        }
        catch (SignatureRejected e) { throw new ServiceRefused(422, ErrorCodes.Of(e), $"{releaseId} no longer verifies: {e.Message}"); }
    }
}
