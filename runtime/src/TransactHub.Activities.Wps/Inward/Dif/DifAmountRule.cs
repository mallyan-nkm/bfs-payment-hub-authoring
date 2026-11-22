// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Dif/DifAmountRule.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: the disbursed-amount rule of WPS inward Activity 20, DIF generation (design Inward_PAF_DIF_Generation v1.0 §6.2, I-DF6, I-DF11; task T3).
// Responsibility: a pure function from an EDR's fixed pay F, variable pay V (both AED, never converted, AS-A20-06) and recovery R ≥ 0 to the
// disbursed fixed and variable parts and the rounded withdrawal amount: the recovery comes off the fixed pay first, the remainder off the variable
// pay, never twice, never below zero. A negative R is a provider defect and throws (never clamped).

namespace TransactHub.Activities.Wps.Inward.Dif;

/// <summary>The disbursed parts (unrounded, <c>DECIMAL(18,4)</c>) and the DIR amount (rounded to 2 places).</summary>
public sealed record DifAmounts(decimal FixedDisbursed, decimal VariableDisbursed, decimal WithdrawalAmount);

public static class DifAmountRule
{
    /// <summary>
    /// <c>FixedDisbursed = max(0, F − R)</c>; <c>VariableDisbursed = max(0, V − max(0, R − F))</c>;
    /// <c>WithdrawalAmount = round2(FixedDisbursed + VariableDisbursed)</c>, half away from zero.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="recovery"/> is negative.</exception>
    public static DifAmounts Apply(decimal fixedPay, decimal variablePay, decimal recovery)
    {
        if (recovery < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recovery), recovery, "A DIF recovery amount is never negative (design §6.2).");
        }

        var fixedDisbursed = Math.Max(0m, fixedPay - recovery);
        var variableDisbursed = Math.Max(0m, variablePay - Math.Max(0m, recovery - fixedPay));
        return new DifAmounts(fixedDisbursed, variableDisbursed, Round2(fixedDisbursed + variableDisbursed));
    }

    /// <summary>Two places, half away from zero (§6.1).</summary>
    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
