using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Ports;

// The employer's status at the labour ministry's establishment register (uae.wps.port.labour-ministry.v1; Issues I-88, I-118,
// R-12-16). The source read the status (mohreStatus) from its input; here it is asked of the register through the bank's binding:
// `gateway-rest`, a REST service in the gateway's style with its credential by reference, or `simulator`, for tests and the example
// bank. Which service a bank reaches (the ministry's, or the bank's own copy of the register) is the bank's binding.

/// <summary>What the register said: whether it knows the employer, and its status as the register words it, in lower case.</summary>
public sealed record EmployerStatusAnswer(string EmployerId, bool Found, string Status);

public interface IEmployerRegister
{
    string Adapter { get; }
    Task<EmployerStatusAnswer> Status(string employerId, CancellationToken ct = default);
}

public static class EmployerRegisterAdapters
{
    public const string Port = "uae.wps.port.labour-ministry.v1";

    public static IEmployerRegister Create(PortBinding b, HttpClient? http = null, ISecretResolver? secrets = null) => b.Adapter switch
    {
        SimulatedEmployerRegister.Name => new SimulatedEmployerRegister(b),
        RestEmployerRegister.Name => new RestEmployerRegister(b, http ?? new HttpClient(), secrets),
        _ => throw new PortBindingException($"No employer register adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
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
public sealed class SimulatedEmployerRegister(PortBinding b) : IEmployerRegister
{
    public const string Name = "simulator";
    public string Adapter => Name;
    HashSet<string> List(string key) => b.Setting(key, "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    public Task<EmployerStatusAnswer> Status(string employerId, CancellationToken ct = default) => Task.FromResult(
        List("notRegistered").Contains(employerId) ? new EmployerStatusAnswer(employerId, false, "unknown")
        : List("suspended").Contains(employerId) ? new EmployerStatusAnswer(employerId, true, "suspended")
        : new EmployerStatusAnswer(employerId, true, "active"));
}

/// <summary>
/// The register behind a REST service: <c>POST {baseUrl}{statusPath}</c> with <c>{ "employerId": … }</c>; the reply's <c>status</c>
/// is the employer's; a 404 is an employer the register does not know. Headers: <c>X-Channel</c> (setting <c>channel</c>, the bank's: sent only
/// when the binding names one) and <c>X-Ref</c>; the credential by <c>secretRef</c> as a bearer token, or in the header <c>credentialHeader</c> names.
/// </summary>
public sealed class RestEmployerRegister(PortBinding b, HttpClient http, ISecretResolver? secrets = null) : IEmployerRegister
{
    public const string Name = "gateway-rest";
    public string Adapter => Name;
    readonly ISecretResolver secrets = secrets ?? SecretResolver.FromEnvironment();

    public async Task<EmployerStatusAnswer> Status(string employerId, CancellationToken ct = default)
    {
        using var m = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(b.Required("baseUrl")), b.Required("statusPath")))
        {
            Content = JsonContent.Create(new JsonObject { ["employerId"] = employerId }),
        };
        // The channel the gateway knows the caller by is the bank's binding's (R-09-142): platform code names no scheme.
        if (b.Setting("channel", "") is { Length: > 0 } channel) m.Headers.Add("X-Channel", channel);
        m.Headers.Add("X-Ref", employerId);
        if (b.Setting("secretRef", "") is { Length: > 0 } r)
        {
            var token = (await secrets.Resolve(r, ct)).Reveal();
            if (b.Setting("credentialHeader", "") is { Length: > 0 } h) m.Headers.Add(h, token);
            else m.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        using var res = await GuardedHttp.Send(http, b, m, ct, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return new EmployerStatusAnswer(employerId, false, "unknown");
        res.EnsureSuccessStatusCode();
        var j = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        var status = EmployerRegisterAdapters.Normalise((string?)j?["status"]);
        return new EmployerStatusAnswer(employerId, status != "unknown", status);
    }
}
