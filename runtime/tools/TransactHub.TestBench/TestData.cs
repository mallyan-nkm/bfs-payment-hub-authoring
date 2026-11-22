using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TransactHub.TestBench;

/// <summary>The test data of the bench, as the CLI, the API and the UI offer it: the library, the builder, validation and saving.</summary>
public sealed class TestData(Home home)
{
    public sealed record Validation(List<Finding> Format, List<Finding> Synthetic)
    {
        public JsonObject Json() => new()
        {
            ["format"] = new JsonArray(Format.Select(f => (JsonNode?)f.Json()).ToArray()),
            ["synthetic"] = new JsonArray(Synthetic.Select(f => (JsonNode?)f.Json()).ToArray()),
            ["valid"] = Format.Count == 0 && Synthetic.Count == 0,
        };
    }

    public List<JsonObject> List(string? scheme = null) =>
        new Library(home).Entries().Where(e => scheme is null || J.S(e, "scheme") == scheme).Select(e => { e.Remove("text"); return e; }).ToList();

    public Built Build(JsonObject recipe) => new DataBuilder(home).Build(recipe);

    public Validation Validate(Input input, string tenant)
    {
        var routing = new DataBuilder(home).RoutingCode(tenant);
        var format = Format.Check(input, input.Kind == "wps-pif" ? routing : null);
        var synthetic = new Synthetic(home).Check(input.Payload ?? input.Context?.ToJsonString() ?? "");
        return new Validation(format, synthetic);
    }

    /// <summary>The scheme's own rules: the data run on the studio interpreter only, through the profile's workflow and bench.</summary>
    public async Task<JsonObject> Rules(Infra infra, JsonObject data, string profile, string tenant, string? bench)
    {
        var req = new JsonObject { ["profile"] = profile, ["data"] = data.DeepClone(), ["tenant"] = tenant, ["engines"] = new JsonArray(), ["reference"] = true };
        if (bench is not null) req["bench"] = bench;
        var plan = new Catalogue(home).Resolve(req);
        var result = await new Runner(home, infra).Run(plan, Runner.NewId() + "-rules");
        return result["reference"] as JsonObject ?? new JsonObject { ["error"] = J.S(result, "error") ?? "no reference" };
    }

    /// <summary>Saves data as a library sample under local/samples, refused when it looks real.</summary>
    public string Save(string id, string text, string name, JsonObject tags)
    {
        if (!Regex.IsMatch(id, @"^[a-z0-9][a-z0-9.-]{0,63}$")) throw new BenchError("A sample id is lower-case letters, digits, dot and dash");
        var findings = new Synthetic(home).Check(text);
        if (findings.Count > 0) throw new BenchError("Not saved: the data looks real. " + string.Join("; ", findings.Select(f => $"{f.Code} {f.Message}")));
        if (new Library(home).Entries().Any(e => J.S(e, "id") == id && J.S(e, "origin") == "shipped")) throw new BenchError($"{id} is a shipped sample; give the new one another id");
        var input = Inputs.Detect(text, name);
        Directory.CreateDirectory(home.SavedSamples);
        var ext = input.Kind switch { "wps-sif" => ".sif", "wps-pif" => ".pif", "json" => ".json", _ => ".xml" };
        var file = $"{id}{ext}";
        File.WriteAllText(Path.Combine(home.SavedSamples, file), text);
        var entry = new JsonObject
        {
            ["id"] = id, ["title"] = J.S(tags, "title") ?? $"{input.Kind} {J.S(tags, "scenario") ?? ""}".Trim(), ["file"] = file, ["kind"] = input.Kind,
            ["scheme"] = J.S(tags, "scheme") ?? (input.Kind.StartsWith("wps-") ? "uae.wps" : "p"),
            ["direction"] = J.S(tags, "direction") ?? (input.Kind is "wps-pif" or "pacs.008" ? "inward" : "outward"),
            ["scenario"] = J.S(tags, "scenario") ?? "saved", ["expect"] = J.S(tags, "expect"), ["bench"] = J.S(tags, "bench"),
            ["profiles"] = J.Clone(tags["profiles"]), ["build"] = J.Clone(tags["build"]),
        };
        foreach (var k in entry.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) entry.Remove(k);
        var yaml = Path.Combine(home.SavedSamples, $"{id}.yaml");
        File.WriteAllText(yaml, "# A test bench library sample, saved from the builder or the UI. Synthetic data only.\n" + Yaml.Write(entry));
        return yaml;
    }
}
