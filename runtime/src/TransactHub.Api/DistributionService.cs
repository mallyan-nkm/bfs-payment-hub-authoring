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
    /// <summary>The one bank this runtime serves (R-09-73): another bank's bundle, distribution or payment is refused. Null only in a library test.</summary>
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
    /// Installs a configuration release: verified, its values checked against the parameter contract of every distribution of
    /// the bank not removed (an unknown value or one out of bounds is refused), its port bindings by secret reference only, and
    /// its contract versions matched; then stored insert-only, and each value, binding and calendar an effective-dated version.
    /// </summary>
    public async Task<JsonObject> InstallConfiguration(Bundle b, string actor, string purpose, CancellationToken ct = default)
    {
        if (b.Kind != ReleaseSignature.ConfigurationRelease) throw new BundleRefused(422, $"{b.ReleaseId} is a {b.Kind}, not a configuration release");
        OwnBank(b.Bank, b.ReleaseId);
        var v = await VerifyBundle(b, ct);
        var contracts = await Contracts(b.Bank, ct);
        if (contracts.Count == 0) throw new BundleRefused(409, $"{b.ReleaseId} is refused: {b.Bank} has no installed distribution whose parameter contract it could be checked against");
        var problems = new List<string>();
        foreach (var req in b.Manifest["requiresContract"] as JsonArray ?? [])
        {
            var dist = (string?)req?["distribution"];
            if (dist is not null && contracts.TryGetValue(dist, out var c) && "sha256:" + ReleaseSignature.Sha256Hex(Encoding.UTF8.GetBytes(CanonicalJson.Of(c))) != (string?)req?["sha256"])
                problems.Add($"it was checked against another parameter contract of {dist}");
        }
        var setText = b.Files.TryGetValue("configuration/configuration-set.yaml", out var sb) ? Encoding.UTF8.GetString(sb) : null;
        if (setText is not null) problems.AddRange(ParameterContract.Check(BankValues.ValuesOf(setText), contracts));
        var bindings = b.Yaml("bindings/port-bindings.yaml")?["bindings"] as JsonArray ?? [];
        foreach (var bd in bindings)
        {
            if ((string?)bd?["secretRef"] is { } sr && !SecretRef.IsRef(sr)) problems.Add($"the binding of {(string?)bd?["port"]} holds a secret that is not a reference");
            foreach (var (k, val) in bd?["settings"] as JsonObject ?? [])
                if (System.Text.RegularExpressions.Regex.IsMatch(k, "(?i)(password|passphrase|secret|token|apikey|accesskey|privatekey|credential)$") && !SecretRef.IsRef((string?)val))
                    problems.Add($"the binding of {(string?)bd?["port"]} holds {k} as a value, not a secretRef");
            if ((string?)bd?["validFrom"] is null) problems.Add($"the binding of {(string?)bd?["port"]} has no validFrom");
        }
        if (problems.Count > 0) throw new BundleRefused(422, $"{b.ReleaseId} is refused: {string.Join("; ", problems)}");
        StoreResult r;
        try { r = await Registry.StoreConfiguration(b.Bank, b.Release, b.CanonicalZip(), b.Manifest, b.ContentHash, actor, ct); }
        catch (ReleaseConflict e) { throw new BundleRefused(409, e.Message); }
        var values = 0;
        if (r.Stored)
        {
            await VerifiedFact(b, v, purpose, actor, ct);
            if (setText is not null) values = await BankValues.ImportText(db, clock, setText, actor, ct);
            var store = new VersionedValues(db, clock);
            foreach (var bd in bindings)
                await store.Publish(BindingKind, b.Bank, (string?)bd?["port"] ?? "port", new JsonObject { ["binding"] = bd!.DeepClone(), ["configurationRelease"] = b.Release }, BankValues.DayOf((string)bd["validFrom"]!), null, actor, ct);
            foreach (var (path, _) in b.Files.Where(f => f.Key.StartsWith("calendars/") && f.Key.EndsWith(".yaml")))
                if (b.Yaml(path) is JsonObject cal)
                    await store.Publish(CalendarKind, b.Bank, (string?)cal["name"] ?? Path.GetFileNameWithoutExtension(path), new JsonObject { ["calendar"] = cal.DeepClone(), ["configurationRelease"] = b.Release }, cal["validFrom"] is { } vf ? BankValues.DayOf((string)vf!) : DateTimeOffset.UnixEpoch, null, actor, ct);
            await new FactLog(db, clock).Append(b.ReleaseId, "configuration", new NewFact(DistributionRegistry.ConfigKind, b.Release, new JsonObject { ["values"] = values, ["bindings"] = bindings.Count, ["checkedAgainst"] = new JsonArray(contracts.Keys.Select(k => (JsonNode)k).ToArray()) }, b.ReleaseId, actor), ct);
        }
        return new JsonObject
        {
            ["id"] = b.ReleaseId, ["kind"] = b.Kind, ["bank"] = b.Bank, ["release"] = b.Release, ["stored"] = r.Stored, ["values"] = values, ["bindings"] = bindings.Count,
            ["signedBy"] = v.KeyId, ["checkedAgainst"] = new JsonArray(contracts.Keys.Select(k => (JsonNode)k).ToArray()), ["state"] = "installed",
        };
    }

    /// <summary>The port binding in force for a bank and port at a moment (scenario 2: a later validFrom switches the adapter).</summary>
    public async Task<JsonNode?> BindingAt(string bank, string port, DateTimeOffset at, CancellationToken ct = default) =>
        (await new VersionedValues(db, clock).InForce(BindingKind, bank, port, at, ct)) is { } row ? JsonNode.Parse(row.BodyJson)?["binding"] : null;
}

public sealed class BundleRefused(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
