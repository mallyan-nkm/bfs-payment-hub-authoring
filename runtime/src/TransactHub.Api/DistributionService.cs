using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Api;

/// <summary>
/// Installing and running a bank's distributions and configuration releases (docs/runtime/PACKAGING-AND-DEPLOYMENT.md,
/// spec R-09-48 to R-09-57), shared by the release API and the service console. Every bundle is verified against the
/// signers trusted now before it is stored (signature, kind, release id, tenant, every file, the signed bill of materials and
/// parameter contract), and a distribution again before it is activated. A configuration release is checked against the
/// parameter contract of every distribution of its bank not removed, and its values, port bindings and calendars become
/// effective-dated versions.
/// </summary>
public sealed class DistributionService(StoreDbContext db, TimeProvider clock, Func<CancellationToken, Task<TrustedSigners>> trust, IReadOnlySet<string>? testEnvironments = null, RuntimeBank? bank = null)
{
    /// <summary>The one bank this runtime serves (R-09-76): another bank's bundle, distribution or payment is refused. Null only in a library test.</summary>
    public RuntimeBank? Bank => bank;

    void OwnBank(string? b, string what) => bank?.Check(b, what);

    DistributionRegistry Registry => new(db, clock);
    public const string BindingKind = "binding";
    public const string CalendarKind = "calendar";


    async Task<Verification> VerifyBundle(Bundle b, CancellationToken ct)
    {
        try { return b.Verify(await trust(ct)); }
        catch (SignatureRejected e) { throw new BundleRefused(422, $"{b.ReleaseId} is refused: {e.Message}"); }
    }

    async Task VerifiedFact(Bundle b, Verification v, string purpose, string actor, CancellationToken ct) =>
        await new FactLog(db, clock).Append(b.ReleaseId, b.Kind == ReleaseSignature.Distribution ? "distribution" : "configuration", new NewFact("release.verified", purpose, new JsonObject
        {
            ["keyId"] = v.KeyId, ["statementSha256"] = v.StatementSha256, ["changeSet"] = v.ChangeSet, ["signedAt"] = v.SignedAt.ToString("O"), ["files"] = v.Files, ["contentHash"] = b.ContentHash, ["kind"] = b.Kind,
        }, b.ReleaseId, actor), ct);

    /// <summary>Verifies a distribution and stores it insert-only; it is then installed in every environment.</summary>
    public async Task<JsonObject> InstallDistribution(Bundle b, string actor, string purpose, CancellationToken ct = default)
    {
        if (b.Kind != ReleaseSignature.Distribution) throw new BundleRefused(422, $"{b.ReleaseId} is a {b.Kind}, not a distribution");
        OwnBank(b.Bank, b.ReleaseId);
        var v = await VerifyBundle(b, ct);
        StoreResult r;
        try { r = await Registry.Store(b.Bank, b.Release, b.CanonicalZip(), b.Manifest, b.ContentHash, actor, purpose, ct); }
        catch (ReleaseConflict e) { throw new BundleRefused(409, e.Message); }
        catch (DistributionRefused e) { throw new BundleRefused(422, e.Message); }
        if (r.Stored) await VerifiedFact(b, v, purpose, actor, ct);
        return new JsonObject
        {
            ["id"] = b.ReleaseId, ["kind"] = b.Kind, ["bank"] = b.Bank, ["distribution"] = b.Release, ["engine"] = b.Engine, ["stored"] = r.Stored,
            ["hash"] = b.ContentHash, ["signedBy"] = v.KeyId, ["changeSet"] = v.ChangeSet, ["state"] = "installed",
        };
    }

    /// <summary>The stored distribution, as stored.</summary>
    public async Task<Bundle> Stored(string dist, CancellationToken ct = default)
    {
        var (bank, id) = SplitDist(dist);
        var row = await new ReleaseRegistry(db, clock).Get(DistributionRegistry.Scope(bank), id, ct) ?? throw new BundleRefused(404, $"{dist} is not installed");
        return Bundle.FromZip(row.Package, ReleaseSignature.Distribution);
    }

    static (string, string) SplitDist(string dist)
    {
        try { return DistributionRegistry.Split(dist); }
        catch (DistributionRefused e) { throw new BundleRefused(400, e.Message); }
    }

    async Task<T> Guard<T>(Func<Task<T>> f)
    {
        try { return await f(); }
        catch (DistributionRefused e) { throw new BundleRefused(409, e.Message); }
        catch (ActivationRaced e) { throw new BundleRefused(409, e.Message, e.Code); }
    }

