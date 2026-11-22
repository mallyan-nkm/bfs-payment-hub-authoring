using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace TransactHub.Security;

// The cloud secret stores (spec R-06-26), on their official SDKs. No message carries a value or a store's own text:
// each names the reference and the store's status or error code only.

/// <summary>
/// <c>akv:&lt;vault-name&gt;/&lt;secret-name&gt;[#version]</c>: a secret of Azure Key Vault, at
/// <c>https://&lt;vault-name&gt;.&lt;TH_AKV_DNS_SUFFIX, vault.azure.net&gt;/</c>, the latest version unless one is named.
/// The credential is made once, when first needed (<see cref="CredentialFrom"/>).
/// </summary>
public sealed class AzureKeyVaultSecretProvider(Func<CancellationToken, Task<TokenCredential>> credential, string? dnsSuffix = null, HttpPipelineTransport? transport = null) : ISecretProvider
{
    static readonly Regex Locator = new(@"^([A-Za-z][A-Za-z0-9-]{1,22}[A-Za-z0-9])/([A-Za-z0-9-]{1,127})(?:#([0-9A-Fa-f]{32}))?$", RegexOptions.CultureInvariant);
    readonly ConcurrentDictionary<string, SecretClient> clients = new(StringComparer.Ordinal);
    readonly SemaphoreSlim gate = new(1, 1);
    TokenCredential? made;

    public string Scheme => "akv";

    async Task<TokenCredential> Credential(SecretRef r, CancellationToken ct)
    {
        if (made is not null) return made;
        await gate.WaitAsync(ct);
        try { return made ??= await credential(ct); }
        catch (SecretUnavailable e) { throw new SecretUnavailable(r.ToString(), $"could not be read: {e.Reference} {e.Reason}"); }
        finally { gate.Release(); }
    }

    public async Task<SecretValue> Resolve(SecretRef r, CancellationToken ct = default)
    {
        var m = Locator.Match(r.Locator);
        if (!m.Success) throw new SecretUnavailable(r.ToString(), "is not akv:<vault-name>/<secret-name>[#version] (a vault name of 3 to 24 letters, digits and hyphens; a version of 32 hex digits)");
        var host = $"{m.Groups[1].Value.ToLowerInvariant()}.{(string.IsNullOrEmpty(dnsSuffix) ? "vault.azure.net" : dnsSuffix)}";
        var cred = await Credential(r, ct);
        var client = clients.GetOrAdd(host, h =>
        {
            var o = new SecretClientOptions();
            o.Retry.MaxRetries = 2;
            if (transport is not null) o.Transport = transport;
            return new SecretClient(new Uri($"https://{h}/"), cred, o);
        });
        string? value;
        try { value = (await client.GetSecretAsync(m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : null, ct)).Value.Value; }
        catch (RequestFailedException e) when (e.Status == 404) { throw new SecretUnavailable(r.ToString(), $"does not exist: Key Vault answered 404{Code(e)}"); }
        catch (RequestFailedException e) when (e.Status is 401 or 403) { throw new SecretUnavailable(r.ToString(), $"could not be read: Key Vault refused access ({e.Status}{Code(e)})"); }
        catch (RequestFailedException e) when (e.Status != 0) { throw new SecretUnavailable(r.ToString(), $"could not be read: Key Vault answered {e.Status}"); }
        catch (AuthenticationFailedException) { throw new SecretUnavailable(r.ToString(), "could not be read: no Azure credential could get a token (client credentials, workload identity or managed identity)"); }
        catch (Exception e) when (e is RequestFailedException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { throw new SecretUnavailable(r.ToString(), "could not be read: Key Vault did not answer"); }
        if (string.IsNullOrEmpty(value)) throw new SecretUnavailable(r.ToString(), "is empty");
        return new SecretValue(value);
    }

    static string Code(RequestFailedException e) => e.ErrorCode is { Length: > 0 } c ? $" {c}" : "";

    /// <summary>
    /// The credential an installation signs in with, the first configured in DefaultAzureCredential's order: client
    /// credentials (<c>AZURE_TENANT_ID</c>, <c>AZURE_CLIENT_ID</c> and <c>TH_AKV_CLIENT_SECRET</c>, a reference to env: or
    /// file:, never the secret itself), workload identity (<c>AZURE_FEDERATED_TOKEN_FILE</c>), then managed identity
    /// (user-assigned by <c>AZURE_CLIENT_ID</c>, else system-assigned). <c>AZURE_CLIENT_SECRET</c> is refused: it would
    /// hold the client secret itself. <c>AZURE_AUTHORITY_HOST</c> names another cloud's authority.
    /// </summary>
    public static async Task<TokenCredential> CredentialFrom(Func<string, string?> env, ISecretResolver bootstrap, HttpPipelineTransport? transport = null, CancellationToken ct = default)
    {
        Uri? authority = env("AZURE_AUTHORITY_HOST") is { Length: > 0 } a ? new Uri(a) : null;
        T With<T>(T o) where T : TokenCredentialOptions
        {
            if (authority is not null) o.AuthorityHost = authority;
            if (transport is not null) o.Transport = transport;
            return o;
        }
        string? tenant = env("AZURE_TENANT_ID"), client = env("AZURE_CLIENT_ID");
        if (env("TH_AKV_CLIENT_SECRET") is { Length: > 0 } secretRef)
        {
            if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(client)) throw new SecretUnavailable("TH_AKV_CLIENT_SECRET", "is set, and AZURE_TENANT_ID and AZURE_CLIENT_ID must be too");
            if (!SecretRef.IsRef(secretRef) || SecretRef.Parse(secretRef).Scheme is not ("env" or "file"))
                throw new SecretUnavailable("TH_AKV_CLIENT_SECRET", $"must be a reference to env: or file:, not {SecretRef.Describe(secretRef)}");
            var secret = await bootstrap.Resolve(secretRef, ct);
            return new ClientSecretCredential(tenant, client, secret.Reveal(), With(new ClientSecretCredentialOptions()));
        }
        if (!string.IsNullOrEmpty(env("AZURE_CLIENT_SECRET")))
            throw new SecretUnavailable("AZURE_CLIENT_SECRET", "holds a client secret itself, which is not read: set TH_AKV_CLIENT_SECRET to a reference to it (env: or file:)");
        if (env("AZURE_FEDERATED_TOKEN_FILE") is { Length: > 0 } tokenFile)
            return new WorkloadIdentityCredential(With(new WorkloadIdentityCredentialOptions { TenantId = tenant, ClientId = client, TokenFilePath = tokenFile }));
        var id = string.IsNullOrEmpty(client) ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(client);
        return new ManagedIdentityCredential(With(new ManagedIdentityCredentialOptions(id)));
    }
}

