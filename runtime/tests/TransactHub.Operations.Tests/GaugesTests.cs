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
        // The awaited reply is the installation's setting (R-09-142): with none named, the gauge watches nothing.
        Assert.Empty(RuntimeGauges.Default);
        var g = new RuntimeGauges(store, ops, RuntimeGauges.FromSetting("""[{ "scheme": "uae.wps", "sent": "uae.wps.sif", "replies": ["uae.wps.ack"] }]"""), clock: new Clock(later));
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

    /// <summary>
    /// R-09-211, Issue I-235: the gauge reads the awaited replies from the schemes of the active distributions (a message's
    /// <c>repliesTo</c>, here uae.wps 1.4.0 of the repository: the ACK answers the salary file the bank submits), and the installation's
    /// setting only adds to them; a distribution not active adds nothing.
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_awaited_replies_are_the_active_distributions_schemes_and_the_setting_adds_to_them(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var scheme = await File.ReadAllBytesAsync(Path.Combine(Repo.Root(), "spec", "factory", "uae.wps", "scheme.yaml"));
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        await using (var s = z.CreateEntry("engine/p.wf.corporate.v1/uae.wps/scheme/1.4.0.yaml").Open())
            await s.WriteAsync(scheme);
        var bytes = ms.ToArray();
        var bank = $"gauge-{Guid.NewGuid():N}"[..16];
        await using (var db = StoreDbContext.Open(store))
        {
            var reg = new DistributionRegistry(db);
            await reg.Store(bank, "2026.11.24-1", bytes, new System.Text.Json.Nodes.JsonObject { ["bank"] = bank }, FileStore.Sha256Hex(bytes), "test", "test");
            await reg.Store(bank, "2026.11.24-2", bytes, new System.Text.Json.Nodes.JsonObject { ["bank"] = bank }, FileStore.Sha256Hex(bytes), "test", "test");
            await reg.Activate(DistributionRegistry.IdOf(bank, "2026.11.24-1"), "test", "test", "test");
        }
        var g = new RuntimeGauges(store, ops, RuntimeGauges.FromSetting("""[{ "scheme": "uae.wps", "sent": "uae.wps.sif-submission", "replies": ["uae.wps.nak-by-setting"] }, { "scheme": "x.other", "sent": "x.other.out", "replies": ["x.other.in"] }]"""));
        await g.Refresh();
        Assert.Equal(["uae.wps uae.wps.sif-submission: uae.wps.ack, uae.wps.nak-by-setting", "x.other x.other.out: x.other.in"],
            g.Watched.Select(r => $"{r.Scheme} {r.Sent}: {string.Join(", ", r.Replies)}"));
        var none = new RuntimeGauges(store, ops);
        await none.Refresh();
        Assert.Equal(["uae.wps uae.wps.sif-submission: uae.wps.ack"], none.Watched.Select(r => $"{r.Scheme} {r.Sent}: {string.Join(", ", r.Replies)}"));
    }
}
