using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// The other hosts' logs in the service console (R-09-71, Issue I-103): read from the installation's log back end (a fake Loki
/// here) by trace id, masked; a back end that does not answer is an item with its code and runbook; the store's facts and the
/// console's own logs are found by the same trace id (R-09-69); an engine's failure of a payment is shown with its runbook
/// (R-09-70).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class LogSourceTests(Databases dbs)
{
    /// <summary>A Loki that answers query_range with one line of another host, and records what it was asked.</summary>
    sealed class FakeLoki(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public readonly List<Uri> Asked = [];
        public string? Auth;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Asked.Add(r.RequestUri!);
            Auth = r.Headers.Authorization?.ToString();
            var q = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["query"] ?? "";
            var trace = System.Text.RegularExpressions.Regex.Match(q, "[0-9a-f]{32}").Value;
            var ns = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var body = new JsonObject
            {
                ["status"] = "success",
                ["data"] = new JsonObject
                {
                    ["resultType"] = "streams",
                    ["result"] = new JsonArray(new JsonObject
                    {
                        ["stream"] = new JsonObject { ["service_name"] = "transacthub-engine-host", ["level"] = "Error" },
                        ["values"] = new JsonArray(new JsonArray(ns, $"activity attempt failed for account AE070331234567890123456 trace={trace}")),
                    }),
                },
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString()) });
        }
    }

    [Fact]
    public async Task Reads_another_hosts_logs_by_trace_masked_and_finds_the_facts_of_that_trace()
    {
        using var s = new Signers();
        var loki = new FakeLoki();
        Environment.SetEnvironmentVariable("TH_TEST_LOKI_TOKEN", "synthetic-loki-token");
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), new() { ["Service:LogSource:Url"] = "https://loki.example.invalid", ["Service:LogSource:TokenRef"] = "env:TH_TEST_LOKI_TOKEN" }, logs: loki);
        var c = api.Engineer;
        await c.GetAsync("/service/api/me");
        var bulk = await Seed.Bulk(api.Store, "p.wf.corporate.v1");
        // A fact written in a trace, and an engine's failure of the payment.
        using var source = new ActivitySource("TransactHub.Service.Tests.Logs");
        using var listener = new ActivityListener { ShouldListenTo = x => x.Name == source.Name, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded };
        ActivitySource.AddActivityListener(listener);
        string trace;
        await using (var db = StoreDbContext.Open(api.Store))
        using (var a = source.StartActivity("attempt")!)
        {
            trace = a.TraceId.ToHexString();
            await new FactLog(db).Append(bulk, "bulk", new NewFact("step", "retry", new JsonObject { ["instance"] = "root", ["step"] = "submit", ["error"] = "TransientError" }, Actor: "engine"));
            await EngineFailures.Record(db, bulk, "temporal", "root", "workflow task failed");
        }
        await api.CatchUp();

        var r = await J.Of(await c.GetAsync($"/service/api/logs?trace={trace}"));
        var items = r["items"]!.AsArray();
        // The facts of that trace, each with its trace id; none of another trace (the bulk's earlier facts were written outside it).
        var facts = items.Where(i => (string?)i!["source"] == "fact").ToList();
        Assert.Equal(["failed", "retry"], facts.Select(x => (string?)x!["code"]).Order().ToArray());
        Assert.All(facts, x => Assert.Equal(trace, (string?)x!["traceId"]));
        // The other host's line, masked, asked of the back end by the trace id, with the token by reference.
        var line = Assert.Single(items, i => (string?)i!["source"] == "backend")!;
        Assert.Equal("transacthub-engine-host", (string?)line["category"]);
        Assert.DoesNotContain("AE070331234567890123456", line.ToJsonString());
        Assert.Contains(trace, Uri.UnescapeDataString(loki.Asked.Single().Query));
        Assert.Equal("Bearer synthetic-loki-token", loki.Auth);
        // The engine's failure, with the dictionary's code and its runbook.
        var failed = await J.Of(await c.GetAsync($"/service/api/logs?payment={bulk}&code=temporal.WorkflowTaskFailed"));
        var f = Assert.Single(failed["items"]!.AsArray())!;
        Assert.Equal(("engine", "failed", "Error"), ((string?)f["kind"], (string?)f["code"], (string?)f["level"]));
        Assert.Equal("#/service/errors/temporal.WorkflowTaskFailed", (string?)f["errors"]![0]!["runbook"]);
        // The back end's lines are not a tenant's: an engineer of one tenant does not get them.
        var mine = await J.Of(await api.As("se-2", "example-bank:ServiceEngineer").GetAsync($"/service/api/logs?trace={trace}"));
        Assert.DoesNotContain(mine["items"]!.AsArray(), i => (string?)i!["source"] == "backend");
        Assert.Single(loki.Asked);
    }

    [Fact]
    public async Task A_log_back_end_that_does_not_answer_is_an_item_with_its_code_and_runbook()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), new() { ["Service:LogSource:Url"] = "https://loki.example.invalid/" }, logs: new FakeLoki(HttpStatusCode.BadGateway));
        var r = await J.Of(await api.Engineer.GetAsync($"/service/api/logs?trace={new string('a', 32)}&source=backend"));
        var item = Assert.Single(r["items"]!.AsArray())!;
        Assert.Equal("Error", (string?)item["level"]);
        Assert.Equal("service.LogSourceUnreachable", (string?)item["errors"]![0]!["code"]);
    }
}
