using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TransactHub.Operations.Api;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Operations.Tests;

/// <summary>The operations console's host for the oversight tests: dev sign-in, the views of the content branch's folder.</summary>
sealed class OversightHost(StoreOptions store) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment("Development");
        b.UseSetting("Store:Provider", store.Provider.ToString());
        b.UseSetting("Store:Connection", store.ConnectionString);
        b.UseSetting("Operations:Views", Path.Combine(Repo.Root(), "content-configurations", "artefacts"));
        b.UseSetting("Operations:PollMilliseconds", "100");
        b.UseSetting("Operations:TatSweepSeconds", "3600");
        b.UseSetting("TH_AUTH_DEV", "1");
    }

    public HttpClient As(string sub, params string[] roles)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add(OperatorAuth.DevHeader, new JsonObject { ["sub"] = sub, ["name"] = sub, ["roles"] = new JsonArray(roles.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString());
        return c;
    }

    public static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    /// <summary>A bank's value as a configuration release put in force leaves it (kind config, BankValues).</summary>
    public static async Task Value(StoreOptions store, string tenant, string parameter, JsonNode value, DateTimeOffset from, string set = "1.10.0")
    {
        await using var db = StoreDbContext.Open(store);
        await new VersionedValues(db).Publish(ConfigurationValues.Kind, tenant, parameter, new JsonObject { ["value"] = value, ["configurationSet"] = set }, from, null, "test");
    }
}

