using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using TransactHub.Security;

namespace TransactHub.Auth;

/// <summary>
/// Who may call a runtime host (spec R-09-27, R-09-29; Issue I-65's runtime part). The runtime belongs to the bank and has
/// no identity store of its own: no users, no passwords, no tokens it issues or keeps. It validates OAuth 2.0 / OIDC bearer
/// JWTs from the bank's identity provider (any OIDC issuer: Entra ID, AD FS, Keycloak, Ping…), people's tokens and machine
/// callers' client-credentials tokens alike, and maps the bank's claims and groups to the host's roles through a role-map
/// file. It trusts exactly one issuer, the bank's; never the studio's. The studio reaches the runtime only through the
/// signature on its packages (TH_TRUSTED_SIGNERS).
/// <para>
/// A host calls <see cref="AddRuntimeAuth"/> with the roles it knows: the release API passes its four, an operations console's
/// API passes its own (operator, supervisor). Configuration, section <c>Auth</c>:
/// <c>Oidc:Issuer</c>, <c>Oidc:Audience</c> (required); <c>Oidc:Authority</c> (the discovery base, default the issuer) or
/// <c>Oidc:SigningKeys</c> (a JWKS, or a reference to one, for an installation that does not fetch discovery);
/// <c>Oidc:ClockSkewSeconds</c> (default 60); <c>Oidc:NameClaim</c> (default <c>sub</c>); <c>Oidc:RequireHttpsMetadata</c>;
/// <c>RoleMap</c> (the role-map JSON, or a reference to it: <c>file:/etc/transacthub/role-map.json</c>).
/// </para>
/// </summary>
public static class RuntimeAuth
{
    /// <summary>The claim types the role map gives a caller.</summary>
    public const string RoleClaim = "th:role";
    public const string TenantClaim = "th:tenant";

    /// <summary>The caller's name, as a host records it: the configured name claim of the bank's token.</summary>
    public static string Actor(ClaimsPrincipal u) => u.Identity?.Name is { Length: > 0 } n ? n : u.FindFirstValue("sub") ?? "unknown";

    /// <summary>Whether the caller is given a tenant, or every tenant (<c>*</c>).</summary>
    public static bool MayActFor(ClaimsPrincipal u, string tenant) => u.FindAll(TenantClaim).Any(c => c.Value == "*" || c.Value == tenant);

    /// <summary>
    /// Adds bearer-JWT authentication against the bank's issuer, the role map, one authorisation policy per role (named as
    /// the role) and a fallback policy that needs an authenticated caller. Throws, so the host does not start, when the
    /// issuer, the audience or the role map is missing, or the map names a role the host does not know.
    /// </summary>
    public static async Task<RoleMap> AddRuntimeAuth(this IServiceCollection services, IConfiguration cfg, ISecretResolver secrets, IReadOnlyCollection<string> roles)
    {
        var issuer = cfg["Auth:Oidc:Issuer"];
        var audience = cfg["Auth:Oidc:Audience"];
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(audience))
            throw new InvalidOperationException("The runtime authenticates callers with the bank's identity provider: set Auth:Oidc:Issuer and Auth:Oidc:Audience (spec R-09-27)");
        var mapSetting = cfg["Auth:RoleMap"] ?? throw new InvalidOperationException("Set Auth:RoleMap: the role map from the bank's claims and groups to the runtime's roles (spec R-09-27)");
        var map = RoleMap.Parse(await Resolve(mapSetting, secrets), roles);
        IList<SecurityKey>? keys = null;
        if (cfg["Auth:Oidc:SigningKeys"] is { Length: > 0 } jwks) keys = new JsonWebKeySet(await Resolve(jwks, secrets)).GetSigningKeys();
        var skew = TimeSpan.FromSeconds(double.Parse(cfg["Auth:Oidc:ClockSkewSeconds"] ?? "60", CultureInfo.InvariantCulture));

