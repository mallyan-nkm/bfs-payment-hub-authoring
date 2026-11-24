using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Service.Tests;

/// <summary>
/// The service console's trust page (R-09-249, Issue I-292): each trusted key with what it may sign and its scope, and the rates
/// feed's own key added by the rotation action, without hand editing, as kinds [configuration] and scope fx-rates. Keys of the test's own.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TrustPageTests(Databases dbs)
{
    static JsonObject Entry(ECDsa k, string[]? kinds = null, string? feed = null)
    {
        var e = new JsonObject { ["keyId"] = ReleaseSignature.KeyIdOf(k), ["publicKey"] = k.ExportSubjectPublicKeyInfoPem() };
        if (kinds is not null) e["kinds"] = new JsonArray(kinds.Select(x => (JsonNode?)x).ToArray());
        if (feed is not null) e["scope"] = new JsonObject { ["feed"] = feed };
        return e;
    }

    [Fact]
    public async Task The_trust_page_shows_what_each_key_may_sign_and_the_rotation_adds_the_rates_feeds_key_without_hand_editing()
    {
        using var s = new Signers();
        using var packages = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var feedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // The studio's key signs every kind; a second key signs workflow packages only.
        File.WriteAllText(s.TrustFile, new JsonObject
        {
            ["kind"] = "trusted-signers",
            ["keys"] = new JsonArray(Entry(s.Key), Entry(s.BackupKey), Entry(packages, ["workflow-package"])),
            ["revoked"] = new JsonArray(),
        }.ToJsonString());
        var feedKeyFile = Path.Combine(s.Dir, "fx-feed-key.pem");
        File.WriteAllText(feedKeyFile, feedKey.ExportPkcs8PrivateKeyPem());
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), new()
        {
            ["Service:TrustWritable"] = "true", ["Service:FxRates:SigningKeyRef"] = $"file:{feedKeyFile}",
        });
        var se = api.Engineer;

        var page = await J.Of(await se.GetAsync("/service/api/trust"));
        var keys = page["keys"]!.AsArray();
        var studio = keys.Single(k => (string?)k!["keyId"] == s.KeyId)!;
        Assert.True((bool)studio["everyKind"]!);
        Assert.Equal(ReleaseSignature.Kinds, studio["kinds"]!.AsArray().Select(x => (string)x!));
        var pk = keys.Single(k => (string?)k!["keyId"] == ReleaseSignature.KeyIdOf(packages))!;
        Assert.Equal((false, "workflow-package", "trusted"), ((bool)pk["everyKind"]!, (string?)pk["kinds"]![0], (string?)pk["state"]));
        var feed = page["feed"]!;
        Assert.Equal((ReleaseSignature.KeyIdOf(feedKey), false, "not-trusted"), ((string?)feed["keyId"], (bool)feed["trusted"]!, (string?)feed["state"]));
        Assert.Equal(["configuration"], feed["entry"]!["kinds"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("fx-rates", (string?)feed["entry"]!["scope"]!["feed"]);
        Assert.DoesNotContain("PRIVATE", page.ToJsonString());

        // The feed's key, added by the rotation action on a bank approver's consent: no PEM pasted, nothing retired.
        var added = await api.Act(se, new { action = "rotate-trusted-key", target = "none", reason = "trust the rates feed", parameters = new { feed = "fx-rates" } });
        Assert.Equal("done", (string?)added["status"]);
        var list = TrustedSigners.Parse(File.ReadAllText(s.TrustFile));
        var entry = list.Keys[ReleaseSignature.KeyIdOf(feedKey)];
        Assert.Equal(["configuration"], entry.Kinds!);
        Assert.Equal(("fx-rates", (DateTimeOffset?)null), (entry.Feed, entry.RetiredAt));
        Assert.All(list.Keys.Values.Where(k => k.KeyId != entry.KeyId), k => Assert.Null(k.RetiredAt));
        var after = await J.Of(await se.GetAsync("/service/api/trust"));
        Assert.Equal((true, "trusted"), ((bool)after["feed"]!["trusted"]!, (string?)after["feed"]!["state"]));
        Assert.Equal("fx-rates", (string?)after["keys"]!.AsArray().Single(k => (string?)k!["keyId"] == entry.KeyId)!["scope"]!["feed"]);

        // A rotation names what the new key may sign; a scope on a key that signs more than configuration releases is refused.
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rot = await api.Act(se, new { action = "rotate-trusted-key", target = ReleaseSignature.KeyIdOf(packages), reason = "packages key rotation", parameters = new { publicKey = next.ExportSubjectPublicKeyInfoPem(), kinds = new[] { "workflow-package", "distribution" } } });
        Assert.Equal("done", (string?)rot["status"]);
        list = TrustedSigners.Parse(File.ReadAllText(s.TrustFile));
        Assert.Equal(["workflow-package", "distribution"], list.Keys[ReleaseSignature.KeyIdOf(next)].Kinds!);
        Assert.NotNull(list.Keys[ReleaseSignature.KeyIdOf(packages)].RetiredAt);
        using var bad = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var refused = await api.Act(se, new { action = "rotate-trusted-key", target = "none", reason = "wrong scope", parameters = new { publicKey = bad.ExportSubjectPublicKeyInfoPem(), kinds = new[] { "distribution" }, scope = new { feed = "fx-rates" } } });
        Assert.Equal("signing.TrustListInvalid", (string?)refused["code"]);
        Assert.False(TrustedSigners.Parse(File.ReadAllText(s.TrustFile)).Keys.ContainsKey(ReleaseSignature.KeyIdOf(bad)));
    }

    [Fact]
    public async Task A_host_that_runs_no_feed_shows_no_feed_key_and_refuses_to_add_one()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), new() { ["Service:TrustWritable"] = "true" });
        var page = await J.Of(await api.Engineer.GetAsync("/service/api/trust"));
        Assert.Null(page["feed"]);
        var r = await api.Act(api.Engineer, new { action = "rotate-trusted-key", target = "none", reason = "no feed", parameters = new { feed = "fx-rates" } });
        Assert.Equal("signing.TrustListInvalid", (string?)r["code"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.As("").GetAsync("/service/api/trust")).StatusCode);
    }
}