/// <summary>
/// The fraud alert list (R-05-90; owner's decision of 23 November 2026, Issue I-189), on SQL Server and PostgreSQL: a payment the
/// fraud engine found suspect is a work item of kind alert, worked like any item, and its payment is released or refused only by
/// the payment's own action with the view's rules. Synthetic data only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FraudAlertTests(Databases dbs)
{
    const string Bank = WpsRun.Tenant;

    static async Task<List<FactRow>> Chain(StoreOptions store, string chain)
    {
        await using var db = StoreDbContext.Open(store);
        return await db.Facts.AsNoTracking().Where(f => f.SubjectId == chain).OrderBy(f => f.Seq).ToListAsync();
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_suspect_payment_is_an_alert_in_the_queue_worked_by_assign_note_and_release_through_the_payments_own_action(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var suspect = await WpsRun.Seed(store, fileId: "0000001234567261123090001", suspect: true);
        var plain = await WpsRun.Seed(store, fileId: "0000001234567261123090002");
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var host = new OversightHost(store);
        var op = host.As("op-1", $"{Bank}:Operator");
        var other = host.As("op-9", "other-bank:Operator");

        // The alert: its reason, its state, the link to its payment; the queue shows it as an alert, not as a payment item too.
        var list = await OversightHost.Json(await op.GetAsync("/ops/api/alerts"));
        var alert = Assert.Single(list["rows"]!.AsArray(), r => (string?)r!["item"] == suspect)!;
        Assert.Equal(("alert", "FRAD", "held", $"/payments/{suspect}"), ((string?)alert["itemKind"], (string?)alert["reason"], (string?)alert["state"], (string?)alert["link"]));
        Assert.DoesNotContain(list["rows"]!.AsArray(), r => (string?)r!["item"] == plain);
        Assert.Empty((await OversightHost.Json(await other.GetAsync("/ops/api/alerts")))["rows"]!.AsArray());
        var queue = (await OversightHost.Json(await op.GetAsync("/ops/api/work/queue")))["rows"]!.AsArray();
        Assert.Contains(queue, r => (string?)r!["itemKind"] == "alert" && (string?)r!["item"] == suspect);
        Assert.DoesNotContain(queue, r => (string?)r!["itemKind"] == "payment" && (string?)r!["item"] == suspect);
        Assert.Contains(queue, r => (string?)r!["itemKind"] == "payment" && (string?)r!["item"] == plain);

        // Worked as any item: taken, noted (a note needs its text), and not closed while its payment is held.
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync("/ops/api/work", new { op = "assign", kind = "alert", id = suspect })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/ops/api/work", new { op = "note", kind = "alert", id = suspect })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync("/ops/api/work", new { op = "note", kind = "alert", id = suspect, remarks = "Called the employer: the file is theirs (synthetic)" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync("/ops/api/work", new { op = "close", kind = "alert", id = suspect, remarks = "done" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/ops/api/work", new { op = "close", kind = "payment", id = plain, remarks = "done" })).StatusCode);

        // Releasing the payment is its own action (approve its hold), as the view offers it; the alert's chain says it was asked.
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/ops/api/alerts/{suspect}/payment-action", new { action = "pay-anyway" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.PostAsJsonAsync($"/ops/api/alerts/{plain}/payment-action", new { action = "release" })).StatusCode);
        // A suspect payment's release needs a supervisor's second approval (R-05-94, Issue I-279): the view's approve for a hold after
        // p.screen.fraud.v1. It waits for another person; the operator who asked may not approve it, a supervisor does.
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/ops/api/alerts/{suspect}/payment-action", new { action = "release" })).StatusCode);
        var released = await OversightHost.Json(await op.PostAsJsonAsync($"/ops/api/alerts/{suspect}/payment-action", new { action = "release", remarks = "The employer confirmed the file (synthetic)" }));
        Assert.Equal(("approve", "pending-approval", "Supervisor"), ((string?)released["asked"], (string?)released["status"], (string?)released["secondApprover"]));
        await using (var db = StoreDbContext.Open(store))
            Assert.Empty(await db.Facts.AsNoTracking().Where(f => f.SubjectId == suspect && f.Kind == "hold" && f.Code == "decided").ToListAsync());
        Assert.Equal("held", (string?)(await OversightHost.Json(await op.GetAsync("/ops/api/alerts")))["rows"]!.AsArray().Single(r => (string?)r!["item"] == suspect)!["state"]);
        var actionId = (string)released["action"]!;
        var otherOperator = host.As("op-3", $"{Bank}:Operator");
        Assert.Equal(HttpStatusCode.Forbidden, (await otherOperator.PostAsJsonAsync($"/ops/api/payments/{suspect}/actions/{actionId}/decision", new { approve = true, remarks = "an operator only" })).StatusCode);
        var sup = host.As("sup-1", $"{Bank}:Operator", $"{Bank}:Supervisor");
        Assert.Equal(HttpStatusCode.OK, (await sup.PostAsJsonAsync($"/ops/api/payments/{suspect}/actions/{actionId}/decision", new { approve = true, remarks = "Second approval (synthetic)" })).StatusCode);
        await using (var db = StoreDbContext.Open(store))
        {
            var decided = await db.Facts.AsNoTracking().Where(f => f.SubjectId == suspect && f.Kind == "hold" && f.Code == "decided").ToListAsync();
            Assert.Equal("approved", (string?)JsonNode.Parse(Assert.Single(decided).DataJson)!["decision"]);
        }
        var after = await OversightHost.Json(await op.GetAsync("/ops/api/alerts?state=all"));
        Assert.Equal("released", (string?)after["rows"]!.AsArray().Single(r => (string?)r!["item"] == suspect)!["state"]);
        Assert.Equal(0, (int)(await OversightHost.Json(await op.GetAsync("/ops/api/alerts")))["open"]!);

        var chain = await Chain(store, new WorkItem("alert", suspect).Chain);
        Assert.Equal(["assigned", "noted", "acted"], chain.Select(f => f.Code));
        Assert.Equal("approve", (string?)JsonNode.Parse(chain[2].DataJson)!["paymentAction"]);
        await using (var db = StoreDbContext.Open(store)) Assert.True((await new FactLog(db).Verify(new WorkItem("alert", suspect).Chain)).Ok);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_refused_suspect_payment_stays_an_alert_until_closed_and_a_held_one_is_not_closed(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var suspect = await WpsRun.Seed(store, fileId: "0000001234567261123090003", suspect: true);
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var host = new OversightHost(store);
        var op = host.As("op-2", $"{Bank}:Operator");

        // Refusing is the payment's own decline, which needs remarks as the view says.
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/ops/api/alerts/{suspect}/payment-action", new { action = "refuse" })).StatusCode);
        var refused = await OversightHost.Json(await op.PostAsJsonAsync($"/ops/api/alerts/{suspect}/payment-action", new { action = "refuse", remarks = "The engine's finding is confirmed (synthetic)" }));
        Assert.Equal("decline", (string?)refused["asked"]);
        var alert = (await OversightHost.Json(await op.GetAsync("/ops/api/alerts")))["rows"]!.AsArray().Single(r => (string?)r!["item"] == suspect)!;
        Assert.Equal("refused", (string?)alert["state"]);
        // A refused alert stays work until it is closed with a note; then it leaves the queue.
        Assert.Contains((await OversightHost.Json(await op.GetAsync("/ops/api/work/queue")))["rows"]!.AsArray(), r => (string?)r!["item"] == suspect);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/ops/api/work", new { op = "close", kind = "alert", id = suspect })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync("/ops/api/work", new { op = "close", kind = "alert", id = suspect, remarks = "Reported to the bank's fraud team (synthetic)" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync("/ops/api/work", new { op = "close", kind = "alert", id = suspect, remarks = "again" })).StatusCode);
        Assert.DoesNotContain((await OversightHost.Json(await op.GetAsync("/ops/api/work/queue")))["rows"]!.AsArray(), r => (string?)r!["item"] == suspect);
        Assert.True((bool)(await OversightHost.Json(await op.GetAsync("/ops/api/alerts?state=all")))["rows"]!.AsArray().Single(r => (string?)r!["item"] == suspect)!["closed"]!);
    }
}

