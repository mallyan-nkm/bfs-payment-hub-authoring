using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TransactHub.Operations.Api;
using TransactHub.Operations.Registers;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The operations console's work management, unmasking, turnaround limits, fixed WPS reports, retention and erasure, and the
/// channel's core-banking enquiries (R-05-66 to R-05-71, R-09-100), on SQL Server and PostgreSQL. Synthetic data only. Every
/// change is a fact; every change of the system waits for a bank approver, another person (R-09-77); the store stays insert-only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ConsoleWorkTests(Databases dbs)
{
    const string Bank = WpsRun.Tenant;

    sealed class Host(StoreOptions store, Action<IWebHostBuilder>? more = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Development");
            b.UseSetting("Store:Provider", store.Provider.ToString());
            b.UseSetting("Store:Connection", store.ConnectionString);
            b.UseSetting("Operations:Views", Path.Combine(Repo.Root(), "content-configurations", "artefacts"));
            b.UseSetting("Operations:PollMilliseconds", "100");
            b.UseSetting("Operations:TatSweepSeconds", "3600");
            b.UseSetting("Operations:Notify:Adapter", "simulator");
            b.UseSetting("TH_AUTH_DEV", "1");
            more?.Invoke(b);
        }
    }

    static HttpClient As(Host h, string sub, params string[] roles)
    {
        var c = h.CreateClient();
        c.DefaultRequestHeaders.Add(OperatorAuth.DevHeader, new JsonObject { ["sub"] = sub, ["name"] = sub, ["roles"] = new JsonArray(roles.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString());
        return c;
    }

    static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    static async Task Dial(StoreOptions store, string parameter, string value, DateTimeOffset from, string set = "1.8.0")
    {
        await using var db = StoreDbContext.Open(store);
        await new VersionedValues(db).Publish(OperationsDials.ConfigKind, Bank, parameter, new JsonObject { ["value"] = value, ["configurationSet"] = set }, from, null, "test");
    }

    static async Task<List<FactRow>> Facts(StoreOptions store, string subject)
    {
        await using var db = StoreDbContext.Open(store);
        return await db.Facts.AsNoTracking().Where(f => f.SubjectId == subject).OrderBy(f => f.Seq).ToListAsync();
    }

    static async Task<(string id, string hash)[]> AllFacts(StoreOptions store)
    {
        await using var db = StoreDbContext.Open(store);
        return (await db.Facts.AsNoTracking().OrderBy(f => f.Id).Select(f => new { f.SubjectId, f.Hash }).ToListAsync()).Select(f => (f.SubjectId, f.Hash)).ToArray();
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Work_is_assigned_escalated_reassigned_and_released_each_a_fact_and_a_bulk_change_keeps_each_items_rules(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var a = await WpsRun.Seed(store);
        var b = await WpsRun.Seed(store, fileId: "0000001234567261117083001");
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var host = new Host(store);
        var op1 = As(host, "op-1", $"{Bank}:Operator");
        var op2 = As(host, "op-2", $"{Bank}:Operator");
        var sup = As(host, "sup-1", $"{Bank}:Supervisor");

        var queue = await Json(await op1.GetAsync("/ops/api/work/queue?filter=unassigned"));
        Assert.Equal(2, (int)queue["total"]!);
        var cursor = (long)queue["cursor"]!;

        // An operator takes an item for themself; another operator may not take it from them, nor give it to someone else.
        Assert.Equal(HttpStatusCode.OK, (await op1.PostAsJsonAsync("/ops/api/work", new { op = "assign", kind = "payment", id = a })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op2.PostAsJsonAsync("/ops/api/work", new { op = "assign", kind = "payment", id = a })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op2.PostAsJsonAsync("/ops/api/work", new { op = "assign", kind = "payment", id = b, assignee = "op-1" })).StatusCode);
        // The live queue sees the change without a reload.
        var changes = await Json(await op2.GetAsync($"/ops/api/work/changes?after={cursor}&wait=5"));
        Assert.True((bool)changes["changed"]!);
        Assert.Equal(1, (int)(await Json(await op1.GetAsync("/ops/api/work/queue?filter=mine")))["total"]!);

        // Escalating needs a reason and hands the item to the supervisors; a supervisor reassigns it, with a reason.
        Assert.Equal(HttpStatusCode.BadRequest, (await op1.PostAsJsonAsync("/ops/api/work", new { op = "escalate", kind = "payment", id = a })).StatusCode);
        var esc = await Json(await op1.PostAsJsonAsync("/ops/api/work", new { op = "escalate", kind = "payment", id = a, remarks = "limit breach needs a supervisor" }));
        Assert.True((bool)esc["escalated"]!);
        Assert.Equal(1, (int)esc["level"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await op2.PostAsJsonAsync("/ops/api/work", new { op = "assign", kind = "payment", id = a })).StatusCode);
        Assert.Equal(1, (int)(await Json(await sup.GetAsync("/ops/api/work/queue?filter=escalated")))["total"]!);
        Assert.Equal(HttpStatusCode.OK, (await sup.PostAsJsonAsync("/ops/api/work", new { op = "assign", kind = "payment", id = a, assignee = "op-2", remarks = "op-2 knows the employer" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PostAsJsonAsync("/ops/api/work", new { op = "assign", kind = "payment", id = a, assignee = "op-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op2.PostAsJsonAsync("/ops/api/work", new { op = "release", kind = "payment", id = a, remarks = "done" })).StatusCode);

        var chain = await Facts(store, new WorkItem("payment", a).Chain);
        Assert.Equal(["assigned", "escalated", "assigned", "released"], chain.Select(f => f.Code));
        Assert.Equal(["operator:op-1", "operator:op-1", "operator:sup-1", "operator:op-2"], chain.Select(f => f.Actor));
        await using (var db = StoreDbContext.Open(store)) Assert.True((await new FactLog(db).Verify(new WorkItem("payment", a).Chain)).Ok);

        // A bulk change: each item by its own rules, each its own fact; an unknown item is refused alone.
        var bulk = await Json(await op2.PostAsJsonAsync("/ops/api/work/bulk", new { op = "assign", items = new[] { new { kind = "payment", id = a }, new { kind = "payment", id = b }, new { kind = "payment", id = "no-such" } } }));
        Assert.Equal((2, 1), ((int)bulk["done"]!, (int)bulk["refused"]!));
        Assert.Single((await Facts(store, new WorkItem("payment", b).Chain)));
        var taken = await Json(await op1.PostAsJsonAsync("/ops/api/work/bulk", new { op = "assign", items = new[] { new { kind = "payment", id = a }, new { kind = "payment", id = b } } }));
        Assert.Equal(0, (int)taken["done"]!);
        Assert.All(taken["results"]!.AsArray(), r => Assert.Equal(403, (int)r!["status"]!));

        // A bulk operator action never skips a payment's second approver: each request waits, each on its own chain.
        var cancel = await Json(await op1.PostAsJsonAsync("/ops/api/payments/bulk-actions", new { ids = new[] { a, b }, action = "cancel", remarks = "the employer withdrew both files" }));
        Assert.Equal(2, (int)cancel["done"]!);
        Assert.All(cancel["results"]!.AsArray(), r => Assert.Equal("pending-approval", (string?)r!["result"]!["status"]));
        // The maker may not approve their own, in bulk either; a supervisor's approval goes item by item.
        var items = cancel["results"]!.AsArray().Select(r => new { payment = (string?)r!["payment"], action = (string?)r["result"]!["action"] }).ToArray();
        var self = await Json(await op1.PostAsJsonAsync("/ops/api/actions/bulk-decision", new { items, approve = true, remarks = "mine" }));
        Assert.Equal(0, (int)self["done"]!);
        Assert.All(self["results"]!.AsArray(), r => Assert.Equal(403, (int)r!["status"]!));
        foreach (var id in new[] { a, b })
            Assert.Equal(["requested"], (await Facts(store, id)).Where(f => f.Kind == "operator").Select(f => f.Code));
        // The actions waiting are work items too.
        Assert.Equal(4, (int)(await Json(await sup.GetAsync("/ops/api/work/queue")))["total"]!);
    }

    [Fact]
    public async Task Saved_filters_are_each_persons_own_and_insert_only()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        await using var host = new Host(store);
        var op1 = As(host, "op-1", $"{Bank}:Operator");
        var op2 = As(host, "op-2", $"{Bank}:Operator");
        var saved = await Json(await op1.PostAsJsonAsync("/ops/api/filters", new { name = "WPS on hold", query = new { scheme = "uae.wps", hold = "open" } }));
        Assert.Equal("uae.wps", (string?)saved["query"]!["scheme"]);
        Assert.Single((await Json(await op1.GetAsync("/ops/api/filters"))).AsArray());
        Assert.Empty((await Json(await op2.GetAsync("/ops/api/filters"))).AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await op2.PostAsync($"/ops/api/filters/{(string)saved["id"]!}/remove", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op1.PostAsync($"/ops/api/filters/{(string)saved["id"]!}/remove", null)).StatusCode);
        Assert.Empty((await Json(await op1.GetAsync("/ops/api/filters"))).AsArray());
        await using var db = StoreDbContext.Open(store);
        Assert.Equal(["saved", "removed"], await db.Facts.AsNoTracking().Where(f => f.Kind == SavedFilters.Kind).OrderBy(f => f.Seq).Select(f => f.Code!).ToListAsync());
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_masked_field_is_shown_to_the_person_who_asked_for_the_window_after_a_different_bank_approver_consents(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var file = await WpsRun.Seed(store);
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var host = new Host(store);
        var op = As(host, "op-1", $"{Bank}:Operator");
        var other = As(host, "op-2", $"{Bank}:Operator");
        var approver = As(host, "ba-1", $"{Bank}:BankApprover");
        var both = As(host, "op-1", $"{Bank}:Operator", $"{Bank}:BankApprover");
        var ask = new { subject = file, field = "subject:debitAccount", remarks = "the employer disputes the debit account" };

        // The default window is P0D: unmasking is off until the bank sets one.
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync("/ops/api/unmask", ask)).StatusCode);
        await Dial(store, "p.view.operations.v1#unmaskWindow", "PT3S", DateTimeOffset.UtcNow.AddDays(-1));
        // A field shown whole has nothing to unmask; a reason is required.
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync("/ops/api/unmask", new { subject = file, field = "subject:corporateName", remarks = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/ops/api/unmask", new { subject = file, field = "subject:debitAccount" })).StatusCode);
        var req = await Json(await op.PostAsJsonAsync("/ops/api/unmask", ask));
        var id = (string)req["action"]!;
        Assert.Equal(HttpStatusCode.Conflict, (await op.GetAsync($"/ops/api/unmask/{id}")).StatusCode);
        // The asker may not consent, even holding the role; another bank approver does.
        Assert.Equal(HttpStatusCode.Forbidden, (await both.PostAsJsonAsync($"/ops/api/system-actions/{id}/decision", new { approve = true, remarks = "mine" })).StatusCode);
        var consent = await Json(await approver.PostAsJsonAsync($"/ops/api/system-actions/{id}/decision", new { approve = true, remarks = "dispute ticket seen" }));
        Assert.Equal("done", (string?)consent["status"]);
        var seen = await Json(await op.GetAsync($"/ops/api/unmask/{id}"));
        Assert.Equal("AE070331234567890123999", (string?)seen["value"]);
        // Nobody else sees it, and the read model still holds it masked.
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/ops/api/unmask/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await approver.GetAsync($"/ops/api/unmask/{id}")).StatusCode);
        var shown = await Json(await op.GetAsync($"/ops/api/payments/{file}"));
        Assert.NotEqual("AE070331234567890123999", (string?)shown["payment"]!["fields"]!["debitAccount"]);
        // A line's field names its line.
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/ops/api/unmask", new { subject = file, field = "line:employeeId", remarks = "x" })).StatusCode);
        await Task.Delay(3200);
        Assert.Equal(HttpStatusCode.Gone, (await op.GetAsync($"/ops/api/unmask/{id}")).StatusCode);

        // The request, the consent and the one view are facts with ids only; the value is in none of them.
        var facts = await Facts(store, ConsoleRecord.Subject);
        Assert.Contains(facts, f => f.Kind == ConsoleRecord.ConsentKind && f.Code == "requested" && f.Actor == "operator:op-1");
        Assert.Contains(facts, f => f.Kind == ConsoleRecord.ConsentKind && f.Code == "approved" && f.Actor == "operator:ba-1");
        Assert.Single(facts, f => f.Kind == ConsoleRecord.UnmaskKind && f.Code == "viewed");
        Assert.DoesNotContain(facts, f => f.DataJson.Contains("AE070331234567890123999"));
        await using var db = StoreDbContext.Open(store);
        Assert.True((await new FactLog(db).Verify(ConsoleRecord.Subject)).Ok);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_payment_past_its_turnaround_limit_is_listed_and_alerted_once_through_the_notification_port(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var open = await WpsRun.Seed(store, hold: false);
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var host = new Host(store);
        var op = As(host, "op-1", $"{Bank}:Operator");
        // No limit by default.
        Assert.Equal(0, (int)(await Json(await op.GetAsync("/ops/api/tat/breaches")))["total"]!);
        // The platform's limit is not WPS's once the bank sets WPS's own; a value from a later date does not reach this payment.
        await Dial(store, "p.view.operations.v1#tatLimit", "P1D", DateTimeOffset.UtcNow.AddDays(-2));
        Assert.Equal(0, (int)(await Json(await op.GetAsync("/ops/api/tat/breaches")))["total"]!);
        await Dial(store, "uae.wps.view.operations.v1#tatLimit", "PT1S", DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(0, (int)(await Json(await op.GetAsync("/ops/api/tat/breaches")))["total"]!);
        await Dial(store, "uae.wps.view.operations.v1#tatLimit", "PT1S", DateTimeOffset.UtcNow.AddDays(-2), set: "1.8.1");
        await Task.Delay(1100);
        var list = await Json(await op.GetAsync("/ops/api/tat/breaches"));
        Assert.Equal(1, (int)list["total"]!);
        Assert.Equal(open, (string?)list["rows"]![0]!["id"]);
        Assert.Equal("uae.wps.view.operations.v1#tatLimit", (string?)list["rows"]![0]!["parameter"]);
        Assert.Equal("PT1S", (string?)list["rows"]![0]!["limit"]);
        // Another tenant's operator sees none of it.
        Assert.Equal(0, (int)(await Json(await As(host, "op-x", "other-bank:Operator").GetAsync("/ops/api/tat/breaches")))["total"]!);

        var tat = host.Services.GetRequiredService<Turnaround>();
        Assert.Equal(1, await tat.Sweep());
        Assert.Equal(0, await tat.Sweep());
        var sent = ((SimulatedNotifier)host.Services.GetRequiredService<IOpsNotifier>()).Sent;
        Assert.Equal(open, Assert.Single(sent).SubjectId);
        Assert.Equal("tat.breached", sent[0].Kind);
        Assert.Single(await Facts(store, ConsoleRecord.Subject), f => f.Kind == ConsoleRecord.TatKind && f.Code == "breached");
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_wps_fixed_reports_are_csv_of_declared_fields_masked_and_every_export_is_an_audit_fact(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var file = await WpsRun.Seed(store, console: true);
        await WpsRun.Seed(store, tenant: "other-bank", fileId: "0000001234567261117083333");
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var host = new Host(store);
        var op = As(host, "op-1", $"{Bank}:Operator");
        var list = (await Json(await op.GetAsync("/ops/api/reports/fixed"))).AsArray();
        Assert.Equal(["sif-summary", "sif-detail", "upload-history"], list.Select(r => (string?)r!["id"]));

        async Task<(string[] lines, HttpResponseMessage r)> Csv(string id)
        {
            var r = await op.GetAsync($"/ops/api/reports/fixed/uae.wps.view.operations.v1/{id}.csv");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return ((await r.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries), r);
        }
        var (summary, rs) = await Csv("sif-summary");
        Assert.StartsWith("Transaction id,File name,SIF file id,Corporate name,MOL establishment id", summary[0]);
        Assert.Equal(2, summary.Length);
        Assert.Contains(file, summary[1]);
        Assert.DoesNotContain("0000001234567,", summary[1]);
        var (detail, _) = await Csv("sif-detail");
        Assert.Equal(3, detail.Length);
        Assert.DoesNotContain("00001234567890", string.Join("\n", detail));
        Assert.DoesNotContain("AE070331234567890123456", string.Join("\n", detail));
        var (history, _) = await Csv("upload-history");
        Assert.Equal(2, history.Length);
        Assert.StartsWith($"{WpsRun.FileId}.SIF,", history[1]);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync("/ops/api/reports/fixed/uae.wps.view.operations.v1/payroll.csv")).StatusCode);

        var exports = (await Facts(store, ConsoleRecord.Subject)).Where(f => f.Kind == ConsoleRecord.ExportKind).ToList();
        Assert.Equal(3, exports.Count);
        Assert.Equal(["uae.wps.view.operations.v1/sif-summary", "uae.wps.view.operations.v1/sif-detail", "uae.wps.view.operations.v1/upload-history"], exports.Select(f => (string?)JsonNode.Parse(f.DataJson)!["filter"]!["report"]));
        Assert.Equal([1, 2, 1], exports.Select(f => (int)JsonNode.Parse(f.DataJson)!["rows"]!));
        Assert.Equal(exports[0].Seq.ToString(), rs.Headers.GetValues("x-th-export-fact").Single());
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task An_ended_payment_is_due_after_the_banks_retention_period_and_erased_only_on_consent_and_never_under_a_legal_hold(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var file = await WpsRun.Seed(store, console: true);
        var open = await WpsRun.Seed(store, fileId: "0000001234567261117083002");
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var host = new Host(store);
        var sup = As(host, "sup-1", $"{Bank}:Supervisor");
        var approver = As(host, "ba-1", $"{Bank}:BankApprover");
        var op = As(host, "op-1", $"{Bank}:Operator");
        var before = await AllFacts(store);

        // P0D, the default: nothing is due.
        Assert.Equal(0, (int)(await Json(await sup.GetAsync("/ops/api/retention/due")))["total"]!);
        await Dial(store, "uae.wps.view.operations.v1#retentionPeriod", "PT1S", DateTimeOffset.UtcNow.AddDays(-1));
        await Task.Delay(1100);
        var due = await Json(await sup.GetAsync("/ops/api/retention/due"));
        Assert.Equal(file, (string?)Assert.Single(due["rows"]!.AsArray())!["id"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/ops/api/retention/due")).StatusCode);

        // An open payment is not erased; an operator may not ask.
        Assert.Equal(HttpStatusCode.Conflict, (await sup.PostAsJsonAsync("/ops/api/erasures", new { subject = open, remarks = "retention" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/ops/api/erasures", new { subject = file, remarks = "retention" })).StatusCode);
        // A legal hold blocks it, when asked and at the consent.
        var ask = await Json(await sup.PostAsJsonAsync("/ops/api/erasures", new { subject = file, remarks = "past the retention period" }));
        Assert.Equal("retention", (string?)ask["details"]!["basis"]);
        var hold = await Json(await sup.PostAsJsonAsync("/ops/api/legal-holds", new { subject = file, remarks = "matter LH-0001" }));
        Assert.Equal(HttpStatusCode.Conflict, (await approver.PostAsJsonAsync($"/ops/api/system-actions/{(string)ask["action"]!}/decision", new { approve = true, remarks = "retention" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sup.PostAsJsonAsync("/ops/api/erasures", new { subject = file, remarks = "again" })).StatusCode);
        Assert.True((bool)(await Json(await sup.GetAsync("/ops/api/retention/due")))["rows"]![0]!["legalHold"]!);
        // Releasing the hold waits for a bank approver, another person.
        var release = await Json(await sup.PostAsJsonAsync($"/ops/api/legal-holds/{(string)hold["id"]!}/release", new { remarks = "matter closed" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await sup.PostAsJsonAsync($"/ops/api/system-actions/{(string)release["action"]!}/decision", new { approve = true, remarks = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/ops/api/system-actions/{(string)release["action"]!}/decision", new { approve = true, remarks = "closure letter seen" })).StatusCode);
        Assert.Empty((await Json(await sup.GetAsync($"/ops/api/legal-holds?subject={file}"))).AsArray());

        var ask2 = await Json(await sup.PostAsJsonAsync("/ops/api/erasures", new { subject = file, remarks = "past the retention period" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await sup.PostAsJsonAsync($"/ops/api/system-actions/{(string)ask2["action"]!}/decision", new { approve = true, remarks = "mine" })).StatusCode);
        var done = await Json(await approver.PostAsJsonAsync($"/ops/api/system-actions/{(string)ask2["action"]!}/decision", new { approve = true, remarks = "retention period passed" }));
        Assert.Equal("done", (string?)done["status"]);

        // The read model shows every masked field as erased; the kept documents and unmasking are refused; nothing is due now.
        var shown = await Json(await op.GetAsync($"/ops/api/payments/{file}"));
        Assert.Equal(ViewEval.Erased, (string?)shown["payment"]!["fields"]!["debitAccount"]);
        Assert.Equal(ViewEval.Erased, (string?)shown["payment"]!["fields"]!["corporateName"]);
        Assert.Equal(ViewEval.Erased, (string?)shown["lines"]![0]!["fields"]!["employeeId"]);
        Assert.Equal(WpsRun.FileId, (string?)shown["payment"]!["fields"]!["sifFileId"]);
        var doc = (string)shown["documents"]![0]!["id"]!;
        Assert.Equal(HttpStatusCode.Gone, (await op.GetAsync($"/ops/api/payments/{file}/documents/{doc}")).StatusCode);
        await Dial(store, "p.view.operations.v1#unmaskWindow", "PT1H", DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Equal(HttpStatusCode.Gone, (await op.PostAsJsonAsync("/ops/api/unmask", new { subject = file, field = "subject:debitAccount", remarks = "x" })).StatusCode);
        Assert.Equal(0, (int)(await Json(await sup.GetAsync("/ops/api/retention/due")))["total"]!);
        // A rebuild of the read model gives the same.
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.Rebuild();
        Assert.Equal(ViewEval.Erased, (string?)(await Json(await op.GetAsync($"/ops/api/payments/{file}")))["payment"]!["fields"]!["debitAccount"]);

        // Insert-only: every fact there was before is there unchanged; the erasure is a tombstone on the payment's chain.
        var after = await AllFacts(store);
        Assert.Equal(before, after.Take(before.Length));
        var tomb = Assert.Single(await Facts(store, file), f => f.Kind == Erasure.FactKind);
        Assert.Equal("operator:ba-1", tomb.Actor);
        Assert.Equal("operator:sup-1", (string?)JsonNode.Parse(tomb.DataJson)!["requestedBy"]);
        await using var db = StoreDbContext.Open(store);
        Assert.True((await new FactLog(db).Verify(file)).Ok);
        Assert.True((await new FactLog(db).Verify(ConsoleRecord.Subject)).Ok);
    }

    [Fact]
    public async Task A_channel_asks_core_banking_for_a_balance_the_accounts_and_a_charge_preview_of_a_registered_corporate_only()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var regs = new RegisterService(store, RegisterCatalog.Load());
        var corporate = new JsonObject
        {
            ["corporateId"] = "C-0001", ["name"] = "Example Trading LLC", ["accounts"] = new JsonArray("AE070331234567890123456"), ["cif"] = "CIF000111",
            ["active"] = true, ["establishmentId"] = "0000001234567", ["branch"] = "001",
        };
        var draft = await regs.Draft(Bank, "corporate", "operator:maker-1", "console", [new RegisterInput(corporate)], "onboarding");
        await regs.Decide(draft!.Id, "operator:approver-1", true);
        var banks = Directory.CreateTempSubdirectory("th-banks-");
        try
        {
            Directory.CreateDirectory(Path.Combine(banks.FullName, Bank));
            File.WriteAllText(Path.Combine(banks.FullName, Bank, "core-banking.yaml"), """
                kind: port-bindings
                tenant: example-bank
                bindings:
                  - port: p.port.core-banking.v1
                    adapter: simulator
                    validFrom: 2026-01-01T00:00:00Z
                    settings:
                      openingBalance: "250000.50"
                      currency: AED
                      accounts.CIF000111: AE070331234567890123456,AE260331234567890123457
                      chargeFixed: "10"
                      chargePerRecord: "1.5"
                      vatRate: "0.05"
                """);
            await using var host = new Host(store, b => b.UseSetting("Operations:Banks", banks.FullName));
            var channel = As(host, "channel-ib", $"{Bank}:channel-enquiry");
            var op = As(host, "op-1", $"{Bank}:Operator");

            Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/channel/accounts/balance", new { corporateId = "C-0001", account = "AE070331234567890123456" })).StatusCode);
            var bal = await Json(await channel.PostAsJsonAsync("/api/channel/accounts/balance", new { corporateId = "C-0001", account = "AE070331234567890123456" }));
            Assert.Equal(250000.50m, (decimal)bal["available"]!);
            Assert.Equal("simulator", (string?)bal["adapter"]);
            // An account the register does not hold for the corporate, or a corporate it does not hold, is never sent.
            Assert.Equal(HttpStatusCode.NotFound, (await channel.PostAsJsonAsync("/api/channel/accounts/balance", new { corporateId = "C-0001", account = "AE260331234567890123457" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await channel.PostAsJsonAsync("/api/channel/accounts/balance", new { corporateId = "C-9999", account = "AE070331234567890123456" })).StatusCode);

            var accts = await Json(await channel.GetAsync("/api/channel/accounts?corporateId=C-0001"));
            Assert.Equal(["AE070331234567890123456", "AE260331234567890123457"], accts["accounts"]!.AsArray().Select(a => (string?)a!["account"]));
            Assert.Equal([true, false], accts["accounts"]!.AsArray().Select(a => (bool)a!["registered"]!));

            var q = await Json(await channel.GetAsync("/api/channel/charges/preview?corporateId=C-0001&scheme=uae.wps&messageKind=uae.wps.sif&records=2&amount=1500"));
            Assert.Equal(13m, (decimal)q["charge"]!);
            Assert.Equal(0.65m, (decimal)q["vat"]!);
            Assert.True((bool)q["preview"]!);
            Assert.Equal(HttpStatusCode.BadRequest, (await channel.GetAsync("/api/channel/charges/preview?corporateId=C-0001&scheme=uae.wps&messageKind=uae.wps.sif&records=0")).StatusCode);
        }
        finally { banks.Delete(true); }
    }

    [Fact]
    public async Task The_register_api_answers_core_banking_masked_where_the_installation_says_so()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var regs = new RegisterService(store, RegisterCatalog.Load());
        var d = await regs.Draft(Bank, "corporate", "operator:maker-1", "console", [new RegisterInput(new JsonObject { ["corporateId"] = "C-0001", ["name"] = "Example Trading LLC", ["accounts"] = new JsonArray("AE070331234567890123456"), ["cif"] = "CIF000111", ["active"] = true, ["establishmentId"] = "0000001234567" })], "onboarding");
        await regs.Decide(d!.Id, "operator:approver-1", true);
        await using (var plain = new Host(store))
            Assert.Equal("AE070331234567890123456", (string?)(await Json(await As(plain, "cbs", $"{Bank}:register-client").GetAsync("/api/registers/corporate/C-0001")))["record"]!["accounts"]![0]);
        await using var masked = new Host(store, b => b.UseSetting("Operations:Registers:SystemReadsMasked", "true"));
        var core = As(masked, "cbs", $"{Bank}:register-client");
        var one = await Json(await core.GetAsync("/api/registers/corporate/C-0001"));
        Assert.True((bool)one["masked"]!);
        Assert.NotEqual("AE070331234567890123456", (string?)one["record"]!["accounts"]![0]);
        Assert.NotEqual("AE070331234567890123456", (string?)(await Json(await core.GetAsync("/api/registers/corporate?cif=CIF000111")))["rows"]![0]!["accounts"]![0]);
    }
}
