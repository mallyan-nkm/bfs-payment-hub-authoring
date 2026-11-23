using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Platform.Native;

// FX and charges on an order (R-12-20, R-12-25; Issue I-136): the native code-behinds of p.enrich.fx-rate.v1 and
// p.enrich.charges.v1. Each gives what its script-behind gives (studio/scripts/p.enrich.fx-rate.v1.ts and
// p.enrich.charges.v1.ts), on decimal: the same lines, the same rounding (two decimals, half away from zero, per line), the same
// order totals (the sums of the rounded lines). The rates and the slabs are the bank's values, the attempt's parameters.

/// <summary>Exact money on decimal, as the rule language computes it (R-02-17, R-04-23).</summary>
public static class Money
{
    public static decimal Round2(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    /// <summary>A number or a decimal string, read by its text; anything else is null.</summary>
    public static decimal? Read(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<string>(out var s)) return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : null;
        if (v.TryGetValue<bool>(out _)) return null;
        // Any other number (a long, an int, a double, a JSON element): by its text, never by its binary value (R-02-17).
        return decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
    }

    /// <summary>A decimal as the scripts' JSON gives it: a number, without trailing zeros.</summary>
    public static JsonNode Json(decimal d) => JsonValue.Create(d / 1.000000000000000000000000000000000m)!;

    /// <summary>A list parameter, given as a list or as its JSON text.</summary>
    public static JsonArray List(JsonNode? p)
    {
        if (p is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0) p = JsonNode.Parse(text);
        return p as JsonArray ?? new JsonArray();
    }

    /// <summary>The order's lines: its items, or the payment as one line when it has none (with the order's own facts).</summary>
    public static (bool Lined, List<JsonObject> Lines) Lines(JsonObject view, Func<JsonObject, JsonObject> single)
    {
        var items = NativeContext.Items(view).OfType<JsonObject>().ToList();
        return items.Count > 0 ? (true, items) : (false, [single(view)]);
    }
}

/// <summary><c>p.enrich.fx-rate.v1</c>: each line converted from its instructed currency into its account's, at the bank's rate.</summary>
public sealed class EnrichFxRate : INativeActivity
{
    public string Name => "p.enrich.fx-rate.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var rates = Money.List(a.Params["rates"]).OfType<JsonObject>().ToList();
        (decimal rate, string source)? RateFor(string from, string to)
        {
            if (from == to) return (1m, "same-currency");
            var direct = rates.FirstOrDefault(r => NativeContext.S(r, "from") == from && NativeContext.S(r, "to") == to);
            if (direct is not null) return (Money.Read(direct["rate"]) ?? 0m, "bank");
            var inverse = rates.FirstOrDefault(r => NativeContext.S(r, "from") == to && NativeContext.S(r, "to") == from && (Money.Read(r["rate"]) ?? 0m) > 0m);
            if (inverse is not null) return (1m / Money.Read(inverse["rate"])!.Value, "bank-inverse");
            return null;
        }
        var (lined, items) = Money.Lines(a.View, v => new JsonObject { ["payment"] = v["payment"]?.DeepClone() ?? new JsonObject() });
        var lines = new List<JsonObject>();
        var missing = new List<string>();
        foreach (var item in items)
        {
            var amt = NativeContext.Node(item, "payment.instructedAmount");
            var from = NativeContext.S(amt, "currency");
            var acct = NativeContext.S(item, "payment.debtorAccount.currency");
            var to = acct.Length > 0 ? acct : from;
            if (RateFor(from, to) is not { } r)
            {
                if (!missing.Contains($"{from}/{to}")) missing.Add($"{from}/{to}");
                lines.Add(new JsonObject { ["outcome"] = "no-rate", ["from"] = from, ["to"] = to });
                continue;
            }
            var value = Money.Read(NativeContext.Node(amt, "value")) ?? 0m;
            lines.Add(new JsonObject
            {
                ["outcome"] = "converted", ["from"] = from, ["to"] = to, ["rate"] = Money.Json(r.rate), ["source"] = r.source,
                ["instructed"] = NativeContext.Node(amt, "value")?.DeepClone(), ["amount"] = Money.Json(Money.Round2(value * r.rate)),
            });
        }
        var writes = new JsonObject();
        if (lined) writes["items[]"] = WithFact(items, lines, "fx");
        var outcome = missing.Count > 0 ? "no-rate" : "converted";
        var fx = new JsonObject { ["outcome"] = outcome, ["result"] = outcome, ["lines"] = lines.Count };
        if (missing.Count > 0) fx["missing"] = string.Join(", ", missing);
        else
        {
            var currencies = lines.Select(l => NativeContext.S(l, "to")).Distinct().ToList();
            if (currencies.Count == 1)
            {
                fx["currency"] = currencies[0];
                fx["amount"] = Money.Json(lines.Sum(l => Money.Read(l["amount"]) ?? 0m));
                if (lines.Select(l => Money.Read(l["rate"])).Distinct().Count() == 1) fx["rate"] = lines[0]["rate"]!.DeepClone();
            }
        }
        writes["facts.fx"] = fx;
        return new NativeResult(outcome, null, writes);
    }

    /// <summary>The items with each line's fact added to its own facts, as the script writes the item list whole.</summary>
    internal static JsonArray WithFact(List<JsonObject> items, List<JsonObject> lines, string name) =>
        new(items.Select((item, i) =>
        {
            var copy = (JsonObject)item.DeepClone();
            var facts = copy["facts"] as JsonObject ?? new JsonObject();
            facts[name] = lines[i].DeepClone();
            copy["facts"] = facts;
            return (JsonNode?)copy;
        }).ToArray());
}

