using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>A message a person may upload a file of: one the active distribution's schemes receive (direction <c>in</c>).</summary>
public sealed record UploadMessage(string Id, string Title, string Scheme, string Kind);

/// <summary>What the upload page offers for one bank: the distribution active in the environment and the messages it receives.</summary>
public sealed record UploadTarget(string Tenant, string Environment, string? Distribution, IReadOnlyList<UploadMessage> Messages);

/// <summary>
/// The operations console's upload page (R-05-73, Issue I-201): the messages a person may put a file of through the File API,
/// read from the scheme manifests of the distribution active for the bank in the console's environment, as the File API's
/// front door routes them. A message is offered when its scheme says it comes in and what it does: it starts a payment
/// (<c>start</c>) or is delivered to a running one (<c>deliver</c>). With no active distribution, nothing is offered.
/// </summary>
public static class UploadCatalogue
{
    public static async Task<UploadTarget> Of(StoreOptions store, string bank, string environment, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var dist = await new DistributionRegistry(db).Active(bank, environment, ct);
        if (dist is null) return new UploadTarget(bank, environment, null, []);
        var (b, id) = DistributionRegistry.Split(dist);
        var row = await new ReleaseRegistry(db).Get(DistributionRegistry.Scope(b), id, ct);
        return new UploadTarget(bank, environment, dist, row is null ? [] : Messages(row.Package));
    }

    /// <summary>The messages the schemes of a package receive: the latest release of each scheme manifest, in id order.</summary>
    public static IReadOnlyList<UploadMessage> Messages(byte[] package)
    {
        var out_ = new List<UploadMessage>();
        foreach (var (scope, s) in Schemes(package))
            foreach (var m in (s["messages"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if ((string?)m["direction"] != "in" || m["disposition"] is not JsonObject d) continue;
                var kind = d["start"] is JsonObject ? "start" : d["deliver"] is JsonObject ? "deliver" : d["reconcile"] is JsonObject ? "reconcile" : null;
                if (kind is null || (string?)m["id"] is not { Length: > 0 } mid) continue;
                out_.Add(new UploadMessage(mid, (string?)m["title"] ?? mid, scope, kind));
            }
        return out_.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The activities of a package, by name: the latest release of each.</summary>
    public static IReadOnlyDictionary<string, JsonObject> Activities(byte[] package)
    {
        var acts = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".yaml", StringComparison.Ordinal)))
        {
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            var text = r.ReadToEnd();
            if (!text.Contains("kind: activity")) continue;
            JsonObject? doc;
            try { doc = Yaml.ToJson(text) as JsonObject; } catch { continue; }
            if ((string?)doc?["kind"] != "activity" || (string?)doc["name"] is not { Length: > 0 } name) continue;
            if (!acts.TryGetValue(name, out var had) || ReleaseOrder.Compare((string?)doc["release"], (string?)had["release"]) > 0) acts[name] = doc;
        }
        return acts;
    }

    /// <summary>The scheme manifests of a package, by scope: the latest release of each.</summary>
    public static IReadOnlyDictionary<string, JsonObject> Schemes(byte[] package)
    {
        var schemes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        using (var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        {
            foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".yaml", StringComparison.Ordinal)))
            {
                using var r = new StreamReader(e.Open(), Encoding.UTF8);
                var text = r.ReadToEnd();
                if (!text.Contains("kind: scheme")) continue;
                JsonObject? doc;
                try { doc = Yaml.ToJson(text) as JsonObject; } catch { continue; }
                if ((string?)doc?["kind"] != "scheme" || (string?)doc["scope"] is not { Length: > 0 } scope) continue;
                if (!schemes.TryGetValue(scope, out var had) || ReleaseOrder.Compare((string?)doc["release"], (string?)had["release"]) > 0) schemes[scope] = doc;
            }
        }
        return schemes;
    }
}
