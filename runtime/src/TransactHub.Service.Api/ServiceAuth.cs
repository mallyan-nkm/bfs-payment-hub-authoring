using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using TransactHub.Auth;
using TransactHub.Security;

namespace TransactHub.Service.Api;

/// <summary>
/// Who may call the service console (spec R-06-25): bearer JWTs of the bank's one OIDC issuer through the runtime's
/// authentication library (<see cref="RuntimeAuth.AddRuntimeAuth"/>), the bank's role map giving ServiceEngineer and
/// BankApprover (R-09-115); the console signs in with the authorisation code flow and PKCE, as the operations console does. The
/// studio's issuer is refused. Dev mode (TH_AUTH_DEV=1) takes a synthetic identity from the same header as the operations
/// console's dev sign-in, and is refused when the host runs in Production.
/// </summary>
public static class ServiceAuth
{
    public const string DevScheme = "th-dev";
    public const string DevHeader = "X-TH-Dev-Identity";

    public sealed record ClientConfig(string Mode, string? Authority, string? ClientId, string? Scopes, string? Audience);

    public static async Task<ClientConfig> AddServiceAuthentication(this WebApplicationBuilder b)
    {
        if (b.Configuration["TH_AUTH_DEV"] == "1")
        {
            if (b.Environment.IsProduction())
                throw new InvalidOperationException("TH_AUTH_DEV=1 is refused in Production: the service console takes identities only from the bank's identity provider there (auth.DevRefused).");
            b.Services.AddAuthentication(DevScheme).AddScheme<AuthenticationSchemeOptions, DevAuthentication>(DevScheme, _ => { });
            b.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            return new ClientConfig("dev", null, null, null, null);
        }
        var issuer = b.Configuration["Auth:Oidc:Issuer"];
        var studio = b.Configuration["TH_STUDIO_ISSUER"];
        if (issuer is { Length: > 0 } && studio is { Length: > 0 } && string.Equals(issuer.TrimEnd('/'), studio.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Auth:Oidc:Issuer is the studio's issuer: service engineers sign in at the bank's identity provider, never the studio's (auth.StudioIssuer).");
        await b.Services.AddRuntimeAuth(b.Configuration, SecretResolver.FromEnvironment(k => b.Configuration[k]), ServiceRoles.All);
        return new ClientConfig("oidc", b.Configuration["Auth:Oidc:Authority"] ?? issuer, b.Configuration["Auth:Oidc:ClientId"],
            b.Configuration["Auth:Oidc:Scopes"] ?? "openid profile", b.Configuration["Auth:Oidc:Audience"]);
    }

    /// <summary>Dev mode only: <c>{"sub","name","roles":["tenant:Role"]}</c> in a header, turned into the claims the role map gives.</summary>
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
                    claims.AddRange([new Claim(RuntimeAuth.TenantClaim, tenant), new Claim(RuntimeAuth.RoleClaim, role), new Claim(RuntimeAuth.TenantRoleClaim, RuntimeAuth.TenantRole(tenant, role))]);
            var p = new ClaimsPrincipal(new ClaimsIdentity(claims, DevScheme, "sub", RuntimeAuth.RoleClaim));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(p, DevScheme)));
        }
    }
}
