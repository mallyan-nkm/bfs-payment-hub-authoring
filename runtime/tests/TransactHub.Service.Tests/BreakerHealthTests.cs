using TransactHub.Operations;
using TransactHub.Ports;
using Xunit;

namespace TransactHub.Service.Tests;

/// <summary>
/// An open circuit breaker of a production adapter shows in the service console's health (R-09-156): each host records its
/// breakers' moves in the store, and the probe <c>circuit-breakers</c> reads the latest of each: open fails, half-open warns,
/// closed is ok. Hosts and ports only, never a credential or a payment. Synthetic hosts.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BreakerHealthTests(Databases dbs)
{
    [Fact]
    public async Task An_open_breaker_fails_the_health_a_half_open_one_warns_and_a_closed_one_is_ok_for_the_runtimes_bank_only()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub());
        var c = api.Engineer;
        async Task<System.Text.Json.Nodes.JsonNode> Breakers()
        {
            var h = await J.Of(await c.GetAsync("/service/api/health?refresh=1"));
            return h["components"]!.AsArray().Single(x => (string?)x!["name"] == "circuit-breakers")!;
        }
        var none = await Breakers();
        Assert.Equal("ok", (string?)none["status"]);
        Assert.Equal("ports", (string?)none["group"]);

        // A core-banking gateway that failed five times in a row, seen by the native host; another bank's breaker is not this runtime's.
        var journal = new StoreBreakerJournal(api.Store, "test-host/native");
        var key = new BreakerKey(Seed.Tenant, "p.port.core-banking.v1", "gateway-rest", "core.example.invalid:443");
        var at = DateTimeOffset.UtcNow;
        journal.Record(new BreakerChange(key, BreakerState.Open, 5, at, "HttpRequestException"));
        journal.Record(new BreakerChange(key with { Tenant = "other-bank", Target = "core.other.invalid:443" }, BreakerState.Open, 5, at, "answer"));
        await journal.Written;
        var open = await Breakers();
        Assert.Equal("fail", (string?)open["status"]);
        Assert.Equal("service.ProbeFailed", (string?)open["code"]);
        Assert.Contains("p.port.core-banking.v1 gateway-rest core.example.invalid:443", (string?)open["detail"]);
        Assert.Contains("open since", (string?)open["detail"]);
        Assert.Contains("test-host/native", (string?)open["detail"]);
        Assert.DoesNotContain("core.other.invalid", (string?)open["detail"]);

        journal.Record(new BreakerChange(key, BreakerState.HalfOpen, 5, at.AddSeconds(30), "open period over: one trial call"));
        await journal.Written;
        Assert.Equal("warn", (string?)(await Breakers())["status"]);
        journal.Record(new BreakerChange(key, BreakerState.Closed, 5, at.AddSeconds(31), "a call succeeded"));
        await journal.Written;
        var closed = await Breakers();
        Assert.Equal("ok", (string?)closed["status"]);
        Assert.Equal("1 breaker(s) known, none open", (string?)closed["detail"]);
    }
}
