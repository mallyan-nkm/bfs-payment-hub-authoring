using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Api;
using TransactHub.Ports;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// The exchange-rate feed (R-09-177 to R-09-179, R-11-96; owner's decision of 23 November 2026, docs/OWNER-ACTIONS.md A2, Issues
/// I-189 and I-275): the bank's rates fetched through its binding of p.port.fx-rates.v1, published as a signed configuration release
/// of the bank (its rates table and the rates value, each rate with its asOf), checked like any release, and put in force on the bank
/// approver's standing consent to rate releases, or else waiting for a consent of its own. Synthetic rates only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FxRateFeedTests(Databases dbs)
{
    const string Bank = Seed.Tenant;
    static string Sha(string s) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>A clock moved by hand.</summary>
    sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A distribution of the bank whose parameter contract has the rates value and the staleness limit, as factory 1.17.0 declares them.</summary>
    static Dictionary<string, byte[]> Distribution(ECDsa key, string id)
    {
        var files = new Dictionary<string, string> { ["activities/p.enrich.fx-rate.v1@1.2.0.yaml"] = "kind: activity\nname: p.enrich.fx-rate.v1\nrelease: 1.2.0\n", ["binding.yaml"] = "kind: engine-binding\ntarget: temporal\n" };
        var bom = new JsonObject
        {
            ["packs"] = new JsonArray(new JsonObject { ["scope"] = "p", ["release"] = "1.17.0" }),
            ["artefacts"] = new JsonArray(files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (JsonNode)new JsonObject { ["name"] = f.Key.Split('/')[^1].Split('@')[0], ["kind"] = f.Key.StartsWith("activities") ? "activity" : "engine-binding", ["release"] = "1.2.0", ["path"] = f.Key, ["sha256"] = Sha(f.Value) }).ToArray()),
        };
        var pc = new JsonObject
        {
            ["format"] = "th-parameter-contract/1",
            ["parameters"] = new JsonArray(
                new JsonObject { ["parameter"] = FxRateFeed.RatesParameter, ["type"] = "list", ["default"] = new JsonArray(), ["bounds"] = new JsonObject { ["min"] = 0, ["max"] = 1000000 } },
                new JsonObject { ["parameter"] = "p.enrich.fx-rate.v1#maxRateAge", ["type"] = "duration", ["default"] = "PT24H", ["bounds"] = new JsonObject { ["min"] = "PT1H", ["max"] = "P7D" } }),
        };
        files["manifest.yaml"] = new JsonObject { ["kind"] = "distribution", ["id"] = id, ["bank"] = Bank, ["engine"] = "temporal", ["schemes"] = new JsonArray("uae.wps"), ["longestWait"] = "PT0S", ["billOfMaterials"] = bom.DeepClone(), ["parameterContract"] = pc.DeepClone() }.ToJsonString();
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var (st, sig) = ReleaseSignature.Sign(bytes, new ReleaseClaims($"dist/{Bank}@{id}", "", id, "temporal", Bank, "CS-TEST-FX", ReleaseSignature.Distribution, new JsonObject { ["billOfMaterials"] = bom, ["parameterContract"] = pc }), key, DateTimeOffset.UtcNow);
        bytes[ReleaseSignature.StatementFile] = st;
        bytes[ReleaseSignature.SignatureFile] = sig;
        return bytes;
    }

    static PortBindings Bound(string adapter, string settings)
    {
        var b = new PortBindings();
        b.Add($"kind: port-bindings\ntenant: {Bank}\nbindings:\n  - port: {FxRateAdapters.Port}\n    adapter: {adapter}\n    validFrom: 2026-01-01T00:00:00Z\n    settings:\n{settings}");
        return b;
    }

    sealed record Rig(StoreOptions Store, Signers Signers, Clock Clock, Func<StoreDbContext, DistributionService> Distributions);

    async Task<Rig> Ready()
    {
        var store = dbs.Fresh();
        await using (var db = StoreDbContext.Open(store)) await db.Database.MigrateAsync();
        var s = new Signers();
        var clock = new Clock(DateTimeOffset.Parse("2026-11-23T08:00:00Z"));
        Func<StoreDbContext, DistributionService> svc = db => new DistributionService(db, clock, ct => TrustedSigners.Load(File.ReadAllText(s.TrustFile), SecretResolver.FromEnvironment(_ => null), ct), null, new RuntimeBank(Bank));
        await using (var db = StoreDbContext.Open(store)) await svc(db).InstallDistribution(Bundle.FromFiles(Distribution(s.Key, "2026.11.23-1"), ReleaseSignature.Distribution), "pipeline", "test");
        return new Rig(store, s, clock, svc);
    }

    FxRateFeed Feed(Rig r, PortBindings b, ECDsa? key = null) => new(r.Store, Bank, b, _ => Task.FromResult(key ?? r.Signers.Key), r.Distributions, r.Clock);

    static async Task<JsonArray?> RatesAt(StoreOptions store, DateTimeOffset at)
    {
        await using var db = StoreDbContext.Open(store);
        return (await BankValues.InForce(db, Bank, at)).FirstOrDefault(v => (string?)v["parameter"] == FxRateFeed.RatesParameter)?["value"] as JsonArray;
    }

    [Fact]
    public async Task The_feed_publishes_the_rates_as_a_signed_configuration_release_that_goes_in_force_on_the_standing_consent()
    {
        var r = await Ready();
        using var _ = r.Signers;
        await using (var db = StoreDbContext.Open(r.Store)) await StandingConsent.Record(db, r.Clock, Bank, true, "service:ba-1", "rates may refresh on their own (synthetic)");

        var first = await Feed(r, Bound("simulator", "      rates: AED/USD=0.2723;AED/EUR=0.2511\n")).Run();
        Assert.Equal(("in-force", $"config/{Bank}@0.0.1", 2, "simulator"), ((string?)first["state"], (string?)first["id"], (int?)first["rates"], (string?)first["adapter"]));
        var now = await RatesAt(r.Store, r.Clock.Now.AddMinutes(1));
        Assert.Equal(["AED/EUR", "AED/USD"], now!.Select(x => $"{(string?)x!["from"]}/{(string?)x!["to"]}"));
        Assert.All(now, x => Assert.Equal("2026-11-23T08:00:00Z", (string?)x!["asOf"]));

        // An hour later, new rates: the next release keeps the earlier period, so a lookup by an earlier moment still finds its rates.
        r.Clock.Now = r.Clock.Now.AddHours(1);
        var second = await Feed(r, Bound("simulator", "      rates: AED/USD=0.2724\n")).Run();
        Assert.Equal(("in-force", $"config/{Bank}@0.0.2"), ((string?)second["state"], (string?)second["id"]));
        Assert.Equal("0.2724", (await RatesAt(r.Store, r.Clock.Now.AddMinutes(1)))!.Single()!["rate"]!.ToString());
        Assert.Equal(2, (await RatesAt(r.Store, r.Clock.Now.AddMinutes(-30)))!.Count);

        await using (var db = StoreDbContext.Open(r.Store))
        {
            // The rates table is served by date, today's edition the latest rates (GET /lookups/fx-rates).
            var served = await Lookups.At(db, Bank, FxRateFeed.Domain, r.Clock.Now);
            Assert.Equal((FxRateFeed.Table, "AED", "0.2724"), ((string?)served!["table"], (string?)served["rows"]![0]!["from"], served["rows"]![0]!["rate"]!.ToString()));
            // In force on the standing consent, which the release's fact names; signed by the feed's key, verified like any release.
            var inForce = await db.Facts.AsNoTracking().SingleAsync(f => f.SubjectId == $"config/{Bank}@0.0.2" && f.Kind == DistributionRegistry.InForceKind);
            var consent = JsonNode.Parse(inForce.DataJson)!["consent"]!;
            Assert.Equal(("service:ba-1", $"standing-consent:{StandingConsent.Grant}"), ((string?)consent["consentedBy"], (string?)consent["via"]));
            var verified = await db.Facts.AsNoTracking().FirstAsync(f => f.SubjectId == $"config/{Bank}@0.0.2" && f.Kind == "release.verified");
            Assert.Equal(r.Signers.KeyId, (string?)JsonNode.Parse(verified.DataJson)!["keyId"]);
            Assert.Null(StandingConsent.NotCovered(await r.Distributions(db).StoredConfiguration($"config/{Bank}@0.0.2")));
        }
    }

    [Fact]
    public async Task Without_a_standing_consent_or_for_any_other_release_the_feed_waits_for_a_consent_of_its_own()
    {
        var r = await Ready();
        using var _ = r.Signers;
        var b = Bound("simulator", "      rates: AED/USD=0.2723\n");
        var waits = await Feed(r, b).Run();
        Assert.Equal("awaiting-consent", (string?)waits["state"]);
        Assert.NotNull((string?)waits["consentRequest"]);
        Assert.Null(await RatesAt(r.Store, r.Clock.Now.AddMinutes(1)));
        await using (var db = StoreDbContext.Open(r.Store))
            Assert.Equal((string?)waits["consentRequest"], await ConsentRequests.Open(db, ConsentRequests.PutInForce, $"config/{Bank}@0.0.1"));

        // Granted, the next run goes in force; revoked, the one after waits again.
        await using (var db = StoreDbContext.Open(r.Store)) await StandingConsent.Record(db, r.Clock, Bank, true, "service:ba-1", "granted (synthetic)");
        r.Clock.Now = r.Clock.Now.AddHours(1);
        Assert.Equal("in-force", (string?)(await Feed(r, b).Run())["state"]);
        await using (var db = StoreDbContext.Open(r.Store))
        {
            await Assert.ThrowsAsync<ServiceRefused>(() => StandingConsent.Record(db, r.Clock, Bank, true, "service:ba-1", "again"));
            await StandingConsent.Record(db, r.Clock, Bank, false, "service:ba-1", "revoked (synthetic)");
            Assert.Null(await StandingConsent.InForce(db, Bank));
            Assert.True((await new FactLog(db).Verify(StandingConsent.Chain(Bank))).Ok);
        }
        r.Clock.Now = r.Clock.Now.AddHours(1);
        Assert.Equal("awaiting-consent", (string?)(await Feed(r, b).Run())["state"]);

        // The standing consent covers the feed's rates and table only: a release that sets anything else is not covered.
        var other = new Dictionary<string, string>
        {
            ["configuration/configuration-set.yaml"] = $"kind: configuration-set\ntenant: {Bank}\nrelease: 9.0.0\nvalues:\n  - parameter: {FxRateFeed.RatesParameter}\n    value: []\n    validFrom: 2026-12-01\n  - parameter: p.enrich.fx-rate.v1#maxRateAge\n    value: P7D\n    validFrom: 2026-12-01\nbindings: []\n",
        };
        var bom = new JsonObject { ["artefacts"] = new JsonArray(other.Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["sha256"] = Sha(f.Value) }).ToArray()) };
        other["manifest.yaml"] = new JsonObject { ["kind"] = "configuration-release", ["bank"] = Bank, ["release"] = "9.0.0", ["feed"] = "fx-rates", ["billOfMaterials"] = bom }.ToJsonString();
        var bundle = Bundle.FromFiles(other.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value)), ReleaseSignature.ConfigurationRelease);
        Assert.Contains("values other than", StandingConsent.NotCovered(bundle));
    }

    /// <summary>
    /// R-09-184 (Issue I-277): the feed's own key, trusted with <c>kinds: [configuration]</c> and <c>scope: { feed: fx-rates }</c>, verifies the
    /// feed's rate releases, and nothing else: a distribution it signs, or a configuration release that claims the feed but sets more,
    /// is refused as <c>signing.OutOfScope</c> and nothing is installed.
    /// </summary>
    [Fact]
    public async Task The_feeds_key_scoped_to_the_feed_signs_rate_releases_and_nothing_else()
    {
        var r = await Ready();
        using var _ = r.Signers;
        using var feedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(r.Signers.TrustFile, new JsonObject
        {
            ["kind"] = "trusted-signers",
            ["keys"] = new JsonArray(
                new JsonObject { ["keyId"] = ReleaseSignature.KeyIdOf(r.Signers.Key), ["publicKey"] = r.Signers.Key.ExportSubjectPublicKeyInfoPem() },
                new JsonObject { ["keyId"] = ReleaseSignature.KeyIdOf(feedKey), ["publicKey"] = feedKey.ExportSubjectPublicKeyInfoPem(), ["kinds"] = new JsonArray("configuration"), ["scope"] = new JsonObject { ["feed"] = "fx-rates" } }),
            ["revoked"] = new JsonArray(),
        }.ToJsonString());
        await using (var db = StoreDbContext.Open(r.Store)) await StandingConsent.Record(db, r.Clock, Bank, true, "service:ba-1", "rates may refresh on their own (synthetic)");
        var run = await Feed(r, Bound("simulator", "      rates: AED/USD=0.2723\n"), feedKey).Run();
        Assert.Equal("in-force", (string?)run["state"]);

        // A distribution signed by the feed's key is refused, as is a configuration release that names the feed and sets more.
        await using (var db = StoreDbContext.Open(r.Store))
        {
            var dist = await Assert.ThrowsAsync<BundleRefused>(() => r.Distributions(db).InstallDistribution(Bundle.FromFiles(Distribution(feedKey, "2026.11.24-9"), ReleaseSignature.Distribution), "pipeline", "test"));
            Assert.Equal(SignatureRejected.OutOfScope, dist.Code);
        }
        var files = new Dictionary<string, string>
        {
            ["configuration/configuration-set.yaml"] = $"kind: configuration-set\ntenant: {Bank}\nrelease: 0.0.9\nvalues:\n  - parameter: {FxRateFeed.RatesParameter}\n    value: []\n    validFrom: 2026-12-01\n  - parameter: p.enrich.fx-rate.v1#maxRateAge\n    value: P7D\n    validFrom: 2026-12-01\nbindings: []\n",
        };
        var bom = new JsonObject { ["artefacts"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["sha256"] = Sha(f.Value) }).ToArray()) };
        files["manifest.yaml"] = new JsonObject { ["kind"] = "configuration-release", ["bank"] = Bank, ["release"] = "0.0.9", ["feed"] = "fx-rates", ["billOfMaterials"] = bom }.ToJsonString();
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var (st, sig) = ReleaseSignature.Sign(bytes, new ReleaseClaims($"config/{Bank}@0.0.9", "", "0.0.9", "", Bank, "CS-TEST-FX", ReleaseSignature.ConfigurationRelease, new JsonObject { ["billOfMaterials"] = bom.DeepClone(), ["feed"] = "fx-rates" }), feedKey, r.Clock.Now);
        bytes[ReleaseSignature.StatementFile] = st;
        bytes[ReleaseSignature.SignatureFile] = sig;
        var wider = Bundle.FromFiles(bytes, ReleaseSignature.ConfigurationRelease);
        var trust = TrustedSigners.Parse(File.ReadAllText(r.Signers.TrustFile));
        var refused = Assert.Throws<SignatureRejected>(() => wider.Verify(trust));
        Assert.Equal(SignatureRejected.OutOfScope, refused.Code);
        Assert.Contains("values other than", refused.Message);
    }

    [Fact]
    public async Task The_standing_consent_is_granted_and_revoked_on_a_bank_approvers_consent_through_the_book()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub());
        var se = api.Engineer;
        // For this runtime's bank only.
        var wrong = await se.PostAsJsonAsync("/service/api/actions", new { action = StandingConsent.Grant, target = "other-bank", reason = "try (synthetic)" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        // An engineer asks; it waits; the engineer may not consent; a bank approver does.
        var ask = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = StandingConsent.Grant, target = Bank, reason = "rates refresh hourly (synthetic)" }));
        Assert.Equal("pending-approval", (string?)ask["status"]);
        await using (var db = StoreDbContext.Open(api.Store)) Assert.Null(await StandingConsent.InForce(db, Bank));
        Assert.Equal(HttpStatusCode.Forbidden, (await se.PostAsJsonAsync($"/service/api/actions/{(string)ask["action"]!}/decision", new { approve = true, reason = "mine" })).StatusCode);
        var done = await J.Of(await api.BankApprover.PostAsJsonAsync($"/service/api/actions/{(string)ask["action"]!}/decision", new { approve = true, reason = "agreed with treasury (synthetic)" }));
        Assert.True((string?)done["status"] == "done", done.ToJsonString());
        await using (var db = StoreDbContext.Open(api.Store)) Assert.Equal("service:ba-1", (string?)(await StandingConsent.InForce(db, Bank))!["by"]);
        var revoked = await api.Act(se, new { action = StandingConsent.Revoke, target = Bank, reason = "treasury asked to see each release (synthetic)" });
        Assert.Equal("done", (string?)revoked["status"]);
        await using (var db = StoreDbContext.Open(api.Store)) Assert.Null(await StandingConsent.InForce(db, Bank));
    }

    sealed class Answer(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task A_malformed_quote_or_a_service_that_does_not_answer_publishes_nothing()
    {
        var r = await Ready();
        using var _ = r.Signers;
        await Assert.ThrowsAsync<HttpRequestException>(() => Feed(r, Bound("simulator", "      answer: rejected\n")).Run());
        await Assert.ThrowsAsync<FxRatesRefused>(() => Feed(r, Bound("simulator", "      rates: AED/AED=1\n")).Run());
        await Assert.ThrowsAsync<FxRatesRefused>(() => Feed(r, Bound("simulator", "      rates: AED/USD=0\n")).Run());
        var rest = Bound("gateway-rest", "      baseUrl: https://fx-rates.example.invalid/\n");
        FxRateFeed Rest(HttpStatusCode code, string body) => new(r.Store, Bank, rest, _ => Task.FromResult(r.Signers.Key), r.Distributions, r.Clock, new HttpClient(new Answer(code, body)));
        await Assert.ThrowsAsync<FxRatesRefused>(() => Rest(HttpStatusCode.OK, """{"rates":[{"from":"AED","to":"USD","rate":0.27}]}""").Run());
        await Assert.ThrowsAsync<HttpRequestException>(() => Rest(HttpStatusCode.ServiceUnavailable, "{}").Run());
        // A key the bank does not trust: the release is refused at install, nothing is in force.
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAsync<BundleRefused>(() => Feed(r, Bound("simulator", "      rates: AED/USD=0.2723\n"), stranger).Run());
        await using (var db = StoreDbContext.Open(r.Store)) Assert.False(await db.Releases.AnyAsync(x => x.Scope == DistributionRegistry.ConfigScope(Bank)));
        // And a good answer from the REST service is published.
        var ok = await Rest(HttpStatusCode.OK, """{"rates":[{"from":"AED","to":"USD","rate":"0.2723","asOf":"2026-11-23T07:55:00Z"}]}""").Run();
        Assert.Equal("awaiting-consent", (string?)ok["state"]);
        // The schedule is the binding's dial, refused outside its bounds.
        Assert.Equal(TimeSpan.FromMinutes(60), FxRateFeed.Every(rest.InForce(Bank, FxRateAdapters.Port, r.Clock.Now)));
        foreach (var bad in new[] { "4", "1441", "hourly" })
            Assert.Throws<PortBindingException>(() => FxRateFeed.Every(Bound("simulator", $"      refreshMinutes: \"{bad}\"\n").InForce(Bank, FxRateAdapters.Port, r.Clock.Now)));
    }

    [Fact]
    public void Each_real_bank_binds_the_rates_port_by_placeholder_host_and_credential_reference()
    {
        foreach (var bank in new[] { "adib", "dib", "enbd", "fab" })
        {
            var b = new PortBindings();
            b.Add(File.ReadAllText(Path.Combine(Repo.Root(), "content-configurations", "banks", bank, "platform-adapters.yaml")), bank);
            var fx = b.InForce(bank, FxRateAdapters.Port, DateTimeOffset.Parse("2026-11-24T00:00:00Z"));
            Assert.Equal(RestFxRates.Name, fx.Adapter);
            Assert.EndsWith(".invalid/", fx.Setting("baseUrl", ""));
            Assert.True(SecretRef.IsRef(fx.Setting("secretRef", "")), bank);
            Assert.Equal(TimeSpan.FromMinutes(60), FxRateFeed.Every(fx));
        }
    }
}
