using System.Globalization;
using System.Net;
using System.Text;
using TransactHub.Ports;

namespace TransactHub.Activities.Native;

/// <summary>
/// The image's bound ports as a native module calls them (native contract 2.0.0, R-09-261, Issue I-315): the bank's binding in force of
/// any port, and a request sent to its endpoint with the credential, the circuit breaker and the timeout the image keeps. The image sends
/// on its own copy of the binding in force for the attempt, never on settings a module gives it, so a module cannot name a host or read a
/// credential. It names no port of its own: a scheme's module builds the adapters of the scheme's ports on it.
/// </summary>
public sealed class NativeBoundPorts(PortBindings bindings, TransactHub.Security.ISecretResolver? secrets = null, Func<PortBinding, HttpClient>? http = null, CircuitBreakers? breakers = null) : INativeBoundPorts
{
    readonly TransactHub.Security.ISecretResolver secrets = secrets ?? TransactHub.Security.SecretResolver.FromEnvironment();
    static readonly HttpClient shared = new();

    /// <summary>A binding as the contract gives it to a module.</summary>
    public static NativeBinding ToNative(PortBinding b) => new(b.Tenant, b.Port, b.Adapter, b.ValidFrom, b.Release, b.Settings);

    public NativeBinding For(NativeAttempt a, string port) => ToNative(InForce(a, port));

    PortBinding InForce(NativeAttempt a, string port)
    {
        if (string.IsNullOrEmpty(a.Tenant)) throw new NativeFailure("NoTenant", $"The attempt names no tenant; {port} is bound per tenant and never defaulted", false);
        try { return bindings.InForce(a.Tenant, port, a.Now); }
        catch (PortBindingException e) { throw new NativeFailure("PortNotBound", e.Message, false); }
    }

    public NativeHttpResponse Send(NativeAttempt a, NativeBinding binding, NativeHttpRequest request)
    {
        var b = InForce(a, binding.Port);
        if (b.Adapter != binding.Adapter || b.ValidFrom != binding.ValidFrom || b.Tenant != binding.Tenant)
            throw new NativeFailure("PortNotBound", $"The {binding.Adapter} binding of {binding.Port} from {binding.ValidFrom:O} is not the one in force for {a.Tenant} at {a.Now:O}", false);
        var port = b.Port;
        if (request.Path.Contains("://", StringComparison.Ordinal) || request.Path.StartsWith("//", StringComparison.Ordinal))
            throw new NativePortRefused(port, $"A request on {port} names a path below the binding's baseUrl, never a host ({request.Path})");
        Uri uri;
        try { uri = new Uri(new Uri(b.Required("baseUrl")), request.Path + (string.IsNullOrEmpty(request.Query) ? "" : "?" + request.Query)); }
        catch (PortBindingException e) { throw new NativePortRefused(port, e.Message); }
        catch (UriFormatException e) { throw new NativePortRefused(port, $"The binding of {port} has no valid baseUrl: {e.Message}"); }
        var credentialHeader = b.Setting("credentialHeader", "");
        using var m = new HttpRequestMessage(new HttpMethod(request.Method.ToUpperInvariant()), uri);
        if (request.Body is not null) m.Content = new StringContent(request.Body.ToJsonString(), Encoding.UTF8, "application/json");
        foreach (var (k, v) in request.Headers ?? new Dictionary<string, string>())
        {
            if (k.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || (credentialHeader.Length > 0 && k.Equals(credentialHeader, StringComparison.OrdinalIgnoreCase)))
                throw new NativePortRefused(port, $"The header {k} carries the binding's credential, which the image sets: a module may not");
            if (!m.Headers.TryAddWithoutValidation(k, v)) throw new NativePortRefused(port, $"The header {k} cannot be sent");
        }
        try
        {
            if (b.Setting("secretRef", "") is { Length: > 0 } r)
            {
                var token = secrets.Resolve(r).GetAwaiter().GetResult().Reveal();
                if (credentialHeader.Length > 0) m.Headers.Add(credentialHeader, token);
                else m.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(double.Parse(b.Setting("timeoutSeconds", "10"), CultureInfo.InvariantCulture)));
            using var res = GuardedHttp.Send(http?.Invoke(b) ?? shared, b, m, timeout.Token, default, breakers).GetAwaiter().GetResult();
            if ((int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests)
                throw new NativeFailure("TransientError", $"{port} ({b.Adapter}) answered {(int)res.StatusCode}", true);
            var body = res.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return new NativeHttpResponse((int)res.StatusCode, res.Content.Headers.ContentType?.MediaType, body);
        }
        catch (TransactHub.Security.SecretUnavailable e) { throw new NativeFailure("SecretUnavailable", e.Message, false); }
        catch (CircuitOpenException e) { throw new NativeFailure(CircuitBreakers.OpenCode, e.Message, true); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or TimeoutException)
        { throw new NativeFailure("TransientError", $"{port} ({b.Adapter}) did not answer: {e.GetType().Name}", true); }
    }
}
