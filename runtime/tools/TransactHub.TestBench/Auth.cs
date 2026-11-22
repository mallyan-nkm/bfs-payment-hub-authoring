using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace TransactHub.TestBench;

/// <summary>
/// How the test bench service signs its callers in. It is the seam to the runtime's shared auth library
/// (TransactHub.Auth, built beside this): at merge, <see cref="OidcBenchAuth"/> is replaced by a call to that library's
/// AddRuntimeAuth with this host's roles, and nothing else here changes. The service has no identity store of its own:
/// the bank's OIDC identity provider signs people in (authorization code with PKCE in the UI), the API takes its bearer
/// JWTs, and the bank's claims become roles through the runtime's role map (kind runtime-role-map).
/// </summary>
public interface IBenchAuth
{
    /// <summary>"oidc", or "dev" for a developer's machine (TH_AUTH_DEV=1, refused in Production).</summary>
    string Mode { get; }
    /// <summary>What the UI needs to sign in: the issuer (its discovery document), the client id and the scope.</summary>
    JsonObject ClientConfig();
    void Map(WebApplication app);
}

public static class BenchRoles
{
    /// <summary>The roles this host knows. Tester runs the bench; an installation may also let operators or supervisors (Auth:AllowedRoles).</summary>
    public static readonly string[] Known = ["tester", "operator", "supervisor"];
    public const string Policy = "testbench";
    public const string RoleClaim = "th:role";
    public const string TenantClaim = "th:tenant";

    public static string Actor(ClaimsPrincipal u) => u.Identity?.Name is { Length: > 0 } n ? n : u.FindFirst("sub")?.Value ?? "unknown";
    public static bool MayActFor(ClaimsPrincipal u, string tenant) => u.FindAll(TenantClaim).Any(c => c.Value == "*" || c.Value == tenant);
}

/// <summary>
/// The bank's mapping from its identity provider's claims to this host's roles and tenants: the same file format and
/// the same rules as the runtime's role map (runtime/src/TransactHub.Auth, RoleMap), so one bank file serves both.
/// Kept here only until the shared library is merged.
/// </summary>
public sealed class BenchRoleMap
{
    public sealed record Grant(string Claim, string Value, string[] Roles, string[] Tenants);
    public required IReadOnlyList<Grant> Grants { get; init; }
    public string? TenantsClaim { get; init; }

    public static BenchRoleMap Parse(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new BenchError("The role map is not a JSON object");
        if (J.S(o, "kind") != "runtime-role-map") throw new BenchError("The role map is not of kind runtime-role-map");
        var grants = new List<Grant>();
        foreach (var g in (o["grants"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var roles = J.Strings(g["roles"]).ToArray();
            foreach (var r in roles) if (!BenchRoles.Known.Contains(r)) throw new BenchError($"The role map grants {r}, which the test bench does not know ({string.Join(", ", BenchRoles.Known)})");
            grants.Add(new Grant(J.S(g, "claim") ?? throw new BenchError("A grant names no claim"), J.S(g, "value") ?? throw new BenchError("A grant names no value"), roles, J.Strings(g["tenants"]).ToArray()));
        }
        if (grants.Count == 0) throw new BenchError("The role map grants nothing");
        return new BenchRoleMap { Grants = grants, TenantsClaim = J.S(o, "tenantsClaim") };
    }

    static IEnumerable<string> Values(ClaimsIdentity id, string type) => id.FindAll(type).SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public void Apply(ClaimsIdentity id)
    {
        var roles = new SortedSet<string>(StringComparer.Ordinal);
        var tenants = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var g in Grants)
            if (Values(id, g.Claim).Contains(g.Value, StringComparer.Ordinal)) { roles.UnionWith(g.Roles); tenants.UnionWith(g.Tenants); }
        if (TenantsClaim is { Length: > 0 } tc) tenants.UnionWith(Values(id, tc));
        foreach (var c in id.FindAll(BenchRoles.RoleClaim).Concat(id.FindAll(BenchRoles.TenantClaim)).ToList()) id.RemoveClaim(c);
        foreach (var r in roles) id.AddClaim(new Claim(BenchRoles.RoleClaim, r));
        foreach (var t in tenants) id.AddClaim(new Claim(BenchRoles.TenantClaim, t));
    }
}

/// <summary>Bearer JWTs from one issuer (the bank's, or the developer identity provider), the role map, and the policy.</summary>
public sealed class OidcBenchAuth : IBenchAuth
{
    readonly string issuer, audience, clientId, scope;
    readonly DevIdentityProvider? dev;
    public string Mode => dev is null ? "oidc" : "dev";

    OidcBenchAuth(string issuer, string audience, string clientId, string scope, DevIdentityProvider? dev)
    {
        this.issuer = issuer; this.audience = audience; this.clientId = clientId; this.scope = scope; this.dev = dev;
    }

