using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Engines;
using TransactHub.Store;
using YamlDotNet.Serialization;

namespace TransactHub.EngineHost;

/// <summary>
/// A host's plan built from the stored, verified package (R-09-94, Issue I-138 (3)), so that no caller writes one: the distribution
/// holds an engine package per platform workflow (<c>engine/&lt;workflow&gt;/</c>), each with its expanded documents, its engine
/// binding (the queues) and, for Flowable, its BPMN. The plan holds one per platform workflow under <c>plans</c>. The workers the
/// queues are served by are the installation's: <c>TH_HOST_WORKERS</c>, a JSON object of queue to URL (or
/// <c>TH_HOST_WORKER_&lt;QUEUE&gt;</c> per queue, the queue's dashes as underscores).
/// </summary>
public static class Plans
{
    static readonly string[] Kinds = ["workflow", "activity", "rule", "ruleset", "table", "port", "extension", "hook", "scheme", "reference-table", "error-dictionary", "operations-view"];

    static JsonNode? Yaml(string text)
    {
        var o = new DeserializerBuilder().Build().Deserialize<object>(new StringReader(text));
        return o is null ? null : JsonNode.Parse(new SerializerBuilder().JsonCompatible().Build().Serialize(o));
    }

    /// <summary>The worker of a queue at this installation, or null.</summary>
    public static string? WorkerOf(string queue)
    {
        if (System.Environment.GetEnvironmentVariable("TH_HOST_WORKERS") is { Length: > 0 } all && JsonNode.Parse(all) is JsonObject o && (string?)o[queue] is string u) return u;
        return System.Environment.GetEnvironmentVariable($"TH_HOST_WORKER_{queue.Replace('-', '_').ToUpperInvariant()}") is { Length: > 0 } one ? one : null;
    }

    public static JsonObject FromPackage(byte[] zip, string distribution, string bank)
    {
        using var a = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var plans = new JsonObject();
        foreach (var group in a.Entries.Where(e => e.FullName.StartsWith("engine/", StringComparison.Ordinal)).GroupBy(e => e.FullName.Split('/')[1]).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var wf = group.Key;
            var documents = new JsonObject();
            var bpmn = new JsonObject();
            JsonObject? binding = null;
            foreach (var e in group.OrderBy(x => x.FullName, StringComparer.Ordinal))
            {
                var rel = e.FullName[$"engine/{wf}/".Length..];
                using var r = new StreamReader(e.Open(), Encoding.UTF8);
                var text = r.ReadToEnd();
                if (rel.EndsWith(".bpmn20.xml", StringComparison.Ordinal)) { bpmn[Path.GetFileName(rel)] = text; continue; }
                if (!rel.EndsWith(".yaml", StringComparison.Ordinal)) continue;
                if (rel == "binding.yaml") { binding = Yaml(text) as JsonObject; continue; }
                if (Yaml(text) is not JsonObject doc || !Kinds.Contains(J.S(doc, "kind") ?? "")) continue;
                if (rel.StartsWith("tests/", StringComparison.Ordinal) || rel.StartsWith("simulators/", StringComparison.Ordinal)) continue;
                documents[$"{J.S(doc, "name") ?? J.S(doc, "scope")}@{J.S(doc, "release")}"] = doc;
            }
            var queues = J.P(binding, "queues") as JsonObject ?? new JsonObject();
            var workers = new JsonObject();
            foreach (var q in queues.Select(kv => (string?)kv.Value).Where(q => q is not null).Distinct())
                if (WorkerOf(q!) is string url) workers[q!] = url;
            plans[wf] = new JsonObject
            {
                ["built"] = new JsonObject
                {
                    ["id"] = distribution, ["spec"] = new JsonObject(), ["workflow"] = wf,
                    ["config"] = new JsonObject { ["evaluationDate"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), ["tenant"] = bank, ["values"] = new JsonArray() },
                    ["context"] = new JsonObject(),
                },
                ["documents"] = documents, ["queues"] = queues.DeepClone(), ["workers"] = workers,
                ["chunkSize"] = J.P(binding, "chunkSize")?.DeepClone() ?? 100, ["itemConcurrency"] = J.P(binding, "forEachConcurrency")?.DeepClone() ?? 1,
                ["bpmn"] = bpmn.Count > 0 ? bpmn : null,
            };
        }
        if (plans.Count == 0) throw new InvalidOperationException($"{distribution}'s package holds no engine package (engine/<workflow>/)");
        return new JsonObject { ["distribution"] = distribution, ["from"] = "store", ["plans"] = plans };
    }

    /// <summary>The plan of a stored distribution, from the store's verified package (the release API verified it when it stored it).</summary>
    public static async Task<JsonObject?> FromStore(StoreOptions store, string distribution, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var (bank, id) = DistributionRegistry.Split(distribution);
        var row = await new ReleaseRegistry(db).Get(DistributionRegistry.Scope(bank), id, ct);
        return row is null ? null : FromPackage(row.Package, distribution, bank);
    }
}
