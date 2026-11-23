using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Native;
using TransactHub.Operations.Api;
using TransactHub.Operations.Registers;
using TransactHub.Ports;
using TransactHub.Store;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The bank's registers (R-05-62 to R-05-64, R-09-73, R-12-27), the channel enquiry APIs (R-09-74), on SQL Server and PostgreSQL:
/// a change is a maker's draft that takes effect only on a different bank approver's consent; every version is kept and shown,
/// masked; core banking reads and syncs as a system client; the party register lookup reads the register as of the step and
/// WPS matches it with the source's MatchCorporate. Synthetic corporates only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RegisterTests(Databases dbs)
{
    const string Bank = "example-bank";
    const string Corp = "C-0001";
    const string Mol = "0000001234567";
    const string Account = "AE070331234567890123456";

    sealed class Clock(DateTimeOffset t) : TimeProvider
    {
        public DateTimeOffset Now = t;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static JsonObject Corporate(string id = Corp, string mol = Mol, bool active = true, bool wps = true, string account = Account) => new()
    {
        ["corporateId"] = id, ["name"] = "Example Trading LLC", ["emirate"] = "AE-DU", ["telephone"] = "+97140000000",
        ["emails"] = new JsonArray("payroll@example.invalid"), ["accounts"] = new JsonArray(account), ["cif"] = "CIF000111",
        ["active"] = active, ["wpsRegistrationFormHeld"] = wps, ["establishmentId"] = mol, ["contactPerson"] = "Example Person",
        ["licenceExpiry"] = "2027-12-31", ["branch"] = "001",
    };

    static RegisterService Service(StoreOptions s, TimeProvider clock) => new(s, RegisterCatalog.Load(), clock);

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_change_takes_effect_only_when_a_different_bank_approver_consents_and_every_version_is_kept(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        var clock = new Clock(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        var regs = Service(store, clock);

        var draft = await regs.Draft(Bank, "corporate", "operator:maker-1", "console", [new RegisterInput(Corporate())], "onboarding");
        Assert.NotNull(draft);
        clock.Now = clock.Now.AddMinutes(5);
        Assert.Null(await regs.InForce(Bank, "corporate", Corp, clock.Now));
        Assert.Empty(await regs.FindBy(Bank, "corporate", "establishmentId", Mol, clock.Now));
        // A draft waits: another change to the same corporate is refused until it is decided.
        Assert.Equal(409, (await Assert.ThrowsAsync<RegisterRefused>(() => regs.Draft(Bank, "corporate", "operator:maker-2", "console", [new RegisterInput(new JsonObject { ["corporateId"] = Corp, ["name"] = "Other" })]))).Status);
        // Its maker may not consent to it.
        Assert.Equal(403, (await Assert.ThrowsAsync<RegisterRefused>(() => regs.Decide(draft!.Id, "operator:maker-1", true))).Status);
        Assert.Single(await regs.Pending([Bank]));

        await regs.Decide(draft!.Id, "operator:approver-1", true, "checked against the registration form");
        Assert.Equal(409, (await Assert.ThrowsAsync<RegisterRefused>(() => regs.Decide(draft.Id, "operator:approver-2", false))).Status);
        clock.Now = clock.Now.AddMinutes(1);
        var inForce = await regs.InForce(Bank, "corporate", Corp, clock.Now);
        Assert.Equal("Example Trading LLC", (string?)inForce!["name"]);
        Assert.Equal(Corp, (string?)(await regs.FindBy(Bank, "corporate", "cif", "CIF000111", clock.Now)).Single()["corporateId"]);
        Assert.Single(await regs.FindBy(Bank, "corporate", "accounts", Account, clock.Now));
        Assert.Empty(await regs.FindBy(Bank, "corporate", "establishmentId", Mol, clock.Now.AddHours(-1)));

        // A change gives only the fields it changes; a rejected change never takes effect.
        var rename = await regs.Draft(Bank, "corporate", "operator:maker-1", "console", [new RegisterInput(new JsonObject { ["corporateId"] = Corp, ["name"] = "Example Trading Two LLC" })]);
        await regs.Decide(rename!.Id, "operator:approver-1", false, "no evidence");
        Assert.Equal("Example Trading LLC", (string?)(await regs.InForce(Bank, "corporate", Corp, clock.Now.AddMinutes(1)))!["name"]);
        // A change from a later date takes effect from that date only.
        var later = await regs.Draft(Bank, "corporate", "operator:maker-1", "console", [new RegisterInput(new JsonObject { ["corporateId"] = Corp, ["active"] = false }, clock.Now.AddDays(10))]);
        await regs.Decide(later!.Id, "operator:approver-1", true);
        Assert.True((bool?)(await regs.InForce(Bank, "corporate", Corp, clock.Now.AddDays(1)))!["active"]);
        Assert.False((bool?)(await regs.InForce(Bank, "corporate", Corp, clock.Now.AddDays(11)))!["active"]);
        Assert.Equal("CIF000111", (string?)(await regs.InForce(Bank, "corporate", Corp, clock.Now.AddDays(11)))!["cif"]);

        // The history: every version, newest first, with its state, masked as the operations views mask.
        var history = await regs.History(Bank, "corporate", Corp);
        Assert.Equal(["approved", "rejected", "approved"], history.Select(h => h.State));
        var first = history[^1].Fields;
        Assert.Equal("CIF0****0111", (string?)first["cif"]);
        Assert.Equal("E*************", (string?)first["contactPerson"]);
        Assert.Equal("AE07" + new string('*', 15) + "3456", (string?)first["accounts"]![0]);
        Assert.Equal("Example Trading LLC", (string?)first["name"]);
        Assert.DoesNotContain("Example Person", (await regs.History(Bank, "corporate", Corp)).Select(h => h.Fields.ToJsonString()).Aggregate((a, b) => a + b));

        // Insert-only: a version is never changed.
        await using var db = StoreDbContext.Open(store);
        var v = await db.RegisterVersions.FirstAsync();
        v.DataJson = "{}";
        await Assert.ThrowsAsync<InsertOnlyViolation>(() => db.SaveChangesAsync());
        // Each draft and decision is a fact on the change's own chain.
        db.ChangeTracker.Clear();
        Assert.Equal(["register.drafted", "register.approved"], await db.Facts.AsNoTracking().Where(f => f.SubjectId == draft.Id).OrderBy(f => f.Seq).Select(f => f.Kind).ToListAsync());
    }

    [Fact]
    public async Task A_record_is_checked_against_its_kind_and_a_sync_drafts_only_what_changed_once_per_reference()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var clock = new Clock(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        var regs = Service(store, clock);
        var bad = Corporate();
        bad["licenceExpiry"] = "31/12/2027";
        bad["shoeSize"] = "42";
        bad.Remove("name");
        var e = await Assert.ThrowsAsync<RegisterRefused>(() => regs.Draft(Bank, "corporate", "system:core", "sync", [new RegisterInput(bad)]));
        Assert.Equal(422, e.Status);
        Assert.Contains("name is required", e.Message);
        Assert.Contains("licenceExpiry is not a date", e.Message);
        Assert.Contains("shoeSize is not a field", e.Message);

        var first = await regs.Draft(Bank, "corporate", "system:core", "sync", [new RegisterInput(Corporate()), new RegisterInput(Corporate("C-0002", "0000007654321"))], reference: "SYNC-1");
        Assert.Equal(2, first!.Count);
        Assert.Equal(first.Id, (await regs.Draft(Bank, "corporate", "system:core", "sync", [new RegisterInput(Corporate())], reference: "SYNC-1"))!.Id);
        await regs.Decide(first.Id, "operator:approver-1", true);
        var changed = Corporate("C-0002", "0000007654321");
        changed["telephone"] = "+97140000001";
        var second = await regs.Draft(Bank, "corporate", "system:core", "sync", [new RegisterInput(Corporate()), new RegisterInput(changed)], reference: "SYNC-2");
        Assert.Equal(1, second!.Count);
        Assert.Null(await regs.Draft(Bank, "corporate", "system:core", "sync", [new RegisterInput(Corporate())], reference: "SYNC-3"));
    }

    // ------------------------------------------------------------------ the API

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

    static HttpClient As(WebApplicationFactory<Program> host, string sub, params string[] roles)
    {
        var c = host.CreateClient();
        c.DefaultRequestHeaders.Add(OperatorAuth.DevHeader, new JsonObject { ["sub"] = sub, ["name"] = sub, ["roles"] = new JsonArray(roles.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString());
        return c;
    }

    static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task The_console_and_the_register_API_need_the_roles_and_a_change_needs_a_different_bank_approver()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        await using var host = new Host(store);
        var maker = As(host, "maker-1", $"{Bank}:RegisterMaker");
        var approver = As(host, "approver-1", $"{Bank}:BankApprover");
        var makerApprover = As(host, "both-1", $"{Bank}:RegisterMaker", $"{Bank}:BankApprover");
        var operatorOnly = As(host, "op-1", $"{Bank}:Operator");
        var otherBank = As(host, "maker-9", "other-bank:RegisterMaker", "other-bank:BankApprover");
        var core = As(host, "core-banking", $"{Bank}:register-client");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/ops/api/registers")).StatusCode);
        var kinds = await Json(await operatorOnly.GetAsync("/ops/api/registers"));
        Assert.Equal("corporate", (string?)kinds["kinds"]![0]!["name"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorOnly.PostAsJsonAsync("/ops/api/registers/corporate/changes", new { records = new[] { new { fields = Corporate() } } })).StatusCode);

        var made = await maker.PostAsJsonAsync("/ops/api/registers/corporate/changes", new { records = new[] { new { fields = Corporate() } }, remarks = "new corporate" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var change = (string)(await Json(made))["id"]!;
        var pending = await Json(await approver.GetAsync("/ops/api/registers/changes/pending"));
        Assert.True((bool)pending[0]!["mayDecide"]!);
        Assert.Equal("0000*****4567", (string?)pending[0]!["records"]![0]!["fields"]!["establishmentId"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.PostAsJsonAsync($"/ops/api/registers/changes/{change}/decision", new { approve = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherBank.PostAsJsonAsync($"/ops/api/registers/changes/{change}/decision", new { approve = true })).StatusCode);
        var before = await Json(await operatorOnly.GetAsync($"/ops/api/registers/corporate/{Corp}"));
        Assert.Null(before["current"]);
        Assert.Equal("pending", (string?)before["history"]![0]!["state"]);
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/ops/api/registers/changes/{change}/decision", new { approve = true, remarks = "form seen" })).StatusCode);

        var one = await Json(await operatorOnly.GetAsync($"/ops/api/registers/corporate/{Corp}"));
        Assert.Equal("CIF0****0111", (string?)one["current"]!["cif"]);
        Assert.Equal("approved", (string?)one["history"]![0]!["state"]);
        Assert.Equal(1, (int)(await Json(await operatorOnly.GetAsync("/ops/api/registers/corporate?q=Example")))["total"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherBank.GetAsync($"/ops/api/registers/corporate?tenant={Bank}")).StatusCode);

        // One person who holds both roles makes a change, and may not consent to their own.
        var own = await makerApprover.PostAsJsonAsync("/ops/api/registers/corporate/changes", new { records = new[] { new { fields = new JsonObject { ["corporateId"] = Corp, ["branch"] = "002" } } } });
        var ownId = (string)(await Json(own))["id"]!;
        Assert.False((bool)(await Json(await makerApprover.GetAsync("/ops/api/registers/changes/pending")))[0]!["mayDecide"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await makerApprover.PostAsJsonAsync($"/ops/api/registers/changes/{ownId}/decision", new { approve = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/ops/api/registers/changes/{ownId}/decision", new { approve = false })).StatusCode);

        // Core banking as a system client: it reads unmasked by its key and lookup fields, and syncs once per reference.
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorOnly.GetAsync($"/api/registers/corporate/{Corp}")).StatusCode);
        Assert.Equal("CIF000111", (string?)(await Json(await core.GetAsync($"/api/registers/corporate/{Corp}")))["record"]!["cif"]);
        Assert.Equal(Corp, (string?)(await Json(await core.GetAsync($"/api/registers/corporate?establishmentId={Mol}")))["rows"]![0]!["corporateId"]);
        Assert.Equal(Corp, (string?)(await Json(await core.GetAsync("/api/registers/corporate?cif=CIF000111")))["rows"]![0]!["corporateId"]);
        var sync = await core.PostAsJsonAsync("/api/registers/corporate/sync", new { reference = "CBS-2026-11-24", records = new[] { new { fields = Corporate() }, new { fields = Corporate("C-0002", "0000007654321") } } });
        Assert.Equal(HttpStatusCode.Accepted, sync.StatusCode);
        var synced = await Json(sync);
        Assert.Equal((1, 1), ((int)synced["drafted"]!, (int)synced["unchanged"]!));
        Assert.Equal((string?)synced["change"], (string?)(await Json(await core.PostAsJsonAsync("/api/registers/corporate/sync", new { reference = "CBS-2026-11-24", records = new[] { new { fields = Corporate("C-0002", "0000007654321") } } })))["change"]);
        Assert.Equal(HttpStatusCode.NotFound, (await core.GetAsync("/api/registers/corporate/C-0002")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/ops/api/registers/changes/{(string)synced["change"]!}/decision", new { approve = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await core.GetAsync("/api/registers/corporate/C-0002")).StatusCode);
    }

    [Fact]
    public async Task A_channel_asks_a_payments_or_a_files_status_in_pain_002_words_and_searches_its_own_banks_only()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var file = await WpsRun.Seed(store, hold: false, ack: "nak");
        var other = await WpsRun.Seed(store, tenant: "other-bank", fileId: "0000001234567261117083333");
        await using var host = new Host(store);
        var channel = As(host, "channel-ib", $"{Bank}:channel-enquiry");
        var none = As(host, "op-1", $"{Bank}:Operator");

        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync($"/api/channel/payments/{file}/status")).StatusCode);
        JsonNode? status = null;
        for (var i = 0; i < 200 && status is null; i++)
        {
            var r = await channel.GetAsync($"/api/channel/batches/{file}/status");
            // The projection fills the legs as it catches up: wait for the scheme's acknowledgement field.
            if (r.IsSuccessStatusCode && await Json(r) is { } j && j["scheme"]?["fields"]?["cbResponse"] is not null) status = j; else await Task.Delay(100);
        }
        Assert.NotNull(status);
        Assert.Equal(file, (string?)status!["transactionId"]);
        Assert.Equal(WpsRun.FileId, (string?)status["originalGroupInformationAndStatus"]!["originalMessageId"]);
        Assert.NotNull(status["originalPaymentInformationAndStatus"]![0]!["transactionInformationAndStatus"]);
        Assert.Equal("uae.wps", (string?)status["scheme"]!["scope"]);
        Assert.Contains((string?)status["scheme"]!["status"], new[] { "PENDING", "ACCEPTED", "REJECTED" });
        Assert.Equal("nak", (string?)status["scheme"]!["fields"]!["cbResponse"]!["value"]);
        Assert.Equal(file, (string?)(await Json(await channel.GetAsync($"/api/channel/payments/{WpsRun.FileId}/status")))["transactionId"]);
        Assert.Equal(HttpStatusCode.NotFound, (await channel.GetAsync($"/api/channel/payments/{other}/status")).StatusCode);
        var found = await Json(await channel.GetAsync("/api/channel/payments?reference=083000&scheme=uae.wps"));
        Assert.Equal(1, (int)found["total"]!);
        Assert.Equal(0, (int)(await Json(await channel.GetAsync("/api/channel/payments?reference=083333")))["total"]!);
    }

    // ------------------------------------------------------------------ the party register lookup

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_party_register_lookup_reads_the_register_as_of_the_step_and_WPS_matches_it_with_the_sources_rules(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        var clock = new Clock(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        var regs = Service(store, clock);
        var c = await regs.Draft(Bank, "corporate", "operator:maker-1", "console",
            [new RegisterInput(Corporate()), new RegisterInput(Corporate("C-0002", "0000002222222", wps: false)), new RegisterInput(Corporate("C-0003", "0000003333333", active: false))]);
        await regs.Decide(c!.Id, "operator:approver-1", true);
        var bindings = new PortBindings();
        bindings.Add($"kind: port-bindings\ntenant: {Bank}\nbindings:\n  - port: p.port.party-register.v1\n    adapter: register\n    validFrom: 2026-01-01T00:00:00Z\n");
        RegisterReader reader = async (tenant, kind, field, value, at, ct) => await regs.FindBy(tenant, kind, field, value, at, ct);
        // The platform module's lookup on the image's register port; WPS's match is its module's contribution (native contract 1.1.0).
        var act = new TransactHub.Activities.Platform.Native.EnrichPartyRegister(new NativePorts(bindings, new MemoryNativeFiles(), reader),
            key => key == $"party-matcher:{WpsCorporateMatch.Scheme}" ? (PartyMatcher)WpsCorporateMatch.Match : null);
        var now = clock.Now.AddMinutes(1);
        NativeResult Ask(string mol, string? account = null, string scheme = "uae.wps") => act.Run(new NativeAttempt(act.Name, "1.0.0", new JsonObject
        {
            ["header"] = new JsonObject { ["scheme"] = scheme },
            ["batch"] = new JsonObject { ["debtor"] = new JsonObject { ["organisationId"] = new JsonObject { ["id"] = mol, ["scheme"] = "MOL" } }, ["debtorAccount"] = account is null ? null : new JsonObject { ["iban"] = account } },
        }, new JsonObject(), now, Bank, "batch"));

        var ok = Ask(Mol, Account);
        Assert.Equal(("registered", (string?)null), (ok.Outcome, ok.ReasonCode));
        Assert.True((bool)ok.Writes["facts.partyRegister"]!["found"]!);
        Assert.DoesNotContain("Example Trading", ok.Writes.ToJsonString());
        var unknown = Ask("0000009999999");
        Assert.Equal(("not-registered", "RR04", "003"), (unknown.Outcome, unknown.ReasonCode, (string?)unknown.Writes["facts.partyRegister"]!["schemeCode"]));
        Assert.DoesNotContain("0000009999999", unknown.Writes.ToJsonString());
        Assert.Equal("not-registered", Ask("0000002222222").Outcome);
        Assert.Equal("not-registered", Ask("0000003333333").Outcome);
        var other = Ask(Mol, "AE070331234567890999999");
        Assert.Equal(("mismatch", "BE01", "010"), (other.Outcome, other.ReasonCode, (string?)other.Writes["facts.partyRegister"]!["schemeCode"]));
        // Before the consent, the register held nothing for the file.
        Assert.Equal("not-registered", act.Run(new NativeAttempt(act.Name, "1.0.0", new JsonObject
        {
            ["header"] = new JsonObject { ["scheme"] = "uae.wps" },
            ["batch"] = new JsonObject { ["debtor"] = new JsonObject { ["organisationId"] = new JsonObject { ["id"] = Mol } } },
        }, new JsonObject(), clock.Now.AddHours(-1), Bank, "batch")).Outcome);
        // A scheme with no matcher of its own gets the platform's: found and active, and a supplied account among those registered.
        Assert.Equal("registered", Ask("0000002222222", scheme: "xx.other").Outcome);
        Assert.Equal("mismatch", Ask(Mol, "AE070331234567890999999", "xx.other").Outcome);
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => act.Run(new NativeAttempt(act.Name, "1.0.0", new JsonObject(), new JsonObject(), now, "other-bank", "batch"))).Name);
    }
}
