using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Wps.Native;

/// <summary>
/// <c>uae.wps.enrich.employer-status.v1</c> on the runtime (Issues I-88, I-118, R-12-16): the employer's status at the labour ministry's
/// establishment register, asked through the bank's binding of <c>uae.wps.port.labour-ministry.v1</c> (the image's adapter, through the
/// native contract, R-09-125), written to <c>ext.uae.wps.employerStatus</c> as the script-behind writes it (active, suspended, unknown).
/// An employer the register does not know is <c>not-found</c> and is not refused for it; a register that cannot be reached is a
/// transient error, retried by the step's policy.
/// </summary>
public sealed class EnrichEmployerStatus(INativePorts ports) : INativeActivity
{
    public const string Port = "uae.wps.port.labour-ministry.v1";
    public string Name => "uae.wps.enrich.employer-status.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var employer = NativeContext.S(a.View, "ext.uae.wps.employerId");
        if (employer.Length == 0) throw new NativeFailure("BusinessReject", "The salary file names no employer to ask the register about", false);
        NativePortAnswer answer;
        try { answer = ports.Ask(a, Port, "status", new JsonObject { ["employer"] = employer }); }
        catch (NativePortRefused e) { throw new NativeFailure("BusinessReject", e.Message, false); }
        var status = answer.Body["found"]?.GetValue<bool>() == true ? (string?)answer.Body["status"] ?? "unknown" : "unknown";
        // Only the status is written: the activity declares that one write.
        return new NativeResult(status == "unknown" ? "not-found" : "found", null, new JsonObject { ["ext.uae.wps.employerStatus"] = status });
    }
}
