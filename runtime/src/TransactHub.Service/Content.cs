using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Api;
using TransactHub.Operations;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Service;

/// <summary>How the service console reaches the content it installs, and what it trusts.</summary>
public sealed record ContentSettings(string TrustSetting, ISecretResolver Secrets, IReadOnlySet<string> TestEnvironments, string Environment);

/// <summary>
/// Content install, update and upgrade (spec R-09-40, docs/runtime/PACKAGING-AND-DEPLOYMENT.md). GitHub is the artefact
/// repository, read only, through <see cref="IPackageSource"/>: every version of every kind is listed; a workflow package is
/// downloaded, verified against the trusted signers and stored exactly as the release API stores an upload; activation
/// from a date moves the previous release to draining; rollback activates the previous release again; removal is refused
/// while the release is active or a payment pinned to it is open. A distribution or a configuration release is listed and
/// refused at install with the reason (Issue I-102). Every action is a fact (<see cref="ServiceAudit"/>).
/// </summary>
public sealed class ContentService(StoreOptions store, StoreOptions ops, IPackageSource? source, ContentSettings settings, ServiceAudit audit, ErrorDictionary dictionary, TimeProvider clock)
{
    /// <summary>ISO 20022 statuses a payment does not leave. The store holds no end of a payment yet (Issue I-97).</summary>
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
        return new JsonArray(versions.Select(v =>
        {
            var installable = v.Kind == ReleaseSignature.WorkflowPackage;
            return (JsonNode)new JsonObject
            {
                ["tag"] = v.Tag, ["kind"] = v.Kind, ["workflow"] = v.Workflow, ["release"] = v.Release, ["target"] = v.Target, ["commit"] = v.Commit,
                ["releaseId"] = v.ReleaseId, ["state"] = states.TryGetValue(v.ReleaseId, out var s) ? s : "offered", ["installable"] = installable,
                ["notInstallable"] = installable ? null : $"Installing a {v.Kind} is not built yet: install the workflow packages it is built from (Issue I-102)",
            };
        }).ToArray());
    }

    /// <summary>Downloads a version and checks it as an install would, storing nothing.</summary>
    public async Task<(Package package, Verification verification, PublishedVersion version)> Check(string tag, CancellationToken ct = default)
    {
        var v = await Version(tag, ct);
        if (v.Kind != ReleaseSignature.WorkflowPackage)
            throw new ServiceRefused(501, ErrorCodes.KindNotInstallable, $"{tag} is a {v.Kind}; installing one is not built yet (Issue I-102, docs/runtime/PACKAGING-AND-DEPLOYMENT.md)");
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
        try { return (p, p.VerifySignature(await TrustedSigners.Load(settings.TrustSetting, settings.Secrets, ct)), v); }
        catch (SignatureRejected e) { throw new ServiceRefused(422, ErrorCodes.Of(e), $"{p.ReleaseId} is refused: {e.Message}"); }
    }

    /// <summary>Verifies a version on GitHub against the trusted signers; the outcome, either way, is a fact.</summary>
    public async Task<JsonObject> Verify(string tag, ServiceIdentity who, string? why, CancellationToken ct = default)
    {
        try
        {
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
        Package p; Verification v;
        try { (p, v, _) = await Check(tag, ct); }
        catch (ServiceRefused e)
        {
            await audit.Record("install", who, why, new JsonObject { ["tag"] = tag, ["result"] = "refused", ["error"] = e.Code, ["message"] = e.Message }, ct: ct);
            throw;
        }
        await using var db = StoreDbContext.Open(store);
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

    /// <summary>The difference between a version and what is active for its scope: artefacts, versions, parameters.</summary>
    public async Task<JsonObject> Diff(string tag, string? environment = null, CancellationToken ct = default)
    {
        var env = environment ?? settings.Environment;
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
        var (scope, release) = Split(releaseId);
        await using var db = StoreDbContext.Open(store);
        var reg = new ReleaseRegistry(db, clock);
        var row = await reg.Get(scope, release, ct) ?? throw new ServiceRefused(404, ErrorCodes.UnknownVersion, $"{releaseId} is not installed; install it first");
        if (await Removed(db, releaseId, ct)) throw new ServiceRefused(409, ErrorCodes.RemovalRefused, $"{releaseId} was removed; install it again before activating it");
        var p = Package.FromZip(row.Package);
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

    /// <summary>Rollback: the release that was active before the current one, activated again now.</summary>
    public async Task<JsonObject> Rollback(string scope, string? environment, ServiceIdentity who, string why, CancellationToken ct = default)
    {
        var env = environment ?? settings.Environment;
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
    public async Task<JsonObject> Remove(string releaseId, ServiceIdentity who, string why, CancellationToken ct = default)
    {
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
        var open = await OpenPayments(ct);
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in releases)
        {
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
                ["releaseId"] = r.Id, ["scope"] = r.Scope, ["release"] = r.Release, ["kind"] = ReleaseSignature.WorkflowPackage, ["target"] = (string?)m?["target"], ["tenant"] = (string?)m?["tenant"],
                ["promotable"] = m?["promotable"]?.GetValue<bool>() == true && m?["containsSimulators"]?.GetValue<bool>() != true,
                ["contentHash"] = r.ContentHash, ["storedBy"] = r.StoredBy, ["storedAt"] = r.StoredAt.ToString("O"), ["state"] = states.GetValueOrDefault(r.Id, "installed"),
                ["openPayments"] = open.GetValueOrDefault(r.Id),
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
