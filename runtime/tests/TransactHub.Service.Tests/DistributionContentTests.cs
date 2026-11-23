using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// The service console's distributions and configuration releases (R-09-40 as amended, R-09-48 to R-09-57, Issue I-102 done):
/// both kinds listed from GitHub, verified, the difference against the active distribution shown, installed, activated with
/// the old one draining, drained by hand, removal refused while a pinned payment is open, rollback; the open-payment count
/// per distribution from the pins and end facts.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class DistributionContentTests(Databases dbs)
{
    const string Bank = "example-bank";
    static string Sha(string s) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    static string Folder(string id) => $"content-configurations/releases/distributions/{Bank}/{id}";

    public static Dictionary<string, byte[]> Distribution(ECDsa key, string id, string activityRelease = "1.0.0", string ackMax = "P7D", string bank = Bank)
    {
        var files = new Dictionary<string, string>
        {
            [$"activities/uae.wps.exchange.submit-salary-file.v1@{activityRelease}.yaml"] = $"kind: activity\nname: uae.wps.exchange.submit-salary-file.v1\nrelease: {activityRelease}\n",
            ["binding.yaml"] = "kind: engine-binding\ntarget: temporal\n",
        };
        var bom = new JsonObject
        {
            ["packs"] = new JsonArray(new JsonObject { ["scope"] = "uae.wps", ["release"] = activityRelease }),
            ["artefacts"] = new JsonArray(files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (JsonNode)new JsonObject { ["name"] = f.Key.Split('/')[^1].Split('@')[0], ["kind"] = f.Key.StartsWith("activities") ? "activity" : "engine-binding", ["release"] = activityRelease, ["path"] = f.Key, ["sha256"] = Sha(f.Value) }).ToArray()),
        };
        var pc = new JsonObject { ["format"] = "th-parameter-contract/1", ["parameters"] = new JsonArray(new JsonObject { ["parameter"] = "uae.wps.exchange.submit-salary-file.v1#ackWait", ["type"] = "duration", ["default"] = "P1D", ["bounds"] = new JsonObject { ["min"] = "PT1H", ["max"] = ackMax } }) };
        files["errors/uae.wps.errors.wps.v1@1.0.0.yaml"] = "kind: error-dictionary\nname: uae.wps.errors.wps.v1\nrelease: 1.0.0\nentries: []\n";
        files["manifest.yaml"] = new JsonObject { ["kind"] = "distribution", ["id"] = id, ["bank"] = bank, ["engine"] = "temporal", ["schemes"] = new JsonArray("uae.wps"), ["longestWait"] = "PT0S", ["billOfMaterials"] = bom.DeepClone(), ["parameterContract"] = pc.DeepClone() }.ToJsonString();
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var (st, sig) = ReleaseSignature.Sign(bytes, new ReleaseClaims($"dist/{bank}@{id}", "", id, "temporal", bank, "CS-TEST-010", ReleaseSignature.Distribution, new JsonObject { ["billOfMaterials"] = bom, ["parameterContract"] = pc }), key, DateTimeOffset.UtcNow);
        bytes[ReleaseSignature.StatementFile] = st;
        bytes[ReleaseSignature.SignatureFile] = sig;
        return bytes;
    }

    public static Dictionary<string, byte[]> Configuration(ECDsa key, string release, string ack)
    {
        var files = new Dictionary<string, string> { ["configuration/configuration-set.yaml"] = $"kind: configuration-set\ntenant: {Bank}\nrelease: {release}\nvalues:\n  - parameter: uae.wps.exchange.submit-salary-file.v1#ackWait\n    value: {ack}\n    validFrom: 2026-12-01\nbindings: []\n" };
        var bom = new JsonObject { ["artefacts"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["sha256"] = Sha(f.Value) }).ToArray()) };
        files["manifest.yaml"] = new JsonObject { ["kind"] = "configuration-release", ["bank"] = Bank, ["release"] = release, ["billOfMaterials"] = bom.DeepClone() }.ToJsonString();
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var (st, sig) = ReleaseSignature.Sign(bytes, new ReleaseClaims($"config/{Bank}@{release}", "", release, "", Bank, "CS-TEST-011", ReleaseSignature.ConfigurationRelease, new JsonObject { ["billOfMaterials"] = bom }), key, DateTimeOffset.UtcNow);
        bytes[ReleaseSignature.StatementFile] = st;
        bytes[ReleaseSignature.SignatureFile] = sig;
        return bytes;
    }

    static Task<HttpResponseMessage> Post(HttpClient c, string path, object body) => c.PostAsJsonAsync(path, body);

    [Fact]
    public async Task Distributions_and_configuration_releases_are_listed_verified_diffed_installed_activated_drained_removed_and_rolled_back()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        gh.Publish($"dist/{Bank}/2026.11.22-1", Folder("2026.11.22-1"), Distribution(s.Key, "2026.11.22-1"));
        gh.Publish($"dist/{Bank}/2026.11.22-2", Folder("2026.11.22-2"), Distribution(s.Key, "2026.11.22-2", activityRelease: "1.0.1"));
        var tampered = Distribution(s.Key, "2026.11.22-3");
        tampered["binding.yaml"] = "kind: engine-binding\ntarget: dtfx\n"u8.ToArray();
        gh.Publish($"dist/{Bank}/2026.11.22-3", Folder("2026.11.22-3"), tampered);
        gh.Publish($"config/{Bank}/1.0.0", $"content-configurations/releases/configuration/{Bank}/1.0.0", Configuration(s.Key, "1.0.0", "P2D"));
        gh.Publish($"config/{Bank}/1.0.1", $"content-configurations/releases/configuration/{Bank}/1.0.1", Configuration(s.Key, "1.0.1", "P9D"));
        await using var api = new ServiceApi(dbs.Fresh(), s, gh);
        var se = api.Engineer;
        var sa = api.Approver;

        var versions = (await J.Of(await se.GetAsync("/service/api/content/versions"))).AsArray();
        Assert.Equal(["configuration", "configuration", "distribution", "distribution", "distribution"], versions.Select(v => (string)v!["kind"]!).Order());
        Assert.All(versions, v => Assert.True((bool)v!["installable"]!));
        Assert.True((bool)(await J.Of(await Post(se, "/service/api/content/verify", new { tag = $"dist/{Bank}/2026.11.22-1", reason = "check" })))["verified"]!);
        var bad = await api.Consented(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.22-3", reason = "try" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("signing.Tampered", (string?)(await J.Of(bad))["code"]);
        // A configuration release with no distribution to check it against is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await api.Consented(se, "/service/api/content/install", new { tag = $"config/{Bank}/1.0.0", reason = "early" })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await api.Consented(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.22-1", reason = "CR-1" })).StatusCode);
        async Task<JsonNode> Approved(string action, string target, object? parameters = null)
        {
            var ask = await J.Of(await Post(se, "/service/api/actions", new { action, target, reason = $"CR: {action}", parameters = parameters ?? new { environment = "production" } }));
            return await J.Of(await Post(sa, $"/service/api/actions/{(string)ask["action"]!}/decision", new { approve = true, reason = "four eyes" }));
        }
        const string D1 = "dist/example-bank@2026.11.22-1", D2 = "dist/example-bank@2026.11.22-2";
        Assert.Equal("done", (string?)(await Approved("activate", D1))["status"]);

        // A configuration release within D1's contract installs; one out of its bounds is refused.
        Assert.Equal(HttpStatusCode.Created, (await api.Consented(se, "/service/api/content/install", new { tag = $"config/{Bank}/1.0.0", reason = "values" })).StatusCode);
        var outOf = await J.Of(await api.Consented(se, "/service/api/content/install", new { tag = $"config/{Bank}/1.0.1", reason = "values" }));
        Assert.Contains("is above its maximum P7D", (string?)outOf["error"]);

        // A payment pinned to D1 at acceptance, still open.
        await using (var db = StoreDbContext.Open(api.Store)) await new DistributionRegistry(db).Pin("PAY-C1", Bank, "production", "front-door");

        // The difference of D2 against the active D1: the activity's release.
        var diff = await J.Of(await se.GetAsync($"/service/api/content/diff?tag={Uri.EscapeDataString($"dist/{Bank}/2026.11.22-2")}&environment=production"));
        Assert.Equal(D1, (string?)diff["active"]);
        Assert.Contains(diff["artefacts"]!.AsArray(), a => (string?)a!["key"] == "activity:uae.wps.exchange.submit-salary-file.v1" && (string?)a["change"] == "changed");
        Assert.Contains(diff["packs"]!.AsArray(), a => (string?)a!["key"] == "uae.wps");

        Assert.Equal(HttpStatusCode.Created, (await api.Consented(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.22-2", reason = "CR-2" })).StatusCode);
        var up = await Approved("activate", D2);
        Assert.Equal(D1, (string?)up["result"]!["draining"]);
        var rel = (await J.Of(await se.GetAsync("/service/api/content/releases"))).AsArray().Where(r => (string?)r!["kind"] == "distribution").ToDictionary(r => (string)r!["releaseId"]!);
        Assert.Equal("draining", (string?)rel[D1]!["state"]);
        Assert.Equal("active", (string?)rel[D2]!["state"]);
        Assert.Equal(1, (int)rel[D1]!["openPayments"]!);
        Assert.Equal(1, (int)(await J.Of(await se.GetAsync("/service/api/content/open-payments")))[D1]!);

        var refused = await Approved("remove", D1);
        Assert.Equal("refused", (string?)refused["status"]);
        Assert.Equal("service.RemovalRefused", (string?)refused["code"]);
        await using (var db = StoreDbContext.Open(api.Store)) await new DistributionRegistry(db).End("PAY-C1", "settled", "front-door");
        Assert.Equal("done", (string?)(await Approved("remove", D1))["status"]);

        // Drain by hand: no distribution takes new payments until one is activated; rollback to a removed one is refused.
        Assert.Equal("done", (string?)(await Approved("drain", D2))["status"]);
        await using (var db = StoreDbContext.Open(api.Store)) Assert.Null(await new DistributionRegistry(db).Active(Bank, "production"));
        var rb = await Approved("rollback", $"dist/{Bank}");
        Assert.Equal("refused", (string?)rb["status"]);

        await using var check = StoreDbContext.Open(api.Store);
        var codes = await check.Facts.AsNoTracking().Where(f => f.Kind == "service").Select(f => f.Code).ToListAsync();
        Assert.Contains("activate", codes);
        Assert.Contains("draining", codes);
        Assert.Contains("removed", codes);
        Assert.Contains("drain", codes);
    }

    /// <summary>
    /// Issue I-206, R-09-164, R-09-165: a payment the front door pinned whose start never reached an engine keeps its distribution
    /// open. The service console lists it, a service engineer asks to end it, a bank approver consents, and it ends abandoned; the
    /// distribution then drains and is removed. A pin whose payment runs is not offered, and the one who asked may not consent.
    /// </summary>
    [Fact]
    public async Task A_pin_with_no_payment_is_listed_ended_with_consent_and_its_distribution_drains_and_is_removed()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        gh.Publish($"dist/{Bank}/2026.11.23-1", Folder("2026.11.23-1"), Distribution(s.Key, "2026.11.23-1"));
        gh.Publish($"dist/{Bank}/2026.11.23-2", Folder("2026.11.23-2"), Distribution(s.Key, "2026.11.23-2", activityRelease: "1.0.1"));
        await using var api = new ServiceApi(dbs.Fresh(), s, gh, extra: new Dictionary<string, string?> { ["Service:StuckPinAfter"] = "PT0S" });
        var se = api.Engineer;
        var sa = api.Approver;
        async Task<JsonNode> Approved(string action, string target, object? parameters = null)
        {
            var ask = await J.Of(await Post(se, "/service/api/actions", new { action, target, reason = $"CR: {action}", parameters = parameters ?? new { environment = "production" } }));
            Assert.Equal("pending-approval", (string?)ask["status"]);
            return await J.Of(await Post(sa, $"/service/api/actions/{(string)ask["action"]!}/decision", new { approve = true, reason = "four eyes" }));
        }
        const string D1 = "dist/example-bank@2026.11.23-1", D2 = "dist/example-bank@2026.11.23-2";
        Assert.Equal(HttpStatusCode.Created, (await api.Consented(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.23-1", reason = "CR-1" })).StatusCode);
        Assert.Equal("done", (string?)(await Approved("activate", D1))["status"]);

        // Two payments pinned to D1: one the front door pinned and whose start then failed before any engine took it, and one an
        // engine's run accepted (its payload's create fact names it).
        await using (var db = StoreDbContext.Open(api.Store))
        {
            var reg = new DistributionRegistry(db);
            await reg.Pin("PAY-STUCK", Bank, "production", "front-door");
            await reg.Pin("PAY-RUNS", Bank, "production", "front-door");
            await new FactLog(db).Append("payload-PAY-RUNS", "payload", new NewFact("payload", "create", new JsonObject { ["paymentId"] = "PAY-RUNS" }, D1, "engine:temporal"));
        }
        var stuck = (await J.Of(await se.GetAsync("/service/api/content/stuck-pins"))).AsArray();
        Assert.Equal(["PAY-STUCK"], stuck.Select(p => (string)p!["paymentId"]!));
        Assert.Equal(D1, (string?)stuck[0]!["distribution"]);
        Assert.Equal("end-pin", (string?)stuck[0]!["action"]);

        Assert.Equal(HttpStatusCode.Created, (await api.Consented(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.23-2", reason = "CR-2" })).StatusCode);
        Assert.Equal("done", (string?)(await Approved("activate", D2))["status"]);
        Assert.Equal(2, (int)(await J.Of(await se.GetAsync("/service/api/content/open-payments")))[D1]!);

        // A pin whose payment runs is not a stuck pin: refused, and nothing ends.
        var notStuck = await Approved("end-pin", "PAY-RUNS");
        Assert.Equal("refused", (string?)notStuck["status"]);
        // The one who asked may not consent.
        var ask = await J.Of(await Post(se, "/service/api/actions", new { action = "end-pin", target = "PAY-STUCK", reason = "CR-3: the start failed (synthetic)" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(se, $"/service/api/actions/{(string)ask["action"]!}/decision", new { approve = true, reason = "mine" })).StatusCode);
        var ended = await J.Of(await Post(sa, $"/service/api/actions/{(string)ask["action"]!}/decision", new { approve = true, reason = "four eyes" }));
        Assert.Equal("done", (string?)ended["status"]);
        Assert.Equal("abandoned", (string?)ended["result"]!["ended"]);
        Assert.Empty((await J.Of(await se.GetAsync("/service/api/content/stuck-pins"))).AsArray());

        await using (var db = StoreDbContext.Open(api.Store))
        {
            var end = await db.Facts.AsNoTracking().SingleAsync(f => f.SubjectId == "PAY-STUCK" && f.Kind == DistributionRegistry.EndKind);
            Assert.Equal("abandoned", end.Code);
            Assert.Contains("the start failed", end.DataJson);
            Assert.Equal(D1, end.ReleaseId);
            // The running payment ends as payments do; D1 then has no open payment.
            await new DistributionRegistry(db).End("PAY-RUNS", "settled", "engine:temporal");
        }
        var rel = (await J.Of(await se.GetAsync("/service/api/content/releases"))).AsArray().Where(r => (string?)r!["kind"] == "distribution").ToDictionary(r => (string)r!["releaseId"]!);
        Assert.Equal("drained", (string?)rel[D1]!["state"]);
        Assert.Equal("done", (string?)(await Approved("remove", D1))["status"]);
    }
}
