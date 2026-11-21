// Purpose: The pure CBX-only rules on the SIF control record (SCR) (ported from the source's CbxScrRules, US 5001).
// Responsibility: read SCR field 10 (the debit account a CBX file carries), and apply rule 82 (it is present)
// and rule 83 (it is one of the corporate's registered accounts, compared on the rightmost
// CbxRuleSettings.AccountMatchLength characters), with the legacy CBX messages. Rule 81 (exactly 10 SCR
// fields) is already enforced for every channel; only its CBX message text lives here. Rule 66 (SCR field 3 is
// this bank's own routing code, 2026-10-07 traceability check) is CBX-only, from the CBX legacy rules. The registered
// accounts are loaded by the caller (wps.ValidateSifFile), so this class does no I/O.
namespace TransactHub.Activities.Wps.Rules;

/// <summary>The outcome of rules 82/83 on one SCR line.</summary>
/// <param name="DebitAccount">SCR field 10, trimmed; null when blank.</param>
/// <param name="RegisteredAccount">
/// The one registered account <paramref name="DebitAccount"/> matched under rule 83, as registered; null when rule 83
/// did not run or failed.
/// </param>
/// <param name="Failure">The first rule that failed, or null.</param>
public sealed record CbxDebitAccountCheck(string? DebitAccount, string? RegisteredAccount, WpsRuleFailure? Failure);

/// <summary>Pure CBX SCR rules 66 and 81–83. Failures carry validation error code <c>007</c>.</summary>
public static class CbxScrRules
{
    public const string ValidationErrorCode = "007";
    public const string DebitAccountMissingMessage = "Debit Account No is not available in the last column of SCR!";
    public const string DebitAccountNotRegisteredMessage = "Debit Account No does not match with the registered corporate accounts!";

    /// <summary>Fields in an SCR line: fixed by the Central Bank SIF format, the same for every bank.</summary>
    public const int ScrFieldCount = 10;

    /// <summary>The longest account a debit account can be: an IBAN (ISO 13616) has at most 34 characters.</summary>
    public const int MaxAccountLength = 34;

    private const int DebitAccountFieldIndex = ScrFieldCount - 1;
    private const int BankRoutingCodeFieldIndex = 2;

    /// <summary>
    /// The bank's own routing code rule 66 compares SCR field 3 with. One neutral parameter for every bank: the
    /// source named it after the tenant (<c>{tenant}RoutingCode</c>), which put a bank name in a shared key.
    /// </summary>
    public const string BankRoutingCodeParam = "BankRoutingCode";

    /// <summary>Rule 66's legacy CBX message.</summary>
    public static string BankRoutingCodeMessage(string? value, int lineNumber) =>
        $"Control Record: Bank Code of the Employer {value} is invalid in Line No :{lineNumber}";

    /// <summary>
    /// Rule 66 (CBX): SCR field 3, the employer's bank routing code, must be this bank's own routing code. The generated
    /// Central Bank SIF carries the configured code either way; this rejects a corporate file that names another bank.
    /// </summary>
    public static WpsRuleFailure? CheckBankRoutingCode(IReadOnlyList<string> scrFields, string configuredRoutingCode, int lineNumber)
    {
        ArgumentNullException.ThrowIfNull(scrFields);
        var value = scrFields.Count > BankRoutingCodeFieldIndex ? scrFields[BankRoutingCodeFieldIndex].Trim() : null;
        return string.Equals(value, configuredRoutingCode.Trim(), StringComparison.Ordinal)
            ? null
            : new WpsRuleFailure(ValidationErrorCode, BankRoutingCodeMessage(value, lineNumber));
    }

    /// <summary>Rule 81's CBX message for an SCR line that does not have exactly <see cref="ScrFieldCount"/> fields.</summary>
    public static string ColumnCountMessage(int lineNumber) =>
        $"Invalid No. of columns in SIF: Expected No is {ScrFieldCount}, COMMA is expected as field delimiter in Line No: {lineNumber}";

