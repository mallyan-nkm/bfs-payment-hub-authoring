using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactHub.Store;

namespace TransactHub.Native.Tests;

/// <summary>
/// Spec R-09-130 to R-09-132 (Issue I-222): an attempt names its payment's distribution, as the engine sent it, and the native host
/// serves it from that distribution's build, whatever worker carried it; it refuses an attempt whose worker runs another
/// distribution (<c>native.WrongDistribution</c>) and one for a distribution the payment is not pinned to (<c>native.NotPinned</c>). An attempt
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
        Assert.Equal("native.WrongDistribution", (string?)wrong["name"]);
        Assert.False((bool)wrong["retryable"]!);
        Assert.Contains(a, (string?)wrong["message"]);

        // B's payment is not served from A, though A is installed and its worker names A (R-09-132).
        foreach (var asked in new[] { Ack(pinnedToB, a), Ack(pinnedToB, a, a) })
        {
            var notPinned = await Post(c, asked);
            Assert.Equal("native.NotPinned", (string?)notPinned["name"]);
            Assert.False((bool)notPinned["retryable"]!);
            Assert.Contains(b, (string?)notPinned["message"]);
        }

        // An attempt that names none is served as before: from its worker's distribution, then from the development modules.
        Assert.Equal("wps-build-b", Build(await Post(c, Ack(pinnedToB, null, b))));
        Assert.Equal("native.NotNative", (string?)(await Post(c, Ack(pinnedToB, null)))["name"]);

        // A payment with no pin in the store (the studio's test runs) is served from the distribution the attempt names (Issue I-230).
        Assert.Equal("wps-build-a", Build(await Post(c, Ack(unpinned, a))));

        var modules = (await c.GetFromJsonAsync<JsonObject>("/modules"))!;
        var loaded = (modules["loaded"] as JsonArray)!.OfType<JsonObject>().ToDictionary(x => (string)x["distribution"]!);
        Assert.Equal(3, (int)loaded[a]["served"]![ModuleBuilds.AckIn]!);
        Assert.Equal(3, (int)loaded[b]["served"]![ModuleBuilds.AckIn]!);
    }
}
