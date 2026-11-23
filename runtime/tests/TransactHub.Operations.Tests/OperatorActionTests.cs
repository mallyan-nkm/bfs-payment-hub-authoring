using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Activities.Native;
using TransactHub.Store;
using static TransactHub.Operations.Tests.ProjectionTests;

namespace TransactHub.Operations.Tests;

/// <summary>
/// Operator actions (R-05-50): a hold waits for the operator's decision as the operations-queue port's answer; each request,
/// approval and result is a fact with the operator's identity; maker-checker where the view asks for it; a tenant's
/// operator acts on that tenant's payments only; a decision, a signal and a retry go to the engine's host on the outbox, which
/// it reads in order, once each, from where it stopped.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OperatorActionTests(Databases dbs)
{
    static readonly ViewCatalog Views = Repo.Views();

    static NativeAttempt Attempt(string fileId = WpsRun.FileId) => new("p.hold.approval.v1", "1.0.0",
        new JsonObject { ["header"] = new JsonObject { ["scheme"] = "uae.wps", ["direction"] = "outward", ["kind"] = "payment", ["fileId"] = fileId } },
        new JsonObject { ["expiry"] = "P1D" }, DateTimeOffset.Parse("2026-11-17T06:00:00Z"), WpsRun.Tenant);

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_hold_is_held_at_once_and_the_operators_decision_reaches_the_engine_host_in_order_once(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, hold: false);
        var hold = new StoreHold(store, "p.hold.approval.v1", "approval", "facts.approval", ["approved", "declined"]);
        // The attempt answers at once (R-05-56): no connection waits for the operator; the engine awaits the decision.
        var held = hold.Run(Attempt());
        Assert.Equal("held", held.Outcome);
        Assert.Equal("P1D", (string?)held.Held!["expiry"]);
        var holdId = (string)held.Held["hold"]!;
        Assert.Equal(holdId, (string?)hold.Run(Attempt()).Held!["hold"]); // asked again: the same hold, still held
        var projector = new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero };
        await projector.CatchUp();
        var actions = new OperatorActions(store, ops, Views, new OutboxSignals(store));
        Assert.Contains(await actions.Offers(id), o => o.Action == "approve");

        // The engine host follows the payment (R-05-57): it records the instance's engine ids, and reads the outbox in order.
        await using (var db = StoreDbContext.Open(store))
        {
            var acceptedRef = $"payload-{Guid.NewGuid():N}#ctx";
            await new FactLog(db).Append(acceptedRef, "payload", new NewFact("payload", "accepted", new JsonObject { ["bulk"] = id }, Actor: "claim-check"));
            var source = new TransactHub.Engines.Durable.StoreSignals(store);
            var feed = (await source.Follow(acceptedRef, "dtfx", "root", "run-1"))!;
            Assert.Empty(await feed.Poll());
            var r = await actions.Request(Operator("op-1", WpsRun.Tenant), id, "approve", "agreed with the employer");
            Assert.Equal("done", (string?)r["status"]);
            await actions.Request(Operator("op-1", WpsRun.Tenant), id, "re-screen", "the lists were updated");
            var rows = await feed.Poll();
            Assert.Equal(["decide", "re-screen"], rows.Select(x => (string?)x["signal"]));
            Assert.Equal("approved", (string?)rows[0]["decision"]);
            Assert.Equal(holdId, (string?)rows[0]["hold"]);
            Assert.Equal("operator:op-1", (string?)rows[0]["by"]);
            // On the engine: the decision is the message the hold awaits; the signal, the engine's signal event.
            var (name, data) = TransactHub.Engines.LiveSignals.EventOf(rows[0], new JsonObject { ["conversation"] = "conv-1" });
            Assert.Equal(("message", "p.ops.decision", "approved"), (name, (string?)data["type"], (string?)data["status"]));
            await feed.Ack(rows[0], "delivered");
            await feed.Ack(rows[0], "delivered"); // a row delivered twice is recorded once
            // A host that restarts follows again and reads only what is not delivered: resumable.
            var again = (await source.Follow(acceptedRef, "dtfx", "root", "run-2"))!;
            Assert.Equal(["re-screen"], (await again.Poll()).Select(x => (string?)x["signal"]));
            var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == id && (f.Kind == "signal" || f.Kind == "engine")).OrderBy(f => f.Seq).ToListAsync();
            Assert.Equal(["engine:started", "signal:delivered", "engine:started"], facts.Select(f => $"{f.Kind}:{f.Code}"));
            Assert.Equal("run-1", (string?)JsonNode.Parse(facts[0].DataJson)!["run"]);
            Assert.True((await new FactLog(db).Verify(id)).Ok);
        }
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_hold_whose_expiry_passed_is_expired_and_its_decision_goes_to_the_engine(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, hold: false);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var hold = new StoreHold(store, "p.hold.approval.v1", "approval", "facts.approval", ["approved", "declined"], clock);
        Assert.Equal("held", hold.Run(Attempt()).Outcome);
        var sweep = new HoldExpiry(store, new OutboxSignals(store, clock), clock);
        Assert.Equal(0, await sweep.Sweep());
        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await sweep.Sweep());
        Assert.Equal(0, await sweep.Sweep());
        await using var db = StoreDbContext.Open(store);
        Assert.Contains(await db.Facts.AsNoTracking().Where(f => f.SubjectId == id && f.Kind == "hold").Select(f => f.Code).ToListAsync(), c => c == "expired");
        var row = await db.Outbox.AsNoTracking().Where(o => o.Topic == OutboxSignals.Topic && o.Key == id).SingleAsync();
        Assert.Equal("expired", (string?)JsonNode.Parse(row.PayloadJson)!["decision"]);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task An_action_that_needs_a_second_approver_waits_for_another_supervisor(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store);
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        var actions = new OperatorActions(store, ops, Views, new OutboxSignals(store));
        var maker = Operator("op-1", WpsRun.Tenant, OperatorRoles.Supervisor);
        var r = await actions.Request(maker, id, "cancel", "the employer withdrew the file");
        Assert.Equal("pending-approval", (string?)r["status"]);
        var action = (string)r["action"]!;
        var own = await Assert.ThrowsAsync<ActionRefused>(() => actions.Decide(maker, id, action, true, null));
        Assert.Equal(403, own.Status);
        var notSupervisor = await Assert.ThrowsAsync<ActionRefused>(() => actions.Decide(Operator("op-2", WpsRun.Tenant), id, action, true, null));
        Assert.Equal(403, notSupervisor.Status);
        var done = await actions.Decide(Operator("sup-1", WpsRun.Tenant, OperatorRoles.Supervisor), id, action, true, "checked");
        Assert.Equal("done", (string?)done["status"]);
        await using var db = StoreDbContext.Open(store);
        var signal = await db.Outbox.AsNoTracking().Where(x => x.Topic == OutboxSignals.Topic && x.Key == id).SingleAsync();
        Assert.Equal("cancel", (string?)JsonNode.Parse(signal.PayloadJson)!["signal"]);
        var again = await Assert.ThrowsAsync<ActionRefused>(() => actions.Decide(Operator("sup-2", WpsRun.Tenant, OperatorRoles.Supervisor), id, action, true, null));
        Assert.Equal(409, again.Status);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task An_operator_acts_only_on_their_tenants_payments_and_only_as_the_view_allows(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store);
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        var actions = new OperatorActions(store, ops, Views, new OutboxSignals(store));
        Assert.Equal(403, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(Operator("op-x", "other-bank"), id, "approve", null))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(Operator("op-1", WpsRun.Tenant), id, "release", "x"))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(Operator("op-1", WpsRun.Tenant), id, "decline", null))).Status);
        // Retry and resend are declared on the WPS leg; the payment's state allows them only with a parked step and a kept file (R-05-58).
        Assert.Equal(409, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(Operator("op-1", WpsRun.Tenant), id, "retry", "the gateway was down"))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(Operator("op-1", WpsRun.Tenant), id, "resend", "lost"))).Status);
        await using (var db = StoreDbContext.Open(store))
            await new FactLog(db).Append(id, "bulk", new NewFact("step", "stuck", new JsonObject { ["instance"] = "root/leg", ["step"] = "submit" }, Actor: "engine"));
        var retry = await actions.Request(Operator("op-1", WpsRun.Tenant), id, "retry", "the gateway was down");
        Assert.Equal("done", (string?)retry["status"]);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_holds_decision_on_an_engine_is_its_steps_fact_and_an_action_names_the_workflows_it_applies_to(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, hold: false);
        var root = $"order-{Guid.NewGuid():N}"[..20];
        await using (var db = StoreDbContext.Open(store))
            await new FactLog(db).Append($"{root}#ctx", "payload", new NewFact("payload", "accepted", new JsonObject { ["bulk"] = id }, Actor: "claim-check"));
        // A refund order's steps name their platform workflow (R-05-60); its approval hold is decided on the engine (R-05-59).
        var steps = new TransactHub.Engines.Durable.StepFacts(store);
        var before = TransactHub.Engines.Interp.Decided;
        TransactHub.Engines.Durable.StoreSignals.Install(store, steps);
        try
        {
            var header = new JsonObject { ["scheme"] = "uae.wps", ["direction"] = "inward", ["kind"] = "refund", ["originalPaymentId"] = "000000000000000000000001" };
            await steps.Record(new JsonObject { ["instance"] = root, ["workflow"] = "p.wf.refund.v1", ["step"] = "original", ["activity"] = "p.enrich.payment-record.v1", ["release"] = "1.0.0", ["call"] = 1, ["attempt"] = 1, ["ctx"] = new JsonObject { ["header"] = header.DeepClone() } },
                new JsonObject { ["kind"] = "result", ["outcome"] = "found" });
            var decision = new JsonObject { ["outcome"] = "approved", ["result"] = "approved", ["status"] = "approved", ["by"] = "operator:op-1", ["at"] = "2026-11-17T06:05:00.000Z" };
            var req = new JsonObject { ["instance"] = root, ["workflow"] = "p.wf.refund.v1", ["step"] = "approval", ["activity"] = "p.hold.approval.v1", ["release"] = "1.0.0", ["call"] = 1, ["attempt"] = 0, ["ctx"] = new JsonObject { ["header"] = header.DeepClone() } };
            var res = new JsonObject { ["kind"] = "result", ["outcome"] = "approved", ["writes"] = new JsonObject { ["facts.approval"] = decision } };
            TransactHub.Engines.Interp.Decided!(req, res);
            TransactHub.Engines.Interp.Decided!((JsonObject)req.DeepClone(), (JsonObject)res.DeepClone()); // a replay takes it again: one fact
        }
        finally { TransactHub.Engines.Interp.Decided = before; }
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using (var o = OpsDbContext.Open(ops))
        {
            var f = JsonNode.Parse((await o.Subjects.AsNoTracking().SingleAsync(x => x.Id == id)).FieldsJson)!;
            Assert.Equal("approved", (string?)f["approval"]);
            Assert.Equal("operator:op-1", (string?)f["approvalBy"]);
            Assert.Equal("2026-11-17T06:05:00.000Z", (string?)f["approvalAt"]);
        }
        await using (var db = StoreDbContext.Open(store))
            Assert.Single(await db.Facts.AsNoTracking().Where(x => x.SubjectId == id && x.Kind == "step" && x.Code == "approved").ToListAsync());
        // re-screen applies to the workflows that screen: not to a refund order, whose root steps name p.wf.refund.v1.
        var actions = new OperatorActions(store, ops, Views, new OutboxSignals(store));
        var offered = (await actions.Offers(id)).Select(x => x.Action).ToList();
        Assert.DoesNotContain("re-screen", offered);
        Assert.Contains("cancel", offered);
        Assert.Equal(409, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(Operator("op-1", WpsRun.Tenant), id, "re-screen", "lists updated"))).Status);
        // A payment of p.wf.corporate.v1 is offered it.
        var other = await WpsRun.Seed(store, fileId: "0000001234567261117083001", hold: false);
        await using (var db = StoreDbContext.Open(store))
            await new FactLog(db).Append(other, "bulk", new NewFact("step", "pass", new JsonObject { ["instance"] = "corp-1", ["workflow"] = "p.wf.corporate.v1", ["step"] = "validate" }, Actor: "engine"));
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        Assert.Contains(await actions.Offers(other), x => x.Action == "re-screen");
    }
}

/// <summary>A clock a test moves.</summary>
sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}
