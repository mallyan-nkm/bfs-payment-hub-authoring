using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace TransactHub.TestBench;

/// <summary>Where the test bench finds the repository, its own folder, the studio and the runtime's tools.</summary>
public sealed class Home
{
    public required string Repo { get; init; }
    /// <summary>runtime/testbench: profiles, benches, the sample library, the shipped suite and the driver.</summary>
    public required string Bench { get; init; }
    /// <summary>Where saved cases, saved samples and run history go (TH_TESTBENCH_LOCAL, default runtime/testbench/local).</summary>
    public required string Local { get; init; }

    public string Studio => Path.Combine(Repo, "studio");
    public string Runtime => Path.Combine(Repo, "runtime");
    public string Tools => Path.Combine(Runtime, "tools");
    public string Profiles => Path.Combine(Bench, "profiles");
    public string Samples => Path.Combine(Bench, "samples");
    public string Suite => Path.Combine(Bench, "suite");
    public string Driver => Path.Combine(Bench, "driver", "bench.ts");
    public string Runs => Path.Combine(Local, "runs");
    public string SavedCases => Path.Combine(Local, "cases");
    public string SavedSamples => Path.Combine(Local, "samples");

    public static Home Find(string? from = null)
    {
        foreach (var start in new[] { from, Environment.GetEnvironmentVariable("TH_TESTBENCH_REPO"), Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "runtime", "TransactHub.Runtime.slnx")) && Directory.Exists(Path.Combine(d.FullName, "runtime", "testbench")))
                {
                    var bench = Path.Combine(d.FullName, "runtime", "testbench");
                    return new Home { Repo = d.FullName, Bench = bench, Local = Environment.GetEnvironmentVariable("TH_TESTBENCH_LOCAL") is { Length: > 0 } l ? Path.GetFullPath(l) : Path.Combine(bench, "local") };
                }
        }
        throw new DirectoryNotFoundException("The repository (runtime/TransactHub.Runtime.slnx and runtime/testbench): run the test bench inside it, or set TH_TESTBENCH_REPO");
    }
}

/// <summary>YAML in and out, as JSON nodes: profiles, benches, cases and the library are plain data.</summary>
public static class Yaml
{
    public static JsonNode? Parse(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents.Count == 0 ? null : Node(stream.Documents[0].RootNode);
    }

    public static JsonObject ParseObject(string text, string what) =>
        Parse(text) as JsonObject ?? throw new BenchError($"{what} is not a YAML mapping");

    public static JsonObject Load(string file)
    {
        try { return ParseObject(File.ReadAllText(file), file); }
        catch (YamlDotNet.Core.YamlException e) { throw new BenchError($"{file}: not valid YAML: {e.Message}"); }
    }

    static JsonNode? Node(YamlNode n) => n switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value ?? "", Node(kv.Value)))),
        YamlSequenceNode s => new JsonArray(s.Children.Select(Node).ToArray()),
        YamlScalarNode sc => Scalar(sc),
        _ => null,
    };

    static JsonNode? Scalar(YamlScalarNode s)
    {
        var v = s.Value ?? "";
        // Quoted scalars stay strings: a routing code or an id is never a number.
        if (s.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted or YamlDotNet.Core.ScalarStyle.Literal or YamlDotNet.Core.ScalarStyle.Folded) return JsonValue.Create(v);
        if (v is "" or "~" or "null") return null;
        if (v is "true") return JsonValue.Create(true);
        if (v is "false") return JsonValue.Create(false);
        if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^-?(0|[1-9]\d{0,14})$")) return JsonValue.Create(long.Parse(v, CultureInfo.InvariantCulture));
        if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^-?(0|[1-9]\d*)\.\d+$")) return JsonValue.Create(decimal.Parse(v, CultureInfo.InvariantCulture));
        return JsonValue.Create(v);
    }

    static readonly ISerializer Writer = new SerializerBuilder().DisableAliases().Build();

    public static string Write(JsonNode? node) => Writer.Serialize(Plain(node));

    static object? Plain(JsonNode? n) => n switch
    {
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => Plain(kv.Value)),
        JsonArray a => a.Select(Plain).ToList(),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => decimal.Parse(v.ToJsonString(), CultureInfo.InvariantCulture),
        _ => null,
    };
}

/// <summary>A mistake in a request, a profile or a case: said plainly, never a stack trace.</summary>
public sealed class BenchError(string message) : Exception(message);

static class J
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    public static string? S(JsonNode? n, string key) => n is JsonObject o && o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : n is JsonObject o2 && o2[key] is JsonValue v2 ? v2.ToJsonString().Trim('"') : null;
    static string Text(JsonValue v) => v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
    public static IEnumerable<string> Strings(JsonNode? n) => n is JsonArray a ? a.Select(x => x is JsonValue v ? Text(v) : "").Where(s => s.Length > 0) : n is JsonValue one ? [Text(one)] : [];
    public static JsonNode? Clone(JsonNode? n) => n?.DeepClone();
    public static long? Long(JsonNode? n) => n is JsonValue v && decimal.TryParse(v.ToJsonString().Trim('"'), System.Globalization.NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (long)d : null;
}
