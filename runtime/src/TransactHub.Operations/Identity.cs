using System.Security.Claims;
using System.Text.RegularExpressions;

namespace TransactHub.Operations;

/// <summary>The two operator roles, per tenant (R-06-22). A supervisor may do what an operator may, and is the second approver.</summary>
public static class OperatorRoles
{
    public const string Operator = "Operator";
    public const string Supervisor = "Supervisor";
    public static readonly string[] All = [Operator, Supervisor];
}

/// <summary>Who is acting: a verified identity from the bank's identity provider, never a name from the request.</summary>
public sealed record OperatorIdentity(string Subject, string DisplayName, IReadOnlyDictionary<string, IReadOnlySet<string>> RolesByTenant)
{
    public bool Has(string tenant, string role) =>
        RolesByTenant.TryGetValue(tenant, out var roles) && (roles.Contains(role) || (role == OperatorRoles.Operator && roles.Contains(OperatorRoles.Supervisor)));

    public IEnumerable<string> Tenants => RolesByTenant.Keys;

    /// <summary>What the facts record as the actor: the identity provider's subject, never a display name alone.</summary>
    public string Actor => $"operator:{Subject}";
}

/// <summary>
/// Turns a verified principal into an operator identity. The runtime has no identity store: roles come from the bank's
/// claims or groups through a configurable mapping. This is the seam the runtime's own authentication library replaces
/// at merge (it gives the principal; this gives the roles).
/// </summary>
public interface IOperatorIdentityResolver
{
    OperatorIdentity? Resolve(ClaimsPrincipal user);
}

/// <summary>One rule of the role mapping: a claim value (exact, or a pattern with &lt;tenant&gt;) gives a role in a tenant.</summary>
public sealed record RoleRule(string ClaimType, string Value, string Role, string? Tenant = null);

/// <summary>
/// The role mapping (configuration <c>Operations:Roles</c>). The default reads claim <c>th_roles</c> values written
/// <c>&lt;tenant&gt;:&lt;role&gt;</c>, which the dev sign-in issues; a bank maps its own groups, for example
/// <c>{ ClaimType: "groups", Value: "PH-WPS-Ops", Tenant: "example-bank", Role: "Operator" }</c>.
/// </summary>
public sealed class RoleMapping(IEnumerable<RoleRule> rules) : IOperatorIdentityResolver
{
    readonly List<RoleRule> rules = rules.ToList();
    public static RoleMapping Default => new([new RoleRule("th_roles", "<tenant>:<role>", "<role>")]);

    public OperatorIdentity? Resolve(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var sub = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(sub)) return null;
        var name = user.FindFirst("name")?.Value ?? user.FindFirst("preferred_username")?.Value ?? user.Identity?.Name ?? sub;
        var roles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var r in rules)
            foreach (var c in user.FindAll(r.ClaimType))
            {
                string? tenant = r.Tenant, role = r.Role;
                if (r.Value.Contains('<'))
                {
                    var rx = new Regex("^" + Regex.Escape(r.Value).Replace("<tenant>", "(?<tenant>[a-z0-9-]+)").Replace("<role>", "(?<role>[A-Za-z]+)") + "$");
                    var m = rx.Match(c.Value);
                    if (!m.Success) continue;
                    if (m.Groups["tenant"].Success) tenant = m.Groups["tenant"].Value;
                    if (role == "<role>") role = m.Groups["role"].Value;
                }
                else if (c.Value != r.Value) continue;
                if (tenant is null || !OperatorRoles.All.Contains(role)) continue;
                (roles.TryGetValue(tenant, out var set) ? set : roles[tenant] = new HashSet<string>()).Add(role);
            }
        return new OperatorIdentity(sub, name, roles.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value));
    }
}
