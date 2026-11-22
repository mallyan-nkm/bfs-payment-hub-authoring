// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardPrc/PrcNakFileReaderTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TransactHub.Activities.Wps.Inward;
using TransactHub.Activities.Wps.Rules;
using Xunit;

namespace TransactHub.Activities.Wps.Tests.Source;

/// <summary>
/// Unit coverage of the pure NAK reader (design Inward_PAF_PRC_Nak_Extraction v1.0 §5 steps 5-6, §6.3; task T1): AC-FIL-02..08, plus the
/// length and hash checks of AC-FIL-01 and the FileContent shape of AC-APL-04.
/// </summary>
public sealed class PrcNakFileReaderTests
{
    private const string PrcName = "ARD180711134539.PRC";
    private const string NakName = "ARD180711134539N25000000001.NAK";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static PrcNakReadResult Read(string text, string prc = PrcName)
    {
        var bytes = Bytes(text);
        return PrcNakFileReader.Read(bytes, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), prc, NakName);
    }

    // The FSD sample shape (L831-L833): one AHR, one DER, one ATR.
    private static string Sample() => "AHR,REJECTED,ARD180711134539.PRC\nDER,2,00210,Not enough balance for AGT\nATR,REJECTED,3\n";

    [Fact]
    public void Fil04_the_sample_gives_one_rejection_and_the_code_00210()
    {
        var result = Read(Sample());

        result.IsValid.Should().BeTrue();
        var extract = result.Extract!;
        extract.Rejections.Should().Equal(new WpsAckDetailError(2, "00210", "Not enough balance for AGT"));
        extract.RejectionCode.Should().Be("00210");
        extract.Warnings.Should().BeEmpty();
        extract.AhrStatus.Should().Be("REJECTED");
        extract.Document.RawContent.Should().Be(Sample(), "the raw text is kept verbatim (I-N5)");
    }

    [Fact]
    public void Fil05_three_ders_are_all_kept_in_order_and_the_code_is_the_last_ones()
    {
        var text = "AHR,REJECTED,ARD180711134539.PRC\nDER,2,00210,first\nDER,5,00300,second\nDER,9,00410,third, with a comma\nATR,REJECTED,5\n";

        var extract = Read(text).Extract!;

        extract.Rejections.Select(r => r.ErrorNumber).Should().Equal("00210", "00300", "00410");
        extract.Rejections.Select(r => r.LineNumber).Should().Equal(2, 5, 9);
        extract.Rejections[2].ErrorDescription.Should().Be("third, with a comma");
        extract.RejectionCode.Should().Be("00410");
    }

    [Fact]
    public void Fil06_a_three_field_der_is_read_with_an_empty_description_and_a_warning()
    {
        var extract = Read("AHR,REJECTED,ARD180711134539.PRC\nDER,2,00210\nATR,REJECTED,3\n").Extract!;

        extract.Rejections.Should().Equal(new WpsAckDetailError(2, "00210", string.Empty));
        extract.RejectionCode.Should().Be("00210");
        extract.Warnings.Should().Contain(PrcNakWarnings.DerWithoutDescription);
    }

    [Fact]
    public void Fil07_no_der_gives_empty_rejections_an_empty_code_a_warning_and_is_still_valid()
    {
        var result = Read("AHR,REJECTED,ARD180711134539.PRC\nATR,REJECTED,2\n");

        result.IsValid.Should().BeTrue();
        result.Extract!.Rejections.Should().BeEmpty();
        result.Extract.RejectionCode.Should().BeEmpty();
        result.Extract.Warnings.Should().Contain(PrcNakWarnings.NoDerRecord);
    }

    [Fact]
    public void Fil03_no_ahr_is_accepted_with_the_warning_NO_AHR()
    {
        var result = Read("DER,2,00210,Not enough balance\nATR,REJECTED,2\n");

        result.IsValid.Should().BeTrue();
        result.Extract!.Warnings.Should().Contain(PrcNakWarnings.NoAhr);
        result.Extract.AhrStatus.Should().BeNull();
        result.Extract.RejectionCode.Should().Be("00210");
    }

    [Fact]
    public void Fil02_an_accepted_ahr_is_refused()
    {
        var result = Read("AHR,ACCEPTED,ARD180711134539.PRC\nATR,ACCEPTED,2\n");

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Contain("ACCEPTED");
    }

    [Fact]
    public void Fil02_an_ahr_naming_another_prc_is_refused_but_the_case_is_ignored()
    {
        Read("AHR,REJECTED,ARD999999999999.PRC\nDER,2,00210,x\nATR,REJECTED,3\n").Failure.Should().Contain("ARD999999999999.PRC");
        Read("AHR,REJECTED,ard180711134539.prc\nDER,2,00210,x\nATR,REJECTED,3\n").IsValid.Should().BeTrue();
    }

    [Fact]
    public void Fil08_atr_anomalies_are_warnings_only()
    {
        var noAtr = Read("AHR,REJECTED,ARD180711134539.PRC\nDER,2,00210,x\n").Extract!;
        noAtr.Warnings.Should().Contain(PrcNakWarnings.NoAtr);

        var countOff = Read("AHR,REJECTED,ARD180711134539.PRC\nDER,2,00210,x\nATR,REJECTED,9\n").Extract!;
        countOff.Warnings.Should().Contain(PrcNakWarnings.AtrCountMismatch);

        var statusOff = Read("AHR,REJECTED,ARD180711134539.PRC\nDER,2,00210,x\nATR,ACCEPTED,3\n").Extract!;
        statusOff.Warnings.Should().Contain(PrcNakWarnings.AhrAtrStatusMismatch);
    }

    [Fact]
    public void Fil01_a_changed_length_or_hash_is_refused()
    {
        var bytes = Bytes(Sample());
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        PrcNakFileReader.Read(bytes, bytes.Length + 1, hash, PrcName, NakName).Failure.Should().Contain("bytes");
        PrcNakFileReader.Read(bytes, bytes.Length, new string('0', 64), PrcName, NakName).Failure.Should().Contain("SHA-256");
        PrcNakFileReader.Read(bytes, bytes.Length, hash.ToLowerInvariant(), PrcName, NakName).IsValid.Should().BeTrue("the hash comparison ignores case");
    }

    [Fact]
    public void Padded_error_numbers_and_a_bom_are_trimmed()
    {
        var extract = Read("﻿AHR,REJECTED,ARD180711134539.PRC\nDER, 2 , 00210 ,  padded  \nATR,REJECTED,3\n").Extract!;

        extract.Rejections.Should().Equal(new WpsAckDetailError(2, "00210", "padded"));
    }

    [Fact]
    public void Apl04_the_file_content_json_holds_the_document_the_rejections_the_code_and_the_warnings()
    {
        var extract = Read("AHR,REJECTED,ARD180711134539.PRC\nDER,2,00210\nATR,REJECTED,3\n").Extract!;

        using var json = JsonDocument.Parse(extract.ToFileContentJson());
        var root = json.RootElement;
        root.GetProperty("document").GetProperty("rawContent").GetString().Should().Be("AHR,REJECTED,ARD180711134539.PRC\nDER,2,00210\nATR,REJECTED,3\n");
        root.GetProperty("rejections").GetArrayLength().Should().Be(1);
        root.GetProperty("rejectionCode").GetString().Should().Be("00210");
        root.GetProperty("warnings").EnumerateArray().Select(e => e.GetString()).Should().Contain(PrcNakWarnings.DerWithoutDescription);
    }
}
