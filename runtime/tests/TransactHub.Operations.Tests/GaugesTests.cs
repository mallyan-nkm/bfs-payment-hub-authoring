using TransactHub.Model;
using TransactHub.Operations;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The gauges the alert rules read (R-09-72), from the store and the read model on both databases: the oldest file still
/// waiting for its scheme's acknowledgement, and the oldest hold still waiting for its decision, by tenant; ids only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class GaugesTests(Databases dbs)
{
    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Read_the_oldest_wait_for_an_acknowledgement_and_the_oldest_open_hold(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var bulk = await WpsRun.Seed(store);
        // The salary file goes out; no acknowledgement comes back.
        await using (var db = StoreDbContext.Open(store))
            await new PaymentStore(db).KeepMessage(WpsRun.Tenant, "uae.wps", Direction.Outward, "uae.wps.sif", "OUT.SIF", "SCR,synthetic\n"u8.ToArray(), bulk);
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        var later = DateTimeOffset.UtcNow.AddHours(25);
        var g = new RuntimeGauges(store, ops, clock: new Clock(later));
        await g.Refresh();
        var (_, awaitAge, holdAge) = g.Current;
        var wait = Assert.Single(awaitAge);
        Assert.True(wait.Value > 24 * 3600, $"waited {wait.Value} s");
        Assert.Contains(wait.Tags.ToArray(), t => t.Key == "th.tenant" && (string?)t.Value == WpsRun.Tenant);
        Assert.Contains(wait.Tags.ToArray(), t => t.Key == "th.message" && (string?)t.Value == "uae.wps.sif");
        var hold = Assert.Single(holdAge);
        Assert.True(hold.Value > 24 * 3600);
        Assert.Contains(hold.Tags.ToArray(), t => t.Key == "th.hold.kind" && (string?)t.Value == "approval");
        // Once the acknowledgement comes in, nothing waits.
        await using (var db = StoreDbContext.Open(store))
            await new PaymentStore(db).KeepMessage(WpsRun.Tenant, "uae.wps", Direction.Inward, "uae.wps.ack", "OUT.ACK", "ACK,synthetic\n"u8.ToArray(), bulk);
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await g.Refresh();
        Assert.Equal(0, Assert.Single(g.Current.awaitAge).Value);
    }
}
