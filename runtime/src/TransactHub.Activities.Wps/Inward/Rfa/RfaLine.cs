// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rfa/RfaLine.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: the immutable records of an RFA of WPS inward Activity 25, RFA generation (design Inward_PAF_RFA_Generation v1.0 §6.1, §6.4, §7.1, §7.2;
// task T2).
// Responsibility: the shape of one wps.RfaFiles row and its wps.RfaFileLines rows, as persisted and as read back (the builder takes only these,
// I-RG3), the candidate EDRs with their resolved return reason, and the pure composition of the lines: order numbers 1…n by Pif_Edr.Id (§6.1,
// I-RG4) and the amount rule per line (§6.4, A-6).

namespace TransactHub.Activities.Wps.Inward.Rfa;

/// <summary>One <c>wps.RfaFiles</c> row. <see cref="Id"/> is 0 before it is persisted.</summary>
/// <param name="RoutingCode">The <c>HeadRoutingCode</c> used: characters 1–3 in the name and the ARI reference, the whole code in the ARC.</param>
/// <param name="PifWpsFileId">The PIF's <c>WpsFileId</c> (ARI field 2, the ARC reference).</param>
/// <param name="EstablishmentId">The PIF's <c>EstablishmentId</c> (ARI field 3).</param>
/// <param name="PendingVerificationCount">The PIF's EDRs at <c>RfaInitiatedEdrStatus</c> (30), counted, not included (A-2).</param>
/// <param name="TotalAmount">Σ of the rounded line amounts (the ARC total, AC-AMT-01).</param>
/// <param name="BusinessDate">The business date of the generation instant (the two reference dates, §6.2).</param>
/// <param name="GeneratedAtUtc">The generation instant in UTC (<c>rfaGeneratedAtUtc</c>).</param>
public sealed record RfaFileRecord(
    long Id,
    string TenantId,
    string TransactionId,
    int GenerationNo,
    string RfaFileName,
    string RoutingCode,
    string PifWpsFileId,
    string EstablishmentId,
    int LineCount,
    int PendingVerificationCount,
    decimal TotalAmount,
    DateOnly BusinessDate,
    DateTime GeneratedAtUtc,
    string LayoutVersion,
    string ContentSha256,
    long SizeBytes,
    string? WorkflowId,
    string CreatedBy);

/// <summary>One <c>wps.RfaFileLines</c> row: one ARI line, <see cref="OrderNo"/> 1… in <see cref="EdrId"/> order (the Activity 29 key).</summary>
/// <param name="EmployeeUniqueId">The EDR's <c>EmployeeId</c> (ARI field 4).</param>
/// <param name="AmountReturned"><c>FixedPay + VariablePay</c>, rounded to 2 places half away from zero (ARI field 5).</param>
/// <param name="ReturnReasonCode">The resolved reason as recorded, or null when none exists (ARI field 6, blank).</param>
/// <param name="ReasonSource">Where the reason came from (<see cref="RfaReasonSources"/>, I-RG7).</param>
/// <param name="ReasonSourceId">The source row's <c>Id</c>; null for <see cref="RfaReasonSources.None"/>.</param>
/// <param name="VerifiedEventId">The EDR's latest event, at 31, read at §5 step 5; the persist requires it still to be the latest (§7.3 step 4).</param>
public sealed record RfaLineRecord(
    int OrderNo,
    long EdrId,
    string EmployeeUniqueId,
    decimal FixedPay,
    decimal VariablePay,
    decimal AmountReturned,
    string? ReturnReasonCode,
    string ReasonSource,
    long? ReasonSourceId,
    long VerifiedEventId);

/// <summary>The values of <c>wps.RfaFileLines.ReasonSource</c> (its CHECK, §7.2).</summary>
public static class RfaReasonSources
{
    public const string PifEdrReturnReasons = "PifEdrReturnReasons";
    public const string CbDeOutcomeRecords = "CbDeOutcomeRecords";
    public const string None = "None";
}

/// <summary>An EDR's resolved return reason (§6.4, A-3): the first of the sources that exists.</summary>
public sealed record RfaReason(string? Code, string Source, long? SourceId)
{
    /// <summary>Neither source has a reason (a substituted or suspense record): a blank field and <c>RFA_REASON_MISSING</c> (E-RG8).</summary>
    public static readonly RfaReason Missing = new(null, RfaReasonSources.None, null);

    /// <summary>
    /// The §6.4 precedence: the EDR's <c>wps.PifEdrReturnReasons</c> row with the highest <c>Id</c>; else its <c>wps.CbDeOutcomeRecords</c> row (at 31,
    /// non-blank <c>ErrorCode</c>, highest <c>Id</c>); else <see cref="Missing"/>.
    /// </summary>
    public static RfaReason Resolve(long? returnReasonId, string? returnReasonCode, long? outcomeRecordId, string? outcomeErrorCode)
    {
        if (returnReasonId is { } rr)
        {
            return new RfaReason(returnReasonCode ?? string.Empty, RfaReasonSources.PifEdrReturnReasons, rr);
        }

        if (outcomeRecordId is { } outcomeId && !string.IsNullOrWhiteSpace(outcomeErrorCode))
        {
            return new RfaReason(outcomeErrorCode, RfaReasonSources.CbDeOutcomeRecords, outcomeId);
        }

        return Missing;
    }
}

/// <summary>An EDR whose latest per-EDR event is <c>RfaVerifiedEdrStatus</c> (31) and that is on no RFA yet (§6.1, A-7).</summary>
/// <param name="VerifiedEventId">The latest event's <c>Id</c> (the 31 event).</param>
public sealed record RfaCandidate(long EdrId, string EmployeeId, decimal FixedPay, decimal VariablePay, long VerifiedEventId, RfaReason Reason);

public static class RfaLines
{
    /// <summary>
    /// The ARI lines of <paramref name="candidates"/>: ordered by <c>EdrId</c> ascending and numbered 1…n (§6.1, I-RG4); each line's amount by
    /// <see cref="RfaAmountRule"/>; the reason as resolved (I-RG7).
    /// </summary>
    public static IReadOnlyList<RfaLineRecord> Compose(IEnumerable<RfaCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .OrderBy(c => c.EdrId)
            .Select((c, i) => new RfaLineRecord(
                i + 1, c.EdrId, c.EmployeeId, c.FixedPay, c.VariablePay, RfaAmountRule.Returned(c.FixedPay, c.VariablePay), c.Reason.Code,
                c.Reason.Source, c.Reason.SourceId, c.VerifiedEventId))
            .ToList();
    }
}
