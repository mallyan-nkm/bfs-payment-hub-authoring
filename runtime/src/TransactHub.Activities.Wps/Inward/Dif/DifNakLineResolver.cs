// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Dif/DifNakLineResolver.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: the DER resolution of WPS inward Activity 24, DIF NAK extraction (design Inward_PAF_DIF_Nak_Extraction v1.0 §6, A-3, E-DN1–E-DN4; task T2).
// Responsibility: a pure function from the DERs PrcNakFileReader read off a DIF NAK (file order, trimmed) and the DIF's OrderNo → EdrId map to the
// reason of every EDR (the codes of its DERs joined with ';' in file order, cut to 500 characters, or NULL), the line matches, the unmatched DERs
// and the warnings. A DER that names no DIR line is a file-level error, never a failure (A-3). No I/O, no clock, no database.
using TransactHub.Activities.Wps.Rules;

namespace TransactHub.Activities.Wps.Inward.Dif;

/// <summary>The warning codes the resolver adds to the reader's (design §6); <see cref="NoDerRecord"/> is the reader's own.</summary>
public static class DifNakWarnings
{
    public const string NoDerRecord = PrcNakWarnings.NoDerRecord;
    public const string DerLineUnmatched = "DER_LINE_UNMATCHED";
    public const string DerReasonTruncated = "DER_REASON_TRUNCATED";
    public const string DerWithoutErrorNumber = "DER_WITHOUT_ERROR_NUMBER";
}

/// <summary>One DER matched to a DIR line: the <c>lineMatches</c> element of the verdict's <c>FileContent</c> (design §6).</summary>
public sealed record DifNakLineMatch(int LineNumber, int OrderNo, long EdrId, string ErrorNumber, string ErrorDescription);

/// <summary>The result of <see cref="DifNakLineResolver.Resolve"/> (design §6).</summary>
/// <param name="Reasons">Every EDR of the DIF, with its reason, or null when no DER names it with an error number (A-2, E-DN2).</param>
/// <param name="Matches">The matched DERs, in file order.</param>
/// <param name="Unmatched">The DERs that name no DIR line, in file order: file-level errors (E-DN3).</param>
/// <param name="Warnings">The reader's warnings followed by the resolver's, each once; none of them stops the step (A-3).</param>
public sealed record DifNakResolution(
    IReadOnlyDictionary<long, string?> Reasons,
    IReadOnlyList<DifNakLineMatch> Matches,
    IReadOnlyList<WpsAckDetailError> Unmatched,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The EDRs that carry a bank error code (<c>difNakRejectedEdrCount</c>).</summary>
    public int RejectedEdrCount => Reasons.Values.Count(r => r is not null);

    /// <summary>The DERs that matched no DIR line (<c>difNakUnmatchedDerCount</c>).</summary>
    public int UnmatchedDerCount => Unmatched.Count;
}

/// <summary>Resolves the DERs of a DIF NAK to the DIF's EDRs through <c>wps.DifFileLines.OrderNo</c> (design §6).</summary>
public static class DifNakLineResolver
{
    /// <summary>The <c>Reason</c> of a per-EDR status row is limited to this many characters (design §6).</summary>
    public const int ReasonMaxLength = 500;

    private const string ReasonSeparator = ";";

    /// <summary>
    /// A DER whose <c>LineNumber</c> is an <c>OrderNo</c> of the DIF (1…n) is matched to that EDR and its trimmed error number joins the EDR's reason
    /// in file order; an empty error number is matched but adds nothing (<see cref="DifNakWarnings.DerWithoutErrorNumber"/>). A <c>LineNumber</c> of
    /// 0 (not numeric), n+1 (the DCR line) or beyond is unmatched (<see cref="DifNakWarnings.DerLineUnmatched"/>). A reason over
    /// <see cref="ReasonMaxLength"/> is cut (<see cref="DifNakWarnings.DerReasonTruncated"/>). No DER leaves every reason null with
    /// <see cref="DifNakWarnings.NoDerRecord"/>. Never fails on content (A-3).
    /// </summary>
    /// <param name="rejections">The reader's DERs (<see cref="PrcNakExtract.Rejections"/>), in file order.</param>
    /// <param name="edrIdByOrderNo">The DIF's lines: <c>OrderNo</c> 1…n to <c>EdrId</c>.</param>
    /// <param name="readerWarnings">The reader's warnings (<see cref="PrcNakExtract.Warnings"/>), kept first.</param>
    public static DifNakResolution Resolve(
        IReadOnlyList<WpsAckDetailError> rejections, IReadOnlyDictionary<int, long> edrIdByOrderNo, IEnumerable<string> readerWarnings)
    {
        ArgumentNullException.ThrowIfNull(rejections);
        ArgumentNullException.ThrowIfNull(edrIdByOrderNo);
        ArgumentNullException.ThrowIfNull(readerWarnings);

        var warnings = new List<string>();
        foreach (var warning in readerWarnings)
        {
            AddOnce(warnings, warning);
        }

        if (rejections.Count == 0)
        {
            AddOnce(warnings, DifNakWarnings.NoDerRecord);
        }

        var codes = edrIdByOrderNo.Values.ToDictionary(edrId => edrId, _ => new List<string>());
        var matches = new List<DifNakLineMatch>();
        var unmatched = new List<WpsAckDetailError>();

        foreach (var rejection in rejections)
        {
            if (!edrIdByOrderNo.TryGetValue(rejection.LineNumber, out var edrId))
            {
                unmatched.Add(rejection);
                AddOnce(warnings, DifNakWarnings.DerLineUnmatched);
                continue;
            }

            var errorNumber = rejection.ErrorNumber.Trim();
            matches.Add(new DifNakLineMatch(rejection.LineNumber, rejection.LineNumber, edrId, errorNumber, rejection.ErrorDescription));
            if (errorNumber.Length == 0)
            {
                AddOnce(warnings, DifNakWarnings.DerWithoutErrorNumber);
                continue;
            }

            codes[edrId].Add(errorNumber);
        }

        var reasons = new Dictionary<long, string?>(codes.Count);
        foreach (var (edrId, edrCodes) in codes)
        {
            reasons[edrId] = edrCodes.Count == 0 ? null : Reason(edrCodes, warnings);
        }

        return new DifNakResolution(reasons, matches, unmatched, warnings);
    }

    private static string Reason(List<string> codes, List<string> warnings)
    {
        var reason = string.Join(ReasonSeparator, codes);
        if (reason.Length <= ReasonMaxLength)
        {
            return reason;
        }

        AddOnce(warnings, DifNakWarnings.DerReasonTruncated);
        return reason[..ReasonMaxLength];
    }

    private static void AddOnce(List<string> warnings, string warning)
    {
        if (!warnings.Contains(warning))
        {
            warnings.Add(warning);
        }
    }
}
