// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardDif/DifFileBuilderTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
// Purpose: the DIF layout and content check of WPS inward Activity 20 (design Inward_PAF_DIF_Generation v1.0 §6.1, §6.3, §6.4, §5.1 step 6; A-4, A-7;
// task T3): AC-FMT-01, AC-FMT-02 (U half), AC-FMT-03, AC-ORD-01 (U half), AC-SRC-03 (U half), AC-FMT-05 (U half).
// Responsibility: the pure DifFileBuilder over records composed by DifLines, byte for byte against the design's worked example.
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TransactHub.Activities.Wps.Inward.Dif;
using Xunit;

namespace TransactHub.Activities.Wps.Tests.Source;

public class DifFileBuilderTests
{
    // 2025-01-16 in Dubai (UTC+4): the 24 events at 06:00 UTC; the generation at 10:15 business time.
    private static readonly DateTime PostedUtc = new(2025, 1, 16, 6, 0, 0, DateTimeKind.Utc);

    private static readonly DifCandidate[] Example =
    [
        new(4002, "910123456702", "0510123456789012346", 4200.00m, 300.00m, 902, PostedUtc),
        new(4001, "910123456701", "0510123456789012345", 5500.00m, 750.00m, 901, PostedUtc),
    ];

    private static DifFileRecord File(IReadOnlyList<DifLineRecord> lines, string layout = DifFileBuilder.CurrentLayoutVersion) => new(
        0, "bank-a", "TX-1", 7, 11, 1, "05101234567250115090012.DIF", "250115090012", "051", lines.Count, 0,
        lines.Sum(l => l.FixedPay), lines.Sum(l => l.VariablePay), lines.Sum(l => l.RecoveryAmount), DifFileBuilder.Total(lines),
        new DateTime(2025, 1, 16, 10, 15, 0), "Asia/Dubai", new DateTime(2025, 1, 16, 6, 15, 0, DateTimeKind.Utc), layout, string.Empty, 0, "wf-1", "svc");

    private static IReadOnlyList<DifLineRecord> Lines(params DifCandidate[] candidates) =>
        DifLines.Compose(candidates, new Dictionary<long, decimal>(), "Asia/Dubai");

