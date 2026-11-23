using YamlDotNet.RepresentationModel;

namespace TransactHub.Ports;

/// <summary>One port bound to one adapter for a tenant, from a moment on. Settings are the adapter's, as strings.</summary>
/// <param name="Release">The configuration release that bound it, when the binding came from one (R-09-112); null from a folder.</param>
public sealed record PortBinding(string Tenant, string Port, string Adapter, DateTimeOffset ValidFrom, IReadOnlyDictionary<string, string> Settings, string? Release = null)
{
    public string Setting(string key, string fallback) => Settings.TryGetValue(key, out var v) ? v : fallback;
    public string Required(string key) => Settings.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new PortBindingException($"The {Adapter} binding of {Port} for {Tenant} has no '{key}'");
}

public sealed class PortBindingException(string message) : Exception(message);

/// <summary>
/// A bank's port bindings. Bindings are effective-dated values: the one in force at a moment is the latest whose `validFrom` is
/// not after it, so switching a bank's core banking is a new binding, not a release.
/// <para>
/// A runtime's hosts read them live (<see cref="Live"/>) from the bank's configuration releases in force, as of each attempt's
/// time (spec R-09-112): a binding a new configuration release brings takes effect with no restart. The latest configuration
/// release that binds a port is the bank's whole set of bindings for it, as for values. Read from a folder
/// (`content-configurations/banks/&lt;bank&gt;/*.yaml`, kind `port-bindings`, <see cref="FromDirectory"/>) only by a host that
/// serves no runtime's bank: the studio's test runs (R-09-113).
/// </para>
/// </summary>
public sealed class PortBindings
{
    List<PortBinding> all = new();
    readonly Func<IReadOnlyList<PortBinding>>? source;
    readonly TimeSpan refresh;
    readonly Func<DateTimeOffset> now;
    DateTimeOffset loadedAt = DateTimeOffset.MinValue;
    readonly object gate = new();

    public PortBindings() => (refresh, now) = (TimeSpan.Zero, () => DateTimeOffset.UtcNow);

    PortBindings(Func<IReadOnlyList<PortBinding>> source, TimeSpan refresh, Func<DateTimeOffset> now) => (this.source, this.refresh, this.now) = (source, refresh, now);

    /// <summary>
    /// Bindings read from <paramref name="source"/> (the store's configuration releases in force) at each use, at most once per
    /// <paramref name="refresh"/> (default two seconds). A binding that did not change keeps its instance, so an adapter built
    /// for it is built once; a changed one is a new binding and gets its own adapter.
    /// </summary>
    public static PortBindings Live(Func<IReadOnlyList<PortBinding>> source, TimeSpan? refresh = null, Func<DateTimeOffset>? now = null) =>
        new(source, refresh ?? TimeSpan.FromSeconds(2), now ?? (() => DateTimeOffset.UtcNow));

    /// <summary>Whether these bindings are read live from a source rather than given once.</summary>
    public bool IsLive => source is not null;

    public IReadOnlyList<PortBinding> All
    {
        get
        {
            Refresh();
            return all;
        }
    }

    void Refresh()
    {
        if (source is null) return;
        lock (gate)
        {
            var t = now();
            if (loadedAt != DateTimeOffset.MinValue && t - loadedAt < refresh) return;
            IReadOnlyList<PortBinding> fresh;
            try { fresh = source(); }
            catch (Exception e) when (e is not PortBindingException) { throw new PortBindingException($"The port bindings in force could not be read: {e.Message}"); }
            var had = all.GroupBy(Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            all = fresh.Select(b => had.TryGetValue(Key(b), out var same) ? same : b).ToList();
            loadedAt = t;
        }
    }

    static string Key(PortBinding b) =>
        $"{b.Tenant}\n{b.Port}\n{b.Adapter}\n{b.ValidFrom:O}\n{b.Release}\n{string.Join("\n", b.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value))}";

    public static PortBindings FromDirectory(string dir)
    {
        var p = new PortBindings();
        foreach (var f in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            p.Add(File.ReadAllText(f), f);
        return p;
    }

    public void Add(string yaml, string source = "(text)")
    {
        if (this.source is not null) throw new InvalidOperationException("Bindings read live from the configuration releases in force are not added to by hand");
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

    /// <summary>
    /// The binding in force for a tenant's port at a moment. A tenant is always given; there is no default bank. Of bindings from
    /// configuration releases, only the latest release that binds the port counts.
    /// </summary>
    public PortBinding InForce(string tenant, string port, DateTimeOffset at)
    {
        var mine = All.Where(b => b.Tenant == tenant && b.Port == port).ToList();
        var newest = mine.Count == 0 ? null : mine.Select(b => b.Release ?? "").Max(Comparer<string>.Create(CompareReleases));
        return mine.Where(b => (b.Release ?? "") == newest && b.ValidFrom <= at).MaxBy(b => b.ValidFrom)
            ?? throw new PortBindingException($"{port} is not bound for {tenant} at {at:O}");
    }

    /// <summary>Releases by their numbers (<c>major.minor.patch</c>, then build metadata), as the store orders them.</summary>
    public static int CompareReleases(string? a, string? b)
    {
        static int[] P(string? r) => (r ?? "").Split(['.', '+', '-'], StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var i) ? i : -1).Where(i => i >= 0).ToArray();
        var (x, y) = (P(a), P(b));
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : -1).CompareTo(i < y.Length ? y[i] : -1);
            if (c != 0) return c;
        }
        return string.CompareOrdinal(a, b);
    }
}
