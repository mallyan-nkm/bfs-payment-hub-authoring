// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Dif/DifErrorCodes.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: the exception codes of WPS inward Activity 20, DIF generation (design Inward_PAF_DIF_Generation v1.0 §9.1 as restated by v1.1 §9.1;
// tasks T2, V2).
// Responsibility: the seven codes and the severity each is raised with. Their (category, type) are exact entries of ExceptionClassificationMap:
// four SYSTEM codes on the TEQ, three BANK_INTERNAL codes on the BEQ. Every code starts INWARD_DIF_ (v1.1 §9.2).

namespace TransactHub.Activities.Wps.Inward.Dif;

public static class DifErrorCodes
{
    public const string ConfigInvalid = "INWARD_DIF_CONFIG_INVALID";
    public const string SourceMissing = "INWARD_DIF_SOURCE_MISSING";
    public const string GenerationFailed = "INWARD_DIF_GENERATION_FAILED";
    public const string FileConflict = "INWARD_DIF_FILE_CONFLICT";

    /// <summary>BEQ (business): no header, or the latest state is not CB-Posting-Completed.</summary>
    public const string NotEligible = "INWARD_DIF_NOT_ELIGIBLE";

    /// <summary>BEQ (business): a written text field is blank, non-ASCII or has a comma, CR or LF.</summary>
    public const string InvalidContent = "INWARD_DIF_INVALID_CONTENT";

    /// <summary>BEQ (business): the PIF state or an included EDR changed before the commit.</summary>
    public const string StatusChanged = "INWARD_DIF_STATUS_CHANGED";

    /// <summary>The four technical (TEQ) codes.</summary>
    public static readonly IReadOnlyList<string> Technical = [ConfigInvalid, SourceMissing, GenerationFailed, FileConflict];

    /// <summary>The three business (BEQ) codes.</summary>
    public static readonly IReadOnlyList<string> Business = [NotEligible, InvalidContent, StatusChanged];

    /// <summary>All seven.</summary>
    public static readonly IReadOnlyList<string> All = [.. Technical, .. Business];

    /// <summary>The severity to raise with: CRITICAL for a file conflict; null (the map's) otherwise.</summary>
    public static string? SeverityFor(string code) => code == FileConflict ? "CRITICAL" : null;
}
