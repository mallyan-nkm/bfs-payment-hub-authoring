using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactHub.Operations.Api;
using TransactHub.Store;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The console's API (R-05-48, R-05-50, R-06-22): who may call it, what the list filters by, the detail of a payment with
/// several legs, an action through the API; dev sign-in refused in Production and the studio's issuer never trusted.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OperatorApiTests(Databases dbs)
{
    sealed class Host(StoreOptions store, string environment, Dictionary<string, string?>? extra = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment(environment);
            b.UseSetting("Store:Provider", store.Provider.ToString());
            b.UseSetting("Store:Connection", store.ConnectionString);
            b.UseSetting("Operations:Views", Path.Combine(Repo.Root(), "content-configurations", "artefacts"));
            b.UseSetting("Operations:PollMilliseconds", "100");
            foreach (var (k, v) in extra ?? new() { ["TH_AUTH_DEV"] = "1" }) b.UseSetting(k, v);
        }
    }

    static HttpClient As(HttpClient c, string sub, params string[] roles)
    {
        c.DefaultRequestHeaders.Remove(OperatorAuth.DevHeader);
        c.DefaultRequestHeaders.Add(OperatorAuth.DevHeader, new JsonObject { ["sub"] = sub, ["name"] = sub, ["roles"] = new JsonArray(roles.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString());
        return c;
    }

    static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    static async Task<JsonNode> Until(HttpClient c, string url, Func<JsonNode, bool> ok)
    {
        for (var i = 0; ; i++)
        {
            var r = await c.GetAsync(url);
            if (r.IsSuccessStatusCode && await Json(r) is { } j && ok(j)) return j;
            if (i > 200) throw new TimeoutException($"{url}: {await r.Content.ReadAsStringAsync()}");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Filters_the_list_by_what_every_leg_has_and_by_declared_fields_and_shows_a_payment_with_its_legs()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var held = await WpsRun.Seed(store);
        var nak = await WpsRun.Seed(store, fileId: "0000001234567261117083222", hold: false, ack: "nak");
        var other = await WpsRun.Seed(store, tenant: "other-bank", fileId: "0000001234567261117083333");
        await using var host = new Host(store, "Development");
        var c = As(host.CreateClient(), "op-1", "example-bank:Operator");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/ops/api/payments")).StatusCode);
        var all = await Until(c, "/ops/api/payments", j => (int)j["total"]! == 2);
        Assert.DoesNotContain(other, all.ToJsonString());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/ops/api/payments?tenant=other-bank")).StatusCode);

        var holds = await Json(await c.GetAsync("/ops/api/payments?hold=open&scheme=uae.wps&direction=outward"));
        Assert.Equal(held, (string?)holds["rows"]![0]!["id"]);
        Assert.Equal(1, (int)holds["total"]!);
        var byLeg = await Json(await c.GetAsync("/ops/api/payments?leg=uae.wps.wf.salary-file-out.v1&legOutcome=nak"));
        Assert.Equal(nak, (string?)byLeg["rows"]![0]!["id"]);
        var byField = await Json(await c.GetAsync("/ops/api/payments?f:uae.wps.view.operations.v1:uae.wps.wf.salary-file-out.v1:cbResponse=ack"));
        Assert.Equal(held, (string?)byField["rows"]![0]!["id"]);
        var byAmount = await Json(await c.GetAsync("/ops/api/payments?f:uae.wps.view.operations.v1:subject:totalAmount:min=1000&minAmount=1500&maxAmount=1500"));
        Assert.Equal(2, (int)byAmount["total"]!);
        var byFile = await Json(await c.GetAsync("/ops/api/payments?q=083222.SIF"));
        Assert.Equal(nak, (string?)byFile["rows"]![0]!["id"]);
        var facets = await Json(await c.GetAsync("/ops/api/facets"));
        Assert.Contains("uae.wps.wf.salary-file-out.v1", facets["legs"]!.AsArray().Select(x => (string?)x));

        var detail = await Json(await c.GetAsync($"/ops/api/payments/{held}"));
        Assert.Single(detail["payment"]!["legs"]!.AsArray());
        Assert.Equal(2, detail["lines"]!.AsArray().Count);
        Assert.Contains(detail["timeline"]!.AsArray(), t => (string?)t!["step"] == "submit.read-ack");
        Assert.Contains(detail["offers"]!.AsArray(), o => (string?)o!["action"] == "approve");
        var doc = (string)detail["documents"]![0]!["id"]!;
        Assert.Equal("SCR,synthetic\n", await c.GetStringAsync($"/ops/api/payments/{held}/documents/{doc}"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/ops/api/payments/{other}")).StatusCode);

        var act = await c.PostAsJsonAsync($"/ops/api/payments/{held}/actions", new { action = "approve", remarks = "ok" });
        Assert.Equal(HttpStatusCode.OK, act.StatusCode);
        await Until(c, $"/ops/api/payments/{held}", j => j["payment"]!["hold"] is null && j["actions"]!.AsArray().Count == 1);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.DeleteAsync($"/ops/api/payments/{held}")).StatusCode);
        var health = await Json(await c.GetAsync("/ops/api/health"));
        Assert.Equal(0, (int)health["lag"]!["behind"]!);
    }

    [Fact]
    public void Dev_sign_in_is_refused_in_production()
    {
        var store = dbs.Fresh(StoreProvider.PostgreSql);
        using var host = new Host(store, "Production");
        var e = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        Assert.Contains("TH_AUTH_DEV=1 is refused in Production", e.ToString());
    }

    [Fact]
    public void The_studios_issuer_is_never_the_operators_identity_provider()
    {
        var store = dbs.Fresh(StoreProvider.PostgreSql);
        using var host = new Host(store, "Production", new() { ["TH_OIDC_AUTHORITY"] = "https://studio.example.invalid/", ["TH_OIDC_AUDIENCE"] = "th-ops", ["TH_STUDIO_ISSUER"] = "https://studio.example.invalid" });
        var e = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        Assert.Contains("the studio's issuer", e.ToString());
    }

    [Fact]
    public async Task With_the_banks_identity_provider_a_request_without_a_valid_token_is_refused()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        await using var host = new Host(store, "Production", new() { ["TH_OIDC_AUTHORITY"] = "https://idp.example.invalid/realms/bank", ["TH_OIDC_AUDIENCE"] = "th-ops", ["TH_OIDC_CLIENT_ID"] = "th-ops-console" });
        var c = As(host.CreateClient(), "op-1", "example-bank:Supervisor");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/ops/api/payments")).StatusCode);
        var cfg = await Json(await c.GetAsync("/ops/api/auth/config"));
        Assert.Equal("oidc", (string?)cfg["mode"]);
        Assert.Equal("th-ops-console", (string?)cfg["clientId"]);
    }

    [Fact]
    public void Roles_come_from_the_banks_claims_through_the_mapping()
    {
        var m = new RoleMapping([new RoleRule("groups", "PH-WPS-Ops", OperatorRoles.Operator, "example-bank"), new RoleRule("groups", "PH-WPS-Sup", OperatorRoles.Supervisor, "example-bank")]);
        var who = m.Resolve(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new("sub", "u-1"), new("groups", "PH-WPS-Ops"), new("groups", "Unrelated")], "jwt")))!;
        Assert.True(who.Has("example-bank", OperatorRoles.Operator));
        Assert.False(who.Has("example-bank", OperatorRoles.Supervisor));
        Assert.False(who.Has("other-bank", OperatorRoles.Operator));
        var dflt = RoleMapping.Default.Resolve(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new("sub", "u-2"), new("th_roles", "example-bank:Supervisor")], "jwt")))!;
        Assert.True(dflt.Has("example-bank", OperatorRoles.Operator));
        Assert.True(dflt.Has("example-bank", OperatorRoles.Supervisor));
        Assert.Null(RoleMapping.Default.Resolve(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }
}
