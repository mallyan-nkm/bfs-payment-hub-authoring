using System.Text.Json.Nodes;
using TransactHub.WpsGolden;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// No functionality lost (plan section 4): the runtime's port of the source's WPS salary-file rules gives
/// the source's own answer, code and message, for every case of the golden corpus. The corpus was written by
/// tools/WpsGolden from the source's rule classes; regenerate it there, never by hand.
/// </summary>
public sealed class WpsRulesGoldenTests
{
    [Fact]
    public void The_port_answers_every_golden_case_as_the_source_does()
    {
        var golden = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "wps-rules.golden.json")))!;
        var expected = golden["cases"]!.AsArray();
        var actual = Corpus.Run();
        Assert.Equal(expected.Count, actual.Count);
        var diffs = new List<string>();
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i]!;
            var a = actual[i]!;
            if ((string?)e["rule"] != (string?)a["rule"] || (string?)e["input"] != (string?)a["input"])
            {
                diffs.Add($"case {i}: the corpus itself differs ({e["rule"]} {e["input"]} / {a["rule"]} {a["input"]})");
                continue;
            }
            if ((string?)e["result"] != (string?)a["result"])
                diffs.Add($"case {i} {e["rule"]} {e["input"]}:\n  source  {e["result"]}\n  runtime {a["result"]}");
        }
        Assert.True(diffs.Count == 0, $"{diffs.Count} of {expected.Count} cases differ:\n" + string.Join("\n", diffs.Take(10)));
        Assert.True(expected.Count > 1500);
    }
}