    /// <summary>Activation (the switch-over): verified again against the signers trusted now; the one active until now drains.</summary>
    public async Task<JsonObject> Activate(string dist, string environment, string actor, string why, CancellationToken ct = default)
    {
        OwnBank(SplitDist(dist).Item1, dist);
        var b = await Stored(dist, ct);
        // The signed manifest's bank, not only the id: a distribution stored before the runtime had its bank is refused too.
        OwnBank(b.Bank, dist);
        var v = await VerifyBundle(b, ct);
        // A distribution that carries simulators is for a test environment only (R-07-12).
        if (b.Manifest["promotable"] is JsonValue pv && pv.TryGetValue<bool>(out var promotable) && !promotable && !(testEnvironments ?? new HashSet<string> { "test", "dev", "kit" }).Contains(environment))
            throw new BundleRefused(409, $"{dist} carries simulators: it may be activated only in {string.Join(", ", testEnvironments ?? new HashSet<string> { "test", "dev", "kit" })}");
        await VerifiedFact(b, v, $"activation:{environment}", actor, ct);
        var (active, draining) = await Guard(() => Registry.Activate(dist, environment, actor, why, ct));
        return new JsonObject { ["active"] = active, ["draining"] = draining, ["environment"] = environment };
    }

    public async Task<JsonObject> Drain(string dist, string environment, string actor, string why, CancellationToken ct = default)
    {
        OwnBank(SplitDist(dist).Item1, dist);
        await Guard(async () => { await Registry.Drain(dist, environment, actor, why, ct); return 0; });
        return new JsonObject { ["draining"] = dist, ["environment"] = environment };
    }

    public async Task<JsonObject> Rollback(string bank, string environment, string actor, string why, CancellationToken ct = default)
    {
        OwnBank(bank, $"A rollback of {bank}");
        var current = await Registry.Active(bank, environment, ct);
        var acts = await db.Activations.AsNoTracking().Where(a => a.Environment == environment && a.Scope == DistributionRegistry.Scope(bank)).OrderByDescending(a => a.Id).Select(a => a.Release).ToListAsync(ct);
        var previous = acts.FirstOrDefault(r => current is null || DistributionRegistry.IdOf(bank, r) != current);
        if (current is null || previous is null) throw new BundleRefused(409, current is null ? $"No distribution of {bank} is active in {environment}" : $"{bank} has no earlier distribution in {environment} to roll back to");
        var r = await Activate(DistributionRegistry.IdOf(bank, previous), environment, actor, $"rollback: {why}", ct);
        r["rolledBackFrom"] = current;
        return r;
    }

    public async Task<JsonObject> Remove(string dist, string environment, string actor, string why, CancellationToken ct = default)
    {
        await Guard(async () => { await Registry.Remove(dist, environment, actor, why, ct); return 0; });
        return new JsonObject { ["removed"] = dist, ["environment"] = environment, ["state"] = "removed" };
    }

    public async Task<string> Pin(string paymentId, string bank, string environment, string actor, CancellationToken ct = default)
    {
        OwnBank(bank, $"Payment {paymentId}");
        return await Guard(() => Registry.Pin(paymentId, bank, environment, actor, ct));
    }

    public async Task<bool> End(string paymentId, string outcome, string actor, CancellationToken ct = default) =>
        await Guard(() => Registry.End(paymentId, outcome, actor, ct));

    /// <summary>Every distribution of a bank (or every bank) with its state per environment and its open payments.</summary>
    public async Task<JsonArray> List(string? bank, string environment, CancellationToken ct = default)
    {
        var reg = Registry;
        var open = await reg.OpenPayments(ct);
        var banks = bank is null ? await reg.Banks(ct) : [bank];
        var list = new JsonArray();
        foreach (var bk in banks)
            foreach (var d in await reg.Of(bk, ct))
            {
                var state = await reg.Refresh(d, environment, "runtime", ct);
                var (_, id) = DistributionRegistry.Split(d);
                var row = await new ReleaseRegistry(db, clock).Get(DistributionRegistry.Scope(bk), id, ct);
                var m = JsonNode.Parse(row!.ManifestJson);
                list.Add(new JsonObject
                {
                    ["id"] = d, ["bank"] = bk, ["distribution"] = id, ["engine"] = (string?)m?["engine"], ["state"] = state, ["environment"] = environment,
                    ["openPayments"] = open.GetValueOrDefault(d), ["hash"] = row.ContentHash, ["storedAt"] = row.StoredAt.ToString("O"), ["storedBy"] = row.StoredBy,
                    ["longestWait"] = (string?)m?["longestWait"], ["schemes"] = m?["schemes"]?.DeepClone(), ["changeSets"] = m?["changeSets"]?.DeepClone(),
                    ["states"] = new JsonArray((await db.Facts.AsNoTracking().Where(f => f.SubjectId == d && f.Kind == DistributionRegistry.StateKind).OrderBy(f => f.Seq).ToListAsync(ct))
                        .Select(f => (JsonNode)new JsonObject { ["state"] = f.Code, ["at"] = f.At.ToString("O"), ["by"] = f.Actor, ["detail"] = JsonNode.Parse(f.DataJson) }).ToArray()),
                });
            }
        return list;
    }

