using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using TransactHub.Auth;
using TransactHub.Security;

namespace TransactHub.TestBench;

/// <summary>
/// How the test bench service signs its callers in: the runtime's one authentication library (<c>TransactHub.Auth</c>,
/// <see cref="RuntimeAuth.AddRuntimeAuth"/>), as the operations and service consoles do. Bearer JWTs of the bank's one OIDC
/// issuer; the bank's role map gives the roles and tenants; the web app (the runtime's shell, <c>runtime/ops-console</c>)
/// gets the token by the authorisation code flow with PKCE, with the one public client the runtime's web app uses
/// (<c>transacthub-runtime-ui</c>). The service keeps no identity of its own. Dev mode (TH_AUTH_DEV=1) takes a synthetic
/// identity from a header, as the consoles' dev sign-in sends it, and is refused when the host runs in Production.
/// </summary>
public interface IBenchAuth
{
    /// <summary>What the web app needs to sign in, the same shape for every section: mode, authority, client, scopes, audience.</summary>
    JsonObject ClientConfig();
}

public static class BenchRoles
{
    /// <summary>The roles this host knows. Tester runs the bench; an installation may also let Operator or Supervisor (Auth:AllowedRoles).</summary>
    public const string Tester = "Tester";
    public static readonly string[] Known = [Tester, "Operator", "Supervisor"];
    public const string Policy = "testbench";
    public const string DevScheme = "th-dev";
    public const string DevHeader = "X-TH-Dev-Identity";
    /// <summary>The one public client of the runtime's web app: the operations, service and test bench sections sign in once with it.</summary>
    public const string RuntimeClient = "transacthub-runtime-ui";

    public static string Actor(ClaimsPrincipal u) => RuntimeAuth.Actor(u);
    public static bool MayActFor(ClaimsPrincipal u, string tenant) => RuntimeAuth.MayActFor(u, tenant);
}

public sealed class BenchAuth(JsonObject config) : IBenchAuth
{
    public JsonObject ClientConfig() => (JsonObject)config.DeepClone();

    /// <summary>Configures authentication and the Tester policy; refuses to start without the bank's issuer, or with dev mode in Production.</summary>
    public static async Task<IBenchAuth> Add(WebApplicationBuilder b)
    {
        var cfg = b.Configuration;
        var allowed = (cfg["Auth:AllowedRoles"] ?? BenchRoles.Tester).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var r in allowed) if (!BenchRoles.Known.Contains(r)) throw new BenchError($"Auth:AllowedRoles names {r}; the roles are {string.Join(", ", BenchRoles.Known)}");
        JsonObject client;
        if ((cfg["TH_AUTH_DEV"] ?? Environment.GetEnvironmentVariable("TH_AUTH_DEV")) == "1")
        {
            if (b.Environment.IsProduction())
                throw new BenchError("TH_AUTH_DEV=1 is refused in Production: the test bench takes identities only from the bank's identity provider there");
            b.Services.AddAuthentication(BenchRoles.DevScheme).AddScheme<AuthenticationSchemeOptions, DevAuthentication>(BenchRoles.DevScheme, _ => { });
            client = new JsonObject { ["mode"] = "dev" };
        }
        else
        {
            if (string.IsNullOrEmpty(cfg["Auth:Oidc:Issuer"]))
                throw new BenchError("The test bench service signs callers in with the bank's identity provider: set Auth:Oidc:Issuer, Auth:Oidc:Audience and Auth:RoleMap (or TH_AUTH_DEV=1 on a developer's machine)");
            try { await b.Services.AddRuntimeAuth(cfg, SecretResolver.FromEnvironment(k => cfg[k]), BenchRoles.Known); }
            catch (InvalidOperationException e) { throw new BenchError(e.Message); }
            client = new JsonObject
            {
                ["mode"] = "oidc", ["authority"] = cfg["Auth:Oidc:Authority"] ?? cfg["Auth:Oidc:Issuer"],
                ["clientId"] = cfg["Auth:Oidc:ClientId"] ?? BenchRoles.RuntimeClient, ["scopes"] = cfg["Auth:Oidc:Scopes"] ?? "openid profile",
                ["audience"] = cfg["Auth:Oidc:Audience"],
            };
        }
        b.Services.AddAuthorizationBuilder()
            .AddPolicy(BenchRoles.Policy, p => p.RequireAuthenticatedUser().RequireAssertion(c => allowed.Any(r => c.User.HasClaim(RuntimeAuth.RoleClaim, r))))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return new BenchAuth(client);
    }

    /// <summary>
    /// Dev mode only: the identity is a JSON object in a header, <c>{"sub","name","roles":["tenant:Role"]}</c>, as the shell's
    /// dev sign-in sends it (runtime/ops-console/src/shell), turned into the claims the role map gives. Synthetic users only.
    /// </summary>
    sealed class DevAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var raw = Request.Headers[BenchRoles.DevHeader].ToString();
            if (raw.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            JsonObject? o;
            try { o = JsonNode.Parse(raw) as JsonObject; } catch { return Task.FromResult(AuthenticateResult.Fail("the dev identity is not JSON")); }
            var sub = J.S(o, "sub");
            if (string.IsNullOrWhiteSpace(sub)) return Task.FromResult(AuthenticateResult.Fail("the dev identity has no sub"));
            var claims = new List<Claim> { new("sub", sub), new("name", J.S(o, "name") ?? sub) };
            foreach (var pair in J.Strings(o!["roles"]))
                if (pair.Split(':') is [var tenant, var role])
                    claims.AddRange([new Claim(RuntimeAuth.TenantClaim, tenant), new Claim(RuntimeAuth.RoleClaim, role)]);
            var p = new ClaimsPrincipal(new ClaimsIdentity(claims, BenchRoles.DevScheme, "sub", RuntimeAuth.RoleClaim));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(p, BenchRoles.DevScheme)));
        }
    }
}
