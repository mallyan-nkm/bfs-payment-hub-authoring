using System.Text.Json.Nodes;
using TransactHub.TestBench;

namespace TransactHub.TestBench.Tests;

/// <summary>An engine that differs from the interpreter, a broken store or an unmet expectation fails the run (and the CLI exits 1).</summary>
public sealed class JudgeTests
{
    static JsonArray T(params string[] s) => new(s.Select(x => (JsonNode?)x).ToArray());

    static JsonObject Result(JsonObject engine, JsonObject? expect = null) => new()
    {
        ["plan"] = new JsonObject { ["expect"] = expect },
        ["reference"] = new JsonObject { ["engine"] = "interpreter", ["outcome"] = "settled", ["trace"] = T("validate:pass", "leg:settled", "end:settled") },
        ["runs"] = new JsonArray(engine),
    };

    static JsonObject Store(bool ok = true, bool chains = true) => new()
    {
        ["provider"] = "PostgreSql", ["ok"] = ok, ["payloads"] = new JsonArray(new JsonObject { ["ref"] = "r1", ["chain"] = true, ["rebuilt"] = ok }),
        ["rows"] = new JsonObject { ["bulks"] = new JsonArray(new JsonObject()), ["payments"] = new JsonArray(new JsonObject()), ["chains"] = new JsonObject { ["ok"] = chains, ["subjects"] = 2, ["verified"] = chains ? 2 : 1 } },
    };

    [Fact]
    public void The_same_trace_and_outcome_passes()
    {
        var r = Result(new JsonObject { ["engine"] = "dtfx", ["database"] = "PostgreSql", ["outcome"] = "settled", ["trace"] = T("validate:pass", "leg:settled", "end:settled"), ["store"] = Store() },
            new JsonObject { ["outcome"] = "settled", ["store"] = new JsonObject { ["bulks"] = 1, ["payments"] = 1 } });
        Runner.Judge(r);
        Assert.True(r["passed"]!.GetValue<bool>(), r["failures"]!.ToJsonString());
    }

    [Fact]
    public void Another_trace_fails_with_the_diff()
    {
        var r = Result(new JsonObject { ["engine"] = "temporal", ["database"] = "SqlServer", ["outcome"] = "failed", ["trace"] = T("validate:pass", "end:failed"), ["store"] = Store() });
        Runner.Judge(r);
        Assert.False(r["passed"]!.GetValue<bool>());
        var diff = J.Strings(r["runs"]![0]!["comparison"]!["trace"]!["diff"]).ToList();
        Assert.Equal(["  validate:pass", "- leg:settled", "- end:settled", "+ end:failed"], diff);
        Assert.Contains(J.Strings(r["failures"]), f => f.StartsWith("temporal on SqlServer: outcome failed"));
    }

    [Fact]
    public void A_store_that_does_not_rebuild_or_a_broken_chain_fails()
    {
        foreach (var (ok, chains) in new[] { (false, true), (true, false) })
        {
            var r = Result(new JsonObject { ["engine"] = "elsa", ["database"] = "PostgreSql", ["outcome"] = "settled", ["trace"] = T("validate:pass", "leg:settled", "end:settled"), ["store"] = Store(ok, chains) });
            Runner.Judge(r);
            Assert.False(r["passed"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void An_engine_that_did_not_run_or_an_unmet_expectation_fails()
    {
        var r = Result(new JsonObject { ["engine"] = "flowable", ["database"] = "PostgreSql", ["error"] = "Flowable did not start" });
        Runner.Judge(r);
        Assert.False(r["passed"]!.GetValue<bool>());
        var e = Result(new JsonObject { ["engine"] = "dtfx", ["database"] = "none", ["outcome"] = "settled", ["trace"] = T("validate:pass", "leg:settled", "end:settled") }, new JsonObject { ["outcome"] = "rejected" });
        Runner.Judge(e);
        Assert.Contains(J.Strings(e["failures"]), f => f == "interpreter: outcome settled, expected rejected");
        Assert.Contains(J.Strings(e["failures"]), f => f == "dtfx on none: outcome settled, expected rejected");
    }

    [Fact]
    public void A_report_names_each_run_and_its_diff()
    {
        var r = Result(new JsonObject { ["engine"] = "temporal", ["database"] = "SqlServer", ["outcome"] = "failed", ["trace"] = T("end:failed"), ["store"] = Store() });
        r["id"] = "x";
        Runner.Judge(r);
        var md = Report.Markdown(r);
        Assert.Contains("FAILED", md);
        Assert.Contains("| temporal | SqlServer | failed | **NO** |", md);
        Assert.Contains("+ end:failed", md);
        Assert.Contains("<td>temporal</td>", Report.Html(r));
    }

    [Fact]
    public void Connections_get_a_database_of_their_own_and_reports_hide_passwords()
    {
        Assert.Equal("Host=h;Port=1;Username=u;Password=p;Database=tb_1", Infra.WithDatabase("Host=h;Port=1;Username=u;Password=p;Database=postgres", "tb_1"));
        Assert.Equal("Server=h,1;User Id=sa;Password=***;Database=d", Infra.Redact("Server=h,1;User Id=sa;Password=secret;Database=d"));
    }
}
