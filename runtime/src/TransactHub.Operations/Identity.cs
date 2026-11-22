using System.Security.Claims;

namespace TransactHub.Operations;

/// <summary>The two operator roles (R-06-24). A supervisor may do what an operator may, and is the second approver.</summary>
public static class OperatorRoles
{
    public const string Operator = "Operator";
    public const string Supervisor = "Supervisor";
    public static readonly string[] All = [Operator, Supervisor];
}

/// <summary>
/// Who is acting: a verified identity from the bank's identity provider, never a name from the request. A tenant key
/// <c>*</c> stands for every tenant (the bank's role map may grant that).
/// </summary>
public sealed record OperatorIdentity(string Subject, string DisplayName, IReadOnlyDictionary<string, IReadOnlySet<string>> RolesByTenant)
{
    public const string AnyTenant = "*";

    public bool Has(string tenant, string role) => Holds(tenant, role) || Holds(AnyTenant, role);

    bool Holds(string tenant, string role) =>
        RolesByTenant.TryGetValue(tenant, out var roles) && (roles.Contains(role) || (role == OperatorRoles.Operator && roles.Contains(OperatorRoles.Supervisor)));

    public IEnumerable<string> Tenants => RolesByTenant.Keys;

    public bool AllTenants => RolesByTenant.ContainsKey(AnyTenant);

    /// <summary>What the facts record as the actor: the identity provider's subject, never a display name alone.</summary>
    public string Actor => $"operator:{Subject}";
}

/// <summary>
/// Turns the principal the runtime's authentication gives (TransactHub.Auth: the bank's token, its role map applied) into an
/// operator identity. The runtime has no identity store.
/// </summary>
public interface IOperatorIdentityResolver
{
    OperatorIdentity? Resolve(ClaimsPrincipal user);
}
