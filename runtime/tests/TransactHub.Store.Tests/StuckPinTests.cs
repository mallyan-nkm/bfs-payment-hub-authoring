using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// A payment pinned but never started (Issue I-206, R-09-164, R-09-165), on SQL Server and PostgreSQL: a pin with no end whose payment
/// no engine's run accepted is listed once it is old enough; it ends abandoned once, with its reason and code, and then its
/// distribution drains and may be removed. A pin whose run accepted it, or that has ended, is not listed.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StuckPinTests(Databases dbs)
{
    const string Bank = "example-bank", Env = "test";

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_pin_no_run_accepted_is_listed_ends_abandoned_once_and_its_distribution_is_removed(StoreProvider p)
    {
        var o = dbs.Fresh(p);
        var clock = new Clock(new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero));
        await using var db = StoreDbContext.Open(o);
        await db.Database.MigrateAsync();
        var reg = new DistributionRegistry(db, clock);
        foreach (var id in new[] { "2026.11.23-1", "2026.11.23-2" })
        {
            var zip = Encoding.UTF8.GetBytes($"synthetic distribution {id}");
            await reg.Store(Bank, id, zip, new JsonObject { ["id"] = id, ["bank"] = Bank }, Convert.ToHexStringLower(SHA256.HashData(zip)), "test", "install");
        }
        var a = DistributionRegistry.IdOf(Bank, "2026.11.23-1");
        var b = DistributionRegistry.IdOf(Bank, "2026.11.23-2");
        await reg.Activate(a, Env, "engineer", "first");
        await reg.Pin("PAY-STUCK", Bank, Env, "front-door");
        await reg.Pin("PAY-RUNS", Bank, Env, "front-door");
        await reg.Pin("PAY-DONE", Bank, Env, "front-door");
        await new FactLog(db, clock).Append("payload-1", "payload", new NewFact("payload", "create", new JsonObject { ["paymentId"] = "PAY-RUNS" }, a, "engine:temporal"));
        await reg.End("PAY-DONE", "settled", "engine:temporal");

        // Younger than the wait: not yet listed.
        Assert.Empty(await reg.Unstarted(TimeSpan.FromMinutes(15)));
        clock.Now += TimeSpan.FromMinutes(20);
        var stuck = Assert.Single(await reg.Unstarted(TimeSpan.FromMinutes(15)));
        Assert.Equal(("PAY-STUCK", a, Bank, Env), (stuck.PaymentId, stuck.Distribution, stuck.Bank, stuck.Environment));

        await reg.Activate(b, Env, "engineer", "second");
        Assert.Equal(2, (await reg.OpenPayments()).GetValueOrDefault(a));
        Assert.True(await reg.Abandon("PAY-STUCK", "the engine host refused the start (synthetic)", "bank.approver", "frontdoor.EngineRefused"));
        Assert.False(await reg.Abandon("PAY-STUCK", "again", "bank.approver"));
        var end = await db.Facts.AsNoTracking().SingleAsync(f => f.SubjectId == "PAY-STUCK" && f.Kind == DistributionRegistry.EndKind);
        Assert.Equal((DistributionRegistry.Abandoned, a), (end.Code, end.ReleaseId));
        Assert.Equal("frontdoor.EngineRefused", (string?)JsonNode.Parse(end.DataJson)!["code"]);
        Assert.Empty(await reg.Unstarted(TimeSpan.Zero));
        await Assert.ThrowsAsync<DistributionRefused>(() => reg.Remove(a, Env, "engineer", "too early"));
        await reg.End("PAY-RUNS", "settled", "engine:temporal");
        Assert.Equal("drained", await reg.Refresh(a, Env, "service-console"));
        await reg.Remove(a, Env, "engineer", "drained");
        Assert.Equal("removed", await reg.StateIn(a, Env));
        await Assert.ThrowsAsync<DistributionRefused>(() => reg.Abandon("PAY-NONE", "never pinned", "bank.approver"));
    }
}
