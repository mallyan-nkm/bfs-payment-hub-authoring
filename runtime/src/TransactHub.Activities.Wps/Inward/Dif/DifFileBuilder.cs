// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Dif/DifFileBuilder.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: the only place the DIF layout lives (WPS inward Activity 20, design Inward_PAF_DIF_Generation v1.0 §6.1, §6.4, §5.1 step 6; A-4; task T3).
// Responsibility: bytes from the two stored records, deterministically (I-DF8), by the row's LayoutVersion (FSD-PROVISIONAL-1): one DIR line per
// line record in OrderNo order, then one DCR line; ten comma-separated fields per line; numbers with two decimals half away from zero, invariant
// culture; ASCII, no BOM, CRLF after every line including DCR. Also the content check of §5.1 step 6: every written text field non-blank, ASCII,
// no comma, CR or LF (E-DF11).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TransactHub.Activities.Wps.Inward.Dif;

/// <summary>The built file: its bytes and lower-case SHA-256.</summary>
public sealed record DifBuiltFile(byte[] Bytes, string Sha256);

public static class DifFileBuilder
{
    /// <summary>The provisional layout of v1.0 (A-4); the only one.</summary>
    public const string LayoutVersion1 = "FSD-PROVISIONAL-1";

    public const string CurrentLayoutVersion = LayoutVersion1;

    public const string FieldWpsFileId = "WpsFileId";
    public const string FieldEmployeeId = "EmployeeId";
    public const string FieldBeneficiaryAccount = "BeneficiaryAccount";
    public const string FieldAgentId = "AgentId";
    public const string FieldDifFileName = "DifFileName";

    private const string Crlf = "\r\n";
    private const char Separator = ',';
    private const string Zero = "0.00";
    private const string Marker = "EWPMS";

    public static DifBuiltFile Build(DifFileRecord file, IReadOnlyList<DifLineRecord> lines)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(lines);
        if (!string.Equals(file.LayoutVersion, LayoutVersion1, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown DIF layout version '{file.LayoutVersion}'.");
        }

        var text = new StringBuilder();
        var total = 0m;
        var ordered = lines.OrderBy(l => l.OrderNo).ToList();
        foreach (var line in ordered)
        {
            var amount = DifAmountRule.Round2(line.WithdrawalAmount);
            total += amount;
            text.Append(string.Join(
                Separator,
                "DIR", file.WpsFileId, line.EmployeeId, line.BeneficiaryAccount, line.TransactionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Money(amount), Zero, string.Empty, string.Empty, line.EdrId.ToString(CultureInfo.InvariantCulture))).Append(Crlf);
        }

        var printedTotal = Money(total);
        text.Append(string.Join(
            Separator,
            "DCR", file.AgentId, file.FileCreatedAtBusiness.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            file.FileCreatedAtBusiness.ToString("HHmm", CultureInfo.InvariantCulture), ordered.Count.ToString(CultureInfo.InvariantCulture), printedTotal,
            printedTotal, Zero, Marker, Marker)).Append(Crlf);

        var bytes = Encoding.ASCII.GetBytes(text.ToString());
        return new DifBuiltFile(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>The DCR total: Σ of the printed DIR amounts (I-DF7) — what <c>TotalWithdrawalAmount</c> stores.</summary>
    public static decimal Total(IEnumerable<DifLineRecord> lines) => lines.Sum(l => DifAmountRule.Round2(l.WithdrawalAmount));

    /// <summary>
    /// §5.1 step 6: the names of the written text fields that are blank, non-ASCII or contain a comma, CR or LF (each named once; a NULL
    /// account is blank). Empty when the content is valid.
    /// </summary>
    public static IReadOnlyList<string> InvalidFields(string? wpsFileId, string? agentId, string? difFileName, IEnumerable<DifCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var invalid = new List<string>();
        void Check(string field, string? value)
        {
            if (!IsValidText(value) && !invalid.Contains(field))
            {
                invalid.Add(field);
            }
        }

        Check(FieldWpsFileId, wpsFileId);
        Check(FieldAgentId, agentId);
        Check(FieldDifFileName, difFileName);
        foreach (var c in candidates)
        {
            Check(FieldEmployeeId, c.EmployeeId);
            Check(FieldBeneficiaryAccount, c.BeneficiaryAccount);
        }

        return invalid;
    }

    /// <summary>Non-blank, ASCII only, and no comma, CR or LF (§5.1 step 6).</summary>
    public static bool IsValidText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(ch => char.IsAscii(ch) && ch is not (Separator or '\r' or '\n'));

    private static string Money(decimal value) =>
        DifAmountRule.Round2(value).ToString("0.00", CultureInfo.InvariantCulture);
}
