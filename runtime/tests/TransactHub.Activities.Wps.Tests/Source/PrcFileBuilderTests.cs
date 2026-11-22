// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardPrc/PrcFileBuilderTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
using Xunit;
using System.Globalization;
using System.Text;
using FluentAssertions;
using TransactHub.Activities.Wps.Inward;

namespace TransactHub.Activities.Wps.Tests.Source;

public class PrcFileBuilderTests
{
    private const string PifName = "0404080000000000719125005435810.PAF";
    private static readonly DateTime Instant = new(2025, 8, 15, 10, 54, 30);

    private static PrcFileContent Sample(decimal fixedPay = 1907.14m, decimal variable = 0m, int declared = 1, string name = PifName) =>
        new(name, fixedPay, variable, declared, Instant);

    [Fact]
    public void Fmt01_WorkedExample_IsByteExact()
    {
        PrcFileBuilder.BuildText(Sample()).Should().Be(
            "RDR,0404080000000000719125005435810.PAF,1907.14,0.00,1907.14,1, , , ,EWPMS\r\n" +
            "RCR, ,1907.14,0.00,1907.14,1,2025-08-15,1054, ,EWPMS\r\n");
    }

    [Fact]
    public void Fmt02_TenFieldsPerLine_WithFixedFillers()
    {
        var lines = PrcFileBuilder.BuildText(Sample()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var rdr = lines[0].Split(',');
        var rcr = lines[1].Split(',');

        rdr.Should().HaveCount(10);
        rcr.Should().HaveCount(10);
        rdr[0].Should().Be("RDR");
        rdr[6].Should().Be(" ");
        rdr[7].Should().Be(" ");
        rdr[8].Should().Be(" ");
        rdr[9].Should().Be("EWPMS");
        rcr[0].Should().Be("RCR");
        rcr[1].Should().Be(" ");
        rcr[5].Should().Be("1");
        rcr[8].Should().Be(" ");
        rcr[9].Should().Be("EWPMS");
    }

    [Theory]
    [InlineData("0", "0.00")]
    [InlineData("1500.5", "1500.50")]
    [InlineData("1234567.891", "1234567.89")]
    [InlineData("2.345", "2.35")]
    [InlineData("2.3449", "2.34")]
    [InlineData("1000000", "1000000.00")]
    public void Fmt03_Numbers_AreRoundedAwayFromZero_AndCultureIndependent(string value, string expected)
    {
        var amount = decimal.Parse(value, CultureInfo.InvariantCulture);
        foreach (var culture in new[] { "ar-AE", "de-DE", "en-US" })
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            try
            {
                PrcFileBuilder.BuildText(Sample(fixedPay: amount)).Split(',')[2].Should().Be(expected, culture);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }

    [Fact]
    public void Fmt04_RcrTotals_EqualRdrTotals()
    {
        var lines = PrcFileBuilder.BuildText(Sample(1907.14m, 12.5m)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[1].Split(',')[2..5].Should().Equal(lines[0].Split(',')[2..5]);
    }

    [Fact]
    public void Fmt05_Total_IsRoundedFromUnroundedSums()
    {
        // 0.004 + 0.004 = 0.008 -> 0.01, whereas rounding each part first would give 0.00.
        var fields = PrcFileBuilder.BuildText(Sample(0.004m, 0.004m)).Split(',');
        fields[2].Should().Be("0.00");
        fields[3].Should().Be("0.00");
        fields[4].Should().Be("0.01");
    }

    [Fact]
    public void Fmt06_Bytes_AreAsciiNoBom_ExactlyTwoCrlf_EndingWithCrlf()
    {
        var bytes = PrcFileBuilder.BuildBytes(Sample());

        bytes.Should().OnlyContain(b => b < 0x80);
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = Encoding.ASCII.GetString(bytes);
        text.Split("\r\n").Should().HaveCount(3);
        text.Should().EndWith("\r\n");
        text.Count(c => c == '\n').Should().Be(2);
        text.Count(c => c == '\r').Should().Be(2);
    }

    [Theory]
    [InlineData("a,b.PAF")]
    [InlineData("a\rb.PAF")]
    [InlineData("a\nb.PAF")]
    [InlineData("é.PAF")]
    [InlineData("")]
    public void Fmt07_IsPrintable_RejectsUnprintableNames(string name)
    {
        PrcFileBuilder.IsPrintable(name).Should().BeFalse();
        var act = () => PrcFileBuilder.BuildText(Sample(name: name));
        act.Should().Throw<PrcInvalidPifNameException>();
    }

    [Fact]
    public void Fmt08_Instant_MapsToDateAndHhmm()
    {
        var fields = PrcFileBuilder.BuildText(Sample()).Split("\r\n")[1].Split(',');
        fields[6].Should().Be("2025-08-15");
        fields[7].Should().Be("1054");
    }

    [Fact]
    public void Fmt09_PifName_IsPrintedAsGiven()
    {
        PrcFileBuilder.BuildText(Sample(name: "abc.paf")).Should().StartWith("RDR,abc.paf,");
    }
}
