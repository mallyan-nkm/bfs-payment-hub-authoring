using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

/// <summary>
/// A bank's values in force, as the configuration releases put in force left them in the store (R-09-110, R-09-111): each value an
/// effective-dated version row of kind <c>config</c>, named by its parameter. Read by the release API's <c>GET /values</c>
/// (<c>BankValues</c>) and by the engine hosts at each step (R-09-135), so that both read one rule.
/// </summary>
public static class ConfigurationValues
{
    public const string Kind = "config";

    /// <summary>
    /// Every value in force for a tenant at a moment, knowing every version stored so far. A configuration set is the bank's whole
    /// set of periods for a parameter, so only the latest release that sets the parameter counts: a period it closed or dropped
    /// is not in force, whatever an earlier release said. Within it, the period that covers the moment.
    /// </summary>
    public static async Task<List<JsonObject>> InForce(StoreDbContext db, string tenant, DateTimeOffset at, CancellationToken ct = default)
    {
        var t = StoreTime.Of(at);
        var rows = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == Kind && v.Tenant == tenant).ToListAsync(ct);
        string ReleaseOf(VersionedValueRow r) => (string?)JsonNode.Parse(r.BodyJson)?["configurationSet"] ?? "0.0.0";
        var byRelease = Comparer<string>.Create(CompareReleases);
        var result = new List<JsonObject>();
        foreach (var g in rows.GroupBy(r => r.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var newest = g.Select(ReleaseOf).Max(byRelease)!;
            var r = g.Where(x => ReleaseOf(x) == newest && x.EffectiveFrom <= t && (x.EffectiveTo == null || x.EffectiveTo > t)).MaxBy(x => x.VersionNo);
            if (r is null) continue;
            result.Add(new JsonObject
            {
                ["parameter"] = r.Name, ["value"] = JsonNode.Parse(r.BodyJson)?["value"]?.DeepClone(), ["version"] = r.VersionNo,
                ["configurationSet"] = newest, ["effectiveFrom"] = r.EffectiveFrom.ToString("O", CultureInfo.InvariantCulture),
            });
        }
        return result;
    }

    public static int CompareReleases(string a, string b)
    {
        int[] P(string r) => r.Split('.').Select(x => int.TryParse(x, out var i) ? i : 0).ToArray();
        var (x, y) = (P(a), P(b));
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}
