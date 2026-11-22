using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using TransactHub.Operations;

namespace TransactHub.Operations.Api;

/// <summary>
/// How the console's API knows who is calling (R-06-22). The runtime has no identity store: it trusts the bank's
/// identity provider over OIDC. The API validates the bearer JWT the console got by the authorisation code flow with
/// PKCE (issuer, audience, signature, lifetime), never a token of the studio's issuer, and maps the bank's claims or
/// groups to Operator and Supervisor per tenant (<see cref="RoleMapping"/>). Dev mode (TH_AUTH_DEV=1) takes a
/// synthetic identity from a header instead, and is refused when the host runs in Production.
/// This is the small seam the runtime's own authentication library replaces at merge: it registers a scheme
/// that yields a principal; the role mapping and everything after it stay.
/// </summary>
public static class OperatorAuth
{
    public const string DevScheme = "th-dev";
    public const string DevHeader = "X-TH-Dev-Identity";

    public sealed record ClientConfig(string Mode, string? Authority, string? ClientId, string? Scopes, string? Audience);

    public static ClientConfig AddOperatorAuthentication(this WebApplicationBuilder b)
    {
        var dev = b.Configuration["TH_AUTH_DEV"] == "1";
        var rules = b.Configuration.GetSection("Operations:Roles").Get<List<RoleRule>>();
        b.Services.AddSingleton<IOperatorIdentityResolver>(rules is { Count: > 0 } ? new RoleMapping(rules) : RoleMapping.Default);
        if (dev)
        {
            if (b.Environment.IsProduction())
                throw new InvalidOperationException("TH_AUTH_DEV=1 is refused in Production: the operations console takes identities only from the bank's identity provider there.");
            b.Services.AddAuthentication(DevScheme).AddScheme<AuthenticationSchemeOptions, DevAuthentication>(DevScheme, _ => { });
            b.Services.AddAuthorization();
            return new ClientConfig("dev", null, null, null, null);
        }
        var authority = b.Configuration["TH_OIDC_AUTHORITY"] ?? throw new InvalidOperationException("Set TH_OIDC_AUTHORITY (the bank's identity provider) or, outside Production, TH_AUTH_DEV=1");
        var audience = b.Configuration["TH_OIDC_AUDIENCE"] ?? throw new InvalidOperationException("Set TH_OIDC_AUDIENCE (the API's audience at the bank's identity provider)");
        var studio = b.Configuration["TH_STUDIO_ISSUER"];
        if (studio is { Length: > 0 } && Same(studio, authority))
            throw new InvalidOperationException("TH_OIDC_AUTHORITY is the studio's issuer: operators sign in at the bank's identity provider, never the studio's.");
        b.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.Authority = authority;
            o.Audience = audience;
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = !b.Environment.IsDevelopment();
            o.TokenValidationParameters.ValidateIssuer = true;
            o.TokenValidationParameters.ValidateAudience = true;
            o.TokenValidationParameters.ValidateLifetime = true;
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = ctx =>
                {
                    var iss = ctx.Principal?.FindFirst("iss")?.Value;
                    if (studio is { Length: > 0 } && iss is not null && Same(iss, studio)) ctx.Fail("a token of the studio's issuer is never accepted here");
                    return Task.CompletedTask;
                },
            };
        });
        b.Services.AddAuthorization();
        return new ClientConfig("oidc", authority, b.Configuration["TH_OIDC_CLIENT_ID"], b.Configuration["TH_OIDC_SCOPES"] ?? "openid profile", audience);
    }

    static bool Same(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dev mode only: the identity is a JSON object in a header, <c>{"sub","name","roles":["tenant:Role"]}</c>, as the console's
    /// dev sign-in sends it. Synthetic users only.
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
            var claims = new List<Claim> { new("sub", sub), new("name", (string?)o!["name"] ?? sub), new("iss", "th-dev") };
            foreach (var r in (o["roles"] as JsonArray ?? [])) if ((string?)r is { } role) claims.Add(new Claim("th_roles", role));
            var p = new ClaimsPrincipal(new ClaimsIdentity(claims, DevScheme, "name", "th_roles"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(p, DevScheme)));
        }
    }
}
