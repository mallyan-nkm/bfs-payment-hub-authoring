using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TransactHub.Api;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Service;

/// <summary>Where a backup's parts come from and how it is signed and trusted.</summary>
/// <param name="SigningKeyRef">The backup signing key, by reference (TH_BACKUP_SIGNING_KEY): a P-256 private key in PEM.</param>
/// <param name="TrustSetting">The keys whose backups this runtime restores (Service:Backup:TrustedSigners, default TH_TRUSTED_SIGNERS).</param>
/// <param name="RestoreDir">Where a restore writes what lives outside the store: the role map and the trusted signers.</param>
public sealed record BackupSettings(string Installation, string? SigningKeyRef, string TrustSetting, string? RoleMapSetting, string? RestoreDir);

/// <summary>
/// A signed, verifiable configuration backup (spec R-09-41): the releases' ids (the packages stay in GitHub and are installed
/// again from it on restore, verified), the activations, the configuration values, the bindings (a credential only as a
/// <c>secretRef</c>, never a value), the role map and the trusted signers. It is signed as a package is (release.json and
/// release.sig, kind <c>runtime-backup</c>). A restore verifies it, offers a dry run, and goes only into an empty runtime
/// or a test environment. A backup holds the runtime's own bank only (R-09-76, R-09-116): its releases, its values and the
/// bindings of its configuration releases in force, read from the store, never a folder of every bank's bindings; so a backup
/// restores into its own runtime, and another bank's runtime refuses it.
/// </summary>
public sealed class BackupService(StoreOptions store, ContentService content, ContentSettings contentSettings, BackupSettings settings, ISecretResolver secrets, ServiceAudit audit, TimeProvider clock)
{
    public const string Kind = "runtime-backup";
    public const string File = "backup.json";

    static readonly Regex SecretKey = new("(password|passphrase|secret|token|apikey|accesskey|privatekey|clientsecret|credential)$", RegexOptions.IgnoreCase);

