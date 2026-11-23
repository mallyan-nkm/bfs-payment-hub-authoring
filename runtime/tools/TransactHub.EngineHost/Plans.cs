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

    /// <summary>
    /// A YAML document as JSON with its scalars typed as YAML 1.2's core schema types them (a plain <c>3</c> a number, <c>true</c> a
    /// boolean, <c>null</c> or <c>~</c> null, a quoted scalar a string), as the studio reads it: the interpreter reads a policy's
    /// <c>maxAttempts</c> as a number and an outcome's <c>primary</c> as a boolean. Every scalar a string, a plan from the stored
    /// package failed at the first step with a retry policy (found by the laptop test, R-09-105).
    /// </summary>
    public static JsonNode? Yaml(string text)
    {
        var ys = new YamlDotNet.RepresentationModel.YamlStream();
        ys.Load(new StringReader(text));
        return ys.Documents.Count == 0 ? null : Typed(ys.Documents[0].RootNode);
    }

    static JsonNode? Typed(YamlDotNet.RepresentationModel.YamlNode n) => n switch
    {
        YamlDotNet.RepresentationModel.YamlSequenceNode s => new JsonArray(s.Children.Select(Typed).ToArray()),
        YamlDotNet.RepresentationModel.YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create(((YamlDotNet.RepresentationModel.YamlScalarNode)kv.Key).Value!, Typed(kv.Value)))),
        YamlDotNet.RepresentationModel.YamlScalarNode { Style: not YamlDotNet.Core.ScalarStyle.Plain } q => JsonValue.Create(q.Value),
        YamlDotNet.RepresentationModel.YamlScalarNode { Value: null or "~" or "null" or "Null" or "NULL" or "" } => null,
        YamlDotNet.RepresentationModel.YamlScalarNode { Value: "true" or "True" or "TRUE" } => JsonValue.Create(true),
        YamlDotNet.RepresentationModel.YamlScalarNode { Value: "false" or "False" or "FALSE" } => JsonValue.Create(false),
        YamlDotNet.RepresentationModel.YamlScalarNode p when System.Text.RegularExpressions.Regex.IsMatch(p.Value!, @"^[-+]?[0-9]+$") && long.TryParse(p.Value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
        YamlDotNet.RepresentationModel.YamlScalarNode p when System.Text.RegularExpressions.Regex.IsMatch(p.Value!, @"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$") && double.TryParse(p.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
        YamlDotNet.RepresentationModel.YamlScalarNode p => JsonValue.Create(p.Value),
        _ => null,
    };

    /// <summary>
    /// The worker of a queue at this installation, or null. Besides one URL per queue, an installation whose workers are per
    /// distribution names a template, <c>TH_HOST_WORKERS_URL</c>, with <c>{distribution}</c>, <c>{workflow}</c> and <c>{queue}</c>
    /// (each escaped for a path): the laptop's workers service, which runs each distribution's own workers (R-09-105).
    /// </summary>
    public static string? WorkerOf(string queue, string? distribution = null, string? workflow = null)
    {
        if (System.Environment.GetEnvironmentVariable("TH_HOST_WORKERS") is { Length: > 0 } all && JsonNode.Parse(all) is JsonObject o && (string?)o[queue] is string u) return u;
        if (System.Environment.GetEnvironmentVariable($"TH_HOST_WORKER_{queue.Replace('-', '_').ToUpperInvariant()}") is { Length: > 0 } one) return one;
        if (System.Environment.GetEnvironmentVariable("TH_HOST_WORKERS_URL") is { Length: > 0 } template && distribution is not null)
            return template.Replace("{distribution}", Uri.EscapeDataString(distribution)).Replace("{workflow}", Uri.EscapeDataString(workflow ?? "_")).Replace("{queue}", Uri.EscapeDataString(queue));
        return null;
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
                if (WorkerOf(q!, distribution, wf) is string url) workers[q!] = url;
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
