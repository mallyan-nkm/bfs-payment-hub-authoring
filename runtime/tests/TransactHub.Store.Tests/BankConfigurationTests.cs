using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TransactHub.Ports;
using Xunit;

namespace TransactHub.Store.Tests;

// Banks are configuration (plan W6, P5). No bank is named in shared code. A bank's core banking is a port
// binding, so switching it is a new binding with a later validFrom, with no release and no deployment.

public sealed class BankConfigurationTests
{
    static string Repo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content-configurations")) && Directory.Exists(Path.Combine(d.FullName, "runtime"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    static string ExampleBank => Path.Combine(Repo(), "content-configurations", "banks", "example-bank");
    const string Port = "p.port.core-banking.v1";

    // The banks the source forked for, and the shapes their names took in code (namespaces, classes, options).
    static readonly Regex BankName = new(@"\b(DIB|ADIB|ENBD|FAB|Dib|Adib|Enbd|Fab)(?=[A-Z_.\W]|\b)|Dubai Islamic|Abu Dhabi Islamic|Emirates NBD|First Abu Dhabi", RegexOptions.Compiled);

    [Fact]
    public void No_shared_code_names_a_bank()
    {
        var root = Repo();
        var problems = new List<string>();
        string[] dirs = ["runtime/src", "runtime/tools", "studio/src", "studio/engines/shared", "studio/engines/dtfx/harness", "studio/engines/elsa/harness", "studio/engines/temporal", "studio/engines/flowable/harness/src"];
        string[] exts = [".cs", ".ts", ".tsx", ".java", ".py"];
        foreach (var d in dirs.Select(x => Path.Combine(root, x)).Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (!exts.Contains(Path.GetExtension(f)) || rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/node_modules/")) continue;
                var lines = File.ReadAllLines(f);
                for (var i = 0; i < lines.Length; i++)
                    if (BankName.Match(lines[i]) is { Success: true } m) problems.Add($"{rel}:{i + 1}: '{m.Value}'");
            }
        Assert.True(problems.Count == 0, "A bank is named in shared code; make it configuration:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void The_check_finds_a_bank_named_the_way_the_source_named_them()
    {
        foreach (var s in new[] { "namespace PaymentHub.Activities.Bank.Dib.Wps;", "var tenant = \"DIB\";", "class AdibRtcActivities", "ENBD_POSTING" })
            Assert.Matches(BankName, s);
        foreach (var s in new[] { "fabric", "Fabricate", "dibble", "BankRoutingCode", "example-bank" })
            Assert.DoesNotMatch(BankName, s);
    }

    [Fact]
    public void The_example_bank_switches_core_banking_by_a_binding_alone()
    {
        var bindings = PortBindings.FromDirectory(ExampleBank, BankCurrency.Of(ExampleBank));
        var before = bindings.InForce("example-bank", Port, DateTimeOffset.Parse("2026-11-30T23:59:59Z"));
        var after = bindings.InForce("example-bank", Port, DateTimeOffset.Parse("2026-12-01T00:00:00Z"));
        Assert.Equal(SimulatedCoreBanking.Name, before.Adapter);
        Assert.Equal(GatewayRestCoreBanking.Name, after.Adapter);
        Assert.IsType<SimulatedCoreBanking>(CoreBankingAdapters.Create(before));
        Assert.IsType<GatewayRestCoreBanking>(CoreBankingAdapters.Create(after, new HttpClient(new Recorder())));
    }

    [Fact]
    public async Task What_the_source_hard_coded_for_the_bank_travels_in_the_binding()
    {
        var b = PortBindings.FromDirectory(ExampleBank, BankCurrency.Of(ExampleBank)).InForce("example-bank", Port, DateTimeOffset.Parse("2026-12-15T00:00:00Z"));
        var rec = new Recorder();
        var cb = CoreBankingAdapters.Create(b, new HttpClient(rec));
        var batch = new PostingBatch("B0001", new DateOnly(2026, 12, 15), [
            new("debit", "001", "0001234567890", 1500.00m, "AED", "WPSD", "Salary file B0001"),
            new("credit", "001", "0009876543210", 1500.00m, "AED", "WPSC", "Salary"),
        ]);
        var answer = await cb.Post(batch);
        Assert.True(answer.Posted);
        Assert.Equal("H-1", answer.HostReference);

        var (req, body) = rec.Requests.Single();
        Assert.Equal("https://core-banking.example.invalid/api/v1/postings", req.RequestUri!.ToString());
        Assert.Equal("0332", req.Headers.GetValues("X-Function-Id").Single());
        Assert.Equal("EXHOST", req.Headers.GetValues("X-External-Host-Id").Single());
        Assert.Equal("N", req.Headers.GetValues("X-Txn-Flag").Single());
        var j = (JsonObject)JsonNode.Parse(body)!;
        Assert.Equal("EXSVC01", (string?)j["serviceCode"]);
        Assert.Equal("001", (string?)j["uploadBranch"]);
        Assert.Equal("A", (string?)j["authStatus"]);
        Assert.Equal("EXB0001", (string?)j["reference"]);
        Assert.Equal("20261215", (string?)j["valueDate"]);
        Assert.Equal(2, j["entries"]!.AsArray().Count);

        var bal = await cb.Balance("001", "0001234567890");
        Assert.Equal(2500.50m, bal.Available);
        Assert.Equal("0301", rec.Requests[1].req.Headers.GetValues("X-Function-Id").Single());
    }

    [Fact]
    public void A_port_with_no_binding_and_a_setting_that_is_missing_are_refused_not_defaulted()
    {
        var p = new PortBindings();
        p.Add("kind: port-bindings\ntenant: t\nbindings:\n  - port: p.port.core-banking.v1\n    adapter: gateway-rest\n    validFrom: 2026-01-01T00:00:00Z\n    settings:\n      baseUrl: https://x.invalid/\n");
        Assert.Throws<PortBindingException>(() => p.InForce("other-bank", Port, DateTimeOffset.Parse("2026-06-01T00:00:00Z")));
        Assert.Throws<PortBindingException>(() => p.InForce("t", Port, DateTimeOffset.Parse("2025-06-01T00:00:00Z")));
        var cb = CoreBankingAdapters.Create(p.InForce("t", Port, DateTimeOffset.Parse("2026-06-01T00:00:00Z")), new HttpClient(new Recorder()));
        var e = Assert.ThrowsAsync<PortBindingException>(() => cb.Post(new PostingBatch("R", new DateOnly(2026, 6, 1), [])));
        Assert.Contains("has no 'serviceCode'", e.Result.Message);
        Assert.Throws<PortBindingException>(() => p.Add("kind: port-bindings\ntenant: t\nbindings:\n  - port: p.port.core-banking.v1\n    adapter: simulator\n    validFrom: 2026-01-01T00:00:00Z\n"));
        Assert.Throws<PortBindingException>(() => CoreBankingAdapters.Create(new PortBinding("t", Port, "mainframe-ftp", DateTimeOffset.UnixEpoch, new Dictionary<string, string>())));
    }

    /// <summary>A core banking behind a gateway, as far as the adapter can tell: it records each request and answers.</summary>
    sealed class Recorder : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage req, string body)> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var answer = request.RequestUri!.AbsolutePath.EndsWith("postings") ? "{\"code\":\"0\",\"hostReference\":\"H-1\"}" : "{\"available\":2500.50,\"currency\":\"AED\",\"status\":\"NORM\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
