// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Dif/DifLine.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: the immutable records of a DIF of WPS inward Activity 20, DIF generation (design Inward_PAF_DIF_Generation v1.0 §6.2, §6.3, §7.1, §7.2;
// task T3).
// Responsibility: the shape of one wps.DifFiles row and its wps.DifFileLines rows, as persisted and as read back (the builder takes only these,
// I-DF8), and the pure composition of the lines from the candidate EDRs: order numbers 1…n by EdrId (§6.3), the amount rule per line (§6.2) and the
// transaction date as the business date of the EDR's latest 24 event (A-5).

namespace TransactHub.Activities.Wps.Inward.Dif;

/// <summary>One <c>wps.DifFiles</c> row. <see cref="Id"/> is 0 before it is persisted.</summary>
/// <param name="FileCreatedAtBusiness">The generation instant as wall-clock time in <paramref name="BusinessTimeZone"/> (DCR fields 3–4).</param>
/// <param name="GeneratedAtUtc">The same instant in UTC (<c>difGeneratedAtUtc</c>).</param>
public sealed record DifFileRecord(
    long Id,
    string TenantId,
    string TransactionId,
    long InwardPifFileId,
    long PrcFileId,
    int GenerationNo,
    string DifFileName,
    string WpsFileId,
    string AgentId,
    int LineCount,
    int NotIncludedEdrCount,
    decimal TotalFixedPay,
    decimal TotalVariablePay,
    decimal TotalRecovery,
    decimal TotalWithdrawalAmount,
    DateTime FileCreatedAtBusiness,
    string BusinessTimeZone,
    DateTime GeneratedAtUtc,
    string LayoutVersion,
    string ContentSha256,
    long SizeBytes,
    string WorkflowId,
    string CreatedBy);

/// <summary>One <c>wps.DifFileLines</c> row: one DIR line, <see cref="OrderNo"/> 1… in <see cref="EdrId"/> order.</summary>
public sealed record DifLineRecord(
    int OrderNo,
    long EdrId,
    string EmployeeId,
    string BeneficiaryAccount,
    DateOnly TransactionDate,
    long PostedEventId,
    decimal FixedPay,
    decimal VariablePay,
    decimal RecoveryAmount,
    decimal FixedDisbursed,
    decimal VariableDisbursed,
    decimal WithdrawalAmount);

/// <summary>An EDR whose latest per-EDR code is the posted code (24), with that latest event (design §4.2, A-6).</summary>
/// <param name="BeneficiaryAccount">NULL in the PIF is possible; the content check refuses it (E-DF11).</param>
/// <param name="PostedEventId">The latest event's <c>Id</c> (the 24 event).</param>
/// <param name="PostedAtUtc">The latest event's <c>OccurredAt</c> (UTC).</param>
public sealed record DifCandidate(
    long EdrId, string EmployeeId, string? BeneficiaryAccount, decimal FixedPay, decimal VariablePay, long PostedEventId, DateTime PostedAtUtc);

public static class DifLines
{
    /// <summary>
    /// The DIR lines of <paramref name="candidates"/>: ordered by <c>EdrId</c> ascending and numbered 1…n (§6.3); each line's amounts by
    /// <see cref="DifAmountRule"/> with the recovery of <paramref name="recoveries"/> (0 when absent); the date is the business date of the
    /// posted event (A-5). A <c>0.00</c> line is kept (A-7). The account must already have passed the content check.
    /// </summary>
    public static IReadOnlyList<DifLineRecord> Compose(
        IEnumerable<DifCandidate> candidates, IReadOnlyDictionary<long, decimal> recoveries, string businessTimeZoneId)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(recoveries);
        return candidates
            .OrderBy(c => c.EdrId)
            .Select((c, i) =>
            {
                var recovery = recoveries.TryGetValue(c.EdrId, out var r) ? r : 0m;
                var amounts = DifAmountRule.Apply(c.FixedPay, c.VariablePay, recovery);
                var date = ExchangeRateBusinessClock.BusinessDate(
                    new DateTimeOffset(DateTime.SpecifyKind(c.PostedAtUtc, DateTimeKind.Utc)), businessTimeZoneId).Today;
                return new DifLineRecord(
                    i + 1, c.EdrId, c.EmployeeId, c.BeneficiaryAccount ?? string.Empty, date, c.PostedEventId, c.FixedPay, c.VariablePay, recovery,
                    amounts.FixedDisbursed, amounts.VariableDisbursed, amounts.WithdrawalAmount);
            })
            .ToList();
    }
}
