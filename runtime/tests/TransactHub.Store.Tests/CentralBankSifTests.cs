using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Activities.Wps.Sif;
using TransactHub.Model;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// The Central Bank SIF generated from the ISO 20022 store, with the source's writer (plan W1, W2, W6).
// On SQL Server and on PostgreSQL.

[Collection(DatabaseCollection.Name)]
public sealed class CentralBankSifTests(Databases dbs)
{
    static readonly DateTime Created = new(2026, 11, 17, 9, 5, 0);

    static CreditTransfer Employee(string e2e, string person, decimal fixedPay, decimal variablePay, string iban, JsonObject? evp = null)
    {
        var w = new JsonObject { ["personId"] = person, ["payStartDate"] = "2026-10-01", ["payEndDate"] = "2026-10-31", ["daysInPeriod"] = 31, ["fixedPay"] = fixedPay, ["variablePay"] = variablePay, ["leaveDays"] = 0 };
        if (evp is not null) w["evp"] = evp;
        return new CreditTransfer
        {
            PaymentId = new PaymentIdentification { EndToEndId = e2e },
            InstructedAmount = new Money(fixedPay + variablePay, "AED"),
            Creditor = new Party { Name = "Employee " + e2e },
            CreditorAccount = new Account { Iban = iban },
            CreditorAgent = new Agent { ClearingSystem = "AEUAE", MemberId = "203320101" },
            PurposeCode = "SALA",
            SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = w },
        };
    }

    static PaymentBulk SalaryFile() => new()
    {
        MessageId = "0000001234567261117083000", PaymentInformationId = "0000001234567261117083000", CategoryPurposeCode = "SALA",
        Debtor = new Party { Name = "Example Trading LLC", IsOrganisation = true, OtherIds = [new GenericId("0000001234567", "MOL")] },
        DebtorAccount = new Account { Iban = "AE070000000000000000001", Currency = "AED" },
        SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = new() { ["employerId"] = "0000001234567", ["salaryMonth"] = "102026", ["employerReference"] = "CORP-0001" } },
        Transactions =
        [
            Employee("1", "1234567890", 1200m, 300m, "AE070331234567890123456",
                new JsonObject { ["housing"] = 200, ["conveyance"] = 50, ["medical"] = 0, ["annualPassage"] = 0, ["overtime"] = 50, ["other"] = 0, ["leaveEncashment"] = 0 }),
            Employee("2", "00001234567891", 800.5m, 0m, "AE070331234567890123457"),
        ],
    };

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_central_bank_file_is_generated_from_the_store_and_kept_with_its_hash(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var store = new PaymentStore(db);
        var accepted = await store.AcceptBulk("example-bank", "uae.wps", Direction.Outward, SalaryFile(), "uae.wps@1.0.0");
        db.ChangeTracker.Clear();

        var (sif, failure) = await CentralBankSif.Generate(db, "example-bank", accepted.BulkId, "302620122", "0000001234567261117083000.SIF.GPG", Created);
        Assert.Null(failure);
        Assert.Equal("0000001234567261117083000.SIF", sif!.FileName);
        var lines = sif.Document.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[]
        {
            "EDR,00001234567890,203320101,AE070331234567890123456,2026-10-01,2026-10-31,31,1200.00,300.00,0",
            "EVP,00001234567890,203320101,200.00,50.00,0.00,0.00,50.00,0.00,0.00",
            "EDR,00001234567891,203320101,AE070331234567890123457,2026-10-01,2026-10-31,31,800.50,0.00,0",
            "SCR,0000001234567,302620122,2026-11-17,0905,102026,2,2300.50,AED,CORP-0001",
        }, lines);

        // The file is kept as sent: its bytes, and the hash the writer took over them.
        var msg = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == sif.MessageId);
        Assert.Equal(sif.Document.Content, Encoding.UTF8.GetString(msg.Content));
        Assert.Equal(sif.Document.Sha256.ToLowerInvariant(), msg.ContentHash);
        Assert.Equal(accepted.BulkId, msg.SubjectId);
        // Under the scheme message's id, as the File API keeps the sent copy: one file, one kind (R-09-288, Issue I-363).
        Assert.Equal("uae.wps.sif-submission", msg.Kind);
        Assert.Equal(TransactHub.Activities.Wps.Native.SendSalaryFile.Message, CentralBankSif.MessageKind);
        Assert.Equal("sif-generated", (await new FactLog(db).Last(accepted.BulkId, "message"))!.Code);

        // Generated again (a retried step), the bytes are the same.
        var (again, _) = await CentralBankSif.Generate(db, "example-bank", accepted.BulkId, "302620122", "0000001234567261117083000.SIF.GPG", Created);
        Assert.Equal(sif.Document.Sha256, again!.Document.Sha256);
    }

    [Fact]
    public void A_bulk_the_writer_cannot_write_is_refused_with_the_source_s_code()
    {
        var empty = SalaryFile() with { Transactions = [] };
        Assert.Equal("SIF_GENERATION_NO_RECORDS", CentralBankSif.Build(empty, "302620122", Created).Failure!.ErrorCode);
        var badMonth = SalaryFile() with { SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = new() { ["employerId"] = "0000001234567", ["salaryMonth"] = "132026", ["employerReference"] = "CORP-0001" } } };
        Assert.Equal("SIF_GENERATION_INVALID_RECORD", CentralBankSif.Build(badMonth, "302620122", Created).Failure!.ErrorCode);
    }
}