    // AC-FMT-01, AC-FMT-02, AC-FMT-03, AC-SRC-03 (U): the §6.4 worked example, byte for byte.
    [Fact]
    public void WorkedExample_ByteForByte()
    {
        var lines = Lines(Example);
        var built = DifFileBuilder.Build(File(lines), lines);

        const string Expected =
            "DIR,250115090012,910123456701,0510123456789012345,2025-01-16,6250.00,0.00,,,4001\r\n" +
            "DIR,250115090012,910123456702,0510123456789012346,2025-01-16,4500.00,0.00,,,4002\r\n" +
            "DCR,051,2025-01-16,1015,2,10750.00,10750.00,0.00,EWPMS,EWPMS\r\n";
        built.Bytes.Should().Equal(Encoding.ASCII.GetBytes(Expected));
        built.Bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "no BOM");
        built.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(built.Bytes)).ToLowerInvariant());
        File(lines).TotalWithdrawalAmount.Should().Be(10750.00m, "the DCR total equals TotalWithdrawalAmount (I-DF7)");
        DifFileBuilder.CurrentLayoutVersion.Should().Be("FSD-PROVISIONAL-1");
    }

    // AC-FMT-02: count = DIR lines; total = Σ of the PRINTED DIR amounts (each rounded first).
    [Fact]
    public void Dcr_TotalIsSumOfPrintedAmounts()
    {
        var lines = Lines(
            new DifCandidate(1, "E1", "A1", 0.125m, 0m, 1, PostedUtc),
            new DifCandidate(2, "E2", "A2", 0.125m, 0m, 2, PostedUtc),
            new DifCandidate(3, "E3", "A3", 0.125m, 0m, 3, PostedUtc));
        var text = Encoding.ASCII.GetString(DifFileBuilder.Build(File(lines), lines).Bytes);

        text.Should().Contain(",0.13,0.00,,,1\r\n", "0.125 rounds half away from zero");
        text.Should().EndWith("DCR,051,2025-01-16,1015,3,0.39,0.39,0.00,EWPMS,EWPMS\r\n", "Σ printed = 0.39, not round2(0.375) = 0.38");
        DifFileBuilder.Total(lines).Should().Be(0.39m);
    }

    // AC-ORD-01 (U): unordered 4002, 4001, 4007 → order numbers 1, 2, 3 to 4001, 4002, 4007, DIR lines 1–3 in that order.
    [Fact]
    public void OrderNumbers_ByEdrId_AreThePhysicalLineNumbers()
    {
        var lines = Lines(
            new DifCandidate(4002, "E2", "A2", 1m, 0m, 2, PostedUtc),
            new DifCandidate(4001, "E1", "A1", 1m, 0m, 1, PostedUtc),
            new DifCandidate(4007, "E7", "A7", 1m, 0m, 7, PostedUtc));

        lines.Select(l => (l.OrderNo, l.EdrId)).Should().Equal((1, 4001L), (2, 4002L), (3, 4007L));
        var physical = Encoding.ASCII.GetString(DifFileBuilder.Build(File(lines), lines.Reverse().ToList()).Bytes).Split("\r\n");
        physical[0].Should().EndWith(",4001");
        physical[1].Should().EndWith(",4002");
        physical[2].Should().EndWith(",4007");
        physical[3].Should().StartWith("DCR,");
    }

    // A-5 (U half of AC-FMT-04): the date is the business date of the 24 event — 21:30 UTC is the next Dubai day.
    [Fact]
    public void TransactionDate_IsBusinessDateOfThePostedEvent()
    {
        var lines = Lines(new DifCandidate(1, "E1", "A1", 1m, 0m, 1, new DateTime(2025, 1, 15, 21, 30, 0, DateTimeKind.Utc)));
        lines[0].TransactionDate.Should().Be(new DateOnly(2025, 1, 16));
        lines[0].PostedEventId.Should().Be(1);
    }

    [Fact]
    public void UnknownLayout_Throws()
    {
        var lines = Lines(Example);
        var act = () => DifFileBuilder.Build(File(lines, "FSD-OTHER"), lines);
        act.Should().Throw<InvalidOperationException>();
    }

    // AC-FMT-05 (U half): each written text field refuses a comma, CR, LF or non-ASCII character, and blank; a NULL account is refused.
    [Theory]
    [InlineData("A,B")]
    [InlineData("A\rB")]
    [InlineData("A\nB")]
    [InlineData("AÉB")]
    [InlineData(" ")]
    public void ContentCheck_RefusesEachBadValueInEachField(string bad)
    {
        DifCandidate Edr(string employee = "E1", string? account = "A1") => new(1, employee, account, 1m, 0m, 1, PostedUtc);

        DifFileBuilder.InvalidFields("250115090012", "051", "X.DIF", [Edr()]).Should().BeEmpty();
        DifFileBuilder.InvalidFields(bad, "051", "X.DIF", [Edr()]).Should().Equal(DifFileBuilder.FieldWpsFileId);
        DifFileBuilder.InvalidFields("250115090012", bad, "X.DIF", [Edr()]).Should().Equal(DifFileBuilder.FieldAgentId);
        DifFileBuilder.InvalidFields("250115090012", "051", bad, [Edr()]).Should().Equal(DifFileBuilder.FieldDifFileName);
        DifFileBuilder.InvalidFields("250115090012", "051", "X.DIF", [Edr(employee: bad)]).Should().Equal(DifFileBuilder.FieldEmployeeId);
        DifFileBuilder.InvalidFields("250115090012", "051", "X.DIF", [Edr(account: bad)]).Should().Equal(DifFileBuilder.FieldBeneficiaryAccount);
    }

    [Fact]
    public void ContentCheck_RefusesNullAccount() =>
        DifFileBuilder.InvalidFields("250115090012", "051", "X.DIF", [new DifCandidate(1, "E1", null, 1m, 0m, 1, PostedUtc)])
            .Should().Equal(DifFileBuilder.FieldBeneficiaryAccount);
}
