using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace TransactHub.Api;

/// <summary>
/// A bank's configuration values on the runtime (plan W4, P6): every value of the configuration set of a signed configuration
/// release put in force (R-09-51, R-09-110, R-09-111) is an effective-dated version row, kind <c>config</c>, named by its
/// parameter. Values never come from a folder (Issue I-210). A changed value is a new version, never an edit, and
/// reaches the next lookup with no deployment. The value in force at a moment is the one the latest configuration set gives for it.
/// </summary>
public static class BankValues
{
    public const string Kind = "config";

    /// <summary>
    /// A configuration set's text, of a configuration release being put in force (R-09-51, R-09-111): each value an
    /// effective-dated version. Only <c>DistributionService</c> calls it, after the release's signature and bounds were checked.
    /// </summary>
    public static async Task<int> ImportText(StoreDbContext db, TimeProvider clock, string yaml, string actor, CancellationToken ct = default)
    {
        var (release, root) = Read(yaml);
        return root is null ? 0 : await ImportSet(db, new VersionedValues(db, clock), release, root, actor, ct);
    }

    static async Task<int> ImportSet(StoreDbContext db, VersionedValues store, string release, YamlMappingNode root, string actor, CancellationToken ct)
    {
        var added = 0;
        var tenant = S(root, "tenant");
        if (tenant is null || !root.Children.TryGetValue(new YamlScalarNode("values"), out var vs) || vs is not YamlSequenceNode seq) return 0;
        foreach (var v in seq.Children.OfType<YamlMappingNode>())
        {
            var name = S(v, "parameter");
            var from = S(v, "validFrom");
            if (name is null || from is null || !v.Children.TryGetValue(new YamlScalarNode("value"), out var value)) continue;
            var effectiveFrom = Day(from);
            DateTimeOffset? effectiveTo = S(v, "validTo") is { } to ? Day(to).AddDays(1) : null;
            var body = new JsonObject { ["value"] = Json(value), ["configurationSet"] = release };
            var canon = CanonicalJson.Of(body);
            var start = StoreTime.Of(effectiveFrom);
            DateTimeOffset? end = effectiveTo is { } e ? StoreTime.Of(e) : null;
            var same = await db.VersionedValues.AsNoTracking().Where(x => x.Kind == Kind && x.Tenant == tenant && x.Name == name && x.EffectiveFrom == start && x.EffectiveTo == end)
                .OrderByDescending(x => x.VersionNo).Select(x => x.BodyJson).FirstOrDefaultAsync(ct);
            if (same == canon) continue;
            await store.Publish(Kind, tenant, name, body, effectiveFrom, effectiveTo, actor, ct);
            added++;
        }
        return added;
    }

    /// <summary>The values a configuration set's text holds, for the parameter contract check: parameter and value.</summary>
    public static List<(string parameter, JsonNode? value)> ValuesOf(string yaml)
    {
        var (_, root) = Read(yaml);
        if (root is null || !root.Children.TryGetValue(new YamlScalarNode("values"), out var vs) || vs is not YamlSequenceNode seq) return [];
        return seq.Children.OfType<YamlMappingNode>().Where(v => S(v, "parameter") is not null && v.Children.ContainsKey(new YamlScalarNode("value")))
            .Select(v => (S(v, "parameter")!, Json(v.Children[new YamlScalarNode("value")]))).ToList();
    }

    /// <summary>The day a value starts, as a configuration set writes it.</summary>
    public static DateTimeOffset DayOf(string d) => Day(d);

    /// <summary>A YAML node as JSON, as a configuration set's values are read.</summary>
    public static JsonNode? JsonOf(YamlNode n) => Json(n);

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

    static (string release, YamlMappingNode? root) Read(string text)
    {
        var ys = new YamlStream();
        ys.Load(new StringReader(text));
        var root = ys.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;
        return root is not null && S(root, "kind") == "configuration-set" ? (S(root, "release") ?? "0.0.0", root) : ("0.0.0", null);
    }

    static string? S(YamlMappingNode m, string k) => m.Children.TryGetValue(new YamlScalarNode(k), out var v) && v is YamlScalarNode s ? s.Value : null;

    static DateTimeOffset Day(string d) => DateTimeOffset.Parse(d.Length == 10 ? d + "T00:00:00Z" : d, CultureInfo.InvariantCulture);

    static JsonNode? Json(YamlNode n) => n switch
    {
        YamlSequenceNode s => new JsonArray(s.Children.Select(Json).ToArray()),
        YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value!, Json(kv.Value)))),
        YamlScalarNode { Style: not ScalarStyle.Plain } q => JsonValue.Create(q.Value),
        YamlScalarNode p when decimal.TryParse(p.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
        YamlScalarNode p when p.Value is "true" or "false" => JsonValue.Create(p.Value == "true"),
        YamlScalarNode p => JsonValue.Create(p.Value),
        _ => null,
    };

    static int CompareReleases(string a, string b)
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
