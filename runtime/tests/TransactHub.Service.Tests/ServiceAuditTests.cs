using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// The service console's audit page (R-05-76, Issue I-202): its facts newest first, filtered by person, action and date, each row
/// with its hash, and whether every chain they are on verifies, naming one that does not.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ServiceAuditTests(Databases dbs)
{
    [Fact]
    public async Task The_audit_reads_newest_first_filtered_each_row_with_its_hash_and_says_whether_each_chain_verifies()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub());
        var se = api.Engineer;
        // An action carried out at once, and one that waits for a bank approver, who refuses it.
        Assert.Equal(HttpStatusCode.OK, (await se.PostAsJsonAsync("/service/api/actions", new { action = "replay-read-model", reason = "catch up (synthetic)" })).StatusCode);
        var ask = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "activate", target = "dist/example-bank@2026.11.23-9", reason = "go-live (synthetic)", parameters = new { environment = "production" } }));
        Assert.Equal("pending-approval", (string?)ask["status"]);
        Assert.Equal(HttpStatusCode.OK, (await api.BankApprover.PostAsJsonAsync($"/service/api/actions/{(string)ask["action"]!}/decision", new { approve = false, reason = "not this week (synthetic)" })).StatusCode);

        var all = (await J.Of(await se.GetAsync("/service/api/audit"))).AsArray();
        Assert.True(all.Count >= 4, all.ToJsonString());
        var ids = all.Select(f => (long)f!["id"]!).ToList();
        Assert.Equal(ids.OrderByDescending(x => x), ids);
        Assert.All(all, f => Assert.Matches("^[0-9a-f]{64}$", (string)f!["hash"]!));

        // By person: the bank approver's refusal only; by action: the activation's facts only; by date.
        var byApprover = (await J.Of(await se.GetAsync("/service/api/audit?person=ba-1"))).AsArray();
        Assert.NotEmpty(byApprover);
        Assert.All(byApprover, f => Assert.Contains("ba-1", (string)f!["actor"]!));
        var byAction = (await J.Of(await se.GetAsync("/service/api/audit?action=activate"))).AsArray();
        Assert.True(byAction.Count >= 2, byAction.ToJsonString());
        Assert.All(byAction, f => Assert.Equal("activate", (string?)f!["data"]!["name"]));
        Assert.Empty((await J.Of(await se.GetAsync("/service/api/audit?to=2020-01-01"))).AsArray());
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        Assert.Equal(all.Count, (await J.Of(await se.GetAsync($"/service/api/audit?from={today}"))).AsArray().Count);

        var chain = await J.Of(await se.GetAsync("/service/api/audit/chain"));
        Assert.True((bool)chain["ok"]!, chain.ToJsonString());
        Assert.NotEmpty(chain["chains"]!.AsArray());

        // A fact changed behind the store's back: the chain it is on does not verify, and is named.
        var subject = (string)all[0]!["subject"]!;
        await using (var db = StoreDbContext.Open(api.Store))
        {
            var table = db.Model.FindEntityType(typeof(FactRow))!.GetTableName();
            await db.Database.ExecuteSqlRawAsync(api.Store.Provider == StoreProvider.SqlServer
                ? $"UPDATE [{table}] SET [Actor] = 'service:intruder' WHERE [SubjectId] = '{subject}' AND [Seq] = 1"
                : $"UPDATE \"{table}\" SET \"Actor\" = 'service:intruder' WHERE \"SubjectId\" = '{subject}' AND \"Seq\" = 1");
        }
        var broken = await J.Of(await se.GetAsync("/service/api/audit/chain"));
        Assert.False((bool)broken["ok"]!);
        var bad = broken["chains"]!.AsArray().Single(c => (string?)c!["subject"] == subject)!;
        Assert.False((bool)bad["ok"]!);
        Assert.Contains("fact 1 was changed", (string?)bad["problem"]);

        // Only the service roles read it.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("op-1", "example-bank:Operator").GetAsync("/service/api/audit/chain")).StatusCode);
    }
}
