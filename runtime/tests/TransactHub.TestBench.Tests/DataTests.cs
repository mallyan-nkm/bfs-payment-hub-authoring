using System.Text.Json.Nodes;
using TransactHub.TestBench;

namespace TransactHub.TestBench.Tests;

/// <summary>The builder makes valid synthetic data for each bank that runs WPS; each injection gives the finding it is for; the synthetic check refuses what looks real.</summary>
public sealed class DataTests
{
    static readonly Home H = Home.Find(AppContext.BaseDirectory);
    static readonly DataBuilder B = new(H);

    static Input Built(JsonObject recipe) { var b = B.Build(recipe); return Inputs.Detect(b.Text, b.Name); }

    public static TheoryData<string> WpsBanks() => new(new DataBuilder(Home.Find(AppContext.BaseDirectory)).Tenants().Where(t => new DataBuilder(Home.Find(AppContext.BaseDirectory)).RoutingCode(t) is not null));

    [Theory]
    [MemberData(nameof(WpsBanks))]
    public void A_built_salary_and_payment_file_is_valid_for_each_bank_that_runs_WPS(string tenant)
    {
        var routing = B.RoutingCode(tenant)!;
        foreach (var t in new[] { "wps-sif", "wps-pif" })
        {
            var input = Built(new JsonObject { ["template"] = t, ["tenant"] = tenant, ["lines"] = 25, ["seed"] = 5 });
            Assert.Empty(Format.Check(input, routing));
            Assert.Empty(new Synthetic(H).Check(input.Payload!));
            Assert.Contains($",{routing},", input.Payload);
        }
    }

    [Fact]
    public void The_banks_without_WPS_have_no_routing_code_and_the_builder_says_so()
    {
        Assert.Equal("302620122", B.RoutingCode("example-bank"));
        Assert.Equal("802420101", B.RoutingCode("dib"));
        foreach (var t in new[] { "adib", "enbd", "fab" })
        {
            Assert.Null(B.RoutingCode(t));
            Assert.Contains("no WPS routing code", Assert.Throws<BenchError>(() => B.Build(new JsonObject { ["template"] = "wps-sif", ["tenant"] = t })).Message);
        }
    }

    [Theory]
    [InlineData("invalid-iban", "TB-F10")]
    [InlineData("bad-control-total", "TB-F16")]
    [InlineData("bad-record-count", "TB-F15")]
    [InlineData("bad-pay-period", "TB-F14")]
    [InlineData("bad-employer-id", "TB-F05")]
    [InlineData("bad-currency", "TB-F11")]
    [InlineData("duplicate-person-id", "TB-F13")]
    public void Each_injection_gives_its_finding_and_no_other(string inject, string code)
    {
        var input = Built(new JsonObject { ["template"] = "wps-sif", ["lines"] = 3, ["inject"] = new JsonArray(inject) });
        var f = Format.Check(input);
        Assert.Contains(f, x => x.Code == code);
        Assert.All(f, x => Assert.True(x.Code == code || (inject == "bad-employer-id" && x.Code == "TB-F08"), $"{inject}: {x.Code} {x.Message}"));
    }

    [Fact]
    public void A_PIF_to_another_bank_is_found()
    {
        var input = Built(new JsonObject { ["template"] = "wps-pif", ["inject"] = new JsonArray("wrong-agent") });
        Assert.Contains(Format.Check(input, B.RoutingCode("example-bank")), x => x.Code == "TB-F09");
    }

    [Fact]
    public void The_same_recipe_builds_the_same_data_and_a_large_file_is_built()
    {
        var r = new JsonObject { ["template"] = "wps-sif", ["lines"] = 10000, ["seed"] = 9, ["amounts"] = new JsonObject { ["min"] = 900, ["max"] = 25000 } };
        var a = B.Build(r);
        Assert.Equal(a.Text, B.Build(r).Text);
        var input = Inputs.Detect(a.Text, a.Name);
        Assert.Equal(10000, input.Records);
        Assert.Empty(Format.Check(input));
    }

    [Fact]
    public void Generated_IBANs_have_valid_check_digits_under_the_reserved_bank_code()
    {
        var iban = DataBuilder.Iban("AE", "9990010000000000002");
        Assert.Equal("AE439990010000000000002", iban);
        Assert.True(DataBuilder.IbanValid(iban));
        Assert.True(DataBuilder.IbanValid("GB82WEST12345698765432"));
        Assert.False(DataBuilder.IbanValid("GB83WEST12345698765432"));
    }

    [Fact]
    public void ISO_messages_carry_only_synthetic_names_and_the_sanctioned_test_name_on_purpose()
    {
        var pain = B.Build(new JsonObject { ["template"] = "pain.001", ["lines"] = 2 });
        Assert.Empty(new Synthetic(H).Check(pain.Text));
        var hit = B.Build(new JsonObject { ["template"] = "pacs.008", ["inject"] = new JsonArray("sanctioned-name") });
        Assert.Contains(DataBuilder.SanctionedTestName, hit.Text);
        Assert.Equal("sanctions-hit", hit.SuggestedBench);
    }

    [Theory]
    [InlineData("EDR,00001234567890,203320101,GB82WEST12345698765432,2026-10-01", "TB-S01")]
    [InlineData("EDR,00001234567890,203320101,AE630330000010123456789,2026-10-01", "TB-S01")]
    [InlineData("id 784-1980-1234567-1", "TB-S02")]
    [InlineData("contact someone@bank.ae", "TB-S03")]
    [InlineData("call +971 50 123 4567", "TB-S04")]
    [InlineData("<Cdtr><Nm>Jane Real Person</Nm></Cdtr>", "TB-S05")]
    public void Data_that_looks_real_is_refused(string text, string code)
    {
        Assert.Contains(new Synthetic(H).Check(text), f => f.Code == code);
        var dir = Directory.CreateTempSubdirectory("th-tb-local-").FullName;
        try
        {
            var data = new TestData(new Home { Repo = H.Repo, Bench = H.Bench, Local = dir });
            Assert.Contains("looks real", Assert.Throws<BenchError>(() => data.Save("looks-real", "SCR,0000001234567,302620122,2026-11-17,0830,102026,1,1.00,AED,R\n" + text, "x.sif", new JsonObject())).Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_library_samples_are_synthetic_and_a_built_sample_saves_to_the_local_library()
    {
        foreach (var f in Directory.EnumerateFiles(H.Samples, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".yaml")))
            Assert.Empty(new Synthetic(H).Check(File.ReadAllText(f)));
        var dir = Directory.CreateTempSubdirectory("th-tb-local-").FullName;
        try
        {
            var home = new Home { Repo = H.Repo, Bench = H.Bench, Local = dir };
            var built = B.Build(new JsonObject { ["template"] = "wps-sif", ["lines"] = 2, ["seed"] = 77 });
            new TestData(home).Save("my-sample", built.Text, built.Name, new JsonObject { ["scenario"] = "happy path", ["expect"] = "settled", ["bench"] = "wps-happy" });
            var e = new Library(home).Entry("my-sample");
            Assert.Equal("saved", J.S(e, "origin"));
            Assert.Equal(built.Text, new Library(home).Payload("my-sample", "example-bank").Text);
            Assert.Contains("shipped sample", Assert.Throws<BenchError>(() => new TestData(home).Save("wps-sif-happy", built.Text, built.Name, new JsonObject())).Message);
        }
        finally { Directory.Delete(dir, true); }
    }
}
