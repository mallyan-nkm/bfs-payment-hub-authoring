using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Engine.Temporal;
using TransactHub.Engines;
using TransactHub.Model;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// The durable claim check (plan W2): what a run's payload becomes is a chain of facts in the store,
// it reads back the same as the in-memory claim check every harness uses, and the payment it carries
// is accepted into the ISO 20022 tables. On SQL Server and on PostgreSQL.

[Collection(DatabaseCollection.Name)]
public sealed class ClaimCheckTests(Databases dbs)
{
    /// <summary>A WPS salary file after intake, as the studio's canonical context holds it (synthetic).</summary>
    static JsonObject SalaryFile() => (JsonObject)JsonNode.Parse("""
    {
      "batch": { "id": "0000001234567261117083000", "categoryPurpose": "SALA", "controlSum": 1500, "currency": "AED", "numberOfTransactions": 2,
                 "debtor": { "name": "Example Trading LLC", "postalAddress": { "townName": "Dubai", "country": "AE" } },
                 "debtorAccount": { "iban": "AE070000000000000000001" }, "debtorAgent": { "clearingMemberId": "302620122" } },
      "items": [
        { "payment": { "creditor": { "name": "Employee One" }, "creditorAgent": { "clearingMemberId": "203320101" }, "creditorAccount": { "iban": "AE070331234567890123456" }, "instructedAmount": { "value": 1000, "currency": "AED" } },
          "ext": { "uae": { "wps": { "personId": "00001234567890", "fixedPay": 800, "variablePay": 200, "leaveDays": 0 } } } },
        { "payment": { "creditor": { "name": "Employee Two" }, "creditorAgent": { "clearingMemberId": "203320101" }, "creditorAccount": { "iban": "AE070331234567890123457" }, "instructedAmount": { "value": 500.25, "currency": "AED" } },
          "ext": { "uae": { "wps": { "personId": "00001234567891", "fixedPay": 500.25, "variablePay": 0, "leaveDays": 2 } } } }
      ],
      "ext": { "uae": { "wps": { "employerId": "0000001234567", "salaryMonth": "102026" } } }
    }
    """)!;

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Every_change_is_a_fact_and_the_payload_rebuilt_from_the_facts_is_the_one_in_memory(StoreProvider p)
    {
        var o = dbs.Fresh(p);
        await using (var db = StoreDbContext.Open(o)) await db.Database.MigrateAsync();
        var durable = new DurableClaimCheck(o);
        var memory = new MemoryClaimCheck();
        foreach (IClaimCheck cc in new IClaimCheck[] { durable, memory })
        {
            await cc.Create("r1#ctx", SalaryFile());
            var t1 = await cc.Stage("r1#ctx", [("facts.validation", new JsonObject { ["errors"] = 0 }), ("items[1].status", "accepted")]);
            await cc.Commit(t1);
            await cc.Commit(t1); // the engine retried its call: nothing changes
            var t2 = await cc.Stage("r1#ctx", [("facts.never", "staged, not committed")]);
            await cc.Fork("r1#ctx", "r2#ctx");
            var t3 = await cc.Stage("r2#ctx", [("facts.leg", "uae.wps")]);
            await cc.Commit(t3);
            await cc.Copy("r2#ctx", "r1#ctx", ["facts.leg"]);
            _ = t2;
        }
        foreach (var r in new[] { "r1#ctx", "r2#ctx" })
        {
            var expected = CanonicalJson.Of(memory.Full(null, r));
            Assert.Equal(expected, CanonicalJson.Of(durable.Full(null, r)));
            Assert.Equal(expected, CanonicalJson.Of(await DurableClaimCheck.Load(o, r)));
        }
        var rebuilt = await DurableClaimCheck.Load(o, "r1#ctx");
        Assert.Equal("uae.wps", (string)rebuilt["facts"]!["leg"]!);
        Assert.Equal("accepted", (string)rebuilt["items"]![1]!["status"]!);
        Assert.Null(rebuilt["facts"]!["never"]);
        Assert.Equal(2, durable.Count("r1#ctx", "items"));

        await using var check = StoreDbContext.Open(o);
        var facts = new FactLog(check);
        Assert.True((await facts.Verify("r1#ctx")).Ok);
        // create, staged, commit (once), staged, copy
        Assert.Equal(["create", "staged", "commit", "staged", "copy"], (await facts.Of("r1#ctx")).Select(f => f.Code!).ToArray());
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_payment_of_a_run_is_accepted_into_the_ISO_20022_tables(StoreProvider p)
    {
        var o = dbs.Fresh(p);
        await using (var db = StoreDbContext.Open(o)) await db.Database.MigrateAsync();
        var cc = new DurableClaimCheck(o);
        await cc.Create("root#ctx", SalaryFile(), new AcceptInfo("example-bank", "uae.wps", Direction.Outward, "p.wf.corporate.v1@1.0.0"));
        var (bulkId, kind, count) = cc.Accepted["root#ctx"];
        Assert.Equal(("bulk", 2), (kind, count));

        await using var read = StoreDbContext.Open(o);
        var bulk = (await new PaymentStore(read).ReadBulk(bulkId))!;
        Assert.Equal("0000001234567261117083000", bulk.MessageId);
        Assert.Equal("SALA", bulk.CategoryPurposeCode);
        Assert.Equal("Example Trading LLC", bulk.Debtor.Name);
        Assert.Equal("Dubai", bulk.Debtor.PostalAddress!.TownName);
        Assert.Equal("AE070000000000000000001", bulk.DebtorAccount.Iban);
        Assert.Equal(new Agent { ClearingSystem = "AEUAE", MemberId = "302620122" }, bulk.DebtorAgent);
        Assert.Equal("0000001234567", (string)bulk.SupplementaryData["uae.wps"]["employerId"]!);
        Assert.Equal(1500.25m, bulk.ControlSum);
        var second = bulk.Transactions[1];
        Assert.Equal("0000001234567261117083000-2", second.PaymentId.EndToEndId);
        Assert.Equal(Money.Aed(500.25m), second.InstructedAmount);
        Assert.Equal("Employee Two", second.Creditor.Name);
        Assert.Equal("203320101", second.CreditorAgent!.MemberId);
        Assert.Equal(2, (int)second.SupplementaryData["uae.wps"]["leaveDays"]!);
        Assert.Equal("RCVD", (await new FactLog(read).Last(bulkId, "status"))!.Code);
        // The run's payload knows which bulk it is.
        Assert.Contains((await new FactLog(read).Of("root#ctx")), f => f.Code == "accepted");
    }

    [Fact]
    public void A_single_payment_maps_too_and_a_payload_without_one_is_not_accepted()
    {
        var single = (JsonObject)JsonNode.Parse("""{ "payment": { "endToEndId": "E2E-1", "instructedAmount": { "value": 10, "currency": "AED" }, "creditor": { "name": "Payee" }, "creditorAccount": { "iban": "AE07" }, "purpose": "SALA" }, "ext": { "b": { "costCentre": "CC-1" } } }""")!;
        var t = CanonicalMapper.ToPayment(single, null, 0)!;
        Assert.Equal("E2E-1", t.PaymentId.EndToEndId);
        Assert.Equal("SALA", t.PurposeCode);
        Assert.Equal("CC-1", (string)t.SupplementaryData["b"]["costCentre"]!);
        Assert.Null(CanonicalMapper.ToBulk(single));
        Assert.Null(CanonicalMapper.ToPayment(new JsonObject(), null, 0));
    }
}
