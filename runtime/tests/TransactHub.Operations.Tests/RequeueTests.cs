using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;
using static TransactHub.Operations.Tests.ProjectionTests;

namespace TransactHub.Operations.Tests;

/// <summary>
/// An operator requeues a dead-lettered step from the operations console (R-09-91): offered only while a step's attempts are spent
/// (a parked step on any engine, or a dead-letter job Flowable's binding surfaced as the step's stuck fact), put on the engine host's
/// outbox once per action, audited as the operator's facts, and delivered to the engine once however often the host reads the row.
/// The engines take it as the same control: DTFx, Elsa and Temporal through the shared interpreter, Flowable through its binding
/// (conformance scenario CK-PARK-03).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RequeueTests(Databases dbs)
{
    static readonly ViewCatalog Views = Repo.Views();

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_dead_lettered_step_is_requeued_once_per_action_and_audited(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store);
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        var actions = new OperatorActions(store, ops, Views, new OutboxSignals(store));
        var op = Operator("op-1", WpsRun.Tenant);
        // Nothing is dead-lettered: requeue is not offered.
        Assert.DoesNotContain(await actions.Offers(id), o => o.Action == "requeue");
        Assert.Equal(409, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(op, id, "requeue", "the gateway is back"))).Status);
        await using (var db = StoreDbContext.Open(store))
            await new FactLog(db).Append(id, "bulk", new NewFact("step", "stuck", new JsonObject { ["instance"] = "root/leg", ["step"] = "submit" }, Actor: "engine"));
        Assert.Contains(await actions.Offers(id), o => o.Action == "requeue");
        Assert.Equal(400, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(op, id, "requeue", null))).Status);
        var r = await actions.Request(op, id, "requeue", "the gateway is back");
        Assert.Equal("done", (string?)r["status"]);
        var action = (string)r["action"]!;

        await using (var db = StoreDbContext.Open(store))
        {
            // On the outbox once, as the engine's control.
            var rows = await db.Outbox.AsNoTracking().Where(o => o.Topic == OutboxSignals.Topic && o.Key == id).ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal("requeue", (string?)JsonNode.Parse(row.PayloadJson)!["signal"]);
            Assert.Equal(action, (string?)JsonNode.Parse(row.PayloadJson)!["action"]);
            // Audited: who asked, why, and what came of it.
            var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == id && f.Kind == "operator").OrderBy(f => f.Seq).ToListAsync();
            Assert.Equal(["requested", "done"], facts.Select(f => f.Code));
            Assert.All(facts, f => Assert.Equal(action, (string?)JsonNode.Parse(f.DataJson)!["action"]));
            Assert.Equal("operator:op-1", facts[0].Actor);
            Assert.Equal("the gateway is back", (string?)JsonNode.Parse(facts[0].DataJson)!["remarks"]);

            // The engine host reads the row and delivers it once, however often it reads it (the interpreter dedups by its id).
            var acceptedRef = $"payload-{Guid.NewGuid():N}#ctx";
            await new FactLog(db).Append(acceptedRef, "payload", new NewFact("payload", "accepted", new JsonObject { ["bulk"] = id }, Actor: "claim-check"));
            var feed = (await new TransactHub.Engines.Durable.StoreSignals(store).Follow(acceptedRef, "dtfx", "root", "run-1"))!;
            var polled = await feed.Poll();
            var (name, data) = TransactHub.Engines.LiveSignals.EventOf(Assert.Single(polled), new JsonObject { ["conversation"] = "conv-1" });
            Assert.Equal(("signal", "requeue"), (name, (string?)data["signal"]));
            Assert.Contains("requeue", TransactHub.Engines.Interp.OperatorControls);
            await feed.Ack(polled[0], "delivered");
            await feed.Ack(polled[0], "delivered");
            Assert.Empty(await feed.Poll());
            Assert.Equal(1, await db.Facts.AsNoTracking().CountAsync(f => f.SubjectId == id && f.Kind == "signal" && f.Code == "delivered"));
        }
    }
}
