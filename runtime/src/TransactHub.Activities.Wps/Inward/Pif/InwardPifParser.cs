// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/InwardPifParser.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: Pure parser for the WPS inward Payment Instruction File (PIF), Activity 3 (design v2.0 §5.2 steps 1a-3, §5.3, §5.4).
// Responsibility: bytes in, either a fully parsed file or one classified rejection out. No I/O, no clock, no
// configuration, no logging: every rule is unit-testable (AC-PRS-11). Whole-file all-or-nothing: the first bad
// field rejects the file and nothing is returned for the lines before it.
namespace TransactHub.Activities.Wps.Inward;

using System.Globalization;

/// <summary>The error codes (design §8.1) the parser can classify a rejection as.</summary>
public static class InwardPifParseErrorCodes
{
    /// <summary>A malformed line, field, file name or encoding, or an orphan EVP / duplicate employee id.</summary>
    public const string Malformed = "INWARD_PIF_MALFORMED";

    /// <summary>The PCR is missing, repeated, or its count or total does not match the EDRs.</summary>
    public const string ReconciliationFailed = "INWARD_PIF_RECONCILIATION_FAILED";

    /// <summary>PGP-encrypted content (or a binary first byte): never parsed as text (I-11).</summary>
    public const string Unreadable = "INWARD_PIF_UNREADABLE";
}

/// <summary>An employee detail record (EDR), with the in-memory variable-pay flag (A3-R-03).</summary>
public sealed record InwardPifEdrLine(
    int LineNo,
    string EmployeeId,
    string RoutingCode,
    string AccountNoRaw,
    DateOnly PayStartDate,
    DateOnly PayEndDate,
    int DaysInPeriod,
    decimal FixedPay,
    decimal VariablePay,
    int LeaveDays,
    bool HasVariablePay);

/// <summary>An employee variable-pay record (EVP); allowances are the official-layout fields 4 to 10.</summary>
public sealed record InwardPifEvpLine(
    int LineNo,
    string EmployeeId,
    string RoutingCode,
    decimal Housing,
    decimal Conveyance,
    decimal Medical,
    decimal AnnualPassage,
    decimal Overtime,
    decimal Other,
    decimal LeaveEncashment);

/// <summary>The payment control record (PCR), stored once on the file header (A3-R-02).</summary>
public sealed record InwardPifControlRecord(
    int LineNo,
    string EstablishmentId,
    string EmployerBankRoutingCode,
    DateOnly FileCreationDate,
    string FileCreationTime,
    string SalaryMonth,
    int DeclaredRecordCount,
    decimal DeclaredTotalAmount,
    string PaymentCurrency,
    string EmployerReference);

/// <summary>A successfully parsed and reconciled PIF.</summary>
public sealed record InwardPifParsedFile(
    string WpsFileId,
    InwardPifControlRecord Header,
    IReadOnlyList<InwardPifEdrLine> Edrs,
    IReadOnlyList<InwardPifEvpLine> Evps,
    int IgnoredLineCount,
    IReadOnlyList<int> IgnoredLineNumbers,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The number of EDR lines.</summary>
    public int EdrCount => Edrs.Count;

    /// <summary>The number of EVP lines.</summary>
    public int EvpCount => Evps.Count;
}

/// <summary>A classified whole-file rejection. <see cref="Reason"/> never contains a field value (no account numbers in logs, §11).</summary>
public sealed record InwardPifRejection(string ErrorCode, string Reason, int? LineNo, int? FieldNumber);

/// <summary>The outcome of <see cref="InwardPifParser.Parse"/>: exactly one of <see cref="File"/> and <see cref="Rejection"/> is set.</summary>
public sealed class InwardPifParseResult
{
    private InwardPifParseResult(InwardPifParsedFile? file, InwardPifRejection? rejection)
    {
        File = file;
        Rejection = rejection;
    }

    /// <summary>The parsed file, when <see cref="IsSuccess"/>.</summary>
    public InwardPifParsedFile? File { get; }

    /// <summary>The rejection, when not <see cref="IsSuccess"/>.</summary>
    public InwardPifRejection? Rejection { get; }

