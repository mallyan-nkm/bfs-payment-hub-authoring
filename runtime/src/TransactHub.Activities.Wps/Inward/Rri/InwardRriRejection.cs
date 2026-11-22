// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rri/InwardRriRejection.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: A classified whole-file rejection of an RRI file (design Inward_RRI_Extract_Save v1.0 §5.2–§5.4, §8.1; task T3).
// Responsibility: the error code and a reason that carries line and field numbers only, never a value (§5.1, I-14).

namespace TransactHub.Activities.Wps.Inward.Rri;

/// <summary>A rejection of the whole file.</summary>
/// <param name="ErrorCode">One of the <c>INWARD_RRI_*</c> codes.</param>
/// <param name="Reason">The rule broken and where; never a field value.</param>
/// <param name="LineNo">The physical line, when the rejection is tied to one.</param>
/// <param name="FieldNumber">The 1-based field, when the rejection is tied to one.</param>
public sealed record InwardRriRejection(string ErrorCode, string Reason, int? LineNo, int? FieldNumber);
