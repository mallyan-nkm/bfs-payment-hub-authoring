// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/InwardPsiParser.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: Pure parser for the WPS inward Personal Salary Information file (PSI), Activity 4 (PSI design v2.0 §5.2
// steps 1a-3, §5.3, §5.4).
// Responsibility: bytes in, either a fully parsed file or one classified rejection out. No I/O, no clock, no
// configuration, no logging: every rule is unit-testable (AC-PRS-15). Whole-file all-or-nothing, as InwardPifParser:
// the first bad field rejects the file. The generic text rules (BOM, PGP/binary guard, strict UTF-8, line splitting,
// ASCII rule, the 12-character identifier) are the shared InwardFileText ones (design §3.1, I-13).
namespace TransactHub.Activities.Wps.Inward;

/// <summary>The error codes (PSI design §8.1) the parser can classify a rejection as.</summary>
public static class InwardPsiParseErrorCodes
{
    /// <summary>A malformed line, field, file name or encoding; no CSI, two CSIs, or no ESI line.</summary>
    public const string Malformed = "INWARD_PSI_MALFORMED";

    /// <summary>PGP-encrypted content (or a binary first byte): never parsed as text.</summary>
    public const string Unreadable = "INWARD_PSI_UNREADABLE";
}

/// <summary>An employee line (ESI). The Future fields are stored as received; a field absent from a 9-field line is null.</summary>
public sealed record InwardPsiEsiLine(
    int LineNo,
    string PersonId,
    string EmployeeName,
    string LaborCardNo,
    string? Future1,
    string? Future2,
    string? Future3,
    string? Future4,
    string? Future5,
    string? Future6);

/// <summary>The employer line (CSI), stored once on the file header (no UPDATE).</summary>
public sealed record InwardPsiControlRecord(
    int LineNo,
    string EmployerUniqueId,
    string EmployerName,
    string? Future1,
    string? Future2,
    string? Future3,
    string? Future4,
    string? Future5,
    string? Future6,
    string? Future7);

/// <summary>A successfully parsed PSI: exactly one CSI and at least one ESI.</summary>
public sealed record InwardPsiParsedFile(
    string WpsFileId,
    InwardPsiControlRecord Csi,
    IReadOnlyList<InwardPsiEsiLine> Esis,
    int IgnoredLineCount,
    IReadOnlyList<int> IgnoredLineNumbers)
{
    /// <summary>The number of ESI lines.</summary>
    public int EsiCount => Esis.Count;
}

/// <summary>A classified whole-file rejection. <see cref="Reason"/> never contains a field value (no personal data, §11, I-10).</summary>
public sealed record InwardPsiRejection(string ErrorCode, string Reason, int? LineNo, int? FieldNumber);

/// <summary>The outcome of <see cref="InwardPsiParser.Parse"/>: exactly one of <see cref="File"/> and <see cref="Rejection"/> is set.</summary>
public sealed class InwardPsiParseResult
{
    private InwardPsiParseResult(InwardPsiParsedFile? file, InwardPsiRejection? rejection)
    {
        File = file;
        Rejection = rejection;
    }

    /// <summary>The parsed file, when <see cref="IsSuccess"/>.</summary>
    public InwardPsiParsedFile? File { get; }

    /// <summary>The rejection, when not <see cref="IsSuccess"/>.</summary>
    public InwardPsiRejection? Rejection { get; }

    /// <summary>True when the file parsed.</summary>
    public bool IsSuccess => File is not null;

    internal static InwardPsiParseResult Ok(InwardPsiParsedFile file) => new(file, null);

    internal static InwardPsiParseResult Reject(InwardPsiRejection rejection) => new(null, rejection);
}

/// <summary>
/// Parses the bytes of a staged PSI (PSI design §5.2 step 1a to §5.4). The caller has already verified the size and
/// SHA-256 of the bytes exactly as staged (§5.2 step 1); this class receives those same bytes.
/// </summary>
public static class InwardPsiParser
{
    /// <summary>The FSD's text for a PSI with no ESI line (L350, App D L2115), kept in the rejection reason.</summary>
    public const string NoRecordsText = "No records in PSI";

    // §5.3: only the two observed shapes are accepted — the format lines' 10 fields and the UAT sample's 9 (Q-7, A-7).
    private const int ShortFieldCount = 9;
    private const int FullFieldCount = 10;

    private const int PersonIdMax = 50;
    private const int EmployeeNameMax = 150;
    private const int LaborCardNoMax = 30;
    private const int EmployerUniqueIdMax = 50;
    private const int EmployerNameMax = 150;
    private const int FutureMax = 100;