        services.AddSingleton(map);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            if (keys is null) o.Authority = cfg["Auth:Oidc:Authority"] ?? issuer;
            else o.Configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration { Issuer = issuer };
            o.Audience = audience;
            o.RequireHttpsMetadata = cfg["Auth:Oidc:RequireHttpsMetadata"] != "false";
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = issuer, ValidAudience = audience, ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true, RequireExpirationTime = true, ClockSkew = skew,
                NameClaimType = cfg["Auth:Oidc:NameClaim"] ?? "sub", RoleClaimType = RoleClaim,
                ValidAlgorithms = ["RS256", "RS384", "RS512", "PS256", "ES256", "ES384"],
                IssuerSigningKeys = keys,
            };
            // The bank's claims and groups become the host's roles and tenants, by the role map; nothing else does.
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = ctx =>
                {
                    if (ctx.Principal?.Identity is ClaimsIdentity id) map.Apply(id);
                    return Task.CompletedTask;
                },
            };
        });
        var auth = services.AddAuthorizationBuilder();
        foreach (var r in roles) auth.AddPolicy(r, p => p.RequireAuthenticatedUser().RequireRole(r));
        auth.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return map;
    }

    static async Task<string> Resolve(string setting, ISecretResolver secrets) =>
        setting.TrimStart().StartsWith('{') ? setting : (await secrets.Resolve(setting)).Reveal();
}

/// <summary>
/// The bank's mapping from its identity provider's claims to a host's roles and tenants. A grant matches when the token
/// carries the claim with the value (a claim given several times, as a JSON array, or space-separated as <c>scp</c> is, is
/// matched value by value). <c>tenantsClaim</c>, when set, also gives the tenants the token itself names.
/// <code>
/// { "kind": "runtime-role-map", "tenantsClaim": "tenants",
///   "grants": [ { "claim": "roles", "value": "TransactHub.Pipeline", "roles": ["release-importer", "release-activator"], "tenants": ["*"] },
///               { "claim": "groups", "value": "0b1c…", "roles": ["release-reader"] } ] }
/// </code>
/// </summary>
public sealed class RoleMap
{
    public sealed record Grant(string Claim, string Value, string[] Roles, string[] Tenants);
    public required IReadOnlyList<Grant> Grants { get; init; }
    public string? TenantsClaim { get; init; }

    public static RoleMap Parse(string json, IReadOnlyCollection<string> knownRoles)
    {
        JsonObject o;
        try { o = JsonNode.Parse(json) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("The role map is not a JSON object"); }
        if ((string?)o["kind"] != "runtime-role-map") throw new InvalidOperationException("The role map is not of kind runtime-role-map");
        var grants = new List<Grant>();
        foreach (var g in o["grants"] as JsonArray ?? [])
        {
            var claim = (string?)g?["claim"] ?? throw new InvalidOperationException("A grant of the role map names no claim");
            var value = (string?)g?["value"] ?? throw new InvalidOperationException($"A grant on claim {claim} names no value");
            var roles = (g?["roles"] as JsonArray ?? []).Select(x => (string)x!).ToArray();
            foreach (var r in roles) if (!knownRoles.Contains(r)) throw new InvalidOperationException($"The role map grants {r}, which this host does not know ({string.Join(", ", knownRoles)})");
            grants.Add(new Grant(claim, value, roles, (g?["tenants"] as JsonArray ?? []).Select(x => (string)x!).ToArray()));
        }
        if (grants.Count == 0) throw new InvalidOperationException("The role map grants nothing");
        return new RoleMap { Grants = grants, TenantsClaim = (string?)o["tenantsClaim"] };
    }

    static IEnumerable<string> Values(ClaimsIdentity id, string type) =>
        id.FindAll(type).SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Adds the roles and tenants the identity's claims are granted.</summary>
    public void Apply(ClaimsIdentity id)
    {
        var roles = new SortedSet<string>(StringComparer.Ordinal);
        var tenants = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var g in Grants)
            if (Values(id, g.Claim).Contains(g.Value, StringComparer.Ordinal)) { roles.UnionWith(g.Roles); tenants.UnionWith(g.Tenants); }
        if (TenantsClaim is { Length: > 0 } tc) tenants.UnionWith(Values(id, tc));
        // Only the map gives roles: a claim of the host's own types in the token is not taken.
        foreach (var c in id.FindAll(RuntimeAuth.RoleClaim).Concat(id.FindAll(RuntimeAuth.TenantClaim)).ToList()) id.RemoveClaim(c);
        foreach (var r in roles) id.AddClaim(new Claim(RuntimeAuth.RoleClaim, r));
        foreach (var t in tenants) id.AddClaim(new Claim(RuntimeAuth.TenantClaim, t));
    }
}