/// <summary><c>p.enrich.charges.v1</c>: each line priced on the bank's slab (fixed plus a percentage, floor, ceiling), then its VAT.</summary>
public sealed class EnrichCharges : INativeActivity
{
    public string Name => "p.enrich.charges.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var slabs = Money.List(a.Params["chargeSlabs"]).OfType<JsonObject>().Where(s => s["upTo"] is not null)
            .OrderBy(s => Money.Read(s["upTo"]) ?? 0m).ToList();
        var floor = Money.Read(a.Params["minCharge"]) ?? 0m;
        var ceiling = Money.Read(a.Params["maxCharge"]) ?? 0m;
        var vat = Money.Read(a.Params["vatPercent"]) ?? 0m;
        var (lined, items) = Money.Lines(a.View, v => new JsonObject
        {
            ["payment"] = v["payment"]?.DeepClone() ?? new JsonObject(),
            ["facts"] = new JsonObject { ["fx"] = NativeContext.Node(v, "facts.fx")?.DeepClone() },
        });
        var lines = new List<JsonObject>();
        var priced = 0;
        foreach (var item in items)
        {
            var fx = NativeContext.Node(item, "facts.fx");
            var amount = Money.Read(NativeContext.Node(fx, "amount")) ?? Money.Read(NativeContext.Node(item, "payment.instructedAmount.value")) ?? 0m;
            var to = NativeContext.S(fx, "to");
            var currency = to.Length > 0 ? to : NativeContext.S(item, "payment.instructedAmount.currency");
            var slab = slabs.FirstOrDefault(s => (Money.Read(s["upTo"]) ?? 0m) >= amount);
            if (slab is null)
            {
                lines.Add(new JsonObject
                {
                    ["outcome"] = "none", ["amount"] = 0, ["currency"] = currency, ["tax"] = new JsonObject { ["rate"] = Money.Json(vat), ["amount"] = 0 },
                    ["totalChargesAndTaxAmount"] = 0,
                });
                continue;
            }
            priced++;
            var b = (Money.Read(slab["fixed"]) ?? 0m) + amount * (Money.Read(slab["percent"]) ?? 0m) / 100m;
            if (floor > 0m && b < floor) b = floor;
            if (ceiling > 0m && b > ceiling) b = ceiling;
            var charge = Money.Round2(b);
            var tax = Money.Round2(charge * vat / 100m);
            lines.Add(new JsonObject
            {
                ["outcome"] = "priced", ["amount"] = Money.Json(charge), ["currency"] = currency, ["slab"] = slab["upTo"]!.DeepClone(),
                ["tax"] = new JsonObject { ["rate"] = Money.Json(vat), ["amount"] = Money.Json(tax) }, ["totalChargesAndTaxAmount"] = Money.Json(charge + tax),
            });
        }
        var writes = new JsonObject();
        if (lined) writes["items[]"] = EnrichFxRate.WithFact(items, lines, "charges");
        var outcome = priced > 0 ? "priced" : "none";
        var total = lines.Sum(l => Money.Read(l["amount"]) ?? 0m);
        var taxTotal = lines.Sum(l => Money.Read(NativeContext.Node(l, "tax.amount")) ?? 0m);
        var currencies = lines.Select(l => NativeContext.S(l, "currency")).Distinct().ToList();
        var charges = new JsonObject { ["outcome"] = outcome, ["result"] = outcome, ["lines"] = lines.Count, ["amount"] = Money.Json(total) };
        if (currencies.Count == 1) charges["currency"] = currencies[0];
        charges["tax"] = new JsonObject { ["rate"] = Money.Json(vat), ["amount"] = Money.Json(taxTotal) };
        charges["totalChargesAndTaxAmount"] = Money.Json(total + taxTotal);
        writes["facts.charges"] = charges;
        return new NativeResult(outcome, null, writes);
    }
}
