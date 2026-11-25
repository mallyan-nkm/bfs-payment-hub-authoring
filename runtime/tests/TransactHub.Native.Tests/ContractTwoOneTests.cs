using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactHub.Activities.Native;
using TransactHub.Store;
using TransactHub.Tests.Support;

namespace TransactHub.Native.Tests;

/// <summary>
/// Native contract 2.1.0, a minor (R-09-276 to R-09-279; Issues I-381, I-382): a module built against 2.0 still loads and runs, beside a 2.1
/// module in one distribution; a 2.1 module is refused by a 2.0 runtime; and the reservation, the posting and the reversal are the platform
/// module's, served from the distribution, on the image's core banking gated by the module, while the image answers none of them.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ContractTwoOneTests(Store store)
{
    const string Bank = "cb-bank";

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
        await new DistributionRegistry(db).Store(Bank, id, package, manifest, ModuleBuilds.Sha(package)[7..], "test", "native contract 2.1.0");
        return DistributionRegistry.IdOf(Bank, id);
    }

    static async Task<JsonObject> Post(HttpClient c, object body)
    {
        var text = await (await c.PostAsJsonAsync("/native", body)).Content.ReadAsStringAsync();
        try { return (JsonObject)JsonNode.Parse(text)!; }
        catch (System.Text.Json.JsonException) { throw new Xunit.Sdk.XunitException($"The native host answered: {text[..Math.Min(text.Length, 3000)]}"); }
    }

    static object Ack(string d) => new
    {
        activity = ModuleBuilds.AckIn, release = ModuleBuilds.AckInRelease, distribution = d, now = "2026-11-24T08:00:00Z", tenant = Bank, @params = new { },
        view = new { header = new { scheme = "uae.wps", paymentId = "PAY-NATIVE-21-1" }, messages = new { lastReceived = new { status = "accepted" } } },
    };

    static object Window(string d) => new
    {
        activity = ModuleBuilds.PostingWindow, release = ModuleBuilds.PostingWindowRelease, distribution = d, now = "2026-11-24T08:00:00Z", tenant = Bank,
        @params = new { windowStart = "06:00", windowEnd = "20:00" }, view = new { header = new { paymentId = "PAY-NATIVE-21-2" } },
    };

    [Fact]
    public async Task A_2_0_module_still_loads_and_runs_beside_a_2_1_module_and_a_2_1_module_is_refused_by_a_2_0_runtime()
    {
        var wps20 = ContractBuilds.BuiltAgainst(store.BuildA, "2.0.0");
        Assert.Equal("2.0.0", NativeAssemblies.ContractOf(wps20));
        var ack = $"{ModuleBuilds.AckIn}@{ModuleBuilds.AckInRelease}";
        var window = $"{ModuleBuilds.PostingWindow}@{ModuleBuilds.PostingWindowRelease}";
        // A distribution of 2.0 modules, built before the minor: the 2.1 runtime takes it.
        var old = await Install("2026.11.24-51", "2.0.0", new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, wps20, [ack]));
        // A 2.0 WPS module beside a 2.1 platform module: the manifest names the latest minor of its modules.
        var mixed = await Install("2026.11.24-52", NativeContract.Version,
            new ModuleBuilds.Module("p", ModuleBuilds.PlatformAssembly, store.PlatformA, [window]),
            new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, wps20, [ack]));
        // A 2.1 module under a manifest that names 2.0: refused, the manifest names too early a contract.
        var under = await Install("2026.11.24-53", "2.0.0", new ModuleBuilds.Module("p", ModuleBuilds.PlatformAssembly, store.PlatformA, [window]));
        await using var host = new Host(store.Options);
        var c = host.CreateClient();

        Assert.Equal("wps-build-a", (string?)(await Post(c, Ack(old)))["writes"]!["status"]!["additionalInformation"]);
        Assert.Equal("wps-build-a", (string?)(await Post(c, Ack(mixed)))["writes"]!["status"]!["additionalInformation"]);
        Assert.Equal("platform-build-a", (string?)(await Post(c, Window(mixed)))["writes"]!["facts.postingWindow"]!["build"]);
        var u = await Post(c, Window(under));
        Assert.Equal("native.ModuleRefused", (string?)u["name"]);
        Assert.Contains($"was built against native contract {NativeContract.Version}, its manifest says 2.0.0", (string?)u["message"]);

        // A 2.0 runtime refuses a 2.1 module, at install and at load, with the contract's code (the check the image runs, at 2.0).
        var (manifest, package) = ModuleBuilds.Distribution(Bank, "2026.11.24-54", NativeContract.Version, new ModuleBuilds.Module("p", ModuleBuilds.PlatformAssembly, store.PlatformA, [window]));
        var files = NativeAssemblies.Unzip(package);
        var e = Assert.Throws<NativeAssembliesRefused>(() => NativeAssemblies.Check(manifest, f => files.TryGetValue(f, out var x) ? x : null, files.Keys, runtimeContract: "2.0.0"));
        Assert.Equal(NativeAssemblies.ContractUnsupported, e.Code);
        Assert.Contains("this runtime supports 2.x up to 2.0.0", e.Message);
        // A 2.1 runtime refuses a 2.2 module so too, and says the images move first (R-09-309, Issue I-398).
        var e21 = Assert.Throws<NativeAssembliesRefused>(() => NativeAssemblies.Check(manifest, f => files.TryGetValue(f, out var x) ? x : null, files.Keys, runtimeContract: "2.1.0"));
        Assert.Equal(NativeAssemblies.ContractUnsupported, e21.Code);
        Assert.Contains("built against native contract 2.2.0", e21.Message);
        Assert.Contains("this runtime supports 2.x up to 2.1.0: take the runtime images of contract 2.2 first, then install the distribution", e21.Message);
        Assert.Contains("section 3.4", e21.Message);
        // The same distribution on this runtime is taken.
        Assert.Single(NativeAssemblies.Check(manifest, f => files.TryGetValue(f, out var x) ? x : null, files.Keys));
    }

    /// <summary>The reservation as factory 1.22.0 published it: a builtin of the reservation service on core banking, which the image answered.</summary>
    public const string ReservationBefore21 = "kind: activity\nname: p.reserve.funds.v1\nrelease: 1.0.0\ntype: reserve\nports:\n  - p.port.core-banking.v1\nimplementation:\n  binding: builtin\n  service: reservation\n";

    /// <summary>
    /// R-09-291 (Issue I-384): a distribution built before contract 2.1.0 carries the reservation, a builtin the image answered and no
    /// longer does, and no module of it serves it. The native host refuses it as it loads the distribution, with its own code, for every
    /// attempt of it, not only at the reservation's step; the image's own activities are no gap.
    /// </summary>
    [Fact]
    public async Task A_distribution_whose_native_activity_nothing_serves_is_refused_when_the_host_loads_it()
    {
        var ack = $"{ModuleBuilds.AckIn}@{ModuleBuilds.AckInRelease}";
        var hold = "kind: activity\nname: p.hold.approval.v1\nrelease: 1.1.0\ntype: hold\nports:\n  - p.port.operations-queue.v1\nimplementation:\n  binding: builtin\n  service: operations\n";
        var (manifest, package) = ModuleBuilds.Distribution(Bank, "2026.11.24-61", NativeContract.Version,
            new Dictionary<string, string> { ["p.reserve.funds.v1@1.0.0"] = ReservationBefore21, ["p.hold.approval.v1@1.1.0"] = hold },
            new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, store.BuildA, [ack]));
        await using (var db = StoreDbContext.Open(store.Options))
            await new DistributionRegistry(db).Store(Bank, "2026.11.24-61", package, manifest, ModuleBuilds.Sha(package)[7..], "test", "installed before R-09-291");
        var d = DistributionRegistry.IdOf(Bank, "2026.11.24-61");
        await using var host = new Host(store.Options);
        var r = await Post(host.CreateClient(), Ack(d));
        Assert.Equal(("error", NativeAssemblies.ActivityUnserved, false), ((string?)r["kind"], (string?)r["name"], (bool?)r["retryable"]));
        Assert.Contains("p.reserve.funds.v1@1.0.0", (string?)r["message"]);
        Assert.DoesNotContain("p.hold.approval.v1", (string?)r["message"]);
        Assert.Contains("section 3.3", (string?)r["message"]);
    }

    static object Attempt(string? d, string activity, string release, object view, object? p = null, string now = "2026-11-24T06:00:00Z") => new
    {
        activity, release, distribution = d, now, tenant = Bank, @params = p ?? new { }, view,
    };

    [Fact]
    public async Task The_reservation_the_posting_and_the_reversal_are_served_from_the_platform_module_of_the_distribution()
    {
        var banks = Directory.CreateTempSubdirectory("th-native-cb-banks-");
        try
        {
            Directory.CreateDirectory(Path.Combine(banks.FullName, Bank));
            await File.WriteAllTextAsync(Path.Combine(banks.FullName, Bank, "core-banking.yaml"), $"""
                kind: port-bindings
                tenant: {Bank}
                bindings:
                  - port: p.port.core-banking.v1
                    adapter: simulator
                    validFrom: 2026-01-01T00:00:00Z
                    settings:
                      openingBalance: "5000"
                      currency: AED
                      postingWindowStart: "07:00"
                      postingWindowEnd: "22:00"
                """);
            string[] served = ["p.reserve.funds.v1@1.1.0", "p.post.accounting.v1@1.3.0", "p.post.reverse.v1@1.1.0", "p.post.verify.v1@1.1.0", "p.reserve.release.v1@1.1.0"];
            var withModule = await Install("2026.11.24-55", NativeContract.Version, new ModuleBuilds.Module("p", ModuleBuilds.PlatformAssembly, store.PlatformA, served));
            await using var host = new Host(store.Options, banks.FullName);
            var c = host.CreateClient();
            var view = new JsonObject
            {
                ["header"] = new JsonObject { ["paymentId"] = "P-NATIVE-CB-1", ["direction"] = "outward", ["businessDate"] = "2026-11-24" },
                ["payment"] = new JsonObject { ["debtorAccount"] = new JsonObject { ["id"] = "0010000000001" }, ["instructedAmount"] = new JsonObject { ["value"] = 3500, ["currency"] = "AED" } },
                ["batch"] = new JsonObject { ["controlSum"] = 3500 },
                ["facts"] = new JsonObject(),
            };
            async Task<JsonObject> Run(string activity, string release, object? p = null, string now = "2026-11-24T06:00:00Z")
            {
                var r = await Post(c, Attempt(withModule, activity, release, view.DeepClone(), p, now));
                if ((string?)r["kind"] == "result")
                    foreach (var (k, v) in (JsonObject)r["writes"]!) ((JsonObject)view["facts"]!)[k["facts.".Length..]] = v?.DeepClone();
                return r;
            }
            var reserve = await Run("p.reserve.funds.v1", "1.1.0");
            Assert.True((string?)reserve["outcome"] == "reserved", reserve.ToJsonString());
            var template = new { template = "default", transactionCode = "PAY", reversalTransactionCode = "REV", narration = "Payment", settlementAccount = "9990000000001", chargeSlabs = Array.Empty<object>(), vatPercent = 0 };
            Assert.Equal("posted", (string?)(await Run("p.post.accounting.v1", "1.3.0", template))["outcome"]);
            Assert.Equal("verified", (string?)(await Run("p.post.verify.v1", "1.1.0"))["outcome"]);
            Assert.Equal("reversed", (string?)(await Run("p.post.reverse.v1", "1.1.0"))["outcome"]);
            Assert.Equal("released", (string?)(await Run("p.reserve.release.v1", "1.1.0"))["outcome"]);
            // Outside the binding's window the module's gate holds core banking back, a retryable error.
            var closed = await Run("p.reserve.funds.v1", "1.1.0", now: "2026-11-24T20:00:00Z");
            Assert.Equal(("error", "TransientError", true), ((string?)closed["kind"], (string?)closed["name"], (bool?)closed["retryable"]));
            var postings = (await c.GetFromJsonAsync<JsonArray>("/postings"))!.OfType<JsonObject>().Single(x => (string?)x["tenant"] == Bank);
            Assert.Equal(["P-NATIVE-CB-1", "P-NATIVE-CB-1:REV"], ((JsonArray)postings["batches"]!).Select(b => (string?)b!["reference"]));

            // The image answers none of them: with no distribution and no development module, the host has no reservation to run.
            Assert.Equal("native.NotNative", (string?)(await Post(c, Attempt(null, "p.reserve.funds.v1", "1.1.0", view.DeepClone())))["name"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TH_BANKS_DIR", null);
            banks.Delete(true);
        }
    }

    /// <summary>An imported scheme's posting that posts with the platform's (R-09-311, Issue I-400): a builtin the runtime answers.</summary>
    const string SchemePosting = "kind: activity\nname: uae.xps.post.payment.v1\nrelease: 1.0.0\ntype: post\nports:\n  - p.port.core-banking.v1\nimplementation:\n  binding: builtin\n  service: accounting\n  native: true\n  postsWith: p.post.accounting.v1\n";

    async Task<string> InstallPosting(string id, string postsWith, params ModuleBuilds.Module[] modules)
    {
        var (manifest, package) = ModuleBuilds.Distribution(Bank, id, NativeContract.Version, new Dictionary<string, string> { ["uae.xps.post.payment.v1@1.0.0"] = SchemePosting }, modules);
        ((JsonObject)manifest["native"]!)["postsWith"] = new JsonObject { ["uae.xps.post.payment.v1@1.0.0"] = postsWith };
        await using var db = StoreDbContext.Open(store.Options);
        await new DistributionRegistry(db).Store(Bank, id, package, manifest, ModuleBuilds.Sha(package)[7..], "test", "a scheme's posting that posts with the platform's");
        return DistributionRegistry.IdOf(Bank, id);
    }

    /// <summary>
    /// R-09-311 (Issue I-400): an imported scheme brings no native module, so its posting, when its contract is the platform posting's,
    /// posts with the platform's: the native host answers it from the distribution's platform module, with the scheme activity's own
    /// parameters as the accounting template, on the bank's core-banking binding in force and through the posting gate the platform module
    /// contributes. Outside the binding's window the gate holds core banking back. Without the platform module the distribution is refused
    /// as it is loaded: there is no other way to core banking.
    /// </summary>
    [Fact]
    public async Task An_imported_schemes_posting_posts_with_the_platforms_posting_through_the_posting_gate()
    {
        var banks = Directory.CreateTempSubdirectory("th-native-pw-banks-");
        try
        {
            Directory.CreateDirectory(Path.Combine(banks.FullName, Bank));
            await File.WriteAllTextAsync(Path.Combine(banks.FullName, Bank, "core-banking.yaml"), $"""
                kind: port-bindings
                tenant: {Bank}
                bindings:
                  - port: p.port.core-banking.v1
                    adapter: simulator
                    validFrom: 2026-01-01T00:00:00Z
                    settings:
                      openingBalance: "5000"
                      currency: AED
                      postingWindowStart: "07:00"
                      postingWindowEnd: "22:00"
                """);
            var withPlatform = await InstallPosting("2026.11.25-81", "p.post.accounting.v1", new ModuleBuilds.Module("p", ModuleBuilds.PlatformAssembly, store.PlatformA, ["p.post.accounting.v1@1.3.0"]));
            var withoutPlatform = await InstallPosting("2026.11.25-82", "p.post.accounting.v1", new ModuleBuilds.Module("uae.wps", ModuleBuilds.Assembly, store.BuildA, [$"{ModuleBuilds.AckIn}@{ModuleBuilds.AckInRelease}"]));
            await using var host = new Host(store.Options, banks.FullName);
            var c = host.CreateClient();
            var view = new JsonObject
            {
                ["header"] = new JsonObject { ["paymentId"] = "P-NATIVE-PW-1", ["direction"] = "outward", ["businessDate"] = "2026-11-25" },
                ["payment"] = new JsonObject { ["debtorAccount"] = new JsonObject { ["id"] = "0010000000001" }, ["instructedAmount"] = new JsonObject { ["value"] = 1200, ["currency"] = "AED" } },
                ["facts"] = new JsonObject(),
            };
            // The scheme's own template: its parameters, as the bank set them on the scheme's activity.
            var template = new { template = "xps-payment", transactionCode = "XPS", reversalTransactionCode = "XRV", narration = "Scheme payment", settlementAccount = "9990000000002", chargeSlabs = Array.Empty<object>(), vatPercent = 0 };
            var posted = await Post(c, Attempt(withPlatform, "uae.xps.post.payment.v1", "1.0.0", view.DeepClone(), template, "2026-11-25T06:00:00Z"));
            Assert.True((string?)posted["outcome"] == "posted", posted.ToJsonString());
            Assert.Equal("xps-payment", (string?)posted["writes"]!["facts.posting"]!["template"]);
            var postings = (await c.GetFromJsonAsync<JsonArray>("/postings"))!.OfType<JsonObject>().Single(x => (string?)x["tenant"] == Bank);
            var batch = ((JsonArray)postings["batches"]!).OfType<JsonObject>().Single(b => (string?)b["reference"] == "P-NATIVE-PW-1");
            Assert.All(((JsonArray)batch["legs"]!).OfType<JsonObject>(), l => Assert.Equal("XPS", (string?)l["txnCode"]));
            // The posting gate: outside the binding's window core banking is not asked, a retryable error.
            var closed = await Post(c, Attempt(withPlatform, "uae.xps.post.payment.v1", "1.0.0", view.DeepClone(), template, "2026-11-25T20:00:00Z"));
            Assert.Equal(("error", "TransientError", true), ((string?)closed["kind"], (string?)closed["name"], (bool?)closed["retryable"]));
            // Without the platform module nothing answers it, and the distribution is refused as it is loaded.
            var refused = await Post(c, Attempt(withoutPlatform, "uae.xps.post.payment.v1", "1.0.0", view.DeepClone(), template, "2026-11-25T06:00:00Z"));
            Assert.Equal(("error", NativeAssemblies.ActivityUnserved), ((string?)refused["kind"], (string?)refused["name"]));
            Assert.Contains("uae.xps.post.payment.v1@1.0.0", (string?)refused["message"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TH_BANKS_DIR", null);
            banks.Delete(true);
        }
    }
}
