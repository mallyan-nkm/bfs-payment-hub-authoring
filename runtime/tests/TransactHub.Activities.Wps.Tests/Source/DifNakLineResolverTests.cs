// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardDif/DifNakLineResolverTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
// Purpose: the DER resolution of WPS inward Activity 24, DIF NAK extraction (design Inward_PAF_DIF_Nak_Extraction v1.0 §6, §5 step 5, E-DN1–E-DN6;
// task T2): AC-RES-01…05 and AC-RDR-02.
// Responsibility: the pure DifNakLineResolver, and Activity 10's PrcNakFileReader reused unchanged with a DIF name (AC-REG-01 is its own test class,
// PrcNakFileReaderTests, unmodified).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TransactHub.Activities.Wps.Inward;
using TransactHub.Activities.Wps.Inward.Dif;
using TransactHub.Activities.Wps.Rules;
using Xunit;

namespace TransactHub.Activities.Wps.Tests.Source;

public class DifNakLineResolverTests
{
    private const string DifName = "ARD180711134539.DIF";
    private const string NakName = "ARD180711134539000000000124.NAK";

    // A DIF of n lines: OrderNo i → EdrId 100 + i.
    private static Dictionary<int, long> Lines(int n) => Enumerable.Range(1, n).ToDictionary(i => i, i => 100L + i);

    private static DifNakResolution Resolve(int n, params WpsAckDetailError[] ders) => DifNakLineResolver.Resolve(ders, Lines(n), []);

    private static WpsAckDetailError Der(int lineNumber, string errorNumber) => new(lineNumber, errorNumber, "description");

