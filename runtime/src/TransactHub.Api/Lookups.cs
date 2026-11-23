using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Api;

/// <summary>
/// The reference tables of a bank's installed configuration (R-01-25, R-09-75): the participant directory, the emirates, free zones,
/// purpose codes, the business calendar and the bank's branches, as the studio shipped them in a configuration release. Each table
/// is stored insert-only, effective-dated, under its domain; <c>GET /lookups/{domain}</c> answers the rows of the edition in force
/// on a date, from the latest configuration release that carries the table. Market and bank reference data only: bank customer
/// data is in the registers.
/// </summary>
public static class Lookups
{
    public const string Kind = "reference";

    /// <summary>Stores the reference tables of a configuration release: one version per table, from its first edition.</summary>
    public static async Task<int> Import(StoreDbContext db, TimeProvider clock, string bank, string release, IEnumerable<JsonObject> tables, string actor, CancellationToken ct = default)
    {
        var store = new VersionedValues(db, clock);
        var n = 0;
        foreach (var t in tables)
        {
            var domain = (string?)t["domain"] ?? (string?)t["name"];
            if (domain is null) continue;
            var first = (t["editions"] as JsonArray ?? []).Select(e => (string?)e?["validFrom"]).Where(x => x is not null).Order(StringComparer.Ordinal).FirstOrDefault();
            await store.Publish(Kind, bank, domain, new JsonObject { ["table"] = t.DeepClone(), ["configurationRelease"] = release }, first is null ? DateTimeOffset.UnixEpoch : BankValues.DayOf(first), null, actor, ct);
            n++;
        }
        return n;
    }

    /// <summary>The domains a bank's configuration holds, with each table's name and release.</summary>
    public static async Task<List<JsonObject>> Domains(StoreDbContext db, string bank, CancellationToken ct = default)
    {
        var rows = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == Kind && v.Tenant == bank).ToListAsync(ct);
        return rows.GroupBy(r => r.Name).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var t = JsonNode.Parse(g.MaxBy(r => r.VersionNo)!.BodyJson)!["table"]!;
            return new JsonObject { ["domain"] = g.Key, ["table"] = (string?)t["name"], ["release"] = (string?)t["release"], ["title"] = (string?)t["title"] };
        }).ToList();
    }

    /// <summary>The rows of a domain's table in force at a date: the latest table stored, its latest edition not after the date.</summary>
    public static async Task<JsonObject?> At(StoreDbContext db, string bank, string domain, DateTimeOffset at, CancellationToken ct = default)
    {
        var row = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == Kind && v.Tenant == bank && v.Name == domain).OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var body = JsonNode.Parse(row.BodyJson)!;
        var t = body["table"]!;
        var day = at.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var edition = (t["editions"] as JsonArray ?? []).OfType<JsonObject>().Where(e => string.CompareOrdinal((string?)e["validFrom"], day) <= 0)
            .OrderBy(e => (string?)e["validFrom"], StringComparer.Ordinal).LastOrDefault();
        return new JsonObject
        {
            ["domain"] = domain, ["table"] = (string?)t["name"], ["release"] = (string?)t["release"], ["configurationRelease"] = (string?)body["configurationRelease"],
            ["asOf"] = day, ["validFrom"] = (string?)edition?["validFrom"], ["key"] = t["key"]?.DeepClone(), ["columns"] = t["columns"]?.DeepClone(),
            ["rows"] = edition?["rows"]?.DeepClone() ?? new JsonArray(),
        };
    }
}