/// <summary>The bank's ledger accounts for supervisors, read-only (R-05-91; docs/OWNER-ACTIONS.md A3, Issue I-184). Synthetic accounts.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class LedgerAccountTests(Databases dbs)
{
    const string Bank = "ledger-bank";

    [Fact]
    public async Task A_supervisor_reads_the_banks_ledger_accounts_as_its_configuration_names_them_and_no_one_else_does()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var from = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        await OversightHost.Value(store, Bank, "p.post.accounting.v2#settlementAccount", JsonValue.Create("GL-NOSTRO-0001")!, from);
        await OversightHost.Value(store, Bank, "p.post.accounting.v2#suspenseAccount", JsonValue.Create("GL-SUSPENSE-0002")!, from);
        await OversightHost.Value(store, Bank, "uae.wps.post.return.v1#settlementAccount", JsonValue.Create("GL-NOSTRO-WPS-0003")!, from);
        await OversightHost.Value(store, Bank, "p.post.accounting.v2#transactionCode", JsonValue.Create("TX01")!, from);
        await using var host = new OversightHost(store);

        var sup = host.As("sup-1", $"{Bank}:Supervisor");
        var answer = await OversightHost.Json(await sup.GetAsync($"/ops/api/ledger-accounts?tenant={Bank}"));
        var bank = answer["banks"]!.AsArray().Single()!;
        Assert.True((bool)bank["readOnly"]!);
        var rows = bank["accounts"]!.AsArray().Select(r => ((string?)r!["parameter"], (string?)r!["account"], (bool)r!["set"]!)).ToList();
        Assert.Contains(("p.post.accounting.v2#settlementAccount", "GL-NOSTRO-0001", true), rows);
        Assert.Contains(("p.post.accounting.v2#suspenseAccount", "GL-SUSPENSE-0002", true), rows);
        // An account the configuration does not set is shown as not set, never guessed.
        Assert.Contains(("p.post.accounting.v2#vatAccount", null, false), rows);
        Assert.Contains(("uae.wps.post.return.v1#settlementAccount", "GL-NOSTRO-WPS-0003", true), rows);
        Assert.DoesNotContain(rows, r => r.Item1!.EndsWith("#transactionCode"));
        Assert.Equal(["income", "settlement", "suspense", "vat"], bank["accounts"]!.AsArray().Select(r => (string)r!["kind"]!).Distinct().Order());

        // Not an operator's, not another bank's supervisor's, not a channel's; and the console offers no way to change them.
        Assert.Equal(HttpStatusCode.Forbidden, (await host.As("op-1", $"{Bank}:Operator").GetAsync($"/ops/api/ledger-accounts?tenant={Bank}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.As("sup-9", "other-bank:Supervisor").GetAsync($"/ops/api/ledger-accounts?tenant={Bank}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.As("ch-1", $"{Bank}:channel-enquiry").GetAsync($"/ops/api/ledger-accounts?tenant={Bank}")).StatusCode);
        // The channel's API has no such enquiry (whatever answers that path, no account is in it).
        Assert.DoesNotContain("GL-NOSTRO", await (await host.As("ch-1", $"{Bank}:channel-enquiry").GetAsync("/api/channel/ledger-accounts")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await sup.PutAsJsonAsync("/ops/api/ledger-accounts", new { account = "GL-X" })).StatusCode);
    }

    /// <summary>
    /// R-05-100, Issue I-282: the view lists every parameter an active distribution of the bank marks as a ledger account
    /// (<c>role: ledger-account</c>, with its kind), whatever its name: here a scheme's charge account and a nostro per currency the
    /// four names do not know. A parameter not so marked is not an account, and a distribution not active adds nothing.
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_view_lists_every_parameter_the_active_distribution_marks_as_a_ledger_account(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        var bank = $"ledger-{Guid.NewGuid():N}"[..14];
        const string activity = """
            kind: activity
            name: x.post.scheme-charge.v1
            release: 1.1.0
            parameters:
              schemeChargeAccount: { type: string, default: GL-0, role: ledger-account, ledgerKind: scheme-charge, description: The scheme's charge account. }
              nostroUsd: { type: string, default: GL-0, role: ledger-account, ledgerKind: settlement }
              narration: { type: string, default: Payment }
            """;
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        await using (var s = z.CreateEntry("engine/p.wf.corporate.v1/activities/x.post.scheme-charge.v1@1.1.0.yaml").Open())
            await s.WriteAsync(System.Text.Encoding.UTF8.GetBytes(activity));
        var bytes = ms.ToArray();
        await using (var db = StoreDbContext.Open(store))
        {
            var reg = new DistributionRegistry(db);
            await reg.Store(bank, "2026.11.24-1", bytes, new JsonObject { ["bank"] = bank }, FileStore.Sha256Hex(bytes), "test", "test");
            await reg.Activate(DistributionRegistry.IdOf(bank, "2026.11.24-1"), "test", "test", "test");
        }
        await OversightHost.Value(store, bank, "x.post.scheme-charge.v1#schemeChargeAccount", JsonValue.Create("GL-SCH-0001")!, DateTimeOffset.Parse("2026-11-01T00:00:00Z"));
        var view = await new LedgerAccounts(store).For(bank);
        var rows = view["accounts"]!.AsArray().Select(r => ((string?)r!["kind"], (string?)r!["parameter"], (string?)r!["account"], (string?)r!["title"])).ToList();
        Assert.Contains(("scheme-charge", "x.post.scheme-charge.v1#schemeChargeAccount", "GL-SCH-0001", "The scheme's charge account."), rows);
        Assert.Contains(("settlement", "x.post.scheme-charge.v1#nostroUsd", null, "Settlement (nostro)"), rows);
        Assert.DoesNotContain(rows, r => r.Item2!.EndsWith("#narration"));
        // The platform's templates are still listed by their four names, set or not.
        Assert.Contains(rows, r => r.Item2 == "p.post.accounting.v1#vatAccount");
    }
}

