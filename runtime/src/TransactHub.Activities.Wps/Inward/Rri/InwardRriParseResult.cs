// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rri/InwardRriParseResult.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: The outcome of parsing an RRI file (design Inward_RRI_Extract_Save v1.0 §5.2–§5.4; task T3).
// Responsibility: exactly one of a parsed file and a rejection.

namespace TransactHub.Activities.Wps.Inward.Rri;

/// <summary>The outcome of <see cref="InwardRriParser.Parse"/>: exactly one of <see cref="File"/> and <see cref="Rejection"/> is set.</summary>
public sealed class InwardRriParseResult
{
    private InwardRriParseResult(InwardRriParsedFile? file, InwardRriRejection? rejection)
    {
        File = file;
        Rejection = rejection;
    }

    /// <summary>The parsed file, when <see cref="IsSuccess"/>.</summary>
    public InwardRriParsedFile? File { get; }

    /// <summary>The rejection, when not <see cref="IsSuccess"/>.</summary>
    public InwardRriRejection? Rejection { get; }

    /// <summary>True when the file parsed.</summary>
    public bool IsSuccess => File is not null;

    internal static InwardRriParseResult Ok(InwardRriParsedFile file) => new(file, null);

    internal static InwardRriParseResult Reject(InwardRriRejection rejection) => new(null, rejection);
}
