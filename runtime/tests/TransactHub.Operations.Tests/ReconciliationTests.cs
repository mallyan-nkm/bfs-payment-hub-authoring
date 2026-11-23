using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;
using static TransactHub.Operations.Tests.ProjectionTests;

namespace TransactHub.Operations.Tests;

/// <summary>
/// Reconciliation (R-09-92): the scheme's report is matched against the store and every break recorded as a fact; the console's
/// break queue lists them and resolves each once: an explanation at once, a resolution that changes the system (the payment takes
/// the scheme's status) only with the consent of a bank approver who did not ask for it (R-09-77). The File API runs it when the
/// report arrives (FileApiTests, "a scheme report is reconciled on arrival").
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReconciliationTests(Databases dbs)
{
    static string E2e(int n) => $"{WpsRun.FileId}-{n}";

    static byte[] Report() => Encoding.UTF8.GetBytes(string.Join("\n",
        "reference,amount,currency,status",
        $"{E2e(1)},1000.00,AED,",                 // matches
        $"{E2e(2)},499.00,AED,",                  // the amount differs
        "0000000000000000UNKNOWN-1,10.00,AED,",   // no such payment
        $"{E2e(1)},1000.00,AED,settled"));        // the scheme says settled; the store has it received

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_scheme_report_is_matched_and_its_breaks_are_resolved_once_with_consent_where_they_change_the_system(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        var bulk = await WpsRun.Seed(store);
        JsonObject run;
        await using (var db = StoreDbContext.Open(store))
            run = await new SchemeReconciliation(db).Run(WpsRun.Tenant, "uae.wps", "file-report-1", Report(), "file-api:reconcile");
        Assert.Equal("unmatched", (string?)run["outcome"]);
        Assert.Equal((4, 1), ((int)run["lines"]!, (int)run["matched"]!));
        var breaks = ((JsonArray)run["breaks"]!).Select(b => (string?)b!["kind"]).ToList();
        Assert.Equal(["amount", "not-in-store", "status"], breaks);

        var q = new BreakQueue(store);
        var op = Operator("op-1", WpsRun.Tenant);
        var approver = Operator("ap-1", WpsRun.Tenant, OperatorRoles.BankApprover);
        var list = await q.List(op);
        Assert.Equal(3, list.Count);
        Assert.Empty(await q.List(Operator("op-x", "other-bank")));
        string Id(string kind) => (string)list.First(b => (string?)b!["kind"] == kind)!["break"]!;

        // An explanation changes nothing: resolved at once, and once.
        Assert.Equal(400, (await Assert.ThrowsAsync<ActionRefused>(() => q.Resolve(op, Id("not-in-store"), "explain", null))).Status);
        Assert.Equal("resolved", (string?)(await q.Resolve(op, Id("not-in-store"), "explain", "a test line of the scheme"))["status"]);
        Assert.Equal(409, (await Assert.ThrowsAsync<ActionRefused>(() => q.Resolve(op, Id("not-in-store"), "explain", "again"))).Status);
        // Adopting the scheme's status changes the system: it waits for a bank approver, not the one who asked.
        Assert.Equal(409, (await Assert.ThrowsAsync<ActionRefused>(() => q.Resolve(op, Id("amount"), "adopt-status", "the scheme names no status"))).Status);
        var asked = await q.Resolve(op, Id("status"), "adopt-status", "the scheme settled it");
        Assert.Equal(("pending-consent", OperatorRoles.BankApprover), ((string?)asked["status"], (string?)asked["needs"]));
        Assert.Equal(403, (await Assert.ThrowsAsync<ActionRefused>(() => q.Consent(op, Id("status"), true, "mine"))).Status);
        var both = new OperatorIdentity("op-1", "op-1", new Dictionary<string, IReadOnlySet<string>> { [WpsRun.Tenant] = new HashSet<string> { OperatorRoles.Operator, OperatorRoles.BankApprover } });
        Assert.Equal(403, (await Assert.ThrowsAsync<ActionRefused>(() => q.Consent(both, Id("status"), true, "my own"))).Status);
        var done = await q.Consent(approver, Id("status"), true, "checked against the scheme's statement");
        Assert.Equal(("resolved", "ACSC"), ((string?)done["status"], (string?)done["adopted"]));
        Assert.Equal(409, (await Assert.ThrowsAsync<ActionRefused>(() => q.Consent(approver, Id("status"), true, "again"))).Status);

        var open = await q.List(op);
        Assert.Equal(["amount"], open.Select(b => (string?)b!["kind"]));
        Assert.Equal(3, (await q.List(op, all: true)).Count);
        await using (var db = StoreDbContext.Open(store))
        {
            var payment = (string)done["payment"]!;
            var status = await db.Facts.AsNoTracking().Where(f => f.SubjectId == payment && f.Kind == "status").OrderBy(f => f.Seq).ToListAsync();
            Assert.Equal("ACSC", status[^1].Code);
            Assert.Equal("operator:ap-1", status[^1].Actor);
            var chain = await db.Facts.AsNoTracking().Where(f => f.SubjectId == (string)run["run"]!).OrderBy(f => f.Seq).Select(f => f.Code).ToListAsync();
            Assert.Equal(["run", "break", "break", "break", "unmatched", "resolved", "resolution-requested", "resolved"], chain);
            Assert.True((await new FactLog(db).Verify((string)run["run"]!)).Ok);
        }
    }

    [Fact]
    public void A_report_reads_as_CSV_with_a_header_or_as_JSON_lines_amounts_exact()
    {
        var csv = SchemeReport.Parse(Encoding.UTF8.GetBytes("Reference,Amount\nE2E-1,0.10\n# a remark\nE2E-2,1234567890123.45\n"));
        Assert.Equal([0.10m, 1234567890123.45m], csv.Select(l => l.Amount!.Value));
        var json = SchemeReport.Parse(Encoding.UTF8.GetBytes("""{ "lines": [ { "reference": "E2E-1", "amount": "5.00", "status": "rejected" } ] }"""));
        Assert.Equal(("E2E-1", 5.00m, "RJCT"), (json[0].Reference, json[0].Amount!.Value, SchemeReconciliation.Iso(json[0].Status)));
        Assert.Throws<FormatException>(() => SchemeReport.Parse(Encoding.UTF8.GetBytes("amount\n1\n")));
    }
}
