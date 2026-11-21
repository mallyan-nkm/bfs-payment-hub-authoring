using YamlDotNet.RepresentationModel;

namespace TransactHub.Ports;

/// <summary>One port bound to one adapter for a tenant, from a moment on. Settings are the adapter's, as strings.</summary>
public sealed record PortBinding(string Tenant, string Port, string Adapter, DateTimeOffset ValidFrom, IReadOnlyDictionary<string, string> Settings)
{
    public string Setting(string key, string fallback) => Settings.TryGetValue(key, out var v) ? v : fallback;
    public string Required(string key) => Settings.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new PortBindingException($"The {Adapter} binding of {Port} for {Tenant} has no '{key}'");
}

public sealed class PortBindingException(string message) : Exception(message);

/// <summary>
/// A bank's port bindings, read from its configuration (`content-configurations/banks/&lt;bank&gt;/*.yaml`,
/// kind `port-bindings`). Bindings are effective-dated values: the one in force at a moment is the latest
/// whose `validFrom` is not after it, so switching a bank's core banking is a new binding, not a release.
/// </summary>
public sealed class PortBindings
{
    readonly List<PortBinding> all = new();
    public IReadOnlyList<PortBinding> All => all;

    public static PortBindings FromDirectory(string dir)
    {
        var p = new PortBindings();
        foreach (var f in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            p.Add(File.ReadAllText(f), f);
        return p;
    }

    public void Add(string yaml, string source = "(text)")
    {
        var ys = new YamlStream();
        ys.Load(new StringReader(yaml));
        if (ys.Documents.Count == 0 || ys.Documents[0].RootNode is not YamlMappingNode root) return;
        string? S(YamlMappingNode m, string k) => m.Children.TryGetValue(new YamlScalarNode(k), out var v) && v is YamlScalarNode s ? s.Value : null;
        if (S(root, "kind") != "port-bindings") return;
        var tenant = S(root, "tenant") ?? throw new PortBindingException($"{source}: port bindings name no tenant");
        if (!root.Children.TryGetValue(new YamlScalarNode("bindings"), out var list) || list is not YamlSequenceNode seq) return;
        foreach (var n in seq.Children.OfType<YamlMappingNode>())
        {
            var port = S(n, "port") ?? throw new PortBindingException($"{source}: a binding names no port");
            var adapter = S(n, "adapter") ?? throw new PortBindingException($"{source}: the binding of {port} names no adapter");
            var from = DateTimeOffset.Parse(S(n, "validFrom") ?? throw new PortBindingException($"{source}: the binding of {port} has no validFrom"), System.Globalization.CultureInfo.InvariantCulture);
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            if (n.Children.TryGetValue(new YamlScalarNode("settings"), out var st) && st is YamlMappingNode sm)
                foreach (var (k, v) in sm.Children) settings[((YamlScalarNode)k).Value!] = (v as YamlScalarNode)?.Value ?? "";
            if (all.Any(b => b.Tenant == tenant && b.Port == port && b.ValidFrom == from))
                throw new PortBindingException($"{source}: {port} for {tenant} is bound twice from {from:O}");
            all.Add(new PortBinding(tenant, port, adapter, from, settings));
        }
    }

    /// <summary>The binding in force for a tenant's port at a moment. A tenant is always given; there is no default bank.</summary>
    public PortBinding InForce(string tenant, string port, DateTimeOffset at) =>
        all.Where(b => b.Tenant == tenant && b.Port == port && b.ValidFrom <= at).MaxBy(b => b.ValidFrom)
        ?? throw new PortBindingException($"{port} is not bound for {tenant} at {at:O}");
}
