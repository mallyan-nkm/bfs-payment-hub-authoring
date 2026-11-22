using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TransactHub.Security;

namespace TransactHub.Store.Tests;

// A bank's distributions and configuration releases (docs/runtime/PACKAGING-AND-DEPLOYMENT.md, R-09-48 to R-09-57), on SQL
// Server and PostgreSQL: stored insert-only after their signature and kind verify; the same id with other content refused;
// activation per bank and environment moves the one active to draining in the same step; a payment is pinned at acceptance
// and its end is a fact; removal is refused while a pinned payment is open or before the longest declared wait has passed;
// rollback; a configuration release checked against the parameter contract of every distribution not removed.

[Collection(DatabaseCollection.Name)]
public sealed class DistributionApiTests(Databases dbs)
{
    const string Bank = "example-bank";
    static string Sha(string s) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    static JsonObject Contract(string ackMax = "P7D") => new()
    {
        ["format"] = "th-parameter-contract/1",
        ["parameters"] = new JsonArray(
            new JsonObject { ["parameter"] = "uae.wps.exchange.submit-salary-file.v1#ackWait", ["type"] = "duration", ["default"] = "P1D", ["bounds"] = new JsonObject { ["min"] = "PT1H", ["max"] = ackMax } },
            new JsonObject { ["parameter"] = "b.rule.internal-limit.v1#internalLimit", ["type"] = "amount", ["default"] = 1000, ["bounds"] = new JsonObject { ["min"] = 0, ["max"] = 1000000 } }),
    };