static class AwsLocator
{
    public const string Region = @"[a-z]{2}(?:-[a-z]+)+-\d{1,2}";

    public static string Reason(string service, Exception e) => e switch
    {
        AmazonServiceException s when (int)s.StatusCode > 0 => $"could not be read: {service} answered {(int)s.StatusCode}{(s.ErrorCode is { Length: > 0 } c ? $" {c}" : "")}",
        AmazonClientException c when c.Message.Contains("credential", StringComparison.OrdinalIgnoreCase) => "could not be read: no AWS credentials were found (the default chain: environment, web identity, the container or instance role)",
        _ => $"could not be read: {service} did not answer",
    };

    public static bool IsAws(Exception e) => e is AmazonClientException or HttpRequestException or TaskCanceledException;
}

/// <summary>
/// <c>aws-sm:&lt;region&gt;/&lt;secret-id&gt;[#json-field]</c>: an AWS Secrets Manager secret's string, or one field of it
/// when it is a JSON object. The secret id is its name or ARN. The client signs in with the AWS default credential chain
/// (environment, web identity for IRSA, the container or instance role) unless <paramref name="client"/> is given.
/// </summary>
public sealed class AwsSecretsManagerSecretProvider(Func<string, IAmazonSecretsManager>? client = null) : ISecretProvider
{
    static readonly Regex Locator = new($@"^({AwsLocator.Region})/([A-Za-z0-9/_+=.@:!-]{{1,2048}}?)(?:#([^#]+))?$", RegexOptions.CultureInvariant);
    readonly ConcurrentDictionary<string, IAmazonSecretsManager> clients = new(StringComparer.Ordinal);
    readonly Func<string, IAmazonSecretsManager> make = client ?? (region => new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(region), MaxErrorRetry = 2 }));

    public string Scheme => "aws-sm";

    public async Task<SecretValue> Resolve(SecretRef r, CancellationToken ct = default)
    {
        var m = Locator.Match(r.Locator);
        if (!m.Success) throw new SecretUnavailable(r.ToString(), "is not aws-sm:<region>/<secret-id>[#json-field]");
        GetSecretValueResponse res;
        try { res = await clients.GetOrAdd(m.Groups[1].Value, make).GetSecretValueAsync(new GetSecretValueRequest { SecretId = m.Groups[2].Value }, ct); }
        catch (Amazon.SecretsManager.Model.ResourceNotFoundException) { throw new SecretUnavailable(r.ToString(), "does not exist: Secrets Manager answered ResourceNotFoundException"); }
        catch (Exception e) when (AwsLocator.IsAws(e) && !ct.IsCancellationRequested) { throw new SecretUnavailable(r.ToString(), AwsLocator.Reason("Secrets Manager", e)); }
        if (res.SecretString is null) throw new SecretUnavailable(r.ToString(), "is a binary secret: keep a secret the runtime reads as a secret string");
        if (m.Groups[3].Success) return JsonField(r, res.SecretString, m.Groups[3].Value);
        if (res.SecretString.Length == 0) throw new SecretUnavailable(r.ToString(), "is empty");
        return new SecretValue(res.SecretString);
    }

    static SecretValue JsonField(SecretRef r, string text, string field)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { throw new SecretUnavailable(r.ToString(), $"names field {field}, but the secret is not a JSON object"); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new SecretUnavailable(r.ToString(), $"names field {field}, but the secret is not a JSON object");
            if (doc.RootElement.TryGetProperty(field, out var v))
            {
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return new SecretValue(s);
                if (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return new SecretValue(v.GetRawText());
            }
            throw new SecretUnavailable(r.ToString(), $"has no field {field}");
        }
    }
}

