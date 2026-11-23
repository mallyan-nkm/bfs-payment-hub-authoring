using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

/// <summary>
/// The reference tables a bank's configuration release installed (R-01-25, R-09-75), read where a host needs them (the native
/// host's reference adapters, R-09-90): the latest table stored for a domain, its edition in force on a day. The release API's
/// <c>GET /lookups/{domain}</c> answers from the same rows.
/// </summary>
public static class ReferenceTables
{
    public const string Kind = "reference";

    public static async Task<(string? Table, JsonArray? Rows)> RowsAt(StoreDbContext db, string bank, string domain, DateTimeOffset at, CancellationToken ct = default)
    {
        var row = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == Kind && v.Tenant == bank && v.Name == domain).OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct);
        if (row is null) return (null, null);
        var t = JsonNode.Parse(row.BodyJson)?["table"];
        var day = at.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var edition = (t?["editions"] as JsonArray ?? []).OfType<JsonObject>().Where(e => string.CompareOrdinal((string?)e["validFrom"], day) <= 0)
            .OrderBy(e => (string?)e["validFrom"], StringComparer.Ordinal).LastOrDefault();
        return ((string?)t?["name"], edition?["rows"]?.DeepClone() as JsonArray);
    }
}
