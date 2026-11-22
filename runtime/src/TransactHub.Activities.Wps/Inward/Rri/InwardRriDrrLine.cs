// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rri/InwardRriDrrLine.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: One parsed refund line (DRR) of an RRI file (design Inward_RRI_Extract_Save v1.0 §5.3; task T3).
// Responsibility: the stored fields, as received (not trimmed). Fields 2, 9 and 10 are not kept.

namespace TransactHub.Activities.Wps.Inward.Rri;

/// <summary>A refund line (<c>DRR</c>).</summary>
/// <param name="LineNo">The physical, 1-based line number.</param>
/// <param name="PersonId">Field 3: the person ID the payroll link matches on.</param>
/// <param name="RefundAmount">Field 4: greater than zero, at most two decimal places.</param>
/// <param name="ReasonCode">Field 5: its domain is not checked (OD-3).</param>
/// <param name="PayrollFileName">Field 6: the payroll file the line refunds.</param>
/// <param name="PayrollWpsFileReference">Field 7: kept with the line.</param>
/// <param name="AccountNoRaw">Field 8: the account as read.</param>
public sealed record InwardRriDrrLine(
    int LineNo,
    string PersonId,
    decimal RefundAmount,
    string ReasonCode,
    string PayrollFileName,
    string PayrollWpsFileReference,
    string AccountNoRaw);
