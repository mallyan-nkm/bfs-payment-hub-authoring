using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// The owner's decisions of 23 November 2026 in the service console. Every action that changes the system is asked for by one
/// person and carried out only when another person with the bank approver's role consents, both as facts (R-09-74); installing a
/// version and restoring a backup among them (Issue I-122). The runtime serves one bank, configured at install: another bank's
/// distribution is refused at verify, install and activate (R-09-73).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ConsentTests(Databases dbs)
{
    const string Bank = Seed.Tenant;

    [Fact]
    public async Task Every_change_of_the_system_waits_for_a_bank_approver_who_is_another_person_and_both_are_facts()
    {
        // The line drawn (R-09-74): only what the runtime does by itself is carried out at once.
        var immediate = ActionBook.Catalogue.Where(a => !a.Risky).Select(a => a.Id).Order().ToArray();
        Assert.Equal(["replay-read-model", "reverify-package"], immediate);
        Assert.Contains(ActionBook.Catalogue, a => a.Id == "install" && a.Risky);
        Assert.Contains(ActionBook.Catalogue, a => a.Id == "restore" && a.Risky);

        using var s = new Signers();
        var gh = new FakeGitHub();
        gh.Publish($"dist/{Bank}/2026.11.23-1", $"content-configurations/releases/distributions/{Bank}/2026.11.23-1", DistributionContentTests.Distribution(s.Key, "2026.11.23-1"));
        await using var api = new ServiceApi(dbs.Fresh(), s, gh);
        var se = api.Engineer;

        // Asked for: verified now, nothing installed until a bank approver consents.
        var ask = await se.PostAsJsonAsync("/service/api/content/install", new { tag = $"dist/{Bank}/2026.11.23-1", reason = "CR-10: the bank's first distribution (synthetic)" });
        Assert.Equal(HttpStatusCode.Accepted, ask.StatusCode);
        var asked = await J.Of(ask);
        Assert.Equal("pending-approval", (string?)asked["status"]);
        Assert.Equal("BankApprover", (string?)asked["needs"]);
        var id = (string)asked["action"]!;
        await using (var db = StoreDbContext.Open(api.Store)) Assert.False(await db.Releases.AnyAsync());

        // An engineer may not consent; the maker may not, even holding the bank approver's role; a decision needs a reason.
        Assert.Equal(HttpStatusCode.Forbidden, (await se.PostAsJsonAsync($"/service/api/actions/{id}/decision", new { approve = true, reason = "mine" })).StatusCode);
        var self = await J.Of(await api.As("se-1", "*:ServiceEngineer", "*:BankApprover").PostAsJsonAsync($"/service/api/actions/{id}/decision", new { approve = true, reason = "mine" }));
        Assert.Equal("ops.MakerApproves", (string?)self["code"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.BankApprover.PostAsJsonAsync($"/service/api/actions/{id}/decision", new { approve = true })).StatusCode);
        var pending = (await J.Of(await api.BankApprover.GetAsync("/service/api/actions/pending"))).AsArray();
        Assert.Contains(pending, p => (string?)p!["data"]!["action"] == id && (string?)p["data"]!["name"] == "install");

        var done = await J.Of(await api.BankApprover.PostAsJsonAsync($"/service/api/actions/{id}/decision", new { approve = true, reason = "Checked against CR-10 (synthetic)" }));
        Assert.True((string?)done["status"] == "done", done.ToJsonString());
        Assert.True((bool)done["result"]!["stored"]!);

        // Activation too, and the facts: who asked, who consented, what was done.
        var act = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "activate", target = $"dist/{Bank}@2026.11.23-1", reason = "CR-10 go-live (synthetic)", parameters = new { environment = "production" } }));
        Assert.Equal("pending-approval", (string?)act["status"]);
        Assert.Equal("done", (string?)(await J.Of(await api.BankApprover.PostAsJsonAsync($"/service/api/actions/{(string)act["action"]!}/decision", new { approve = true, reason = "go-live agreed" })))["status"]);
        await using (var db = StoreDbContext.Open(api.Store))
        {
            var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == ServiceAudit.Subject && f.Code!.StartsWith("action.")).OrderBy(f => f.Seq).ToListAsync();
            var install = facts.Where(f => (string?)JsonNode.Parse(f.DataJson)!["action"] == id).ToList();
            Assert.Equal(["action.requested", "action.approved", "action.done"], install.Select(f => f.Code!));
            Assert.Equal("service:se-1", install[0].Actor);
            Assert.Equal("service:ba-1", install[1].Actor);
            Assert.Equal("service:se-1", (string?)JsonNode.Parse(install[1].DataJson)!["requestedBy"]);
            Assert.True((bool)JsonNode.Parse(install[1].DataJson)!["consent"]!);
            Assert.True((await new FactLog(db).Verify(ServiceAudit.Subject)).Ok);
            Assert.Equal("active", await new DistributionRegistry(db).StateIn($"dist/{Bank}@2026.11.23-1", "production"));
        }

        // A refused consent changes nothing.
        var drain = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "drain", target = $"dist/{Bank}@2026.11.23-1", reason = "try (synthetic)", parameters = new { environment = "production" } }));
        Assert.Equal("rejected", (string?)(await J.Of(await api.BankApprover.PostAsJsonAsync($"/service/api/actions/{(string)drain["action"]!}/decision", new { approve = false, reason = "not agreed" })))["status"]);
        await using (var db = StoreDbContext.Open(api.Store)) Assert.Equal("active", await new DistributionRegistry(db).StateIn($"dist/{Bank}@2026.11.23-1", "production"));
    }

    [Fact]
    public async Task Another_banks_distribution_is_refused_at_verify_install_and_activate()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        // Signed by a key the runtime trusts, for another bank: the studio is multi-tenant, the runtime is not.
        gh.Publish("dist/dib/2026.11.23-1", "content-configurations/releases/distributions/dib/2026.11.23-1", DistributionContentTests.Distribution(s.Key, "2026.11.23-1", bank: "dib"));
        await using var api = new ServiceApi(dbs.Fresh(), s, gh);
        var se = api.Engineer;
        var listed = (await J.Of(await se.GetAsync("/service/api/content/versions"))).AsArray().Single()!;
        Assert.Equal("dib", (string?)listed["bank"]);
        Assert.False((bool)listed["installable"]!);

        var verify = await se.PostAsJsonAsync("/service/api/content/verify", new { tag = "dist/dib/2026.11.23-1", reason = "check" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, verify.StatusCode);
        Assert.Contains("names bank dib, but this runtime serves example-bank only", (string?)(await J.Of(verify))["error"]);
        var install = await se.PostAsJsonAsync("/service/api/content/install", new { tag = "dist/dib/2026.11.23-1", reason = "try (synthetic)" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, install.StatusCode);
        Assert.Equal("package.Rejected", (string?)(await J.Of(install))["code"]);
        Assert.Empty((await J.Of(await se.GetAsync("/service/api/actions/pending"))).AsArray());

        // Already in the store (as a runtime of no bank could have stored it): never activated here, even with consent.
        await using (var db = StoreDbContext.Open(api.Store))
            await new DistributionRegistry(db).Store("dib", "2026.11.23-1", [1, 2, 3], new JsonObject { ["id"] = "2026.11.23-1", ["bank"] = "dib" }, "00", "test", "older runtime");
        var act = await api.Act(se, new { action = "activate", target = "dist/dib@2026.11.23-1", reason = "try (synthetic)", parameters = new { environment = "production" } });
        Assert.Equal("refused", (string?)act["status"]);
        Assert.Contains("serves example-bank only", (string?)act["result"]!["message"]);
        await using (var db = StoreDbContext.Open(api.Store)) Assert.Null(await new DistributionRegistry(db).Active("dib", "production"));
    }

    [Fact]
    public async Task A_service_console_without_its_bank_does_not_start()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), new() { ["TH_BANK"] = "" });
        Assert.Contains("Set TH_BANK", Assert.ThrowsAny<Exception>(() => api.CreateClient()).ToString());
    }
}