/// <summary>
/// <c>aws-ssm:&lt;region&gt;/&lt;parameter-name&gt;</c>: an AWS Systems Manager Parameter Store SecureString, decrypted. A name
/// with a '/' in it is a path, and a path starts with '/': <c>aws-ssm:eu-west-1/th/prod/token</c> reads
/// <c>/th/prod/token</c>. A String or StringList parameter is refused: a secret is kept in a SecureString.
/// </summary>
public sealed class AwsParameterStoreSecretProvider(Func<string, IAmazonSimpleSystemsManagement>? client = null) : ISecretProvider
{
    static readonly Regex Locator = new($@"^({AwsLocator.Region})/(/?[A-Za-z0-9_.\-/]{{1,2048}})$", RegexOptions.CultureInvariant);
    readonly ConcurrentDictionary<string, IAmazonSimpleSystemsManagement> clients = new(StringComparer.Ordinal);
    readonly Func<string, IAmazonSimpleSystemsManagement> make = client ?? (region => new AmazonSimpleSystemsManagementClient(new AmazonSimpleSystemsManagementConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(region), MaxErrorRetry = 2 }));

    public string Scheme => "aws-ssm";

    public static string ParameterName(string name) => name.Contains('/') && !name.StartsWith('/') ? "/" + name : Regex.Replace(name, "^//+", "/");

    public async Task<SecretValue> Resolve(SecretRef r, CancellationToken ct = default)
    {
        var m = Locator.Match(r.Locator);
        if (!m.Success) throw new SecretUnavailable(r.ToString(), "is not aws-ssm:<region>/<parameter-name>");
        Parameter? p;
        try { p = (await clients.GetOrAdd(m.Groups[1].Value, make).GetParameterAsync(new GetParameterRequest { Name = ParameterName(m.Groups[2].Value), WithDecryption = true }, ct)).Parameter; }
        catch (ParameterNotFoundException) { throw new SecretUnavailable(r.ToString(), "does not exist: Parameter Store answered ParameterNotFound"); }
        catch (Exception e) when (AwsLocator.IsAws(e) && !ct.IsCancellationRequested) { throw new SecretUnavailable(r.ToString(), AwsLocator.Reason("Parameter Store", e)); }
        if (p?.Type != ParameterType.SecureString) throw new SecretUnavailable(r.ToString(), $"is a {p?.Type?.Value ?? "parameter of no type"}, not a SecureString: keep a secret in a SecureString");
        if (string.IsNullOrEmpty(p.Value)) throw new SecretUnavailable(r.ToString(), "is empty");
        return new SecretValue(p.Value);
    }
}

/// <summary>
/// A store read over the network, with its values kept for a short time. A value is never used past its time, so a
/// rotated or revoked secret takes effect within it; a store that fails is not answered from the cache.
/// </summary>
public sealed class CachingSecretProvider(ISecretProvider inner, TimeSpan ttl, TimeProvider? clock = null) : ISecretProvider
{
    readonly ConcurrentDictionary<string, (SecretValue Value, DateTimeOffset At)> cache = new(StringComparer.Ordinal);
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public string Scheme => inner.Scheme;
    public TimeSpan Ttl => ttl;

    public async Task<SecretValue> Resolve(SecretRef r, CancellationToken ct = default)
    {
        var key = r.ToString();
        if (cache.TryGetValue(key, out var hit) && clock.GetUtcNow() - hit.At < ttl) return hit.Value;
        cache.TryRemove(key, out _);
        var v = await inner.Resolve(r, ct);
        if (ttl > TimeSpan.Zero) cache[key] = (v, clock.GetUtcNow());
        return v;
    }

    public void Invalidate(string reference) => cache.TryRemove(reference, out _);
}
