// Purpose: The pure CBX-only rules on SIF employee records (EDR and EVP) and on the file's layout.
// Responsibility: the legacy CBX rules the shared SIF validation does not apply — rules 4/5 and 46/47 (employee id
// numeric, or freezone prefix and length), 7 (duplicate agent + account), 14 (IBAN bank code matches the agent),
// 32/36 (pay field length), 54–60 (allowance length and presence), 77 (IBAN agent code) and 79 (empty line before the
// SCR) — with the legacy CBX messages. Applied only to CBX files, so IB validation is unchanged. No I/O: the caller
// supplies the corporate's freezone id.
namespace TransactHub.Activities.Wps.Rules;

using System.Globalization;


/// <summary>Pure CBX employee-record and layout rules. Each check returns the legacy messages it raises (none when valid).</summary>
public static class CbxRecordRules
{
    /// <summary>The freezone id that means "UAE mainland": not a freezone corporate.</summary>
    public const string MainlandFreezoneId = "000";

    /// <summary>The longest amount field the legacy rules accept (rules 32, 36, 54–60).</summary>
    public const int MaxAmountLength = 15;

    private const int MinEmployeeIdLength = 14;
    private const int MaxEmployeeIdLength = 35;
    private const int FieldCount = 10;
    private const int FirstBankAgentCode = 2;
    private const int LastBankAgentCode = 57;

    private static readonly string[] EvpComponents =
        ["Housing Allowance", "Conveyance Allowance", "Medical Allowance", "Annual Passage Allowance", "Overtime Allowance", "Other Allowance", "Leave Encashment"];

    /// <summary>True when <paramref name="freezoneId"/> names a freezone: set and not the mainland id.</summary>
    public static bool IsFreezone(string? freezoneId) =>
        !string.IsNullOrWhiteSpace(freezoneId) && !string.Equals(freezoneId.Trim(), MainlandFreezoneId, StringComparison.Ordinal);

    /// <summary>
    /// Rules 4/5, 14, 32, 36 and 77 on one EDR line (fields as split on commas). A line without 10 fields is left to the
    /// shared validation, which rejects it.
    /// </summary>
    /// <param name="lineNumber">The physical line number in the file, as the legacy messages report it.</param>
    /// <param name="freezoneId">The corporate's freezone id; null, empty or <see cref="MainlandFreezoneId"/> for mainland.</param>
    public static IReadOnlyList<string> CheckEdr(IReadOnlyList<string> fields, int lineNumber, string? freezoneId)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count != FieldCount)
            return [];

        var messages = new List<string>();
        var personId = fields[1].Trim();
        var agent = fields[2].Trim();
        var account = fields[3].Trim();

        if (!IsValidEmployeeId(personId, freezoneId))
            messages.Add($"Invalid MOL Person ID in Line No : {lineNumber}, MOL ID : {personId}");

        if (account.StartsWith("AE", StringComparison.OrdinalIgnoreCase))
        {
            // Rule 77: an IBAN can only be held at a bank, i.e. an agent whose code (routing code positions 2–4) is 002–057.
            if (!IsBankAgent(agent))
                messages.Add($"IBAN Validation Failed due to invalid Agent ID for Account No,in Line No :{lineNumber}");
            // Rule 14: the IBAN's bank code (positions 5–7) must be the agent's (routing code positions 2–4).
            else if (account.Length < 7 || !string.Equals(account.Substring(4, 3), agent.Substring(1, 3), StringComparison.Ordinal))
                messages.Add($"Invalid Employee Account No with AgentRouting code in Line No :{lineNumber}, MOL ID : {personId}");
        }

        if (fields[7].Trim().Length > MaxAmountLength)
            messages.Add($"Income Fixed Component exceeds permissible length in Line No :{lineNumber}");
        if (fields[8].Trim().Length > MaxAmountLength)
            messages.Add($"Income Variable Component exceeds permissible length({MaxAmountLength}) in Line No :{lineNumber}, MOL ID : {personId}");

        return messages;
    }

    /// <summary>Rules 46/47 and 54–60 on one EVP line. A line without 10 fields is left to the shared validation.</summary>
    public static IReadOnlyList<string> CheckEvp(IReadOnlyList<string> fields, int lineNumber, string? freezoneId)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count != FieldCount)
            return [];

        var messages = new List<string>();
        var employeeId = fields[1].Trim();
        if (!IsValidEmployeeId(employeeId, freezoneId))
            messages.Add($"Invalid Employee ID in Line No : {lineNumber}, MOL ID : {employeeId}");

        for (var i = 0; i < EvpComponents.Length; i++)
        {
            var value = fields[3 + i].Trim();
            var name = EvpComponents[i];
            if (value.Length == 0)
                messages.Add($"{name} Component cannot be null in Line No :{lineNumber}, {name} : {value}");
            else if (value.Length > MaxAmountLength)
                messages.Add($"{name} Component exceeds permissible length in Line No :{lineNumber}");
        }

        return messages;
    }

    /// <summary>
    /// Rule 7's message: the same agent routing code and account appear on two EDR lines. The account is masked
    /// (<see cref="AccountMasking.Mask"/>): the message is stored with the transaction's history and returned as the
    /// activity's failure, so it must not carry an employee's full account number.
    /// </summary>
    public static string DuplicateAgentAccountMessage(int lineNumber, string account, string agent) =>
        $"Duplicate Employee Account with Agent value in Line No :{lineNumber}, Employee Account : {AccountMasking.Mask(account)}, Agent value : {agent}";

    /// <summary>
    /// Rule 79: an empty line anywhere before the SCR record. Returns the message for the first one, or null. Blank lines
    /// after the SCR are not this rule's concern. A file without an SCR is left to the shared validation.
    /// </summary>
    public static string? CheckEmptyLineBeforeScr(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith(SifRecordTypes.Scr, StringComparison.Ordinal))
                return null;
            if (string.IsNullOrWhiteSpace(line))
                return $"Invalid File Format Found! Empty Line Found In Uploaded SIF File,in Line No :{i + 1}";
        }

        return null;
    }

    // Rules 4/5 and 46/47: a mainland corporate's employee ids are numbers; a freezone corporate's start with its
    // freezone id and are 14–35 characters long.
    private static bool IsValidEmployeeId(string id, string? freezoneId)
    {
        if (!IsFreezone(freezoneId))
            return decimal.TryParse(id, NumberStyles.Number, CultureInfo.InvariantCulture, out _);

        return id.Length is >= MinEmployeeIdLength and <= MaxEmployeeIdLength
            && id.StartsWith(freezoneId!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBankAgent(string agent) =>
        agent.Length >= 4
        && agent.All(char.IsAsciiDigit)
        && int.TryParse(agent.AsSpan(1, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code)
        && code is >= FirstBankAgentCode and <= LastBankAgentCode;
}