    /// <summary>True when the file parsed and reconciled.</summary>
    public bool IsSuccess => File is not null;

    internal static InwardPifParseResult Ok(InwardPifParsedFile file) => new(file, null);

    internal static InwardPifParseResult Reject(InwardPifRejection rejection) => new(null, rejection);
}

/// <summary>
/// Parses the bytes of a staged PIF (design §5.2 step 1a to §5.4). The caller has already verified the size and
/// SHA-256 of the bytes exactly as staged (§5.2 step 1); this class receives those same bytes and removes the BOM itself.
/// </summary>
public static class InwardPifParser
{
    private const int IdentifierLength = InwardFileText.IdentifierLength;
    private const int FieldCount = 10;
    private const int MaxIntegerDigits = 14; // DECIMAL(18,4)
    private const int MaxDecimalPlaces = 4;

    /// <summary>Parses a staged PIF.</summary>
    /// <param name="stagedBytes">The file bytes exactly as staged (a leading UTF-8 BOM is allowed and removed here).</param>
    /// <param name="fileName">The staged file name, from which the 12-character identifier is derived (§5.3).</param>
    public static InwardPifParseResult Parse(byte[] stagedBytes, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stagedBytes);

        try
        {
            return ParseCore(stagedBytes, fileName);
        }
        catch (PifRejectException ex)
        {
            return InwardPifParseResult.Reject(ex.Rejection);
        }
    }

    private static InwardPifParseResult ParseCore(byte[] stagedBytes, string? fileName)
    {
        // §5.2 steps 1a-3 (one BOM removed, PGP/binary guard, strict UTF-8): the shared InwardFileText rules; the
        // rejection texts are this parser's own and unchanged (PSI design v2.0 §3.1, task T2).
        var decoded = InwardFileText.Decode(stagedBytes);
        switch (decoded.Failure)
        {
            case InwardFileDecodeFailure.PgpArmour:
                throw Reject(InwardPifParseErrorCodes.Unreadable, "The file starts with a PGP armour header; encrypted content is not accepted.");
            case InwardFileDecodeFailure.BinaryFirstByte:
                throw Reject(InwardPifParseErrorCodes.Unreadable, "The first byte is not an ASCII record type (binary or encrypted content, or a second byte-order mark).");
            case InwardFileDecodeFailure.InvalidUtf8:
                throw Reject(InwardPifParseErrorCodes.Malformed, $"Line {decoded.Line}: the file is not valid UTF-8 (byte offset {decoded.ByteOffset}).", decoded.Line);
        }

        var text = decoded.Text!;

        // design choice: the file-name identifier is derived after the content checks, so a PGP or encoding fault
        // is reported as such even when the file name is also bad.
        var wpsFileId = DeriveWpsFileId(fileName);

        return ParseLines(text, wpsFileId);
    }

    /// <summary>Derives the 12-character identifier: the file name without its last extension, then its last 12 characters (§5.3).</summary>
    private static string DeriveWpsFileId(string? fileName)
    {
        // The derivation is shared with the PSI parser (InwardFileText, PSI design I-13); the texts are this parser's.
        return InwardFileText.TryDeriveWpsFileId(fileName, out var id) switch
        {
            InwardFileIdFailure.None => id,
            InwardFileIdFailure.EmptyName => throw Reject(InwardPifParseErrorCodes.Malformed, "The file name is empty; the 12-character identifier cannot be derived."),
            InwardFileIdFailure.TooShort => throw Reject(InwardPifParseErrorCodes.Malformed, $"The file name base is shorter than {IdentifierLength} characters; the identifier cannot be derived."),
            _ => throw Reject(InwardPifParseErrorCodes.Malformed, "The 12-character identifier taken from the file name contains a non-ASCII or control character."),
        };
    }

    private static InwardPifParseResult ParseLines(string text, string wpsFileId)
    {
        var edrs = new List<InwardPifEdrLine>();
        var evps = new List<InwardPifEvpLine>();
        var pcrs = new List<InwardPifControlRecord>();
        var ignored = new List<int>();

        // A line ends at CRLF, LF or CR; CRLF is one terminator. The empty segment after the final terminator is not a line (§5.3, G-3).
        foreach (var (lineNo, line) in InwardFileText.Lines(text))
        {
            // Plain comma split, no quote handling (L173). The record type is field 1, exact and case-sensitive.
            var fields = line.Split(',');
            switch (fields[0])
            {
                case "EDR":
                    edrs.Add(ParseEdr(fields, lineNo));
                    break;
                case "EVP":
                    evps.Add(ParseEvp(fields, lineNo));
                    break;
                case "PCR":
                    pcrs.Add(ParsePcr(fields, lineNo));
                    break;
                default:
                    ignored.Add(lineNo); // unknown type or blank line: skipped and counted (L177)
                    break;
            }
        }

        return Reconcile(wpsFileId, edrs, evps, pcrs, ignored);
    }

    // ---- §5.4 reconciliation and EVP matching -------------------------------------------------------------------------------------

    private static InwardPifParseResult Reconcile(
        string wpsFileId,
        List<InwardPifEdrLine> edrs,
        List<InwardPifEvpLine> evps,
        List<InwardPifControlRecord> pcrs,
        List<int> ignored)
    {
        // Exactly one PCR; its position is not checked.
        if (pcrs.Count == 0)
        {
            throw Reject(InwardPifParseErrorCodes.ReconciliationFailed, "The file has no PCR record.");
        }

        if (pcrs.Count > 1)
        {
            throw Reject(InwardPifParseErrorCodes.ReconciliationFailed, $"The file has {pcrs.Count} PCR records; exactly one is required (second PCR at line {pcrs[1].LineNo}).", pcrs[1].LineNo);
        }

        var pcr = pcrs[0];

        if (pcr.DeclaredRecordCount != edrs.Count)
        {
            throw Reject(
                InwardPifParseErrorCodes.ReconciliationFailed,
                $"Line {pcr.LineNo}, field 7 (EDR count): the PCR declares {pcr.DeclaredRecordCount} EDR records but the file holds {edrs.Count}.",
                pcr.LineNo,
                7);
        }

        // Total salary = sum(fixed + variable), exact at 4 decimals (decimal arithmetic is exact at this scale).
        var actualTotal = edrs.Sum(e => e.FixedPay + e.VariablePay);
        if (pcr.DeclaredTotalAmount != actualTotal)
        {
            throw Reject(
                InwardPifParseErrorCodes.ReconciliationFailed,
                $"Line {pcr.LineNo}, field 8 (total salary): the PCR declares {pcr.DeclaredTotalAmount.ToString("0.0000", CultureInfo.InvariantCulture)} but the EDRs total {actualTotal.ToString("0.0000", CultureInfo.InvariantCulture)}.",
                pcr.LineNo,
                8);
        }

        // design choice: reconciliation runs before EVP matching, so a file whose EDR tags are lower-case (all skipped as
        // unknown, which leaves every EVP an orphan) is classified RECONCILIATION_FAILED as AC-PRS-07 / E-11 require.

        // EVP matching on employee id, after the whole file is parsed (E-8). Two EDRs with one employee id are ambiguous (E-9).
        var edrIndexByEmployee = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < edrs.Count; i++)
        {
            if (!edrIndexByEmployee.TryAdd(edrs[i].EmployeeId, i))
            {
                throw Reject(
                    InwardPifParseErrorCodes.Malformed,
                    $"Line {edrs[i].LineNo}, field 2 (EmployeeId): duplicate employee id (first EDR at line {edrs[edrIndexByEmployee[edrs[i].EmployeeId]].LineNo}).",
                    edrs[i].LineNo,
                    2);
            }
        }

        // design choice: several EVP lines for one employee are accepted (the design only rejects an EVP with no EDR);
        // the flag is existence-based, so an EVP of all zeros still sets it (AC-REC-06 on REF).
        var flagged = new bool[edrs.Count];
        foreach (var evp in evps)
        {
            if (!edrIndexByEmployee.TryGetValue(evp.EmployeeId, out var index))
            {
                throw Reject(
                    InwardPifParseErrorCodes.Malformed,
                    $"Line {evp.LineNo}, field 2 (EmployeeId): the EVP has no EDR with the same employee id.",
                    evp.LineNo,
                    2);
            }

            flagged[index] = true;
        }

        var finalEdrs = new List<InwardPifEdrLine>(edrs.Count);
        for (var i = 0; i < edrs.Count; i++)
        {
            finalEdrs.Add(edrs[i] with { HasVariablePay = flagged[i] });
        }

        var warnings = new List<string>();
        if (!string.Equals(pcr.PaymentCurrency, "AED", StringComparison.Ordinal))
        {
            // E-13: saved, not rejected.
            warnings.Add($"PCR currency is {pcr.PaymentCurrency}, not AED.");
        }

        return InwardPifParseResult.Ok(new InwardPifParsedFile(wpsFileId, pcr, finalEdrs, evps, ignored.Count, ignored, warnings));
    }

    // ---- record parsers (§5.3 field table) ----------------------------------------------------------------------------------------

    private static InwardPifEdrLine ParseEdr(string[] f, int lineNo)
    {
        RequireFieldCount(f, lineNo, "EDR");
        return new InwardPifEdrLine(
            lineNo,
            AsciiText(f, lineNo, 2, "EmployeeId", 30),
            AsciiText(f, lineNo, 3, "RoutingCode", 20),
            AsciiText(f, lineNo, 4, "AccountNo", 34),
            Date(f, lineNo, 5, "PayStartDate"),
            Date(f, lineNo, 6, "PayEndDate"),
            NonNegativeInt(f, lineNo, 7, "DaysInPeriod"),
            Amount(f, lineNo, 8, "FixedPay"),
            Amount(f, lineNo, 9, "VariablePay"),
            NonNegativeInt(f, lineNo, 10, "LeaveDays"),
            false);
    }

    private static InwardPifEvpLine ParseEvp(string[] f, int lineNo)
    {
        RequireFieldCount(f, lineNo, "EVP");
        return new InwardPifEvpLine(
            lineNo,
            AsciiText(f, lineNo, 2, "EmployeeId", 30),
            AsciiText(f, lineNo, 3, "RoutingCode", 20),
            Amount(f, lineNo, 4, "Housing"),
            Amount(f, lineNo, 5, "Conveyance"),
            Amount(f, lineNo, 6, "Medical"),
            Amount(f, lineNo, 7, "AnnualPassage"),
            Amount(f, lineNo, 8, "Overtime"),
            Amount(f, lineNo, 9, "Other"),
            Amount(f, lineNo, 10, "LeaveEncashment"));
    }

    private static InwardPifControlRecord ParsePcr(string[] f, int lineNo)
    {
        RequireFieldCount(f, lineNo, "PCR");
        return new InwardPifControlRecord(
            lineNo,
            UnicodeText(f, lineNo, 2, "EstablishmentId", 35), // NVARCHAR: exempt from the ASCII rule
            AsciiText(f, lineNo, 3, "EmployerBankRoutingCode", 20),
            Date(f, lineNo, 4, "FileCreationDate"),
            CreationTime(f, lineNo, 5),
            SalaryMonth(f, lineNo, 6),
            NonNegativeInt(f, lineNo, 7, "EdrCount"),
            Amount(f, lineNo, 8, "TotalSalary"),
            Currency(f, lineNo, 9),
            UnicodeText(f, lineNo, 10, "EmployerReference", 35)); // NVARCHAR: exempt from the ASCII rule
    }

    // ---- field rules --------------------------------------------------------------------------------------------------------------

    private static void RequireFieldCount(string[] f, int lineNo, string recordType)
    {
        if (f.Length != FieldCount)
        {
            throw Reject(
                InwardPifParseErrorCodes.Malformed,
                $"Line {lineNo} ({recordType}): field count is {f.Length}, expected exactly {FieldCount}.",
                lineNo);
        }
    }

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

    private static string UnicodeText(string[] f, int lineNo, int field, string name, int maxLength)
    {
        var v = f[field - 1];

        // Length of an NVARCHAR column is counted in UTF-16 code units, which is what string.Length counts.
        if (v.Length > maxLength)
        {
            throw FieldError(lineNo, field, name, $"is longer than {maxLength} characters");
        }

        return v;
    }

    private static DateOnly Date(string[] f, int lineNo, int field, string name)
    {
        if (!DateOnly.TryParseExact(f[field - 1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            throw FieldError(lineNo, field, name, "is not a valid yyyy-MM-dd date");
        }

        return d;
    }

    private static int NonNegativeInt(string[] f, int lineNo, int field, string name)
    {
        var v = f[field - 1];
        if (v.Length == 0 || !v.All(char.IsAsciiDigit) || !int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            throw FieldError(lineNo, field, name, "is not a non-negative integer");
        }

        return n;
    }

    /// <summary>Invariant decimal, no sign, no spaces, at most 4 decimal places counting trailing zeros (§5.3, F-3, I-16).</summary>
    private static decimal Amount(string[] f, int lineNo, int field, string name)
    {
        var v = f[field - 1];
        var dot = v.IndexOf('.');
        var intPart = dot >= 0 ? v[..dot] : v;
        var fracPart = dot >= 0 ? v[(dot + 1)..] : string.Empty;

        // design choice: a digit is required on both sides of the point (no ".5", no "5."), no sign, no exponent, no spacing.
        var lexicalOk = intPart.Length > 0 && intPart.All(char.IsAsciiDigit) && (dot < 0 || (fracPart.Length > 0 && fracPart.All(char.IsAsciiDigit)));
        if (!lexicalOk)
        {
            throw FieldError(lineNo, field, name, "is not a non-negative decimal number");
        }

        if (fracPart.Length > MaxDecimalPlaces)
        {
            throw FieldError(lineNo, field, name, $"has more than {MaxDecimalPlaces} decimal places");
        }

        if (!decimal.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) || d >= 100_000_000_000_000m)
        {
            // design choice: a value that cannot fit DECIMAL(18,4) (more than 14 integer digits) is malformed, never a SQL overflow (R2 G-5 spirit).
            throw FieldError(lineNo, field, name, $"is too large (more than {MaxIntegerDigits} integer digits)");
        }

        return d;
    }

    private static string CreationTime(string[] f, int lineNo, int field)
    {
        var v = f[field - 1];
        if (v.Length != 4 || !v.All(char.IsAsciiDigit) || int.Parse(v[..2], CultureInfo.InvariantCulture) > 23 || int.Parse(v[2..], CultureInfo.InvariantCulture) > 59)
        {
            throw FieldError(lineNo, field, "FileCreationTime", "is not a valid HHmm time");
        }

        return v;
    }

    private static string SalaryMonth(string[] f, int lineNo, int field)
    {
        var v = f[field - 1];
        if (v.Length != 6 || !v.All(char.IsAsciiDigit))
        {
            throw FieldError(lineNo, field, "SalaryMonth", "is not 6 digits in MMyyyy form");
        }

        var month = int.Parse(v[..2], CultureInfo.InvariantCulture);
        if (month is < 1 or > 12)
        {
            throw FieldError(lineNo, field, "SalaryMonth", "has a month outside 01 to 12");
        }

        return v; // any 4-digit year is accepted (design choice, §5.3)
    }

    private static string Currency(string[] f, int lineNo, int field)
    {
        var v = f[field - 1];

        // design choice: "3 letters" is read as three ASCII letters of either case, stored as given.
        if (v.Length != 3 || !v.All(char.IsAsciiLetter))
        {
            throw FieldError(lineNo, field, "PaymentCurrency", "is not a 3-letter currency code");
        }

        return v;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    private static PifRejectException FieldError(int lineNo, int field, string name, string problem) =>
        Reject(InwardPifParseErrorCodes.Malformed, $"Line {lineNo}, field {field} ({name}): value {problem}.", lineNo, field);

    private static PifRejectException Reject(string code, string reason, int? lineNo = null, int? field = null) =>
        new(new InwardPifRejection(code, reason, lineNo, field));

    /// <summary>Control flow for the first failure; never escapes <see cref="Parse"/>.</summary>
    private sealed class PifRejectException(InwardPifRejection rejection) : Exception(rejection.Reason)
    {
        public InwardPifRejection Rejection { get; } = rejection;
    }
}
