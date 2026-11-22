// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rri/InwardRriParser.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: Pure parser for the WPS inward Refund Request Instruction file (RRI), Activity 2 (design Inward_RRI_Extract_Save v1.0 §5.2
// steps 1a-3, §5.3, §5.4; task T3).
// Responsibility: bytes, a file name and the configured agent code in; either a fully parsed file or one classified rejection out. No I/O, no
// clock, no configuration read, no logging. Whole-file all-or-nothing, as InwardPsiParser: the first bad field rejects the file (decision
// Q-6). The generic text rules (BOM, PGP/binary guard, strict UTF-8, line splitting, ASCII rule, the 12-character reference) are the shared
// InwardFileText ones (AS-RRI2-07).
using System.Globalization;
using TransactHub.Activities.Wps.Inward.Rri.Constants;

namespace TransactHub.Activities.Wps.Inward.Rri;

/// <summary>
/// Parses the bytes of a staged RRI (design §5.2 step 1a to §5.4). The caller has already verified the size and SHA-256 of the bytes exactly
/// as staged (§5.2 step 1) and validated the parameters (§9); this class receives those same bytes and the validated agent code.
/// </summary>
public static class InwardRriParser
{
    /// <summary>The FSD's text for an RRI with no <c>DRR</c> line, kept in the rejection reason (§5.4 rule 5).</summary>
    public const string NoRecordsText = "No records in RRI";

    /// <summary>The width of <c>wps.InwardRriFile.FileName</c>: a longer name is malformed (§5.2, AC-INT-08).</summary>
    public const int FileNameMax = 150;

    /// <summary>The shortest base name (without its last extension) the positional rules can read (§5.4 rule 1).</summary>
    public const int MinBaseNameLength = 19;

    // §5.4: 1-based positions 4–6 carry the agent code, 7–19 the establishment reference (13 characters).
    private const int AgentCodeStart = 3;
    private const int EstablishmentReferenceStart = 6;
    private const int EstablishmentReferenceLength = 13;

    // §5.3: exactly ten fields for both record types (the FSD table has ten positions for each).
    private const int FieldCount = 10;

    // §5.3 widths: design choices sized from the one UAT sample (OD-6), equal to the §7 columns.
    private const int PersonIdMax = 30;
    private const int ReasonCodeMax = 10;
    private const int PayrollFileNameMax = 150;
    private const int PayrollWpsFileReferenceMax = 30;
    private const int AccountNoMax = 34;
    private const int HeaderWpsReferenceMax = 35;

    // §5.3 amount pattern ^\d{1,14}(\.\d{1,2})?$: fits DECIMAL(18,4).
    private const int MaxIntegerDigits = 14;
    private const int MaxDecimalPlaces = 2;

    // The field numbers the DRC reconciliation names.
    private const int DrcTotalField = 4;
    private const int DrcCountField = 5;

    /// <summary>Parses a staged RRI.</summary>
    /// <param name="stagedBytes">The file bytes exactly as staged (a leading UTF-8 BOM is allowed and removed).</param>
    /// <param name="fileName">The staged file name, from which the references are derived and the agent code is read (§5.4).</param>
    /// <param name="fileNameAgentCode">The validated <c>RriFileNameAgentCode</c>; compared case-sensitively with characters 4–6.</param>
    public static InwardRriParseResult Parse(byte[] stagedBytes, string? fileName, string fileNameAgentCode)
    {
        ArgumentNullException.ThrowIfNull(stagedBytes);
        ArgumentException.ThrowIfNullOrEmpty(fileNameAgentCode);

        try
        {
            // §5.2 steps 1a-3 first, so a PGP or encoding fault is reported as such even when the file name is also bad (as PSI); then the
            // name, before any line (design §3.1 step 4).
            var text = Decode(stagedBytes);
            var (establishmentReference, wpsFileReference) = ParseFileName(fileName, fileNameAgentCode);
            return InwardRriParseResult.Ok(ParseLines(text, establishmentReference, wpsFileReference));
        }
        catch (RriRejectException ex)
        {
            return InwardRriParseResult.Reject(ex.Rejection);
        }
    }

    // ---- §5.2 steps 1a-3: decoding ------------------------------------------------------------------------------------------------

