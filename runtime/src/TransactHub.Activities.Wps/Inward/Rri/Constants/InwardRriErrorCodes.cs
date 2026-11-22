// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rri/Constants/InwardRriErrorCodes.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: The INWARD_RRI_* codes a run of Activity 2 of the RRI flow can end with (design Inward_RRI_Extract_Save v1.0 §8.1; task T2).
// Responsibility: one place for the codes the parser, the service, the repository, the raise step and ExceptionClassificationMap agree on. The
// business codes reuse existing classification pairs; the four technical ones are SYSTEM rows seeded by task T1.

namespace TransactHub.Activities.Wps.Inward.Rri.Constants;

/// <summary>The <c>INWARD_RRI_*</c> error codes (design §8.1).</summary>
public static class InwardRriErrorCodes
{
    /// <summary>A second file with the same tenant and file name under another transaction (business, <c>RRI-Rejected</c>).</summary>
    public const string Duplicate = "INWARD_RRI_DUPLICATE";

    /// <summary>A malformed line, field, encoding, file name, record type or reference; no or two <c>DRC</c>, or no <c>DRR</c> (business).</summary>
    public const string Malformed = "INWARD_RRI_MALFORMED";

    /// <summary>Characters 4–6 of the file name differ from the configured <c>RriFileNameAgentCode</c> (business).</summary>
    public const string FilenameInvalid = "INWARD_RRI_FILENAME_INVALID";

    /// <summary>The <c>DRC</c> count or total disagrees with the <c>DRR</c> lines (business).</summary>
    public const string ReconciliationFailed = "INWARD_RRI_RECONCILIATION_FAILED";

    /// <summary>Size, hash, 0-byte or missing file under an available root, a broken hand-off, or a category other than <c>RRI</c> (technical).</summary>
    public const string IntegrityFailed = "INWARD_RRI_INTEGRITY_FAILED";

    /// <summary>PGP-armoured or binary content (technical).</summary>
    public const string Unreadable = "INWARD_RRI_UNREADABLE";

    /// <summary>A parameter missing or invalid, or no current account-mapping row for the tenant (technical, <c>RRI-Failed</c> on the final attempt).</summary>
    public const string ConfigMissing = "INWARD_RRI_CONFIG_MISSING";

    /// <summary>A technical failure on the final attempt (<c>RRI-Failed</c>).</summary>
    public const string SaveFailed = "INWARD_RRI_SAVE_FAILED";
}
