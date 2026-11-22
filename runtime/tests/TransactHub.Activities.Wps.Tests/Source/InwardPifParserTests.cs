// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardPifParserTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
// Purpose: Unit coverage of the pure inward PIF parser (design v2.0 §5.2 steps 1a-3, §5.3, §5.4): AC-PRS-09 and AC-PRS-11,
// with the pure halves of AC-PRS-02..08, 10, 13..15, AC-REC-01..09 and 11, and edge cases E-6, E-8..E-13, E-25..E-30.
// Pure logic: no SQL Server, no file system. Sample data is the FSD sample (REF, L203-L207).

using System.Text;
using FluentAssertions;
using TransactHub.Activities.Wps.Inward;
using Xunit;

namespace TransactHub.Activities.Wps.Tests.Source;

public class InwardPifParserTests
{
    private const string FileName = "05101234567250115090012.PAF";

    private const string Edr1 = "EDR,90000000000005,309314334,AE000930000000000000002,2018-11-01,2018-11-28,28,3025.00,0.00,0";
    private const string Edr2 = "EDR,90000000000006,309314334,AE000930000000000000003,2018-11-01,2018-11-28,28,1475.00,0.00,0";
    private const string Evp1 = "EVP,90000000000005,309314334,0.00,0.00,0.00,0.00,0.00,0.00,0.00";
    private const string Evp2 = "EVP,90000000000006,309314334,0.00,0.00,0.00,0.00,0.00,0.00,0.00";
    private const string Pcr = "PCR,0000000900001,309314334,2018-12-21,0901,112018,2,4500.00,AED,NovemberMonthSal";

    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string Join(string eol, params string[] lines) => string.Join(eol, lines);

    private static string Ref(string eol = "\r\n", bool finalTerminator = true) =>
        Join(eol, Edr1, Edr2, Evp1, Evp2, Pcr) + (finalTerminator ? eol : string.Empty);

    private static InwardPifParseResult Parse(string text, string fileName = FileName) => InwardPifParser.Parse(Bytes(text), fileName);

    private static InwardPifRejection Rejected(InwardPifParseResult r)
    {
        r.IsSuccess.Should().BeFalse();
        r.File.Should().BeNull();
        return r.Rejection!;
    }

    private static void ShouldBeMalformed(InwardPifParseResult r, int line, int field)
    {
        var rej = Rejected(r);
        rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
        rej.LineNo.Should().Be(line);
        rej.FieldNumber.Should().Be(field);
        rej.Reason.Should().Contain($"Line {line}").And.Contain($"field {field}");
    }

    // ── REF: the normal PIF (EDR + EVP + PCR) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Ref_ParsesEveryFieldAsGiven()
    {
        var r = Parse(Ref());

        r.IsSuccess.Should().BeTrue();
        r.Rejection.Should().BeNull();
        var f = r.File!;
        f.WpsFileId.Should().Be("250115090012");
        f.EdrCount.Should().Be(2);
        f.EvpCount.Should().Be(2);
        f.IgnoredLineCount.Should().Be(0);
        f.IgnoredLineNumbers.Should().BeEmpty();
        f.Warnings.Should().BeEmpty();

        var e1 = f.Edrs[0];
        e1.LineNo.Should().Be(1);
        e1.EmployeeId.Should().Be("90000000000005");
        e1.RoutingCode.Should().Be("309314334");
        e1.AccountNoRaw.Should().Be("AE000930000000000000002");
        e1.PayStartDate.Should().Be(new DateOnly(2018, 11, 1));
        e1.PayEndDate.Should().Be(new DateOnly(2018, 11, 28));
        e1.DaysInPeriod.Should().Be(28);
        e1.FixedPay.Should().Be(3025.00m);
        e1.VariablePay.Should().Be(0m);
        e1.LeaveDays.Should().Be(0);
        f.Edrs[1].LineNo.Should().Be(2);
        f.Edrs[1].FixedPay.Should().Be(1475.00m);

        f.Evps[0].LineNo.Should().Be(3);
        f.Evps[1].LineNo.Should().Be(4);
        f.Evps[0].EmployeeId.Should().Be("90000000000005");
        f.Evps[0].RoutingCode.Should().Be("309314334");

        var h = f.Header;
        h.LineNo.Should().Be(5);
        h.EstablishmentId.Should().Be("0000000900001");
        h.EmployerBankRoutingCode.Should().Be("309314334");
        h.FileCreationDate.Should().Be(new DateOnly(2018, 12, 21));
        h.FileCreationTime.Should().Be("0901");
        h.SalaryMonth.Should().Be("112018");
        h.DeclaredRecordCount.Should().Be(2);
        h.DeclaredTotalAmount.Should().Be(4500.00m);
        h.PaymentCurrency.Should().Be("AED");
        h.EmployerReference.Should().Be("NovemberMonthSal");
    }

