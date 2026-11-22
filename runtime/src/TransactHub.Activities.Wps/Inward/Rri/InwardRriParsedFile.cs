// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rri/InwardRriParsedFile.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: A successfully parsed RRI file (design Inward_RRI_Extract_Save v1.0 §5.3, §5.4; task T3).
// Responsibility: the name-derived references, exactly one DRC and at least one DRR, reconciled; the blank lines skipped.

namespace TransactHub.Activities.Wps.Inward.Rri;

/// <summary>A parsed RRI.</summary>
/// <param name="EstablishmentReference">File name positions 7–19 (Q-4).</param>
/// <param name="WpsFileReference">The last 12 characters of the file name without its extension (Q-5).</param>
/// <param name="Drc">The header line.</param>
/// <param name="Drrs">The refund lines, in file order.</param>
/// <param name="IgnoredLineCount">The blank lines skipped.</param>
/// <param name="IgnoredLineNumbers">Their physical line numbers.</param>
public sealed record InwardRriParsedFile(
    string EstablishmentReference,
    string WpsFileReference,
    InwardRriControlRecord Drc,
    IReadOnlyList<InwardRriDrrLine> Drrs,
    int IgnoredLineCount,
    IReadOnlyList<int> IgnoredLineNumbers)
{
    /// <summary>The number of <c>DRR</c> lines.</summary>
    public int DrrCount => Drrs.Count;
}
