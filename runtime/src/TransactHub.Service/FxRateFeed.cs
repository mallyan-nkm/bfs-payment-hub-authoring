using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TransactHub.Api;
using TransactHub.Ports;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Service;

/// <summary>
/// The bank approver's standing consent to the exchange-rate feed's releases (R-09-179; the choice of Issue I-275): granted once,
/// through the service console's book (<c>grant-rate-consent</c>, asked by a service engineer, consented by a bank approver), and
/// revoked the same way (<c>revoke-rate-consent</c>). It covers a configuration release of the feed only: one that sets
/// <see cref="FxRateFeed.RatesParameter"/> and the rates table and nothing else, signed and checked against the parameter contracts
/// as every release is. Every other release, and a rate release while no standing consent is in force, waits for its own consent.
/// Each grant and revocation is a fact on the consent's own chain, with who and why.
/// </summary>
public static class StandingConsent
{
    public const string Kind = "consent", Grant = "grant-rate-consent", Revoke = "revoke-rate-consent";

    public static string Chain(string bank) => $"standing-consent:fx-rates:{bank}";

    /// <summary>The grant in force for the bank, or null: the latest fact of the chain, if it grants.</summary>
    public static async Task<JsonObject?> InForce(StoreDbContext db, string bank, CancellationToken ct = default)
    {
        var last = await db.Facts.AsNoTracking().Where(f => f.SubjectId == Chain(bank) && f.Kind == Kind).OrderByDescending(f => f.Seq).FirstOrDefaultAsync(ct);
        if (last is null || last.Code != "granted") return null;
        var d = JsonNode.Parse(last.DataJson) as JsonObject ?? new JsonObject();
        d["seq"] = last.Seq;
        return d;
    }

    public static async Task<JsonObject> Record(StoreDbContext db, TimeProvider clock, string bank, bool grant, string actor, string why, CancellationToken ct = default)
    {
        var now = await InForce(db, bank, ct);
        if (grant == now is not null) throw new ServiceRefused(409, ErrorCodes.ActionNotOffered, grant ? $"{bank} has a standing consent to rate releases already" : $"{bank} has no standing consent to rate releases to revoke");
        var f = await new FactLog(db, clock).Append(Chain(bank), Kind, new NewFact(Kind, grant ? "granted" : "revoked",
            new JsonObject { ["bank"] = bank, ["scope"] = "fx-rates", ["parameter"] = FxRateFeed.RatesParameter, ["table"] = FxRateFeed.Table, ["by"] = actor, ["why"] = why }, Actor: actor), ct);
        return new JsonObject { ["bank"] = bank, ["standingConsent"] = grant ? "granted" : "revoked", ["fact"] = f.Seq };
    }

    /// <summary>Whether a configuration release is one the standing consent covers: the feed's rates and table, nothing else.</summary>
    public static string? NotCovered(Bundle b)
    {
        var set = b.Yaml("configuration/configuration-set.yaml") as JsonObject;
        var values = set?["values"] as JsonArray ?? [];
        if (values.Count == 0 || values.Any(v => (string?)v?["parameter"] != FxRateFeed.RatesParameter)) return $"it sets values other than {FxRateFeed.RatesParameter}";
        if ((set?["bindings"] as JsonArray)?.Count > 0 || b.Files.ContainsKey("bindings/port-bindings.yaml")) return "it binds ports";
        foreach (var f in b.Files.Keys.Where(f => !ReleaseSignature.IsSignatureFile(f) && f is not "manifest.yaml" and not "configuration/configuration-set.yaml"))
            if (f != $"reference/{FxRateFeed.Table}.yaml") return $"it carries {f}";
        if ((string?)b.Manifest["feed"] != "fx-rates") return "its manifest does not name the rates feed";
        return null;
    }
}

