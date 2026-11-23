using TransactHub.Ports;
using Xunit;

namespace TransactHub.Store.Tests;

// R-09-112: a host reads the bank's bindings live from the configuration releases in force, as of each attempt's time. A binding
// that did not change keeps its instance, so an adapter built for it is built once; a new configuration release's binding is
// seen at the next read after the refresh, with no restart; only the latest release that binds a port counts.
public sealed class PortBindingsLiveTests
{
    static PortBinding B(string adapter, string from, string release, string url = "https://core.bank.example.test") =>
        new("example-bank", "p.port.core-banking.v1", adapter, DateTimeOffset.Parse(from), new Dictionary<string, string> { ["baseUrl"] = url }, release);

    [Fact]
    public void A_new_configuration_releases_binding_is_read_at_the_next_refresh_and_an_unchanged_one_keeps_its_instance()
    {
        var now = DateTimeOffset.Parse("2026-11-23T10:00:00Z");
        var rows = new List<PortBinding> { B("simulator", "2026-11-01T00:00:00Z", "1.0.0"), B("gateway-rest", "2027-02-01T00:00:00Z", "1.0.0") };
        var reads = 0;
        var live = PortBindings.Live(() => { reads++; return rows.Select(r => r with { Settings = new Dictionary<string, string>(r.Settings) }).ToList(); }, TimeSpan.FromSeconds(2), () => now);
        Assert.True(live.IsLive);
        var first = live.InForce("example-bank", "p.port.core-banking.v1", DateTimeOffset.Parse("2027-01-15T00:00:00Z"));
        Assert.Equal("simulator", first.Adapter);
        Assert.Equal("gateway-rest", live.InForce("example-bank", "p.port.core-banking.v1", DateTimeOffset.Parse("2027-02-02T00:00:00Z")).Adapter);
        Assert.Equal(1, reads);

        // A later release binds core banking anew: not seen until the refresh, then seen; the release's whole set counts.
        rows.Add(B("gateway-rest", "2026-11-01T00:00:00Z", "1.1.0", "https://core2.bank.example.test"));
        Assert.Equal("simulator", live.InForce("example-bank", "p.port.core-banking.v1", DateTimeOffset.Parse("2027-01-15T00:00:00Z")).Adapter);
        now = now.AddSeconds(3);
        var after = live.InForce("example-bank", "p.port.core-banking.v1", DateTimeOffset.Parse("2027-01-15T00:00:00Z"));
        Assert.Equal(("gateway-rest", "1.1.0", "https://core2.bank.example.test"), (after.Adapter, after.Release, after.Settings["baseUrl"]));
        Assert.Equal(2, reads);
        // The earlier release's bindings are kept as they were, by instance: an adapter built for one is not built again.
        Assert.Same(first, live.All.Single(b => b.Release == "1.0.0" && b.Adapter == "simulator"));
        Assert.Throws<InvalidOperationException>(() => live.Add("kind: port-bindings\ntenant: example-bank\nbindings: []\n"));
        Assert.Throws<PortBindingException>(() => live.InForce("other-bank", "p.port.core-banking.v1", now));
    }

    [Fact]
    public void A_store_that_cannot_be_read_is_a_binding_refusal_not_a_crash()
    {
        var live = PortBindings.Live(() => throw new InvalidOperationException("the store is not there"));
        Assert.Contains("could not be read", Assert.Throws<PortBindingException>(() => live.InForce("example-bank", "p.port.core-banking.v1", DateTimeOffset.UtcNow)).Message);
    }

    [Fact]
    public void Releases_order_by_their_numbers_and_their_build_metadata()
    {
        Assert.True(PortBindings.CompareReleases("1.10.0", "1.9.0") > 0);
        Assert.True(PortBindings.CompareReleases("50.0.0+bindings.1.2.0", "50.0.0") > 0);
        Assert.True(PortBindings.CompareReleases("50.0.0+bindings.1.2.0", "50.0.0+bindings.1.10.0") < 0);
        Assert.Equal(0, PortBindings.CompareReleases(null, null));
    }
}
