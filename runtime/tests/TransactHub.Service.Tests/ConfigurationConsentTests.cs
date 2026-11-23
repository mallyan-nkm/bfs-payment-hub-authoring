using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Api;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// The north-star review's findings of 23 November 2026, in the service console. A bank's values reach the runtime only in a
/// signed configuration release, checked against the parameter contract of every distribution not removed, and are put in force
/// only on the consent of a bank approver who did not ask for it (R-09-110, R-09-111): a configuration release the release API's
/// pipeline installed outside its test environments waits, its consent asked for in the console's book; the bounds are checked
/// again when it is put in force. A workflow package is not installed beside a distribution (R-09-114). The earlier role name
/// ServiceApprover gives nothing (R-09-115).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ConfigurationConsentTests(Databases dbs)
{
    const string Bank = Seed.Tenant;
    const string Ack = "uae.wps.exchange.submit-salary-file.v1#ackWait";

    static async Task<object?> AckAt(StoreOptions o, string at)
    {
        await using var db = StoreDbContext.Open(o);
        return (await BankValues.InForce(db, Bank, DateTimeOffset.Parse(at))).FirstOrDefault(v => (string?)v["parameter"] == Ack)?["value"]?.ToString();
    }

    /// <summary>What the release API does with a configuration release outside the environments its pipeline may change: installed, checked, waiting.</summary>
    static async Task<string> PipelineInstalls(StoreOptions o, Signers s, Dictionary<string, byte[]> files)
    {
        await using var db = StoreDbContext.Open(o);
        var svc = new DistributionService(db, TimeProvider.System, ct => TrustedSigners.Load(File.ReadAllText(s.TrustFile), SecretResolver.FromEnvironment(_ => null), ct), null, new RuntimeBank(Bank));
        var b = Bundle.FromFiles(files, ReleaseSignature.ConfigurationRelease);
        var r = await svc.InstallConfiguration(b, "pipeline", "upload", putInForce: false);
        Assert.False((bool)r["inForce"]!);
        Assert.Equal("awaiting-consent", (string?)r["state"]);
        return await ConsentRequests.Ask(db, TimeProvider.System, ConsentRequests.PutInForce, b.ReleaseId, "upload: installed by the release API in production", "pipeline");
    }

    [Fact]
    public async Task A_configuration_release_the_pipeline_installed_is_in_force_only_once_a_bank_approver_consents_and_its_bounds_hold_then()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        gh.Publish($"dist/{Bank}/2026.11.23-1", $"content-configurations/releases/distributions/{Bank}/2026.11.23-1", DistributionContentTests.Distribution(s.Key, "2026.11.23-1"));
        gh.Publish($"dist/{Bank}/2026.11.23-2", $"content-configurations/releases/distributions/{Bank}/2026.11.23-2", DistributionContentTests.Distribution(s.Key, "2026.11.23-2", activityRelease: "1.0.1", ackMax: "P3D"));
        await using var api = new ServiceApi(dbs.Fresh(), s, gh);
        var se = api.Engineer;
        Assert.Equal(HttpStatusCode.Created, (await api.Consented(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.23-1", reason = "CR-20 (synthetic)" })).StatusCode);

        // Installed by the pipeline: checked and stored, nothing of it in force; its consent is asked for in the console's book.
        var id = await PipelineInstalls(api.Store, s, DistributionContentTests.Configuration(s.Key, "2.0.0", "P2D"));
        Assert.Null(await AckAt(api.Store, "2026-12-02T00:00:00Z"));
        var pending = (await J.Of(await api.BankApprover.GetAsync("/service/api/actions/pending"))).AsArray();
        var ask = Assert.Single(pending, p => (string?)p!["data"]!["action"] == id)!;
        Assert.Equal(("put-in-force", $"config/{Bank}@2.0.0", "pipeline"), ((string?)ask["data"]!["name"], (string?)ask["data"]!["target"], (string?)ask["actor"]));
        var listed = (await J.Of(await se.GetAsync("/service/api/content/releases"))).AsArray().Single(r => (string?)r!["releaseId"] == $"config/{Bank}@2.0.0")!;
        Assert.Equal("awaiting-consent", (string?)listed["state"]);

        // A service engineer may not consent; a bank approver may, and then the values are in force, with the consent recorded.
        Assert.Equal(HttpStatusCode.Forbidden, (await se.PostAsJsonAsync($"/service/api/actions/{id}/decision", new { approve = true, reason = "mine" })).StatusCode);
        var done = await J.Of(await api.BankApprover.PostAsJsonAsync($"/service/api/actions/{id}/decision", new { approve = true, reason = "Values agreed with operations (synthetic)" }));
        Assert.True((string?)done["status"] == "done", done.ToJsonString());
        Assert.Equal("P2D", await AckAt(api.Store, "2026-12-02T00:00:00Z"));
        await using (var db = StoreDbContext.Open(api.Store))
        {
            var f = await db.Facts.AsNoTracking().SingleAsync(x => x.SubjectId == $"config/{Bank}@2.0.0" && x.Kind == DistributionRegistry.InForceKind);
            Assert.Equal("service:ba-1", (string?)JsonNode.Parse(f.DataJson)!["consent"]!["consentedBy"]);
            Assert.True((await new FactLog(db).Verify($"config/{Bank}@2.0.0")).Ok);
        }
        Assert.Equal("in-force", (string?)(await J.Of(await se.GetAsync("/service/api/content/releases"))).AsArray().Single(r => (string?)r!["releaseId"] == $"config/{Bank}@2.0.0")!["state"]);

        // Installed within D1's bounds, then a distribution with a narrower bound: putting it in force checks again, and refuses it.
        var late = await PipelineInstalls(api.Store, s, DistributionContentTests.Configuration(s.Key, "2.1.0", "P5D"));
        Assert.Equal(HttpStatusCode.Created, (await api.Consented(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.23-2", reason = "CR-21 (synthetic)" })).StatusCode);
        var refused = await J.Of(await api.BankApprover.PostAsJsonAsync($"/service/api/actions/{late}/decision", new { approve = true, reason = "agreed (synthetic)" }));
        Assert.Equal("refused", (string?)refused["status"]);
        Assert.Contains("is above its maximum P3D", (string?)refused["result"]!["message"]);
        Assert.Equal("P2D", await AckAt(api.Store, "2026-12-02T00:00:00Z"));
        // And an engineer who asks for it again is refused when asking, before any consent.
        var again = await se.PostAsJsonAsync("/service/api/actions", new { action = "put-in-force", target = $"config/{Bank}@2.1.0", reason = "try again (synthetic)" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
    }

    [Fact]
    public async Task A_workflow_package_is_not_installed_or_activated_beside_a_distribution()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        Packages.Publish(gh, s.Key, "p.wf.corporate.v1", "1.0.0");
        Packages.Publish(gh, s.Key, "p.wf.corporate.v1", "1.0.1");
        gh.Publish($"dist/{Bank}/2026.11.23-1", $"content-configurations/releases/distributions/{Bank}/2026.11.23-1", DistributionContentTests.Distribution(s.Key, "2026.11.23-1"));
        await using var api = new ServiceApi(dbs.Fresh(), s, gh);
        var se = api.Engineer;
        // Before any distribution, a workflow package still installs (a test bench's runtime).
        Assert.Equal(HttpStatusCode.Created, (await api.Consented(se, "/service/api/content/install", new { tag = FakeGitHub.Tag("p.wf.corporate.v1", "1.0.0"), reason = "CR-30 (synthetic)" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await api.Consented(se, "/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.23-1", reason = "CR-31 (synthetic)" })).StatusCode);

        var versions = (await J.Of(await se.GetAsync("/service/api/content/versions"))).AsArray();
        var wp = versions.Single(v => (string?)v!["tag"] == FakeGitHub.Tag("p.wf.corporate.v1", "1.0.1"))!;
        Assert.False((bool)wp["installable"]!);
        Assert.Contains("runs example-bank's distributions", (string?)wp["notInstallable"]);
        var install = await se.PostAsJsonAsync("/service/api/content/install", new { tag = FakeGitHub.Tag("p.wf.corporate.v1", "1.0.1"), reason = "try (synthetic)" });
        Assert.Equal(HttpStatusCode.Conflict, install.StatusCode);
        Assert.Equal("package.KindNotInstallable", (string?)(await J.Of(install))["code"]);
        var act = await api.Act(se, new { action = "activate", target = Packages.Id("p.wf.corporate.v1", "1.0.0"), reason = "try (synthetic)", parameters = new { environment = "production" } });
        Assert.Equal("refused", (string?)act["status"]);
        Assert.Equal("package.KindNotInstallable", (string?)act["code"]);
        await using var db = StoreDbContext.Open(api.Store);
        Assert.False(await db.Activations.AnyAsync(a => a.Scope == "p.wf.corporate.v1/temporal"));
    }

    [Fact]
    public async Task The_earlier_role_name_ServiceApprover_gives_no_consent()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub());
        Assert.DoesNotContain("ServiceApprover", ServiceRoles.All);
        var old = api.As("old-1", "*:ServiceApprover");
        Assert.False((bool?)(await J.Of(await old.GetAsync("/service/api/me")))["service"] ?? false);
        var ask = await J.Of(await api.Engineer.PostAsJsonAsync("/service/api/actions", new { action = "pause-intake", target = "uae.wps", reason = "window closed (synthetic)", parameters = new { tenant = Bank } }));
        Assert.Equal(HttpStatusCode.Forbidden, (await old.PostAsJsonAsync($"/service/api/actions/{(string)ask["action"]!}/decision", new { approve = true, reason = "old role" })).StatusCode);
    }
}
