using System.Text.Json.Nodes;
using TransactHub.TestBench;

namespace TransactHub.TestBench.Tests;

/// <summary>The shipped profiles, benches, library and suite resolve; the data is read and mapped as the README says.</summary>
public sealed class CatalogueTests
{
    static readonly Home H = Home.Find(AppContext.BaseDirectory);
    static readonly Catalogue C = new(H);

    [Fact]
    public void Every_shipped_profile_resolves_with_its_library_samples()
    {
        var profiles = C.Profiles().Select(p => J.S(p, "name")!).ToList();
        Assert.Contains("wps-all-engines", profiles);
        Assert.Contains("wps-all-engines-sqlserver", profiles);
        Assert.Contains("wps-inward-all-engines", profiles);
        Assert.Contains("wps-inward-all-engines-sqlserver", profiles);
        foreach (var e in new Library(H).Entries())
            foreach (var p in J.Strings(e["profiles"]))
            {
                Assert.Contains(p, profiles);
                var plan = C.Resolve(new JsonObject { ["profile"] = p, ["data"] = new JsonObject { ["sample"] = J.S(e, "id") } });
                Assert.Equal(J.S(e, "bench"), plan.BenchName);
                Assert.Equal(J.S(e, "expect"), J.S(plan.Expect, "outcome"));
            }
    }

