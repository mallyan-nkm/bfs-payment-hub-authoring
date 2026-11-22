// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardPsiParserTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
// Purpose: Unit coverage of the pure inward PSI parser (PSI design v2.0 §5.2 steps 1a-3, §5.3, §5.4; task T3):
// AC-PRS-11, AC-PRS-13, AC-PRS-14, AC-PRS-15, AC-CTL-05, plus the pure halves of AC-PRS-01..10, AC-PRS-12 and
// AC-CTL-01..04. Pure logic: no SQL Server, no file system. Sample data is the FSD's UAT sample (REF, L302-L304).

using System.Text;
using FluentAssertions;
using TransactHub.Activities.Wps.Inward;
using Xunit;

namespace TransactHub.Activities.Wps.Tests.Source;

public class InwardPsiParserTests
{
    private const string FileName = "041041DOM784000000000001126000005935.PSI";

    // REF: every line has 9 fields (the UAT sample), the last Future field present carries EWPMS.
    private const string Esi1 = "ESI,DOM205590910784000000000002,EMPLOYEE ALPHA SYNTHETIC NAME ONE,90000001, , , , ,EWPMS";
    private const string Esi2 = "ESI,DOM205840528784000000000003,EMPLOYEE BETA TWO,900000002, , , , ,EWPMS";
    private const string Csi = "CSI,DOM784000000000001,EMPLOYER GAMMA SYNTHETIC, , , , , ,EWPMS";

    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string Ref(string eol = "\r\n", bool finalTerminator = true) =>
        string.Join(eol, Esi1, Esi2, Csi) + (finalTerminator ? eol : string.Empty);

    private static InwardPsiParseResult Parse(string text, string fileName = FileName) => InwardPsiParser.Parse(Bytes(text), fileName);

    private static InwardPsiParsedFile Parsed(InwardPsiParseResult r)
    {
        r.IsSuccess.Should().BeTrue(r.Rejection?.Reason);
        r.Rejection.Should().BeNull();
        return r.File!;
    }

    private static InwardPsiRejection Rejected(InwardPsiParseResult r, string code = InwardPsiParseErrorCodes.Malformed)
    {
        r.IsSuccess.Should().BeFalse();
        r.File.Should().BeNull();
        r.Rejection!.ErrorCode.Should().Be(code);
        return r.Rejection;
    }

    // ---- the reference file (AC-PRS-01, pure half) ---------------------------------------------------------------------------------

    [Fact]
    public void Ref_ParsesFieldByField_LeftToRight()
    {
        var f = Parsed(Parse(Ref()));

        f.WpsFileId.Should().Be("126000005935");
        f.EsiCount.Should().Be(2);
        f.IgnoredLineCount.Should().Be(0);
        f.Csi.Should().Be(new InwardPsiControlRecord(3, "DOM784000000000001", "EMPLOYER GAMMA SYNTHETIC", " ", " ", " ", " ", " ", "EWPMS", null));
        f.Esis[0].Should().Be(new InwardPsiEsiLine(1, "DOM205590910784000000000002", "EMPLOYEE ALPHA SYNTHETIC NAME ONE", "90000001", " ", " ", " ", " ", "EWPMS", null));
        f.Esis[1].LineNo.Should().Be(2);
        f.Esis[1].LaborCardNo.Should().Be("900000002");
    }

    [Fact]
    public void TenFieldLines_FillTheLastFutureSlot() // AC-PRS-02
    {
        var f = Parsed(Parse(string.Join("\n", Esi1 + ",X6", Esi2 + ",", Csi + ",Y7")));

        f.Esis[0].Future6.Should().Be("X6");
        f.Esis[1].Future6.Should().Be(string.Empty, "an empty 10th field is present, so it is the empty string, not null");
        f.Csi.Future7.Should().Be("Y7");
    }

    // ---- field count (AC-PRS-03) --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ESI,P1,Name,LC")]                              // 4
    [InlineData("ESI,P1,Name,LC,1,2,3,4")]                      // 8
    [InlineData("ESI,P1,Name,LC,1,2,3,4,5,6,7")]                // 11
    [InlineData("ESI,P1,Name")]                                 // 3
    public void AnEsiWithAnyCountOtherThan9Or10_IsMalformed(string esi)
    {
        var rej = Rejected(Parse(string.Join("\n", esi, Csi)));
        rej.LineNo.Should().Be(1);
        rej.Reason.Should().Contain("field count is").And.Contain("expected 9 or 10");
    }

