using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactHub.Auth;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// Who may call the release API (spec R-09-27, R-09-29; Issue I-65's runtime part). The runtime keeps no identities: it
// takes the bank's identity provider's bearer JWTs (a person's, or a machine caller's client-credentials token), maps the
// bank's claims and groups to its roles by the role map, and trusts no other issuer, the studio's included. 401 for no
// caller or a token it does not accept, 403 for a caller without the role or the tenant.

[Collection(DatabaseCollection.Name)]
public sealed class ApiAuthTests(Databases dbs)
{
    const string StudioIssuer = "https://login.studio.example.test/oauth2";

    static byte[] Package(TestSigners s) => TestSigners.Zip(TestSigners.Sign(new Dictionary<string, string>
    {
        ["workflows/p.wf.corporate.v1@1.0.0.yaml"] = "kind: workflow\n",
        ["manifest.yaml"] = $"kind: export\ntarget: temporal\npromotable: true\nflows:\n  - name: p.wf.corporate.v1\n    kind: workflow\n    wfKind: platform\n    release: 1.0.0\nfiles:\n  - path: workflows/p.wf.corporate.v1@1.0.0.yaml\n    hash: sha256:{Convert.ToHexStringLower(SHA256.HashData("kind: workflow\n"u8.ToArray()))}\n",
    }, s.Key, "p.wf.corporate.v1/temporal@1.0.0"));

