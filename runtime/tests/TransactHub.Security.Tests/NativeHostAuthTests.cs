using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TransactHub.Security.Tests;

// The native host answers native activities for the workers, and can post to a bank's core banking (Issue I-75). It takes
// only the bank's machine callers (R-08-17): a client-credentials JWT of the bank's issuer that the role map gives
// native-caller, in the tenant the attempt names. With no authentication configured it answers on loopback only, and does not
// start when it listens beyond the machine.
public sealed class NativeHostAuthTests
{
    const string Issuer = "https://idp.bank.example.test/realms/runtime";
    const string Audience = "transacthub-native";
    const string RoleMap = """
        { "kind": "runtime-role-map", "grants": [
          { "claim": "roles", "value": "TransactHub.Worker.ExampleBank", "roles": ["native-caller"], "tenants": ["example-bank"] },
          { "claim": "roles", "value": "TransactHub.Reader", "roles": [] } ] }
        """;

    sealed class Idp : IDisposable
    {
        public readonly RSA Rsa = RSA.Create(2048);
        public string Jwks
        {
            get
            {
                var p = Rsa.ExportParameters(false);
                return new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA", ["kid"] = "n-1", ["use"] = "sig", ["alg"] = "RS256", ["n"] = Base64UrlEncoder.Encode(p.Modulus), ["e"] = Base64UrlEncoder.Encode(p.Exponent) }) }.ToJsonString();
            }
        }
        public string Client(string client, string appRole) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = client, ["azp"] = client, ["roles"] = new[] { appRole } },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(Rsa) { KeyId = "n-1" }, SecurityAlgorithms.RsaSha256),
        });
        public void Dispose() => Rsa.Dispose();
    }

    sealed class Host(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        }
    }

    static object Attempt(string tenant) => new { activity = "uae.wps.validate.cbx-file.v1", release = "1.0.0", view = new { }, @params = new { }, now = "2026-11-22T08:00:00Z", tenant };

    [Fact]
    public async Task With_the_bank_s_provider_only_a_native_caller_of_the_attempt_s_tenant_is_served()
    {
        using var idp = new Idp();
        await using var host = new Host(new() { ["TH_NATIVE_URLS"] = "http://0.0.0.0:7300", ["Auth:Oidc:Issuer"] = Issuer, ["Auth:Oidc:Audience"] = Audience, ["Auth:Oidc:SigningKeys"] = idp.Jwks, ["Auth:RoleMap"] = RoleMap });
        var anon = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/native", Attempt("example-bank"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/files")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/postings")).StatusCode);

        HttpClient Bearer(string token) { var c = host.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }
        var reader = Bearer(idp.Client("reader", "TransactHub.Reader"));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/native", Attempt("example-bank"))).StatusCode);
        var worker = Bearer(idp.Client("worker-example-bank", "TransactHub.Worker.ExampleBank"));
        var served = await worker.PostAsJsonAsync("/native", Attempt("example-bank"));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.NotEqual("NotAuthorised", (string?)(await served.Content.ReadFromJsonAsync<JsonObject>())!["name"]);
        // Its role is in its own bank only: another bank's attempt is refused before any activity runs.
        var other = await worker.PostAsJsonAsync("/native", Attempt("other-bank"));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal("NotAuthorised", (string?)(await other.Content.ReadFromJsonAsync<JsonObject>())!["name"]);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/files")).StatusCode);
        // A token of another issuer's key is not taken.
        using var stranger = new Idp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(stranger.Client("worker-example-bank", "TransactHub.Worker.ExampleBank")).PostAsJsonAsync("/native", Attempt("example-bank"))).StatusCode);
    }

    [Fact]
    public void Without_authentication_it_does_not_start_beyond_loopback()
    {
        foreach (var urls in new[] { "http://0.0.0.0:7300", "http://*:7300", "http://10.1.2.3:7300", "http://127.0.0.1:7300;http://[::]:7301" })
        {
            using var host = new Host(new() { ["TH_NATIVE_URLS"] = urls });
            Assert.Contains("has no authentication", Assert.ThrowsAny<Exception>(() => host.CreateClient()).ToString());
        }
    }

    [Fact]
    public async Task Without_authentication_on_loopback_it_answers_as_before()
    {
        await using var host = new Host(new() { ["TH_NATIVE_URLS"] = "http://127.0.0.1:7300;http://[::1]:7301;http://localhost:7302" });
        var c = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/native", Attempt("example-bank"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/files")).StatusCode);
    }
}