    [Theory]
    [InlineData("CSI,E1,Employer,1,2,3,4,5")]       // 8
    [InlineData("CSI,E1,Employer,1,2,3,4,5,6,7,8")] // 11
    public void ACsiWithAnyCountOtherThan9Or10_IsMalformed(string csi)
    {
        Rejected(Parse(string.Join("\n", Esi1, csi))).LineNo.Should().Be(2);
    }

    // ---- record tag (AC-PRS-04) ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Tags_AreTrimmedAndCaseInsensitive()
    {
        var f = Parsed(Parse(string.Join("\n", "esi" + Esi1[3..], " ESI " + Esi2[3..], "Csi" + Csi[3..])));
        f.EsiCount.Should().Be(2);
        f.Csi.EmployerUniqueId.Should().Be("DOM784000000000001");
    }

    // ---- values as received (AC-PRS-05, AC-PRS-09) --------------------------------------------------------------------------------

    [Fact]
    public void DataFields_AreNotTrimmed()
    {
        var f = Parsed(Parse(string.Join("\n", "ESI, P1 ,Name ,LC,  a  , , , ,EWPMS", Csi)));
        f.Esis[0].PersonId.Should().Be(" P1 ");
        f.Esis[0].EmployeeName.Should().Be("Name ");
        f.Esis[0].Future1.Should().Be("  a  ");
    }

    [Fact]
    public void AnEmptyLaborCardNo_IsTheEmptyString()
    {
        Parsed(Parse(string.Join("\n", "ESI,P1,Name,, , , , ,EWPMS", Csi))).Esis[0].LaborCardNo.Should().BeEmpty();
    }

    // ---- lengths (AC-PRS-06) ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2, 51)]   // PersonId
    [InlineData(3, 151)]  // EmployeeName
    [InlineData(4, 31)]   // LaborCardNo
    [InlineData(5, 101)]  // Future1
    [InlineData(9, 101)]  // Future5
    public void AnOverLongEsiField_IsMalformed_AtTheLimitItIsAccepted(int field, int length)
    {
        string Line(int len)
        {
            var parts = "ESI,P1,Name,LC, , , , ,EWPMS".Split(',');
            parts[field - 1] = new string('A', len);
            return string.Join(",", parts);
        }

        var rej = Rejected(Parse(string.Join("\n", Line(length), Csi)));
        rej.LineNo.Should().Be(1);
        rej.FieldNumber.Should().Be(field);
        Parsed(Parse(string.Join("\n", Line(length - 1), Csi)));
    }

    [Theory]
    [InlineData(2, 51)]   // EmployerUniqueId
    [InlineData(3, 151)]  // EmployerName
    [InlineData(4, 101)]  // Future1
    public void AnOverLongCsiField_IsMalformed(int field, int length)
    {
        var parts = Csi.Split(',');
        parts[field - 1] = new string('B', length);
        Rejected(Parse(string.Join("\n", Esi1, string.Join(",", parts)))).FieldNumber.Should().Be(field);
    }

    // ---- character sets (AC-PRS-07) -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ESI,PÜ1,Name,LC, , , , ,EWPMS", 2)]
    [InlineData("ESI,P1,Name,L\tC, , , , ,EWPMS", 4)]
    public void NonAsciiInAVarcharField_IsMalformed(string esi, int field)
    {
        Rejected(Parse(string.Join("\n", esi, Csi))).FieldNumber.Should().Be(field);
    }

    [Fact]
    public void AnArabicEmployeeName_IsAccepted()
    {
        Parsed(Parse(string.Join("\n", "ESI,P1,اسم علي,LC, , , , ,EWPMS", Csi))).Esis[0].EmployeeName.Should().Be("اسم علي");
    }

    // ---- required fields, blank = empty or whitespace-only (AC-PRS-08, R2 G-1) -----------------------------------------------------

