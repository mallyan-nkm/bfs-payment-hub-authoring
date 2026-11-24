using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// Whether the bank's exchange rates are stale (R-05-92; owner's decision of 23 November 2026, Issue I-189): the rates a payment's
/// <c>p.enrich.fx-rate.v1</c> reads now (the value <c>p.enrich.fx-rate.v1#rates</c> in force), each with the instant its feed gave
/// it (<c>asOf</c>), against the bank's staleness limit (<c>p.enrich.fx-rate.v1#maxRateAge</c> in force, else the activity's
/// default). A rate older than the limit is one a payment refuses (<c>fx.RateStale</c>), so the console warns "rates stale" for it.
/// A rate with no <c>asOf</c> is one the bank set by hand: the console lists it, but does not age it, as the activity does not,
/// unless the bank requires every rate's <c>asOf</c> (<c>p.enrich.fx-rate.v1#requireAsOf</c>, R-09-247, Issue I-278): then a payment
/// refuses it like a stale one (<c>fx.RateUndated</c>), and the console warns "rates stale" for it too.
/// </summary>
public sealed class FxRateStatus(StoreOptions store, TimeProvider? clock = null)
{
    public const string Rates = "p.enrich.fx-rate.v1#rates", Limit = "p.enrich.fx-rate.v1#maxRateAge", RequireAsOf = "p.enrich.fx-rate.v1#requireAsOf";
    /// <summary>The activity's declared default (spec/tools/build_factory.py), held equal by <c>FxRateStatusTests</c>.</summary>
    public const string DefaultLimit = "PT24H";
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<JsonObject> For(string tenant, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var db = StoreDbContext.Open(store);
        var values = await ConfigurationValues.InForce(db, tenant, now, ct);
        var rates = values.FirstOrDefault(v => (string?)v["parameter"] == Rates);
        var limitValue = values.FirstOrDefault(v => (string?)v["parameter"] == Limit);
        var limitText = (string?)limitValue?["value"] ?? DefaultLimit;
        var limit = OperationsDials.Duration(limitText) ?? OperationsDials.Duration(DefaultLimit)!.Value;
        var required = values.FirstOrDefault(v => (string?)v["parameter"] == RequireAsOf)?["value"] is JsonValue rv
            && (rv.TryGetValue<bool>(out var rb) ? rb : string.Equals(rv.ToString(), "true", StringComparison.OrdinalIgnoreCase));
        var undated = new List<string>();
        var rows = new JsonArray();
        var stale = new List<string>();
        DateTimeOffset? oldest = null;
        foreach (var r in rates?["value"] as JsonArray ?? [])
        {
            var pair = $"{(string?)r?["from"]}/{(string?)r?["to"]}";
            DateTimeOffset? asOf = (string?)r?["asOf"] is { } a && DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            var isStale = asOf is { } x && now - x > limit;
            if (isStale) stale.Add(pair);
            var isUndated = required && asOf is null;
            if (isUndated) undated.Add(pair);
            if (asOf is { } y && (oldest is null || y < oldest)) oldest = y;
            rows.Add(new JsonObject
            {
                ["pair"] = pair, ["rate"] = r?["rate"]?.DeepClone(), ["asOf"] = asOf?.ToString("O"), ["age"] = asOf is { } z ? OperationsDials.Iso(now - z) : null,
                ["stale"] = isStale || isUndated, ["standing"] = asOf is null && !required, ["undated"] = isUndated,
            });
        }
        return new JsonObject
        {
            ["tenant"] = tenant, ["at"] = now.ToString("O"), ["limit"] = limitText, ["limitSource"] = limitValue is null ? "default" : "bank",
            ["configurationSet"] = (string?)rates?["configurationSet"], ["since"] = (string?)rates?["effectiveFrom"],
            ["stale"] = stale.Count + undated.Count > 0, ["stalePairs"] = new JsonArray(stale.Concat(undated).Select(s => (JsonNode)s).ToArray()), ["oldest"] = oldest?.ToString("O"),
            ["requireAsOf"] = required, ["undatedPairs"] = new JsonArray(undated.Select(s => (JsonNode)s).ToArray()),
            ["rates"] = rows, ["code"] = stale.Count > 0 ? "fx.RateStale" : undated.Count > 0 ? "fx.RateUndated" : null,
        };
    }
}
