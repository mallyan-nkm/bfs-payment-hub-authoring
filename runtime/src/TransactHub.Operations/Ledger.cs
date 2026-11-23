using System.Text.Json.Nodes;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The bank's own ledger accounts, read-only, for supervisors (R-05-91; owner's decision of 23 November 2026, docs/OWNER-ACTIONS.md
/// A3, Issue I-184): the settlement (nostro), suspense, charge income and VAT accounts a payment posts against, as the bank's
/// configuration release in force names them, each the value of an accounting activity's parameter (<c>p.post.accounting.v1</c>,
/// <c>p.post.accounting.v2</c>, a scheme's return posting). These are bank settings: they are changed only in the studio, as a new
/// configuration release, and never offered to a channel. An account the bank has not set is shown as not set, never guessed.
/// </summary>
public sealed class LedgerAccounts(StoreOptions store, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>The kinds of ledger account, each the parameter that names it.</summary>
    public static readonly (string Kind, string Parameter, string Title)[] Kinds =
    [
        ("settlement", "settlementAccount", "Settlement (nostro)"),
        ("suspense", "suspenseAccount", "Suspense"),
        ("income", "chargeIncomeAccount", "Charge income"),
        ("vat", "vatAccount", "VAT"),
    ];

    /// <summary>The accounting activities whose four accounts are always listed, set or not.</summary>
    public static readonly string[] Templates = ["p.post.accounting.v1", "p.post.accounting.v2"];

    public async Task<JsonObject> For(string tenant, CancellationToken ct = default)
    {
        var at = clock.GetUtcNow();
        await using var db = StoreDbContext.Open(store);
        var values = await ConfigurationValues.InForce(db, tenant, at, ct);
        var byName = values.ToDictionary(v => (string)v["parameter"]!, v => v);
        var rows = new JsonArray();
        var activities = values.Select(v => (string)v["parameter"]!).Where(p => Kinds.Any(k => p.EndsWith("#" + k.Parameter, StringComparison.Ordinal)))
            .Select(p => p[..p.IndexOf('#')]).Concat(Templates).Distinct().Order(StringComparer.Ordinal).ToList();
        foreach (var activity in activities)
            foreach (var (kind, parameter, title) in Kinds)
            {
                var name = $"{activity}#{parameter}";
                byName.TryGetValue(name, out var v);
                // Only a value the configuration sets: an account is never shown from a default or a placeholder of the runtime's own.
                if (v is null && !Templates.Contains(activity)) continue;
                rows.Add(new JsonObject
                {
                    ["kind"] = kind, ["title"] = title, ["activity"] = activity, ["parameter"] = name,
                    ["account"] = v?["value"] is JsonValue s ? s.ToString() : null, ["set"] = v is not null,
                    ["configurationSet"] = (string?)v?["configurationSet"], ["effectiveFrom"] = (string?)v?["effectiveFrom"],
                });
            }
        return new JsonObject { ["tenant"] = tenant, ["asOf"] = at.ToString("O"), ["readOnly"] = true, ["accounts"] = rows };
    }
}
