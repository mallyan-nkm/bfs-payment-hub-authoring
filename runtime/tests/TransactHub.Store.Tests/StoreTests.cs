using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Model;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// Every test runs on SQL Server and on PostgreSQL. A behaviour that differs between them fails here.

[Collection(DatabaseCollection.Name)]
public sealed class StoreTests(Databases dbs)
{
    static readonly DateTimeOffset T0 = new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);

    /// <summary>A WPS salary file as a pain.001 bulk: an employer, its account, two employees. Synthetic.</summary>
    static PaymentBulk SalaryFile() => new()
    {
        MessageId = "SIF-0000000000001-20261101",
        PaymentInformationId = "PMTINF-1",
        CreationDateTime = T0,
        RequestedExecutionDate = new DateOnly(2026, 11, 2),
        CategoryPurposeCode = "SALA",
        Debtor = new Party
        {
            Name = "Example Trading LLC", IsOrganisation = true,
            OtherIds = [new GenericId("0000000000001", "MOL", "AE-MOHRE")],
            PostalAddress = new PostalAddress { StreetName = "Example Street", BuildingNumber = "1", TownName = "Dubai", Country = "AE" },
        },
        DebtorAccount = new Account { Iban = "AE070000000000000000001", Currency = "AED" },
        DebtorAgent = new Agent { ClearingSystem = "AEUAE", MemberId = "000000001" },
        SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = new() { ["salaryMonth"] = "2026-10", ["establishmentId"] = "0000000000001" } },
        Transactions =
        [
            Line("E2E-1", 4500.50m, "Employee One", "AE070000000000000000011", fixedPay: 4000m, variablePay: 500.50m),
            Line("E2E-2", 3200m, "Employee Two", "AE070000000000000000022", fixedPay: 3200m, variablePay: 0m),
        ],
    };

    static CreditTransfer Line(string e2e, decimal amount, string name, string iban, decimal fixedPay, decimal variablePay) => new()
    {
        PaymentId = new PaymentIdentification { EndToEndId = e2e, Uetr = Guid.NewGuid().ToString() },
        InstructedAmount = Money.Aed(amount),
        Creditor = new Party { Name = name, OtherIds = [new GenericId("10000000000001", "PERSON-CODE")] },
        CreditorAccount = new Account { Iban = iban },
        CreditorAgent = new Agent { ClearingSystem = "AEUAE", MemberId = "000000002" },
        PurposeCode = "SALA",
        RemittanceUnstructured = ["Salary 2026-10"],
        SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = new() { ["fixedPay"] = fixedPay, ["variablePay"] = variablePay, ["leaveDays"] = 0 } },
    };

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_bulk_reads_back_as_the_model_it_was_accepted_as(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var store = new PaymentStore(db, new TestClock(T0));
        var accepted = await store.AcceptBulk("example-bank", "uae.wps", Direction.Outward, SalaryFile(), "uae.wps@1.0.0");

        db.ChangeTracker.Clear();
        var back = (await store.ReadBulk(accepted.BulkId))!;
        var sent = SalaryFile();
        Assert.Equal(sent.MessageId, back.MessageId);
        Assert.Equal(2, back.NumberOfTransactions);
        Assert.Equal(7700.50m, back.ControlSum);
        Assert.Equal("Example Trading LLC", back.Debtor.Name);
        Assert.Equal("Dubai", back.Debtor.PostalAddress!.TownName);
        Assert.Equal(new GenericId("0000000000001", "MOL", "AE-MOHRE"), back.Debtor.OtherIds.Single());
        Assert.Equal("AE070000000000000000001", back.DebtorAccount.Iban);
        Assert.Equal("000000001", back.DebtorAgent!.MemberId);
        Assert.Equal("2026-10", (string)back.SupplementaryData["uae.wps"]["salaryMonth"]!);

        var first = back.Transactions[0];
        Assert.Equal("E2E-1", first.PaymentId.EndToEndId);
        Assert.Equal(Money.Aed(4500.50m), first.InstructedAmount);
        Assert.Equal("Employee One", first.Creditor.Name);
        Assert.Equal("AE070000000000000000011", first.CreditorAccount.Iban);
        Assert.Equal(500.50m, (decimal)first.SupplementaryData["uae.wps"]["variablePay"]!);
        Assert.Equal(["Salary 2026-10"], first.RemittanceUnstructured);
        Assert.Null(first.Debtor); // the bulk's debtor, not repeated per line
        Assert.Equal("E2E-2", back.Transactions[1].PaymentId.EndToEndId);

        var facts = new FactLog(db);
        Assert.Equal("RCVD", (await facts.Last(accepted.BulkId, "status"))!.Code);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_store_refuses_any_update_or_delete(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var store = new PaymentStore(db, new TestClock(T0));
        var accepted = await store.AcceptBulk("example-bank", "uae.wps", Direction.Outward, SalaryFile(), "uae.wps@1.0.0");

        var row = await db.Payments.FirstAsync(x => x.Id == accepted.PaymentIds[0]);
        row.Amount = 1m;
        Assert.Throws<InsertOnlyViolation>(() => db.SaveChanges());
        db.ChangeTracker.Clear();

        var fact = await db.Facts.FirstAsync();
        db.Facts.Remove(fact);
        await Assert.ThrowsAsync<InsertOnlyViolation>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(4500.50m, (await db.Payments.AsNoTracking().FirstAsync(x => x.Id == accepted.PaymentIds[0])).Amount);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Facts_chain_by_hash_and_the_status_is_their_projection(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var clock = new TestClock(T0);
        var facts = new FactLog(db, clock);
        await facts.Append("pay-1", "payment", new NewFact("status", "RCVD"));
        clock.Advance(TimeSpan.FromSeconds(1.2345678));
        await facts.Append("pay-1", "payment", new NewFact("step", "uae.wps.validate.sif", new JsonObject { ["errors"] = 0 }));
        await facts.Append("pay-1", "payment", new NewFact("status", "ACSP"));
        await facts.Append("pay-2", "payment", new NewFact("status", "RJCT", new JsonObject { ["reason"] = "00803" }));

        Assert.True((await facts.Verify("pay-1")).Ok);
        Assert.Equal(3, (await facts.Verify("pay-1")).Facts);
        var statuses = await facts.CurrentStatuses(["pay-1", "pay-2", "pay-3"]);
        Assert.Equal("ACSP", statuses["pay-1"]);
        Assert.Equal("RJCT", statuses["pay-2"]);
        Assert.False(statuses.ContainsKey("pay-3"));

        // A fact changed behind the store's back (here, by raw SQL, as an intruder would) is found.
        var table = db.Model.FindEntityType(typeof(FactRow))!.GetTableName();
        await db.Database.ExecuteSqlRawAsync(p == StoreProvider.SqlServer
            ? $"UPDATE [{table}] SET [Code] = 'ACSC' WHERE [SubjectId] = 'pay-1' AND [Seq] = 3"
            : $"UPDATE \"{table}\" SET \"Code\" = 'ACSC' WHERE \"SubjectId\" = 'pay-1' AND \"Seq\" = 3");
        var check = await facts.Verify("pay-1");
        Assert.False(check.Ok);
        Assert.Contains("fact 3 was changed", check.Problem);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Two_writers_on_one_subject_never_share_a_number(StoreProvider p)
    {
        var o = dbs.Fresh(p);
        await using (var setup = StoreDbContext.Open(o)) await setup.Database.MigrateAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var db = StoreDbContext.Open(o);
            var facts = new FactLog(db);
            for (var k = 0; k < 5; k++) await facts.Append("bulk-1", "bulk", new NewFact("step", $"w{i}-{k}"));
        }));
        await using var read = StoreDbContext.Open(o);
        var check = await new FactLog(read).Verify("bulk-1");
        Assert.True(check.Ok, check.Problem);
        Assert.Equal(40, check.Facts);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_release_is_stored_once_and_another_content_is_refused(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var reg = new ReleaseRegistry(db, new TestClock(T0));
        var pkg = Encoding.UTF8.GetBytes("package uae.wps 1.0.0");
        var manifest = new JsonObject { ["scope"] = "uae.wps", ["release"] = "1.0.0" };

        Assert.True((await reg.Store("uae.wps", "1.0.0", pkg, manifest, "studio")).Stored);
        Assert.False((await reg.Store("uae.wps", "1.0.0", pkg, manifest, "studio")).Stored); // the same again: nothing changes
        var ex = await Assert.ThrowsAsync<ReleaseConflict>(() => reg.Store("uae.wps", "1.0.0", Encoding.UTF8.GetBytes("changed"), manifest, "studio"));
        Assert.Contains("new release", ex.Message);
        Assert.Equal(ReleaseRegistry.HashOf(pkg), (await reg.Get("uae.wps", "1.0.0"))!.ContentHash);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task New_payments_take_the_active_release_and_rollback_is_a_new_activation(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var reg = new ReleaseRegistry(db, new TestClock(T0));
        foreach (var r in new[] { "1.0.0", "1.1.0" }) await reg.Store("uae.wps", r, Encoding.UTF8.GetBytes(r), new JsonObject(), "studio");

        await Assert.ThrowsAsync<InvalidOperationException>(() => reg.Activate("prod", "uae.wps", "9.9.9", T0, "ops"));
        await reg.Activate("prod", "uae.wps", "1.0.0", T0, "ops");
        await reg.Activate("prod", "uae.wps", "1.1.0", T0.AddDays(10), "ops");
        Assert.Null(await reg.ActiveAt("prod", "uae.wps", T0.AddHours(-1)));
        Assert.Equal("1.0.0", await reg.ActiveAt("prod", "uae.wps", T0.AddDays(9)));
        Assert.Equal("1.1.0", await reg.ActiveAt("prod", "uae.wps", T0.AddDays(10)));
        Assert.Null(await reg.ActiveAt("test", "uae.wps", T0.AddDays(10)));

        // Rollback from day 12: an activation of 1.0.0, never an edit of the 1.1.0 row.
        await reg.Activate("prod", "uae.wps", "1.0.0", T0.AddDays(12), "ops");
        Assert.Equal("1.1.0", await reg.ActiveAt("prod", "uae.wps", T0.AddDays(11)));
        Assert.Equal("1.0.0", await reg.ActiveAt("prod", "uae.wps", T0.AddDays(12)));
        Assert.Equal(3, await db.Activations.CountAsync());
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_future_dated_rule_does_not_hide_the_rule_in_force(StoreProvider p)
    {
        // Issue N1: the source's RuleRepository chose the newest version, then filtered by date, so a
        // charge published today for next month removed today's charge.
        await using var db = await dbs.Open(p);
        var clock = new TestClock(T0);
        var values = new VersionedValues(db, clock);
        await values.Publish("rule", "example-bank", "p.charges.salary-file", new JsonObject { ["fee"] = 2.0 }, T0, null, "checker");
        clock.Advance(TimeSpan.FromDays(1));
        await values.Publish("rule", "example-bank", "p.charges.salary-file", new JsonObject { ["fee"] = 2.5 }, T0.AddDays(30), null, "checker");

        var today = await values.InForce("rule", "example-bank", "p.charges.salary-file", T0.AddDays(2));
        Assert.Equal(1, today!.VersionNo);
        var later = await values.InForce("rule", "example-bank", "p.charges.salary-file", T0.AddDays(30));
        Assert.Equal(2, later!.VersionNo);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_payment_asking_as_of_its_acceptance_gets_the_same_answer_after_a_hot_update(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var clock = new TestClock(T0);
        var values = new VersionedValues(db, clock);
        await values.Publish("config", "example-bank", "uae.wps.posting-window", new JsonObject { ["closes"] = "14:00" }, T0, null, "checker");
        var accepted = T0.AddHours(1);

        // A hot update, published later and effective at once, even backdated.
        clock.Set(T0.AddHours(3));
        await values.Publish("config", "example-bank", "uae.wps.posting-window", new JsonObject { ["closes"] = "15:00" }, T0, null, "checker");

        Assert.Equal(1, (await values.InForce("config", "example-bank", "uae.wps.posting-window", accepted))!.VersionNo);
        Assert.Equal(2, (await values.InForce("config", "example-bank", "uae.wps.posting-window", clock.GetUtcNow()))!.VersionNo);
        await Assert.ThrowsAsync<ArgumentException>(() => values.Publish("config", "example-bank", "x", new JsonObject(), T0, T0, "checker"));
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_file_seen_before_is_known_by_its_content(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var store = new PaymentStore(db, new TestClock(T0));
        var sif = Encoding.ASCII.GetBytes("EDR,...\nSCR,...\n");
        Assert.False((await store.KeepMessage("example-bank", "uae.wps", Direction.Outward, "uae.wps.sif", "a.SIF", sif, null)).SeenBefore);
        Assert.True((await store.KeepMessage("example-bank", "uae.wps", Direction.Outward, "uae.wps.sif", "b.SIF", sif, null)).SeenBefore);
        Assert.False((await store.KeepMessage("other-bank", "uae.wps", Direction.Outward, "uae.wps.sif", "a.SIF", sif, null)).SeenBefore);
        Assert.Equal(3, await db.Messages.CountAsync());
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Times_and_money_read_back_exactly(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var odd = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.FromHours(4)).AddTicks(1234567);
        var store = new PaymentStore(db, new TestClock(odd));
        var id = await store.AcceptPayment("example-bank", "uae.p", Direction.Outward, Line("E2E-9", 1234567890123.4567m, "Payee", "AE070000000000000000099", 0, 0), "uae.p@1.0.0");
        db.ChangeTracker.Clear();
        var row = await db.Payments.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.Equal(1234567890123.4567m, row.Amount);
        Assert.Equal(StoreTime.Of(odd), row.AcceptedAt);
        Assert.Equal(TimeSpan.Zero, row.AcceptedAt.Offset);
    }
}
