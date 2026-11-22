// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/PrcFileContent.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: The inputs of one inward PRC file (design v1.0 §6.1).
// Responsibility: carries the unrounded sums, the declared record count, the PIF file name as printed and the one business
// instant shared by the file name and the RCR line (I-G7, A-14). Rounding happens only in PrcFileBuilder (A-4).
namespace TransactHub.Activities.Wps.Inward;

/// <summary>Everything <see cref="PrcFileBuilder"/> needs. Sums are unrounded; the instant is in the business time zone.</summary>
public sealed record PrcFileContent(
    string PifFileName,
    decimal SumFixed,
    decimal SumVariable,
    int DeclaredRecordCount,
    DateTime BusinessInstant);

/// <summary>The PIF file name cannot be printed in a comma-separated PRC line (E-G8).</summary>
public sealed class PrcInvalidPifNameException(string message) : InvalidOperationException(message);
