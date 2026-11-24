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
    public byte[] BuildA = [], BuildB = [], PlatformA = [], PlatformB = [];

    public async Task InitializeAsync()
    {
        var start = pg.StartAsync();
        // Two scheme patch releases of WPS: the same code-behind, built twice with another ACK mapping.
        BuildA = await Task.Run(() => ModuleBuilds.Wps("wps-build-a"));
        BuildB = await Task.Run(() => ModuleBuilds.Wps("wps-build-b"));
        // Two platform releases: the platform module built twice with another posting window decision (R-09-126).
        PlatformA = await Task.Run(() => ModuleBuilds.Platform("platform-build-a"));
        PlatformB = await Task.Run(() => ModuleBuilds.Platform("platform-build-b"));
        await start;
        await using var db = StoreDbContext.Open(Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await pg.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class StoreCollection : ICollectionFixture<Store> { public const string Name = "native-store"; }

/// <summary>
/// Spec R-09-120 to R-09-126: the native host's image holds no native code-behind of any pack; each distribution's native assemblies are loaded from
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

    async Task<string> Install(string id, byte[] module, string contract = NativeContract.Version, byte[]? carried = null) =>
        await Install(ModuleBuilds.Distribution(Bank, id, module, contract, carried), id);

    async Task<string> Install((System.Text.Json.Nodes.JsonObject Manifest, byte[] Package) d, string id)
    {
        var (manifest, package) = d;
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
        Assert.Equal("native.NotNative", (string?)(await Post(c, Ack(null)))["name"]);
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
        Assert.Equal("native.NotInDistribution", (string?)(await Post(c, Ack(a, "1.1.0")))["name"]);

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
        Assert.Equal("native.DistributionRemoved", (string?)(await Post(c, Ack(a)))["name"]);
        // B goes on.
        Assert.Equal("wps-build-b", (string?)(await Post(c, Ack(b)))["writes"]!["status"]!["additionalInformation"]);
    }

    static object Window(string? distribution) => new
    {
        activity = ModuleBuilds.PostingWindow, release = ModuleBuilds.PostingWindowRelease, distribution, now = "2026-11-23T08:00:00Z", tenant = Bank,
        @params = new { windowStart = "06:00", windowEnd = "20:00" }, view = new { header = new { paymentId = "PAY-NATIVE-2" } },
    };

    [Fact]
    public async Task Each_distribution_runs_its_own_build_of_a_platform_native_activity_side_by_side()
    {
        // R-09-126 (Issue I-221): the platform's native activities are a module each distribution carries, as a scheme's are.
        Assert.NotEqual(ModuleBuilds.Sha(store.PlatformA), ModuleBuilds.Sha(store.PlatformB));
        var window = $"{ModuleBuilds.PostingWindow}@{ModuleBuilds.PostingWindowRelease}";
        var a = await Install(ModuleBuilds.Distribution(Bank, "2026.11.23-11", NativeContract.Version,
            new ModuleBuilds.Module("p", ModuleBuilds.PlatformAssembly, store.PlatformA, [window]),
            new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, store.BuildA, [$"{ModuleBuilds.AckIn}@{ModuleBuilds.AckInRelease}"])), "2026.11.23-11");
        var b = await Install(ModuleBuilds.Distribution(Bank, "2026.11.23-12", NativeContract.Version,
            new ModuleBuilds.Module("p", ModuleBuilds.PlatformAssembly, store.PlatformB, [window])), "2026.11.23-12");
        await using var host = new Host(store.Options);
        var c = host.CreateClient();

        // The image holds no platform native code-behind: with no distribution, and no development module, it has none to run.
        Assert.Equal("native.NotNative", (string?)(await Post(c, Window(null)))["name"]);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), x => x.GetName().Name == ModuleBuilds.PlatformAssembly && System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(x) == System.Runtime.Loader.AssemblyLoadContext.Default);

        // A payment pinned to A runs A's build of the platform activity; one pinned to B runs B's, in one host.
        var ra = await Post(c, Window(a));
        Assert.Equal("result", (string?)ra["kind"]);
        Assert.Equal("open", (string?)ra["outcome"]);
        Assert.Equal("platform-build-a", (string?)ra["writes"]!["facts.postingWindow"]!["build"]);
        Assert.Equal("platform-build-b", (string?)(await Post(c, Window(b)))["writes"]!["facts.postingWindow"]!["build"]);
        Assert.Equal("platform-build-a", (string?)(await Post(c, Window(a)))["writes"]!["facts.postingWindow"]!["build"]);
        // A's WPS module runs beside A's platform module, in the same context.
        Assert.Equal("wps-build-a", (string?)(await Post(c, Ack(a)))["writes"]!["status"]!["additionalInformation"]);

        var loaded = ((await c.GetFromJsonAsync<JsonObject>("/modules"))!["loaded"] as JsonArray)!.OfType<JsonObject>().ToDictionary(x => (string)x["distribution"]!);
        var mods = (JsonArray)loaded[a]["modules"]!;
        Assert.Equal(["p", "uae.wps"], mods.Select(m => (string)m!["scope"]!).Order());
        Assert.Equal(ModuleBuilds.Sha(store.PlatformA), (string?)mods.First(m => (string?)m!["scope"] == "p")!["sha256"]);
        Assert.Equal(ModuleBuilds.Sha(store.PlatformB), (string?)loaded[b]["modules"]![0]!["sha256"]);
        Assert.NotEqual((string?)loaded[a]["context"], (string?)loaded[b]["context"]);
    }

    [Fact]
    public async Task A_distribution_whose_module_references_the_image_beyond_the_contract_is_not_loaded()
    {
        // R-09-125 (Issue I-220): a module references the native contract only; one that references the ports is refused, with its code.
        var reaching = ModuleBuilds.ReachesIntoTheImage();
        var bad = await Install("2026.11.23-13", reaching);
        var good = await Install("2026.11.23-14", store.BuildA);
        await using var host = new Host(store.Options);
        var c = host.CreateClient();
        var r = await Post(c, Ack(bad));
        Assert.Equal(NativeAssemblies.ImageReference, (string?)r["name"]);
        Assert.Contains("references TransactHub.Ports", (string?)r["message"]);
        // The WPS module as built references the contract only, and runs.
        Assert.Equal("ack", (string?)(await Post(c, Ack(good)))["outcome"]);
        Assert.DoesNotContain(NativeAssemblies.References(store.BuildA), n => n.StartsWith("TransactHub.") && n != NativeContract.AssemblyName);
        Assert.DoesNotContain(NativeAssemblies.References(store.PlatformA), n => n.StartsWith("TransactHub.") && n != NativeContract.AssemblyName);
    }

    [Fact]
    public async Task A_distribution_whose_assembly_does_not_match_its_hash_or_whose_contract_is_unsupported_is_not_loaded()
    {
        var tampered = await Install("2026.11.23-3", store.BuildA, carried: store.BuildB);
        var future = await Install("2026.11.23-4", store.BuildA, contract: "3.0.0");
        var newer = await Install("2026.11.23-5", store.BuildA, contract: "2.9.0");
        // Built against 2.1.0, its manifest claiming 1.1.0, a contract 2.x no longer takes (R-09-262).
        var claims = await Install("2026.11.23-6", store.BuildA, contract: "1.1.0");
        await using var host = new Host(store.Options);
        var c = host.CreateClient();
        var t = await Post(c, Ack(tampered));
        Assert.Equal("native.ModuleRefused", (string?)t["name"]);
        Assert.Contains("does not match its SHA-256", (string?)t["message"]);
        var f = await Post(c, Ack(future));
        Assert.Equal(NativeAssemblies.ContractUnsupported, (string?)f["name"]);
        Assert.Contains("native contract 3.0.0", (string?)f["message"]);
        var n = await Post(c, Ack(newer));
        Assert.Equal(NativeAssemblies.ContractUnsupported, (string?)n["name"]);
        Assert.Contains("native contract 2.9.0", (string?)n["message"]);
        var cl = await Post(c, Ack(claims));
        Assert.Equal(NativeAssemblies.ContractUnsupported, (string?)cl["name"]);
        Assert.Contains("native contract 1.1.0", (string?)cl["message"]);
        Assert.Equal("native.DistributionUnknown", (string?)(await Post(c, Ack("dist/example-bank@2026.11.23-99")))["name"]);
    }

    [Fact]
    public void The_contract_is_versioned_and_the_runtime_takes_its_major_up_to_its_own_minor()
    {
        var v = typeof(INativeModule).Assembly.GetName().Version!;
        Assert.Equal(NativeContract.Version, $"{v.Major}.{v.Minor}.{v.Build}");
        Assert.Equal(NativeContract.AssemblyName, typeof(INativeModule).Assembly.GetName().Name);
        // 2.0.0 (R-09-262): the same major, at a minor no later than the runtime's own; every 1.x module is refused. 2.1.0 (R-09-276), a
        // minor: a 2.0 module runs on it, and a 2.1 module is refused by a 2.0 runtime.
        Assert.Equal("2.1.0", NativeContract.Version);
        Assert.True(NativeContract.Supports("2.0.0"));
        Assert.True(NativeContract.Supports("2.1.0"));
        Assert.False(NativeContract.Supports("1.0.0"));
        Assert.False(NativeContract.Supports("1.1.0"));
        Assert.False(NativeContract.Supports("2.2.0"));
        Assert.False(NativeContract.Supports("3.0.0"));
        Assert.False(NativeContract.Supports(null));
        Assert.True(NativeContract.Supports("2.0.0", "2.0.0"));
        Assert.False(NativeContract.Supports("2.1.0", "2.0.0"));
        Assert.Contains("take the runtime images of contract 2.1", NativeContract.Refusal("2.1.0", "2.0.0"));
        // 2.0.0 hands out no type of the image (Issue I-226) and gives a module bound ports (Issue I-315).
        Assert.Null(typeof(INativeServices).GetMethod("Service"));
        Assert.NotNull(typeof(INativeServices).GetProperty(nameof(INativeServices.BoundPorts)));
        Assert.Equal(typeof(INativeBoundPorts), typeof(INativeServices).GetProperty(nameof(INativeServices.BoundPorts))!.PropertyType);
        Assert.Equal("binding-gate:p.port.core-banking.v1", NativeContributions.BindingGate("p.port.core-banking.v1"));
        // The WPS and platform builds reference the contract they were built against, read without loading them.
        Assert.Equal(NativeContract.Version, NativeAssemblies.ContractOf(store.BuildA));
        Assert.Equal(NativeContract.Version, NativeAssemblies.ContractOf(store.PlatformA));
        Assert.Equal("2.0.0", NativeAssemblies.ContractOf(TransactHub.Tests.Support.ContractBuilds.BuiltAgainst(store.BuildA, "2.0.0")));
        Assert.Null(NativeAssemblies.ContractOf(File.ReadAllBytes(typeof(NativeContract).Assembly.Location)));
    }
}
