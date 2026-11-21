// Purpose: The structural rule for a CBUAE routing number (SSM 6.1), shared by SIF validation and the SIF reports.
// Responsibility: decide whether a value is a well-formed 9-digit routing code. Moved unchanged from
// WpsPlatformActivities.IsValidRoutingCode (2026-10-25), which now forwards here, so SIF validation behaves exactly as
// before while the reporting layer (which cannot reference the activities project) applies the same rule.
namespace TransactHub.Activities.Wps.Rules;

/// <summary>CBUAE routing-number rules (UAEWPS SSM 6.1).</summary>
public static class WpsRoutingCode
{
    /// <summary>
    /// Validates a CBUAE routing number against SSM 6.1: 9 digits, laid out CBBBEBBBB.
    /// </summary>
    /// <remarks>
    /// C = check digit, BBB = institution id, E = emirate id, BBBB = branch. Only the structural rules
    /// the SSM states are enforced here — length, all-numeric, a known emirate (SSM 7 lists 1-7) and a
    /// non-zero institution. The SSM also requires the code to exist in the WPS master database
    /// (errors 00810/00811); that is a lookup this validator has no access to, so it is deliberately
    /// left to the Central Bank rather than approximated with a hardcoded range.
    /// </remarks>
    public static bool IsValid(string? routingCode, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(routingCode))
        {
            error = "routing code is mandatory";
            return false;
        }

        var value = routingCode.Trim();
        if (value.Length != 9 || !value.All(char.IsAsciiDigit))
        {
            error = $"routing code must be exactly 9 digits in the SSM 6.1 format CBBBEBBBB (got '{value}')";
            return false;
        }

        // Positions 2-4 (0-based 1..3) are the institution id; position 5 (0-based 4) is the emirate.
        var institutionId = value[1..4];
        if (institutionId == "000")
        {
            error = $"routing code '{value}' has an empty institution id";
            return false;
        }

        var emirateId = value[4] - '0';
        if (emirateId is < 1 or > 7)
        {
            error = $"routing code '{value}' has emirate id '{value[4]}', which is not one of the 1-7 codes in SSM section 7";
            return false;
        }

        return true;
    }
}
