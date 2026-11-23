using System.Security.Claims;
using TransactHub.Auth;

namespace TransactHub.Service;

/// <summary>
/// The service console's roles (spec R-06-25), from the bank's role map like every runtime host's. A service engineer asks;
/// a bank approver, another person, consents to every action that changes the system (owner's decision of 23 November 2026,
/// R-09-74). <see cref="Approver"/> (ServiceApprover) is the name an earlier role map gave the same consent, kept so such a map
/// still starts (Issue I-148).
/// </summary>
public static class ServiceRoles
{
    public const string Engineer = "ServiceEngineer";
    public const string BankApprover = "BankApprover";
    public const string Approver = "ServiceApprover";
    public static readonly string[] All = [Engineer, BankApprover, Approver];
}

/// <summary>
/// A service engineer: a verified identity from the bank's identity provider, never a name from the request. The roles are
/// installation-wide (health, content, backups); the tenants bound what payment data the engineer sees (logs, traces).
/// </summary>
public sealed record ServiceIdentity(string Subject, string DisplayName, IReadOnlySet<string> Roles, IReadOnlySet<string> Tenants)
{
    public const string AnyTenant = "*";

    /// <summary>A bank approver may see what a service engineer may (to judge what it consents to).</summary>
    public bool Has(string role) => Roles.Contains(role) || (role == ServiceRoles.Engineer && MayConsent) || (role == ServiceRoles.BankApprover && Roles.Contains(ServiceRoles.Approver));

    /// <summary>Whether this person may consent to a change of the system another person asked for (R-09-74).</summary>
    public bool MayConsent => Roles.Contains(ServiceRoles.BankApprover) || Roles.Contains(ServiceRoles.Approver);

    public bool Sees(string tenant) => Tenants.Contains(AnyTenant) || Tenants.Contains(tenant);

    /// <summary>What the facts record as the actor.</summary>
    public string Actor => $"service:{Subject}";

    /// <summary>The role map's roles and tenants on a principal that TransactHub.Auth (or the dev sign-in) gave.</summary>
    public static ServiceIdentity? From(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var sub = user.FindFirst("sub")?.Value ?? RuntimeAuth.Actor(user);
        var roles = user.FindAll(RuntimeAuth.RoleClaim).Select(c => c.Value).Where(ServiceRoles.All.Contains).ToHashSet(StringComparer.Ordinal);
        var tenants = user.FindAll(RuntimeAuth.TenantClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var name = user.FindFirst("name")?.Value ?? user.FindFirst("preferred_username")?.Value ?? sub;
        return new ServiceIdentity(sub, name, roles, tenants);
    }
}

/// <summary>A refusal with its HTTP status and its error code in the dictionary (R-05-52).</summary>
public sealed class ServiceRefused(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
