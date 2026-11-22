// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rri/InwardRriControlRecord.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: The parsed header line (DRC) of an RRI file (design Inward_RRI_Extract_Save v1.0 §5.3; task T3).
// Responsibility: the stored fields. Fields 2 and 6–10 are not kept.

namespace TransactHub.Activities.Wps.Inward.Rri;

/// <summary>The header line (<c>DRC</c>), stored once on the file header (no UPDATE).</summary>
/// <param name="LineNo">The physical, 1-based line number.</param>
/// <param name="HeaderWpsReference">Field 3, stored as <c>HeaderWpsReference</c> (Q-5).</param>
/// <param name="DeclaredTotalAmount">Field 4: reconciled against the sum of the refund amounts.</param>
/// <param name="DeclaredRecordCount">Field 5: reconciled against the number of refund lines.</param>
public sealed record InwardRriControlRecord(
    int LineNo,
    string HeaderWpsReference,
    decimal DeclaredTotalAmount,
    int DeclaredRecordCount);
