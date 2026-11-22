using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TransactHub.TestBench;

/// <summary>
/// Regression cases: a request and what it is expected to give, saved as YAML (kind testbench-case), and replayed as a
/// suite. Banks and the platform team keep their own packs; the shipped suite is runtime/testbench/suite.
/// </summary>
public static class Cases
{
    public static IEnumerable<(string File, JsonObject Case)> Load(string dir)
    {
        if (File.Exists(dir)) { yield return (dir, Check(Yaml.Load(dir), dir)); yield break; }
        if (!Directory.Exists(dir)) throw new BenchError($"No suite folder {dir}");
        foreach (var f in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            yield return (f, Check(Yaml.Load(f), f));
    }

    static JsonObject Check(JsonObject c, string file)
    {
        if (J.S(c, "kind") != "testbench-case") throw new BenchError($"{file} is not a testbench-case");
        if (J.S(c, "id") is null) throw new BenchError($"{file} has no id");
        if (c["request"] is not JsonObject) throw new BenchError($"{file} has no request");
        return c;
    }

    /// <summary>The case's request, ready to resolve: its data file read relative to the case, its expectation attached, the overrides applied.</summary>
    public static JsonObject Request(string file, JsonObject c, JsonObject? overrides = null)
    {
        var req = (JsonObject)c["request"]!.DeepClone();
        if (req["data"] is JsonObject d && J.S(d, "file") is { } f && !Path.IsPathRooted(f)) d["file"] = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, f));
        if (c["expect"] is JsonObject e) req["expect"] = e.DeepClone();
        foreach (var kv in overrides ?? []) req[kv.Key] = kv.Value?.DeepClone();
        return req;
    }

    /// <summary>A case from a run: its request, and the outcome and trace the interpreter gave as what is expected.</summary>
    public static JsonObject FromRun(JsonObject result, string id, string? title)
    {
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")) throw new BenchError("A case id is letters, digits, dot, dash and underscore");
        var plan = result["plan"] as JsonObject ?? throw new BenchError("The run has no plan");
        var reference = result["reference"] as JsonObject;
        var firstRun = (result["runs"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(r => J.S(r, "error") is null);
        var basis = reference ?? firstRun ?? throw new BenchError("The run has no outcome to expect");
        var request = (JsonObject)(result["request"] ?? new JsonObject()).DeepClone();
        request.Remove("expect");
        var expect = new JsonObject { ["outcome"] = J.S(basis, "outcome"), ["trace"] = J.Clone(basis["trace"]) };
        if (firstRun?["store"]?["rows"] is JsonObject rows)
            expect["store"] = new JsonObject { ["bulks"] = (rows["bulks"] as JsonArray)?.Count ?? 0, ["payments"] = (rows["payments"] as JsonArray)?.Count ?? 0 };
        return new JsonObject
        {
            ["kind"] = "testbench-case", ["id"] = id, ["title"] = title ?? $"{J.S(plan, "profile")}: {J.S(plan["input"], "name")}",
            ["request"] = request, ["expect"] = expect,
        };
    }

    public static string Save(Home home, JsonObject c, string? dir = null)
    {
        dir ??= home.SavedCases;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{J.S(c, "id")}.yaml");
        File.WriteAllText(file, "# A test bench regression case (runtime/testbench/README.md). Replay: testbench suite <folder>.\n" + Yaml.Write(c));
        return file;
    }

    /// <summary>
    /// A case may name a known issue: the engines it names are known to differ from the interpreter (an Issue in
    /// docs/ISSUES.md). The case still fails on anything else: the interpreter not giving what is expected, an engine the
    /// issue does not name, a broken store. And it fails when a named engine now agrees, so the mark is removed when the
    /// issue is fixed.
    /// </summary>
    static void Known(JsonObject entry, JsonObject result, JsonObject known)
    {
        var id = J.S(known, "id") ?? "an issue";
        var engines = J.Strings(known["engines"]).ToHashSet();
        var other = new JsonArray();
        var stillDiffers = false;
        var ran = false;
        if (result["reference"] is JsonObject re && re["expectation"]?["failures"] is JsonArray rf) foreach (var f in J.Strings(rf)) other.Add($"interpreter: {f}");
        if (J.S(result, "error") is { } err) other.Add(err);
        foreach (var run in (result["runs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var label = $"{J.S(run, "engine")} on {J.S(run, "database")}";
            if (engines.Contains(J.S(run, "engine") ?? ""))
            {
                ran = true;
                if (run["passed"]?.GetValue<bool>() == true) other.Add($"{label} now agrees with the interpreter: {id} may be fixed; remove knownIssue from the case");
                else stillDiffers = true;
            }
            else foreach (var f in J.Strings(run["failures"])) other.Add($"{label}: {f}");
        }
        entry["known"] = id;
        entry["passed"] = other.Count == 0 && (stillDiffers || !ran);
        if (other.Count > 0) result["failures"] = other;
    }

    /// <summary>Runs every case of a folder; the suite passes when every case does.</summary>
    public static async Task<JsonObject> Run(Home home, Infra infra, string dir, JsonObject? overrides, Action<string>? progress, CancellationToken ct)
    {
        var catalogue = new Catalogue(home);
        var runner = new Runner(home, infra);
        var cases = new JsonArray();
        foreach (var (file, c) in Cases.Load(dir))
        {
            var id = J.S(c, "id")!;
            progress?.Invoke($"case {id}");
            var entry = new JsonObject { ["id"] = id, ["title"] = J.S(c, "title"), ["file"] = Path.GetRelativePath(home.Repo, file), ["expected"] = J.S(c["expect"], "outcome") };
            try
            {
                var req = Request(file, c, overrides);
                var plan = catalogue.Resolve(req);
                entry["profile"] = plan.Profile;
                var result = await runner.Run(plan, $"{Runner.NewId()}-{Regex.Replace(id, "[^A-Za-z0-9]", "").ToLowerInvariant()}", s => progress?.Invoke($"{id}: {s}"), ct);
                result["request"] = req;
                entry["result"] = result;
                entry["passed"] = result["passed"]?.GetValue<bool>() == true;
                if (c["knownIssue"] is JsonObject known) Known(entry, result, known);
            }
            catch (BenchError e)
            {
                entry["result"] = new JsonObject { ["failures"] = new JsonArray(e.Message) };
                entry["passed"] = false;
            }
            progress?.Invoke($"case {id}: {(entry["passed"]!.GetValue<bool>() ? "pass" : "FAIL")}");
            cases.Add(entry);
        }
        return new JsonObject { ["dir"] = Path.GetRelativePath(home.Repo, Path.GetFullPath(dir)), ["cases"] = cases, ["passed"] = cases.All(c => c?["passed"]?.GetValue<bool>() == true) };
    }
}
