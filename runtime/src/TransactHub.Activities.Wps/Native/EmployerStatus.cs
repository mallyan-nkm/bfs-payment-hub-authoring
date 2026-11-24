using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Wps.Native;

// The employer's status at the labour ministry's establishment register (uae.wps.port.labour-ministry.v1; Issues I-88, I-118, I-237,
// R-12-16, R-09-263). The source read the status (mohreStatus) from its input; here it is asked of the register through the bank's
// binding: `gateway-rest`, a REST service in the gateway's style with its credential by reference, or `simulator`, for tests and the
// example bank. Which service a bank reaches (the ministry's, or the bank's own copy of the register) is the bank's binding. The port is
// WPS's, so its adapters are this module's (native contract 2.0.0): each is built on the binding in force the image gives
// (INativeBoundPorts), and the REST adapter's request is sent by the image with the binding's credential, breaker and timeout.

/// <summary>What the register said: whether it knows the employer, and its status as the register words it, in lower case.</summary>
public sealed record EmployerStatusAnswer(string EmployerId, bool Found, string Status);

/// <summary>An adapter of the labour ministry's register, on one binding, for one attempt.</summary>
public interface IEmployerRegister
{
    string Adapter { get; }
    EmployerStatusAnswer Status(string employerId);
}

public static class EmployerRegisterAdapters
{
    public const string Port = "uae.wps.port.labour-ministry.v1";

    /// <summary>The adapter of the binding in force for the attempt; an adapter this module does not have is refused (<see cref="NativePortRefused"/>).</summary>
    public static IEmployerRegister Create(NativeBinding b, INativeBoundPorts ports, NativeAttempt a) => b.Adapter switch
    {
        SimulatedEmployerRegister.Name => new SimulatedEmployerRegister(b),
        RestEmployerRegister.Name => new RestEmployerRegister(b, ports, a),
        _ => throw new NativePortRefused(Port, $"No employer register adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };

    /// <summary>The register's words for a status, as the factory's extension holds them: active, suspended, or unknown.</summary>
    public static string Normalise(string? status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        "active" or "accepted" or "registered" or "valid" => "active",
        "suspended" or "blocked" => "suspended",
        _ => "unknown",
    };
}

/// <summary>Settings: <c>suspended</c> and <c>notRegistered</c>, comma lists of employer ids; every other employer is active.</summary>
public sealed class SimulatedEmployerRegister(NativeBinding b) : IEmployerRegister
{
    public const string Name = "simulator";
    public string Adapter => Name;

    public EmployerStatusAnswer Status(string employerId) =>
        b.List("notRegistered").Contains(employerId) ? new EmployerStatusAnswer(employerId, false, "unknown")
        : b.List("suspended").Contains(employerId) ? new EmployerStatusAnswer(employerId, true, "suspended")
        : new EmployerStatusAnswer(employerId, true, "active");
}

/// <summary>
/// The register behind a REST service: <c>POST {baseUrl}{statusPath}</c> with <c>{ "employerId": … }</c>; the reply's <c>status</c>
/// is the employer's; a 404 is an employer the register does not know. Headers: <c>X-Channel</c> (setting <c>channel</c>, the bank's:
/// sent only when the binding names one) and <c>X-Ref</c>. The credential (<c>secretRef</c>, as a bearer token or in the header
/// <c>credentialHeader</c> names), the base URL, the breaker and the timeout are the image's, applied when it sends (R-09-261).
/// </summary>
public sealed class RestEmployerRegister(NativeBinding b, INativeBoundPorts ports, NativeAttempt a) : IEmployerRegister
{
    public const string Name = "gateway-rest";
    public string Adapter => Name;

    public EmployerStatusAnswer Status(string employerId)
    {
        var headers = new Dictionary<string, string> { ["X-Ref"] = employerId };
        // The channel the gateway knows the caller by is the bank's binding's (R-09-142).
        if (b.Setting("channel") is { Length: > 0 } channel) headers["X-Channel"] = channel;
        var res = ports.Send(a, b, new NativeHttpRequest("POST", b.Required("statusPath"), new JsonObject { ["employerId"] = employerId }, headers));
        if (res.Status == 404) return new EmployerStatusAnswer(employerId, false, "unknown");
        if (!res.Success) throw new NativeFailure("TransientError", $"The establishment register answered {res.Status}", true);
        var status = EmployerRegisterAdapters.Normalise((string?)res.Json()?["status"]);
        return new EmployerStatusAnswer(employerId, status != "unknown", status);
    }
}

/// <summary>
/// <c>uae.wps.enrich.employer-status.v1</c> on the runtime (Issues I-88, I-118, R-12-16): the employer's status at the labour ministry's
/// establishment register, asked through the bank's binding of <c>uae.wps.port.labour-ministry.v1</c> by this module's own adapter
/// (R-09-263), written to <c>ext.uae.wps.employerStatus</c> as the script-behind writes it (active, suspended, unknown). An employer the
/// register does not know is <c>not-found</c> and is not refused for it; a register that cannot be reached is a transient error, retried
/// by the step's policy; a binding the adapter cannot use is a business rejection.
/// </summary>
public sealed class EnrichEmployerStatus(INativeBoundPorts ports) : INativeActivity
{
    public const string Port = EmployerRegisterAdapters.Port;
    public string Name => "uae.wps.enrich.employer-status.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var employer = NativeContext.S(a.View, "ext.uae.wps.employerId");
        if (employer.Length == 0) throw new NativeFailure("BusinessReject", "The salary file names no employer to ask the register about", false);
        EmployerStatusAnswer answer;
        try { answer = EmployerRegisterAdapters.Create(ports.For(a, Port), ports, a).Status(employer); }
        catch (NativePortRefused e) { throw new NativeFailure("BusinessReject", e.Message, false); }
        catch (System.Text.Json.JsonException e) { throw new NativeFailure("TransientError", $"The establishment register's reply is not JSON: {e.Message}", true); }
        var status = answer.Found ? answer.Status : "unknown";
        // Only the status is written: the activity declares that one write.
        return new NativeResult(status == "unknown" ? "not-found" : "found", null, new JsonObject { ["ext.uae.wps.employerStatus"] = status });
    }
}
