using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using TransactHub.Auth;
using TransactHub.Operations;
using TransactHub.Security;

namespace TransactHub.Operations.Api;

/// <summary>
/// How the console's API knows who is calling (R-06-24). It uses the runtime's one authentication library
/// (<c>TransactHub.Auth</c>, <see cref="RuntimeAuth.AddRuntimeAuth"/>): bearer JWTs of the bank's one OIDC issuer, the bank's
/// role map giving the roles Operator and Supervisor and the tenants. The console gets the token by the authorisation
/// code flow with PKCE. The studio's issuer is never the operators' (<c>TH_STUDIO_ISSUER</c> is refused as
/// <c>Auth:Oidc:Issuer</c>). Dev mode (TH_AUTH_DEV=1) takes a synthetic identity from a header instead, and is refused
/// when the host runs in Production.
/// </summary>
public static class OperatorAuth
{
    public const string DevScheme = "th-dev";
    public const string DevHeader = "X-TH-Dev-Identity";

    public sealed record ClientConfig(string Mode, string? Authority, string? ClientId, string? Scopes, string? Audience);

    public static async Task<ClientConfig> AddOperatorAuthentication(this WebApplicationBuilder b)
    {
        b.Services.AddSingleton<IOperatorIdentityResolver, RuntimeAuthOperators>();
        if (b.Configuration["TH_AUTH_DEV"] == "1")
        {
            if (b.Environment.IsProduction())
                throw new InvalidOperationException("TH_AUTH_DEV=1 is refused in Production: the operations console takes identities only from the bank's identity provider there.");
            b.Services.AddAuthentication(DevScheme).AddScheme<AuthenticationSchemeOptions, DevAuthentication>(DevScheme, _ => { });
            b.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            return new ClientConfig("dev", null, null, null, null);
        }
        var issuer = b.Configuration["Auth:Oidc:Issuer"];
        var studio = b.Configuration["TH_STUDIO_ISSUER"];
        if (issuer is { Length: > 0 } && studio is { Length: > 0 } && string.Equals(issuer.TrimEnd('/'), studio.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Auth:Oidc:Issuer is the studio's issuer: operators sign in at the bank's identity provider, never the studio's.");
        await b.Services.AddRuntimeAuth(b.Configuration, SecretResolver.FromEnvironment(k => b.Configuration[k]), OperatorRoles.All);
        return new ClientConfig("oidc", b.Configuration["Auth:Oidc:Authority"] ?? issuer, b.Configuration["Auth:Oidc:ClientId"],
            b.Configuration["Auth:Oidc:Scopes"] ?? "openid profile", b.Configuration["Auth:Oidc:Audience"]);
    }

    /// <summary>
    /// Dev mode only: the identity is a JSON object in a header, <c>{"sub","name","roles":["tenant:Role"]}</c>, as the console's
    /// dev sign-in sends it, turned into the same claims the role map gives. Synthetic users only.
    /// </summary>
    sealed class DevAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var raw = Request.Headers[DevHeader].ToString();
            if (raw.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            JsonObject? o;
            try { o = JsonNode.Parse(raw) as JsonObject; } catch { return Task.FromResult(AuthenticateResult.Fail("the dev identity is not JSON")); }
            var sub = (string?)o?["sub"];
            if (string.IsNullOrWhiteSpace(sub)) return Task.FromResult(AuthenticateResult.Fail("the dev identity has no sub"));
            var claims = new List<Claim> { new("sub", sub), new("name", (string?)o!["name"] ?? sub) };
            foreach (var r in (o["roles"] as JsonArray ?? []))
                if ((string?)r is { } pair && pair.Split(':') is [var tenant, var role])
                    claims.AddRange([new Claim(RuntimeAuth.TenantClaim, tenant), new Claim(RuntimeAuth.RoleClaim, role)]);
            var p = new ClaimsPrincipal(new ClaimsIdentity(claims, DevScheme, "sub", RuntimeAuth.RoleClaim));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(p, DevScheme)));
        }
    }
}

/// <summary>
/// An operator from the runtime's claims: the role map's roles (<see cref="RuntimeAuth.RoleClaim"/>) in each tenant it grants
/// (<see cref="RuntimeAuth.TenantClaim"/>, <c>*</c> for every tenant). The library grants roles and tenants together, not a
/// role per tenant (Issue I-65).
/// </summary>
public sealed class RuntimeAuthOperators : IOperatorIdentityResolver
{
    public OperatorIdentity? Resolve(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var sub = user.FindFirst("sub")?.Value ?? RuntimeAuth.Actor(user);
        var roles = user.FindAll(RuntimeAuth.RoleClaim).Select(c => c.Value).Where(OperatorRoles.All.Contains).ToHashSet();
        var tenants = user.FindAll(RuntimeAuth.TenantClaim).Select(c => c.Value).Distinct();
        var name = user.FindFirst("name")?.Value ?? user.FindFirst("preferred_username")?.Value ?? sub;
        return new OperatorIdentity(sub, name, roles.Count == 0 ? new Dictionary<string, IReadOnlySet<string>>()
            : tenants.ToDictionary(t => t, _ => (IReadOnlySet<string>)roles));
    }
}
