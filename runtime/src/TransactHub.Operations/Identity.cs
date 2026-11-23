using System.Security.Claims;

namespace TransactHub.Operations;

/// <summary>
/// The people's roles of the operations console (R-06-24, R-06-29). A supervisor may do what an operator may, and is the second
/// approver of a payment action. A register maker drafts changes to the bank's registers; the bank approver consents to them, and
/// a change takes effect only then (owner's decision of 23 November 2026: any action that updates the system needs the bank
/// approver's consent, a maker plus a different approver). Each may read the registers.
/// </summary>
public static class OperatorRoles
{
    public const string Operator = "Operator";
    public const string Supervisor = "Supervisor";
    public const string RegisterMaker = "RegisterMaker";
    public const string BankApprover = "BankApprover";
    public static readonly string[] All = [Operator, Supervisor, RegisterMaker, BankApprover];
    /// <summary>Who may read the registers.</summary>
    public static readonly string[] RegisterReaders = [Operator, Supervisor, RegisterMaker, BankApprover];
}

/// <summary>
/// The machine callers' roles (R-09-73, R-09-74): system clients of the bank's identity provider, by the client credentials
/// grant, given these roles per tenant by the bank's role map. <c>register-client</c>: core banking's register API and bulk sync
/// (its changes are drafts that need the bank approver's consent). <c>channel-enquiry</c>: a channel's status and search enquiries.
/// </summary>
public static class SystemRoles
{
    public const string RegisterClient = "register-client";
    public const string ChannelEnquiry = "channel-enquiry";
    public static readonly string[] All = [RegisterClient, ChannelEnquiry];
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

    /// <summary>Whether this person may consent to a change of the system (R-09-77): a bank approver of any tenant the runtime serves.</summary>
    public bool MayConsent => RolesByTenant.Values.Any(r => r.Contains(OperatorRoles.BankApprover));

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