    /// <summary>Configures authentication and authorisation; refuses to start without the bank's issuer, or with the developer login in Production.</summary>
    public static IBenchAuth Add(WebApplicationBuilder b, Home home, string baseUrl)
    {
        var cfg = b.Configuration;
        var devOn = Environment.GetEnvironmentVariable("TH_AUTH_DEV") == "1";
        if (devOn && b.Environment.IsProduction())
            throw new BenchError("TH_AUTH_DEV=1 is refused in Production: sign in with the bank's identity provider (Auth:Oidc:Issuer)");
        DevIdentityProvider? dev = devOn ? new DevIdentityProvider(baseUrl.TrimEnd('/') + "/dev-idp") : null;
        var issuer = dev?.Issuer ?? cfg["Auth:Oidc:Issuer"];
        var audience = dev is not null ? DevIdentityProvider.Audience : cfg["Auth:Oidc:Audience"];
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(audience))
            throw new BenchError("The test bench service signs callers in with the bank's identity provider: set Auth:Oidc:Issuer, Auth:Oidc:Audience, Auth:Oidc:ClientId and Auth:RoleMap (or TH_AUTH_DEV=1 on a developer's machine)");
        var mapSetting = cfg["Auth:RoleMap"] ?? (dev is not null ? "file:" + Path.Combine(home.Bench, "auth", "dev-role-map.json") : null)
            ?? throw new BenchError("Set Auth:RoleMap: the bank's role map (kind runtime-role-map), inline JSON or file:<path>");
        var map = BenchRoleMap.Parse(mapSetting.TrimStart().StartsWith('{') ? mapSetting : File.ReadAllText(mapSetting.StartsWith("file:") ? mapSetting[5..] : mapSetting));
        var allowed = (cfg["Auth:AllowedRoles"] ?? "tester").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var r in allowed) if (!BenchRoles.Known.Contains(r)) throw new BenchError($"Auth:AllowedRoles names {r}; the roles are {string.Join(", ", BenchRoles.Known)}");

        b.Services.AddSingleton(map);
        b.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            if (dev is null)
            {
                o.Authority = cfg["Auth:Oidc:Authority"] ?? issuer;
                o.RequireHttpsMetadata = cfg["Auth:Oidc:RequireHttpsMetadata"] != "false";
            }
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = issuer, ValidAudience = audience, ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(60),
                NameClaimType = cfg["Auth:Oidc:NameClaim"] ?? "sub", RoleClaimType = BenchRoles.RoleClaim,
                ValidAlgorithms = ["RS256", "RS384", "RS512", "PS256", "ES256", "ES384"],
                IssuerSigningKey = dev?.Key,
            };
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = ctx =>
                {
                    if (ctx.Principal?.Identity is ClaimsIdentity id) map.Apply(id);
                    return Task.CompletedTask;
                },
            };
        });
        b.Services.AddAuthorizationBuilder().AddPolicy(BenchRoles.Policy, p => p.RequireAuthenticatedUser().RequireAssertion(c => allowed.Any(r => c.User.HasClaim(BenchRoles.RoleClaim, r))));
        return new OidcBenchAuth(issuer, audience, cfg["Auth:Oidc:ClientId"] ?? "transacthub-runtime-ui", cfg["Auth:Oidc:Scope"] ?? "openid profile", dev);
    }

    public JsonObject ClientConfig() => new() { ["mode"] = Mode, ["issuer"] = issuer, ["clientId"] = dev is not null ? DevIdentityProvider.ClientId : clientId, ["scope"] = scope, ["audience"] = audience };

    public void Map(WebApplication app) => dev?.Map(app);
}

/// <summary>
/// A developer's identity provider, on with TH_AUTH_DEV=1 only and never in Production: a real OIDC authorization-code
/// flow with PKCE (S256) and RS256 tokens from a key made at start, so the UI and the API run exactly the code they run
/// against the bank's provider. It keeps no user: whoever signs in names themselves and their groups.
/// </summary>
public sealed class DevIdentityProvider(string issuer)
{
    public const string Audience = "transacthub-testbench";
    public const string ClientId = "transacthub-runtime-ui";
    public static readonly string[] Groups = ["th-testers", "th-operators", "th-supervisors", "th-readers"];
    public string Issuer => issuer;
    readonly RSA rsa = RSA.Create(2048);
    public RsaSecurityKey Key => key ??= new RsaSecurityKey(rsa) { KeyId = "dev-" + Convert.ToHexStringLower(SHA256.HashData(rsa.ExportRSAPublicKey()))[..8] };
    RsaSecurityKey? key;
    readonly ConcurrentDictionary<string, (string Challenge, string Redirect, string User, string[] Groups, string[] Tenants, DateTimeOffset At)> codes = new();

    string Path0 => new Uri(issuer).AbsolutePath;