    [Fact]
    public void Parse_Ref_HasVariablePayIsExistenceBasedOnTheEvp()
    {
        // REF's EVPs are all zeros and still flag the EDR (AC-REC-06 on REF): the flag records that an EVP exists.
        var f = Parse(Ref()).File!;
        f.Edrs.Should().OnlyContain(e => e.HasVariablePay);
    }

    // ── §5.2 step 1a, 2, 3: BOM, PGP guard, UTF-8 (E-25, AC-INT-11, AC-INT-07, AC-INT-09) ───────────────────────────────

    [Fact]
    public void Parse_OneLeadingBom_IsRemovedAndFileParsesAsRef()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Ref())).ToArray();

        var r = InwardPifParser.Parse(withBom, FileName);

        r.IsSuccess.Should().BeTrue();
        r.File!.Edrs[0].EmployeeId.Should().Be("90000000000005");
        r.File.Edrs[0].LineNo.Should().Be(1);
        r.File.EdrCount.Should().Be(2);
    }

    [Fact]
    public void Parse_TwoLeadingBoms_AreUnreadable()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF }.Concat(Bytes(Ref())).ToArray();

        Rejected(InwardPifParser.Parse(bytes, FileName)).ErrorCode.Should().Be("INWARD_PIF_UNREADABLE");
    }

    [Fact]
    public void Parse_FirstByteAtOrAbove0x80_IsUnreadable()
    {
        var bytes = new byte[] { 0x85 }.Concat(Bytes(Ref())).ToArray();

        Rejected(InwardPifParser.Parse(bytes, FileName)).ErrorCode.Should().Be("INWARD_PIF_UNREADABLE");
    }

    [Fact]
    public void Parse_FirstByte0x7F_IsBelowTheBinaryGuard_ParsedAsText_AndItsLineIsAnUnknownType()
    {
        // Boundary: only >= 0x80 trips the guard. "EDR" is an unknown record type, so the first EDR is skipped and the
        // PCR count (2) no longer matches the EDR lines (1).
        var bytes = new byte[] { 0x7F }.Concat(Bytes(Ref())).ToArray();

        var rej = Rejected(InwardPifParser.Parse(bytes, FileName));

        rej.ErrorCode.Should().Be("INWARD_PIF_RECONCILIATION_FAILED");
    }

    [Fact]
    public void Parse_PgpArmourHeader_IsUnreadableAndNeverParsedAsText()
    {
        var text = "-----BEGIN PGP MESSAGE-----\r\nhQEMA...\r\n-----END PGP MESSAGE-----\r\n";

        var rej = Rejected(Parse(text));

        rej.ErrorCode.Should().Be("INWARD_PIF_UNREADABLE");
    }

    [Fact]
    public void Parse_PgpArmourAfterOneBom_IsUnreadable()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes("-----BEGIN PGP MESSAGE-----\r\n")).ToArray();

        Rejected(InwardPifParser.Parse(bytes, FileName)).ErrorCode.Should().Be("INWARD_PIF_UNREADABLE");
    }

    [Fact]
    public void Parse_InvalidUtf8InsideTheFile_IsMalformedWithTheLine_NoWindows1252Fallback()
    {
        // 0xE9 is "e acute" in Windows-1252 but an invalid UTF-8 lead byte with no continuation.
        var head = Bytes(Edr1 + "\r\n");
        var bad = Bytes("EVP,3010203801605").Concat(new byte[] { 0xE9 }).Concat(Bytes(",309314334,0.00,0.00,0.00,0.00,0.00,0.00,0.00\r\n")).ToArray();

        var rej = Rejected(InwardPifParser.Parse(head.Concat(bad).ToArray(), FileName));

        rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
        rej.LineNo.Should().Be(2);
        rej.Reason.Should().Contain("UTF-8");
    }

    [Fact]
    public void Parse_EmptyBytes_HasNoPcr_ReconciliationFailed()
    {
        var rej = Rejected(InwardPifParser.Parse([], FileName));

        rej.ErrorCode.Should().Be("INWARD_PIF_RECONCILIATION_FAILED");
    }

    // ── §5.3 line rules (G-3, E-26, AC-PRS-13, E-10, AC-PRS-06) ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\n", true)]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\r", true)]
    [InlineData("\r", false)]
    public void Parse_AnyTerminatorWithOrWithoutFinal_GivesTheSameLinesAndCounts(string eol, bool finalTerminator)
    {
        var f = Parse(Ref(eol, finalTerminator)).File!;

        f.IgnoredLineCount.Should().Be(0);
        f.Edrs.Select(e => e.LineNo).Should().Equal(1, 2);
        f.Evps.Select(e => e.LineNo).Should().Equal(3, 4);
        f.Header.LineNo.Should().Be(5);
    }

    [Fact]
    public void Parse_OneInsertedBlankLine_IsCountedAndShiftsLaterLineNumbers()
    {
        var text = Join("\r\n", Edr1, Edr2, string.Empty, Evp1, Evp2, Pcr) + "\r\n";

        var f = Parse(text).File!;

        f.IgnoredLineCount.Should().Be(1);
        f.IgnoredLineNumbers.Should().Equal(3);
        f.Evps.Select(e => e.LineNo).Should().Equal(4, 5);
    }

    [Fact]
    public void Parse_AFinalBlankLineBeyondTheLastTerminator_IsOneBlankLine_ButTheEmptySegmentAfterItIsNot()
    {
        // "...PCR\r\n\r\n": the second CRLF ends a real blank line 6; nothing follows it.
        var f = Parse(Ref() + "\r\n").File!;

        f.IgnoredLineCount.Should().Be(1);
        f.IgnoredLineNumbers.Should().Equal(6);
    }

    [Fact]
    public void Parse_CrLfIsOneTerminator_NotTwo()
    {
        Parse(Ref("\r\n")).File!.IgnoredLineCount.Should().Be(0);
    }

    [Fact]
    public void Parse_UnknownRecordType_IsSkippedAndCounted_WithItsLineNumber()
    {
        var text = Join("\r\n", Edr1, "XYZ,1,2", Edr2, string.Empty, Evp1, Evp2, Pcr);

        var f = Parse(text).File!;

        f.IgnoredLineCount.Should().Be(2);
        f.IgnoredLineNumbers.Should().Equal(2, 4);
        f.Edrs.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_UnknownTypeLine_IsNeverFieldChecked()
    {
        // An unknown line may have any shape, including non-ASCII text.
        var text = Join("\n", Edr1, Edr2, "NOTE,éé", Evp1, Evp2, Pcr);

        Parse(text).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Parse_LowerCaseRecordTypes_AreUnknown_AndTheFileFailsReconciliation()
    {
        // E-11 / AC-PRS-07: the lower-case EDRs are skipped, so the PCR count 2 != 0. Reconciliation precedes EVP matching.
        var text = Join("\r\n", Edr1.Replace("EDR", "edr"), Edr2.Replace("EDR", "edr"), Evp1, Evp2, Pcr);

        var rej = Rejected(Parse(text));

        rej.ErrorCode.Should().Be("INWARD_PIF_RECONCILIATION_FAILED");
    }

    // ── §5.3 field table: field counts (AC-PRS-02) ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("EDR")]
    [InlineData("EVP")]
    [InlineData("PCR")]
    public void Parse_NineOrElevenFields_IsMalformed_NamingTheLine(string type)
    {
        var lines = new Dictionary<string, string> { ["EDR"] = Edr1, ["EVP"] = Evp1, ["PCR"] = Pcr };
        var good = lines[type];
        foreach (var bad in new[] { good[..good.LastIndexOf(',')], good + ",x" })
        {
            var text = Join("\r\n", type == "EDR" ? bad : Edr1, type == "EVP" ? bad : Evp1, type == "PCR" ? bad : Pcr);

            var rej = Rejected(Parse(text));

            rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
            rej.Reason.Should().Contain("field count").And.Contain($"({type})");
            rej.LineNo.Should().NotBeNull();
        }
    }

    [Fact]
    public void Parse_RecordTypeAloneOnALine_IsAFieldCountError_NotAnUnknownLine()
    {
        ShouldBeMalformedLine(Parse(Join("\r\n", Edr1, "EDR", Pcr)), 2);
    }

    private static void ShouldBeMalformedLine(InwardPifParseResult r, int line)
    {
        var rej = Rejected(r);
        rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
        rej.LineNo.Should().Be(line);
    }

    // ── §5.3 field table: types and bounds (AC-PRS-03, 04, 08) ──────────────────────────────────────────────────────────

    private static string Edr(int field, string value)
    {
        var f = Edr1.Split(',');
        f[field - 1] = value;
        return string.Join(',', f);
    }

    private static string EvpWith(int field, string value)
    {
        var f = Evp1.Split(',');
        f[field - 1] = value;
        return string.Join(',', f);
    }

    private static string PcrWith(int field, string value)
    {
        var f = Pcr.Split(',');
        f[field - 1] = value;
        return string.Join(',', f);
    }

    private static InwardPifParseResult ParseEdrOnly(string edr, string? pcr = null) =>
        Parse(Join("\r\n", edr, pcr ?? "PCR,0000000900001,309314334,2018-12-21,0901,112018,1,3025.00,AED,Ref"));

    [Theory]
    [InlineData("2018-11-1")]
    [InlineData("2018/11/01")]
    [InlineData("01-11-2018")]
    [InlineData("2018-13-01")]
    [InlineData("2018-02-30")]
    [InlineData("20181101")]
    [InlineData(" 2018-11-01")]
    [InlineData("")]
    public void Parse_EdrBadDate_IsMalformed(string value)
    {
        ShouldBeMalformed(ParseEdrOnly(Edr(5, value)), 1, 5);
        ShouldBeMalformed(ParseEdrOnly(Edr(6, value)), 1, 6);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(" 28")]
    [InlineData("+28")]
    [InlineData("2147483648")]
    public void Parse_EdrDaysAndLeaveDays_MustBeNonNegativeIntegers(string value)
    {
        ShouldBeMalformed(ParseEdrOnly(Edr(7, value)), 1, 7);
        ShouldBeMalformed(ParseEdrOnly(Edr(10, value)), 1, 10);
    }

    [Fact]
    public void Parse_EdrZeroDays_IsValid()
    {
        ParseEdrOnly(Edr(7, "0")).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("-3025.00")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("\"3025.00\"")]
    [InlineData(" 3025.00")]
    [InlineData("3025.00 ")]
    [InlineData("1e3")]
    [InlineData("1,5")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("+5")]
    [InlineData("-0")]
    public void Parse_EdrAmount_NonNumericNegativeQuotedOrSpaced_IsMalformed(string value)
    {
        // "1,5" splits into an extra field, so it is a field count error on the same line; the rest fail on field 8.
        var rej = Rejected(ParseEdrOnly(Edr(8, value)));

        rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
        rej.LineNo.Should().Be(1);
    }

    [Theory]
    [InlineData("3025.00001")]
    [InlineData("3025.00000")]
    [InlineData("1.00000")]
    [InlineData("0.123456")]
    public void Parse_AmountWithMoreThanFourDecimalPlaces_IsMalformed_TrailingZerosCount(string value)
    {
        ShouldBeMalformed(ParseEdrOnly(Edr(8, value)), 1, 8);
        ShouldBeMalformed(ParseEdrOnly(Edr(9, value)), 1, 9);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("3025.5", "3025.5")]
    [InlineData("3025.50", "3025.50")]
    [InlineData("3025.123", "3025.123")]
    [InlineData("3025.1234", "3025.1234")]
    [InlineData("3025.0000", "3025")]
    [InlineData("99999999999999.9999", "99999999999999.9999")]
    public void Parse_AmountWithZeroToFourDecimals_IsStoredExactly(string value, string expected)
    {
        var r = ParseEdrOnly(Edr(8, value), $"PCR,0000000900001,309314334,2018-12-21,0901,112018,1,{value},AED,Ref");

        r.IsSuccess.Should().BeTrue();
        r.File!.Edrs[0].FixedPay.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Parse_AmountWithMoreThanFourteenIntegerDigits_IsMalformed_NeverASqlOverflow()
    {
        ShouldBeMalformed(ParseEdrOnly(Edr(8, "100000000000000.00")), 1, 8);
    }

    [Fact]
    public void Parse_AmountPreservesFourDecimalPlaces_NothingIsRounded()
    {
        var r = ParseEdrOnly(Edr(8, "3025.1234"), "PCR,0000000900001,309314334,2018-12-21,0901,112018,1,3025.1234,AED,Ref");

        r.File!.Edrs[0].FixedPay.Should().Be(3025.1234m);
        r.File.Header.DeclaredTotalAmount.Should().Be(3025.1234m);
    }

    [Theory]
    [InlineData(2, 30, 31)]
    [InlineData(3, 20, 21)]
    [InlineData(4, 34, 35)]
    public void Parse_EdrTextLengths_BoundaryAcceptedOneOverRejected(int field, int max, int over)
    {
        ParseEdrOnly(Edr(field, new string('A', max))).IsSuccess.Should().BeTrue();
        ShouldBeMalformed(ParseEdrOnly(Edr(field, new string('A', over))), 1, field);
    }

    [Theory]
    [InlineData(2, 30, 31)]
    [InlineData(3, 20, 21)]
    public void Parse_EvpTextLengths_BoundaryAcceptedOneOverRejected(int field, int max, int over)
    {
        string Text(string evp) => Join("\r\n", Edr1, evp, Pcr.Replace(",2,4500.00", ",1,3025.00"));

        // The EVP id must match an EDR, so the boundary case builds a matching EDR when field is 2.
        if (field == 2)
        {
            var id = new string('7', max);
            var edr = Edr(2, id);
            var evp = EvpWith(2, id);
            Parse(Join("\r\n", edr, evp, Pcr.Replace(",2,4500.00", ",1,3025.00"))).IsSuccess.Should().BeTrue();
            ShouldBeMalformed(Parse(Text(EvpWith(2, new string('7', over)))), 2, 2);
        }
        else
        {
            Parse(Text(EvpWith(3, new string('7', max)))).IsSuccess.Should().BeTrue();
            ShouldBeMalformed(Parse(Text(EvpWith(3, new string('7', over)))), 2, 3);
        }
    }

    [Theory]
    [InlineData(2, 35, 36)]
    [InlineData(3, 20, 21)]
    [InlineData(10, 35, 36)]
    public void Parse_PcrTextLengths_BoundaryAcceptedOneOverRejected(int field, int max, int over)
    {
        string Text(string pcr) => Join("\r\n", Edr1, Edr2, pcr);

        Parse(Text(PcrWith(field, new string('A', max)))).IsSuccess.Should().BeTrue();
        ShouldBeMalformed(Parse(Text(PcrWith(field, new string('A', over)))), 3, field);
    }

    [Theory]
    [InlineData("AE")]
    [InlineData("AEDX")]
    [InlineData("A1D")]
    [InlineData("")]
    [InlineData("A D")]
    public void Parse_PcrCurrency_MustBeThreeLetters(string value)
    {
        ShouldBeMalformed(Parse(Join("\r\n", Edr1, Edr2, PcrWith(9, value))), 3, 9);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("2.5")]
    [InlineData("x")]
    [InlineData("")]
    public void Parse_PcrEdrCount_MustBeANonNegativeInteger(string value)
    {
        ShouldBeMalformed(Parse(Join("\r\n", Edr1, Edr2, PcrWith(7, value))), 3, 7);
    }

    [Fact]
    public void Parse_EvpAllowances_AreFieldsFourToTen_InOfficialOrder()
    {
        var evp = "EVP,90000000000005,309314334,1.10,2.20,3.30,4.40,5.50,6.60,7.70";
        var text = Join("\r\n", Edr1, evp, "PCR,0000000900001,309314334,2018-12-21,0901,112018,1,3025.00,AED,Ref");

        var e = Parse(text).File!.Evps[0];

        e.RoutingCode.Should().Be("309314334");
        e.Housing.Should().Be(1.10m);
        e.Conveyance.Should().Be(2.20m);
        e.Medical.Should().Be(3.30m);
        e.AnnualPassage.Should().Be(4.40m);
        e.Overtime.Should().Be(5.50m);
        e.Other.Should().Be(6.60m);
        e.LeaveEncashment.Should().Be(7.70m);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(10)]
    public void Parse_EvpAllowance_BadValues_AreMalformed(int field)
    {
        string Text(string v) => Join("\r\n", Edr1, EvpWith(field, v), "PCR,0000000900001,309314334,2018-12-21,0901,112018,1,3025.00,AED,Ref");

        ShouldBeMalformed(Parse(Text("-1.00")), 2, field);
        ShouldBeMalformed(Parse(Text("x")), 2, field);
        ShouldBeMalformed(Parse(Text("1.00000")), 2, field);
    }

    // ── PCR f5 HHmm, f6 MMyyyy (AC-PRS-14, E-27) ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2460")]
    [InlineData("0960")]
    [InlineData("901")]
    [InlineData("09011")]
    [InlineData("24:0")]
    [InlineData("")]
    [InlineData("abcd")]
    public void Parse_PcrFileCreationTime_BadValues_AreMalformed(string value)
    {
        ShouldBeMalformed(Parse(Join("\r\n", Edr1, Edr2, PcrWith(5, value))), 3, 5);
    }

    [Theory]
    [InlineData("0000")]
    [InlineData("2359")]
    [InlineData("0901")]
    public void Parse_PcrFileCreationTime_BoundaryValues_AreStoredAsText(string value)
    {
        var f = Parse(Join("\r\n", Edr1, Edr2, PcrWith(5, value))).File!;

        f.Header.FileCreationTime.Should().Be(value);
    }

    [Theory]
    [InlineData("132018")]
    [InlineData("002018")]
    [InlineData("11201")]
    [InlineData("1120181")]
    [InlineData("1120a8")]
    [InlineData("")]
    public void Parse_PcrSalaryMonth_BadValues_AreMalformed(string value)
    {
        ShouldBeMalformed(Parse(Join("\r\n", Edr1, Edr2, PcrWith(6, value))), 3, 6);
    }

    [Theory]
    [InlineData("122018")]
    [InlineData("012018")]
    [InlineData("110000")]
    [InlineData("119999")]
    public void Parse_PcrSalaryMonth_ValidMonthAndAnyFourDigitYear_IsStoredAsText(string value)
    {
        Parse(Join("\r\n", Edr1, Edr2, PcrWith(6, value))).File!.Header.SalaryMonth.Should().Be(value);
    }

    [Fact]
    public void Parse_PcrFileCreationDate_BadFormat_IsMalformed()
    {
        ShouldBeMalformed(Parse(Join("\r\n", Edr1, Edr2, PcrWith(4, "21-12-2018"))), 3, 4);
    }

    // ── ASCII rule (R3 CA-11, AC-PRS-15, E-29) ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("é")]
    [InlineData(" ")]
    [InlineData("ا")]
    [InlineData("\u007f")]
    [InlineData("\t")]
    public void Parse_NonAsciiOrControlInVarcharFields_IsMalformed(string bad)
    {
        // EDR f2, f3, f4 (the id and routing code kept short so only the ASCII rule can fail)
        ShouldBeMalformed(ParseEdrOnly(Edr(2, "12" + bad + "34")), 1, 2);
        ShouldBeMalformed(ParseEdrOnly(Edr(3, "30" + bad)), 1, 3);
        ShouldBeMalformed(ParseEdrOnly(Edr(4, "AE" + bad + "47")), 1, 4);

        // EVP f2, f3
        string EvpText(string evp) => Join("\r\n", Edr1, evp, Pcr.Replace(",2,4500.00", ",1,3025.00"));
        ShouldBeMalformed(Parse(EvpText(EvpWith(2, "12" + bad + "34"))), 2, 2);
        ShouldBeMalformed(Parse(EvpText(EvpWith(3, "30" + bad))), 2, 3);

        // PCR f3, f5, f6, f9
        string PcrText(string pcr) => Join("\r\n", Edr1, Edr2, pcr);
        ShouldBeMalformed(Parse(PcrText(PcrWith(3, "30" + bad))), 3, 3);
        ShouldBeMalformed(Parse(PcrText(PcrWith(5, "09" + bad + "1"))), 3, 5);
        ShouldBeMalformed(Parse(PcrText(PcrWith(6, "11" + bad + "01"))), 3, 6);
        ShouldBeMalformed(Parse(PcrText(PcrWith(9, "A" + bad + "D"))), 3, 9);
    }

    [Fact]
    public void Parse_NonAsciiInPcrNvarcharFields_EstablishmentIdAndEmployerReference_IsSavedIntact()
    {
        var pcr = PcrWith(2, "Shérikah 01");
        pcr = pcr.Replace("NovemberMonthSal", "Salaire décembre");

        var f = Parse(Join("\r\n", Edr1, Edr2, pcr)).File!;

        f.Header.EstablishmentId.Should().Be("Shérikah 01");
        f.Header.EmployerReference.Should().Be("Salaire décembre");
    }

    [Fact]
    public void Parse_PrintableAsciiBoundaries_SpaceAndTilde_AreAccepted()
    {
        ParseEdrOnly(Edr(4, "AE 47~")).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Parse_FieldIsTakenLiterally_ATrailingSpaceInF3IsKept_NotTrimmed()
    {
        var f = ParseEdrOnly(Edr(3, "309314334 ")).File!;

        f.Edrs[0].RoutingCode.Should().Be("309314334 ");
    }

    [Fact]
    public void Parse_QuotesAreNotStripped_AQuotedTextFieldIsStoredWithItsQuotes()
    {
        var f = ParseEdrOnly(Edr(2, "\"90000000000005\"")).File!;

        f.Edrs[0].EmployeeId.Should().Be("\"90000000000005\"");
    }

    // ── 12-character identifier (§5.3, AC-PRS-09) ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("05101234567250115090012.PAF", "250115090012")]
    [InlineData("A.B.250115090012.PAF", "250115090012")]
    [InlineData("A.B250115090012.PAF", "250115090012")] // base "A.B250115090012": last 12 characters
    [InlineData("250115090012.PAF", "250115090012")]
    [InlineData("XX250115090012.paf", "250115090012")]
    [InlineData("250115090012", "250115090012")] // design choice: no extension, whole name is the base
    public void Parse_WpsFileId_IsTheLastTwelveCharactersOfTheNameWithoutItsLastExtension(string name, string expected)
    {
        var r = Parse(Ref(), name);

        r.IsSuccess.Should().BeTrue();
        r.File!.WpsFileId.Should().Be(expected);
    }

    [Fact]
    public void Parse_MultiDotName_UsesTheLastExtensionOnly()
    {
        Parse(Ref(), "A.B.250115090012.PAF").File!.WpsFileId.Should().Be("250115090012");
    }

    [Theory]
    [InlineData("SHORT.PAF")]
    [InlineData("12345678901.PAF")] // 11 characters
    [InlineData(".PAF")]
    [InlineData("")]
    public void Parse_BaseNameShorterThanTwelve_IsMalformed(string name)
    {
        var rej = Rejected(Parse(Ref(), name));

        rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
        rej.Reason.Should().Contain("12");
    }

    [Fact]
    public void Parse_ExactlyTwelveCharacterBase_IsAccepted()
    {
        Parse(Ref(), "250115090012.PAF").File!.WpsFileId.Should().Be("250115090012");
    }

    [Fact]
    public void Parse_NonAsciiInTheIdentifierTakenFromTheFileName_IsMalformed()
    {
        var rej = Rejected(Parse(Ref(), "2501150900é12.PAF"));

        rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
        rej.Reason.Should().Contain("identifier");
    }

    [Fact]
    public void Parse_NonAsciiBeforeTheIdentifierWindow_DoesNotMatter()
    {
        Parse(Ref(), "éé250115090012.PAF").IsSuccess.Should().BeTrue();
    }

    // ── §5.4 reconciliation (AC-REC-01..05, E-6, E-7) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_PcrCountDiffersFromEdrLines_IsReconciliationFailed()
    {
        var rej = Rejected(Parse(Join("\r\n", Edr1, Edr2, Evp1, Evp2, PcrWith(7, "3"))));

        rej.ErrorCode.Should().Be("INWARD_PIF_RECONCILIATION_FAILED");
        rej.LineNo.Should().Be(5);
        rej.FieldNumber.Should().Be(7);
    }

    [Fact]
    public void Parse_PcrTotalOffByOneCent_IsReconciliationFailed()
    {
        var rej = Rejected(Parse(Join("\r\n", Edr1, Edr2, Evp1, Evp2, PcrWith(8, "4500.01"))));

        rej.ErrorCode.Should().Be("INWARD_PIF_RECONCILIATION_FAILED");
        rej.FieldNumber.Should().Be(8);
    }

    [Fact]
    public void Parse_TotalIsFixedPlusVariable_ReconcilesWhenVariablePayIsIncluded()
    {
        var edr1 = Edr(9, "100.00");

        Parse(Join("\r\n", edr1, Edr2, Evp1, Evp2, PcrWith(8, "4600.00"))).IsSuccess.Should().BeTrue();
        Rejected(Parse(Join("\r\n", edr1, Edr2, Evp1, Evp2, PcrWith(8, "4500.00")))).ErrorCode.Should().Be("INWARD_PIF_RECONCILIATION_FAILED");
    }

    [Fact]
    public void Parse_TotalComparedAtFourDecimals_TrailingZerosDoNotMatter()
    {
        Parse(Join("\r\n", Edr1, Edr2, Evp1, Evp2, PcrWith(8, "4500.0000"))).IsSuccess.Should().BeTrue();
        Rejected(Parse(Join("\r\n", Edr1, Edr2, Evp1, Evp2, PcrWith(8, "4500.0001")))).ErrorCode.Should().Be("INWARD_PIF_RECONCILIATION_FAILED");
    }

    [Fact]
    public void Parse_NoPcr_IsReconciliationFailed()
    {
        var rej = Rejected(Parse(Join("\r\n", Edr1, Edr2, Evp1, Evp2)));

        rej.ErrorCode.Should().Be("INWARD_PIF_RECONCILIATION_FAILED");
        rej.Reason.Should().Contain("no PCR");
    }

    [Fact]
    public void Parse_TwoPcrs_IsReconciliationFailed()
    {
        var rej = Rejected(Parse(Join("\r\n", Edr1, Edr2, Evp1, Evp2, Pcr, Pcr)));

        rej.ErrorCode.Should().Be("INWARD_PIF_RECONCILIATION_FAILED");
        rej.LineNo.Should().Be(6);
    }

    [Fact]
    public void Parse_PcrFirst_IsAccepted_PlacementIsNotChecked()
    {
        var f = Parse(Join("\r\n", Pcr, Edr1, Edr2, Evp1, Evp2)).File!;

        f.Header.LineNo.Should().Be(1);
        f.Edrs.Select(e => e.LineNo).Should().Equal(2, 3);
    }

    [Fact]
    public void Parse_PcrOnlyWithZeroCountAndTotal_IsAccepted()
    {
        var r = Parse("PCR,0000000900001,309314334,2018-12-21,0901,112018,0,0.00,AED,Ref\r\n");

        r.IsSuccess.Should().BeTrue();
        r.File!.EdrCount.Should().Be(0);
        r.File.EvpCount.Should().Be(0);
        r.File.Header.DeclaredTotalAmount.Should().Be(0m);
    }

    [Fact]
    public void Parse_MalformedLineBeatsAMissingPcr_BecauseParsingCompletesBeforeReconciliation()
    {
        var rej = Rejected(Parse(Join("\r\n", Edr(5, "bad"), Edr2)));

        rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
    }

    [Fact]
    public void Parse_FirstBadFieldWins_InFileOrderThenFieldOrder()
    {
        var text = Join("\r\n", Edr(7, "x"), Edr(5, "bad"), Pcr);

        ShouldBeMalformed(Parse(text), 1, 7);
        ShouldBeMalformed(Parse(Join("\r\n", Edr(5, "bad").Replace("28,3025.00", "x,3025.00"), Pcr)), 1, 5);
    }

    // ── EVP matching (A3-G-10, E-8, E-9, AC-REC-06..08, AC-REC-11, E-30) ────────────────────────────────────────────────

    [Fact]
    public void Parse_EvpBeforeItsEdr_IsMatchedAfterTheWholeFileIsParsed()
    {
        var f = Parse(Join("\r\n", Evp1, Edr1, Edr2, Evp2, Pcr)).File!;

        f.Edrs.Single(e => e.EmployeeId == "90000000000005").HasVariablePay.Should().BeTrue();
        f.Edrs.Single(e => e.EmployeeId == "90000000000006").HasVariablePay.Should().BeTrue();
    }

    [Fact]
    public void Parse_EvpWithoutAnEdr_IsMalformed_NamingTheEvpLine()
    {
        var orphan = Evp2.Replace("90000000000006", "99999999999999");

        var rej = Rejected(Parse(Join("\r\n", Edr1, Edr2, Evp1, orphan, Pcr)));

        rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
        rej.LineNo.Should().Be(4);
        rej.FieldNumber.Should().Be(2);
        rej.Reason.Should().NotContain("99999999999999", "no employee ids in logs");
    }

    [Fact]
    public void Parse_TwoEdrsWithTheSameEmployeeId_IsMalformed()
    {
        var dup = Edr2.Replace("90000000000006", "90000000000005");

        var rej = Rejected(Parse(Join("\r\n", Edr1, dup, Pcr)));

        rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
        rej.LineNo.Should().Be(2);
        rej.FieldNumber.Should().Be(2);
        rej.Reason.Should().Contain("duplicate");
    }

    [Fact]
    public void Parse_EmployeeIdMatching_IsExactAndCaseSensitive()
    {
        var edr = Edr(2, "ab123");
        var evp = EvpWith(2, "AB123");

        var rej = Rejected(Parse(Join("\r\n", edr, evp, "PCR,0000000900001,309314334,2018-12-21,0901,112018,1,3025.00,AED,Ref")));

        rej.ErrorCode.Should().Be("INWARD_PIF_MALFORMED");
    }

    [Fact]
    public void Parse_ThreeEdrsWithEvpsForTwo_FlagsExactlyThoseTwo()
    {
        var edr3 = "EDR,90000000000007,309314334,AE000000000000000000001,2018-11-01,2018-11-28,28,500.00,0.00,0";
        var pcr = "PCR,0000000900001,309314334,2018-12-21,0901,112018,3,5000.00,AED,Ref";

        var f = Parse(Join("\r\n", Edr1, Edr2, edr3, Evp1, Evp2, pcr)).File!;

        f.Edrs.Select(e => e.HasVariablePay).Should().Equal(true, true, false);
    }

    [Fact]
    public void Parse_NoEvpLines_IsValid_AllFlagsFalse()
    {
        var f = Parse(Join("\r\n", Edr1, Edr2, Pcr)).File!;

        f.EvpCount.Should().Be(0);
        f.Evps.Should().BeEmpty();
        f.Edrs.Should().OnlyContain(e => !e.HasVariablePay);
    }

    [Fact]
    public void Parse_TwoEvpsForOneEmployee_AreBothKept_DesignChoice()
    {
        var f = Parse(Join("\r\n", Edr1, Edr2, Evp1, Evp1, Evp2, Pcr)).File!;

        f.EvpCount.Should().Be(3);
    }

    [Fact]
    public void Parse_EdrOnlyFile_WithMatchingPcr_IsAccepted()
    {
        var f = Parse(Join("\r\n", Edr1, Edr2, Pcr)).File!;

        f.EdrCount.Should().Be(2);
    }

    // ── currency warning (E-13, AC-REC-09) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NonAedCurrency_IsSaved_WithAWarningNamingTheCurrency()
    {
        var f = Parse(Join("\r\n", Edr1, Edr2, Evp1, Evp2, PcrWith(9, "USD"))).File!;

        f.Header.PaymentCurrency.Should().Be("USD");
        f.Warnings.Should().ContainSingle().Which.Should().Contain("USD");
    }

    // ── purity (AC-PRS-11) ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_IsDeterministic_AndDoesNotMutateTheInputBytes()
    {
        var bytes = Bytes(Ref());
        var copy = (byte[])bytes.Clone();

        var a = InwardPifParser.Parse(bytes, FileName);
        var b = InwardPifParser.Parse(bytes, FileName);

        bytes.Should().Equal(copy);
        a.File!.Edrs.Should().Equal(b.File!.Edrs);
        a.File.Header.Should().Be(b.File.Header);
    }

    [Fact]
    public void Parse_NullBytes_Throws()
    {
        Action act = () => InwardPifParser.Parse(null!, FileName);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Parse_RejectionReasons_NeverContainFieldValues()
    {
        var rej = Rejected(ParseEdrOnly(Edr(4, "AE47é90000000000002")));

        rej.Reason.Should().NotContain("AE47").And.NotContain("0934");
    }
}
