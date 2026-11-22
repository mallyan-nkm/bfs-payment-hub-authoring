using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The factory's operations views (R-05-48, R-05-49) as the runtime reads them, and the WPS field mapping
/// (docs/runtime/ops-console-wps-mapping.md): every field the mapping calls mapped is declared in the view it names, with
/// the source it names, and the coverage it states is what its rows say. The static checks are lint L38
/// (spec/tools/validate.py, with mutation cases M40 to M46).
/// </summary>
public sealed class OperationsViewTests
{
    static readonly ViewCatalog Views = Repo.Views();

    [Fact]
    public void The_platform_and_the_WPS_views_are_published_and_every_source_is_one_the_console_reads()
    {
        Assert.Equal(["p.view.operations.v1", "uae.wps.view.operations.v1"], Views.All.Select(v => v.Name));
        var wps = Views.SchemeView("uae.wps")!;
        Assert.Equal(["uae.wps.wf.salary-file-out.v1", "uae.wps.wf.agent-payment-in.v1", "uae.wps.wf.return-in.v1", "uae.wps.wf.refund-out.v1", "uae.wps.wf.inward-refuse.v1"],
            wps.Legs.Select(l => (string)l["leg"]!));
        foreach (var v in Views.All)
            foreach (var f in Fields(v))
            {
                var src = (string)f["source"]!;
                Assert.True(Sources.Roots.Any(r => src.StartsWith(r + ".") || src.StartsWith(r + ":") || src.StartsWith(r + "(")), $"{v.Name} {f["id"]}: {src}");
            }
    }

    static IEnumerable<JsonObject> Fields(OperationsView v) =>
        (v.Subject?["fields"] as JsonArray ?? []).Concat(Sources.Path(v.Subject, "lines.fields") as JsonArray ?? [])
            .Concat(v.Legs.SelectMany(l => l["fields"] as JsonArray ?? [])).OfType<JsonObject>();

    static JsonObject? Field(string spec)
    {
        // <p|uae.wps>:<subject|lines|leg>:<field>
        var parts = spec.Split(':');
        var view = Views.All.Single(v => v.Name == $"{parts[0]}.view.operations.v1");
        var fields = parts[1] switch
        {
            "subject" => view.Subject?["fields"] as JsonArray,
            "lines" => Sources.Path(view.Subject, "lines.fields") as JsonArray,
            _ => view.Legs.Single(l => (string?)l["leg"] == parts[1])["fields"] as JsonArray,
        };
        return fields?.OfType<JsonObject>().SingleOrDefault(f => (string?)f["id"] == parts[2]);
    }

    [Fact]
    public void Every_field_the_WPS_mapping_calls_mapped_is_declared_with_its_source_and_the_coverage_is_counted()
    {
        var doc = File.ReadAllLines(Path.Combine(Repo.Root(), "docs", "runtime", "ops-console-wps-mapping.md"));
        var rows = doc.Where(l => l.StartsWith("| ") && !l.StartsWith("| Old field") && !l.StartsWith("| ---")).Select(l => l.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray()).ToList();
        var problems = new List<string>();
        int mapped = 0, empty = 0, notMapped = 0, deliberately = 0;
        foreach (var r in rows)
        {
            var (old, status, field, source) = (r[0], r[1], r[2], r[3]);
            if (status == "not mapped")
            {
                notMapped++;
                if (r[4].StartsWith("Deliberately")) deliberately++;
                else if (!r[4].Contains("I-") && !r[4].Contains("plan P2") && !r[4].Contains("No factory flow")) problems.Add($"{old}: not mapped, with no issue named");
                continue;
            }
            mapped++;
            if (status == "mapped, empty") empty++;
            else if (status != "mapped") { problems.Add($"{old}: status {status}"); continue; }
            if (field.StartsWith('(')) continue; // the list, the timeline, the documents, the offers: the console's own
            var specs = field.Split(", ");
            var prefix = specs[0][..(specs[0].LastIndexOf(':') + 1)];
            var sources = Regex.Matches(source, "`([^`]+)`").Select(m => m.Groups[1].Value).ToList();
            for (var i = 0; i < specs.Length; i++)
            {
                var spec = specs[i].Contains(':') ? specs[i] : prefix + specs[i];
                var f = Field(spec);
                if (f is null) { problems.Add($"{old}: {spec} is not declared"); continue; }
                if (i < sources.Count && (string?)f["source"] != sources[i]) problems.Add($"{old}: {spec} reads {f["source"]}, the mapping says {sources[i]}");
                if (source.Contains("masked") && f["mask"] is null) problems.Add($"{old}: {spec} is said to be masked and declares no mask");
            }
        }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
        var coverage = doc.Single(l => l.StartsWith("Of the "));
        Assert.Equal($"Of the {rows.Count} rows above: {mapped} mapped ({empty} of them empty until the store keeps the files or the channel's fields, I-97 and I-100), {notMapped} not mapped. Of the {notMapped}, {deliberately} are kept out on purpose (request and reply bodies and the audit's payloads hold personal data; error texts can quote values), and {notMapped - deliberately} have no source in the new model, each in Issue I-100.", coverage);
    }
}