    [Theory]
    [InlineData("ESI,,Name,LC, , , , ,EWPMS", true, 2)]
    [InlineData("ESI,   ,Name,LC, , , , ,EWPMS", true, 2)]
    [InlineData("CSI,,Employer, , , , , ,EWPMS", false, 2)]
    [InlineData("CSI, ,Employer, , , , , ,EWPMS", false, 2)]
    [InlineData("CSI,E1,, , , , , ,EWPMS", false, 3)]
    [InlineData("CSI,E1,  , , , , , ,EWPMS", false, 3)]
    public void ABlankRequiredField_IsMalformed(string line, bool isEsi, int field)
    {
        var text = isEsi ? string.Join("\n", line, Csi) : string.Join("\n", Esi1, line);
        var rej = Rejected(Parse(text));
        rej.FieldNumber.Should().Be(field);
        rej.Reason.Should().Contain("blank");
    }

    // ---- unknown and blank lines (AC-PRS-10, pure half) ---------------------------------------------------------------------------

    [Fact]
    public void UnknownAndBlankLines_AreSkippedAndCounted()
    {
        var f = Parsed(Parse(string.Join("\n", Esi1, "XYZ,1,2", "", Esi2, Csi)));
        f.EsiCount.Should().Be(2);
        f.IgnoredLineCount.Should().Be(2);
        f.IgnoredLineNumbers.Should().Equal(2, 3);
        f.Esis[1].LineNo.Should().Be(4);
    }

    // ---- AC-PRS-11: line endings ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\n", false)]
    [InlineData("\r", true)]
    [InlineData("\r", false)]
    public void LineEndings_AreEquivalent(string eol, bool finalTerminator)
    {
        var f = Parsed(Parse(Ref(eol, finalTerminator)));
        f.Esis.Select(e => e.LineNo).Should().Equal(1, 2);
        f.Csi.LineNo.Should().Be(3);
        f.IgnoredLineCount.Should().Be(0);
    }

    // ---- AC-PRS-12: duplicate PersonId -----------------------------------------------------------------------------------------------

    [Fact]
    public void ADuplicatePersonId_IsAccepted_OneRowPerLine()
    {
        Parsed(Parse(string.Join("\n", Esi1, Esi1, Csi))).EsiCount.Should().Be(2);
    }

    // ---- AC-PRS-13: comma inside a name -----------------------------------------------------------------------------------------------

    [Fact]
    public void ACommaInAName_ShiftsFields_UnderThePlainSplitRule()
    {
        // 9 fields + 1 comma = 10 fields: accepted, with the name split across EmployeeName and LaborCardNo.
        var f = Parsed(Parse(string.Join("\n", "ESI,P1,DOE,JOHN,LC, , , , ,EWPMS", Csi)));
        f.Esis[0].EmployeeName.Should().Be("DOE");
        f.Esis[0].LaborCardNo.Should().Be("JOHN");
        f.Esis[0].Future6.Should().Be("EWPMS", "every later field shifts one place");

        // 10 fields + 1 comma = 11 fields: rejected.
        Rejected(Parse(string.Join("\n", "ESI,P1,DOE,JOHN,LC, , , , ,EWPMS,X", Csi)));
    }

    // ---- AC-PRS-14: identifier -----------------------------------------------------------------------------------------------------

    [Fact]
    public void WpsFileId_IsTheLast12CharactersOfTheBaseName()
    {
        Parsed(Parse(Ref())).WpsFileId.Should().Be("126000005935");
    }

    [Theory]
    [InlineData("SHORT.PSI")]
    [InlineData("")]
    [InlineData("0000000000ü1.PSI")]
    public void ABadFileName_IsMalformed(string name)
    {
        Rejected(Parse(Ref(), name));
    }

    // ---- control-record and content rules (AC-CTL-01..05, pure halves) ------------------------------------------------------------

    [Fact]
    public void NoCsi_IsMalformed() // AC-CTL-01
    {
        Rejected(Parse(string.Join("\n", Esi1, Esi2))).Reason.Should().Contain("no CSI");
    }

    [Fact]
    public void TwoCsis_AreMalformed() // AC-CTL-02
    {
        var rej = Rejected(Parse(string.Join("\n", Esi1, Csi, Csi)));
        rej.LineNo.Should().Be(3);
        rej.Reason.Should().Contain("2 CSI records");
    }

    [Fact]
    public void TheCsiFirst_IsAccepted_AndEsiLinesAfterItAreKept() // AC-CTL-03, E-13
    {
        var f = Parsed(Parse(string.Join("\n", Csi, Esi1, Esi2)));
        f.Csi.LineNo.Should().Be(1);
        f.EsiCount.Should().Be(2);
    }

