// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/PrcFileBuilder.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: Builds the exact bytes of an inward PRC file (design v1.0 §6.1, §6.3).
// Responsibility: two lines (RDR, RCR) of ten comma-separated fields, ASCII, no BOM, CRLF after each line. Pure and
// culture-independent: every number goes through one formatter (AwayFromZero, two decimals, invariant culture, A-4).
using System.Globalization;
using System.Text;

namespace TransactHub.Activities.Wps.Inward;

public static class PrcFileBuilder
{
    private const string Crlf = "\r\n";
    private const string Trailer = "EWPMS";

    /// <summary>The file text. Throws <see cref="PrcInvalidPifNameException"/> when the PIF name cannot be printed.</summary>
    public static string BuildText(PrcFileContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!IsPrintable(content.PifFileName))
        {
            throw new PrcInvalidPifNameException("The PIF file name is empty, non-ASCII, or contains a comma, CR or LF.");
        }

        var fixedPay = Money(content.SumFixed);
        var variablePay = Money(content.SumVariable);
        var total = Money(content.SumFixed + content.SumVariable);
        var declared = content.DeclaredRecordCount.ToString(CultureInfo.InvariantCulture);
        var date = content.BusinessInstant.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var time = content.BusinessInstant.ToString("HHmm", CultureInfo.InvariantCulture);

        var rdr = string.Join(',', "RDR", content.PifFileName, fixedPay, variablePay, total, declared, " ", " ", " ", Trailer);
        var rcr = string.Join(',', "RCR", " ", fixedPay, variablePay, total, "1", date, time, " ", Trailer);
        return rdr + Crlf + rcr + Crlf;
    }

    /// <summary>ASCII bytes, no BOM.</summary>
    public static byte[] BuildBytes(PrcFileContent content) => Encoding.ASCII.GetBytes(BuildText(content));

    /// <summary>True when the name is non-empty printable ASCII with no comma, CR or LF (E-G8).</summary>
    public static bool IsPrintable(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c < 0x20 || c > 0x7E || c == ',')
            {
                return false;
            }
        }

        return true;
    }

    private static string Money(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
}
