// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rfa/RfaAmountRule.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: the returned-amount rule of WPS inward Activity 25, RFA generation (design Inward_PAF_RFA_Generation v1.0 §6.3, §6.4; A-6; task T2).
// Responsibility: a pure function from an EDR's fixed pay F and variable pay V (both AED as the PIF declared them, never converted) to the ARI
// amount: round2(F + V), half away from zero; and the ARC total: the sum of the rounded line amounts (AC-AMT-01).

namespace TransactHub.Activities.Wps.Inward.Rfa;

public static class RfaAmountRule
{
    /// <summary>Amount Returned = <c>round2(FixedPay + VariablePay)</c>, half away from zero (§6.4, A-6).</summary>
    public static decimal Returned(decimal fixedPay, decimal variablePay) => Round2(fixedPay + variablePay);

    /// <summary>The ARC total and <c>wps.RfaFiles.TotalAmount</c>: Σ of the rounded line amounts (§6.3), not the rounded sum.</summary>
    public static decimal Total(IEnumerable<RfaLineRecord> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Sum(l => Round2(l.AmountReturned));
    }

    /// <summary>Two places, half away from zero (§6.3).</summary>
    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