    private static PrcNakExtract ReadDif(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = PrcNakFileReader.Read(bytes, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), DifName, NakName);
        result.IsValid.Should().BeTrue(result.Failure ?? string.Empty);
        return result.Extract!;
    }

    private static DifNakResolution ReadAndResolve(string text, int n)
    {
        var extract = ReadDif(text);
        return DifNakLineResolver.Resolve(extract.Rejections, Lines(n), extract.Warnings);
    }

    // AC-RES-01: the FSD sample DER,2,AC01 → order 2 → AC01; order 1 has no reason (E-DN1).
    [Theory]
    [InlineData(2, "AC01", 1)]
    [InlineData(1, "00210", 2)]
    [InlineData(3, "AC04", 1)]
    public void AcRes01_ADerNamingAnOrderNo_GivesThatEdrItsCode_AndTheOthersNone(int orderNo, string code, int otherOrderNo)
    {
        var result = Resolve(3, Der(orderNo, code));

        result.Reasons.Should().HaveCount(3, "every EDR of the DIF has an entry (A-5)");
        result.Reasons[100 + orderNo].Should().Be(code);
        result.Reasons[100 + otherOrderNo].Should().BeNull();
        result.Matches.Should().Equal(new DifNakLineMatch(orderNo, orderNo, 100 + orderNo, code, "description"));
        result.Unmatched.Should().BeEmpty();
        result.RejectedEdrCount.Should().Be(1);
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void AcRes01_TheFsdSampleThroughTheReader_GivesOrderTwoTheCodeAC01()
    {
        var result = ReadAndResolve("AHR,REJECTED,ARD180711134539.DIF\nDER,2,AC01\nATR,REJECTED,3\n", 2);

        result.Reasons.Should().BeEquivalentTo(new Dictionary<long, string?> { [101] = null, [102] = "AC01" });
    }

    // AC-RES-02: several DERs for one OrderNo are joined with ';' in file order (E-DN4).
    [Theory]
    [InlineData("AC01;AC02", "AC01", "AC02")]
    [InlineData("AC02;AC01", "AC02", "AC01")]
    [InlineData("AC01;AC01;00210", "AC01", "AC01", "00210")]
    public void AcRes02_SeveralDersForOneOrderNo_AreJoinedInFileOrder(string expected, params string[] codes)
    {
        var result = Resolve(2, codes.Select(c => Der(2, c)).ToArray());

        result.Reasons[102].Should().Be(expected);
        result.Reasons[101].Should().BeNull();
        result.Matches.Should().HaveCount(codes.Length);
        result.Warnings.Should().NotContain(DifNakWarnings.DerReasonTruncated);
    }

    // AC-RES-02: a joined reason over 500 characters is cut to 500 with DER_REASON_TRUNCATED; exactly 500 is kept whole.
    [Theory]
    [InlineData(250, 249, false)]
    [InlineData(250, 250, true)]
    [InlineData(600, 1, true)]
    public void AcRes02_AJoinedReasonOver500Characters_IsCutTo500WithAWarning(int firstLength, int secondLength, bool truncated)
    {
        var first = new string('A', firstLength);
        var second = new string('B', secondLength);

        var result = Resolve(1, Der(1, first), Der(1, second));

        var joined = first + ";" + second;
        result.Reasons[101].Should().Be(truncated ? joined[..DifNakLineResolver.ReasonMaxLength] : joined);
        result.Reasons[101]!.Length.Should().BeLessThanOrEqualTo(DifNakLineResolver.ReasonMaxLength);
        if (truncated)
        {
            result.Warnings.Should().Equal(DifNakWarnings.DerReasonTruncated);
        }
        else
        {
            result.Warnings.Should().BeEmpty();
        }
    }

    // AC-RES-03: line 0, n+1 (the DCR line), n+5 or a non-numeric line number is unmatched, counted and warned; resolution never fails (E-DN3).
    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("7")]
    [InlineData("X")]
    public void AcRes03_ADerNamingNoDirLine_IsUnmatched_AndNeverFails(string lineNumber)
    {
        var result = ReadAndResolve($"AHR,REJECTED,ARD180711134539.DIF\nDER,{lineNumber},AC01,bad line\nATR,REJECTED,3\n", 2);

        result.Unmatched.Should().ContainSingle().Which.ErrorNumber.Should().Be("AC01");
        result.UnmatchedDerCount.Should().Be(1);
        result.Matches.Should().BeEmpty();
        result.Reasons.Values.Should().AllSatisfy(r => r.Should().BeNull());
        result.RejectedEdrCount.Should().Be(0);
        result.Warnings.Should().Contain(DifNakWarnings.DerLineUnmatched);
    }

    [Fact]
    public void AcRes03_AllFourUnmatchedShapesInOneFile_AreAllCounted_BesideAMatchedDer()
    {
        var result = ReadAndResolve(
            "AHR,REJECTED,ARD180711134539.DIF\nDER,0,E0\nDER,2,AC01\nDER,3,E3\nDER,7,E7\nDER,X,EX\nATR,REJECTED,7\n", 2);

        result.Unmatched.Select(u => u.ErrorNumber).Should().Equal("E0", "E3", "E7", "EX");
        result.UnmatchedDerCount.Should().Be(4);
        result.Reasons[102].Should().Be("AC01");
        result.Warnings.Should().Contain(PrcNakWarnings.DerLineNumberInvalid).And.Contain(DifNakWarnings.DerLineUnmatched);
        result.Warnings.Count(w => w == DifNakWarnings.DerLineUnmatched).Should().Be(1, "each warning code is recorded once");
    }

    // AC-RES-04: no DER → every EDR without a reason, warning NO_DER_RECORD, once whether or not the reader already reported it (E-DN2).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcRes04_NoDer_LeavesEveryEdrWithoutAReason_WithNoDerRecord(bool readerReportedIt)
    {
        string[] readerWarnings = readerReportedIt ? [PrcNakWarnings.NoDerRecord] : [];

        var result = DifNakLineResolver.Resolve([], Lines(3), readerWarnings);

        result.Reasons.Should().HaveCount(3);
        result.Reasons.Values.Should().AllSatisfy(r => r.Should().BeNull());
        result.RejectedEdrCount.Should().Be(0);
        result.Warnings.Should().Equal(DifNakWarnings.NoDerRecord);
    }

    [Fact]
    public void AcRes04_NoDerThroughTheReader_KeepsTheReadersWarnings()
    {
        var result = ReadAndResolve("AHR,REJECTED,ARD180711134539.DIF\nATR,REJECTED,2\n", 2);

        result.Reasons.Values.Should().AllSatisfy(r => r.Should().BeNull());
        result.Warnings.Should().Contain(DifNakWarnings.NoDerRecord);
    }

    // AC-RES-05: an empty error number is matched but adds nothing to the reason; warning DER_WITHOUT_ERROR_NUMBER.
    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("", "AC01")]
    public void AcRes05_AnEmptyErrorNumber_IsMatchedButAddsNothing(string emptyCode, string? otherCode)
    {
        var ders = otherCode is null ? new[] { Der(2, emptyCode) } : [Der(2, emptyCode), Der(2, otherCode)];

        var result = Resolve(2, ders);

        result.Matches.Should().HaveCount(ders.Length);
        result.Matches[0].Should().Be(new DifNakLineMatch(2, 2, 102, string.Empty, "description"));
        result.Reasons[102].Should().Be(otherCode);
        result.Unmatched.Should().BeEmpty();
        result.Warnings.Should().Equal(DifNakWarnings.DerWithoutErrorNumber);
    }

    // AC-RDR-02: the reader called with the DIF name reads a 3-field DER with an empty description (E-DN5).
    [Fact]
    public void AcRdr02_AThreeFieldDer_IsReadWithAnEmptyDescription()
    {
        var extract = ReadDif("AHR,REJECTED,ARD180711134539.DIF\nDER,2,AC01\nATR,REJECTED,3\n");

        extract.Rejections.Should().Equal(new WpsAckDetailError(2, "AC01", string.Empty));
        extract.Warnings.Should().Contain(PrcNakWarnings.DerWithoutDescription);
        DifNakLineResolver.Resolve(extract.Rejections, Lines(2), extract.Warnings).Matches
            .Should().Equal(new DifNakLineMatch(2, 2, 102, "AC01", string.Empty));
    }

    // AC-RDR-02: a spaced error number is trimmed, and the raw text is kept verbatim in FileContent (E-DN6).
    [Fact]
    public void AcRdr02_ASpacedErrorNumber_IsTrimmed_AndTheRawTextIsKept()
    {
        const string Text = "AHR,REJECTED,ARD180711134539.DIF\nDER, 2 , 00210 ,Not enough balance\nATR,REJECTED,3\n";

        var extract = ReadDif(Text);

        extract.Rejections.Should().Equal(new WpsAckDetailError(2, "00210", "Not enough balance"));
        extract.AhrStatus.Should().Be("REJECTED");
        using var json = JsonDocument.Parse(extract.ToFileContentJson());
        json.RootElement.GetProperty("document").GetProperty("rawContent").GetString().Should().Be(Text);
        DifNakLineResolver.Resolve(extract.Rejections, Lines(2), extract.Warnings).Reasons[102].Should().Be("00210");
    }

    // AC-RDR-02 (with §5 step 5): a present AHR must name the DIF being applied, ignoring case.
    [Fact]
    public void AcRdr02_TheAhrIsCheckedAgainstTheDifName()
    {
        var other = Encoding.UTF8.GetBytes("AHR,REJECTED,ARD999999999999.DIF\nDER,2,AC01\nATR,REJECTED,3\n");
        PrcNakFileReader.Read(other, other.Length, Convert.ToHexString(SHA256.HashData(other)), DifName, NakName)
            .Failure.Should().Contain("ARD999999999999.DIF");

        ReadDif("AHR,REJECTED,ard180711134539.dif\nDER,2,AC01\nATR,REJECTED,3\n").Rejections.Should().ContainSingle();
    }
}