    /// <summary>Parses a staged PSI.</summary>
    /// <param name="stagedBytes">The file bytes exactly as staged (a leading UTF-8 BOM is allowed and removed).</param>
    /// <param name="fileName">The staged file name, from which the 12-character identifier is derived (§5.3).</param>
    public static InwardPsiParseResult Parse(byte[] stagedBytes, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stagedBytes);

        try
        {
            return ParseCore(stagedBytes, fileName);
        }
        catch (PsiRejectException ex)
        {
            return InwardPsiParseResult.Reject(ex.Rejection);
        }
    }

    private static InwardPsiParseResult ParseCore(byte[] stagedBytes, string? fileName)
    {
        // §5.2 steps 1a-3: one BOM removed, PGP/binary guard, strict UTF-8 (the shared rules).
        var decoded = InwardFileText.Decode(stagedBytes);
        switch (decoded.Failure)
        {
            case InwardFileDecodeFailure.PgpArmour:
                throw Reject(InwardPsiParseErrorCodes.Unreadable, "The file starts with a PGP armour header; encrypted content is not accepted.");
            case InwardFileDecodeFailure.BinaryFirstByte:
                throw Reject(InwardPsiParseErrorCodes.Unreadable, "The first byte is not an ASCII record type (binary or encrypted content, or a second byte-order mark).");
            case InwardFileDecodeFailure.InvalidUtf8:
                throw Reject(InwardPsiParseErrorCodes.Malformed, $"Line {decoded.Line}: the file is not valid UTF-8 (byte offset {decoded.ByteOffset}).", decoded.Line);
        }

        // As in the PIF parser, the identifier is derived after the content checks, so a PGP or encoding fault is
        // reported as such even when the file name is also bad.
        var wpsFileId = InwardFileText.TryDeriveWpsFileId(fileName, out var id) switch
        {
            InwardFileIdFailure.None => id,
            InwardFileIdFailure.EmptyName => throw Reject(InwardPsiParseErrorCodes.Malformed, "The file name is empty; the 12-character identifier cannot be derived."),
            InwardFileIdFailure.TooShort => throw Reject(InwardPsiParseErrorCodes.Malformed, $"The file name base is shorter than {InwardFileText.IdentifierLength} characters; the identifier cannot be derived."),
            _ => throw Reject(InwardPsiParseErrorCodes.Malformed, "The 12-character identifier taken from the file name contains a non-ASCII or control character."),
        };

        return ParseLines(decoded.Text!, wpsFileId);
    }

    private static InwardPsiParseResult ParseLines(string text, string wpsFileId)
    {
        var esis = new List<InwardPsiEsiLine>();
        var csis = new List<InwardPsiControlRecord>();
        var ignored = new List<int>();

        // Every line is read — ESI lines after the CSI are kept (§5.4; the legacy "reading stops at CSI", L279, dropped them).
        foreach (var (lineNo, line) in InwardFileText.Lines(text))
        {
            // Plain comma split, no quote handling (L277). The record tag is field 1, trimmed and case-insensitive
            // (L277; deliberately unlike the PIF parser, Q-12). Data fields are never trimmed (L297, Q-11).
            var fields = line.Split(',');
            var tag = fields[0].Trim();
            if (tag.Equals("ESI", StringComparison.OrdinalIgnoreCase))
            {
                esis.Add(ParseEsi(fields, lineNo));
            }
            else if (tag.Equals("CSI", StringComparison.OrdinalIgnoreCase))
            {
                csis.Add(ParseCsi(fields, lineNo));
            }
            else
            {
                ignored.Add(lineNo); // unknown tag or blank line: skipped and counted (L280)
            }
        }

        // §5.4: exactly one CSI, its position not checked (Activity 3's PCR rule); then at least one ESI.
        if (csis.Count == 0)
        {
            throw Reject(InwardPsiParseErrorCodes.Malformed, "The file has no CSI record; exactly one is required.");
        }

        if (csis.Count > 1)
        {
            throw Reject(InwardPsiParseErrorCodes.Malformed, $"The file has {csis.Count} CSI records; exactly one is required (second CSI at line {csis[1].LineNo}).", csis[1].LineNo);
        }

        if (esis.Count == 0)
        {
            throw Reject(InwardPsiParseErrorCodes.Malformed, $"{NoRecordsText}: the file has no ESI record.");
        }

        // Duplicate PersonIds are accepted, one row per line (design choice §5.3: nothing matches on PersonId).
        return InwardPsiParseResult.Ok(new InwardPsiParsedFile(wpsFileId, csis[0], esis, ignored.Count, ignored));
    }

    // ---- record parsers (§5.3 field table) ----------------------------------------------------------------------------------------

    private static InwardPsiEsiLine ParseEsi(string[] f, int lineNo)
    {
        RequireFieldCount(f, lineNo, "ESI");
        return new InwardPsiEsiLine(
            lineNo,
            RequiredAsciiText(f, lineNo, 2, "PersonId", PersonIdMax),
            UnicodeText(f, lineNo, 3, "EmployeeName", EmployeeNameMax),
            AsciiText(f, lineNo, 4, "LaborCardNo", LaborCardNoMax),
            Future(f, lineNo, 5, "Future1"),
            Future(f, lineNo, 6, "Future2"),
            Future(f, lineNo, 7, "Future3"),
            Future(f, lineNo, 8, "Future4"),
            Future(f, lineNo, 9, "Future5"),
            Future(f, lineNo, 10, "Future6"));
    }

    private static InwardPsiControlRecord ParseCsi(string[] f, int lineNo)
    {
        RequireFieldCount(f, lineNo, "CSI");
        return new InwardPsiControlRecord(
            lineNo,
            RequiredUnicodeText(f, lineNo, 2, "EmployerUniqueId", EmployerUniqueIdMax),
            RequiredUnicodeText(f, lineNo, 3, "EmployerName", EmployerNameMax),
            Future(f, lineNo, 4, "Future1"),
            Future(f, lineNo, 5, "Future2"),
            Future(f, lineNo, 6, "Future3"),
            Future(f, lineNo, 7, "Future4"),
            Future(f, lineNo, 8, "Future5"),
            Future(f, lineNo, 9, "Future6"),
            Future(f, lineNo, 10, "Future7"));
    }

    // ---- field rules --------------------------------------------------------------------------------------------------------------

    private static void RequireFieldCount(string[] f, int lineNo, string recordType)
    {
        if (f.Length is not (ShortFieldCount or FullFieldCount))
        {
            throw Reject(
                InwardPsiParseErrorCodes.Malformed,
                $"Line {lineNo} ({recordType}): field count is {f.Length}, expected {ShortFieldCount} or {FullFieldCount}.",
                lineNo);
        }
    }

    /// <summary>A VARCHAR field: printable ASCII and bounded; may be empty.</summary>
    private static string AsciiText(string[] f, int lineNo, int field, string name, int maxLength)
    {
        var v = f[field - 1];
        if (!InwardFileText.IsPrintableAscii(v))
        {
            throw FieldError(lineNo, field, name, "contains a non-ASCII or control character");
        }

        if (v.Length > maxLength)
        {
            throw FieldError(lineNo, field, name, $"is longer than {maxLength} characters");
        }

        return v;
    }

    private static string RequiredAsciiText(string[] f, int lineNo, int field, string name, int maxLength)
    {
        RequireNonBlank(f, lineNo, field, name);
        return AsciiText(f, lineNo, field, name, maxLength);
    }

    /// <summary>An NVARCHAR field: any Unicode (an Arabic name is accepted), bounded in UTF-16 code units as the column is.</summary>
    private static string UnicodeText(string[] f, int lineNo, int field, string name, int maxLength)
    {
        var v = f[field - 1];
        if (v.Length > maxLength)
        {
            throw FieldError(lineNo, field, name, $"is longer than {maxLength} characters");
        }

        return v;
    }

    private static string RequiredUnicodeText(string[] f, int lineNo, int field, string name, int maxLength)
    {
        RequireNonBlank(f, lineNo, field, name);
        return UnicodeText(f, lineNo, field, name, maxLength);
    }

    /// <summary>"Blank" is empty or whitespace-only, tested on the trimmed value; a non-blank value is kept untrimmed (R2 G-1).</summary>
    private static void RequireNonBlank(string[] f, int lineNo, int field, string name)
    {
        if (string.IsNullOrWhiteSpace(f[field - 1]))
        {
            throw FieldError(lineNo, field, name, "is blank (required)");
        }
    }

    /// <summary>A reserved field, stored as received (a single space stays a space); absent on a 9-field line → null.</summary>
    private static string? Future(string[] f, int lineNo, int field, string name) =>
        field <= f.Length ? UnicodeText(f, lineNo, field, name, FutureMax) : null;

    private static PsiRejectException FieldError(int lineNo, int field, string name, string problem) =>
        Reject(InwardPsiParseErrorCodes.Malformed, $"Line {lineNo}, field {field} ({name}): value {problem}.", lineNo, field);

    private static PsiRejectException Reject(string code, string reason, int? lineNo = null, int? field = null) =>
        new(new InwardPsiRejection(code, reason, lineNo, field));

    /// <summary>Control flow for the first failure; never escapes <see cref="Parse"/>.</summary>
    private sealed class PsiRejectException(InwardPsiRejection rejection) : Exception(rejection.Reason)
    {
        public InwardPsiRejection Rejection { get; } = rejection;
    }
}
