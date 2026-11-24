using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Engines.Durable;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// R-09-239 (Issue I-301): a distribution's engine service checkpoints its history every so many facts. The checkpoint holds, in their
/// order, the facts of the payments that have not ended, and a service that starts again reads the chain from its last checkpoint:
/// what it folds is what the open payments hold, not what the distribution ever ran. The chain stays insert-only and verifies whole.
/// Synthetic facts in the engines' shapes (a DTFx episode and message, an Elsa instance), no payment data.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StoreHistoryTests(Databases dbs)
{
    static JsonObject Start(string root) => new() { ["paymentId"] = $"P-{root}", ["rootId"] = root, ["workflow"] = "p.wf.corporate.v1", ["input"] = new JsonObject { ["kind"] = "workflow" } };

    [Theory]
    [InlineData(StoreProvider.SqlServer)]
    [InlineData(StoreProvider.PostgreSql)]
    public async Task A_service_that_starts_again_reads_its_history_from_the_last_checkpoint_which_holds_only_its_open_payments(StoreProvider p)
    {
        var store = dbs.Fresh(p);
        await using (var db = StoreDbContext.Open(store)) await db.Database.MigrateAsync();
        var h = new StoreHistory(store, "dtfx", "dist-checkpoint") { CheckpointEvery = 8 };
        Assert.Empty(h.Load());
        h.Append("opened", new JsonObject { ["engine"] = "dtfx" });
        // Payment A runs and ends; its end is recorded by the host.
        h.Append("start", Start("root-a"));
        h.Append("message", new JsonObject { ["instance"] = "root-a", ["msg"] = "start-a" });
        h.Append("episode", new JsonObject { ["instance"] = "root-a", ["activities"] = new JsonArray(new JsonObject { ["seq"] = 1, ["msg"] = "act-a" }) });
        h.Append("message", new JsonObject { ["instance"] = "root-a/leg1", ["msg"] = "start-a-leg" });
        // Payment B starts and waits; one of its activities answered with no message for an instance (dropped) stays named.
        h.Append("start", Start("root-b"));
        h.Append("episode", new JsonObject { ["instance"] = "root-b", ["activities"] = new JsonArray(new JsonObject { ["seq"] = 2, ["msg"] = "act-b" }) });
        h.Append("activity-done", new JsonObject { ["activity"] = "1" });
        Assert.Equal(1, h.Checkpoints); // the eighth fact
        h.Append("reported", new JsonObject { ["rootId"] = "root-a", ["outcome"] = "settled" });
        h.Append("activity-done", new JsonObject { ["activity"] = "2" });

        // A service that starts again: its last checkpoint, then what came after; A's facts are still in it until the next one.
        var again = new StoreHistory(store, "dtfx", "dist-checkpoint") { CheckpointEvery = 8 };
        var facts = again.Load();
        Assert.True(again.Loaded.From > 0);
        Assert.Equal(3, again.Loaded.Rows); // the checkpoint and the two facts after it
        Assert.DoesNotContain(facts, f => f.code == "opened");
        Assert.Equal(["start", "message", "episode", "message", "start", "episode", "activity-done", "reported", "activity-done"], facts.Select(f => f.code));

        // The next checkpoint leaves A out entirely: its start, its instances, its messages, its episode and its activity's answer.
        for (var i = 0; i < 6; i++) again.Append("message", new JsonObject { ["instance"] = "root-b", ["msg"] = $"b-{i}" });
        Assert.Equal(1, again.Checkpoints);
        var third = new StoreHistory(store, "dtfx", "dist-checkpoint");
        var left = third.Load();
        Assert.Equal(1, third.Loaded.Rows);
        Assert.All(left, f => Assert.DoesNotContain("root-a", f.data.ToJsonString()));
        Assert.Equal(["start", "episode", "activity-done", "message", "message", "message", "message", "message", "message"], left.Select(f => f.code));
        Assert.Equal("root-b", (string?)left[0].data["rootId"]);
        // The chain is insert-only and verifies whole, checkpoints and all.
        var v = third.Verify();
        Assert.True((bool)v["chain"]!);
        Assert.Equal(18, (int)v["facts"]!);
    }

    [Fact]
    public void Compacting_keeps_the_open_payments_facts_in_their_order_on_either_engine()
    {
        var facts = new List<(string, JsonObject)>
        {
            ("restarted", new JsonObject()),
            ("start", Start("root-x")),
            ("instance", new JsonObject { ["id"] = "root-x", ["execution"] = 1 }),           // Elsa
            ("dispatch", new JsonObject { ["id"] = "root-x/leg1", ["ops"] = new JsonArray() }),
            ("start", Start("root-y")),
            ("instance", new JsonObject { ["id"] = "root-y", ["execution"] = 1 }),
            ("deliver", new JsonObject { ["id"] = "root-y", ["entries"] = new JsonArray(), ["from"] = "root-x#1#3" }),
            ("reported", new JsonObject { ["rootId"] = "root-x" }),
        };
        var kept = StoreHistory.Compact(facts);
        Assert.Equal(["start", "instance", "deliver"], kept.Select(f => f.code));
        Assert.All(kept, f => Assert.StartsWith("root-y", (string?)f.data["rootId"] ?? (string?)f.data["id"]));
    }
}
