using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TransactHub.TestBench;

namespace TransactHub.TestBench.Tests;

/// <summary>
/// The test bench service and its sign-in through the runtime's auth library (TransactHub.Auth): bearer JWTs of the bank's
/// issuer with the bank's role map (Tester; Operator or Supervisor only when the installation allows them), the tenant
/// check, the dev identity outside Production, and a run through POST /testbench/api/runs. The engines are stood in for by
/// a runner that answers at once; the real runs are the suite's.
/// </summary>
[Collection("service")]
public sealed class ServiceTests : IAsyncLifetime
{
    static readonly Home H0 = Home.Find(AppContext.BaseDirectory);
    const string Issuer = "https://idp.example.test/realms/bank";
    const string Audience = "transacthub-runtime";
    readonly string local = Directory.CreateTempSubdirectory("th-tb-svc-").FullName;
    readonly RSA rsa = RSA.Create(2048);
    WebApplication? app;
    string url = "";

    sealed class FakeRunner : Service.IJobRunner
    {
        public Task<JsonObject> Run(Plan plan, string id, Action<string> progress, CancellationToken ct)
        {
            progress("interpreter: settled");
            var r = new JsonObject { ["id"] = id, ["plan"] = plan.Json(), ["reference"] = new JsonObject { ["outcome"] = "settled", ["trace"] = new JsonArray("end:settled") }, ["runs"] = new JsonArray() };
            Runner.Judge(r);
            return Task.FromResult(r);
        }
        public Task<JsonObject> Suite(string dir, JsonObject? overrides, Action<string> progress, CancellationToken ct) => Task.FromResult(new JsonObject { ["passed"] = true, ["cases"] = new JsonArray() });
    }

