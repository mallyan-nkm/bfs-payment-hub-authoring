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

    /// <summary>
    /// R-09-240 (Issue I-304): a step's rule trace is kept with its step fact on the payment's chain, so it is not lost when the engine's
    /// service starts again: the rules that fired, the findings' codes and the worst, never the facts the rules were given (personal
    /// data) or their bindings. The observation and the console's timeline read it from there.
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_steps_rule_trace_is_kept_on_the_payments_chain_without_the_facts_it_was_given(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, hold: false);
        var root = $"root-{Guid.NewGuid():N}"[..20];
        await using (var db = StoreDbContext.Open(store))
            await new FactLog(db).Append($"{root}#ctx", "payload", new NewFact("payload", "accepted", new JsonObject { ["bulk"] = id }, Actor: "claim-check"));
        var trace = JsonNode.Parse("""
            { "worst": "reject", "facts": { "Payment": [ { "creditorAccount": { "iban": "AE070331234567890123456" } } ] }, "parameters": { "maxAmount": 1000 },
              "findings": [ { "rule": "p.rule.batch-currency-present.v1", "severity": "reject", "path": "batch.currency", "reasonCode": "AM03", "message": "synthetic" } ],
              "decisions": [],
              "trace": [ { "rule": "p.rule.batch-currency-present.v1", "release": "1.0.0", "stage": "cross-field", "fired": true, "activations": 1, "bindings": [ { "b": { "currency": "" } } ] },
                         { "rule": "p.rule.amount-positive.v1", "release": "1.0.0", "stage": "field", "fired": false, "activations": 0 } ] }
            """)!;
        var steps = new TransactHub.Engines.Durable.StepFacts(store);
        await steps.Record(new JsonObject { ["instance"] = root, ["step"] = "validate", ["activity"] = "p.validate.canonical.v2", ["release"] = "2.0.0", ["call"] = 1, ["attempt"] = 1, ["ctx"] = new JsonObject { ["header"] = new JsonObject { ["scheme"] = "uae.wps" } } },
            new JsonObject { ["kind"] = "result", ["outcome"] = "reject", ["reasonCode"] = "AM03", ["ruleTrace"] = trace });
        JsonArray traces = new();
        for (var i = 0; i < 100 && traces.Count == 0; i++) { traces = await TransactHub.Engines.Durable.StepFacts.RuleTraces(store, id); if (traces.Count == 0) await Task.Delay(100); }
        var kept = Assert.Single(traces)!;
        Assert.Equal(("validate", "p.validate.canonical.v2"), ((string?)kept["step"], (string?)kept["activity"]));
        Assert.Equal("reject", (string?)kept["trace"]!["worst"]);
        Assert.Equal(["p.rule.batch-currency-present.v1"], kept["trace"]!["rules"]!.AsArray().Where(r => (bool)r!["fired"]!).Select(r => (string)r!["rule"]!));
        Assert.Equal("AM03", (string?)kept["trace"]!["findings"]![0]!["reasonCode"]);
        var text = kept.ToJsonString();
        Assert.DoesNotContain("AE07", text);
        Assert.DoesNotContain("bindings", text);
        Assert.DoesNotContain("maxAmount", text);
        // The console's timeline has it with the step.
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var o = OpsDbContext.Open(ops);
        var entry = await o.Timeline.AsNoTracking().SingleAsync(x => x.SubjectId == id && x.Kind == "step" && x.Step == "validate" && x.Activity == "p.validate.canonical.v2");
        Assert.Equal("reject", (string?)JsonNode.Parse(entry.DataJson)!["ruleTrace"]!["worst"]);
    }

    /// <summary>
    /// R-09-241 (Issue I-290): after parallel branches that both hold (sanctions pending, fraud suspect), each hold names the step whose
    /// handler it is in, as the engine says it, not the latest step with a reason, which is the other branch's for one of them. An
    /// attempt that names no handler keeps the latest step with a reason (R-05-94).
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Holds_after_parallel_branches_each_name_the_step_whose_handler_they_are_in(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, suspect: true);
        // The sanctions branch answered pending after the fraud branch's suspect (synthetic): the latest step with a reason is sanctions.
        await using (var db = StoreDbContext.Open(store))
            await new FactLog(db).Append(id, "bulk", new NewFact("step", "pending", new JsonObject
            {
                ["instance"] = "root", ["step"] = "sanctions", ["activity"] = "p.screen.sanctions.v1", ["outcome"] = "pending", ["reasonCode"] = "SANC",
            }, Actor: "engine"));
        NativeResult HoldIn(string activity, string step, string handledStep, string handledActivity, string outcome)
        {
            AttemptEnvelope.Current = new JsonObject { ["step"] = step, ["handlerOf"] = new JsonObject { ["step"] = handledStep, ["activity"] = handledActivity, ["outcome"] = outcome } };
            try { return new StoreHold(store, activity, activity.Contains("repair") ? "repair" : "approval", "facts.approval", ["approved", "declined"]).Run(Attempt()); }
            finally { AttemptEnvelope.Current = null; }
        }
        // The fraud review's hold (a hold of its own activity line, so it is not the seeded one).
        Assert.Equal("held", HoldIn("p.hold.repair.v1", "fraud-review", "fraud", "p.screen.fraud.v1", "suspect").Outcome);
        await using (var db = StoreDbContext.Open(store))
        {
            var opened = await db.Facts.AsNoTracking().Where(f => f.SubjectId == id && f.Kind == "hold" && f.Code == "opened").OrderBy(f => f.Seq).ToListAsync();
            var last = JsonNode.Parse(opened[^1].DataJson)!;
            Assert.Equal(("p.screen.fraud.v1", "fraud", "fraud-review"), ((string?)last["after"], (string?)last["afterStep"], (string?)last["step"]));
            Assert.Equal("FRAD at fraud (p.screen.fraud.v1)", (string?)last["reason"]);
            // The latest step with a reason is the sanctions branch's: an attempt that names no handler would have named it.
            Assert.Equal("p.screen.sanctions.v1", (await HoldCause.Before(db, id, int.MaxValue))!.Activity);
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
        // A refund order's steps name their platform workflow (R-05-61); its approval hold is decided on the engine (R-05-60).
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
        // The decision is written off the workflow's thread: wait for it.
        for (var i = 0; i < 100; i++)
        {
            await using var db = StoreDbContext.Open(store);
            if (await db.Facts.AsNoTracking().AnyAsync(x => x.SubjectId == id && x.Kind == "step" && x.Code == "approved")) break;
            await Task.Delay(100);
        }
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
    public void Set(DateTimeOffset at) => now = at;
}