    [Fact]
    public void The_all_engines_profiles_run_four_engines_and_the_interpreter_on_their_database()
    {
        var pg = C.Resolve(new JsonObject { ["profile"] = "wps-all-engines", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" } });
        Assert.Equal(["temporal", "dtfx", "elsa", "flowable"], pg.Engines);
        Assert.True(pg.Reference);
        Assert.Equal(["PostgreSql"], pg.Databases);
        Assert.Equal("uae.wps.sif", pg.Input.Message);
        var ms = C.Resolve(new JsonObject { ["profile"] = "wps-all-engines-sqlserver", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" } });
        Assert.Equal(["SqlServer"], ms.Databases);
    }

    /// <summary>Temporal is the default engine (R-09-79); a tester still starts DTFx, Elsa or Flowable by naming them.</summary>
    [Fact]
    public void A_profile_that_names_no_engine_runs_on_Temporal_and_a_request_may_start_the_others()
    {
        Assert.Equal(["temporal"], Catalogue.DefaultEngines);
        var plan = C.Resolve(new JsonObject { ["profile"] = "wps-default-engine", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" } });
        Assert.Equal(["temporal"], plan.Engines);
        Assert.Equal(["SqlServer"], plan.Databases);
        foreach (var other in new[] { "dtfx", "elsa", "flowable" })
            Assert.Equal([other], C.Resolve(new JsonObject { ["profile"] = "wps-default-engine", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" }, ["engines"] = new JsonArray(other) }).Engines);
    }

    [Fact]
    public void A_bench_extends_another_and_a_request_overrides_one_port()
    {
        var nak = C.Bench("wps-nak");
        Assert.Contains(new BenchPort("uae.wps.port.wps.v1", "uae.wps.sim.wps-outward.v1", "reject"), nak);
        Assert.Contains(new BenchPort("p.port.sanctions.v1", "p.sim.sanctions.v1", "accept"), nak);
        Assert.Single(nak, p => p.Port == "uae.wps.port.wps.v1");
        var plan = C.Resolve(new JsonObject
        {
            ["profile"] = "wps-all-engines", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" }, ["bench"] = "wps-nak",
            ["ports"] = new JsonArray("p.port.fraud.v1=p.sim.fraud.v1:reject"), ["engines"] = new JsonArray("dtfx"), ["databases"] = new JsonArray("sqlserver"),
            ["clock"] = new JsonObject { ["date"] = "2026-12-01" },
        });
        Assert.Contains(new BenchPort("p.port.fraud.v1", "p.sim.fraud.v1", "reject"), plan.Bench);
        Assert.Equal(["dtfx"], plan.Engines);
        Assert.Equal(["SqlServer"], plan.Databases);
        Assert.Equal("2026-12-01", plan.EvaluationDate);
    }

    [Fact]
    public void A_mistake_in_a_request_is_said_plainly()
    {
        Assert.Contains("No profile", Assert.Throws<BenchError>(() => C.Resolve(new JsonObject { ["profile"] = "nope", ["data"] = "x" })).Message);
        Assert.Contains("No engine", Assert.Throws<BenchError>(() => C.Resolve(new JsonObject { ["profile"] = "wps-all-engines", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" }, ["engines"] = new JsonArray("camunda") })).Message);
        Assert.Contains("No bench", Assert.Throws<BenchError>(() => C.Bench("nope")).Message);
    }

    [Fact]
    public void A_tenant_other_than_the_example_bank_loads_its_own_bank_folder()
    {
        Assert.Equal(["dib"], C.BanksFor("dib", []));
        Assert.Empty(C.BanksFor("example-bank", []));
    }

    [Fact]
    public void Every_shipped_case_resolves()
    {
        var cases = Cases.Load(H.Suite).ToList();
        Assert.True(cases.Count >= 15);
        foreach (var (file, c) in cases)
        {
            var plan = C.Resolve(Cases.Request(file, c));
            Assert.NotNull(J.S(plan.Expect, "outcome"));
        }
    }

    [Fact]
    public void Scheme_files_go_through_the_front_door_and_ISO_messages_become_the_context()
    {
        var sif = Inputs.Detect(File.ReadAllText(Path.Combine(H.Samples, "wps", "sif-happy.sif")), "x.sif");
        Assert.Equal(("wps-sif", "uae.wps.sif", 1), (sif.Kind, sif.Message, sif.Records));
        Assert.NotNull(sif.Payload);
        var pif = Inputs.Detect(File.ReadAllText(Path.Combine(H.Samples, "wps", "pif-happy.pif")), "x.pif");
        Assert.Equal(("wps-pif", "uae.wps.pif", 2), (pif.Kind, pif.Message, pif.Records));
        var pain = Inputs.Detect(File.ReadAllText(Path.Combine(H.Samples, "iso", "pain001-salary.xml")), "p.xml");
        Assert.Equal("pain.001", pain.Kind);
        Assert.Null(pain.Payload);
        Assert.Equal(2, (pain.Context!["items"] as JsonArray)!.Count);
        Assert.Equal("302620122", J.S(pain.Context["payment"]!["debtorAgent"], "clearingMemberId"));
        Assert.Equal("outward", J.S(pain.Context["header"], "direction"));
        // No party name enters the context.
        Assert.DoesNotContain("Synthetic", pain.Context.ToJsonString());
        var pacs = Inputs.Detect(File.ReadAllText(Path.Combine(H.Samples, "iso", "pacs008-credit.xml")), "c.xml");
        Assert.Equal(("pacs.008", "inward"), (pacs.Kind, J.S(pacs.Context!["header"], "direction")));
        var json = Inputs.Detect(File.ReadAllText(Path.Combine(H.Samples, "json", "canonical-payment.json")), "c.json");
        Assert.Equal("json", json.Kind);
        Assert.Throws<BenchError>(() => Inputs.Detect("hello", "x.txt"));
    }

    [Fact]
    public void Profiles_benches_and_cases_are_YAML_that_reads_back()
    {
        var c = Cases.FromRun(new JsonObject
        {
            ["plan"] = new JsonObject { ["profile"] = "wps-reference", ["input"] = new JsonObject { ["name"] = "a.sif" } },
            ["request"] = new JsonObject { ["profile"] = "wps-reference", ["data"] = new JsonObject { ["sample"] = "wps-sif-happy" } },
            ["reference"] = new JsonObject { ["outcome"] = "settled", ["trace"] = new JsonArray("validate:pass", "end:settled") },
            ["runs"] = new JsonArray(),
        }, "TB-T-001", "A title: with a colon");
        var dir = Directory.CreateTempSubdirectory("th-tb-cases-").FullName;
        try
        {
            var file = Cases.Save(H, c, dir);
            var (_, back) = Assert.Single(Cases.Load(dir));
            Assert.Equal("A title: with a colon", J.S(back, "title"));
            Assert.Equal("settled", J.S(back["expect"], "outcome"));
            Assert.Equal(["validate:pass", "end:settled"], J.Strings(back["expect"]!["trace"]));
            Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(dir, true); }
    }
}