    const string Get = "/releases?scope=p.wf.corporate.v1/temporal&release=1.0.0";
    const string Values = "/values?tenant=example-bank&at=2026-11-24T06:00:00Z";
    static readonly object Activation = new { environment = "test", scope = "p.wf.corporate.v1/temporal", release = "1.0.0" };

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task No_token_or_one_the_bank_s_provider_did_not_issue_is_401(StoreProvider p)
    {
        using var s = new TestSigners();
        await using var api = new ApiHost(dbs.Fresh(p), s);
        using var otherKey = RSA.Create(2048);
        var reader = new Dictionary<string, object> { ["roles"] = new[] { "TransactHub.Reader" } };
        var refused = new[]
        {
            api.Client(null),
            api.Bearer("not-a-jwt"),
            // The studio's identity provider is not the runtime's: its tokens are never taken.
            api.Bearer(api.Idp.Token("studio.user", reader, issuer: StudioIssuer)),
            api.Bearer(api.Idp.Token("x", reader, audience: "another-api")),
            api.Bearer(api.Idp.Token("x", reader, life: TimeSpan.FromMinutes(-5))),
            api.Bearer(api.Idp.Token("x", reader, key: otherKey)),
        };
        foreach (var c in refused)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/releases", new ByteArrayContent(Package(s)))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Get)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/releases/package?scope=p.wf.corporate.v1/temporal&release=1.0.0")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Values)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/activations", Activation)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/activations/active?environment=test&scope=x")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/import-branch", new { path = "/nowhere" })).StatusCode);
        }
        // Within the configured clock skew (60 s by default) a just-expired token is still taken.
        Assert.Equal(HttpStatusCode.NotFound, (await api.Bearer(api.Idp.Token("x", reader, life: TimeSpan.FromSeconds(-20))).GetAsync(Get)).StatusCode);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Machine_callers_get_the_roles_the_role_map_gives_their_app_roles_and_nothing_else_is_403(StoreProvider p)
    {
        using var s = new TestSigners();
        await using var api = new ApiHost(dbs.Fresh(p), s);
        var reader = api.Client("reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/releases", new ByteArrayContent(Package(s)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/import-branch", new { path = "/nowhere" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/activations", Activation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Values)).StatusCode);
        var importer = api.Client("importer");
        Assert.Equal(HttpStatusCode.Created, (await importer.PostAsync("/releases", new ByteArrayContent(Package(s)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await importer.PostAsJsonAsync("/activations", Activation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await importer.GetAsync(Get)).StatusCode);
        Assert.Equal("importer", (string)(await reader.GetFromJsonAsync<JsonObject>(Get))!["storedBy"]!);
        // A worker reads its own tenant's values only.
        var worker = api.Client("bank-worker");
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync(Values)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/values?tenant=other-bank")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync(Get)).StatusCode);
        // A token that names the runtime's own role claim is not taken at its word: only the map gives roles.
        var forged = api.Bearer(api.Idp.Token("forger", new Dictionary<string, object> { [RuntimeAuth.RoleClaim] = new[] { "release-activator" }, [RuntimeAuth.TenantClaim] = "*" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await forged.PostAsJsonAsync("/activations", Activation)).StatusCode);
        // The actor recorded is the caller, not the name a body gives.
        Assert.Equal(HttpStatusCode.Created, (await api.Client("pipeline").PostAsJsonAsync("/activations", new { environment = "test", scope = "p.wf.corporate.v1/temporal", release = "1.0.0", actor = "someone-else" })).StatusCode);
        var facts = (await reader.GetFromJsonAsync<JsonObject>(Get))!["verifications"]!.AsArray();
        Assert.Equal(["importer", "pipeline"], facts.Select(f => (string)f!["by"]!));
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task People_get_roles_from_their_groups_and_tenants_from_their_token(StoreProvider p)
    {
        using var s = new TestSigners();
        await using var api = new ApiHost(dbs.Fresh(p), s);
        var ops = api.Bearer(api.Idp.Token("ops.user", new Dictionary<string, object> { ["groups"] = new[] { "grp-other", "grp-runtime-operations" } }));
        Assert.Equal(HttpStatusCode.Created, (await ops.PostAsync("/releases", new ByteArrayContent(Package(s)))).StatusCode);
        Assert.Equal("ops.user", (string)(await ops.GetFromJsonAsync<JsonObject>(Get))!["storedBy"]!);
        Assert.Equal(HttpStatusCode.Created, (await ops.PostAsJsonAsync("/activations", Activation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync(Values)).StatusCode);
        var analyst = api.Bearer(api.Idp.Token("analyst", new Dictionary<string, object> { ["groups"] = new[] { "grp-bank-analysts" }, ["tenants"] = "example-bank" }));
        Assert.Equal(HttpStatusCode.OK, (await analyst.GetAsync(Get)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await analyst.GetAsync(Values)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync("/values?tenant=other-bank")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsJsonAsync("/activations", Activation)).StatusCode);
        // A person in no mapped group is known and may do nothing.
        var nobody = api.Bearer(api.Idp.Token("nobody", new Dictionary<string, object> { ["groups"] = new[] { "grp-other" } }));
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync(Get)).StatusCode);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_API_does_not_start_without_the_bank_s_issuer_a_role_map_or_trusted_signers(StoreProvider p)
    {
        using var s = new TestSigners();
        using var idp = new TestIdp();
        var o = dbs.Fresh(p);
        async Task<string> StartFails(params (string Key, string Value)[] settings)
        {
            await using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Store:Provider", o.Provider.ToString());
                b.UseSetting("Store:Connection", o.ConnectionString);
                foreach (var (k, v) in settings) b.UseSetting(k, v);
            });
            return Assert.ThrowsAny<Exception>(() => f.CreateClient()).ToString();
        }
        var trust = ("TH_TRUSTED_SIGNERS", $"file:{s.TrustFile}");
        var oidc = new[] { ("Auth:Oidc:Issuer", TestIdp.Issuer), ("Auth:Oidc:Audience", TestIdp.Audience), ("Auth:Oidc:SigningKeys", idp.Jwks) };
        Assert.Contains("TH_TRUSTED_SIGNERS", await StartFails());
        Assert.Contains("Auth:Oidc:Issuer", await StartFails(trust));
        Assert.Contains("Auth:RoleMap", await StartFails([trust, .. oidc]));
        Assert.Contains("which this host does not know", await StartFails([trust, .. oidc, ("Auth:RoleMap", """{ "kind": "runtime-role-map", "grants": [ { "claim": "roles", "value": "x", "roles": ["superuser"] } ] }""")]));
    }

    [Fact]
    public void The_role_map_is_a_library_another_host_uses_with_its_own_roles()
    {
        // An operations console's API calls the same code with its own roles (Issue I-65).
        var map = RoleMap.Parse("""{ "kind": "runtime-role-map", "grants": [ { "claim": "groups", "value": "grp-payments-operators", "roles": ["operator"], "tenants": ["example-bank"] }, { "claim": "scp", "value": "payments.supervise", "roles": ["supervisor"] } ] }""", ["operator", "supervisor"]);
        var id = new ClaimsIdentity([new Claim("sub", "u1"), new Claim("groups", "grp-payments-operators"), new Claim("scp", "payments.read payments.supervise")], "test", "sub", RuntimeAuth.RoleClaim);
        map.Apply(id);
        var u = new ClaimsPrincipal(id);
        Assert.True(u.IsInRole("operator"));
        Assert.True(u.IsInRole("supervisor"));
        Assert.True(RuntimeAuth.MayActFor(u, "example-bank"));
        Assert.False(RuntimeAuth.MayActFor(u, "other-bank"));
        Assert.Throws<InvalidOperationException>(() => RoleMap.Parse("""{ "kind": "runtime-role-map", "grants": [ { "claim": "groups", "value": "g", "roles": ["release-importer"] } ] }""", ["operator", "supervisor"]));
    }
}
