using System.Text.Json.Nodes;
using TransactHub.Model;
using Xunit;

namespace TransactHub.Store.Tests;

public sealed class ModelTests
{
    static PaymentContext Ctx() => new()
    {
        Tenant = "example-bank", SubjectId = "b1", SubjectKind = SubjectKind.Bulk, Scheme = "uae.wps",
        Direction = Direction.Outward, ReleaseId = "uae.wps@1.0.0", AsOf = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void The_context_takes_branch_facts()
    {
        var c = Ctx().With("validationErrorCount", 0).With("ackType", "ACK");
        Assert.Equal(0, (int)c.Facts["validationErrorCount"]!);
        Assert.Equal("ACK", (string)c.Facts["ackType"]!);
        Assert.Empty(Ctx().Facts); // contexts are values
    }

    [Theory]
    [InlineData("corporateEmail")]
    [InlineData("accountNo")]
    [InlineData("senderIban")]
    [InlineData("cifNumber")]
    [InlineData("debtorName")]
    public void The_context_refuses_personal_data(string key) =>
        Assert.Throws<ArgumentException>(() => Ctx().With(key, "x"));

    [Fact]
    public void The_context_refuses_records_and_long_values()
    {
        Assert.Throws<ArgumentException>(() => Ctx().With("record", new JsonObject { ["a"] = 1 }));
        Assert.Throws<ArgumentException>(() => Ctx().With("note", new string('x', 65)));
    }

    [Fact]
    public void A_bulk_computes_its_count_and_control_sum()
    {
        var t = new CreditTransfer { PaymentId = new() { EndToEndId = "1" }, InstructedAmount = new Money(10.25m, "AED"), Creditor = new(), CreditorAccount = new() };
        var b = new PaymentBulk { MessageId = "m", PaymentInformationId = "p", Debtor = new(), DebtorAccount = new(), Transactions = [t, t with { InstructedAmount = new Money(5m, "AED") }] };
        Assert.Equal(2, b.NumberOfTransactions);
        Assert.Equal(15.25m, b.ControlSum);
    }
}
