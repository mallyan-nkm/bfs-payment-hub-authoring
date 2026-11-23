using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// Two activations of different distributions for the same bank and environment at once (R-09-137): the store's unique key on the
/// activation's number lets one win. Exactly one activation row and one draining fact come of it, on SQL Server and PostgreSQL.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ActivationRaceTests(Databases dbs)
{
    const string Bank = "example-bank", Env = "test";

    /// <summary>Holds each writer after it read the last activation's number until the other has read it too: a race every time.</summary>
    sealed class Gate(Barrier barrier) : DbCommandInterceptor
    {
        static bool ReadsTheNumber(DbCommand c) => c.CommandText.Contains("release_activation", StringComparison.Ordinal) && c.CommandText.Contains("MAX(", StringComparison.OrdinalIgnoreCase);

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (ReadsTheNumber(command)) barrier.SignalAndWait(TimeSpan.FromSeconds(20));
            return ValueTask.FromResult(result);
        }

        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
        {
            if (ReadsTheNumber(command)) barrier.SignalAndWait(TimeSpan.FromSeconds(20));
            return ValueTask.FromResult(result);
        }
    }

    static StoreDbContext Gated(StoreOptions o, Barrier b) =>
        new(new DbContextOptionsBuilder<StoreDbContext>(StoreDbContext.Configure(o)).AddInterceptors(new Gate(b)).Options);

    static async Task Install(StoreOptions o, string id)
    {
        await using var db = StoreDbContext.Open(o);
        var zip = Encoding.UTF8.GetBytes($"synthetic distribution {id}");
        await new DistributionRegistry(db).Store(Bank, id, zip, new JsonObject { ["id"] = id, ["bank"] = Bank }, Convert.ToHexStringLower(SHA256.HashData(zip)), "test", "install");
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Of_two_activations_at_once_one_wins_with_one_activation_row_and_one_draining_fact(StoreProvider p)
    {
        var o = dbs.Fresh(p);
        await using (var db = StoreDbContext.Open(o)) await db.Database.MigrateAsync();
        foreach (var id in new[] { "2026.11.23-1", "2026.11.23-2", "2026.11.23-3" }) await Install(o, id);
        var a = DistributionRegistry.IdOf(Bank, "2026.11.23-1");
        var b = DistributionRegistry.IdOf(Bank, "2026.11.23-2");
        var c = DistributionRegistry.IdOf(Bank, "2026.11.23-3");
        await using (var db = StoreDbContext.Open(o)) await new DistributionRegistry(db).Activate(a, Env, "engineer", "first");

        using var barrier = new Barrier(2);
        async Task<Exception?> Activate(string dist)
        {
            await using var db = Gated(o, barrier);
            try { await new DistributionRegistry(db).Activate(dist, Env, "engineer", "at once"); return null; }
            catch (Exception e) { return e; }
        }
        var results = await Task.WhenAll(Task.Run(() => Activate(b)), Task.Run(() => Activate(c)));

        Assert.Single(results, r => r is null);
        var lost = Assert.Single(results, r => r is not null);
        Assert.IsType<ActivationRaced>(lost);
        await using (var db = StoreDbContext.Open(o))
        {
            var rows = await db.Activations.AsNoTracking().Where(x => x.Environment == Env && x.Scope == DistributionRegistry.Scope(Bank)).OrderBy(x => x.Sequence).ToListAsync();
            Assert.Equal([1L, 2L], rows.Select(x => x.Sequence));
            var winner = DistributionRegistry.IdOf(Bank, rows[1].Release);
            Assert.Equal(results[0] is null ? b : c, winner);
            var draining = await db.Facts.AsNoTracking().Where(f => f.Kind == DistributionRegistry.StateKind && f.Code == "draining").ToListAsync();
            Assert.Equal(a, Assert.Single(draining).SubjectId);
            var reg = new DistributionRegistry(db);
            Assert.Equal(winner, await reg.Active(Bank, Env));
            Assert.Equal("installed", await reg.StateIn(winner == b ? c : b, Env));
        }

        // The one that lost activates afterwards as an ordinary switch-over: the winner drains.
        await using (var db = StoreDbContext.Open(o))
        {
            var loser = results[0] is null ? c : b;
            var (active, drained) = await new DistributionRegistry(db).Activate(loser, Env, "engineer", "again");
            Assert.Equal((loser, results[0] is null ? b : c), (active, drained));
            Assert.Equal(3L, await ReleaseRegistry.LastSequence(db, Env, DistributionRegistry.Scope(Bank)));
        }
    }
}