    /// <summary>A key that names a secret must hold a reference or nothing (as check L37 asks of the branch).</summary>
    public static IEnumerable<string> LiteralSecrets(JsonNode? n, string at = "")
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    var here = at.Length == 0 ? k : $"{at}.{k}";
                    if ((SecretKey.IsMatch(k.Replace("_", "").Replace("-", "")) || k == "secretRef") && v is JsonValue s && s.TryGetValue<string>(out var str) && str.Length > 0 && !SecretRef.IsRef(str))
                        yield return here;
                    foreach (var x in LiteralSecrets(v, here)) yield return x;
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) foreach (var x in LiteralSecrets(a[i], $"{at}[{i}]")) yield return x;
                break;
        }
    }

    async Task<JsonNode?> Resolve(string? setting, CancellationToken ct) =>
        setting is null ? null : JsonNode.Parse(setting.TrimStart().StartsWith('{') ? setting : (await secrets.Resolve(setting, ct)).Reveal());

    /// <summary>What the runtime holds now, as a backup document (unsigned).</summary>
    public async Task<JsonObject> Snapshot(CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var own = contentSettings.Bank;
        // The runtime's own bank only (R-09-116): a release that names another bank, or a value of another tenant, is not taken.
        var releases = (await db.Releases.AsNoTracking().OrderBy(r => r.Id).Select(r => new { r.Id, r.Scope, r.Release, r.ContentHash, r.ManifestJson }).ToListAsync(ct))
            .Where(r => own is null || BankOfRelease(r.Scope, (string?)JsonNode.Parse(r.ManifestJson)?["tenant"]) is not { Length: > 0 } b || own.Serves(b)).ToList();
        var acts = await db.Activations.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        var ids = releases.Select(r => $"{r.Scope}@{r.Release}").ToHashSet(StringComparer.Ordinal);
        acts = acts.Where(a => ids.Contains($"{a.Scope}@{a.Release}")).ToList();
        var values = (await db.VersionedValues.AsNoTracking().OrderBy(v => v.Kind).ThenBy(v => v.Tenant).ThenBy(v => v.Name).ThenBy(v => v.VersionNo).ToListAsync(ct))
            .Where(v => own is null || own.Serves(v.Tenant)).ToList();
        // The bindings of the bank's configuration releases in force (R-09-112), as the store holds them: a credential only by reference.
        var bindings = new JsonArray();
        foreach (var b in ConfigurationBindings.Load(store, own?.Id).OrderBy(x => x.Tenant).ThenBy(x => x.Port).ThenBy(x => x.ValidFrom))
            bindings.Add(new JsonObject
            {
                ["tenant"] = b.Tenant, ["port"] = b.Port, ["adapter"] = b.Adapter, ["validFrom"] = b.ValidFrom.ToString("O"), ["configurationRelease"] = b.Release,
                ["settings"] = new JsonObject(b.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            });
        var doc = new JsonObject
        {
            ["kind"] = Kind, ["format"] = 1, ["installation"] = settings.Installation, ["environment"] = contentSettings.Environment,
            ["releases"] = new JsonArray(releases.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["scope"] = r.Scope, ["release"] = r.Release, ["contentHash"] = r.ContentHash, ["tenant"] = (string?)JsonNode.Parse(r.ManifestJson)?["tenant"] }).ToArray()),
            ["activations"] = new JsonArray(acts.Select(a => (JsonNode)new JsonObject { ["environment"] = a.Environment, ["scope"] = a.Scope, ["release"] = a.Release, ["effectiveFrom"] = a.EffectiveFrom.ToString("O"), ["actor"] = a.Actor }).ToArray()),
            ["values"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject
            {
                ["kind"] = v.Kind, ["tenant"] = v.Tenant, ["name"] = v.Name, ["versionNo"] = v.VersionNo, ["effectiveFrom"] = v.EffectiveFrom.ToString("O"),
                ["effectiveTo"] = v.EffectiveTo?.ToString("O"), ["body"] = JsonNode.Parse(v.BodyJson), ["publishedBy"] = v.PublishedBy,
            }).ToArray()),
            ["bindings"] = bindings,
            ["roleMap"] = await Resolve(settings.RoleMapSetting, ct),
            ["trustedSigners"] = await Resolve(contentSettings.TrustSetting, ct),
        };
        var literal = LiteralSecrets(doc).ToList();
        if (literal.Count > 0) throw new ServiceRefused(422, ErrorCodes.BackupInvalid, $"The configuration holds a secret as a value, not a reference, at {string.Join(", ", literal.Take(5))}: it is not backed up (R-06-22)");
        return doc;
    }

    /// <summary>The signed backup: a zip of backup.json, release.json and release.sig.</summary>
    public async Task<byte[]> Export(ServiceIdentity who, string why, CancellationToken ct = default)
    {
        if (settings.SigningKeyRef is not { Length: > 0 } keyRef) throw new ServiceRefused(503, ErrorCodes.BackupInvalid, "No backup signing key is configured (TH_BACKUP_SIGNING_KEY, a secret reference)");
        var doc = await Snapshot(ct);
        var now = clock.GetUtcNow();
        doc["createdAt"] = now.ToString("O");
        var files = new Dictionary<string, byte[]> { [File] = Encoding.UTF8.GetBytes(doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })) };
        using var key = ReleaseSignature.PrivateKeyFromPem(await secrets.Resolve(keyRef, ct));
        var id = $"backup/{settings.Installation}/{now:yyyyMMddTHHmmssfffZ}";
        var (st, sig) = ReleaseSignature.Sign(files, new ReleaseClaims(id, "backup", "1", "", null, null, Kind), key, now);
        files[ReleaseSignature.StatementFile] = st;
        files[ReleaseSignature.SignatureFile] = sig;
        await audit.Record("backup", who, why, new JsonObject { ["backup"] = id, ["keyId"] = ReleaseSignature.KeyIdOf(key), ["releases"] = ((JsonArray)doc["releases"]!).Count, ["values"] = ((JsonArray)doc["values"]!).Count }, ct: ct);
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var e = z.CreateEntry(path, CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(bytes);
            }
        return ms.ToArray();
    }

    /// <summary>Verifies a backup and returns its document; refuses one unsigned, changed, or signed by a key not trusted here.</summary>
    public async Task<(JsonObject doc, Verification v)> Verify(byte[] zip, CancellationToken ct = default)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            // Read within the limits whatever the headers claim: a zip bomb is refused (R-09-59).
            foreach (var (name, bytes) in SafeZip.Read(zip)) files[name] = bytes;
        }
        catch (InvalidDataException) { throw new ServiceRefused(422, ErrorCodes.BackupInvalid, "The backup is not a zip"); }
        if (!files.TryGetValue(File, out var body)) throw new ServiceRefused(422, ErrorCodes.BackupInvalid, $"The backup has no {File}");
        string? id = null;
        if (files.TryGetValue(ReleaseSignature.StatementFile, out var st)) { try { id = (string?)JsonNode.Parse(st)?["releaseId"]; } catch { /* reported below */ } }
        Verification v;
        try { v = ReleaseSignature.Verify(files, new ReleaseExpectation(id ?? "", null, Kind), await TrustedSigners.Load(settings.TrustSetting, secrets, ct)); }
        catch (SignatureRejected e) { throw new ServiceRefused(422, ErrorCodes.BackupInvalid, $"The backup does not verify: {e.Message}"); }
        var doc = JsonNode.Parse(body) as JsonObject;
        if ((string?)doc?["kind"] != Kind) throw new ServiceRefused(422, ErrorCodes.BackupInvalid, $"{File} is not a {Kind}");
        var literal = LiteralSecrets(doc).ToList();
        if (literal.Count > 0) throw new ServiceRefused(422, ErrorCodes.BackupInvalid, $"The backup holds a secret as a value at {string.Join(", ", literal.Take(5))}");
        return (doc!, v);
    }

    /// <summary>
    /// Restores a backup: verified first; with <paramref name="dryRun"/>, only the plan. Refused into a runtime that already holds
    /// releases, activations or values unless its environment is a test one. Each release is installed again from the package
    /// source and must have the content hash the backup names.
    /// </summary>
    public async Task<JsonObject> Restore(byte[] zip, bool dryRun, ServiceIdentity who, string why, CancellationToken ct = default)
    {
        var (doc, v) = await Verify(zip, ct);
        await using var db = StoreDbContext.Open(store);
        var empty = !await db.Releases.AnyAsync(ct) && !await db.Activations.AnyAsync(ct) && !await db.VersionedValues.AnyAsync(ct);
        var test = contentSettings.TestEnvironments.Contains(contentSettings.Environment);
        var problems = new JsonArray();
        if (!empty && !test) problems.Add($"This runtime ({contentSettings.Environment}) is not empty and not a test environment: a restore goes only into one that is");
        // A backup of another bank's runtime is never restored here (R-09-76): every release, value and binding is this bank's.
        if (contentSettings.Bank is { } own)
        {
            static string? BankOf(JsonObject r) => BankOfRelease((string?)r["scope"], (string?)r["tenant"]);
            var others = (doc["releases"] as JsonArray ?? []).OfType<JsonObject>().Select(BankOf)
                .Concat((doc["values"] as JsonArray ?? []).OfType<JsonObject>().Select(x => (string?)x["tenant"]))
                .Concat((doc["bindings"] as JsonArray ?? []).OfType<JsonObject>().Select(x => (string?)x["tenant"]))
                .Where(b => b is { Length: > 0 } && !own.Serves(b)).Distinct().Order(StringComparer.Ordinal).ToList();
            if (others.Count > 0) problems.Add($"The backup holds content of {string.Join(", ", others)}, but this runtime serves {own.Id} only (TH_BANK): it is not restored (R-09-76)");
        }
        var versions = (await content.Versions(ct)).OfType<JsonObject>().ToList();
        var plan = new JsonArray();
        foreach (var r in (doc["releases"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = (string)r["id"]!;
            var tag = versions.FirstOrDefault(x => (string?)x["releaseId"] == id);
            var have = await db.Releases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (have is not null && have.ContentHash != (string?)r["contentHash"]) problems.Add($"{id} is installed here with other content");
            else if (have is null && tag is null) problems.Add($"{id} is not offered by the package source: it cannot be installed again");
            plan.Add(new JsonObject { ["release"] = id, ["action"] = have is not null ? "already installed" : "install", ["tag"] = (string?)tag?["tag"] });
        }
        var acts = (doc["activations"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var values = (doc["values"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var currentRoles = await Resolve(settings.RoleMapSetting, ct);
        var currentTrust = await Resolve(contentSettings.TrustSetting, ct);
        var result = new JsonObject
        {
            ["backup"] = (string?)JsonNode.Parse(Encoding.UTF8.GetString(ZipEntry(zip, ReleaseSignature.StatementFile)))?["releaseId"], ["signedBy"] = v.KeyId, ["dryRun"] = dryRun,
            ["target"] = new JsonObject { ["environment"] = contentSettings.Environment, ["empty"] = empty, ["test"] = test },
            ["releases"] = plan, ["activations"] = acts.Count, ["values"] = values.Count, ["bindings"] = (doc["bindings"] as JsonArray)?.Count ?? 0,
            ["roleMapDiffers"] = CanonicalJson.Of(currentRoles) != CanonicalJson.Of(doc["roleMap"]),
            ["trustedSignersDiffer"] = CanonicalJson.Of(currentTrust) != CanonicalJson.Of(doc["trustedSigners"]),
            ["problems"] = problems,
        };
        if (dryRun || problems.Count > 0)
        {
            await audit.Record(dryRun ? "restore.dry-run" : "restore.refused", who, why, new JsonObject { ["backup"] = (string?)result["backup"], ["problems"] = problems.DeepClone() }, ct: ct);
            if (!dryRun) throw new ServiceRefused(409, ErrorCodes.RestoreRefused, string.Join("; ", problems.Select(p => (string?)p)));
            return result;
        }
        // A configuration release is checked against the distributions installed: they go in first.
        foreach (var p in plan.OfType<JsonObject>().Where(p => (string?)p["action"] == "install").OrderBy(p => ((string?)p["release"] ?? "").StartsWith(DistributionRegistry.ConfigPrefix, StringComparison.Ordinal) ? 1 : 0))
            await content.Install((string)p["tag"]!, who, $"restore: {why}", ct);
        var reg = new ReleaseRegistry(db, clock);
        foreach (var a in acts)
        {
            var from = DateTimeOffset.Parse((string)a["effectiveFrom"]!, System.Globalization.CultureInfo.InvariantCulture);
            var env = (string)a["environment"]!;
            var scope = (string)a["scope"]!;
            var release = (string)a["release"]!;
            if (await db.Activations.AnyAsync(x => x.Environment == env && x.Scope == scope && x.Release == release && x.EffectiveFrom == StoreTime.Of(from), ct)) continue;
            await reg.Activate(env, scope, release, from, (string?)a["actor"] ?? who.Actor, ct);
        }
        var vv = new VersionedValues(db, clock);
        foreach (var x in values)
            await vv.Publish((string)x["kind"]!, (string)x["tenant"]!, (string)x["name"]!, x["body"]!.DeepClone(), DateTimeOffset.Parse((string)x["effectiveFrom"]!, System.Globalization.CultureInfo.InvariantCulture),
                (string?)x["effectiveTo"] is { } to ? DateTimeOffset.Parse(to, System.Globalization.CultureInfo.InvariantCulture) : null, (string?)x["publishedBy"] ?? who.Actor, ct);
        // The bindings are values of the store (kind binding), restored above with the rest. What lives outside the store is
        // written for the deployment to take: never applied to the running host behind its back.
        if (settings.RestoreDir is { Length: > 0 } outDir)
        {
            Directory.CreateDirectory(outDir);
            if (doc["roleMap"] is { } rm) await System.IO.File.WriteAllTextAsync(Path.Combine(outDir, "role-map.json"), rm.ToJsonString(), ct);
            if (doc["trustedSigners"] is { } ts) await System.IO.File.WriteAllTextAsync(Path.Combine(outDir, "trusted-signers.json"), ts.ToJsonString(), ct);
            result["written"] = outDir;
        }
        await audit.Record("restore", who, why, new JsonObject { ["backup"] = (string?)result["backup"], ["releases"] = plan.Count, ["activations"] = acts.Count, ["values"] = values.Count }, ct: ct);
        result["restored"] = true;
        return result;
    }

    /// <summary>The bank a stored release is of: a distribution's or a configuration release's by its scope, a workflow package's by its tenant.</summary>
    static string? BankOfRelease(string? scope, string? tenant) => scope is { } sc && (sc.StartsWith(DistributionRegistry.DistPrefix, StringComparison.Ordinal) || sc.StartsWith(DistributionRegistry.ConfigPrefix, StringComparison.Ordinal))
        ? sc[(sc.IndexOf('/') + 1)..] : tenant;

    /// <summary>Keeps a backup's bytes in the store, insert-only by their hash, while a restore waits for a bank approver's consent (R-09-77).</summary>
    public async Task<string> Keep(byte[] zip, CancellationToken ct = default)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip));
        await using var db = StoreDbContext.Open(store);
        if (!await db.FileContents.AsNoTracking().AnyAsync(x => x.ContentHash == hash, ct))
        {
            db.FileContents.Add(new FileContentRow { ContentHash = hash, Content = zip, At = StoreTime.Of(clock.GetUtcNow()) });
            await db.SaveChangesAsync(ct);
        }
        return hash;
    }

    /// <summary>The bytes of a backup kept by <see cref="Keep"/>, checked against their hash.</summary>
    public async Task<byte[]> Kept(string hash, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var row = await db.FileContents.AsNoTracking().FirstOrDefaultAsync(x => x.ContentHash == hash, ct) ?? throw new ServiceRefused(404, ErrorCodes.BackupInvalid, $"No backup {hash} is kept");
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(row.Content)) != hash) throw new ServiceRefused(409, ErrorCodes.BackupInvalid, $"The backup kept as {hash} does not match its hash");
        return row.Content;
    }

    static byte[] ZipEntry(byte[] zip, string name)
    {
        using var a = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var s = a.GetEntry(name)!.Open();
        return SafeZip.ReadAtMost(s, SafeZip.MaxEntryBytes, name);
    }
}