    /// <summary>A distribution as the studio builds it: its files, a manifest with the bill of materials and the parameter contract, signed.</summary>
    static byte[] Distribution(ECDsa key, string id, string body = "kind: workflow\nname: p.wf.corporate.v1\n", string longestWait = "P30D", JsonObject? contract = null, string kind = "distribution")
    {
        var files = new Dictionary<string, string> { ["workflows/p.wf.corporate.v1@1.0.0.yaml"] = body, ["binding.yaml"] = "kind: engine-binding\ntarget: temporal\n" };
        var bom = new JsonObject
        {
            ["packs"] = new JsonArray(new JsonObject { ["scope"] = "p", ["release"] = "1.1.0" }),
            ["artefacts"] = new JsonArray(files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["sha256"] = Sha(f.Value) }).ToArray()),
        };
        var pc = contract ?? Contract();
        var manifest = new JsonObject { ["kind"] = "distribution", ["id"] = id, ["bank"] = Bank, ["engine"] = "temporal", ["longestWait"] = longestWait, ["billOfMaterials"] = bom.DeepClone(), ["parameterContract"] = pc.DeepClone() };
        files["manifest.yaml"] = manifest.ToJsonString();
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var (st, sig) = ReleaseSignature.Sign(bytes, new ReleaseClaims($"dist/{Bank}@{id}", "", id, "temporal", Bank, "CS-TEST-001", kind, new JsonObject { ["billOfMaterials"] = bom, ["parameterContract"] = pc }), key, DateTimeOffset.UtcNow);
        bytes[ReleaseSignature.StatementFile] = st;
        bytes[ReleaseSignature.SignatureFile] = sig;
        return TestSigners.Zip(bytes);
    }

    static byte[] Configuration(ECDsa key, string release, string values, string? bindings = null)
    {
        var files = new Dictionary<string, string> { ["configuration/configuration-set.yaml"] = $"kind: configuration-set\ntenant: {Bank}\nrelease: {release}\nvalues:\n{values}bindings: []\n" };
        if (bindings is not null) files["bindings/port-bindings.yaml"] = bindings;
        var bom = new JsonObject { ["artefacts"] = new JsonArray(files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["sha256"] = Sha(f.Value) }).ToArray()) };
        files["manifest.yaml"] = new JsonObject { ["kind"] = "configuration-release", ["bank"] = Bank, ["release"] = release, ["billOfMaterials"] = bom.DeepClone() }.ToJsonString();
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var (st, sig) = ReleaseSignature.Sign(bytes, new ReleaseClaims($"config/{Bank}@{release}", "", release, "", Bank, "CS-TEST-002", ReleaseSignature.ConfigurationRelease, new JsonObject { ["billOfMaterials"] = bom }), key, DateTimeOffset.UtcNow);
        bytes[ReleaseSignature.StatementFile] = st;
        bytes[ReleaseSignature.SignatureFile] = sig;
        return TestSigners.Zip(bytes);
    }

    static Task<HttpResponseMessage> Act(HttpClient c, string what, object body) => c.PostAsJsonAsync($"/distributions/{what}", body);
    static async Task<JsonNode> J(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_distribution_is_verified_and_stored_once_and_another_with_the_same_id_is_refused(StoreProvider p)
    {
        using var s = new TestSigners();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var host = new ApiHost(dbs.Fresh(p), s);
        var c = host.Client();
        var first = await c.PostAsync("/distributions", new ByteArrayContent(Distribution(s.Key, "2026.11.22-1")));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("dist/example-bank@2026.11.22-1", (string?)(await J(first))["id"]);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/distributions", new ByteArrayContent(Distribution(s.Key, "2026.11.22-1")))).StatusCode);
        var other = await c.PostAsync("/distributions", new ByteArrayContent(Distribution(s.Key, "2026.11.22-1", body: "kind: workflow\nname: p.wf.corporate.v1\n# fixed\n")));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        // A workflow package's signature is not a distribution's; a stranger's key is not trusted; a bad id is refused.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsync("/distributions", new ByteArrayContent(Distribution(s.Key, "2026.11.22-2", kind: ReleaseSignature.WorkflowPackage)))).StatusCode);
        var strange = await c.PostAsync("/distributions", new ByteArrayContent(Distribution(stranger, "2026.11.22-3")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, strange.StatusCode);
        Assert.Contains("does not trust", (string?)(await J(strange))["error"]);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.DeleteAsync("/distributions")).StatusCode);
        var list = (await J(await c.GetAsync($"/distributions?bank={Bank}&environment=prod"))).AsArray();
        Assert.Equal("installed", (string?)list.Single()!["state"]);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Switch_over_drains_the_old_payments_stay_pinned_removal_waits_for_them_and_their_longest_wait_and_rollback_reactivates(StoreProvider p)
    {
        using var s = new TestSigners();
        var clock = new TestClock(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
        var o = dbs.Fresh(p);
        await using var host = new ApiHost(o, s, b => b.ConfigureTestServices(sv => sv.AddSingleton<TimeProvider>(clock)));
        var c = host.Client();
        foreach (var id in new[] { "2026.11.22-1", "2026.11.22-2" }) Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/distributions", new ByteArrayContent(Distribution(s.Key, id)))).StatusCode);
        const string D1 = "dist/example-bank@2026.11.22-1", D2 = "dist/example-bank@2026.11.22-2";
        // No active distribution: a payment is not accepted.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/payments/pin", new { paymentId = "PAY-0", bank = Bank, environment = "prod" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Act(c, "activate", new { id = D1, environment = "prod", why = "first" })).StatusCode);
        Assert.Equal(D1, (string?)(await J(await c.PostAsJsonAsync("/payments/pin", new { paymentId = "PAY-1", bank = Bank, environment = "prod" })))["distribution"]);
        // The switch-over: D2 active, D1 draining, in one step; a new payment takes D2, PAY-1 keeps D1.
        var sw = await J(await Act(c, "activate", new { id = D2, environment = "prod", why = "fix" }));
        Assert.Equal(D1, (string?)sw["draining"]);
        Assert.Equal(D2, (string?)(await J(await c.PostAsJsonAsync("/payments/pin", new { paymentId = "PAY-2", bank = Bank, environment = "prod" })))["distribution"]);
        Assert.Equal(D1, (string?)(await J(await c.PostAsJsonAsync("/payments/pin", new { paymentId = "PAY-1", bank = Bank, environment = "prod" })))["distribution"]);
        // Another environment is its own: D1 is not active in test.
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/distributions/active?bank={Bank}&environment=test")).StatusCode);
        var states = (await J(await c.GetAsync($"/distributions?bank={Bank}&environment=prod"))).AsArray();
        Assert.Equal(["draining", "active"], states.Select(x => (string)x!["state"]!));
        Assert.Equal(1, (int)states[0]!["openPayments"]!);
        // Removal: refused while active, while PAY-1 is open, then until the 30-day wait has passed since it stopped.
        Assert.Contains("is active", (string?)(await J(await Act(c, "remove", new { id = D2, environment = "prod", why = "x" })))["error"]);
        var open = await Act(c, "remove", new { id = D1, environment = "prod", why = "x" });
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        Assert.Contains("1 open payment", (string?)(await J(open))["error"]);
        Assert.True((bool)(await J(await c.PostAsJsonAsync("/payments/end", new { paymentId = "PAY-1", outcome = "settled" })))["recorded"]!);
        Assert.False((bool)(await J(await c.PostAsJsonAsync("/payments/end", new { paymentId = "PAY-1", outcome = "settled" })))["recorded"]!);
        clock.Advance(TimeSpan.FromDays(10));
        var early = await J(await Act(c, "remove", new { id = D1, environment = "prod", why = "x" }));
        Assert.Contains("longest declared wait (P30D) has not passed", (string?)early["error"]);
        Assert.Equal("drained", (string?)(await J(await c.GetAsync($"/distributions?bank={Bank}&environment=prod"))).AsArray()[0]!["state"]);
        clock.Advance(TimeSpan.FromDays(21));
        Assert.Equal(HttpStatusCode.OK, (await Act(c, "remove", new { id = D1, environment = "prod", why = "drained and past its wait" })).StatusCode);
        // Rollback to a removed distribution needs it installed again first; then it is active and D2 drains.
        var rb = await Act(c, "rollback", new { bank = Bank, environment = "prod", why = "incident" });
        Assert.Equal(HttpStatusCode.Conflict, rb.StatusCode);
        Assert.Contains("install it again", (string?)(await J(rb))["error"]);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/distributions", new ByteArrayContent(Distribution(s.Key, "2026.11.22-1")))).StatusCode);
        var back = await J(await Act(c, "rollback", new { bank = Bank, environment = "prod", why = "incident" }));
        Assert.Equal(D1, (string?)back["active"]);
        Assert.Equal(D2, (string?)back["draining"]);
        // Every state change is an insert-only fact on the distribution's own chain, with who and why.
        await using var db = StoreDbContext.Open(o);
        var d1 = await db.Facts.AsNoTracking().Where(f => f.SubjectId == D1 && f.Kind == DistributionRegistry.StateKind).OrderBy(f => f.Seq).Select(f => f.Code).ToListAsync();
        Assert.Equal(["installed", "active", "draining", "drained", "removed", "installed", "active"], d1);
        Assert.True((await new FactLog(db).Verify(D1)).Ok);
        Assert.Equal(D1, await db.Facts.AsNoTracking().Where(f => f.SubjectId == "PAY-1" && f.Kind == DistributionRegistry.EndKind).Select(f => f.ReleaseId).SingleAsync());
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_configuration_release_is_checked_against_every_distribution_not_removed(StoreProvider p)
    {
        using var s = new TestSigners();
        var o = dbs.Fresh(p);
        await using var host = new ApiHost(o, s);
        var c = host.Client();
        const string ack = "  - parameter: uae.wps.exchange.submit-salary-file.v1#ackWait\n    value: P2D\n    validFrom: 2026-12-01\n";
        // No distribution: nothing to check against.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/configuration-releases", new ByteArrayContent(Configuration(s.Key, "1.0.0", ack)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/distributions", new ByteArrayContent(Distribution(s.Key, "2026.11.22-1")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/distributions", new ByteArrayContent(Distribution(s.Key, "2026.11.22-2", contract: Contract(ackMax: "P1D"))))).StatusCode);
        // Out of the bounds of one distribution that reads it: refused.
        var out1 = await c.PostAsync("/configuration-releases", new ByteArrayContent(Configuration(s.Key, "1.0.1", ack)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, out1.StatusCode);
        Assert.Contains("is above its maximum P1D in dist/example-bank@2026.11.22-2", (string?)(await J(out1))["error"]);
        // Unknown to every distribution: refused. A credential as a value, not a reference: refused.
        var unknown = await c.PostAsync("/configuration-releases", new ByteArrayContent(Configuration(s.Key, "1.0.2", "  - parameter: p.nothing.v1#dial\n    value: 1\n    validFrom: 2026-12-01\n")));
        Assert.Contains("is not a dial of any installed distribution", (string?)(await J(unknown))["error"]);
        var secret = await c.PostAsync("/configuration-releases", new ByteArrayContent(Configuration(s.Key, "1.0.3", "  - parameter: b.rule.internal-limit.v1#internalLimit\n    value: 5000\n    validFrom: 2026-12-01\n",
            "kind: port-bindings\nbindings:\n  - port: p.port.core-banking.v1\n    adapter: gateway-rest\n    validFrom: 2026-12-01\n    settings:\n      apiToken: synthetic-not-a-ref\n")));
        Assert.Contains("apiToken as a value", (string?)(await J(secret))["error"]);
        // Within every bound, with a binding by reference: stored; its values and binding are effective-dated versions.
        var ok = await c.PostAsync("/configuration-releases", new ByteArrayContent(Configuration(s.Key, "1.1.0", "  - parameter: b.rule.internal-limit.v1#internalLimit\n    value: 5000\n    validFrom: 2026-12-01\n",
            "kind: port-bindings\nbindings:\n  - port: p.port.core-banking.v1\n    adapter: gateway-rest\n    validFrom: 2026-12-15\n    secretRef: env:TH_CORE_TOKEN\n    settings:\n      baseUrl: https://core.bank.example.test\n")));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(2, (await J(ok))["checkedAgainst"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/configuration-releases", new ByteArrayContent(Configuration(s.Key, "1.1.0", "  - parameter: b.rule.internal-limit.v1#internalLimit\n    value: 6000\n    validFrom: 2026-12-01\n")))).StatusCode);
        var values = (await J(await c.GetAsync($"/values?tenant={Bank}&at=2026-12-02T00:00:00Z"))).AsArray();
        Assert.Contains(values, v => (string?)v!["parameter"] == "b.rule.internal-limit.v1#internalLimit" && (decimal)v!["value"]! == 5000m);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/bindings?tenant={Bank}&port=p.port.core-banking.v1&at=2026-12-02T00:00:00Z")).StatusCode);
        Assert.Equal("gateway-rest", (string?)(await J(await c.GetAsync($"/bindings?tenant={Bank}&port=p.port.core-banking.v1&at=2026-12-16T00:00:00Z")))["adapter"]);
    }

    [Fact]
    public void The_parameter_contract_reads_bounds_as_the_validator_does()
    {
        var dials = ParameterContract.Of(Contract());
        var ack = dials["uae.wps.exchange.submit-salary-file.v1#ackWait"];
        Assert.Null(ParameterContract.Misfit(ack, JsonValue.Create("P2D")));
        Assert.Contains("below its minimum", ParameterContract.Misfit(ack, JsonValue.Create("PT30M")));
        Assert.Contains("not an ISO 8601 duration", ParameterContract.Misfit(ack, JsonValue.Create("two days")));
        Assert.Contains("above its maximum", ParameterContract.Misfit(dials["b.rule.internal-limit.v1#internalLimit"], JsonValue.Create(2000000)));
        Assert.Null(ParameterContract.Misfit(new JsonObject { ["type"] = "list", ["bounds"] = new JsonObject { ["allowed"] = new JsonArray("a", "b") } }, new JsonArray("a")));
        Assert.Contains("not one of", ParameterContract.Misfit(new JsonObject { ["type"] = "list", ["bounds"] = new JsonObject { ["allowed"] = new JsonArray("a", "b") } }, new JsonArray("c")));
        var problems = ParameterContract.Check([("b.rule.internal-limit.v1#internalLimit", JsonValue.Create(5)), ("p.new.v1#dial", JsonValue.Create(1))], new Dictionary<string, JsonNode?> { ["dist/x@1"] = Contract() });
        Assert.Equal(["p.new.v1#dial is not a dial of any installed distribution (dist/x@1)"], problems);
    }

}