/// <summary>The console's "rates stale" warning (R-05-92, R-09-178; Issue I-189). Synthetic rates.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class FxRateStatusTests(Databases dbs)
{
    const string Bank = "fx-status-bank";

    [Fact]
    public async Task The_console_says_rates_stale_once_a_rate_is_older_than_the_banks_limit()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var now = DateTimeOffset.UtcNow;
        var rates = new JsonArray(
            new JsonObject { ["from"] = "AED", ["to"] = "USD", ["rate"] = 0.2723m },
            new JsonObject { ["from"] = "AED", ["to"] = "EUR", ["rate"] = 0.2511m, ["asOf"] = now.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ssZ") });
        await OversightHost.Value(store, Bank, FxRateStatus.Rates, rates, now.AddDays(-1));
        await using var host = new OversightHost(store);
        var op = host.As("op-1", $"{Bank}:Operator");

        // Two hours old against the default limit of a day: fresh; the rate set by hand is listed and not aged.
        var fresh = await OversightHost.Json(await op.GetAsync($"/ops/api/fx-rates?tenant={Bank}"));
        Assert.False((bool)fresh["stale"]!);
        var b = fresh["banks"]!.AsArray().Single()!;
        Assert.Equal(("PT24H", "default"), ((string?)b["limit"], (string?)b["limitSource"]));
        Assert.True((bool)b["rates"]!.AsArray().Single(r => (string?)r!["pair"] == "AED/USD")!["standing"]!);

        // The bank tightens its limit to one hour: the same rate is stale, named with its pair and the code a payment would refuse it with.
        await OversightHost.Value(store, Bank, FxRateStatus.Limit, JsonValue.Create("PT1H")!, now.AddDays(-1));
        var stale = await OversightHost.Json(await op.GetAsync($"/ops/api/fx-rates?tenant={Bank}"));
        Assert.True((bool)stale["stale"]!);
        var s = stale["banks"]!.AsArray().Single()!;
        Assert.Equal(["AED/EUR"], s["stalePairs"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(("fx.RateStale", "bank"), ((string?)s["code"], (string?)s["limitSource"]));
        Assert.Equal(HttpStatusCode.Forbidden, (await host.As("op-9", "other-bank:Operator").GetAsync($"/ops/api/fx-rates?tenant={Bank}")).StatusCode);
    }

    [Fact]
    public void The_consoles_default_limit_is_the_activitys_declared_default()
    {
        var act = (JsonObject)Yaml.ToJson(File.ReadAllText(Path.Combine(Repo.Root(), "spec", "factory", "p", "activities", "p.enrich.fx-rate.v1.yaml")))!;
        Assert.Equal((string?)act["parameters"]!["maxRateAge"]!["default"], FxRateStatus.DefaultLimit);
        Assert.Equal(FxRateStatus.DefaultLimit, TransactHub.Activities.Platform.Native.EnrichFxRate.DefaultMaxRateAge);
    }
}
