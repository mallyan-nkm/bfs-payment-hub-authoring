using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TransactHub.Operations;
using TransactHub.Operations.Api;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The operations console's own chain (owner's decisions of 23 November 2026): every CSV export is an audit fact with who, when,
/// the filter and the row count, and no separate role (R-05-62, Issue I-144); rebuilding the read model, the console's one change
/// of the system, is asked for by a supervisor and carried out only when a bank approver, another person, consents (R-09-74).
/// An operator's decision on a payment's hold is a business decision and needs no such consent.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ConsoleRecordTests(Databases dbs)
{
    sealed class Host(StoreOptions store) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Development");
            b.UseSetting("Store:Provider", store.Provider.ToString());
            b.UseSetting("Store:Connection", store.ConnectionString);
            b.UseSetting("Operations:Views", Path.Combine(Repo.Root(), "content-configurations", "artefacts"));
            b.UseSetting("Operations:PollMilliseconds", "100");
            b.UseSetting("TH_AUTH_DEV", "1");
        }
    }

    static HttpClient As(HttpClient c, string sub, params string[] roles)
    {
        c.DefaultRequestHeaders.Add(OperatorAuth.DevHeader, new JsonObject { ["sub"] = sub, ["name"] = sub, ["roles"] = new JsonArray(roles.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString());
        return c;
    }

    static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Every_export_is_an_audit_fact_with_who_when_the_filter_and_the_row_count(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        await WpsRun.Seed(store);
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var host = new Host(store);
        var op = As(host.CreateClient(), "op-1", "example-bank:Operator");
        var r = await op.GetAsync("/ops/api/reports/export.csv?scheme=uae.wps&status=ACSC");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var rows = int.Parse(r.Headers.GetValues("x-th-rows").Single());
        await using (var db = StoreDbContext.Open(store))
        {
            var fact = await db.Facts.AsNoTracking().SingleAsync(f => f.SubjectId == ConsoleRecord.Subject && f.Kind == ConsoleRecord.ExportKind);
            Assert.Equal("csv", fact.Code);
            Assert.Equal("operator:op-1", fact.Actor);
            var d = JsonNode.Parse(fact.DataJson)!;
            Assert.Equal(rows, (int)d["rows"]!);
            Assert.Equal("uae.wps", (string?)d["filter"]!["scheme"]);
            Assert.Equal("ACSC", (string?)d["filter"]!["status"]);
            Assert.Equal(["example-bank"], d["tenants"]!.AsArray().Select(x => (string?)x));
            Assert.True(fact.At > DateTimeOffset.UtcNow.AddMinutes(-5));
            Assert.Equal(fact.Seq.ToString(), r.Headers.GetValues("x-th-export-fact").Single());
            Assert.True((await new FactLog(db).Verify(ConsoleRecord.Subject)).Ok);
        }
        // An export needs no role but the operator's: a supervisor's audit shows it.
        var sup = As(host.CreateClient(), "sup-1", "example-bank:Supervisor");
        var audit = await Json(await sup.GetAsync("/ops/api/audit"));
        Assert.Equal("operator:op-1", (string?)audit["exports"]![0]!["actor"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/ops/api/audit")).StatusCode);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_rebuild_waits_for_a_bank_approvers_consent_who_is_another_person(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        await using var host = new Host(store);
        var sup = As(host.CreateClient(), "sup-1", "example-bank:Supervisor");
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PostAsJsonAsync("/ops/api/rebuild", new { })).StatusCode);
        var ask = await sup.PostAsJsonAsync("/ops/api/rebuild", new { remarks = "after a restore (synthetic)" });
        Assert.Equal(HttpStatusCode.Accepted, ask.StatusCode);
        var id = (string)(await Json(ask))["action"]!;
        var approver = As(host.CreateClient(), "ba-1", "example-bank:BankApprover");
        Assert.Contains((await Json(await approver.GetAsync("/ops/api/system-actions/pending"))).AsArray(), x => (string?)x!["data"]!["action"] == id);
        // The supervisor may not consent to its own request, nor may another supervisor without the bank approver's role.
        Assert.Equal(HttpStatusCode.Forbidden, (await sup.PostAsJsonAsync($"/ops/api/system-actions/{id}/decision", new { approve = true, remarks = "mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(host.CreateClient(), "sup-2", "example-bank:Supervisor").PostAsJsonAsync($"/ops/api/system-actions/{id}/decision", new { approve = true, remarks = "x" })).StatusCode);
        var self = As(host.CreateClient(), "sup-1", "example-bank:Supervisor", "example-bank:BankApprover");
        Assert.Contains("may not consent", (string?)(await Json(await self.PostAsJsonAsync($"/ops/api/system-actions/{id}/decision", new { approve = true, remarks = "mine" })))["error"]);
        var done = await Json(await approver.PostAsJsonAsync($"/ops/api/system-actions/{id}/decision", new { approve = true, remarks = "agreed (synthetic)" }));
        Assert.Equal("done", (string?)done["status"]);
        Assert.True((bool)done["result"]!["rebuilt"]!);
        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == ConsoleRecord.Subject && f.Kind == ConsoleRecord.ConsentKind).OrderBy(f => f.Seq).ToListAsync();
        Assert.Equal(["requested", "approved", "done"], facts.Select(f => f.Code!));
        Assert.Equal(["operator:sup-1", "operator:ba-1", "operator:ba-1"], facts.Select(f => f.Actor));
        Assert.Equal(HttpStatusCode.Conflict, (await approver.PostAsJsonAsync($"/ops/api/system-actions/{id}/decision", new { approve = true, remarks = "again" })).StatusCode);
    }
}
