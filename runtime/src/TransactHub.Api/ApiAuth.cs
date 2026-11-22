using System.Security.Claims;
using TransactHub.Auth;

namespace TransactHub.Api;

/// <summary>
/// The release API's roles (spec R-09-27). Authentication is the bank's (<see cref="RuntimeAuth"/>): a bearer JWT from its
/// identity provider, its claims and groups mapped to these roles by the role map. No caller is 401; a caller without the
/// role, or without the tenant for values, is 403. The actor the store records is the caller's verified name.
/// <list type="table">
/// <item><term>release-importer</term><description><c>POST /releases</c>, <c>POST /import-branch</c></description></item>
/// <item><term>release-activator</term><description><c>POST /activations</c>, and the activations an import carries</description></item>
/// <item><term>release-reader</term><description><c>GET /releases</c>, <c>GET /releases/package</c>, <c>GET /activations/active</c></description></item>
/// <item><term>values-reader</term><description><c>GET /values</c>, for the tenants the caller is given (or <c>*</c>)</description></item>
/// </list>
/// </summary>
public static class ApiAuth
{
    public const string Importer = "release-importer";
    public const string Activator = "release-activator";
    public const string Reader = "release-reader";
    public const string ValuesReader = "values-reader";
    /// <summary>The front door: pins a payment to the active distribution at acceptance, and records its end (R-09-53, R-09-54).</summary>
    public const string Intake = "payment-intake";
    public static readonly string[] Roles = [Importer, Activator, Reader, ValuesReader, Intake];

    public static string Actor(ClaimsPrincipal u) => RuntimeAuth.Actor(u);
    public static bool MayReadTenant(ClaimsPrincipal u, string tenant) => RuntimeAuth.MayActFor(u, tenant);
}
