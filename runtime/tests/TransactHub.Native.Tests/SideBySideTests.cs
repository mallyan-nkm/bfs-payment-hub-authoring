using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TransactHub.Activities.Native;
using TransactHub.Store;

namespace TransactHub.Native.Tests;

/// <summary>The store on PostgreSQL 16 and the two WPS builds, once for the run.</summary>
public sealed class Store : IAsyncLifetime
{
    readonly PostgreSqlContainer pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public StoreOptions Options => new(StoreProvider.PostgreSql, pg.GetConnectionString());
    public byte[] BuildA = [], BuildB = [];

    public async Task InitializeAsync()
    {
        var start = pg.StartAsync();
        // Two scheme patch releases of WPS: the same code-behind, built twice with another ACK mapping.
        BuildA = await Task.Run(() => ModuleBuilds.Wps("wps-build-a"));
        BuildB = await Task.Run(() => ModuleBuilds.Wps("wps-build-b"));
        await start;
        await using var db = StoreDbContext.Open(Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await pg.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class StoreCollection : ICollectionFixture<Store> { public const string Name = "native-store"; }

/// <summary>
/// Spec R-09-120 to R-09-124: the native host's image holds no scheme code; each distribution's native assemblies are loaded from
/// the store side by side, one collectible context per distribution, checked again against the signed manifest; an attempt is
/// served from its payment's pinned distribution by activity and release; a distribution removed is unloaded and its context
/// collected while the others go on.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SideBySideTests(Store store)
{
    const string Bank = "example-bank";
    const string Env = "test";

    sealed class Host : WebApplicationFactory<Program>
    {
        public Host(StoreOptions store)
        {
            // The host reads its store and refresh period from its environment, as a deployment sets them.
            Environment.SetEnvironmentVariable("TH_STORE_PROVIDER", store.Provider.ToString());
            Environment.SetEnvironmentVariable("TH_STORE_CONNECTION", store.ConnectionString);
            Environment.SetEnvironmentVariable("TH_NATIVE_REFRESH", "00:00:01");
        }

        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("TH_NATIVE_URLS", "http://127.0.0.1:7300");
    }

    static object Ack(string? distribution, string release = ModuleBuilds.AckInRelease) => new
    {
        activity = ModuleBuilds.AckIn, release, distribution, now = "2026-11-23T08:00:00Z", tenant = Bank, @params = new { },
        view = new { header = new { scheme = "uae.wps", paymentId = "PAY-NATIVE-1" }, messages = new { lastReceived = new { status = "accepted" } } },
    };

    async Task<string> Install(string id, byte[] module, string contract = "1.0.0", byte[]? carried = null)
    {
        var (manifest, package) = ModuleBuilds.Distribution(Bank, id, module, contract, carried);
        await using var db = StoreDbContext.Open(store.Options);
        await new DistributionRegistry(db).Store(Bank, id, package, manifest, ModuleBuilds.Sha(package)[7..], "test", "native side by side");
        return DistributionRegistry.IdOf(Bank, id);
    }

    static async Task<JsonObject> Post(HttpClient c, object body)
    {
        var text = await (await c.PostAsJsonAsync("/native", body)).Content.ReadAsStringAsync();
        try { return (JsonObject)JsonNode.Parse(text)!; }
        catch (System.Text.Json.JsonException) { throw new Xunit.Sdk.XunitException($"The native host answered: {text[..Math.Min(text.Length, 3000)]}"); }
    }

    [Fact]
    public async Task Each_distribution_runs_its_own_build_of_the_WPS_activity_and_a_removed_one_is_unloaded()
    {
        Assert.NotEqual(ModuleBuilds.Sha(store.BuildA), ModuleBuilds.Sha(store.BuildB));
        var a = await Install("2026.11.23-1", store.BuildA);
        var b = await Install("2026.11.23-2", store.BuildB);
        await using var host = new Host(store.Options);
        var c = host.CreateClient();

        // The image carries no scheme code: an attempt that names no distribution has no WPS activity to run.
        Assert.Equal("NotNative", (string?)(await Post(c, Ack(null)))["name"]);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), x => x.GetName().Name == ModuleBuilds.Assembly && System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(x) == System.Runtime.Loader.AssemblyLoadContext.Default);

        // A payment pinned to A runs A's build; one pinned to B runs B's, side by side in one host.
        var ra = await Post(c, Ack(a));
        Assert.Equal("result", (string?)ra["kind"]);
        Assert.Equal("ack", (string?)ra["outcome"]);
        Assert.Equal("wps-build-a", (string?)ra["writes"]!["status"]!["additionalInformation"]);
        var rb = await Post(c, Ack(b));
        Assert.Equal("wps-build-b", (string?)rb["writes"]!["status"]!["additionalInformation"]);
        Assert.Equal("wps-build-a", (string?)(await Post(c, Ack(a)))["writes"]!["status"]!["additionalInformation"]);

        var modules = (await c.GetFromJsonAsync<JsonObject>("/modules"))!;
        var loaded = (modules["loaded"] as JsonArray)!.OfType<JsonObject>().ToDictionary(x => (string)x["distribution"]!);
        Assert.Equal(ModuleBuilds.Sha(store.BuildA), (string?)loaded[a]["modules"]![0]!["sha256"]);
        Assert.Equal(ModuleBuilds.Sha(store.BuildB), (string?)loaded[b]["modules"]![0]!["sha256"]);
        Assert.True((bool)loaded[a]["collectible"]!);
        Assert.NotEqual((string?)loaded[a]["context"], (string?)loaded[b]["context"]);
        Assert.Equal(2, (int)loaded[a]["served"]![ModuleBuilds.AckIn]!);
        Assert.Equal(1, (int)loaded[b]["served"]![ModuleBuilds.AckIn]!);

        // By release too: an activity at a release the distribution does not carry is refused, never served from another.
        Assert.Equal("NotInDistribution", (string?)(await Post(c, Ack(a, "1.1.0")))["name"]);
        // A platform activity runs from the image, within the distribution's own table.
        var pw = await Post(c, new { activity = "p.decide.posting-window.v1", release = "1.0.0", distribution = a, now = "2026-11-23T08:00:00Z", tenant = Bank, @params = new { windowStart = "06:00", windowEnd = "20:00" }, view = new { } });
        Assert.Equal("result", (string?)pw["kind"]);

        // A drains once B is active; with no payment open it is removed (its longest wait is zero), and the host unloads it.
        await using (var db = StoreDbContext.Open(store.Options))
        {
            var reg = new DistributionRegistry(db);
            await reg.Activate(a, Env, "test.engineer", "A first");
            await reg.Activate(b, Env, "test.engineer", "B takes new payments");
            Assert.Equal("drained", await reg.Refresh(a, Env, "test"));
            await reg.Remove(a, Env, "test.engineer", "A is drained");
        }
        JsonObject? gone = null;
        for (var until = DateTime.UtcNow.AddSeconds(30); DateTime.UtcNow < until; await Task.Delay(500))
        {
            var m = (await c.GetFromJsonAsync<JsonObject>("/modules"))!;
            gone = (m["unloaded"] as JsonArray)!.OfType<JsonObject>().FirstOrDefault(x => (string?)x["distribution"] == a);
            if (gone is not null && (bool)gone["collected"]!) break;
        }
        Assert.NotNull(gone);
        Assert.True((bool)gone!["collected"]!, "A's context was unloaded and collected");
        var after = (await c.GetFromJsonAsync<JsonObject>("/modules"))!;
        Assert.DoesNotContain((after["loaded"] as JsonArray)!.OfType<JsonObject>(), x => (string?)x["distribution"] == a);
        Assert.Equal("DistributionRemoved", (string?)(await Post(c, Ack(a)))["name"]);
        // B goes on.
        Assert.Equal("wps-build-b", (string?)(await Post(c, Ack(b)))["writes"]!["status"]!["additionalInformation"]);
    }

    [Fact]
    public async Task A_distribution_whose_assembly_does_not_match_its_hash_or_whose_contract_is_unsupported_is_not_loaded()
    {
        var tampered = await Install("2026.11.23-3", store.BuildA, carried: store.BuildB);
        var future = await Install("2026.11.23-4", store.BuildA, contract: "2.0.0");
        var newer = await Install("2026.11.23-5", store.BuildA, contract: "1.9.0");
        await using var host = new Host(store.Options);
        var c = host.CreateClient();
        var t = await Post(c, Ack(tampered));
        Assert.Equal("NativeModuleRefused", (string?)t["name"]);
        Assert.Contains("does not match its SHA-256", (string?)t["message"]);
        var f = await Post(c, Ack(future));
        Assert.Equal("NativeModuleRefused", (string?)f["name"]);
        Assert.Contains("native contract 2.0.0", (string?)f["message"]);
        Assert.Contains("native contract 1.9.0", (string?)(await Post(c, Ack(newer)))["message"]);
        Assert.Equal("DistributionUnknown", (string?)(await Post(c, Ack("dist/example-bank@2026.11.23-99")))["name"]);
    }

    [Fact]
    public void The_contract_is_versioned_and_the_runtime_takes_its_major_up_to_its_own_minor()
    {
        var v = typeof(INativeModule).Assembly.GetName().Version!;
        Assert.Equal(NativeContract.Version, $"{v.Major}.{v.Minor}.{v.Build}");
        Assert.Equal(NativeContract.AssemblyName, typeof(INativeModule).Assembly.GetName().Name);
        Assert.True(NativeContract.Supports("1.0.0"));
        Assert.False(NativeContract.Supports("2.0.0"));
        Assert.False(NativeContract.Supports("1.1.0"));
        Assert.False(NativeContract.Supports("0.9.0"));
        Assert.False(NativeContract.Supports(null));
        // The WPS build references the contract it was built against, read without loading it.
        Assert.Equal("1.0.0", NativeAssemblies.ContractOf(store.BuildA));
        Assert.Null(NativeAssemblies.ContractOf(File.ReadAllBytes(typeof(NativeContract).Assembly.Location)));
    }
}
