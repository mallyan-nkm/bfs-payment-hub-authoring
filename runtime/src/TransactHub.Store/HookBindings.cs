using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

/// <summary>
/// The bank's hook bindings on a runtime (R-06-13, R-09-217, Issue I-335): the <c>bindings</c> of the configuration set of each
/// configuration release put in force, kept as one effective-dated version (kind <c>hook-bindings</c>) per release when it is put in
/// force, with the release's number. A runtime's engine host reads the bindings in force when a payment's instance starts, from the
/// latest release (by its number) put in force by then; each binding's own <c>validFrom</c> and <c>validTo</c> the engine applies.
/// A hook a binding names runs from the distribution the payment is pinned to: the binding only says where.
/// </summary>
public static class HookBindings
{
    public const string Kind = "hook-bindings";
    const string Name = "hook-bindings";

    /// <summary>Records a configuration release's hook bindings as it is put in force (DistributionService only).</summary>
    public static Task<int> Publish(StoreDbContext db, TimeProvider clock, string bank, string release, JsonArray bindings, string actor, CancellationToken ct = default) =>
        new VersionedValues(db, clock).Publish(Kind, bank, Name, new JsonObject { ["bindings"] = bindings.DeepClone(), ["configurationRelease"] = release },
            DateTimeOffset.UnixEpoch, null, actor, ct);

    /// <summary>The hook bindings of the latest configuration release put in force for a bank by a moment; empty when none was.</summary>
    public static async Task<JsonArray> InForce(StoreDbContext db, string bank, DateTimeOffset at, CancellationToken ct = default)
    {
        var t = StoreTime.Of(at);
        var rows = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == Kind && v.Tenant == bank && v.Name == Name && v.PublishedAt <= t).ToListAsync(ct);
        var latest = rows.Select(r => JsonNode.Parse(r.BodyJson) as JsonObject).OfType<JsonObject>()
            .MaxBy(b => (string?)b["configurationRelease"] ?? "0.0.0", ReleaseOrder.Comparer);
        return latest?["bindings"]?.DeepClone() as JsonArray ?? [];
    }
}
