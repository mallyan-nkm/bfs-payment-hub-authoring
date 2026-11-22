using System.Collections.Concurrent;
using TransactHub.Activities.Native;
using TransactHub.Ports;

namespace TransactHub.Activities.Platform.Posting;

/// <summary>
/// The core banking a tenant's payment talks to at a moment (plan W6, P2): the bank's effective-dated binding of
/// <c>p.port.core-banking.v1</c> in force at the engine's time, built into its adapter once per binding. A tenant with no
/// binding in force is refused, never defaulted to another bank.
/// </summary>
public sealed class CoreBankingResolver
{
    public const string Port = "p.port.core-banking.v1";
    readonly PortBindings bindings;
    readonly Func<PortBinding, HttpClient> http;
    readonly TransactHub.Security.ISecretResolver? secrets;
    readonly ConcurrentDictionary<PortBinding, ICoreBanking> adapters = new(ReferenceEqualityComparer.Instance);

    /// <param name="secrets">Where a binding's <c>secretRef</c> is read (R-06-22); the host's environment, files and Vault by default.</param>
    public CoreBankingResolver(PortBindings bindings, Func<PortBinding, HttpClient>? http = null, TransactHub.Security.ISecretResolver? secrets = null)
    {
        this.bindings = bindings;
        this.http = http ?? (_ => new HttpClient());
        this.secrets = secrets;
    }

    /// <summary>Every bank folder under <paramref name="banksDir"/> (content-configurations/banks): each one's port bindings.</summary>
    public static PortBindings ReadBanks(string banksDir)
    {
        var all = new PortBindings();
        foreach (var dir in Directory.EnumerateDirectories(banksDir).Order(StringComparer.Ordinal))
            foreach (var f in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
                all.Add(File.ReadAllText(f), f);
        return all;
    }

    /// <summary>The adapters built so far, with the binding each serves (the native host reports what the simulator adapter took).</summary>
    public IEnumerable<(PortBinding Binding, ICoreBanking CoreBanking)> Built => adapters.Select(x => (x.Key, x.Value));

    public PortBinding BindingFor(string? tenant, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(tenant)) throw new NativeFailure("NoTenant", "The attempt names no tenant; core banking is bound per tenant and never defaulted", false);
        try { return bindings.InForce(tenant, Port, at); }
        catch (PortBindingException e) { throw new NativeFailure("PortNotBound", e.Message, false); }
    }

    public (ICoreBanking CoreBanking, PortBinding Binding) For(string? tenant, DateTimeOffset at)
    {
        var b = BindingFor(tenant, at);
        try { return (adapters.GetOrAdd(b, x => CoreBankingAdapters.Create(x, http(x), secrets)), b); }
        catch (PortBindingException e) { throw new NativeFailure("PortNotBound", e.Message, false); }
    }
}