    /// <summary>SCR field 10, trimmed; null when the line has fewer than 10 fields or the field is blank.</summary>
    public static string? ReadDebitAccount(IReadOnlyList<string>? scrFields)
    {
        if (scrFields is null || scrFields.Count <= DebitAccountFieldIndex)
            return null;
        var value = scrFields[DebitAccountFieldIndex]?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// The single registered account <paramref name="debitAccount"/> matches, or null. A value longer than
    /// <see cref="CbxRuleSettings.AccountMatchLength"/> is compared on its rightmost that-many characters, so a
    /// 23-character IBAN matches the 15-digit account it ends with.
    /// </summary>
    /// <remarks>
    /// Fail-closed on three points, because the matched value decides which account is debited:
    /// the debit account must be 1–<see cref="MaxAccountLength"/> ASCII letters or digits (otherwise a suffix
    /// match would accept any prefix in front of a registered account's tail); registered entries that are blank
    /// or not of that shape never match; and a suffix shared by two different registered accounts is ambiguous
    /// and matches neither.
    /// </remarks>
    public static string? FindRegisteredAccount(string? debitAccount, IEnumerable<string?> registeredAccounts, CbxRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(registeredAccounts);
        ArgumentNullException.ThrowIfNull(settings);

        var debit = debitAccount?.Trim();
        if (!IsAccountShaped(debit))
            return null;

        var debitKey = MatchKey(debit!, settings.AccountMatchLength);
        var matches = registeredAccounts
            .Select(account => account?.Trim())
            .Where(IsAccountShaped)
            .Where(account => string.Equals(MatchKey(account!, settings.AccountMatchLength), debitKey, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>True when <see cref="FindRegisteredAccount"/> finds exactly one registered account.</summary>
    public static bool MatchesRegisteredAccount(string? debitAccount, IEnumerable<string?> registeredAccounts, CbxRuleSettings settings) =>
        FindRegisteredAccount(debitAccount, registeredAccounts, settings) is not null;

    /// <summary>
    /// Rules 82 and 83 on one SCR line, each only when its flag in <paramref name="settings"/> is on. Rule 83 is not
    /// applied to a blank field 10 when rule 82 is off: there is no account to match, and the caller keeps the
    /// request account. A present field 10 that is not account-shaped fails rule 83 even when rule 83 is off, so
    /// a malformed value is never handed on as the account to debit.
    /// </summary>
    public static CbxDebitAccountCheck CheckDebitAccount(
        IReadOnlyList<string>? scrFields, IEnumerable<string?> registeredAccounts, CbxRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(registeredAccounts);
        ArgumentNullException.ThrowIfNull(settings);

        var debitAccount = ReadDebitAccount(scrFields);
        if (debitAccount is null)
        {
            return settings.ScrDebitAccountRequired
                ? new CbxDebitAccountCheck(null, null, new WpsRuleFailure(ValidationErrorCode, DebitAccountMissingMessage))
                : new CbxDebitAccountCheck(null, null, null);
        }

        var notRegistered = new WpsRuleFailure(ValidationErrorCode, DebitAccountNotRegisteredMessage);
        if (!IsAccountShaped(debitAccount))
            return new CbxDebitAccountCheck(debitAccount, null, notRegistered);

        if (!settings.ScrDebitAccountRegisteredCheck)
            return new CbxDebitAccountCheck(debitAccount, null, null);

        var registered = FindRegisteredAccount(debitAccount, registeredAccounts, settings);
        return registered is null
            ? new CbxDebitAccountCheck(debitAccount, null, notRegistered)
            : new CbxDebitAccountCheck(debitAccount, registered, null);
    }

    private static bool IsAccountShaped(string? account) =>
        !string.IsNullOrEmpty(account) && account.Length <= MaxAccountLength && account.All(char.IsAsciiLetterOrDigit);

    private static string MatchKey(string account, int matchLength) =>
        account.Length > matchLength ? account[^matchLength..] : account;
}
