using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Activities.Native;
using TransactHub.Store;
using static TransactHub.Operations.Tests.ProjectionTests;

namespace TransactHub.Operations.Tests;

/// <summary>
/// Operator actions (R-05-50): a hold waits for the operator's decision as the operations-queue port's answer; each request,
/// approval and result is a fact with the operator's identity; maker-checker where the view asks for it; a tenant's
/// operator acts on that tenant's payments only; an action with no engine path yet is recorded and refused.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OperatorActionTests(Databases dbs)
{
    static readonly ViewCatalog Views = Repo.Views();

    static NativeAttempt Attempt(string fileId = WpsRun.FileId) => new("p.hold.approval.v1", "1.0.0",
        new JsonObject { ["header"] = new JsonObject { ["scheme"] = "uae.wps", ["direction"] = "outward", ["kind"] = "payment", ["fileId"] = fileId } },
        new JsonObject { ["expiry"] = "P1D" }, DateTimeOffset.Parse("2026-11-17T06:00:00Z"), WpsRun.Tenant);

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_hold_waits_for_the_operator_and_takes_their_decision(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, hold: false);
        var hold = new StoreHold(store, "p.hold.approval.v1", "approval", "facts.approval", ["approved", "declined"]) { MaxWait = TimeSpan.FromSeconds(60), PollEvery = TimeSpan.FromMilliseconds(50) };
        var waiting = Task.Run(() => hold.Run(Attempt()));
        var projector = new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero };
        var actions = new OperatorActions(store, ops, Views, new OutboxSignals(store));
        // The hold opens on the payment's chain and shows in the read model.
        for (var i = 0; i < 200; i++)
        {
            await projector.CatchUp();
            try { if ((await actions.Offers(id)).Any(o => o.Action == "approve")) break; } catch (ActionRefused) { }
            await Task.Delay(50);
        }
        Assert.False(waiting.IsCompleted);
        var offers = await actions.Offers(id);
        Assert.Contains(offers, o => o.Action == "approve");
        Assert.DoesNotContain(offers, o => o.Action == "release"); // no repair hold is open
        var r = await actions.Request(Operator("op-1", WpsRun.Tenant), id, "approve", "agreed with the employer");
        Assert.Equal("done", (string?)r["status"]);
        var result = await waiting;
        Assert.Equal("approved", result.Outcome);
        Assert.Equal("operator:op-1", (string?)result.Writes["facts.approval"]!["by"]);

        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == id && (f.Kind == "operator" || f.Kind == "hold")).OrderBy(f => f.Seq).ToListAsync();
        Assert.Equal(["hold:opened", "operator:requested", "hold:decided", "operator:done"], facts.Select(f => $"{f.Kind}:{f.Code}"));
        Assert.All(facts.Skip(1), f => Assert.Equal("operator:op-1", f.Actor));
        Assert.True((await new FactLog(db).Verify(id)).Ok);
        Assert.True(await db.ExceptionItems.AnyAsync(x => x.SubjectId == id && x.Code == "hold.approval"));
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
        // Resend is declared on the WPS leg, recorded, and refused until an engine has a path for it (Issue I-99).
        var resend = await actions.Request(Operator("op-1", WpsRun.Tenant), id, "retry", "the gateway was down");
        Assert.Equal("refused", (string?)resend["status"]);
    }
}
