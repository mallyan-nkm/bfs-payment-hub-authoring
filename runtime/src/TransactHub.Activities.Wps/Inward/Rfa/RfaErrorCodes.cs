// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rfa/RfaErrorCodes.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: the exception codes of WPS inward Activity 25, RFA generation (design Inward_PAF_RFA_Generation v1.0 §9.1; task T1).
// Responsibility: the six codes and the severity each is raised with. Their (category, type) are exact entries of ExceptionClassificationMap:
// four SYSTEM codes on the TEQ, two BANK_INTERNAL codes on the BEQ. Every code starts INWARD_RFA_GEN_.

namespace TransactHub.Activities.Wps.Inward.Rfa;

public static class RfaErrorCodes
{
    /// <summary>TEQ: the input is incomplete (no transaction id; §5 step 1).</summary>
    public const string InvalidInput = "INWARD_RFA_GEN_INVALID_INPUT";

    /// <summary>TEQ: a parameter or the local directory is missing or invalid (§5 step 2, E-RG10).</summary>
    public const string ConfigInvalid = "INWARD_RFA_GEN_CONFIG_INVALID";

    /// <summary>TEQ: a database or file failure on the last attempt, or the name-clash limit (§5 steps 7, 8; E-RG4). Retry first.</summary>
    public const string Failed = "INWARD_RFA_GEN_FAILED";

    /// <summary>TEQ, raised CRITICAL: other bytes already exist under the RFA name; nothing is overwritten (§5 step 8, E-RG6).</summary>
    public const string FileConflict = "INWARD_RFA_GEN_FILE_CONFLICT";

    /// <summary>BEQ (business): a reason or field the file cannot carry (§5 step 6, §6.3, §6.4, E-RG9; D-A25-06).</summary>
    public const string InvalidContent = "INWARD_RFA_GEN_INVALID_CONTENT";

    /// <summary>BEQ (business): the PIF is not in an entry state, or an included EDR left 31 before the commit (§5 steps 4, 7; E-RG7, E-RG11).</summary>
    public const string StatusConflict = "INWARD_RFA_GEN_STATUS_CONFLICT";

    /// <summary>The four technical (TEQ) codes.</summary>
    public static readonly IReadOnlyList<string> Technical = [InvalidInput, ConfigInvalid, Failed, FileConflict];

    /// <summary>The two business (BEQ) codes.</summary>
    public static readonly IReadOnlyList<string> Business = [InvalidContent, StatusConflict];

    /// <summary>All six.</summary>
    public static readonly IReadOnlyList<string> All = [.. Technical, .. Business];

    /// <summary>The severity to raise with: CRITICAL for a file conflict; null (the map's) otherwise.</summary>
    public static string? SeverityFor(string code) => code == FileConflict ? "CRITICAL" : null;
}
