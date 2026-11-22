using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TransactHub.Security;

/// <summary>
/// A secret's value. It never prints itself: <see cref="ToString"/> is a mark, so a value that reaches a log line,
/// an exception message or a serialiser by mistake shows nothing. Only <see cref="Reveal"/> gives the text.
/// </summary>
public sealed class SecretValue(string value)
{
    readonly string value = value;
    public string Reveal() => value;
    public override string ToString() => "[secret]";
}

/// <summary>
/// A reference to a secret, as a binding or the installation's settings write it (R-06-22): <c>env:NAME</c>,
/// <c>file:/absolute/path</c> or <c>vault:mount/path#field</c>. A reference is safe to log; its value is not.
/// </summary>
public sealed record SecretRef(string Scheme, string Locator)
{
    public static readonly Regex Grammar = new(@"^(env|file|vault|akv):(\S+)$", RegexOptions.CultureInvariant);

    public static bool IsRef(string? s) => s is not null && Grammar.IsMatch(s);

    public static SecretRef Parse(string reference)
    {
        var m = Grammar.Match(reference ?? "");
        if (!m.Success) throw new SecretUnavailable(Describe(reference), "is not a secret reference: write env:NAME, file:/path or vault:mount/path#field");
        return new SecretRef(m.Groups[1].Value, m.Groups[2].Value);
    }

    /// <summary>A reference as it may be shown: the reference itself when it is one, else only its length (it may be a value).</summary>
    public static string Describe(string? reference) => IsRef(reference) ? reference! : $"(a value of {reference?.Length ?? 0} characters that is not a reference)";

    public override string ToString() => $"{Scheme}:{Locator}";
}

/// <summary>A secret could not be read. The message names the reference and the reason, never a value.</summary>
public sealed class SecretUnavailable(string reference, string reason) : Exception($"Secret {reference} {reason}")
{
    public string Reference { get; } = reference;
}

/// <summary>One store of secrets, by the scheme of its references.</summary>
public interface ISecretProvider
{
    string Scheme { get; }
    Task<SecretValue> Resolve(SecretRef reference, CancellationToken ct = default);
}

/// <summary>The one way the runtime reads a secret: every host, adapter and check resolves a reference here.</summary>
public interface ISecretResolver
{
    Task<SecretValue> Resolve(string reference, CancellationToken ct = default);
}