    static int FreePort() { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    static string B64(byte[] b) => Base64UrlEncoder.Encode(b);
    Home TempHome => new() { Repo = H0.Repo, Bench = H0.Bench, Local = local };

    string Jwks()
    {
        var k = rsa.ExportParameters(false);
        return new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA", ["use"] = "sig", ["alg"] = "RS256", ["kid"] = "k1", ["n"] = B64(k.Modulus!), ["e"] = B64(k.Exponent!) }) }.ToJsonString();
    }

    /// <summary>A token as the bank's identity provider would issue it: its groups, and the tenants it names.</summary>
    string Token(string[] groups, string? tenants = null, string issuer = Issuer)
    {
        var claims = new Dictionary<string, object> { ["sub"] = "t.tester", ["name"] = "Test tester", ["groups"] = groups };
        if (tenants is not null) claims["tenants"] = tenants;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = Audience, Claims = claims, Expires = DateTime.UtcNow.AddMinutes(10), NotBefore = DateTime.UtcNow.AddMinutes(-1),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        });
    }

    const string Map = """{"kind":"runtime-role-map","tenantsClaim":"tenants","grants":[{"claim":"groups","value":"th-testers","roles":["Tester"],"tenants":["*"]},{"claim":"groups","value":"th-limited","roles":["Tester"]},{"claim":"groups","value":"th-operators","roles":["Operator"],"tenants":["*"]}]}""";

    Task StartOidc(params string[] more)
    {
        Environment.SetEnvironmentVariable("TH_AUTH_DEV", null);
        url = $"http://127.0.0.1:{FreePort()}";
        app = Service.Build(TempHome, url, () => new FakeRunner(),
            ["--environment", "Production", $"--Auth:Oidc:Issuer={Issuer}", $"--Auth:Oidc:Audience={Audience}", $"--Auth:Oidc:SigningKeys={Jwks()}", $"--Auth:RoleMap={Map}", .. more]);
        return app.StartAsync();
    }

    Task StartDev()
    {
        Environment.SetEnvironmentVariable("TH_AUTH_DEV", "1");
        url = $"http://127.0.0.1:{FreePort()}";
        app = Service.Build(TempHome, url, () => new FakeRunner(), ["--environment", "Development"]);
        return app.StartAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Environment.SetEnvironmentVariable("TH_AUTH_DEV", null);
        Directory.Delete(local, true);
    }

    HttpClient Client(string? token = null, string? dev = null)
    {
        var c = new HttpClient { BaseAddress = new Uri(url) };
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (dev is not null) c.DefaultRequestHeaders.Add(BenchRoles.DevHeader, dev);
        return c;
    }

    static JsonObject Sample(string? tenant = null) => new() { ["profile"] = "wps-reference", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" }, ["tenant"] = tenant };

    [Fact]
    public async Task A_tester_signed_in_by_the_bank_runs_a_profile_and_follows_it()
    {
        await StartOidc();
        var cfg = await Client().GetFromJsonAsync<JsonObject>("/testbench/api/auth/config");
        Assert.Equal("oidc", J.S(cfg, "mode"));
        Assert.Equal(BenchRoles.RuntimeClient, J.S(cfg, "clientId"));
        var c = Client(Token(["th-testers"]));
        var me = await c.GetFromJsonAsync<JsonObject>("/testbench/api/me");
        Assert.Equal("t.tester", J.S(me, "name"));
        Assert.Equal(["Tester"], J.Strings(me!["roles"]));
        var job = await (await c.PostAsJsonAsync("/testbench/api/runs?wait=true", Sample())).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("done", J.S(job, "status"));
        Assert.True(job!["passed"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/testbench/api/runs", Sample())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/testbench/api/runs", new JsonObject { ["profile"] = "no-such", ["data"] = "x" })).StatusCode);
        Assert.NotEmpty((await c.GetFromJsonAsync<JsonArray>("/testbench/api/profiles"))!);
        var built = await (await c.PostAsJsonAsync("/testbench/api/data/build", new JsonObject { ["template"] = "wps-sif", ["lines"] = 2 })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(built!["validation"]!["valid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task No_token_another_issuer_or_a_role_the_map_does_not_give_is_refused()
    {
        await StartOidc();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync("/testbench/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("not-a-token").GetAsync("/testbench/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(Token(["th-testers"], issuer: "https://studio.example.test")).GetAsync("/testbench/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(Token(["th-readers"])).GetAsync("/testbench/api/me")).StatusCode);
        // Operators are not let in unless the installation allows them (Auth:AllowedRoles).
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(Token(["th-operators"])).GetAsync("/testbench/api/me")).StatusCode);
        // A dev identity is no identity outside dev mode.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(dev: """{"sub":"x","roles":["*:Tester"]}""").GetAsync("/testbench/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("/testbench/api/health")).StatusCode);
    }

    [Fact]
    public async Task An_installation_may_let_operators_in_and_a_tester_limited_to_tenants_is_held_to_them()
    {
        await StartOidc("--Auth:AllowedRoles=Tester,Operator");
        Assert.Equal(HttpStatusCode.OK, (await Client(Token(["th-operators"])).GetAsync("/testbench/api/me")).StatusCode);
        var dibOnly = Client(Token(["th-limited"], tenants: "dib"));
        Assert.Equal(HttpStatusCode.Forbidden, (await dibOnly.PostAsJsonAsync("/testbench/api/runs", Sample())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await dibOnly.PostAsJsonAsync("/testbench/api/runs", Sample("dib"))).StatusCode);
    }

    [Fact]
    public async Task The_dev_identity_works_outside_Production_as_the_shell_sends_it()
    {
        await StartDev();
        Assert.Equal("dev", J.S(await Client().GetFromJsonAsync<JsonObject>("/testbench/api/auth/config"), "mode"));
        Assert.Equal(HttpStatusCode.OK, (await Client(dev: """{"sub":"tb.tester","name":"Example tester","roles":["*:Tester"]}""").GetAsync("/testbench/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(dev: """{"sub":"tb.reader","name":"Reader","roles":[]}""").GetAsync("/testbench/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync("/testbench/api/me")).StatusCode);
    }

    [Fact]
    public void Dev_mode_is_refused_in_Production_and_the_bank_issuer_is_required_without_it()
    {
        Environment.SetEnvironmentVariable("TH_AUTH_DEV", "1");
        Assert.Contains("refused in Production", Assert.Throws<BenchError>(() => Service.Build(TempHome, "http://127.0.0.1:1", () => new FakeRunner(), ["--environment", "Production"])).Message);
        Environment.SetEnvironmentVariable("TH_AUTH_DEV", null);
        Assert.Contains("Auth:Oidc:Issuer", Assert.Throws<BenchError>(() => Service.Build(TempHome, "http://127.0.0.1:1", () => new FakeRunner(), ["--environment", "Development"])).Message);
        var bad = """{"kind":"runtime-role-map","grants":[{"claim":"groups","value":"x","roles":["Admin"]}]}""";
        Assert.Contains("does not know", Assert.Throws<BenchError>(() => Service.Build(TempHome, "http://127.0.0.1:1", () => new FakeRunner(),
            ["--environment", "Production", $"--Auth:Oidc:Issuer={Issuer}", $"--Auth:Oidc:Audience={Audience}", $"--Auth:Oidc:SigningKeys={Jwks()}", $"--Auth:RoleMap={bad}"])).Message);
        Assert.Contains("Auth:AllowedRoles", Assert.Throws<BenchError>(() => Service.Build(TempHome, "http://127.0.0.1:1", () => new FakeRunner(), ["--environment", "Development", "--Auth:AllowedRoles=Admin"])).Message);
    }
}