    [Fact]
    public void ACsiWithNoEsi_IsMalformed_WithTheFsdText() // AC-CTL-04
    {
        var rej = Rejected(Parse(Csi + "\n"));
        rej.Reason.Should().Contain(InwardPsiParser.NoRecordsText);
    }

    [Fact]
    public void OnlyUnknownOrBlankLines_AreMalformed() // AC-CTL-05
    {
        Rejected(Parse("XYZ,1\n\nABC\n")).Reason.Should().Contain("no CSI");
        Rejected(Parse(string.Empty)).Reason.Should().Contain("no CSI");
    }

    // ---- decoding (AC-INT-08, AC-INT-09, pure halves) -----------------------------------------------------------------------------

    [Fact]
    public void ABom_IsRemoved_AndTheFileParsesAsRef()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Ref())).ToArray();
        InwardPsiParser.Parse(withBom, FileName).File.Should().BeEquivalentTo(Parsed(Parse(Ref())));
    }

    [Fact]
    public void PgpOrBinaryContent_IsUnreadable()
    {
        Rejected(InwardPsiParser.Parse(Bytes("-----BEGIN PGP MESSAGE-----\n"), FileName), InwardPsiParseErrorCodes.Unreadable);
        Rejected(InwardPsiParser.Parse(new byte[] { 0x85, 0x41 }, FileName), InwardPsiParseErrorCodes.Unreadable);
        Rejected(InwardPsiParser.Parse(new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, 0x41 }, FileName), InwardPsiParseErrorCodes.Unreadable);
    }

    [Fact]
    public void InvalidUtf8_IsMalformed()
    {
        var bytes = Bytes(Esi1 + "\n").Concat(new byte[] { 0x43, 0xC3, 0x28 }).ToArray();
        Rejected(InwardPsiParser.Parse(bytes, FileName)).LineNo.Should().Be(2);
    }

    // ---- no personal data in reasons (AC-OBS-01, pure half) -----------------------------------------------------------------------

    [Fact]
    public void Reasons_NeverContainFieldValues()
    {
        var rej = Rejected(Parse(string.Join("\n", "ESI,DOM205590910784000000000002," + new string('N', 151) + ",90000001, , , , ,EWPMS", Csi)));
        rej.Reason.Should().NotContain("DOM205590910784000000000002").And.NotContain("90000001").And.NotContain("NNNN");
    }

    // ---- AS-A4-17 (D-A4-04, AC-DEP-04 on the working assumption): a PIF and its companion PSI share the identifier ---------------

    [Theory]
    [InlineData("05101234567126000005935.PAF", "041041DOM784000000000001126000005935.PSI")]
    [InlineData("05101234567250115090012.PAF", "041041DOM784000000000001250115090012.PSI")] // Activity 3's REF PIF name (250115090012)
    public void APifAndItsCompanionPsi_NamedOnTheSharedConvention_DeriveTheSameIdentifier_ThroughBothParsers(string pifName, string psiName)
    {
        // The PIF is Activity 3's REF content (it reconciles: two EDRs, total 4500.00); only its file name changes.
        var pif = InwardPifParser.Parse(Bytes(string.Join("\r\n",
            "EDR,90000000000005,309314334,AE000930000000000000002,2018-11-01,2018-11-28,28,3025.00,0.00,0",
            "EDR,90000000000006,309314334,AE000930000000000000003,2018-11-01,2018-11-28,28,1475.00,0.00,0",
            "EVP,90000000000005,309314334,0.00,0.00,0.00,0.00,0.00,0.00,0.00",
            "EVP,90000000000006,309314334,0.00,0.00,0.00,0.00,0.00,0.00,0.00",
            "PCR,0000000900001,309314334,2018-12-21,0901,112018,2,4500.00,AED,NovemberMonthSal") + "\r\n"), pifName);
        var psi = Parse(Ref(), psiName);

        pif.IsSuccess.Should().BeTrue(pif.Rejection?.Reason);
        Parsed(psi).WpsFileId.Should().Be(pif.File!.WpsFileId, "Activity 12 joins a PIF to its PSI on this identifier (FSD L926)");
        pif.File.WpsFileId.Should().HaveLength(12);
    }
}