    public string Token(string user, string[] groups, string[] tenants, TimeSpan? life = null)
    {
        var claims = new List<Claim> { new("sub", user), new("preferred_username", user) };
        claims.AddRange(groups.Select(g => new Claim("groups", g)));
        if (tenants.Length > 0) claims.Add(new Claim("tenants", string.Join(' ', tenants)));
        var t = new JwtSecurityToken(issuer, Audience, claims, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.Add(life ?? TimeSpan.FromHours(1)), new SigningCredentials(Key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(t);
    }

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string H(string s) => WebUtility.HtmlEncode(s);

    public void Map(WebApplication app)
    {
        var p = Path0;
        app.MapGet($"{p}/.well-known/openid-configuration", () => Results.Json(new JsonObject
        {
            ["issuer"] = issuer, ["authorization_endpoint"] = $"{issuer}/authorize", ["token_endpoint"] = $"{issuer}/token", ["jwks_uri"] = $"{issuer}/jwks",
            ["response_types_supported"] = new JsonArray("code"), ["code_challenge_methods_supported"] = new JsonArray("S256"), ["grant_types_supported"] = new JsonArray("authorization_code"),
        })).AllowAnonymous();
        app.MapGet($"{p}/jwks", () =>
        {
            var k = rsa.ExportParameters(false);
            return Results.Json(new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA", ["use"] = "sig", ["alg"] = "RS256", ["kid"] = Key.KeyId, ["n"] = B64Url(k.Modulus!), ["e"] = B64Url(k.Exponent!) }) });
        }).AllowAnonymous();
        app.MapGet($"{p}/authorize", (HttpRequest r) =>
        {
            var q = r.Query;
            if (q["response_type"] != "code" || q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["code_challenge"])) return Results.BadRequest("The developer identity provider takes the authorization code flow with PKCE (S256) only");
            if (q["client_id"] != ClientId) return Results.BadRequest($"Unknown client {q["client_id"]}");
            var groups = string.Concat(Groups.Select(g => $"<label><input type=\"checkbox\" name=\"groups\" value=\"{g}\"{(g == "th-testers" ? " checked" : "")}> {g}</label><br>"));
            var hidden = string.Concat(new[] { "redirect_uri", "code_challenge", "state" }.Select(k => $"<input type=\"hidden\" name=\"{k}\" value=\"{H(q[k].ToString())}\">"));
            return Results.Content($"""
                <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Developer sign-in</title><meta name="viewport" content="width=device-width, initial-scale=1">
                <style>body{"{"}font:14px system-ui;max-width:420px;margin:40px auto;padding:0 16px{"}"} input[type=text]{"{"}width:100%;padding:6px{"}"} .warn{"{"}background:#fff4ce;padding:8px;border-radius:4px{"}"}</style></head>
                <body><h1>Developer sign-in</h1><p class="warn">TH_AUTH_DEV=1: a developer's identity provider, refused in Production. The bank's identity provider signs people in everywhere else.</p>
                <form method="post" action="{issuer}/authorize">{hidden}
                <p><label>User <input type="text" name="user" value="dev.tester" autofocus></label></p>
                <p>Groups<br>{groups}</p>
                <p><label>Tenants (space-separated, * for all) <input type="text" name="tenants" value=""></label></p>
                <p><button type="submit" id="sign-in">Sign in</button></p></form></body></html>
                """, "text/html");
        }).AllowAnonymous();
        app.MapPost($"{p}/authorize", async (HttpRequest r) =>
        {
            var f = await r.ReadFormAsync();
            var redirect = f["redirect_uri"].ToString();
            if (!Uri.TryCreate(redirect, UriKind.Absolute, out var ru) || ru.GetLeftPart(UriPartial.Authority) != new Uri(issuer).GetLeftPart(UriPartial.Authority))
                return Results.BadRequest("The redirect is not to this host");
            var user = f["user"].ToString().Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(user, @"^[A-Za-z0-9._-]{1,64}$")) return Results.BadRequest("A user name is letters, digits, dot, dash and underscore");
            var code = B64Url(RandomNumberGenerator.GetBytes(24));
            codes[code] = (f["code_challenge"].ToString(), redirect, user, f["groups"].Where(g => g is not null && Groups.Contains(g)).Select(g => g!).ToArray(),
                f["tenants"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries), DateTimeOffset.UtcNow);
            var sep = redirect.Contains('?') ? '&' : '?';
            return Results.Redirect($"{redirect}{sep}code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(f["state"].ToString())}");
        }).AllowAnonymous().DisableAntiforgery();
        app.MapPost($"{p}/token", async (HttpRequest r) =>
        {
            var f = await r.ReadFormAsync();
            if (f["grant_type"] != "authorization_code") return Results.Json(new JsonObject { ["error"] = "unsupported_grant_type" }, statusCode: 400);
            if (!codes.TryRemove(f["code"].ToString(), out var c) || DateTimeOffset.UtcNow - c.At > TimeSpan.FromMinutes(5)) return Results.Json(new JsonObject { ["error"] = "invalid_grant" }, statusCode: 400);
            if (f["redirect_uri"] != c.Redirect || f["client_id"] != ClientId) return Results.Json(new JsonObject { ["error"] = "invalid_grant" }, statusCode: 400);
            var verifier = f["code_verifier"].ToString();
            if (verifier.Length < 43 || B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != c.Challenge) return Results.Json(new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "PKCE" }, statusCode: 400);
            var token = Token(c.User, c.Groups, c.Tenants);
            return Results.Json(new JsonObject { ["access_token"] = token, ["id_token"] = token, ["token_type"] = "Bearer", ["expires_in"] = 3600 });
        }).AllowAnonymous().DisableAntiforgery();
    }
}
