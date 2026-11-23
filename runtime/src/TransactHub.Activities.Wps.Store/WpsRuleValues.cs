using System.Text.Json.Nodes;
using TransactHub.Activities.Wps.Rules;
using TransactHub.Store;

namespace TransactHub.Activities.Wps;

/// <summary>
/// A bank's values for the WPS salary-file rules (plan W6): configuration values in the store, effective-dated,
/// read as of the payment's date. Nothing has a code default: a value that is missing, malformed or out of range
/// is reported by name and the file is not processed, as the source did. The parameter names are the source's,
/// so a bank's existing values carry over; the routing code is the one neutral BankRoutingCode.
/// </summary>
public static class WpsRuleValues
{
    public const string Kind = "config";
    public const string Prefix = "uae.wps.";

    public static IReadOnlyList<string> Parameters { get; } = [.. CbxRuleSettings.ParamNames.All, CbxScrRules.BankRoutingCodeParam];

    public sealed record Values(CbxRuleSettings Settings, string BankRoutingCode);

    public static async Task<(Values? Values, string? Invalid)> Load(VersionedValues store, string tenant, DateTimeOffset asOf, CancellationToken ct = default)
    {
        var raw = new Dictionary<string, string?>();
        foreach (var p in Parameters)
        {
            var v = await store.InForce(Kind, tenant, Prefix + p, asOf, ct);
            raw[p] = v is null ? null : JsonNode.Parse(v.BodyJson)?["value"]?.ToString();
        }
        var (settings, invalid) = CbxRuleSettings.FromParameters(raw);
        if (settings is null) return (null, invalid);
        var code = raw[CbxScrRules.BankRoutingCodeParam]?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length != 9 || !code.All(char.IsAsciiDigit)) return (null, CbxScrRules.BankRoutingCodeParam);
        return (new Values(settings, code), null);
    }

    /// <summary>Publish a bank's configuration set (as content-configurations holds it): one version per value.</summary>
    public static async Task Publish(VersionedValues store, string tenant, IReadOnlyDictionary<string, string> values, DateTimeOffset effectiveFrom, string actor, CancellationToken ct = default)
    {
        foreach (var (k, v) in values.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!Parameters.Contains(k)) throw new ArgumentException($"{k} is not a WPS rule parameter");
            await store.Publish(Kind, tenant, Prefix + k, new JsonObject { ["value"] = v }, effectiveFrom, null, actor, ct);
        }
    }
}
