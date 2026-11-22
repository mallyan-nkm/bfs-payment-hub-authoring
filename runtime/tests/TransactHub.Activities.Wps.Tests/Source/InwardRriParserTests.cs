// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardRriParserTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
// Purpose: Unit coverage of the pure inward RRI parser (design Inward_RRI_Extract_Save v1.0 §5.2 steps 1a-3, §5.3, §5.4; task T3):
// AC-PRS-01..12, AC-PRS-14, AC-NAM-01..06, AC-NAM-08..11 and AC-INT-06..08. Pure logic: no SQL Server, no file system. Sample data is the
// FSD's UAT sample (X-L66-X-L70) with RriFileNameAgentCode = 027; 407 appears only where the code must not assume a value (AC-NAM-03).

using System.Globalization;
using System.Text;
using FluentAssertions;
using TransactHub.Activities.Wps.Inward;
using TransactHub.Activities.Wps.Inward.Rri;
using TransactHub.Activities.Wps.Inward.Rri.Constants;
using Xunit;

namespace TransactHub.Activities.Wps.Tests.Source;

public class InwardRriParserTests
{
    private const string FileName = "0270270000000009899609000042725.RRI";
    private const string AgentCode = "027";
    private const string EstRef = "0000000009899";

    private const string Drr1 = "DRR,0000000009899,90000000000003,100.00,005,12334567890987654322121212121222.PAF,109000005248609000042662,AE000190000000000000005, ,EWPMS";
    private const string Drr2 = "DRR,0000000009899,90000000000001,500.00,003,12334567890987654322121212121222.PAF,109000005248609000042662,AE000190000000000000004, ,EWPMS";
    private const string Drc = "DRC,,12345678901,600.00,2, , , , ,EWPMS";

    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string Sample(string eol = "\r\n", bool finalTerminator = true) =>
        string.Join(eol, Drr1, Drr2, Drc) + (finalTerminator ? eol : string.Empty);

    private static string DrrLine(
        string personId = "P1",
        string amount = "100.00",
        string reason = "005",
        string payroll = "PAYROLL.PAF",
        string payrollRef = "109000005248609000042662",
        string account = "AE000190000000000000005",
        string f2 = EstRef,
        string f9 = " ",
        string f10 = "EWPMS",
        string tag = "DRR") =>
        string.Join(',', tag, f2, personId, amount, reason, payroll, payrollRef, account, f9, f10);

    private static string DrcLine(string total = "100.00", string count = "1", string headerRef = "12345678901", string f2 = "", string rest = " , , , ,EWPMS", string tag = "DRC") =>
        string.Join(',', tag, f2, headerRef, total, count, rest);

    /// <summary>One DRR and its reconciling DRC.</summary>
    private static string OneLine(string drr) => string.Join("\n", drr, DrcLine());

    private static InwardRriParseResult Parse(string text, string fileName = FileName, string agentCode = AgentCode) =>
        InwardRriParser.Parse(Bytes(text), fileName, agentCode);

    private static InwardRriParsedFile Parsed(InwardRriParseResult r)
    {
        r.IsSuccess.Should().BeTrue(r.Rejection?.Reason);
        r.Rejection.Should().BeNull();
        return r.File!;
    }

    private static InwardRriRejection Rejected(InwardRriParseResult r, string code = InwardRriErrorCodes.Malformed)
    {
        r.IsSuccess.Should().BeFalse();
        r.File.Should().BeNull();
        r.Rejection!.ErrorCode.Should().Be(code, r.Rejection.Reason);
        return r.Rejection;
    }

    private static T UnderCulture<T>(string culture, Func<T> action)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ---- the sample (AC-PRS-14, AC-NAM-04, AC-NAM-06) ------------------------------------------------------------------------------

    [Fact]
    public void Sample_ParsesToOneHeaderAndTwoLines_InTheOrderRead() // AC-PRS-14
    {
        var f = Parsed(Parse(Sample()));

        f.Drc.Should().Be(new InwardRriControlRecord(3, "12345678901", 600.00m, 2));
        f.DrrCount.Should().Be(2);
        f.IgnoredLineCount.Should().Be(0);
        f.IgnoredLineNumbers.Should().BeEmpty();
        f.Drrs[0].Should().Be(new InwardRriDrrLine(1, "90000000000003", 100.00m, "005", "12334567890987654322121212121222.PAF", "109000005248609000042662", "AE000190000000000000005"));
        f.Drrs[1].Should().Be(new InwardRriDrrLine(2, "90000000000001", 500.00m, "003", "12334567890987654322121212121222.PAF", "109000005248609000042662", "AE000190000000000000004"));
    }

