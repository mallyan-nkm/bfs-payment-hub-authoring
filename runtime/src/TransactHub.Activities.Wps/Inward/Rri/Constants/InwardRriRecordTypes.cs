// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rri/Constants/InwardRriRecordTypes.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: The two record tags of an RRI file (design Inward_RRI_Extract_Save v1.0 §5.3; FSD X-L52).
// Responsibility: the tags the parser compares field 1 with, trimmed and case-insensitively.

namespace TransactHub.Activities.Wps.Inward.Rri.Constants;

/// <summary>The record types of an RRI line.</summary>
public static class InwardRriRecordTypes
{
    /// <summary>A refund line (detail).</summary>
    public const string Detail = "DRR";

    /// <summary>The header line; exactly one per file.</summary>
    public const string Header = "DRC";
}
