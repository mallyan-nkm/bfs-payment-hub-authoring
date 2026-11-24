using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactHub.Activities.Native;
using TransactHub.Store;

namespace TransactHub.Native.Tests;

/// <summary>
/// Native contract 2.0.0 (R-09-261 to R-09-264; Issues I-315, I-226, I-237, I-227, I-224): a module builds a bound adapter of its own on
/// the contract's bound ports, through the native host, with the credential and the endpoint the image keeps; a module built against
/// contract 1.x is refused with its own code; and two distributions whose modules are at different versions run side by side.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ContractTwoTests(Store store)
{
    const string Bank = "lm-bank";
    const string EmployerStatus = "uae.wps.enrich.employer-status.v1";
    const string EmployerStatusRelease = "1.1.0";

    sealed class Host : WebApplicationFactory<Program>
    {
        public Host(StoreOptions store, string? banksDir = null)
        {
            Environment.SetEnvironmentVariable("TH_STORE_PROVIDER", store.Provider.ToString());
            Environment.SetEnvironmentVariable("TH_STORE_CONNECTION", store.ConnectionString);
            Environment.SetEnvironmentVariable("TH_NATIVE_REFRESH", "00:00:01");
            Environment.SetEnvironmentVariable("TH_BANKS_DIR", banksDir);
        }

        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("TH_NATIVE_URLS", "http://127.0.0.1:7300");
    }

    async Task<string> Install(string id, string contract, params ModuleBuilds.Module[] modules)
    {
        var (manifest, package) = ModuleBuilds.Distribution(Bank, id, contract, modules);
        await using var db = StoreDbContext.Open(store.Options);
        await new DistributionRegistry(db).Store(Bank, id, package, manifest, ModuleBuilds.Sha(package)[7..], "test", "native contract 2.0.0");
        return DistributionRegistry.IdOf(Bank, id);
    }

    static async Task<JsonObject> Post(HttpClient c, object body)
    {
        var text = await (await c.PostAsJsonAsync("/native", body)).Content.ReadAsStringAsync();
        try { return (JsonObject)JsonNode.Parse(text)!; }
        catch (System.Text.Json.JsonException) { throw new Xunit.Sdk.XunitException($"The native host answered: {text[..Math.Min(text.Length, 3000)]}"); }
    }

    static object Employer(string distribution, string employer) => new
    {
        activity = EmployerStatus, release = EmployerStatusRelease, distribution, now = "2026-11-24T08:00:00Z", tenant = Bank, @params = new { }, level = "batch",
        view = new { header = new { scheme = "uae.wps", paymentId = "PAY-NATIVE-LM-1" }, ext = new { uae = new { wps = new { employerId = employer } } } },
    };

    /// <summary>The bank's register behind a REST service on this machine: what it was asked, and its answers (synthetic employer ids).</summary>
    sealed class Register : IDisposable
    {
        readonly HttpListener listener = new();
        public readonly List<(string Path, string? Key, string? Channel, string Body)> Calls = [];
        public string BaseUrl { get; }

        public Register()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(BaseUrl);
            listener.Start();
            _ = Task.Run(Serve);
        }

        async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                lock (Calls) Calls.Add((ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["X-Api-Key"], ctx.Request.Headers["X-Channel"], body));
                var status = ctx.Request.Headers["X-Ref"] switch { "0000001234567" => "SUSPENDED", "0000001234568" => "active", _ => null };
                ctx.Response.StatusCode = status is null ? 404 : 200;
                if (status is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes($$"""{ "status": "{{status}}" }""");
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                }
                ctx.Response.Close();
            }
        }

        public void Dispose() { listener.Stop(); listener.Close(); }
    }

    [Fact]
    public async Task A_module_builds_its_own_bound_adapter_and_the_image_sends_its_request_with_the_credential_it_keeps()
    {
        using var register = new Register();
        var token = "synthetic-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("TH_TEST_NATIVE_LM_TOKEN", token);
        var banks = Directory.CreateTempSubdirectory("th-native-lm-banks-");
        try
        {
            Directory.CreateDirectory(Path.Combine(banks.FullName, Bank));
            await File.WriteAllTextAsync(Path.Combine(banks.FullName, Bank, "labour-ministry.yaml"), $"""
                kind: port-bindings
                tenant: {Bank}
                bindings:
                  - port: uae.wps.port.labour-ministry.v1
                    adapter: gateway-rest
                    validFrom: 2026-11-01T00:00:00Z
                    settings:
                      baseUrl: {register.BaseUrl}
                      statusPath: /api/establishments/status
                      secretRef: env:TH_TEST_NATIVE_LM_TOKEN
                      credentialHeader: X-Api-Key
                      channel: BANK-CHANNEL
                      timeoutSeconds: "5"
                """);
            var d = await Install("2026.11.24-21", "2.0.0", new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, store.BuildA, [$"{EmployerStatus}@{EmployerStatusRelease}"]));
            await using var host = new Host(store.Options, banks.FullName);
            var c = host.CreateClient();

            // The WPS module's own gateway adapter (R-09-263), on the binding the image gives it, sent by the image (R-09-261).
            var r = await Post(c, Employer(d, "0000001234567"));
            Assert.True((string?)r["kind"] == "result", r.ToJsonString());
            Assert.Equal("found", (string?)r["outcome"]);
            Assert.Equal("suspended", (string?)r["writes"]!["ext.uae.wps.employerStatus"]);
            Assert.Equal("not-found", (string?)(await Post(c, Employer(d, "0000009999999")))["outcome"]);
            lock (register.Calls)
            {
                var call = register.Calls[0];
                Assert.Equal("/api/establishments/status", call.Path);
                Assert.Equal(token, call.Key);
                Assert.Equal("BANK-CHANNEL", call.Channel);
                Assert.Equal("""{"employerId":"0000001234567"}""", call.Body);
            }
            // The module is the distribution's, loaded in its own context; the image has no adapter of the port.
            Assert.DoesNotContain(typeof(NativeBoundPorts).Assembly.GetTypes(), t => t.Name.Contains("EmployerRegister"));
            Assert.DoesNotContain(typeof(TransactHub.Ports.PortBindings).Assembly.GetTypes(), t => t.Name.Contains("EmployerRegister"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TH_BANKS_DIR", null);
            Environment.SetEnvironmentVariable("TH_TEST_NATIVE_LM_TOKEN", null);
            banks.Delete(true);
        }
    }

    [Fact]
    public async Task A_module_built_against_contract_1_is_refused_with_its_own_code_and_a_rebuilt_one_runs()
    {
        // Every module built before 2.0.0 references contract 1.x; the runtime refuses it at load, as the release API does at install.
        var old = ModuleBuilds.BuiltAgainst("1.1.0");
        Assert.Equal("1.1.0", NativeAssemblies.ContractOf(old));
        var legacy = await Install("2026.11.24-22", "1.1.0", new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, old, []));
        // Its manifest claims 2.0.0, its assembly was built against 1.1.0: refused as well.
        var claims = await Install("2026.11.24-23", "2.0.0", new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, old, []));
        var rebuilt = await Install("2026.11.24-24", "2.0.0", new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, store.BuildA, [$"{ModuleBuilds.AckIn}@{ModuleBuilds.AckInRelease}"]));
        await using var host = new Host(store.Options);
        var c = host.CreateClient();
        static object Ack(string d) => new
        {
            activity = ModuleBuilds.AckIn, release = ModuleBuilds.AckInRelease, distribution = d, now = "2026-11-24T08:00:00Z", tenant = Bank, @params = new { },
            view = new { header = new { scheme = "uae.wps", paymentId = "PAY-NATIVE-OLD-1" }, messages = new { lastReceived = new { status = "accepted" } } },
        };
        var r = await Post(c, Ack(legacy));
        Assert.Equal(("error", NativeAssemblies.ContractUnsupported, false), ((string?)r["kind"], (string?)r["name"], (bool?)r["retryable"]));
        Assert.Contains("native contract 1.1.0", (string?)r["message"]);
        Assert.Contains("rebuild every module against 2.0.0", (string?)r["message"]);
        var cr = await Post(c, Ack(claims));
        Assert.Equal(NativeAssemblies.ContractUnsupported, (string?)cr["name"]);
        Assert.Contains("was built against native contract 1.1.0, its manifest says 2.0.0", (string?)cr["message"]);
        Assert.Equal("ack", (string?)(await Post(c, Ack(rebuilt)))["outcome"]);
        Assert.Equal("2.0.0", (string?)(await c.GetFromJsonAsync<JsonObject>("/modules"))!["contract"]);
    }

    [Fact]
    public async Task Two_distributions_whose_modules_are_at_different_versions_run_side_by_side()
    {
        // A scheme release's module, built at its own assembly version: the same assembly name, two versions, one context each.
        var v1 = await Task.Run(() => ModuleBuilds.Wps("wps-v1-20", "1.20.0"));
        var v2 = await Task.Run(() => ModuleBuilds.Wps("wps-v1-23", "1.23.0"));
        var a = await Install("2026.11.24-25", "2.0.0",
            new ModuleBuilds.Module("p", ModuleBuilds.PlatformAssembly, store.PlatformA, [$"{ModuleBuilds.PostingWindow}@{ModuleBuilds.PostingWindowRelease}"]),
            new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, v1, [$"{ModuleBuilds.AckIn}@{ModuleBuilds.AckInRelease}"]));
        var b = await Install("2026.11.24-26", "2.0.0",
            new ModuleBuilds.Module("p", ModuleBuilds.PlatformAssembly, store.PlatformB, [$"{ModuleBuilds.PostingWindow}@{ModuleBuilds.PostingWindowRelease}"]),
            new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, v2, [$"{ModuleBuilds.AckIn}@{ModuleBuilds.AckInRelease}"]));
        await using var host = new Host(store.Options);
        var c = host.CreateClient();
        object Ack(string d) => new
        {
            activity = ModuleBuilds.AckIn, release = ModuleBuilds.AckInRelease, distribution = d, now = "2026-11-24T08:00:00Z", tenant = Bank, @params = new { },
            view = new { header = new { scheme = "uae.wps", paymentId = "PAY-NATIVE-VER-1" }, messages = new { lastReceived = new { status = "accepted" } } },
        };
        Assert.Equal("wps-v1-20", (string?)(await Post(c, Ack(a)))["writes"]!["status"]!["additionalInformation"]);
        Assert.Equal("wps-v1-23", (string?)(await Post(c, Ack(b)))["writes"]!["status"]!["additionalInformation"]);
        Assert.Equal("wps-v1-20", (string?)(await Post(c, Ack(a)))["writes"]!["status"]!["additionalInformation"]);
        var loaded = ((await c.GetFromJsonAsync<JsonObject>("/modules"))!["loaded"] as JsonArray)!.OfType<JsonObject>().ToDictionary(x => (string)x["distribution"]!);
        string? Version(string d) => (string?)((JsonArray)loaded[d]["modules"]!).First(m => (string?)m!["scope"] == "uae.wps")!["version"];
        Assert.Equal("1.20.0.0", Version(a));
        Assert.Equal("1.23.0.0", Version(b));
        Assert.NotEqual((string?)loaded[a]["context"], (string?)loaded[b]["context"]);
        // Both are 2.0.0 modules: the contract the image shares with them is one.
        Assert.Equal("2.0.0", NativeAssemblies.ContractOf(v1));
        Assert.Equal("2.0.0", NativeAssemblies.ContractOf(v2));
    }
}