    [Fact]
    public void Sample_DerivesTheEstablishmentAndWpsFileReferencesFromTheName() // AC-NAM-04, AC-NAM-06
    {
        var f = Parsed(Parse(Sample()));

        f.EstablishmentReference.Should().Be("0000000009899");
        f.WpsFileReference.Should().Be("609000042725");
        f.Drc.HeaderWpsReference.Should().Be("12345678901", "DRC field 3 is kept apart from the name-derived reference (Q-5)");
    }

    // ---- lines (AC-PRS-01) --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\r\n", true)]
    [InlineData("\n", true)]
    [InlineData("\r", true)]
    [InlineData("\r\n", false)]
    [InlineData("\n", false)]
    public void LineEndings_CrLfLfCr_AllParse_TrailingTerminatorIsNotALine(string eol, bool finalTerminator)
    {
        var f = Parsed(Parse(Sample(eol, finalTerminator)));

        f.DrrCount.Should().Be(2);
        f.Drc.LineNo.Should().Be(3);
        f.IgnoredLineCount.Should().Be(0, "the empty segment after the final terminator is not a line");
    }

    [Fact]
    public void BlankLines_AreSkippedAndCounted_LineNoIsPhysical()
    {
        var text = string.Join("\r\n", string.Empty, Drr1, "   ", Drr2, "\t", Drc, string.Empty) + "\r\n";

        var f = Parsed(Parse(text));

        f.IgnoredLineCount.Should().Be(4);
        f.IgnoredLineNumbers.Should().Equal(1, 3, 5, 7);
        f.Drrs.Select(d => d.LineNo).Should().Equal(2, 4);
        f.Drc.LineNo.Should().Be(6);
    }

    // ---- record tag (AC-PRS-02, AC-PRS-03) ----------------------------------------------------------------------------------------

    [Fact]
    public void Tags_AreTrimmedAndCaseInsensitive() // AC-PRS-02
    {
        var f = Parsed(Parse(string.Join("\n", DrrLine(tag: "drr"), DrcLine(tag: " DRC "))));

        f.DrrCount.Should().Be(1);
        f.Drc.LineNo.Should().Be(2);
    }

    [Theory]
    [InlineData("XYZ")]
    [InlineData("ESI")]
    [InlineData("DR")]
    [InlineData("")]
    public void AnyOtherTagOnANonBlankLine_IsMalformed_NamingTheLine(string tag) // AC-PRS-03
    {
        var rej = Rejected(Parse(string.Join("\n", Drr1, DrrLine(tag: tag), Drc)));

        rej.LineNo.Should().Be(2);
        rej.Reason.Should().StartWith("Line 2:").And.Contain("neither DRC nor DRR");
    }

    // ---- field count and split (AC-PRS-04, AC-PRS-05) -----------------------------------------------------------------------------

    [Theory]
    [InlineData("DRR,0000000009899,P1,100.00,005,PAYROLL.PAF,REF,ACC, ")]           // 9
    [InlineData("DRR,0000000009899,P1,100.00,005,PAYROLL.PAF,REF,ACC, ,EWPMS,X")]   // 11
    public void ADrrWith9Or11Fields_IsMalformed_NamingTheLine(string drr) // AC-PRS-04
    {
        var rej = Rejected(Parse(string.Join("\n", Drc, drr)));

        rej.LineNo.Should().Be(2);
        rej.Reason.Should().Contain("Line 2 (DRR)").And.Contain("expected 10");
    }

    [Theory]
    [InlineData("DRC,,12345678901,100.00,1, , , ,EWPMS")]          // 9
    [InlineData("DRC,,12345678901,100.00,1, , , , ,EWPMS,X")]      // 11
    public void ADrcWith9Or11Fields_IsMalformed_NamingTheLine(string drc) // AC-PRS-04
    {
        var rej = Rejected(Parse(string.Join("\n", DrrLine(), drc)));

        rej.LineNo.Should().Be(2);
        rej.Reason.Should().Contain("Line 2 (DRC)").And.Contain("expected 10");
    }

