using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using TransactHub.TestBench;

namespace TransactHub.TestBench.Tests;

/// <summary>
/// The test bench service: the bank's sign-in (here the developer identity provider, a real code flow with PKCE), the
/// role map, the Tester policy, the tenant check, and a run through POST /runs. The engines are stood in for by a runner
/// that answers at once; the real runs are the suite's.
/// </summary>
[Collection("service")]
public sealed class ServiceTests : IAsyncLifetime
{
    static readonly Home H0 = Home.Find(AppContext.BaseDirectory);
    readonly string local = Directory.CreateTempSubdirectory("th-tb-svc-").FullName;
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

    Task Start(params string[] args)
    {
        Environment.SetEnvironmentVariable("TH_AUTH_DEV", "1");
        url = $"http://127.0.0.1:{FreePort()}";
        app = Service.Build(new Home { Repo = H0.Repo, Bench = H0.Bench, Local = local }, url, () => new FakeRunner(), ["--environment", "Development", .. args]);
        return app.StartAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Environment.SetEnvironmentVariable("TH_AUTH_DEV", null);
        Directory.Delete(local, true);
    }

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The authorization code flow with PKCE against the developer identity provider, as the UI runs it.</summary>
    async Task<string> SignIn(string[] groups, string tenants = "", string? wrongVerifier = null)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var verifier = B64(RandomNumberGenerator.GetBytes(40));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var form = new List<KeyValuePair<string, string>> { new("redirect_uri", url + "/"), new("code_challenge", challenge), new("state", "s"), new("user", "t.tester"), new("tenants", tenants) };
        form.AddRange(groups.Select(g => new KeyValuePair<string, string>("groups", g)));
        var auth = await http.PostAsync($"{url}/dev-idp/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, auth.StatusCode);
        var code = System.Web.HttpUtility.ParseQueryString(auth.Headers.Location!.Query)["code"]!;
        var tok = await http.PostAsync($"{url}/dev-idp/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = url + "/", ["client_id"] = DevIdentityProvider.ClientId, ["code_verifier"] = wrongVerifier ?? verifier,
        }));
        if (wrongVerifier is not null) { Assert.Equal(HttpStatusCode.BadRequest, tok.StatusCode); return ""; }
        tok.EnsureSuccessStatusCode();
        return (await tok.Content.ReadFromJsonAsync<JsonObject>())!["access_token"]!.ToString();
    }

    HttpClient Client(string? token)
    {
        var c = new HttpClient { BaseAddress = new Uri(url) };
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task A_tester_signed_in_by_the_bank_runs_a_profile_and_follows_it()
    {
        await Start();
        var c = Client(await SignIn(["th-testers"]));
        var me = await c.GetFromJsonAsync<JsonObject>("/api/me");
        Assert.Equal("t.tester", me!["name"]!.ToString());
        Assert.Equal(["tester"], J.Strings(me["roles"]));
        var post = await c.PostAsJsonAsync("/api/runs?wait=true", new JsonObject { ["profile"] = "wps-reference", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" } });
        var job = await post.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("done", job!["status"]!.ToString());
        Assert.True(job["passed"]!.GetValue<bool>());
        var queued = await c.PostAsJsonAsync("/api/runs", new JsonObject { ["profile"] = "wps-reference", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" } });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/runs", new JsonObject { ["profile"] = "no-such", ["data"] = "x" })).StatusCode);
        Assert.NotEmpty((await c.GetFromJsonAsync<JsonArray>("/api/profiles"))!);
        var built = await (await c.PostAsJsonAsync("/api/data/build", new JsonObject { ["template"] = "wps-sif", ["lines"] = 2 })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(built!["validation"]!["valid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task No_token_a_role_the_map_does_not_give_or_a_wrong_verifier_is_refused()
    {
        await Start();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("not-a-token").GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(await SignIn(["th-readers"])).GetAsync("/api/me")).StatusCode);
        // Operators are not let in unless the installation allows them (Auth:AllowedRoles).
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(await SignIn(["th-operators"])).GetAsync("/api/me")).StatusCode);
        await SignIn(["th-testers"], wrongVerifier: B64(RandomNumberGenerator.GetBytes(40)));
        Assert.Equal(HttpStatusCode.OK, (await Client(null).GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task An_installation_may_let_operators_run_the_bench_and_limit_a_tester_to_tenants()
    {
        var map = """{"kind":"runtime-role-map","tenantsClaim":"tenants","grants":[{"claim":"groups","value":"th-testers","roles":["tester"]},{"claim":"groups","value":"th-operators","roles":["operator"],"tenants":["*"]}]}""";
        await Start($"--Auth:RoleMap={map}", "--Auth:AllowedRoles=tester,operator");
        Assert.Equal(HttpStatusCode.OK, (await Client(await SignIn(["th-operators"])).GetAsync("/api/me")).StatusCode);
        var dibOnly = Client(await SignIn(["th-testers"], "dib"));
        var r = await dibOnly.PostAsJsonAsync("/api/runs", new JsonObject { ["profile"] = "wps-reference", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" } });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var ok = await dibOnly.PostAsJsonAsync("/api/runs", new JsonObject { ["profile"] = "wps-reference", ["tenant"] = "dib", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" } });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
    }

    [Fact]
    public void The_developer_login_is_refused_in_Production_and_the_bank_issuer_is_required_without_it()
    {
        Environment.SetEnvironmentVariable("TH_AUTH_DEV", "1");
        Assert.Contains("refused in Production", Assert.Throws<BenchError>(() => Service.Build(new Home { Repo = H0.Repo, Bench = H0.Bench, Local = local }, "http://127.0.0.1:1", () => new FakeRunner(), ["--environment", "Production"])).Message);
        Environment.SetEnvironmentVariable("TH_AUTH_DEV", null);
        Assert.Contains("Auth:Oidc:Issuer", Assert.Throws<BenchError>(() => Service.Build(new Home { Repo = H0.Repo, Bench = H0.Bench, Local = local }, "http://127.0.0.1:1", () => new FakeRunner(), ["--environment", "Development"])).Message);
        Assert.Contains("does not know", Assert.Throws<BenchError>(() => BenchRoleMap.Parse("""{"kind":"runtime-role-map","grants":[{"claim":"groups","value":"x","roles":["admin"]}]}""")).Message);
    }
}
