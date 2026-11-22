// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/InwardFileText.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: The generic text rules shared by the WPS inward file parsers — PIF (Activity 3) and PSI (Activity 4).
// Responsibility: decode staged bytes (one UTF-8 BOM removed, PGP/binary guard, strict UTF-8), split lines, the
// printable-ASCII rule and the 12-character file identifier. Extracted from InwardPifParser with no behaviour change
// (PSI design v2.0 §3.1, task T2, AC-PRS-16). It returns failure KINDS, not messages: each parser keeps its own error
// codes and wording, so the PIF rejection texts are byte-for-byte what they were.
namespace TransactHub.Activities.Wps.Inward;

using System.Text;

/// <summary>Why <see cref="InwardFileText.Decode"/> refused the bytes.</summary>
public enum InwardFileDecodeFailure
{
    /// <summary>Decoded.</summary>
    None,

    /// <summary>After the BOM, the content starts with a PGP armour header.</summary>
    PgpArmour,

    /// <summary>After the BOM, the first byte is ≥ 0x80 (binary or encrypted content, or a second BOM).</summary>
    BinaryFirstByte,

    /// <summary>The content is not valid UTF-8.</summary>
    InvalidUtf8,
}

/// <summary>The outcome of <see cref="InwardFileText.Decode"/>.</summary>
/// <param name="Failure">Why it failed, or <see cref="InwardFileDecodeFailure.None"/>.</param>
/// <param name="Text">The decoded text, when <see cref="Failure"/> is <see cref="InwardFileDecodeFailure.None"/>.</param>
/// <param name="Line">For <see cref="InwardFileDecodeFailure.InvalidUtf8"/>: the 1-based line holding the bad byte.</param>
/// <param name="ByteOffset">For <see cref="InwardFileDecodeFailure.InvalidUtf8"/>: the byte offset after the BOM.</param>
public readonly record struct InwardFileDecodeResult(InwardFileDecodeFailure Failure, string? Text, int Line, int ByteOffset);

/// <summary>Why <see cref="InwardFileText.TryDeriveWpsFileId"/> could not derive the identifier.</summary>
public enum InwardFileIdFailure
{
    /// <summary>Derived.</summary>
    None,

    /// <summary>The file name is null or empty.</summary>
    EmptyName,

    /// <summary>The base name (without its last extension) is shorter than <see cref="InwardFileText.IdentifierLength"/>.</summary>
    TooShort,

    /// <summary>The identifier contains a non-ASCII or control character.</summary>
    NonAscii,
}

/// <summary>Generic text rules for staged WPS inward files (PIF and PSI).</summary>
public static class InwardFileText
{
    /// <summary>The length of the WPS file identifier taken from the end of the file name's base name.</summary>
    public const int IdentifierLength = 12;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] PgpArmour = "-----BEGIN PGP"u8.ToArray();

    /// <summary>
    /// Removes one leading UTF-8 BOM, refuses PGP-armoured or binary content, and decodes strict UTF-8 with no
    /// Windows-1252 fallback. The caller has already verified size and SHA-256 on the bytes exactly as staged.
    /// </summary>
    public static InwardFileDecodeResult Decode(byte[] stagedBytes)
    {
        ArgumentNullException.ThrowIfNull(stagedBytes);

        var offset = stagedBytes.Length >= 3 && stagedBytes[0] == 0xEF && stagedBytes[1] == 0xBB && stagedBytes[2] == 0xBF ? 3 : 0;
        var content = new ReadOnlySpan<byte>(stagedBytes, offset, stagedBytes.Length - offset);

        // A second BOM starts with 0xEF, so it is caught by the binary-first-byte test.
        if (content.StartsWith(PgpArmour))
        {
            return new InwardFileDecodeResult(InwardFileDecodeFailure.PgpArmour, null, 0, 0);
        }

        if (content.Length > 0 && content[0] >= 0x80)
        {
            return new InwardFileDecodeResult(InwardFileDecodeFailure.BinaryFirstByte, null, 0, 0);
        }

        try
        {
            return new InwardFileDecodeResult(InwardFileDecodeFailure.None, StrictUtf8.GetString(content), 0, 0);
        }
        catch (DecoderFallbackException ex)
        {
            return new InwardFileDecodeResult(InwardFileDecodeFailure.InvalidUtf8, null, LineOfByteOffset(content, ex.Index), ex.Index);
        }
    }

    /// <summary>
    /// Derives the 12-character identifier: the file name without its last extension, then its last 12 characters,
    /// which must be printable ASCII. A name with no extension uses the whole name as its base name.
    /// </summary>
    public static InwardFileIdFailure TryDeriveWpsFileId(string? fileName, out string wpsFileId)
    {
        wpsFileId = string.Empty;
        if (string.IsNullOrEmpty(fileName))
        {
            return InwardFileIdFailure.EmptyName;
        }

        var lastDot = fileName.LastIndexOf('.');
        var baseName = lastDot >= 0 ? fileName[..lastDot] : fileName;
        if (baseName.Length < IdentifierLength)
        {
            return InwardFileIdFailure.TooShort;
        }

        var id = baseName[^IdentifierLength..];
        if (!IsPrintableAscii(id))
        {
            return InwardFileIdFailure.NonAscii;
        }

        wpsFileId = id;
        return InwardFileIdFailure.None;
    }

    /// <summary>
    /// The physical lines of <paramref name="text"/> with their 1-based numbers. A line ends at CRLF, LF or CR; CRLF is
    /// one terminator. The empty segment after the final terminator is not a line; every other blank line is.
    /// </summary>
    public static IEnumerable<(int LineNo, string Line)> Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lineNo = 0;
        var start = 0;
        while (start < text.Length)
        {
            var end = start;
            while (end < text.Length && text[end] != '\r' && text[end] != '\n')
            {
                end++;
            }

            var line = text.Substring(start, end - start);
            lineNo++;

            if (end < text.Length)
            {
                end += text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
            }

            start = end;
            yield return (lineNo, line);
        }
    }

    /// <summary>True when every character is printable ASCII (0x20–0x7E); the empty string is printable.</summary>
    public static bool IsPrintableAscii(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (var c in value)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    private static int LineOfByteOffset(ReadOnlySpan<byte> bytes, int index)
    {
        var line = 1;
        var limit = Math.Min(index, bytes.Length);
        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == 0x0D)
            {
                line++;
                if (i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
                {
                    i++;
                }
            }
            else if (bytes[i] == 0x0A)
            {
                line++;
            }
        }

        return line;
    }
}
