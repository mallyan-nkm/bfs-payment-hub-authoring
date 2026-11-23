using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

/// <summary>One port binding of a configuration release in force: what a host builds its adapter from.</summary>
/// <param name="Release">The configuration release that bound it.</param>
/// <param name="Settings">The adapter's settings as strings; a binding's <c>secretRef</c> is among them, a reference only.</param>
public sealed record StoredBinding(string Tenant, string Port, string Adapter, DateTimeOffset ValidFrom, IReadOnlyDictionary<string, string> Settings, string Release);

/// <summary>
/// The port bindings of a bank's configuration releases in force (spec R-09-112, docs/runtime/PACKAGING-AND-DEPLOYMENT.md
/// scenario 2). A configuration release's bindings become effective-dated versions (kind <c>binding</c>) only when it is put in
/// force, which needs a bank approver's consent outside the test environments (R-09-110, R-09-111), so every row here is in force.
/// A host reads them at each attempt, never once at its start: a binding with a later <c>validFrom</c>, or a new configuration
/// release, takes effect with no restart. The latest configuration release that binds a port is the bank's whole set of
/// bindings for it, as for values (<c>BankValues.InForce</c>); within it, the binding with the latest <c>validFrom</c> not after
/// the attempt's time.
/// </summary>
public static class ConfigurationBindings
{
    public const string Kind = "binding";

    /// <summary>Every binding of the bank's configuration releases in force (every bank's when <paramref name="bank"/> is null).</summary>
    public static List<StoredBinding> Load(StoreOptions store, string? bank)
    {
        using var db = StoreDbContext.Open(store);
        var rows = db.VersionedValues.AsNoTracking().Where(v => v.Kind == Kind && (bank == null || v.Tenant == bank)).OrderBy(v => v.Id).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<StoredBinding>();
        foreach (var r in rows)
        {
            if (Of(r) is not { } b) continue;
            // A release put in force twice (it is not, R-09-111) would give the same binding twice: one is enough.
            if (seen.Add($"{b.Tenant}\n{b.Port}\n{b.Release}\n{b.ValidFrom:O}\n{b.Adapter}\n{string.Join("\n", b.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value))}")) list.Add(b);
        }
        return list;
    }

    /// <summary>The binding a version row holds, or null when it holds none.</summary>
    public static StoredBinding? Of(VersionedValueRow r)
    {
        var body = JsonNode.Parse(r.BodyJson);
        if (body?["binding"] is not JsonObject b || (string?)b["adapter"] is not { Length: > 0 } adapter) return null;
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in b["settings"] as JsonObject ?? [])
            settings[k] = v switch { null => "", JsonValue jv when jv.TryGetValue<string>(out var s) => s, JsonValue jv => jv.ToJsonString(), _ => v.ToJsonString() };
        if ((string?)b["secretRef"] is { Length: > 0 } sr) settings["secretRef"] = sr;
        return new StoredBinding(r.Tenant, r.Name, adapter, r.EffectiveFrom.ToUniversalTime(), settings, (string?)body?["configurationRelease"] ?? "0.0.0");
    }

    /// <summary>The binding in force for a bank's port at a moment, from <paramref name="all"/>: see the class.</summary>
    public static StoredBinding? InForce(IEnumerable<StoredBinding> all, string bank, string port, DateTimeOffset at)
    {
        var mine = all.Where(b => b.Tenant == bank && b.Port == port).ToList();
        if (mine.Count == 0) return null;
        var newest = mine.Select(b => b.Release).Max(ReleaseOrder.Comparer)!;
        return mine.Where(b => b.Release == newest && b.ValidFrom <= at).MaxBy(b => b.ValidFrom);
    }

    /// <summary>The binding in force for a bank's port at a moment, read from the store now.</summary>
    public static async Task<StoredBinding?> InForce(StoreDbContext db, string bank, string port, DateTimeOffset at, CancellationToken ct = default)
    {
        var rows = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == Kind && v.Tenant == bank && v.Name == port).ToListAsync(ct);
        return InForce(rows.Select(Of).OfType<StoredBinding>(), bank, port, at);
    }
}

/// <summary>The order of releases (<c>major.minor.patch</c>, then any build metadata after <c>+</c>), by their numbers.</summary>
public static class ReleaseOrder
{
    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

    public static int Compare(string? a, string? b)
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
