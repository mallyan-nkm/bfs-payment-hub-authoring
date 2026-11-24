using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The operations console's dials (R-05-66): parameters an operations view declares (<c>tatLimit</c>, <c>unmaskWindow</c>,
/// <c>retentionPeriod</c>), whose values a bank sets in its configuration release as <c>&lt;view&gt;#&lt;parameter&gt;</c>. The runtime
/// stores each value as an effective-dated version (kind <c>config</c>, BankValues); the value in force at a moment is the one
/// the latest configuration set that sets the parameter gives for it, else the view's default. No period is the runtime's own:
/// each default is P0D, which switches its mechanism off.
/// </summary>
public sealed class OperationsDials(StoreOptions store, ViewCatalog views)
{
    public const string ConfigKind = "config";
    public const string TatLimit = "tatLimit", UnmaskWindow = "unmaskWindow", RetentionPeriod = "retentionPeriod";
    /// <summary>The work queue's escalation after a time (R-05-107, Issue I-185): how long an item waits, and the role it goes to.</summary>
    public const string EscalateAfter = "escalateAfter", EscalateTo = "escalateTo";

    /// <summary>A dial's value in force for a tenant at a moment, and where it came from (the bank's value, or the view's default).</summary>
    public sealed record Dial(string Parameter, TimeSpan Value, string Source, string? ConfigurationSet);

    /// <summary>
    /// The value of a dial for a payment of a scheme: the scheme view's value where the bank set one, else the platform view's
    /// value or, where the bank set none, its default (R-05-68, R-05-70).
    /// </summary>
    public async Task<Dial> For(string tenant, string? scheme, string dial, DateTimeOffset at, CancellationToken ct = default)
    {
        var platform = views.All.FirstOrDefault(v => v.Scheme is null);
        var sv = scheme is null ? null : views.SchemeView(scheme);
        if (sv is not null && Declares(sv, dial) && await Set(tenant, $"{sv.Name}#{dial}", at, ct) is { } s) return s;
        if (platform is not null && Declares(platform, dial))
            return await Set(tenant, $"{platform.Name}#{dial}", at, ct) ?? new Dial($"{platform.Name}#{dial}", Default(platform, dial), "default", null);
        return new Dial(dial, TimeSpan.Zero, "undeclared", null);
    }

    static bool Declares(OperationsView v, string dial) => v.Doc["parameters"]?[dial] is JsonObject;

    /// <summary>A code dial of the platform's view (<c>escalateTo</c>): the bank's value in force, inside the view's allowed values, else its default.</summary>
    public async Task<(string? Value, string Source)> Code(string tenant, string dial, DateTimeOffset at, CancellationToken ct = default)
    {
        var platform = views.All.FirstOrDefault(v => v.Scheme is null);
        if (platform is null || !Declares(platform, dial)) return (null, "undeclared");
        var decl = (JsonObject)platform.Doc["parameters"]![dial]!;
        var allowed = (decl["bounds"]?["allowed"] as JsonArray ?? []).Select(x => (string?)x).ToList();
        if (await Raw(tenant, $"{platform.Name}#{dial}", at, ct) is { } bank && (string?)bank.value is { Length: > 0 } v && (allowed.Count == 0 || allowed.Contains(v)))
            return (v, "bank");
        return ((string?)decl["default"], "default");
    }

    static TimeSpan Default(OperationsView v, string dial) => Duration((string?)v.Doc["parameters"]?[dial]?["default"]) ?? TimeSpan.Zero;

    /// <summary>The bank's value of a parameter in force at a moment, as BankValues.InForce reads it, knowing every version stored so far: the latest set that sets it, the period that covers the moment.</summary>
    async Task<Dial?> Set(string tenant, string parameter, DateTimeOffset at, CancellationToken ct)
    {
        if (await Raw(tenant, parameter, at, ct) is not { } r || Duration((string?)r.value) is not { } d) return null;
        return new Dial(parameter, d, "bank", r.set);
    }

    /// <summary>The bank's value of a parameter in force at a moment, whatever its type, and the configuration set that gives it.</summary>
    async Task<(JsonNode? value, string set)?> Raw(string tenant, string parameter, DateTimeOffset at, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var t = StoreTime.Of(at);
        var rows = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == ConfigKind && v.Tenant == tenant && v.Name == parameter).ToListAsync(ct);
        if (rows.Count == 0) return null;
        string ReleaseOf(VersionedValueRow r) => (string?)JsonNode.Parse(r.BodyJson)?["configurationSet"] ?? "0.0.0";
        var newest = rows.Select(ReleaseOf).Max(Comparer<string>.Create(CompareReleases))!;
        var r = rows.Where(x => ReleaseOf(x) == newest && x.EffectiveFrom <= t && (x.EffectiveTo == null || x.EffectiveTo > t)).MaxBy(x => x.VersionNo);
        if (r is null) return null;
        return (JsonNode.Parse(r.BodyJson)?["value"]?.DeepClone(), newest);
    }

    public static TimeSpan? Duration(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        try { return XmlConvert.ToTimeSpan(iso); } catch (FormatException) { return null; }
    }

    public static string Iso(TimeSpan t) => XmlConvert.ToString(t);

    static int CompareReleases(string a, string b)
    {
        int[] P(string r) => r.Split('.').Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0).ToArray();
        var (x, y) = (P(a), P(b));
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}
