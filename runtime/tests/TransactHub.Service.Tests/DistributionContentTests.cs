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
/// The service console's distributions and configuration releases (R-09-40 as amended, R-09-47 to R-09-56, Issue I-102 done):
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

    public static Dictionary<string, byte[]> Distribution(ECDsa key, string id, string activityRelease = "1.0.0", string ackMax = "P7D")
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
        files["manifest.yaml"] = new JsonObject { ["kind"] = "distribution", ["id"] = id, ["bank"] = Bank, ["engine"] = "temporal", ["schemes"] = new JsonArray("uae.wps"), ["longestWait"] = "PT0S", ["billOfMaterials"] = bom.DeepClone(), ["parameterContract"] = pc.DeepClone() }.ToJsonString();
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var (st, sig) = ReleaseSignature.Sign(bytes, new ReleaseClaims($"dist/{Bank}@{id}", "", id, "temporal", Bank, "CS-TEST-010", ReleaseSignature.Distribution, new JsonObject { ["billOfMaterials"] = bom, ["parameterContract"] = pc }), key, DateTimeOffset.UtcNow);
        bytes[ReleaseSignature.StatementFile] = st;
        bytes[ReleaseSignature.SignatureFile] = sig;
        return bytes;
    }

    static Dictionary<string, byte[]> Configuration(ECDsa key, string release, string ack)
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
        var bad = await Post(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.22-3", reason = "try" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("signing.Tampered", (string?)(await J.Of(bad))["code"]);
        // A configuration release with no distribution to check it against is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await Post(se, "/service/api/content/install", new { tag = $"config/{Bank}/1.0.0", reason = "early" })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await Post(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.22-1", reason = "CR-1" })).StatusCode);
        async Task<JsonNode> Approved(string action, string target, object? parameters = null)
        {
            var ask = await J.Of(await Post(se, "/service/api/actions", new { action, target, reason = $"CR: {action}", parameters = parameters ?? new { environment = "production" } }));
            return await J.Of(await Post(sa, $"/service/api/actions/{(string)ask["action"]!}/decision", new { approve = true, reason = "four eyes" }));
        }
        const string D1 = "dist/example-bank@2026.11.22-1", D2 = "dist/example-bank@2026.11.22-2";
        Assert.Equal("done", (string?)(await Approved("activate", D1))["status"]);

        // A configuration release within D1's contract installs; one out of its bounds is refused.
        Assert.Equal(HttpStatusCode.Created, (await Post(se, "/service/api/content/install", new { tag = $"config/{Bank}/1.0.0", reason = "values" })).StatusCode);
        var outOf = await J.Of(await Post(se, "/service/api/content/install", new { tag = $"config/{Bank}/1.0.1", reason = "values" }));
        Assert.Contains("is above its maximum P7D", (string?)outOf["error"]);

        // A payment pinned to D1 at acceptance, still open.
        await using (var db = StoreDbContext.Open(api.Store)) await new DistributionRegistry(db).Pin("PAY-C1", Bank, "production", "front-door");

        // The difference of D2 against the active D1: the activity's release.
        var diff = await J.Of(await se.GetAsync($"/service/api/content/diff?tag={Uri.EscapeDataString($"dist/{Bank}/2026.11.22-2")}&environment=production"));
        Assert.Equal(D1, (string?)diff["active"]);
        Assert.Contains(diff["artefacts"]!.AsArray(), a => (string?)a!["key"] == "activity:uae.wps.exchange.submit-salary-file.v1" && (string?)a["change"] == "changed");
        Assert.Contains(diff["packs"]!.AsArray(), a => (string?)a!["key"] == "uae.wps");

        Assert.Equal(HttpStatusCode.Created, (await Post(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.22-2", reason = "CR-2" })).StatusCode);
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
}
