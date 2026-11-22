// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardRfa/RfaFileBuilderTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
// Purpose: the RFA layout, order and amounts of WPS inward Activity 25 (design Inward_PAF_RFA_Generation v1.0 §6.1, §6.3, §6.4, §5 step 6; A-4, A-6;
// task T2): AC-ORD-01, AC-AMT-01, AC-LAY-01 (byte for byte against the FSD UAT sample), AC-LAY-02, and the U half of AC-RSN-04.
// Responsibility: the pure RfaFileBuilder over records composed by RfaLines.
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TransactHub.Activities.Wps.Inward.Rfa;
using Xunit;

namespace TransactHub.Activities.Wps.Tests.Source;

public class RfaFileBuilderTests
{
    private const string Routing = "740810000";

    private static RfaCandidate Candidate(long edrId, string employee, decimal fixedPay, decimal variablePay, string? reason = "006") =>
        new(edrId, employee, fixedPay, variablePay, 9000 + edrId,
            reason is null ? RfaReason.Missing : new RfaReason(reason, RfaReasonSources.PifEdrReturnReasons, 100 + edrId));

    private static RfaFileRecord File(IReadOnlyList<RfaLineRecord> lines, string layout = RfaFileBuilder.CurrentLayoutVersion) => new(
        0, "bank-a", "TX-1", 1, "408260714091512.RFA", Routing, "126005299444", "0000000189685", lines.Count, 0, RfaAmountRule.Total(lines),
        new DateOnly(2026, 7, 14), new DateTime(2026, 7, 14, 5, 15, 12, DateTimeKind.Utc), layout, string.Empty, 0, "wf-1", "svc");