public sealed class SecretResolver(IEnumerable<ISecretProvider> providers) : ISecretResolver
{
    readonly Dictionary<string, ISecretProvider> providers = providers.ToDictionary(p => p.Scheme, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Schemes => providers.Keys;

    public async Task<SecretValue> Resolve(string reference, CancellationToken ct = default)
    {
        var r = SecretRef.Parse(reference);
        if (!providers.TryGetValue(r.Scheme, out var p))
            throw new SecretUnavailable(r.ToString(), r.Scheme == "akv"
                ? "names Azure Key Vault, whose provider is not built (Issue I-73): use env:, file: or vault:"
                : $"names the {r.Scheme} store, which this installation has not configured");
        return await p.Resolve(r, ct);
    }

    /// <summary>
    /// The providers an installation has from its environment: <c>env:</c> and <c>file:</c> always, and <c>vault:</c>
    /// when <c>TH_VAULT_ADDR</c> is set (its token by reference in <c>TH_VAULT_TOKEN</c>, env: or file: only).
    /// <c>TH_SECRET_FILE_ROOTS</c> (paths separated by ':') limits where file: may read.
    /// </summary>
    public static SecretResolver FromEnvironment(Func<string, string?>? env = null, HttpClient? http = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var envProvider = new EnvSecretProvider(env);
        var roots = (env("TH_SECRET_FILE_ROOTS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fileProvider = new FileSecretProvider(roots);
        var list = new List<ISecretProvider> { envProvider, fileProvider };
        if (env("TH_VAULT_ADDR") is { Length: > 0 } addr)
        {
            var tokenRef = env("TH_VAULT_TOKEN") ?? throw new SecretUnavailable("TH_VAULT_TOKEN", "is not set: TH_VAULT_ADDR names a Vault, and its token is read by reference (env:NAME or file:/path)");
            var bootstrap = new SecretResolver([envProvider, fileProvider]);
            list.Add(new VaultSecretProvider(new Uri(addr), ct => bootstrap.Resolve(tokenRef, ct), http ?? new HttpClient(), env("TH_VAULT_NAMESPACE")));
        }
        return new SecretResolver(list);
    }
}

/// <summary><c>env:NAME</c>: a variable the deployment sets.</summary>
public sealed class EnvSecretProvider(Func<string, string?>? env = null) : ISecretProvider
{
    readonly Func<string, string?> env = env ?? Environment.GetEnvironmentVariable;
    public string Scheme => "env";

    public Task<SecretValue> Resolve(SecretRef r, CancellationToken ct = default) =>
        env(r.Locator) is { Length: > 0 } v ? Task.FromResult(new SecretValue(v)) : throw new SecretUnavailable(r.ToString(), "is not set");
}

/// <summary>
/// <c>file:/absolute/path</c>: a mounted secret file (a Kubernetes or Docker secret, a Vault agent's sink). The path is
/// absolute, has no '..', and lies under one of the allowed roots when roots are given. One trailing newline is dropped.
/// </summary>
public sealed class FileSecretProvider(IReadOnlyCollection<string>? roots = null) : ISecretProvider
{
    public string Scheme => "file";

    public async Task<SecretValue> Resolve(SecretRef r, CancellationToken ct = default)
    {
        var p = r.Locator;
        if (!Path.IsPathRooted(p) || p.Split('/', '\\').Contains("..")) throw new SecretUnavailable(r.ToString(), "is not an absolute path without '..'");
        var full = Path.GetFullPath(p);
        if (roots is { Count: > 0 } && !roots.Any(root => full.StartsWith(Path.GetFullPath(root).TrimEnd('/') + "/", StringComparison.Ordinal)))
            throw new SecretUnavailable(r.ToString(), "is outside the folders TH_SECRET_FILE_ROOTS allows");
        string text;
        try { text = await File.ReadAllTextAsync(full, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new SecretUnavailable(r.ToString(), $"cannot be read ({e.GetType().Name})"); }
        if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
        else if (text.EndsWith('\n')) text = text[..^1];
        if (text.Length == 0) throw new SecretUnavailable(r.ToString(), "is empty");
        return new SecretValue(text);
    }
}

/// <summary>
/// <c>vault:mount/path#field</c>: a field of a HashiCorp Vault KV version 2 secret, read over Vault's HTTP API with the
/// installation's token. The answer's body is never put in an exception.
/// </summary>
public sealed class VaultSecretProvider(Uri address, Func<CancellationToken, Task<SecretValue>> token, HttpClient http, string? ns = null) : ISecretProvider
{
    public string Scheme => "vault";

    public async Task<SecretValue> Resolve(SecretRef r, CancellationToken ct = default)
    {
        var hash = r.Locator.IndexOf('#');
        var slash = r.Locator.IndexOf('/');
        if (hash <= 0 || slash <= 0 || slash > hash || hash == r.Locator.Length - 1)
            throw new SecretUnavailable(r.ToString(), "is not mount/path#field");
        var mount = r.Locator[..slash];
        var path = r.Locator[(slash + 1)..hash];
        var field = r.Locator[(hash + 1)..];
        if (path.Length == 0 || path.Split('/').Contains("..")) throw new SecretUnavailable(r.ToString(), "has no path, or a '..' in it");
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(address, $"v1/{Uri.EscapeDataString(mount)}/data/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}"));
        req.Headers.Add("X-Vault-Token", (await token(ct)).Reveal());
        if (ns is { Length: > 0 }) req.Headers.Add("X-Vault-Namespace", ns);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new SecretUnavailable(r.ToString(), $"could not be read: Vault did not answer ({e.HttpRequestError})"); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw new SecretUnavailable(r.ToString(), $"could not be read: Vault answered {(int)res.StatusCode}");
            JsonObject? body;
            try { body = await res.Content.ReadFromJsonAsync<JsonObject>(ct); }
            catch (System.Text.Json.JsonException) { throw new SecretUnavailable(r.ToString(), "could not be read: Vault's answer is not JSON"); }
            var v = body?["data"]?["data"]?[field];
            if (v is not JsonValue jv || !jv.TryGetValue<string>(out var s) || s.Length == 0) throw new SecretUnavailable(r.ToString(), $"has no field {field}");
            return new SecretValue(s);
        }
    }
}
