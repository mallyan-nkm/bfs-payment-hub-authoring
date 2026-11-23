using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using TransactHub.Api;
using TransactHub.Security;

namespace TransactHub.Store.Tests;

// The north-star review of 23 November 2026, at the release API, on SQL Server and PostgreSQL. A bank's values arrive only in a
// signed configuration release, checked against the parameter contract of every distribution not removed (R-09-110), and are put
// in force by the pipeline only where the runtime's own environment is one it may change (R-09-111): elsewhere the release waits
// for a bank approver's consent, asked for in the service console's book. A branch's folders bring no values. The port binding
// in force is that of the latest configuration release that binds the port (R-09-112). A workflow package is not stored or
// activated beside a distribution (R-09-114).

[Collection(DatabaseCollection.Name)]
public sealed class ValuesConsentApiTests(Databases dbs)
{
    const string Bank = "example-bank";
    const string Limit = "  - parameter: b.rule.internal-limit.v1#internalLimit\n    value: 5000\n    validFrom: 2026-12-01\n";
    static async Task<JsonNode> J(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    static ByteArrayContent Body(byte[] b) => new(b);

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Outside_its_test_environments_a_configuration_release_from_the_pipeline_is_installed_checked_and_waits_for_consent(StoreProvider p)
    {
        using var s = new TestSigners();
        var o = dbs.Fresh(p);
        await using var host = new ApiHost(o, s, b => b.UseSetting("Releases:MachineActivationEnvironments", null).UseSetting("Releases:Environment", "production"));
        var c = host.Client();
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/distributions", Body(DistributionApiTests.Distribution(s.Key, "2026.11.23-1")))).StatusCode);
        // Still checked at install: a value out of its bounds is refused, before anything waits.
        var outOf = await c.PostAsync("/configuration-releases", Body(DistributionApiTests.Configuration(s.Key, "1.0.0", "  - parameter: b.rule.internal-limit.v1#internalLimit\n    value: 9999999\n    validFrom: 2026-12-01\n")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outOf.StatusCode);
        Assert.Contains("is above its maximum", (string?)(await J(outOf))["error"]);

        var binding = "kind: port-bindings\nbindings:\n  - port: p.port.core-banking.v1\n    adapter: simulator\n    validFrom: 2026-12-01\n    settings:\n      simulator: p.sim.core-banking.v1\n";
        var zip = DistributionApiTests.Configuration(s.Key, "1.1.0", Limit, binding);
        var r = await c.PostAsync("/configuration-releases", Body(zip));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var j = await J(r);
        Assert.Equal(("awaiting-consent", false, "BankApprover"), ((string?)j["state"], (bool)j["inForce"]!, (string?)j["needs"]));
        var action = (string)j["action"]!;
        // Nothing of it is in force: no value, no binding.
        Assert.DoesNotContain((await J(await c.GetAsync($"/values?tenant={Bank}&at=2026-12-02T00:00:00Z"))).AsArray(), v => (string?)v!["parameter"] == "b.rule.internal-limit.v1#internalLimit");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/bindings?tenant={Bank}&port=p.port.core-banking.v1&at=2026-12-02T00:00:00Z")).StatusCode);
        // Sent again: the same request waits, not a second one.
        var again = await c.PostAsync("/configuration-releases", Body(zip));
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(action, (string?)(await J(again))["action"]);
        await using var db = StoreDbContext.Open(o);
        var asked = await db.Facts.AsNoTracking().Where(f => f.SubjectId == ConsentRequests.Subject && f.Code == "action.requested").ToListAsync();
        var one = Assert.Single(asked);
        var d = JsonNode.Parse(one.DataJson)!;
        Assert.Equal((action, "put-in-force", $"config/{Bank}@1.1.0", true), ((string?)d["action"], (string?)d["name"], (string?)d["target"], (bool)d["risky"]!));
        Assert.Equal("pipeline", one.Actor);
        Assert.False(await db.Facts.AnyAsync(f => f.SubjectId == $"config/{Bank}@1.1.0" && f.Kind == DistributionRegistry.InForceKind));
        Assert.Empty(await db.VersionedValues.Where(v => v.Tenant == Bank).ToListAsync());
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_branch_with_a_banks_configuration_set_brings_no_values(StoreProvider p)
    {
        using var s = new TestSigners();
        var branch = ReleaseApiTests.Branch(s);
        try
        {
            Directory.CreateDirectory(Path.Combine(branch, "banks", Bank));
            File.WriteAllText(Path.Combine(branch, "banks", Bank, "configuration-set.yaml"), $"kind: configuration-set\ntenant: {Bank}\nrelease: 9.9.9\nvalues:\n{Limit}bindings: []\n");
            var o = dbs.Fresh(p);
            await using var host = new ApiHost(o, s);
            var r = (await J(await host.Client().PostAsJsonAsync("/import-branch", new { path = branch }))).AsObject();
            Assert.Empty(r["problems"]!.AsArray());
            Assert.False(r.ContainsKey("values"));
            await using var db = StoreDbContext.Open(o);
            Assert.False(await db.VersionedValues.AnyAsync());
        }
        finally { Directory.Delete(branch, recursive: true); }
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_binding_in_force_is_the_latest_configuration_releases_from_its_validFrom(StoreProvider p)
    {
        using var s = new TestSigners();
        var o = dbs.Fresh(p);
        await using var host = new ApiHost(o, s);
        var c = host.Client();
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/distributions", Body(DistributionApiTests.Distribution(s.Key, "2026.11.23-1")))).StatusCode);
        const string sim = "  - port: p.port.core-banking.v1\n    adapter: simulator\n    validFrom: 2026-12-01\n    settings:\n      simulator: p.sim.core-banking.v1\n";
        const string gw = "  - port: p.port.core-banking.v1\n    adapter: gateway-rest\n    validFrom: 2027-02-01\n    secretRef: env:TH_CORE_TOKEN\n    settings:\n      baseUrl: https://core.bank.example.test\n";
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/configuration-releases", Body(DistributionApiTests.Configuration(s.Key, "1.1.0", Limit, "kind: port-bindings\nbindings:\n" + sim + gw)))).StatusCode);
        async Task<string?> At(string at) => (string?)(await J(await c.GetAsync($"/bindings?tenant={Bank}&port=p.port.core-banking.v1&at={at}")))["adapter"];
        Assert.Equal("simulator", await At("2027-01-15T00:00:00Z"));
        Assert.Equal("gateway-rest", await At("2027-02-02T00:00:00Z"));
        // A later release is the bank's whole set of bindings for the port: it drops the gateway, so the simulator stays.
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/configuration-releases", Body(DistributionApiTests.Configuration(s.Key, "1.2.0", Limit, "kind: port-bindings\nbindings:\n" + sim)))).StatusCode);
        Assert.Equal("simulator", await At("2027-02-02T00:00:00Z"));
        await using var db = StoreDbContext.Open(o);
        var all = ConfigurationBindings.Load(o, Bank);
        Assert.Equal(3, all.Count);
        Assert.Equal("1.2.0", ConfigurationBindings.InForce(all, Bank, "p.port.core-banking.v1", DateTimeOffset.Parse("2027-02-02T00:00:00Z"))!.Release);
        Assert.Equal(1, ReleaseOrder.Compare("50.0.0+bindings.1.2.0", "50.0.0"));
        Assert.Equal(-1, ReleaseOrder.Compare("1.9.0", "1.10.0"));
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_workflow_package_is_not_stored_or_activated_beside_a_distribution(StoreProvider p)
    {
        using var s = new TestSigners();
        await using var host = new ApiHost(dbs.Fresh(p), s);
        var c = host.Client();
        // Before the bank has a distribution, a workflow package is stored and activated as before.
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/releases", Body(ReleaseApiTests.Zip(s, ReleaseApiTests.PackageFiles())))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/distributions", Body(DistributionApiTests.Distribution(s.Key, "2026.11.23-1")))).StatusCode);
        var stored = await c.PostAsync("/releases", Body(ReleaseApiTests.Zip(s, ReleaseApiTests.PackageFiles("1.0.1"), "1.0.1")));
        Assert.Equal(HttpStatusCode.Conflict, stored.StatusCode);
        Assert.Contains("runs example-bank's distributions", (string?)(await J(stored))["error"]);
        var act = await c.PostAsJsonAsync("/activations", new { environment = "test", scope = "p.wf.corporate.v1/temporal", release = "1.0.0" });
        Assert.Equal(HttpStatusCode.Conflict, act.StatusCode);
        Assert.Contains("(R-09-114)", (string?)(await J(act))["error"]);
    }
}
