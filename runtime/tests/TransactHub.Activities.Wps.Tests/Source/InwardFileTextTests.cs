// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardFileTextTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
// Purpose: Unit coverage of the shared inward-file text rules extracted from InwardPifParser for the PSI parser
// (PSI design v2.0 §3.1, task T2): AC-PRS-16 (one implementation of the identifier, called by the PIF parser) and the
// pure behaviour each rule had inside the PIF parser. The PIF parser's own tests (InwardPifParserTests) must keep
// passing unchanged — that is the "no behaviour change" proof (AC-DEP-02).
// Pure logic: no SQL Server, no file system.

using System.Reflection;
using System.Text;
using FluentAssertions;
using TransactHub.Activities.Wps.Inward;
using Xunit;

namespace TransactHub.Activities.Wps.Tests.Source;

public class InwardFileTextTests
{
    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);

    // ---- AC-PRS-16: one implementation of the identifier --------------------------------------------------------------------------

    [Theory]
    [InlineData("05101234567250115090012.PAF", "250115090012")]
    [InlineData("041041DOM784000000000001126000005935.PSI", "126000005935")]
    [InlineData("A.B.250115090012.PAF", "250115090012")]
    [InlineData("NOEXTENSION0123456789", "ON0123456789")]
    public void DeriveWpsFileId_TakesTheLast12CharactersOfTheBaseName(string fileName, string expected)
    {
        InwardFileText.TryDeriveWpsFileId(fileName, out var id).Should().Be(InwardFileIdFailure.None);
        id.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, InwardFileIdFailure.EmptyName)]
    [InlineData("", InwardFileIdFailure.EmptyName)]
    [InlineData("SHORT.PSI", InwardFileIdFailure.TooShort)]
    [InlineData("12345678901.PAF", InwardFileIdFailure.TooShort)]
    [InlineData("0000000000ü1.PSI", InwardFileIdFailure.NonAscii)]
    [InlineData("00000000000\t1.PSI", InwardFileIdFailure.NonAscii)]
    public void DeriveWpsFileId_ClassifiesEachFailure(string? fileName, InwardFileIdFailure expected)
    {
        InwardFileText.TryDeriveWpsFileId(fileName, out var id).Should().Be(expected);
        id.Should().BeEmpty();
    }

    [Fact]
    public void ThePifParser_HasNoPrivateCopyOfTheSharedRules()
    {
        // AC-PRS-16 (review half, made executable): the extracted members no longer exist on InwardPifParser, so the
        // identifier, line splitting, decoding and ASCII rule each have exactly one implementation.
        var privateStatics = typeof(InwardPifParser)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Select(m => m.Name)
            .ToList();

        privateStatics.Should().NotContain(new[] { "IsPrintableAscii", "LineOfByteOffset" });
        typeof(InwardPifParser).GetField("StrictUtf8", BindingFlags.NonPublic | BindingFlags.Static).Should().BeNull();
        typeof(InwardPifParser).GetField("PgpArmour", BindingFlags.NonPublic | BindingFlags.Static).Should().BeNull();
    }

    [Fact]
    public void ThePifParser_StillDerivesTheSameIdentifier()
    {
        // AC-PRS-16: for the FSD's PIF sample name the PIF parser's result is unchanged.
        const string Pcr = "PCR,0000000900001,309314334,2018-12-21,0901,112018,0,0.00,AED,Ref";
        var result = InwardPifParser.Parse(Bytes(Pcr + "\r\n"), "05101234567250115090012.PAF");

        result.IsSuccess.Should().BeTrue();
        result.File!.WpsFileId.Should().Be("250115090012");
    }

    // ---- decoding ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Decode_RemovesOneBom_AndDecodesStrictUtf8()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes("ESI,1,اسم\n")).ToArray();

        var r = InwardFileText.Decode(bytes);

        r.Failure.Should().Be(InwardFileDecodeFailure.None);
        r.Text.Should().Be("ESI,1,اسم\n");
    }

    [Fact]
    public void Decode_ASecondBom_IsBinaryFirstByte()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF }.Concat(Bytes("ESI")).ToArray();
        InwardFileText.Decode(bytes).Failure.Should().Be(InwardFileDecodeFailure.BinaryFirstByte);
    }

    [Fact]
    public void Decode_AFirstByteOf0x85_IsBinaryFirstByte()
    {
        InwardFileText.Decode(new byte[] { 0x85, 0x41 }).Failure.Should().Be(InwardFileDecodeFailure.BinaryFirstByte);
    }

    [Fact]
    public void Decode_PgpArmour_IsRefused_EvenAfterABom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes("-----BEGIN PGP MESSAGE-----\n")).ToArray();
        InwardFileText.Decode(bytes).Failure.Should().Be(InwardFileDecodeFailure.PgpArmour);
    }

    [Fact]
    public void Decode_InvalidUtf8_ReportsTheLineAndTheByteOffsetAfterTheBom()
    {
        // Line 1 "AB\r\n" (4 bytes), line 2 "C" then an invalid continuation byte at offset 5.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A', (byte)'B', 0x0D, 0x0A, (byte)'C', 0xC3, 0x28 };

        var r = InwardFileText.Decode(bytes);

        r.Failure.Should().Be(InwardFileDecodeFailure.InvalidUtf8);
        r.Line.Should().Be(2);
        r.ByteOffset.Should().Be(5);
        r.Text.Should().BeNull();
    }

    [Fact]
    public void Decode_EmptyContent_IsTheEmptyString()
    {
        var r = InwardFileText.Decode(Array.Empty<byte>());
        r.Failure.Should().Be(InwardFileDecodeFailure.None);
        r.Text.Should().BeEmpty();
    }

    // Very short inputs: the BOM is stripped only when all three bytes are present, and nothing indexes past the end.
    [Fact]
    public void Decode_ABomAlone_IsTheEmptyString()
    {
        var r = InwardFileText.Decode(new byte[] { 0xEF, 0xBB, 0xBF });
        r.Failure.Should().Be(InwardFileDecodeFailure.None);
        r.Text.Should().BeEmpty();
    }

    [Theory]
    [InlineData(new byte[] { 0xEF })]
    [InlineData(new byte[] { 0xEF, 0xBB })]
    public void Decode_ATruncatedBom_IsNotStripped_AndIsBinaryFirstByte(byte[] bytes)
    {
        InwardFileText.Decode(bytes).Failure.Should().Be(InwardFileDecodeFailure.BinaryFirstByte);
    }

    [Theory]
    [InlineData(new byte[] { 0x41 }, "A")]
    [InlineData(new byte[] { 0x41, 0x42 }, "AB")]
    [InlineData(new byte[] { 0x0A }, "\n")]
    public void Decode_OneOrTwoAsciiBytes_AreDecodedAsTheyAre(byte[] bytes, string expected)
    {
        var r = InwardFileText.Decode(bytes);
        r.Failure.Should().Be(InwardFileDecodeFailure.None);
        r.Text.Should().Be(expected);
    }

    // ---- line splitting ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\nb\n")]
    [InlineData("a\rb\r")]
    [InlineData("a\r\nb")]
    [InlineData("a\nb")]
    public void Lines_TreatCrLfLfAndCrAlike_AndIgnoreTheSegmentAfterTheFinalTerminator(string text)
    {
        InwardFileText.Lines(text).Should().Equal((1, "a"), (2, "b"));
    }

    [Fact]
    public void Lines_KeepBlankLinesAsLines_WithPhysicalNumbers()
    {
        InwardFileText.Lines("a\r\n\r\nb\n\nc").Should().Equal((1, "a"), (2, ""), (3, "b"), (4, ""), (5, "c"));
    }

    [Fact]
    public void Lines_OfTheEmptyString_AreNone()
    {
        InwardFileText.Lines(string.Empty).Should().BeEmpty();
    }

    // ---- printable ASCII -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", true)]
    [InlineData(" ~ABC123", true)]
    [InlineData("A\tB", false)]
    [InlineData("اسم", false)]
    [InlineData("\u007F", false)]
    public void IsPrintableAscii_AcceptsOnly0x20To0x7E(string value, bool expected)
    {
        InwardFileText.IsPrintableAscii(value).Should().Be(expected);
    }
}
