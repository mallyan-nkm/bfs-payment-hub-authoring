// Purpose: Classify a SIF employee as Own Bank or Other Bank from the routing code of the employee's EDR (2026-10-25).
// Responsibility: the pure comparison only — the full employee routing code (EDR field 3; in the store, the creditor agent's member id), trimmed, against the bank's configured routing code (the bank's BankRoutingCode configuration value,
// WpsRuleValues), trimmed, ordinal.
// An exact match is Own Bank; anything else — another bank, another branch of the same bank, a prefix, a blank — is
// Other Bank. Never the employer's SCR routing code, never a prefix or institution-code comparison. No I/O.
namespace TransactHub.Activities.Wps.Rules;

/// <summary>Where an employee's account is held, as the routing code tells it.</summary>
public enum EmployeeBankCategory
{
    /// <summary>The employee's routing code is the processing bank's configured routing code.</summary>
    OwnBank,

    /// <summary>The employee's routing code is not the processing bank's configured routing code.</summary>
    OtherBank,
}

/// <summary>Own Bank / Other Bank classification of one employee routing code.</summary>
public static class WpsEmployeeBankClassifier
{
    /// <summary>
    /// <see cref="EmployeeBankCategory.OwnBank"/> when <paramref name="employeeRoutingCode"/> equals
    /// <paramref name="configuredRoutingCode"/> after trimming both (ordinal); otherwise
    /// <see cref="EmployeeBankCategory.OtherBank"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="configuredRoutingCode"/> is null, empty or whitespace: without the bank's own code nothing can be
    /// classified, and treating every employee as Other Bank would be a wrong answer, not a missing one.
    /// </exception>
    public static EmployeeBankCategory Classify(string? employeeRoutingCode, string configuredRoutingCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoutingCode);

        return string.Equals(employeeRoutingCode?.Trim(), configuredRoutingCode.Trim(), StringComparison.Ordinal)
            ? EmployeeBankCategory.OwnBank
            : EmployeeBankCategory.OtherBank;
    }
}