    [Fact]
    public void ACommaInsideField6_ShiftsTheFields_IsMalformed() // AC-PRS-05
    {
        var rej = Rejected(Parse(OneLine(DrrLine(payroll: "\"PAY,ROLL.PAF\""))));

        rej.LineNo.Should().Be(1);
        rej.Reason.Should().Contain("field count is 11");
    }

    // ---- DRR fields (AC-PRS-06..09, AC-PRS-11, AC-PRS-12) -------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234567890123456789012345678901")] // 31
    [InlineData("Pé")]
    [InlineData("P\t1")]
    public void PersonId_BlankTooLongOrNotPrintableAscii_IsMalformed(string personId) // AC-PRS-06
    {
        var rej = Rejected(Parse(OneLine(DrrLine(personId: personId))));

        rej.LineNo.Should().Be(1);
        rej.FieldNumber.Should().Be(3);
    }

    [Fact]
    public void PersonId_OfThirtyCharacters_IsAccepted() // AC-PRS-06
    {
        var id = new string('9', 30);

        Parsed(Parse(OneLine(DrrLine(personId: id)))).Drrs[0].PersonId.Should().Be(id);
    }

    [Theory]
    [InlineData("100.00", "100.00")]
    [InlineData("100", "100")]
    [InlineData("0.01", "0.01")]
    [InlineData("99999999999999.99", "99999999999999.99")]
    public void Amounts_TheListedAcceptedValues_ParseUnderAnyCulture(string amount, string expected) // AC-PRS-07
    {
        var value = decimal.Parse(expected, CultureInfo.InvariantCulture);
        foreach (var culture in new[] { "en-US", "de-DE", "ar-AE" })
        {
            // DRR field 4 and DRC field 4 both carry the value, so the file reconciles.
            var f = UnderCulture(culture, () => Parsed(Parse(string.Join("\n", DrrLine(amount: amount), DrcLine(total: amount)))));

            f.Drrs[0].RefundAmount.Should().Be(value, culture);
            f.Drc.DeclaredTotalAmount.Should().Be(value, culture);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-1")]
    [InlineData("1.001")]
    [InlineData("abc")]
    [InlineData("1e3")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 100.00")]
    [InlineData("+100")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("100000000000000")]   // 15 integer digits
    [InlineData("١٠٠")] // Arabic-Indic digits
    public void DrrAmount_TheListedRejectedValues_AreMalformedUnderAnyCulture(string amount) // AC-PRS-07
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        {
            var rej = UnderCulture(culture, () => Rejected(Parse(OneLine(DrrLine(amount: amount)))));

            rej.LineNo.Should().Be(1, culture);
            rej.FieldNumber.Should().Be(4, culture);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-1")]
    [InlineData("1.001")]
    [InlineData("abc")]
    [InlineData("1e3")]
    [InlineData("")]
    public void DrcAmount_TheListedRejectedValues_AreMalformedUnderAnyCulture(string total) // AC-PRS-07
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        {
            var rej = UnderCulture(culture, () => Rejected(Parse(string.Join("\n", DrrLine(), DrcLine(total: total)))));

            rej.LineNo.Should().Be(2, culture);
            rej.FieldNumber.Should().Be(4, culture);
        }
    }

    [Fact]
    public void Amount_WithACommaDecimal_SplitsIntoElevenFields_IsMalformed() // AC-PRS-07 ("1,5")
    {
        var rej = Rejected(Parse(OneLine(DrrLine(amount: "1,5"))));

        rej.LineNo.Should().Be(1);
        rej.Reason.Should().Contain("field count is 11");
    }

    [Theory]
    [InlineData("ZZZ")]
    [InlineData("003")]
    [InlineData("1234567890")] // 10
    public void ReasonCode_HasNoDomainCheck_AndTenCharactersAreAccepted(string reason) // AC-PRS-08
    {
        Parsed(Parse(OneLine(DrrLine(reason: reason)))).Drrs[0].ReasonCode.Should().Be(reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("12345678901")] // 11
    [InlineData("00é")]
    public void ReasonCode_BlankTooLongOrNotPrintableAscii_IsMalformed(string reason) // AC-PRS-08
    {
        Rejected(Parse(OneLine(DrrLine(reason: reason)))).FieldNumber.Should().Be(5);
    }

    [Fact]
    public void PayrollFileName_AtItsLimitOrUnicode_IsAccepted_OneMoreIsMalformed() // AC-PRS-09 field 6
    {
        Parsed(Parse(OneLine(DrrLine(payroll: new string('A', 150))))).Drrs[0].PayrollFileName.Should().HaveLength(150);
        Parsed(Parse(OneLine(DrrLine(payroll: "ملف.PAF")))).Drrs[0].PayrollFileName.Should().Be("ملف.PAF", "field 6 is NVARCHAR");

        Rejected(Parse(OneLine(DrrLine(payroll: new string('A', 151))))).FieldNumber.Should().Be(6);
        Rejected(Parse(OneLine(DrrLine(payroll: " ")))).FieldNumber.Should().Be(6);
        Rejected(Parse(OneLine(DrrLine(payroll: string.Empty)))).FieldNumber.Should().Be(6);
    }

    [Theory]
    [InlineData(7, 30)]
    [InlineData(8, 34)]
    public void PayrollReferenceAndAccount_AtTheirLimit_AreAccepted_OverBlankOrNonAsciiAreMalformed(int field, int max) // AC-PRS-09 fields 7, 8
    {
        string Line(string v) => field == 7 ? DrrLine(payrollRef: v) : DrrLine(account: v);

        var atLimit = Parsed(Parse(OneLine(Line(new string('1', max))))).Drrs[0];
        (field == 7 ? atLimit.PayrollWpsFileReference : atLimit.AccountNoRaw).Should().HaveLength(max);

        foreach (var bad in new[] { new string('1', max + 1), string.Empty, "   ", "AEé1" })
        {
            Rejected(Parse(OneLine(Line(bad)))).FieldNumber.Should().Be(field, $"value of length {bad.Length}");
        }
    }

    [Fact]
    public void DrrFields9And10_AreNotValidated_AndNotStored() // AC-PRS-11
    {
        var expected = Parsed(Parse(OneLine(DrrLine()))).Drrs[0];

        foreach (var (f9, f10) in new[] { ("X", "NOTEWPMS"), (string.Empty, string.Empty), ("é", "م") })
        {
            Parsed(Parse(OneLine(DrrLine(f9: f9, f10: f10)))).Drrs[0].Should().Be(expected, "fields 9 and 10 are read for shape only");
        }
    }

    [Fact]
    public void DrcFields2And6To10_AreNotValidated_AndNotStored() // AC-PRS-11
    {
        var expected = Parsed(Parse(OneLine(DrrLine()))).Drc;

        var f = Parsed(Parse(string.Join("\n", DrrLine(), DrcLine(f2: "ANY é", rest: "a,b,م,d,MISSING"))));

        f.Drc.Should().Be(expected);
    }

    [Fact]
    public void ValidFields_AreKeptAsReceived_NotTrimmed() // AC-PRS-12
    {
        var d = Parsed(Parse(OneLine(DrrLine(personId: "A B", reason: " 05 ", payroll: " PAY.PAF ", account: " AE1 ")))).Drrs[0];

        d.PersonId.Should().Be("A B");
        d.ReasonCode.Should().Be(" 05 ");
        d.PayrollFileName.Should().Be(" PAY.PAF ");
        d.AccountNoRaw.Should().Be(" AE1 ");
    }

    // ---- DRC fields (AC-PRS-10) ---------------------------------------------------------------------------------------------------

    [Fact]
    public void HeaderWpsReference_AtItsLimit_IsAccepted_OverBlankOrNonAsciiAreMalformed() // AC-PRS-10
    {
        Parsed(Parse(string.Join("\n", DrrLine(), DrcLine(headerRef: new string('1', 35))))).Drc.HeaderWpsReference.Should().HaveLength(35);

        foreach (var bad in new[] { new string('1', 36), string.Empty, " ", "12é" })
        {
            var rej = Rejected(Parse(string.Join("\n", DrrLine(), DrcLine(headerRef: bad))));
            rej.LineNo.Should().Be(2);
            rej.FieldNumber.Should().Be(3);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("99999999999")] // over int
    public void RecordCount_NotAnIntegerOfAtLeastOne_IsMalformed(string count) // AC-PRS-10
    {
        var rej = Rejected(Parse(string.Join("\n", DrrLine(), DrcLine(count: count))));

        rej.LineNo.Should().Be(2);
        rej.FieldNumber.Should().Be(5);
    }

    [Fact]
    public void RecordCount_One_IsAccepted() // AC-PRS-10
    {
        Parsed(Parse(OneLine(DrrLine()))).Drc.DeclaredRecordCount.Should().Be(1);
    }

    // ---- file name (AC-NAM-01..03, AC-NAM-06, AC-INT-08) --------------------------------------------------------------------------

    [Fact]
    public void BaseNameOf19Characters_IsAccepted_18IsMalformed() // AC-NAM-01
    {
        Parsed(Parse(OneLine(DrrLine()), "0270270000000009899.RRI"));

        var rej = Rejected(Parse(OneLine(DrrLine()), "027027000000000989.RRI"));
        rej.Reason.Should().Contain("shorter than 19");
        rej.LineNo.Should().BeNull();
    }

    [Fact]
    public void BaseName_IsTheNameWithoutItsLastExtensionOnly() // AC-NAM-01
    {
        // No extension: the whole name is the base. Two dots: only the last extension is removed.
        Parsed(Parse(OneLine(DrrLine()), "0270270000000009899"));
        Parsed(Parse(OneLine(DrrLine()), "0270270000000009899.X.RRI")).WpsFileReference.Should().Be("0000009899.X","the base name is 0270270000000009899.X");
    }

    [Fact]
    public void AgentCode_ComparedCaseSensitively_AMismatchIsFilenameInvalid() // AC-NAM-02
    {
        const string name = "027ABC0000000009899609000042725.RRI";

        Parsed(Parse(OneLine(DrrLine()), name, "ABC"));

        foreach (var configured in new[] { "ABD", "abc", "027" })
        {
            var rej = Rejected(Parse(OneLine(DrrLine()), name, configured), InwardRriErrorCodes.FilenameInvalid);
            rej.Reason.Should().Contain("Characters 4-6").And.Contain(InwardRriParameters.KeyFileNameAgentCode).And.NotContain(configured).And.NotContain("ABC");
            rej.LineNo.Should().BeNull();
        }
    }

    [Fact]
    public void Sample_PassesWith027_IsFilenameInvalidWith407_NoValueIsAssumed() // AC-NAM-03
    {
        Parsed(Parse(Sample(), FileName, "027"));
        Rejected(Parse(Sample(), FileName, "407"), InwardRriErrorCodes.FilenameInvalid);
    }

    [Fact]
    public void AgentCodeMismatch_IsReportedBeforeAnyLineRule()
    {
        Rejected(Parse("XYZ,1\n", FileName, "407"), InwardRriErrorCodes.FilenameInvalid);
    }

    [Theory]
    [InlineData("0270270000000009899609000\u000142725.RRI")]
    [InlineData("02702700000000098996090000é4725.RRI")]
    public void ANonPrintableCharacterInTheLast12_IsMalformed(string name) // AC-NAM-06
    {
        Rejected(Parse(Sample(), name)).Reason.Should().Contain("WPS file reference");
    }

    [Fact]
    public void ANonAsciiCharacterInTheEstablishmentReference_IsMalformed()
    {
        // Positions 7-19 go to a varchar(13) column; the same printable-ASCII rule as the other stored varchars (§5.3).
        Rejected(Parse(Sample(), "027027000000é009899609000042725.RRI")).Reason.Should().Contain("establishment reference");
    }

    [Fact]
    public void AFileNameOver150Characters_IsMalformed_150IsAccepted() // AC-INT-08
    {
        var name150 = "027027" + EstRef + new string('1', 127) + ".RRI";
        var name151 = "027027" + EstRef + new string('1', 128) + ".RRI";
        name150.Should().HaveLength(150);
        name151.Should().HaveLength(151);

        Parsed(Parse(OneLine(DrrLine()), name150));
        Rejected(Parse(OneLine(DrrLine()), name151)).Reason.Should().Contain("longer than 150");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AnEmptyFileName_IsMalformed(string? name)
    {
        Rejected(Parse(Sample(), name!));
    }

    // ---- establishment reference on every DRR (AC-NAM-05) -------------------------------------------------------------------------

    [Theory]
    [InlineData("0000000009898")]
    [InlineData(" 0000000009899")]
    [InlineData("")]
    public void ADrrField2ThatDiffersFromTheEstablishmentReference_IsMalformed_NamingTheLine(string f2) // AC-NAM-05
    {
        var text = string.Join("\n", DrrLine(amount: "100.00"), DrrLine(amount: "500.00", f2: f2), DrcLine(total: "600.00", count: "2"));

        var rej = Rejected(Parse(text));

        rej.LineNo.Should().Be(2);
        rej.FieldNumber.Should().Be(2);
        rej.Reason.Should().StartWith("Line 2, field 2");
    }

    // ---- DRC position and count, DRR presence (AC-NAM-08, AC-NAM-09) --------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OneDrc_AtAnyPosition_IsAccepted_AndEveryDrrIsKept(int drcPosition) // AC-NAM-08
    {
        var lines = new List<string> { Drr1, Drr2 };
        lines.Insert(drcPosition, Drc);

        var f = Parsed(Parse(string.Join("\n", lines)));

        f.DrrCount.Should().Be(2, "lines after the DRC are read and kept");
        f.Drc.LineNo.Should().Be(drcPosition + 1);
    }

    [Fact]
    public void NoDrc_IsMalformed() // AC-NAM-08
    {
        Rejected(Parse(string.Join("\n", Drr1, Drr2))).Reason.Should().Contain("no DRC");
    }

    [Fact]
    public void TwoDrcs_IsMalformed_NamingTheSecond() // AC-NAM-08
    {
        var rej = Rejected(Parse(string.Join("\n", Drc, Drr1, Drr2, Drc)));

        rej.LineNo.Should().Be(4);
        rej.Reason.Should().Contain("2 DRC records");
    }

    [Fact]
    public void ADrcAndNoDrr_IsMalformed_NoRecordsInRri() // AC-NAM-09
    {
        var rej = Rejected(Parse(DrcLine(total: "1.00", count: "1") + "\n"));

        rej.Reason.Should().Contain(InwardRriParser.NoRecordsText);
        InwardRriParser.NoRecordsText.Should().Be("No records in RRI");
    }

    // ---- reconciliation (AC-NAM-10, AC-NAM-11) ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    public void ACountOffByOne_IsReconciliationFailed(string count) // AC-NAM-10
    {
        var rej = Rejected(Parse(string.Join("\n", Drr1, Drr2, DrcLine(total: "600.00", count: count))), InwardRriErrorCodes.ReconciliationFailed);

        rej.LineNo.Should().Be(3);
        rej.FieldNumber.Should().Be(5);
    }

    [Theory]
    [InlineData("599.99")]
    [InlineData("600.01")]
    public void ATotalOffByOneCent_IsReconciliationFailed(string total) // AC-NAM-10
    {
        var rej = Rejected(Parse(string.Join("\n", Drr1, Drr2, DrcLine(total: total, count: "2"))), InwardRriErrorCodes.ReconciliationFailed);

        rej.LineNo.Should().Be(3);
        rej.FieldNumber.Should().Be(4);
        rej.Reason.Should().NotContain("600").And.NotContain("599").And.NotContain("100.00");
    }

    [Fact]
    public void TheSumIsExactDecimal_PointOnePlusPointTwoIsPointThree() // AC-NAM-11
    {
        var f = Parsed(Parse(string.Join("\n", DrrLine(amount: "0.10"), DrrLine(amount: "0.20"), DrcLine(total: "0.30", count: "2"))));

        f.Drc.DeclaredTotalAmount.Should().Be(0.30m);
    }

    [Fact]
    public void TrailingZeros_DoNotAffectReconciliation()
    {
        Parsed(Parse(string.Join("\n", DrrLine(amount: "100"), DrrLine(amount: "500.0"), DrcLine(total: "600.00", count: "2"))));
    }

    // ---- decoding (AC-INT-06..08) -------------------------------------------------------------------------------------------------

    [Fact]
    public void ALeadingBom_IsRemoved_AndTheStagedBytesAreUntouched() // AC-INT-06
    {
        var staged = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Sample())).ToArray();
        var copy = staged.ToArray();

        var f = Parsed(InwardRriParser.Parse(staged, FileName, AgentCode));

        f.Should().BeEquivalentTo(Parsed(Parse(Sample())));
        staged.Should().Equal(copy, "the hash is taken over the bytes as staged, BOM included");
    }

    [Fact]
    public void PgpArmour_IsUnreadable_EvenAfterABom() // AC-INT-07
    {
        Rejected(Parse("-----BEGIN PGP MESSAGE-----\nabc\n"), InwardRriErrorCodes.Unreadable);
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes("-----BEGIN PGP MESSAGE-----\n")).ToArray();
        Rejected(InwardRriParser.Parse(withBom, FileName, AgentCode), InwardRriErrorCodes.Unreadable);
    }

    [Fact]
    public void AFirstByteOf0x80OrAbove_IsUnreadable() // AC-INT-07
    {
        Rejected(InwardRriParser.Parse([0x80, 0x44, 0x52, 0x52], FileName, AgentCode), InwardRriErrorCodes.Unreadable);
        Rejected(InwardRriParser.Parse([0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, 0x44], FileName, AgentCode), InwardRriErrorCodes.Unreadable);
    }

    [Fact]
    public void InvalidUtf8_IsMalformed_NamingTheLine() // AC-INT-08
    {
        var bytes = Bytes(Drr1 + "\n").Concat(new byte[] { 0x44, 0x52, 0x52, 0xC3, 0x28 }).ToArray();

        var rej = Rejected(InwardRriParser.Parse(bytes, FileName, AgentCode));

        rej.LineNo.Should().Be(2);
        rej.Reason.Should().Contain("not valid UTF-8");
    }

    [Fact]
    public void ContentFaults_AreReportedBeforeNameFaults()
    {
        Rejected(Parse("-----BEGIN PGP MESSAGE-----\n", "short.RRI"), InwardRriErrorCodes.Unreadable);
    }

    [Fact]
    public void AnEmptyFile_IsMalformed_NoDrc() // EC-3
    {
        Rejected(InwardRriParser.Parse([], FileName, AgentCode)).Reason.Should().Contain("no DRC");
        Rejected(Parse("  \r\n\r\n")).Reason.Should().Contain("no DRC");
    }

    // ---- no personal data in a reason (§5.1, I-14; the full sweep is AC-ACT-04, task T11) -----------------------------------------

    [Fact]
    public void RejectionReasons_CarryNoFieldValue()
    {
        const string person = "PERSON7777777";
        const string account = "AE99ACCOUNT7777";
        const string payroll = "PAYROLL7777.PAF";
        var cases = new[]
        {
            OneLine(DrrLine(personId: person, account: account, payroll: payroll, amount: "123.45x")),
            OneLine(DrrLine(personId: person, account: account, payroll: payroll, reason: "TOOLONGREASON")),
            OneLine(DrrLine(personId: person, account: account + new string('7', 30), payroll: payroll)),
            OneLine(DrrLine(personId: person, account: account, payroll: payroll, f2: "ESTREF7777777")),
            string.Join("\n", DrrLine(personId: person, account: account, payroll: payroll, amount: "123.45"), DrcLine(total: "123.46")),
            string.Join("\n", DrrLine(personId: person + new string('7', 30), account: account, payroll: payroll), DrcLine()),
        };

        foreach (var text in cases)
        {
            var rej = Parse(text).Rejection;
            rej.Should().NotBeNull();
            rej!.Reason.Should().NotContainAny(person, account, payroll, "123.45", "123.46", "ESTREF", FileName, EstRef);
        }
    }

    // ---- guards -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void NullBytesOrEmptyAgentCode_Throw()
    {
        FluentActions.Invoking(() => InwardRriParser.Parse(null!, FileName, AgentCode)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => InwardRriParser.Parse(Bytes(Sample()), FileName, string.Empty)).Should().Throw<ArgumentException>();
    }
}