    // AC-LAY-01 (A-4): one EDR (employee 90000000000004, 3074.00, reason 006) on 2026-07-14 with R = 740810000 → exactly the FSD UAT sample.
    [Fact]
    public void AcLay01_TheFsdUatSample_ByteForByte()
    {
        var lines = RfaLines.Compose([Candidate(4001, "90000000000004", 3000.00m, 74.00m)]);
        var built = RfaFileBuilder.Build(File(lines), lines);

        const string Expected =
            "ARI,126005299444,0000000189685,90000000000004,3074.00,006,RFA-FROM-408-ON-20260714, , ,EWPMS\r\n" +
            "ARC,ReturnsRef-126005299444-20260714,1,3074.00,740810000, , , ,EWPMS";
        built.Bytes.Should().Equal(Encoding.ASCII.GetBytes(Expected));
        built.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(built.Bytes)).ToLowerInvariant());
    }

    // AC-LAY-02: every ARI line ends CRLF; the ARC has 9 fields and no trailing line break; ASCII, no BOM; LayoutVersion FSD-PROVISIONAL-1.
    [Fact]
    public void AcLay02_CrlfAfterEachAri_NineFieldArc_NoTrailingBreak_AsciiNoBom()
    {
        var lines = RfaLines.Compose([Candidate(2, "E2", 10m, 0m), Candidate(1, "E1", 20m, 0m), Candidate(3, "E3", 30m, 0m, reason: null)]);
        var bytes = RfaFileBuilder.Build(File(lines), lines).Bytes;
        var text = Encoding.ASCII.GetString(bytes);

        bytes.Should().OnlyContain(b => b < 0x80, "ASCII");
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "no BOM");
        var records = text.Split("\r\n");
        records.Should().HaveCount(4, "three ARI lines, each followed by CRLF, then the ARC");
        records.Take(3).Should().OnlyContain(r => r.StartsWith("ARI,", StringComparison.Ordinal) && r.Split(',').Length == 10);
        records[3].Should().StartWith("ARC,").And.Subject.Split(',').Should().HaveCount(9);
        text.Should().NotEndWith("\n").And.NotEndWith("\r");
        text.Count(ch => ch == '\n').Should().Be(3).And.Be(text.Count(ch => ch == '\r'), "no bare LF or CR");
        RfaFileBuilder.CurrentLayoutVersion.Should().Be("FSD-PROVISIONAL-1");
        records[2].Split(',')[5].Should().BeEmpty("E-RG8: no reason → a blank field");
    }

    // An unknown layout is refused, never guessed.
    [Fact]
    public void UnknownLayout_Throws()
    {
        var lines = RfaLines.Compose([Candidate(1, "E1", 1m, 0m)]);
        var act = () => RfaFileBuilder.Build(File(lines, "FSD-PROVISIONAL-9"), lines);
        act.Should().Throw<InvalidOperationException>();
    }

    // AC-ORD-01 (I-RG4): OrderNo 1…n by Pif_Edr.Id ascending; ARI line k carries order number k (its EDR is the k-th by id); the ARC is line n+1.
    [Fact]
    public void AcOrd01_OrderNumbersByEdrId_AriLineKIsOrderK_ArcIsLineNPlusOne()
    {
        var lines = RfaLines.Compose([Candidate(30, "E30", 3m, 0m), Candidate(10, "E10", 1m, 0m), Candidate(20, "E20", 2m, 0m)]);

        lines.Select(l => (l.OrderNo, l.EdrId)).Should().Equal((1, 10L), (2, 20L), (3, 30L));
        lines.Select(l => l.VerifiedEventId).Should().Equal(9010L, 9020L, 9030L);
        var records = Encoding.ASCII.GetString(RfaFileBuilder.Build(File(lines), lines).Bytes).Split("\r\n");
        records.Take(3).Select(r => r.Split(',')[3]).Should().Equal("E10", "E20", "E30");
        records[3].Should().StartWith("ARC,").And.Contain(",3,", "the ARC is line n+1 and carries n");
    }

    // AC-AMT-01 (A-6): Amount Returned = round2(F + V), half away from zero; ARC total = Σ rounded line amounts = TotalAmount.
    [Fact]
    public void AcAmt01_FixedPlusVariable_RoundedHalfAwayFromZero_TotalIsSumOfRounded()
    {
        RfaAmountRule.Returned(3000.00m, 74.00m).Should().Be(3074.00m);
        RfaAmountRule.Returned(0.12m, 0.005m).Should().Be(0.13m, "0.125 rounds half away from zero");
        RfaAmountRule.Returned(1.0049m, 0m).Should().Be(1.00m);

        var lines = RfaLines.Compose([Candidate(1, "E1", 0.125m, 0m), Candidate(2, "E2", 0.125m, 0m), Candidate(3, "E3", 0.120m, 0.005m)]);
        lines.Select(l => l.AmountReturned).Should().Equal(0.13m, 0.13m, 0.13m);
        lines.Select(l => (l.FixedPay, l.VariablePay)).Should().Equal((0.125m, 0m), (0.125m, 0m), (0.120m, 0.005m)); // the PIF's pay is kept unrounded
        RfaAmountRule.Total(lines).Should().Be(0.39m, "Σ rounded = 0.39, not round2(0.375) = 0.38");
        var file = File(lines);
        file.TotalAmount.Should().Be(0.39m);
        Encoding.ASCII.GetString(RfaFileBuilder.Build(file, lines).Bytes).Should().EndWith("ARC,ReturnsRef-126005299444-20260714,3,0.39,740810000, , , ,EWPMS");
    }

    // AC-RSN-04 (U half, E-RG9): a reason over 30 characters, or with a comma, CR or LF, is content the file cannot carry; a blank reason is not.
    [Theory]
    [InlineData("1234567890123456789012345678901")]
    [InlineData("AC,03")]
    [InlineData("AC03\r")]
    [InlineData("AC\n03")]
    [InlineData("AC0é")]
    public void AcRsn04_ReasonTooLongOrWithSeparator_IsInvalidContent(string reason)
    {
        RfaFileBuilder.InvalidFields("126005299444", "0000000189685", [Candidate(1, "E1", 1m, 0m, reason)])
            .Should().Equal(RfaFileBuilder.FieldReturnReasonCode);
    }

    [Fact]
    public void ValidContent_IncludingABlankReasonAndA30CharacterReason_IsCarried()
    {
        RfaFileBuilder.InvalidFields(
                "126005299444", "0000000189685",
                [Candidate(1, "E1", 1m, 0m, reason: null), Candidate(2, "E2", 1m, 0m, new string('9', 30)), Candidate(3, "E3", 1m, 0m, "006")])
            .Should().BeEmpty();
    }

    [Fact]
    public void BlankOrUncarriableIdentifiers_AreNamedOnce()
    {
        RfaFileBuilder.InvalidFields(" ", "EST,1", [Candidate(1, "", 1m, 0m), Candidate(2, "E\n2", 1m, 0m), Candidate(3, new string('7', 31), 1m, 0m)])
            .Should().Equal(RfaFileBuilder.FieldWpsFileId, RfaFileBuilder.FieldEstablishmentId, RfaFileBuilder.FieldEmployeeId);
        RfaFileBuilder.InvalidFields("1260052994440", "0000000189685", []).Should().Equal(RfaFileBuilder.FieldWpsFileId); // longer than VARCHAR(12)
    }

    // §6.4 precedence (A-3, I-RG7): the return-reason row first, then the DE outcome code, else None.
    [Fact]
    public void ReasonResolve_Precedence()
    {
        RfaReason.Resolve(7, "RB1", 9, "AC03").Should().Be(new RfaReason("RB1", RfaReasonSources.PifEdrReturnReasons, 7));
        RfaReason.Resolve(null, null, 9, "AC03").Should().Be(new RfaReason("AC03", RfaReasonSources.CbDeOutcomeRecords, 9));
        RfaReason.Resolve(null, null, 9, "  ").Should().Be(RfaReason.Missing, "a blank DE code is no reason");
        RfaReason.Resolve(null, null, null, null).Should().Be(new RfaReason(null, RfaReasonSources.None, null));
    }
}
