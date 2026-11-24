using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;
using static TransactHub.Operations.Tests.ProjectionTests;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The platform operations view 1.8.0 (owner's decisions of 24 November 2026): pause and resume offered by the payment's state
/// (R-05-106), and a work item's escalation after a time, a dial of the view, off by default, to the role it names (R-05-107,
/// Issue I-185). The view as the factory holds it; synthetic payments only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PauseAndEscalationTests(Databases dbs)
{
    static readonly ViewCatalog Views = ViewCatalog.FromFolder(Path.Combine(Repo.Root(), "spec", "factory"));
    const string View = "p.view.operations.v1";

    [Fact]
    public void The_platform_view_offers_pause_and_resume_and_declares_escalation_off_by_default()
    {
        var v = Views.All.Single(x => x.Name == View);
        Assert.Equal("1.8.0", (string?)v.Doc["release"]);
        var acts = (v.Subject!["actions"] as JsonArray)!.OfType<JsonObject>().ToList();
        foreach (var a in new[] { "pause", "resume" })
        {
            var d = acts.Single(x => (string?)x["action"] == a);
            Assert.Equal(("Operator", "required", (string?)null), ((string?)d["role"], (string?)d["remarks"], (string?)d["secondApprover"]));
        }
        Assert.Equal("P0D", (string?)v.Doc["parameters"]!["escalateAfter"]!["default"]);
        Assert.Equal("Supervisor", (string?)v.Doc["parameters"]!["escalateTo"]!["default"]);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task An_operator_pauses_a_running_payment_with_remarks_and_resumes_it_each_offered_by_its_state(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, hold: false);
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        var actions = new OperatorActions(store, ops, Views, new OutboxSignals(store));
        var op = Operator("op-1", WpsRun.Tenant);
        async Task<List<string>> Offered() => (await actions.Offers(id)).Select(o => o.Action).ToList();

        Assert.Contains("pause", await Offered());
        Assert.DoesNotContain("resume", await Offered());
        Assert.Equal(400, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(op, id, "pause", null))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(op, id, "resume", "nothing to resume"))).Status);
        Assert.Equal("done", (string?)(await actions.Request(op, id, "pause", "the employer asked to hold the file until noon"))["status"]);
        Assert.Contains("resume", await Offered());
        Assert.DoesNotContain("pause", await Offered());
        Assert.Equal(409, (await Assert.ThrowsAsync<ActionRefused>(() => actions.Request(op, id, "pause", "again"))).Status);
        Assert.Equal("done", (string?)(await actions.Request(op, id, "resume", "the employer confirmed"))["status"]);
        Assert.Contains("pause", await Offered());
        // Each reaches the payment's engine host on the outbox, in order; the operator's identity is on the payment's chain.
        await using var db = StoreDbContext.Open(store);
        var signals = await db.Outbox.AsNoTracking().Where(o => o.Topic == OutboxSignals.Topic && o.Key == id).OrderBy(o => o.Id).Select(o => o.PayloadJson).ToListAsync();
        Assert.Equal(["pause", "resume"], signals.Select(s => (string?)JsonNode.Parse(s)!["signal"]));
        Assert.All(await db.Facts.AsNoTracking().Where(f => f.SubjectId == id && f.Kind == "operator").ToListAsync(), f => Assert.Equal("operator:op-1", f.Actor));
        // An ended payment takes neither.
        await new FactLog(db).Append(id, "bulk", new NewFact("payment.end", "settled", new JsonObject(), Actor: "engine"));
        Assert.DoesNotContain(await Offered(), a => a is "pause" or "resume");
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_work_item_escalates_by_itself_once_after_the_banks_escalateAfter_to_the_role_it_names(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store);
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var queue = new WorkQueue(store, ops, clock);
        var dials = new OperationsDials(store, Views);
        var escalation = new WorkEscalation(store, queue, dials, clock);
        var item = new WorkItem(WorkItem.Payment, id);
        Assert.Contains((await queue.Waiting()).Select(w => w.Item), x => x == item);

        // Off by default: a day on, nothing escalates.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await escalation.Sweep());
        Assert.False((await queue.State(item)).Escalated);

        // The bank sets an hour, to its bank approvers; the item has waited a day, so it escalates once, by itself.
        await OversightHost.Value(store, WpsRun.Tenant, $"{View}#escalateAfter", JsonValue.Create("PT1H")!, clock.GetUtcNow().AddDays(-30));
        await OversightHost.Value(store, WpsRun.Tenant, $"{View}#escalateTo", JsonValue.Create("BankApprover")!, clock.GetUtcNow().AddDays(-30));
        Assert.Equal(1, await escalation.Sweep());
        Assert.Equal(0, await escalation.Sweep());
        var st = await queue.State(item);
        Assert.Equal((true, "BankApprover", true, 1), (st.Escalated, st.EscalatedTo, st.Automatic, st.Level));
        var fact = (await queue.History(item)).Last()!;
        Assert.Equal(("escalated", "runtime:operations", "PT1H"), ((string?)fact["code"], (string?)fact["actor"], (string?)fact["data"]!["after"]));
        // Only a person with the role it went to takes it: a supervisor alone does not.
        Assert.Equal(403, (await Assert.ThrowsAsync<ActionRefused>(() => queue.Change(Operator("sup-1", WpsRun.Tenant, OperatorRoles.Operator, OperatorRoles.Supervisor), item, "assign", null, null, null))).Status);
        var taken = await queue.Change(Operator("ba-1", WpsRun.Tenant, OperatorRoles.Operator, OperatorRoles.BankApprover), item, "assign", null, null, null);
        Assert.Equal("ba-1", (string?)taken["assignee"]);
        // Taken, it is not escalated: it does not escalate by itself a second time.
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(0, await escalation.Sweep());
        var queued = await queue.Queue(Operator("ba-1", WpsRun.Tenant, OperatorRoles.Operator, OperatorRoles.BankApprover), [WpsRun.Tenant], "all");
        Assert.Equal("ba-1", (string?)queued["rows"]!.AsArray().Single(r => (string?)r!["item"] == id)!["assignee"]);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task An_item_escalated_by_hand_still_goes_to_the_supervisors_and_is_not_escalated_again_by_time(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store);
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var queue = new WorkQueue(store, ops, clock);
        var escalation = new WorkEscalation(store, queue, new OperationsDials(store, Views), clock);
        var item = new WorkItem(WorkItem.Payment, id);
        await queue.Change(Operator("op-1", WpsRun.Tenant), item, "escalate", null, null, "the employer disputes the hold");
        await OversightHost.Value(store, WpsRun.Tenant, $"{View}#escalateAfter", JsonValue.Create("PT1H")!, clock.GetUtcNow().AddDays(-30));
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, await escalation.Sweep());
        var st = await queue.State(item);
        Assert.Equal(("Supervisor", false, 1), (st.EscalatedTo, st.Automatic, st.Level));
        Assert.Equal("sup-1", (string?)(await queue.Change(Operator("sup-1", WpsRun.Tenant, OperatorRoles.Operator, OperatorRoles.Supervisor), item, "assign", null, null, null))["assignee"]);
    }
}
