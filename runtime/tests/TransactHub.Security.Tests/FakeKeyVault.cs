using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TransactHub.Security.Tests;

/// <summary>
/// A small fake of Azure Key Vault and of the Microsoft identity platform's token endpoint, in the process: the
/// <see cref="HttpMessageHandler"/> the official SDK (Azure.Security.KeyVault.Secrets with Azure.Identity) is given as its
/// transport in place of the network. It answers the REST protocol the SDK speaks: a request with no token gets 401 and
/// the Bearer challenge (authorization, resource); the client-credentials grant at
/// <c>&lt;authority&gt;/&lt;tenant&gt;/oauth2/v2.0/token</c> gives a token for the right client secret only (with the
/// instance discovery and OpenID metadata MSAL asks for first); <c>GET /secrets/{name}[/{version}]?api-version=</c>
/// answers the secret bundle, 404 SecretNotFound or 403 Forbidden. There is no official Key Vault emulator. Every value
/// here is made by the test that uses it.
/// </summary>
public sealed class FakeKeyVault(string tenantId, string? clientId = null, string? clientSecret = null) : HttpMessageHandler
{
    public const string Authority = "https://login.fake-authority.test";
    readonly ConcurrentDictionary<string, List<(string Version, string Value)>> secrets = new();
    readonly ConcurrentDictionary<string, bool> tokens = new();
    public ConcurrentQueue<string> Requests { get; } = new();
    public ConcurrentQueue<string> ClientSecretsSeen { get; } = new();
    public ConcurrentQueue<string> Unknown { get; } = new();

    public string Set(string vault, string name, string value)
    {
        var version = Guid.NewGuid().ToString("N");
        var list = secrets.GetOrAdd($"{vault}/{name}", _ => []);
        lock (list) list.Add((version, value));
        return version;
    }

    public string Issue(bool granted = true)
    {
        var t = "synthetic-token-" + Guid.NewGuid().ToString("N");
        tokens[t] = granted;
        return t;
    }

    static HttpResponseMessage Json(HttpStatusCode status, object body, string? challenge = null)
    {
        var res = new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (challenge is not null) res.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
        return res;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        var origin = url.GetLeftPart(UriPartial.Authority);
        Requests.Enqueue($"{request.Method} {origin}{url.AbsolutePath}");
        var tenant = $"/{tenantId}";
        var authorityHost = new Uri(Authority).Host;
        if (origin == "https://login.microsoftonline.com" && url.AbsolutePath == "/common/discovery/instance")
            return Json(HttpStatusCode.OK, new Dictionary<string, object> { ["tenant_discovery_endpoint"] = $"{Authority}{tenant}/v2.0/.well-known/openid-configuration", ["api-version"] = "1.1", ["metadata"] = new[] { new { preferred_network = authorityHost, preferred_cache = authorityHost, aliases = new[] { authorityHost } } } });
        if (origin == Authority && url.AbsolutePath == $"{tenant}/v2.0/.well-known/openid-configuration")
            return Json(HttpStatusCode.OK, new { issuer = $"{Authority}{tenant}/v2.0", token_endpoint = $"{Authority}{tenant}/oauth2/v2.0/token", authorization_endpoint = $"{Authority}{tenant}/oauth2/v2.0/authorize", end_session_endpoint = $"{Authority}{tenant}/oauth2/v2.0/logout", jwks_uri = $"{Authority}{tenant}/discovery/v2.0/keys" });
        if (origin == Authority && url.AbsolutePath == $"{tenant}/oauth2/v2.0/token" && request.Method == HttpMethod.Post)
        {
            var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            ClientSecretsSeen.Enqueue(form["client_secret"] ?? "");
            if (form["grant_type"] != "client_credentials" || form["client_id"] != clientId || form["client_secret"] != clientSecret)
                return Json(HttpStatusCode.Unauthorized, new { error = "invalid_client", error_description = "AADSTS7000215: Invalid client secret provided." });
            return Json(HttpStatusCode.OK, new { token_type = "Bearer", expires_in = 3599, ext_expires_in = 3599, access_token = Issue() });
        }
        var vault = Regex.Match(origin, @"^https://([a-z0-9-]+)\.vault\.azure\.net$");
        var path = Regex.Match(url.AbsolutePath, @"^/secrets/([A-Za-z0-9-]+)/?([0-9a-f]{32})?/?$");
        if (vault.Success && path.Success && request.Method == HttpMethod.Get && url.Query.Contains("api-version="))
        {
            var auth = request.Headers.Authorization;
            var token = auth?.Scheme == "Bearer" ? auth.Parameter ?? "" : "";
            if (!tokens.TryGetValue(token, out var granted))
                return Json(HttpStatusCode.Unauthorized, new { error = new { code = "Unauthorized", message = "AKV10000: Request is missing a Bearer or PoP token." } }, $"Bearer authorization=\"{Authority}{tenant}\", resource=\"https://vault.azure.net\"");
            if (!granted) return Json(HttpStatusCode.Forbidden, new { error = new { code = "Forbidden", message = "The user, group or application does not have secrets get permission." } });
            var name = path.Groups[1].Value;
            (string Version, string Value)? s = null;
            if (secrets.TryGetValue($"{vault.Groups[1].Value}/{name}", out var list))
                lock (list) s = path.Groups[2].Success ? list.FirstOrDefault(v => v.Version == path.Groups[2].Value) is { Version: not null } f ? f : null : list[^1];
            if (s is null) return Json(HttpStatusCode.NotFound, new { error = new { code = "SecretNotFound", message = $"A secret with (name/id) {name} was not found in this key vault." } });
            return Json(HttpStatusCode.OK, new { value = s.Value.Value, id = $"{origin}/secrets/{name}/{s.Value.Version}", attributes = new { enabled = true, created = 1, updated = 1, recoveryLevel = "Recoverable+Purgeable" }, tags = new { } });
        }
        Unknown.Enqueue($"{request.Method} {url}");
        return Json(HttpStatusCode.BadRequest, new { error = new { code = "BadRequest", message = "The fake does not answer this request." } });
    }
}