/// <summary>
/// The exchange-rate feed (R-09-177 to R-09-179; owner's decision of 23 November 2026, docs/OWNER-ACTIONS.md A2, Issue I-189): a
/// small scheduled job of the service console's host. Each run asks the bank's rates service through its binding of
/// <c>p.port.fx-rates.v1</c> in force (the <c>simulator</c> in a test environment, <c>gateway-rest</c> in production), and publishes
/// what it answers as a configuration release of the bank, signed by the feed's own key (by reference, trusted like any signer): a
/// new edition of the bank's rates table <see cref="Table"/> (domain <c>fx-rates</c>) and the value <see cref="RatesParameter"/> that
/// the payments read, each rate with its <c>asOf</c>, so <c>p.enrich.fx-rate.v1</c> refuses one older than the bank's staleness limit.
/// The release goes in force like any configuration: verified, checked against every distribution's parameter contract, and put in
/// force on the bank approver's standing consent to rate releases (<see cref="StandingConsent"/>), or else waiting for a consent of
/// its own in the service console's book. The value keeps the periods of the last week, so a lookup by an earlier date still finds
/// the rates in force then. How often it runs is the binding's dial <c>refreshMinutes</c> (default 60, from 5 to 1440).
/// </summary>
public sealed class FxRateFeed(StoreOptions store, string bank, PortBindings bindings, Func<CancellationToken, Task<ECDsa>> signingKey, Func<StoreDbContext, DistributionService> distributions,
    TimeProvider? clock = null, HttpClient? http = null, ISecretResolver? secrets = null)
{
    public const string RatesParameter = "p.enrich.fx-rate.v1#rates", Table = "b.ref.fx-rates.v1", Domain = "fx-rates", Actor = "fx-rate-feed";
    public const string RefreshSetting = "refreshMinutes";
    public const int DefaultRefreshMinutes = 60, MinRefreshMinutes = 5, MaxRefreshMinutes = 1440;
    /// <summary>How far back the value keeps its earlier periods.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>The binding in force, or null when the bank has not bound the rates port.</summary>
    public PortBinding? Binding()
    {
        try { return bindings.InForce(bank, FxRateAdapters.Port, clock.GetUtcNow()); }
        catch (PortBindingException) { return null; }
    }

    /// <summary>How often the feed runs, from the binding's <c>refreshMinutes</c>; a value outside its bounds is refused.</summary>
    public static TimeSpan Every(PortBinding b)
    {
        var s = b.Setting(RefreshSetting, DefaultRefreshMinutes.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) || m < MinRefreshMinutes || m > MaxRefreshMinutes)
            throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: {RefreshSetting} is '{s}', not a whole number from {MinRefreshMinutes} to {MaxRefreshMinutes}");
        return TimeSpan.FromMinutes(m);
    }

    /// <summary>One run: fetch, publish, and put in force on the standing consent or ask for one. Answers what it did.</summary>
    public async Task<JsonObject> Run(CancellationToken ct = default)
    {
        if (Binding() is not { } binding) return new JsonObject { ["state"] = "unbound", ["port"] = FxRateAdapters.Port };
        var quotes = await FxRateAdapters.Create(binding, http, secrets, clock).Rates(ct);
        if (quotes.Count == 0) throw new FxRatesRefused($"{FxRateAdapters.Port}: the service gave no rates");
        var now = clock.GetUtcNow();
        await using var db = StoreDbContext.Open(store);
        var release = await NextRelease(db, ct);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["configuration/configuration-set.yaml"] = (await SetOf(db, release, quotes, now, ct)).ToJsonString(),
            [$"reference/{Table}.yaml"] = (await TableOf(db, release, quotes, now, ct)).ToJsonString(),
        };
        var bom = new JsonObject { ["artefacts"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["sha256"] = "sha256:" + ReleaseSignature.Sha256Hex(Encoding.UTF8.GetBytes(f.Value)) }).ToArray()) };
        var changeSet = $"FX-{now:yyyyMMddHHmmss}";
        files["manifest.yaml"] = new JsonObject
        {
            ["kind"] = "configuration-release", ["bank"] = bank, ["release"] = release, ["feed"] = "fx-rates", ["changeSet"] = changeSet, ["billOfMaterials"] = bom.DeepClone(),
        }.ToJsonString();
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var (statement, signature) = ReleaseSignature.Sign(bytes, new ReleaseClaims($"config/{bank}@{release}", "", release, "", bank, changeSet, ReleaseSignature.ConfigurationRelease, new JsonObject { ["billOfMaterials"] = bom }), await signingKey(ct), now);
        bytes[ReleaseSignature.StatementFile] = statement;
        bytes[ReleaseSignature.SignatureFile] = signature;
        var bundle = Bundle.FromFiles(bytes, ReleaseSignature.ConfigurationRelease);
        var consent = await StandingConsent.InForce(db, bank, ct);
        var covered = StandingConsent.NotCovered(bundle);
        var svc = distributions(db);
        var putInForce = consent is not null && covered is null;
        var r = await svc.InstallConfiguration(bundle, Actor, "fx-rate feed", putInForce, ct,
            putInForce ? new JsonObject { ["standing"] = (int?)consent!["seq"], ["consentedBy"] = (string?)consent["by"], ["via"] = $"standing-consent:{StandingConsent.Grant}" } : null);
        if (!putInForce)
            r["consentRequest"] = await ConsentRequests.Open(db, ConsentRequests.PutInForce, bundle.ReleaseId, ct)
                ?? await ConsentRequests.Ask(db, clock, ConsentRequests.PutInForce, bundle.ReleaseId, $"fx-rate feed: {quotes.Count} rate(s) as of {now:O}" + (covered is null ? "" : $" ({covered})"), Actor, ct);
        r["rates"] = quotes.Count;
        r["adapter"] = binding.Adapter;
        return r;
    }

    /// <summary>The feed's release: the patch after the bank's newest configuration release, so it never takes a number the studio gave.</summary>
    async Task<string> NextRelease(StoreDbContext db, CancellationToken ct)
    {
        var scope = DistributionRegistry.ConfigScope(bank);
        var ids = await db.Releases.AsNoTracking().Where(r => r.Scope == scope).Select(r => r.Id).ToListAsync(ct);
        var newest = ids.Count == 0 ? "1.0.0" : ids.Max(Comparer<string>.Create(ConfigurationValues.CompareReleases))!;
        var p = newest.Split('.').Select(x => int.TryParse(x, out var i) ? i : 0).Concat([0, 0, 0]).Take(3).ToArray();
        return ids.Count == 0 ? "0.0.1" : $"{p[0]}.{p[1]}.{p[2] + 1}";
    }

    static JsonArray Rows(IEnumerable<FxQuote> quotes) => new(quotes.OrderBy(q => q.From, StringComparer.Ordinal).ThenBy(q => q.To, StringComparer.Ordinal)
        .Select(q => (JsonNode)new JsonObject { ["from"] = q.From, ["to"] = q.To, ["rate"] = q.Rate, ["asOf"] = q.AsOf.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) }).ToArray());

    /// <summary>The configuration set: the rates value's periods of the last week as the newest release gave them, and the new one.</summary>
    async Task<JsonObject> SetOf(StoreDbContext db, string release, IReadOnlyList<FxQuote> quotes, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == ConfigurationValues.Kind && v.Tenant == bank && v.Name == RatesParameter).ToListAsync(ct);
        string ReleaseOf(VersionedValueRow r) => (string?)JsonNode.Parse(r.BodyJson)?["configurationSet"] ?? "0.0.0";
        var values = new JsonArray();
        if (rows.Count > 0)
        {
            var newest = rows.Select(ReleaseOf).Max(Comparer<string>.Create(ConfigurationValues.CompareReleases))!;
            var periods = rows.Where(r => ReleaseOf(r) == newest).OrderBy(r => r.VersionNo).ToList();
            // The periods of the last week, and the one in force when the week began.
            var since = now - Keep;
            var before = periods.Where(r => r.EffectiveFrom <= since).LastOrDefault();
            foreach (var r in periods.Where(r => r.EffectiveFrom > since || ReferenceEquals(r, before)))
            {
                var v = new JsonObject { ["parameter"] = RatesParameter, ["value"] = JsonNode.Parse(r.BodyJson)?["value"]?.DeepClone(), ["validFrom"] = r.EffectiveFrom.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
                if (r.EffectiveTo is { } to) v["validTo"] = to.ToUniversalTime().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                values.Add(v);
            }
        }
        values.Add(new JsonObject { ["parameter"] = RatesParameter, ["value"] = Rows(quotes), ["validFrom"] = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) });
        return new JsonObject { ["kind"] = "configuration-set", ["tenant"] = bank, ["release"] = release, ["title"] = "Exchange rates from the rates feed", ["values"] = values, ["bindings"] = new JsonArray() };
    }

    /// <summary>The bank's rates table: its editions as the release in force left them, today's replaced by the new rates.</summary>
    async Task<JsonObject> TableOf(StoreDbContext db, string release, IReadOnlyList<FxQuote> quotes, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == ReferenceTables.Kind && v.Tenant == bank && v.Name == Domain).OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct);
        var day = now.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var since = (now - Keep).ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var editions = new JsonArray();
        foreach (var e in (row is null ? null : JsonNode.Parse(row.BodyJson)?["table"]?["editions"] as JsonArray) ?? [])
            if (string.CompareOrdinal((string?)e?["validFrom"], since) >= 0 && (string?)e?["validFrom"] != day) editions.Add(e!.DeepClone());
        editions.Add(new JsonObject { ["validFrom"] = day, ["rows"] = Rows(quotes) });
        return new JsonObject
        {
            ["kind"] = "reference-table", ["name"] = Table, ["release"] = release, ["title"] = "Exchange rates from the bank's rates service", ["domain"] = Domain,
            ["description"] = "The exchange rates the bank's rates service gave, each as of its instant, published by the rates feed (R-09-177).",
            ["key"] = new JsonArray("from", "to"),
            // The columns of the example bank's table (spec/examples/bank/reference/b.ref.fx-rates.v1.yaml), which FxRateFeedTests compares.
            ["columns"] = new JsonArray(
                new JsonObject { ["id"] = "from", ["type"] = "code", ["title"] = "From", ["required"] = true, ["pattern"] = "^[A-Z]{3}$" },
                new JsonObject { ["id"] = "to", ["type"] = "code", ["title"] = "To", ["required"] = true, ["pattern"] = "^[A-Z]{3}$" },
                new JsonObject { ["id"] = "rate", ["type"] = "number", ["title"] = "Rate", ["required"] = true },
                new JsonObject { ["id"] = "asOf", ["type"] = "text", ["title"] = "As of", ["required"] = true, ["pattern"] = "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}Z$" }),
            ["editions"] = editions,
            ["provenance"] = new JsonObject { ["origin"] = "authored", ["status"] = "confirmed" },
        };
    }
}

/// <summary>
/// Runs the feed on its schedule: once a minute it looks whether the binding's <c>refreshMinutes</c> have passed since the last run.
/// A run that fails (the rates service does not answer, its breaker is open, a quote is malformed) publishes nothing; the rates in
/// force grow older, and the operations console says when they are stale (R-05-92).
/// </summary>
public sealed class FxRateFeedService(FxRateFeed feed, ILogger<FxRateFeedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var last = DateTimeOffset.MinValue;
        string? failing = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (feed.Binding() is { } b && DateTimeOffset.UtcNow - last >= FxRateFeed.Every(b))
                {
                    last = DateTimeOffset.UtcNow;
                    var r = await feed.Run(ct);
                    log.LogInformation("fx-rate feed: {Release} {State}", (string?)r["id"], (string?)r["state"]);
                    failing = null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // Said once per kind of failure, not on every beat.
                if (failing != e.GetType().Name) log.LogWarning("fx-rate feed: {Error}", e.Message);
                failing = e.GetType().Name;
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
