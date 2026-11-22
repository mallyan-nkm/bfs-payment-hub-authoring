using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// Content install, update and upgrade (R-09-40): GitHub as the artefact repository, read only; a signed version verified
/// and stored, and an unsigned, a tampered and an untrusted one refused; the difference against what is active; activation
/// from a date (an approver's), the old release draining, rollback, and removal refused while a payment is open. Every
/// action is a fact with who, when and why.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ContentTests(Databases dbs)
{
    const string Wf = "p.wf.demo.v1";

    static async Task<HttpResponseMessage> Post(HttpClient c, string path, object body) => await c.PostAsJsonAsync(path, body);

    [Fact]
    public async Task A_signed_version_is_installed_and_an_unsigned_a_tampered_or_an_untrusted_one_is_refused_and_recorded()
    {
        using var s = new Signers();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var gh = new FakeGitHub();
        Packages.Publish(gh, s.Key, Wf, "1.0.0");
        gh.Publish(FakeGitHub.Tag(Wf, "1.0.1"), FakeGitHub.Folder(Wf, "1.0.1"), Packages.Files(Wf, "1.0.1").ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value)));
        var tampered = Signers.Sign(Packages.Files(Wf, "1.0.2"), s.Key, Packages.Id(Wf, "1.0.2"));
        tampered[$"workflows/{Wf}@1.0.2.yaml"] = "kind: workflow\n# changed on GitHub\n"u8.ToArray();
        gh.Publish(FakeGitHub.Tag(Wf, "1.0.2"), FakeGitHub.Folder(Wf, "1.0.2"), tampered);
        gh.Publish(FakeGitHub.Tag(Wf, "1.0.3"), FakeGitHub.Folder(Wf, "1.0.3"), Signers.Sign(Packages.Files(Wf, "1.0.3"), stranger, Packages.Id(Wf, "1.0.3")));
        gh.Publish("dist/example-bank/2026.11.22-1", "content-configurations/releases/distributions/example-bank/2026.11.22-1", new() { ["manifest.yaml"] = "kind: distribution\n"u8.ToArray() });
        await using var api = new ServiceApi(dbs.Fresh(), s, gh);
        var c = api.Engineer;

        var versions = (await J.Of(await c.GetAsync("/service/api/content/versions"))).AsArray();
        Assert.Equal(5, versions.Count);
        var dist = versions.Single(v => (string?)v!["kind"] == "distribution")!;
        Assert.True((bool)dist["installable"]!);
        Assert.Equal("dist/example-bank@2026.11.22-1", (string?)dist["releaseId"]);
        Assert.All(versions, v => Assert.Equal("offered", (string?)v!["state"]));

        var verify = await J.Of(await Post(c, "/service/api/content/verify", new { tag = FakeGitHub.Tag(Wf, "1.0.0"), reason = "before install" }));
        Assert.True((bool)verify["verified"]!);
        Assert.Equal(s.KeyId, (string?)verify["keyId"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, "/service/api/content/install", new { tag = FakeGitHub.Tag(Wf, "1.0.0") })).StatusCode);
        var install = await Post(c, "/service/api/content/install", new { tag = FakeGitHub.Tag(Wf, "1.0.0"), reason = "CR-1: first release" });
        Assert.Equal(HttpStatusCode.Created, install.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/service/api/content/install", new { tag = FakeGitHub.Tag(Wf, "1.0.0"), reason = "again" })).StatusCode);

        foreach (var (release, code, why) in new[] { ("1.0.1", "signing.NotSigned", "is not signed"), ("1.0.2", "signing.Tampered", "does not match its hash"), ("1.0.3", "signing.UntrustedKey", "does not trust") })
        {
            var r = await Post(c, "/service/api/content/install", new { tag = FakeGitHub.Tag(Wf, release), reason = "try" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            var j = await J.Of(r);
            Assert.Equal(code, (string?)j["code"]);
            Assert.Contains(why, (string?)j["error"]);
            Assert.Equal($"#/service/errors/{code}", (string?)j["runbook"]);
        }
        var d = await Post(c, "/service/api/content/install", new { tag = "dist/example-bank/2026.11.22-1", reason = "try" });
        // A distribution is installed by its own kind (R-09-49): this one is not signed, so it is refused.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, d.StatusCode);
        Assert.Equal("package.Rejected", (string?)(await J.Of(d))["code"]);

        await using var db = StoreDbContext.Open(api.Store);
        Assert.Equal(1, await db.Releases.CountAsync());
        var audit = await db.Facts.AsNoTracking().Where(f => f.Kind == "service").OrderBy(f => f.Id).ToListAsync();
        var installs = audit.Where(f => f.Code == "install").Select(f => JsonNode.Parse(f.DataJson)!).ToList();
        Assert.Equal(["installed", "already installed", "refused", "refused", "refused", "refused"], installs.Select(x => (string)x["result"]!));
        Assert.All(audit, f => Assert.Equal("service:se-1", f.Actor));
        Assert.Contains(installs, x => (string?)x["why"] == "CR-1: first release");
        Assert.Equal("signing.Tampered", (string?)installs[3]["error"]);
        var released = await db.Facts.AsNoTracking().Where(f => f.SubjectId == Packages.Id(Wf, "1.0.0")).Select(f => f.Kind).ToListAsync();
        Assert.Contains("release.verified", released);
    }

    [Fact]
    public async Task Activation_needs_an_approver_the_old_release_drains_rollback_reverts_and_removal_waits_for_open_payments()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        Packages.Publish(gh, s.Key, Wf, "1.0.0");
        Packages.Publish(gh, s.Key, Wf, "1.1.0", limit: "300000");
        await using var api = new ServiceApi(dbs.Fresh(), s, gh);
        var se = api.Engineer;
        var sa = api.Approver;
        foreach (var r in new[] { "1.0.0", "1.1.0" }) await Post(se, "/service/api/content/install", new { tag = FakeGitHub.Tag(Wf, r), reason = "install" });

        async Task<JsonNode> Activate(string release, HttpClient approver)
        {
            var ask = await Post(se, "/service/api/actions", new { action = "activate", target = Packages.Id(Wf, release), reason = $"CR: go live {release}", parameters = new { environment = "production" } });
            Assert.Equal(HttpStatusCode.Accepted, ask.StatusCode);
            var id = (string)(await J.Of(ask))["action"]!;
            Assert.Equal(HttpStatusCode.Forbidden, (await Post(se, $"/service/api/actions/{id}/decision", new { approve = true, reason = "mine" })).StatusCode);
            return await J.Of(await Post(approver, $"/service/api/actions/{id}/decision", new { approve = true, reason = "four eyes" }));
        }

        Assert.Equal("done", (string?)(await Activate("1.0.0", sa))["status"]);
        // A payment accepted on 1.0.0, still open.
        var bulk = await Seed.Bulk(api.Store, Packages.Id(Wf, "1.0.0"));
        await api.CatchUp();

        // The difference of 1.1.0 against what is active: the workflow's version and the parameter's default.
        var diff = await J.Of(await se.GetAsync($"/service/api/content/diff?tag={Uri.EscapeDataString(FakeGitHub.Tag(Wf, "1.1.0"))}&environment=production"));
        Assert.Equal(Packages.Id(Wf, "1.0.0"), (string?)diff["active"]);
        Assert.Equal("changed", (string?)diff["flows"]![0]!["change"]);
        var p = diff["parameters"]!.AsArray().Single()!;
        Assert.Equal("p.validate.limits.v1#maxAmount", (string?)p["key"]);
        Assert.Contains("300000", (string?)p["offered"]);

        var up = await Activate("1.1.0", sa);
        Assert.Equal(Packages.Id(Wf, "1.0.0"), (string?)up["result"]!["draining"]);
        var rel = (await J.Of(await se.GetAsync("/service/api/content/releases"))).AsArray().ToDictionary(r => (string)r!["releaseId"]!);
        Assert.Equal("active", (string?)rel[Packages.Id(Wf, "1.1.0")]!["state"]);
        Assert.Equal("draining", (string?)rel[Packages.Id(Wf, "1.0.0")]!["state"]);
        Assert.Equal(1, (int)rel[Packages.Id(Wf, "1.0.0")]!["openPayments"]!);
        var open = await J.Of(await se.GetAsync("/service/api/content/open-payments"));
        Assert.Equal(1, (int)open[Packages.Id(Wf, "1.0.0")]!);

        // Removal is refused while a payment pinned to it is open, and while a release is active.
        async Task<JsonNode> Remove(string release)
        {
            var id = (string)(await J.Of(await Post(se, "/service/api/actions", new { action = "remove", target = Packages.Id(Wf, release), reason = "tidy" })))["action"]!;
            return await J.Of(await Post(sa, $"/service/api/actions/{id}/decision", new { approve = true, reason = "ok" }));
        }
        var refused = await Remove("1.0.0");
        Assert.Equal("refused", (string?)refused["status"]);
        Assert.Equal("service.RemovalRefused", (string?)refused["code"]);
        Assert.Contains("1 open payment", (string?)refused["result"]!["message"]);
        Assert.Contains("is active", (string?)(await Remove("1.1.0"))["result"]!["message"]);

        // Rollback: 1.0.0 again; payments of 1.1.0 would finish on it.
        var id2 = (string)(await J.Of(await Post(se, "/service/api/actions", new { action = "rollback", target = $"{Wf}/temporal", reason = "incident INC-1", parameters = new { environment = "production" } })))["action"]!;
        var rb = await J.Of(await Post(sa, $"/service/api/actions/{id2}/decision", new { approve = true, reason = "incident" }));
        Assert.Equal(Packages.Id(Wf, "1.1.0"), (string?)rb["result"]!["rolledBackFrom"]);
        rel = (await J.Of(await se.GetAsync("/service/api/content/releases"))).AsArray().ToDictionary(r => (string)r!["releaseId"]!);
        Assert.Equal("active", (string?)rel[Packages.Id(Wf, "1.0.0")]!["state"]);
        Assert.Equal("drained", (string?)rel[Packages.Id(Wf, "1.1.0")]!["state"]);
        Assert.Equal("done", (string?)(await Remove("1.1.0"))["status"]);
        rel = (await J.Of(await se.GetAsync("/service/api/content/releases"))).AsArray().ToDictionary(r => (string)r!["releaseId"]!);
        Assert.Equal("removed", (string?)rel[Packages.Id(Wf, "1.1.0")]!["state"]);

        await using var db = StoreDbContext.Open(api.Store);
        var codes = await db.Facts.AsNoTracking().Where(f => f.Kind == "service" && f.SubjectKind == "release").Select(f => f.Code).ToListAsync();
        Assert.Contains("activate", codes);
        Assert.Contains("draining", codes);
        Assert.Contains("removed", codes);
        Assert.Equal(3, await db.Activations.CountAsync());
    }
}