    /// <summary>The parameter contracts a configuration release of a bank is checked against: those of every distribution not removed.</summary>
    public async Task<Dictionary<string, JsonNode?>> Contracts(string bank, CancellationToken ct = default)
    {
        var reg = Registry;
        var d = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var dist in await reg.Of(bank, ct))
        {
            var states = await reg.StatesOf(dist, ct);
            if (states.Count > 0 && states.Values.All(s => s.State == "removed")) continue;
            var (_, id) = DistributionRegistry.Split(dist);
            var row = await new ReleaseRegistry(db, clock).Get(DistributionRegistry.Scope(bank), id, ct);
            d[dist] = JsonNode.Parse(row!.ManifestJson)?["parameterContract"]?.DeepClone();
        }
        return d;
    }

    /// <summary>
    /// Checks a configuration release as an install would and puts nothing in force: its contract versions matched, every value
    /// against the parameter contract of every distribution of the bank not removed (an unknown value or one out of bounds is
    /// refused), its port bindings by secret reference only. Returns the distributions it was checked against.
    /// </summary>
    public async Task<Dictionary<string, JsonNode?>> CheckConfiguration(Bundle b, CancellationToken ct = default)
    {
        var contracts = await Contracts(b.Bank, ct);
        if (contracts.Count == 0) throw new BundleRefused(409, $"{b.ReleaseId} is refused: {b.Bank} has no installed distribution whose parameter contract it could be checked against");
        var problems = new List<string>();
        foreach (var req in b.Manifest["requiresContract"] as JsonArray ?? [])
        {
            var dist = (string?)req?["distribution"];
            if (dist is not null && contracts.TryGetValue(dist, out var c) && "sha256:" + ReleaseSignature.Sha256Hex(Encoding.UTF8.GetBytes(CanonicalJson.Of(c))) != (string?)req?["sha256"])
                problems.Add($"it was checked against another parameter contract of {dist}");
        }
        if (SetText(b) is { } setText) problems.AddRange(ParameterContract.Check(BankValues.ValuesOf(setText), contracts));
        foreach (var bd in Bindings(b))
        {
            if ((string?)bd?["secretRef"] is { } sr && !SecretRef.IsRef(sr)) problems.Add($"the binding of {(string?)bd?["port"]} holds a secret that is not a reference");
            foreach (var (k, val) in bd?["settings"] as JsonObject ?? [])
                if (System.Text.RegularExpressions.Regex.IsMatch(k, "(?i)(password|passphrase|secret|token|apikey|accesskey|privatekey|credential)$") && !SecretRef.IsRef((string?)val))
                    problems.Add($"the binding of {(string?)bd?["port"]} holds {k} as a value, not a secretRef");
            if ((string?)bd?["validFrom"] is null) problems.Add($"the binding of {(string?)bd?["port"]} has no validFrom");
        }
        if (problems.Count > 0) throw new BundleRefused(422, $"{b.ReleaseId} is refused: {string.Join("; ", problems)}");
        return contracts;
    }

    static string? SetText(Bundle b) => b.Files.TryGetValue("configuration/configuration-set.yaml", out var sb) ? Encoding.UTF8.GetString(sb) : null;
    static JsonArray Bindings(Bundle b) => b.Yaml("bindings/port-bindings.yaml")?["bindings"] as JsonArray ?? [];

    /// <summary>
    /// Installs a configuration release (R-09-51, R-09-110): verified against the signers trusted now, checked against the
    /// parameter contract of every distribution of the bank not removed (<see cref="CheckConfiguration"/>), and stored
    /// insert-only. Its values, port bindings, calendars and reference tables are put in force only when
    /// <paramref name="putInForce"/>: the caller has a bank approver's consent, or the runtime is an environment where its
    /// pipeline may change the system by itself (R-09-78, R-09-111). Otherwise the release waits, installed and not in force,
    /// for <see cref="PutConfigurationInForce"/>.
    /// </summary>
    public async Task<JsonObject> InstallConfiguration(Bundle b, string actor, string purpose, bool putInForce, CancellationToken ct = default, JsonObject? consent = null)
    {
        if (b.Kind != ReleaseSignature.ConfigurationRelease) throw new BundleRefused(422, $"{b.ReleaseId} is a {b.Kind}, not a configuration release");
        OwnBank(b.Bank, b.ReleaseId);
        var v = await VerifyBundle(b, ct);
        var contracts = await CheckConfiguration(b, ct);
        StoreResult r;
        try { r = await Registry.StoreConfiguration(b.Bank, b.Release, b.CanonicalZip(), b.Manifest, b.ContentHash, actor, ct); }
        catch (ReleaseConflict e) { throw new BundleRefused(409, e.Message); }
        if (r.Stored)
        {
            await VerifiedFact(b, v, purpose, actor, ct);
            await new FactLog(db, clock).Append(b.ReleaseId, "configuration", new NewFact(DistributionRegistry.ConfigKind, b.Release, new JsonObject
            {
                ["values"] = BankValues.ValuesOf(SetText(b) ?? "").Count, ["bindings"] = Bindings(b).Count, ["checkedAgainst"] = Names(contracts), ["inForce"] = false,
            }, b.ReleaseId, actor), ct);
        }
        var applied = putInForce ? await PutInForce(b, actor, purpose, contracts, consent, ct) : null;
        var inForce = applied is not null || await InForce(b.ReleaseId, ct);
        return new JsonObject
        {
            ["id"] = b.ReleaseId, ["kind"] = b.Kind, ["bank"] = b.Bank, ["release"] = b.Release, ["stored"] = r.Stored, ["values"] = (int?)applied?["values"] ?? 0, ["bindings"] = Bindings(b).Count,
            ["signedBy"] = v.KeyId, ["checkedAgainst"] = Names(contracts), ["state"] = inForce ? "in-force" : "awaiting-consent", ["inForce"] = inForce,
        };
    }

    static JsonArray Names(Dictionary<string, JsonNode?> contracts) => new(contracts.Keys.Select(k => (JsonNode)k).ToArray());

    /// <summary>Whether a configuration release is in force: its values, bindings, calendars and tables were put in force once.</summary>
    public async Task<bool> InForce(string releaseId, CancellationToken ct = default) =>
        await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == releaseId && f.Kind == DistributionRegistry.InForceKind, ct);

    /// <summary>The installed configuration release, as stored.</summary>
    public async Task<Bundle> StoredConfiguration(string releaseId, CancellationToken ct = default)
    {
        var at = releaseId.IndexOf('@');
        if (!releaseId.StartsWith(DistributionRegistry.ConfigPrefix, StringComparison.Ordinal) || at < 0) throw new BundleRefused(400, $"{releaseId} is not a configuration release id (config/<bank>@<release>)");
        var bank = releaseId[DistributionRegistry.ConfigPrefix.Length..at];
        OwnBank(bank, releaseId);
        var row = await new ReleaseRegistry(db, clock).Get(DistributionRegistry.ConfigScope(bank), releaseId[(at + 1)..], ct) ?? throw new BundleRefused(404, $"{releaseId} is not installed");
        return Bundle.FromZip(row.Package, ReleaseSignature.ConfigurationRelease);
    }

    /// <summary>Checks an installed configuration release again, as putting it in force will (R-09-111): its signature now, its bank, the parameter contracts now.</summary>
    public async Task<Bundle> CheckStoredConfiguration(string releaseId, CancellationToken ct = default)
    {
        var b = await StoredConfiguration(releaseId, ct);
        OwnBank(b.Bank, releaseId);
        await VerifyBundle(b, ct);
        await CheckConfiguration(b, ct);
        return b;
    }

    /// <summary>
    /// Puts an installed configuration release in force (R-09-111), on a bank approver's consent (<paramref name="consent"/>: who
    /// asked and who consented) or in an environment where the pipeline may: verified again against the signers trusted now
    /// and checked again against the parameter contract of every distribution not removed now, then each value, binding and
    /// calendar becomes an effective-dated version and the reference tables are served. Once only: a release in force stays so.
    /// </summary>
    public async Task<JsonObject> PutConfigurationInForce(string releaseId, string actor, string why, JsonObject? consent = null, CancellationToken ct = default)
    {
        var b = await StoredConfiguration(releaseId, ct);
        OwnBank(b.Bank, releaseId);
        await VerifyBundle(b, ct);
        var contracts = await CheckConfiguration(b, ct);
        return await PutInForce(b, actor, why, contracts, consent, ct) ?? new JsonObject { ["id"] = releaseId, ["inForce"] = true, ["already"] = true, ["state"] = "in-force" };
    }

    async Task<JsonObject?> PutInForce(Bundle b, string actor, string why, Dictionary<string, JsonNode?> contracts, JsonObject? consent, CancellationToken ct)
    {
        if (await InForce(b.ReleaseId, ct)) return null;
        var values = SetText(b) is { } setText ? await BankValues.ImportText(db, clock, setText, actor, ct) : 0;
        var store = new VersionedValues(db, clock);
        var bindings = Bindings(b);
        foreach (var bd in bindings)
            await store.Publish(BindingKind, b.Bank, (string?)bd?["port"] ?? "port", new JsonObject { ["binding"] = bd!.DeepClone(), ["configurationRelease"] = b.Release }, BankValues.DayOf((string)bd["validFrom"]!), null, actor, ct);
        foreach (var (path, _) in b.Files.Where(f => f.Key.StartsWith("calendars/") && f.Key.EndsWith(".yaml")))
            if (b.Yaml(path) is JsonObject cal)
                await store.Publish(CalendarKind, b.Bank, (string?)cal["name"] ?? Path.GetFileNameWithoutExtension(path), new JsonObject { ["calendar"] = cal.DeepClone(), ["configurationRelease"] = b.Release }, cal["validFrom"] is { } vf ? BankValues.DayOf((string)vf!) : DateTimeOffset.UnixEpoch, null, actor, ct);
        // The reference tables (R-01-25, R-09-75): each under its domain, served by GET /lookups/{domain}.
        var tables = b.Files.Keys.Where(f => f.StartsWith("reference/") && f.EndsWith(".yaml")).Order(StringComparer.Ordinal).Select(f => b.Yaml(f)).OfType<JsonObject>().ToList();
        await Lookups.Import(db, clock, b.Bank, b.Release, tables, actor, ct);
        var result = new JsonObject
        {
            ["id"] = b.ReleaseId, ["values"] = values, ["bindings"] = bindings.Count, ["referenceTables"] = tables.Count, ["checkedAgainst"] = Names(contracts),
            ["why"] = why, ["consent"] = consent?.DeepClone(), ["inForce"] = true, ["state"] = "in-force",
        };
        await new FactLog(db, clock).Append(b.ReleaseId, "configuration", new NewFact(DistributionRegistry.InForceKind, b.Release, (JsonObject)result.DeepClone(), b.ReleaseId, actor), ct);
        return result;
    }

    /// <summary>The bank's configuration releases installed and not in force yet, oldest first.</summary>
    public async Task<List<string>> AwaitingConsent(string bank, CancellationToken ct = default)
    {
        var scope = DistributionRegistry.ConfigScope(bank);
        var ids = await db.Releases.AsNoTracking().Where(r => r.Scope == scope).OrderBy(r => r.StoredAt).Select(r => r.Id).ToListAsync(ct);
        var inForce = (await db.Facts.AsNoTracking().Where(f => f.Kind == DistributionRegistry.InForceKind && ids.Contains(f.SubjectId)).Select(f => f.SubjectId).ToListAsync(ct)).ToHashSet();
        return ids.Where(i => !inForce.Contains(i)).ToList();
    }

    /// <summary>The port binding in force for a bank and port at a moment (scenario 2: a later validFrom switches the adapter; R-09-112).</summary>
    public async Task<JsonNode?> BindingAt(string bank, string port, DateTimeOffset at, CancellationToken ct = default)
    {
        if (await ConfigurationBindings.InForce(db, bank, port, at, ct) is not { } hit) return null;
        var rows = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == BindingKind && v.Tenant == bank && v.Name == port).OrderByDescending(v => v.VersionNo).ToListAsync(ct);
        var row = rows.FirstOrDefault(r => ConfigurationBindings.Of(r) is { } s && s.Release == hit.Release && s.ValidFrom == hit.ValidFrom && s.Adapter == hit.Adapter);
        return row is null ? null : JsonNode.Parse(row.BodyJson)?["binding"];
    }
}

public sealed class BundleRefused(int status, string message, string? code = null) : Exception(message)
{
    public int Status { get; } = status;
    /// <summary>The error dictionary's code, where the refusal has one of its own (another bank's content: package.WrongBank, R-09-117).</summary>
    public string? Code { get; } = code;
}