    private static string Decode(byte[] stagedBytes)
    {
        var decoded = InwardFileText.Decode(stagedBytes);
        return decoded.Failure switch
        {
            InwardFileDecodeFailure.None => decoded.Text ?? throw new InvalidOperationException("A decoded file carries its text."),
            InwardFileDecodeFailure.PgpArmour => throw Reject(
                InwardRriErrorCodes.Unreadable, "The file starts with a PGP armour header; encrypted content is not accepted."),
            InwardFileDecodeFailure.BinaryFirstByte => throw Reject(
                InwardRriErrorCodes.Unreadable, "The first byte is not an ASCII record type (binary or encrypted content, or a second byte-order mark)."),
            InwardFileDecodeFailure.InvalidUtf8 => throw Reject(
                InwardRriErrorCodes.Malformed, $"Line {decoded.Line}: the file is not valid UTF-8 (byte offset {decoded.ByteOffset}).", decoded.Line),
            _ => throw new InvalidOperationException($"Unhandled decode failure {decoded.Failure}."),
        };
    }

    // ---- §5.4 rules 1-4: the file name --------------------------------------------------------------------------------------------

    private static (string EstablishmentReference, string WpsFileReference) ParseFileName(string? fileName, string fileNameAgentCode)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            throw Reject(InwardRriErrorCodes.Malformed, "The file name is empty; the references cannot be derived.");
        }

        if (fileName.Length > FileNameMax)
        {
            throw Reject(InwardRriErrorCodes.Malformed, $"The file name is longer than {FileNameMax} characters.");
        }

        var lastDot = fileName.LastIndexOf('.');
        var baseName = lastDot >= 0 ? fileName[..lastDot] : fileName;
        if (baseName.Length < MinBaseNameLength)
        {
            throw Reject(InwardRriErrorCodes.Malformed, $"The file name without its extension is shorter than {MinBaseNameLength} characters; the references cannot be derived.");
        }

        // Rule 2 (Q-3): a business exception, not a malformed file. Neither value goes into the reason (keys and positions only).
        if (!string.Equals(baseName.Substring(AgentCodeStart, InwardRriParameters.FileNameAgentCodeLength), fileNameAgentCode, StringComparison.Ordinal))
        {
            throw Reject(InwardRriErrorCodes.FilenameInvalid, $"Characters 4-6 of the file name do not equal the configured {InwardRriParameters.KeyFileNameAgentCode}.");
        }

        // Rule 3 (Q-4): positions 7–19. Printable ASCII, as every other varchar the activity stores (§5.3).
        var establishmentReference = baseName.Substring(EstablishmentReferenceStart, EstablishmentReferenceLength);
        if (!InwardFileText.IsPrintableAscii(establishmentReference))
        {
            throw Reject(InwardRriErrorCodes.Malformed, "The establishment reference (file name positions 7-19) contains a non-ASCII or control character.");
        }

        // Rule 4 (Q-5): the last 12 characters, through the shared rule. The base name is at least 19 characters, so only NonAscii is left.
        if (InwardFileText.TryDeriveWpsFileId(fileName, out var wpsFileReference) != InwardFileIdFailure.None)
        {
            throw Reject(InwardRriErrorCodes.Malformed, $"The WPS file reference (the last {InwardFileText.IdentifierLength} characters of the file name) contains a non-ASCII or control character.");
        }

        return (establishmentReference, wpsFileReference);
    }

    // ---- §5.3 and §5.4 rules 5-7: the lines ---------------------------------------------------------------------------------------

    private static InwardRriParsedFile ParseLines(string text, string establishmentReference, string wpsFileReference)
    {
        var drrs = new List<InwardRriDrrLine>();
        var drcs = new List<InwardRriControlRecord>();
        var ignored = new List<int>();

        // Every line is read: DRR lines after the DRC are kept (§5.4 rule 5).
        foreach (var (lineNo, line) in InwardFileText.Lines(text))
        {
            ReadLine(lineNo, line, establishmentReference, drrs, drcs, ignored);
        }

        var drc = SingleDrc(drcs);
        if (drrs.Count == 0)
        {
            throw Reject(InwardRriErrorCodes.Malformed, $"{NoRecordsText}: the file has no DRR record.");
        }

        Reconcile(drc, drrs);
        return new InwardRriParsedFile(establishmentReference, wpsFileReference, drc, drrs, ignored.Count, ignored);
    }

    private static void ReadLine(
        int lineNo, string line, string establishmentReference, List<InwardRriDrrLine> drrs, List<InwardRriControlRecord> drcs, List<int> ignored)
    {
        // Rule 7: a blank line (empty or whitespace-only) is skipped and counted.
        if (string.IsNullOrWhiteSpace(line))
        {
            ignored.Add(lineNo);
            return;
        }

        // Plain comma split, no quote handling. The tag is field 1, trimmed and case-insensitive; data fields are never trimmed (§5.3).
        var fields = line.Split(',');
        var tag = fields[0].Trim();
        if (tag.Equals(InwardRriRecordTypes.Detail, StringComparison.OrdinalIgnoreCase))
        {
            drrs.Add(ParseDrr(fields, lineNo, establishmentReference));
        }
        else if (tag.Equals(InwardRriRecordTypes.Header, StringComparison.OrdinalIgnoreCase))
        {
            drcs.Add(ParseDrc(fields, lineNo));
        }
        else
        {
            // Decision Q-6: unlike PSI, an unknown non-blank line rejects the file.
            throw Reject(InwardRriErrorCodes.Malformed, $"Line {lineNo}: the record type is neither DRC nor DRR.", lineNo, 1);
        }
    }

    /// <summary>§5.4 rule 5: exactly one DRC, at any position.</summary>
    private static InwardRriControlRecord SingleDrc(List<InwardRriControlRecord> drcs) => drcs.Count switch
    {
        0 => throw Reject(InwardRriErrorCodes.Malformed, "The file has no DRC record; exactly one is required."),
        1 => drcs[0],
        _ => throw Reject(
            InwardRriErrorCodes.Malformed,
            $"The file has {drcs.Count} DRC records; exactly one is required (second DRC at line {drcs[1].LineNo}).",
            drcs[1].LineNo),
    };

    /// <summary>§5.4 rule 6: count and total, exact decimal arithmetic. The reason states which check failed and where, never an amount.</summary>
    private static void Reconcile(InwardRriControlRecord drc, List<InwardRriDrrLine> drrs)
    {
        if (drc.DeclaredRecordCount != drrs.Count)
        {
            throw Reject(
                InwardRriErrorCodes.ReconciliationFailed,
                $"Line {drc.LineNo}, field {DrcCountField} (record count): the DRC count does not equal the number of DRR lines ({drrs.Count}).",
                drc.LineNo,
                DrcCountField);
        }

        if (drc.DeclaredTotalAmount != drrs.Sum(d => d.RefundAmount))
        {
            throw Reject(
                InwardRriErrorCodes.ReconciliationFailed,
                $"Line {drc.LineNo}, field {DrcTotalField} (total amount): the DRC total does not equal the sum of the DRR amounts.",
                drc.LineNo,
                DrcTotalField);
        }
    }

    // ---- record parsers (§5.3 field table) ----------------------------------------------------------------------------------------

    private static InwardRriDrrLine ParseDrr(string[] f, int lineNo, string establishmentReference)
    {
        RequireFieldCount(f, lineNo, InwardRriRecordTypes.Detail);

        // §5.4 rule 3 (I-6): field 2 equals the name-derived establishment reference, compared as received.
        if (!string.Equals(f[1], establishmentReference, StringComparison.Ordinal))
        {
            throw FieldError(lineNo, 2, "EstablishmentReference", "does not equal the establishment reference of the file name");
        }

        // Fields 9 and 10 are read for shape only; EWPMS is not validated (AS-RRI2-08).
        return new InwardRriDrrLine(
            lineNo,
            RequiredAsciiText(f, lineNo, 3, "PersonId", PersonIdMax),
            Amount(f, lineNo, 4, "RefundAmount"),
            RequiredAsciiText(f, lineNo, 5, "ReasonCode", ReasonCodeMax),
            RequiredUnicodeText(f, lineNo, 6, "PayrollFileName", PayrollFileNameMax),
            RequiredAsciiText(f, lineNo, 7, "PayrollWpsFileReference", PayrollWpsFileReferenceMax),
            RequiredAsciiText(f, lineNo, 8, "AccountNo", AccountNoMax));
    }

    private static InwardRriControlRecord ParseDrc(string[] f, int lineNo)
    {
        RequireFieldCount(f, lineNo, InwardRriRecordTypes.Header);

        // Fields 2 and 6–10 are read for shape only (AS-RRI2-08).
        return new InwardRriControlRecord(
            lineNo,
            RequiredAsciiText(f, lineNo, 3, "HeaderWpsReference", HeaderWpsReferenceMax),
            Amount(f, lineNo, DrcTotalField, "TotalAmount"),
            PositiveInt(f, lineNo, DrcCountField, "RecordCount"));
    }

    // ---- field rules --------------------------------------------------------------------------------------------------------------

    private static void RequireFieldCount(string[] f, int lineNo, string recordType)
    {
        if (f.Length != FieldCount)
        {
            throw Reject(
                InwardRriErrorCodes.Malformed,
                $"Line {lineNo} ({recordType}): field count is {f.Length}, expected {FieldCount}.",
                lineNo);
        }
    }

    /// <summary>A required VARCHAR field: non-blank, printable ASCII and bounded; kept untrimmed.</summary>
    private static string RequiredAsciiText(string[] f, int lineNo, int field, string name, int maxLength)
    {
        var v = RequiredUnicodeText(f, lineNo, field, name, maxLength);
        if (!InwardFileText.IsPrintableAscii(v))
        {
            throw FieldError(lineNo, field, name, "contains a non-ASCII or control character");
        }

        return v;
    }

    /// <summary>A required NVARCHAR field: non-blank ("blank" = empty or whitespace-only), bounded in UTF-16 code units as the column is.</summary>
    private static string RequiredUnicodeText(string[] f, int lineNo, int field, string name, int maxLength)
    {
        var v = f[field - 1];
        if (string.IsNullOrWhiteSpace(v))
        {
            throw FieldError(lineNo, field, name, "is blank (required)");
        }

        if (v.Length > maxLength)
        {
            throw FieldError(lineNo, field, name, $"is longer than {maxLength} characters");
        }

        return v;
    }

    /// <summary>
    /// <c>^\d{1,14}(\.\d{1,2})?$</c> over ASCII digits, culture-invariant, greater than zero (§5.3). The lexical test is done by hand, as the
    /// PIF parser does, so no culture and no Unicode digit class can widen it.
    /// </summary>
    private static decimal Amount(string[] f, int lineNo, int field, string name)
    {
        var v = f[field - 1];
        if (!IsAmountLexical(v))
        {
            throw FieldError(lineNo, field, name, $"is not a decimal number of at most {MaxIntegerDigits} integer digits and {MaxDecimalPlaces} decimal places");
        }

        var d = decimal.Parse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (d <= 0m)
        {
            throw FieldError(lineNo, field, name, "is not greater than zero");
        }

        return d;
    }

    private static bool IsAmountLexical(string v)
    {
        var dot = v.IndexOf('.');
        var intPart = dot >= 0 ? v[..dot] : v;
        var fracPart = dot >= 0 ? v[(dot + 1)..] : string.Empty;
        return intPart.Length is > 0 and <= MaxIntegerDigits
            && intPart.All(char.IsAsciiDigit)
            && (dot < 0 || (fracPart.Length is > 0 and <= MaxDecimalPlaces && fracPart.All(char.IsAsciiDigit)));
    }

    /// <summary>An integer of ASCII digits, at least 1 (§5.3, <c>DRC</c> field 5).</summary>
    private static int PositiveInt(string[] f, int lineNo, int field, string name)
    {
        var v = f[field - 1];
        if (v.Length == 0 || !v.All(char.IsAsciiDigit) || !int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
        {
            throw FieldError(lineNo, field, name, "is not an integer of at least 1");
        }

        return n;
    }

    private static RriRejectException FieldError(int lineNo, int field, string name, string problem) =>
        Reject(InwardRriErrorCodes.Malformed, $"Line {lineNo}, field {field} ({name}): value {problem}.", lineNo, field);

    private static RriRejectException Reject(string code, string reason, int? lineNo = null, int? field = null) =>
        new(new InwardRriRejection(code, reason, lineNo, field));

    /// <summary>Control flow for the first failure; never escapes <see cref="Parse"/>.</summary>
    private sealed class RriRejectException(InwardRriRejection rejection) : Exception(rejection.Reason)
    {
        public InwardRriRejection Rejection { get; } = rejection;
    }
}
