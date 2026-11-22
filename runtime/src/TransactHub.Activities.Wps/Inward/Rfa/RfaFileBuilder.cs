// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rfa/RfaFileBuilder.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: the only place the RFA layout lives (WPS inward Activity 25, design Inward_PAF_RFA_Generation v1.0 §6.3, §6.4, §5 step 6; A-4; task T2).
// Responsibility: bytes from the two stored records, deterministically (I-RG3), by the row's LayoutVersion (FSD-PROVISIONAL-1, the FSD UAT sample):
// one ARI line per line record in OrderNo order (ten comma-separated fields, CRLF after each), then one ARC line (nine fields, no line break after
// it); amounts with two decimals half away from zero, invariant culture; ASCII, no BOM. Also the content check of §5 step 6: a field the file
// cannot carry (blank where required, non-ASCII, a comma, CR or LF, or longer than its column; a reason longer than 30) → INVALID_CONTENT, never
// escaped silently (E-RG9). The bank confirms the layout before UAT (OD-RG2).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TransactHub.Activities.Wps.Inward.Rfa;

/// <summary>The built file: its bytes and lower-case SHA-256.</summary>
public sealed record RfaBuiltFile(byte[] Bytes, string Sha256);

public static class RfaFileBuilder
{
    /// <summary>The provisional layout of v1.0 (A-4); the only one.</summary>
    public const string LayoutVersion1 = "FSD-PROVISIONAL-1";

    public const string CurrentLayoutVersion = LayoutVersion1;

    public const string FieldWpsFileId = "WpsFileId";
    public const string FieldEstablishmentId = "EstablishmentId";
    public const string FieldEmployeeId = "EmployeeId";
    public const string FieldReturnReasonCode = "ReturnReasonCode";

    /// <summary>The longest reason the file carries (§6.4; <c>wps.RfaFileLines.ReturnReasonCode VARCHAR(30)</c>).</summary>
    public const int MaxReasonLength = 30;

    /// <summary><c>wps.RfaFiles.PifWpsFileId VARCHAR(12)</c>.</summary>
    public const int MaxWpsFileIdLength = 12;

    /// <summary><c>wps.RfaFiles.EstablishmentId VARCHAR(30)</c>.</summary>
    public const int MaxEstablishmentIdLength = 30;

    /// <summary><c>wps.RfaFileLines.EmployeeUniqueId VARCHAR(30)</c>.</summary>
    public const int MaxEmployeeIdLength = 30;

    private const string Crlf = "\r\n";
    private const char Separator = ',';
    private const string Space = " ";
    private const string Marker = "EWPMS";

    public static RfaBuiltFile Build(RfaFileRecord file, IReadOnlyList<RfaLineRecord> lines)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(lines);
        if (!string.Equals(file.LayoutVersion, LayoutVersion1, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown RFA layout version '{file.LayoutVersion}'.");
        }

        var prefix = RfaFileNamePolicy.RoutingPrefix(file.RoutingCode);
        var date = file.BusinessDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var reference = $"RFA-FROM-{prefix}-ON-{date}";
        var text = new StringBuilder();
        var ordered = lines.OrderBy(l => l.OrderNo).ToList();
        foreach (var line in ordered)
        {
            text.Append(string.Join(
                Separator,
                "ARI", file.PifWpsFileId, file.EstablishmentId, line.EmployeeUniqueId, Money(line.AmountReturned), line.ReturnReasonCode ?? string.Empty,
                reference, Space, Space, Marker)).Append(Crlf);
        }

        text.Append(string.Join(
            Separator,
            "ARC", $"ReturnsRef-{file.PifWpsFileId}-{date}", ordered.Count.ToString(CultureInfo.InvariantCulture), Money(RfaAmountRule.Total(ordered)),
            file.RoutingCode, Space, Space, Space, Marker));

        var bytes = Encoding.ASCII.GetBytes(text.ToString());
        return new RfaBuiltFile(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>
    /// §5 step 6: the names of the fields the file cannot carry, each named once. <c>WpsFileId</c>, <c>EstablishmentId</c> and each
    /// <c>EmployeeId</c> must be non-blank; a reason may be blank (E-RG8). Every field must be ASCII without a comma, CR or LF and fit its column;
    /// a reason at most 30 characters (E-RG9). Empty when the content is valid.
    /// </summary>
    public static IReadOnlyList<string> InvalidFields(string? wpsFileId, string? establishmentId, IEnumerable<RfaCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var invalid = new List<string>();
        void Check(string field, string? value, int maxLength, bool required)
        {
            var ok = string.IsNullOrEmpty(value) ? !required : (!required || !string.IsNullOrWhiteSpace(value)) && value.Length <= maxLength && IsCarried(value);
            if (!ok && !invalid.Contains(field))
            {
                invalid.Add(field);
            }
        }

        Check(FieldWpsFileId, wpsFileId, MaxWpsFileIdLength, required: true);
        Check(FieldEstablishmentId, establishmentId, MaxEstablishmentIdLength, required: true);
        foreach (var c in candidates)
        {
            Check(FieldEmployeeId, c.EmployeeId, MaxEmployeeIdLength, required: true);
            Check(FieldReturnReasonCode, c.Reason.Code, MaxReasonLength, required: false);
        }

        return invalid;
    }

    /// <summary>ASCII only, and no comma, CR or LF (§6.3).</summary>
    public static bool IsCarried(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.All(ch => char.IsAscii(ch) && ch is not (Separator or '\r' or '\n'));
    }

    private static string Money(decimal value) =>
        RfaAmountRule.Round2(value).ToString("0.00", CultureInfo.InvariantCulture);
}
