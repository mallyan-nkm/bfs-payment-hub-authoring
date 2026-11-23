using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactHub.Store;

namespace TransactHub.Native.Tests;

/// <summary>
/// Spec R-09-130 to R-09-132 (Issue I-222): an attempt names its payment's distribution, as the engine sent it, and the native host
/// serves it from that distribution's build, whatever worker carried it; it refuses an attempt whose worker runs another
/// distribution (<c>WrongDistribution</c>) and one for a distribution the payment is not pinned to (<c>NotPinned</c>). An attempt
/// that names none is served as before: from the worker's distribution, then from the development modules.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AttemptRoutingTests(Store store)
{
    const string Bank = "example-bank";
    const string Env = "routing";

    sealed class Host : WebApplicationFactory<Program>
    {
        public Host(StoreOptions store)
        {
            Environment.SetEnvironmentVariable("TH_STORE_PROVIDER", store.Provider.ToString());
            Environment.SetEnvironmentVariable("TH_STORE_CONNECTION", store.ConnectionString);
            Environment.SetEnvironmentVariable("TH_NATIVE_REFRESH", "00:00:01");
        }

        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("TH_NATIVE_URLS", "http://127.0.0.1:7300");
    }

    static object Ack(string paymentId, string? distribution, string? workerDistribution = null) => new
    {
        activity = ModuleBuilds.AckIn, release = ModuleBuilds.AckInRelease, distribution, workerDistribution, now = "2026-11-24T08:00:00Z", tenant = Bank, @params = new { },
        view = new { header = new { scheme = "uae.wps", paymentId }, messages = new { lastReceived = new { status = "accepted" } } },
    };

    static async Task<JsonObject> Post(HttpClient c, object body)
    {
        var text = await (await c.PostAsJsonAsync("/native", body)).Content.ReadAsStringAsync();
        try { return (JsonObject)JsonNode.Parse(text)!; }
        catch (System.Text.Json.JsonException) { throw new Xunit.Sdk.XunitException($"The native host answered: {text[..Math.Min(text.Length, 3000)]}"); }
    }

    static string? Build(JsonObject r) => (string?)r["writes"]?["status"]?["additionalInformation"];

    [Fact]
    public async Task An_attempt_runs_the_distribution_it_names_and_is_refused_for_another_workers_or_another_pin()
    {
        var (manifestA, packageA) = ModuleBuilds.Distribution(Bank, "2026.11.24-1", store.BuildA);
        var (manifestB, packageB) = ModuleBuilds.Distribution(Bank, "2026.11.24-2", store.BuildB);
        var a = DistributionRegistry.IdOf(Bank, "2026.11.24-1");
        var b = DistributionRegistry.IdOf(Bank, "2026.11.24-2");
        const string pinnedToA = "PAY-ROUTE-A", pinnedToB = "PAY-ROUTE-B", unpinned = "PAY-ROUTE-NONE";
        await using (var db = StoreDbContext.Open(store.Options))
        {
            var reg = new DistributionRegistry(db);
            await reg.Store(Bank, "2026.11.24-1", packageA, manifestA, ModuleBuilds.Sha(packageA)[7..], "test", "routing");
            await reg.Store(Bank, "2026.11.24-2", packageB, manifestB, ModuleBuilds.Sha(packageB)[7..], "test", "routing");
            await reg.Activate(a, Env, "test.engineer", "A takes new payments");
            Assert.Equal(a, await reg.Pin(pinnedToA, Bank, Env, "test"));
            await reg.Activate(b, Env, "test.engineer", "B takes new payments");
            Assert.Equal(b, await reg.Pin(pinnedToB, Bank, Env, "test"));
        }
        await using var host = new Host(store.Options);
        var c = host.CreateClient();

        // One worker that names no distribution carries both payments: each runs its own distribution's build (R-09-130, R-09-133).
        Assert.Equal("wps-build-a", Build(await Post(c, Ack(pinnedToA, a))));
        Assert.Equal("wps-build-b", Build(await Post(c, Ack(pinnedToB, b))));
        Assert.Equal("wps-build-a", Build(await Post(c, Ack(pinnedToA, a))));
        // A worker of B carries B's payment.
        Assert.Equal("wps-build-b", Build(await Post(c, Ack(pinnedToB, b, b))));

        // A worker of A does not carry B's payment (R-09-131).
        var wrong = await Post(c, Ack(pinnedToB, b, a));
        Assert.Equal("error", (string?)wrong["kind"]);
        Assert.Equal("WrongDistribution", (string?)wrong["name"]);
        Assert.False((bool)wrong["retryable"]!);
        Assert.Contains(a, (string?)wrong["message"]);

        // B's payment is not served from A, though A is installed and its worker names A (R-09-132).
        foreach (var asked in new[] { Ack(pinnedToB, a), Ack(pinnedToB, a, a) })
        {
            var notPinned = await Post(c, asked);
            Assert.Equal("NotPinned", (string?)notPinned["name"]);
            Assert.False((bool)notPinned["retryable"]!);
            Assert.Contains(b, (string?)notPinned["message"]);
        }

        // An attempt that names none is served as before: from its worker's distribution, then from the development modules.
        Assert.Equal("wps-build-b", Build(await Post(c, Ack(pinnedToB, null, b))));
        Assert.Equal("NotNative", (string?)(await Post(c, Ack(pinnedToB, null)))["name"]);

        // A payment with no pin in the store (the studio's test runs) is served from the distribution the attempt names (Issue I-230).
        Assert.Equal("wps-build-a", Build(await Post(c, Ack(unpinned, a))));

        var modules = (await c.GetFromJsonAsync<JsonObject>("/modules"))!;
        var loaded = (modules["loaded"] as JsonArray)!.OfType<JsonObject>().ToDictionary(x => (string)x["distribution"]!);
        Assert.Equal(3, (int)loaded[a]["served"]![ModuleBuilds.AckIn]!);
        Assert.Equal(3, (int)loaded[b]["served"]![ModuleBuilds.AckIn]!);
    }

    /// <summary>
    /// R-09-143 (Issue I-231): a runtime's native host (TH_BANK) refuses an attempt for a payment with no pin as NotPinned; only an
    /// explicit development or test mode (TH_NATIVE_MODE dev or test) serves it, as a host that serves no runtime's bank does.
    /// </summary>
    [Fact]
    public async Task A_runtimes_native_host_refuses_an_attempt_for_a_payment_with_no_pin_unless_in_dev_or_test_mode()
    {
        var a = DistributionRegistry.IdOf(Bank, "2026.11.24-9");
        const string pinned = "PAY-PIN-RUNTIME", unpinned = "PAY-PIN-NONE";
        await using (var db = StoreDbContext.Open(store.Options))
        {
            var (manifest, package) = ModuleBuilds.Distribution(Bank, "2026.11.24-9", store.BuildA);
            var reg = new DistributionRegistry(db);
            await reg.Store(Bank, "2026.11.24-9", package, manifest, ModuleBuilds.Sha(package)[7..], "test", "routing");
            await reg.Activate(a, "pins", "test.engineer", "A takes new payments");
            Assert.Equal(a, await reg.Pin(pinned, Bank, "pins", "test"));
        }
        JsonObject Body(string? paymentId) => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Ack(paymentId ?? "", a)))!.AsObject();
        string? Env(Dictionary<string, string> e, string k) => e.GetValueOrDefault(k);

        var runtime = AttemptRouting.FromEnvironment(store.Options, k => Env(new() { ["TH_BANK"] = Bank }, k));
        Assert.Equal(a, await runtime.DistributionOf(Body(pinned)));
        foreach (var id in new[] { unpinned, null })
        {
            var e = await Assert.ThrowsAsync<NativeRefusal>(() => runtime.DistributionOf(Body(id)));
            Assert.Equal("NotPinned", e.Name);
        }
        foreach (var mode in new[] { "dev", "test" })
            Assert.Equal(a, await AttemptRouting.FromEnvironment(store.Options, k => Env(new() { ["TH_BANK"] = Bank, ["TH_NATIVE_MODE"] = mode }, k)).DistributionOf(Body(unpinned)));
        // A host that serves no runtime's bank (the studio's test runs) keeps the earlier rule (R-09-132).
        Assert.Equal(a, await AttemptRouting.FromEnvironment(store.Options, k => null).DistributionOf(Body(unpinned)));
        Assert.Throws<InvalidOperationException>(() => AttemptRouting.FromEnvironment(store.Options, k => Env(new() { ["TH_NATIVE_MODE"] = "lenient" }, k)));
    }
}
