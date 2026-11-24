using System.Text.Json.Nodes;

namespace TransactHub.Store.Tests;

/// <summary>
/// A bank folder's default currency (R-02-24), as the studio gives it to the bank's configuration release: the bank's own value of
/// the parameter its country cluster marks <c>role: default-currency</c> (in the factory, <c>spec/factory/&lt;cluster&gt;.p</c>),
/// else the cluster's declared default. A test that reads a bank folder in place of its release reads the folder's bindings with it.
/// </summary>
public static class BankCurrency
{
    static string Repo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "spec")) && Directory.Exists(Path.Combine(d.FullName, "runtime"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    static JsonNode? Yaml(string path)
    {
        var ys = new YamlDotNet.RepresentationModel.YamlStream();
        ys.Load(new StringReader(File.ReadAllText(path)));
        return ys.Documents.Count == 0 ? null : Json(ys.Documents[0].RootNode);
    }

    static JsonNode? Json(YamlDotNet.RepresentationModel.YamlNode n) => n switch
    {
        YamlDotNet.RepresentationModel.YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create(((YamlDotNet.RepresentationModel.YamlScalarNode)kv.Key).Value!, Json(kv.Value)))),
        YamlDotNet.RepresentationModel.YamlSequenceNode s => new JsonArray(s.Children.Select(Json).ToArray()),
        YamlDotNet.RepresentationModel.YamlScalarNode v => v.Value is null ? null : JsonValue.Create(v.Value),
        _ => null,
    };

    /// <summary>The default currency of the bank whose folder this is; null where its cluster declares none.</summary>
    public static string? Of(string bankFolder)
    {
        var lock_ = Path.Combine(bankFolder, "pack.yaml");
        var clusters = File.Exists(lock_)
            ? ((Yaml(lock_)?["dependsOn"] as JsonArray) ?? []).Select(d => ((string?)d?["scope"] ?? "").Split('.')[0]).Where(c => c is { Length: > 0 } && c != "p" && c != "b").Distinct().ToList()
            : [];
        if (clusters.Count != 1) return null;
        var dir = Path.Combine(Repo(), "spec", "factory", $"{clusters[0]}.p", "activities");
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.EnumerateFiles(dir, "*.yaml").Order(StringComparer.Ordinal))
        {
            var doc = Yaml(f);
            foreach (var (k, decl) in doc?["parameters"] as JsonObject ?? [])
            {
                if ((string?)decl?["role"] != "default-currency") continue;
                var key = $"{(string?)doc!["name"]}#{k}";
                var cs = Path.Combine(bankFolder, "configuration-set.yaml");
                var own = File.Exists(cs) ? ((Yaml(cs)?["values"] as JsonArray) ?? []).LastOrDefault(v => (string?)v?["parameter"] == key)?["value"] : null;
                return (string?)own ?? (string?)decl?["default"];
            }
        }
        return null;
    }
}
