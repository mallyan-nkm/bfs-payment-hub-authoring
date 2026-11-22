using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TransactHub.Engine.Flowable;
using TransactHub.Engines;
using TransactHub.Store;

namespace TransactHub.Engine.Flowable.Tests;

/// <summary>The store the binding keeps its payloads, its steps and its own state in (Issue I-89).</summary>
public sealed class RestartStore : IAsyncLifetime
{
    readonly PostgreSqlContainer pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public StoreOptions Options => new(StoreProvider.PostgreSql, pg.GetConnectionString());
    public async Task InitializeAsync()
    {
        await pg.StartAsync();
        await using var db = StoreDbContext.Open(Options);
        await db.Database.MigrateAsync();
    }
    public Task DisposeAsync() => pg.DisposeAsync().AsTask();
}

/// <summary>
/// A worker of queue th-p in this test's process, over HTTP, as a deployment's workers are (the deploy runner's simulators): it
/// outlives the binding's process, so a binding that is killed and started again meets the same worker. Every step answers ok
/// and writes a fact and a payload field of its own.
/// </summary>
sealed class HttpWorker : IDisposable
{
    readonly HttpListener listener = new();
    readonly CancellationTokenSource stop = new();
    public readonly string Url;
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Calls = new();

    public HttpWorker()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add($"{Url}/");
        listener.Start();
        _ = Task.Run(Serve);
    }

    async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext c;
            try { c = await listener.GetContextAsync(); } catch { return; }
            var path = c.Request.Url!.AbsolutePath;
            JsonObject answer;
            if (path.EndsWith("/attempt"))
            {
                using var r = new StreamReader(c.Request.InputStream);
                var req = (JsonObject)JsonNode.Parse(await r.ReadToEndAsync())!;
                var step = J.S(req, "step")!;
                Calls.Enqueue($"{step}#{J.Num(req, "attempt")}");
                answer = new JsonObject { ["kind"] = "result", ["outcome"] = "ok", ["writes"] = new JsonObject { [$"facts.{step}"] = "done", [$"payment.{step}Checked"] = true } };
            }
            else if (path.EndsWith("/sent")) answer = new JsonObject { ["messages"] = new JsonArray() };
            else answer = new JsonObject { ["deliveries"] = new JsonArray() };
            var bytes = Encoding.UTF8.GetBytes(answer.ToJsonString());
            c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
    }

    public void Dispose() { stop.Cancel(); listener.Close(); }
}

/// <summary>
/// Issue I-89, R-09-66: a Flowable binding host that is killed in the middle of a payment and started again with the same run key
/// goes on from its state in the store, and the payment ends with the same facts as a run that was never interrupted. On the REST
/// server in production mode (its executor on, real time) and in virtual mode (the binding runs the jobs and keeps the clock).
/// The binding runs as its own process (the runtime's Flowable harness); the worker and the store outlive it.
/// </summary>
[Collection(FlowableCollection.Name)]
public class FlowableRestartTests(FlowableServer virtualServer, FlowableLiveServer liveServer, RestartStore store) : IClassFixture<RestartStore>
{
    const string Workflow = "t.wf.three.v1";
    static readonly string Harness = HarnessDll();

    static string HarnessDll()
    {
        var here = Path.GetDirectoryName(typeof(FlowableRestartTests).Assembly.Location)!;
        var local = Path.Combine(here, "TransactHub.FlowableHarness.dll");
        if (File.Exists(local)) return local;
        throw new FileNotFoundException("The Flowable harness is not beside the tests: it is a project reference of this test project", local);
    }

    /// <summary>Three calls with a three-second wait between the first and the second, as the generator writes them for a REST binding.</summary>
    static string Three(string key)
    {
        string Call(string s) => $$"""
            <serviceTask id="{{s}}_task" flowable:type="external-worker" flowable:topic="th-p:p.check.{{s}}.v1@1.0.0" flowable:exclusive="false">
              <extensionElements>
                <flowable:executionListener event="start" expression="${execution.setVariable('th_attempt', 1)}"/>
                <th:task step="{{s}}" activity="p.check.{{s}}.v1" release="1.0.0" record="business"/>
              </extensionElements>
            </serviceTask>
            <serviceTask id="{{s}}_rec" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="call" step="{{s}}" var="outcome_{{s}}" record="business"/></extensionElements></serviceTask>
            """;
        return Docs.Head + $$"""
          <process id="{{key}}" isExecutable="true">
            <extensionElements><th:workflow name="{{Workflow}}" release="1.0.0" kind="workflow"/></extensionElements>
            <startEvent id="start"/>
            <serviceTask id="th_started" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="processStart"/></extensionElements></serviceTask>
            {{Call("a")}}
            <intermediateCatchEvent id="pause_wait" name="wait"><timerEventDefinition><timeDuration>PT3S</timeDuration></timerEventDefinition></intermediateCatchEvent>
            <serviceTask id="th_text" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="text" text="wait:PT3S"/></extensionElements></serviceTask>
            {{Call("b")}}
            {{Call("c")}}
            <intermediateThrowEvent id="th_endpoint_done"><extensionElements><flowable:executionListener event="start" expression="${execution.setVariable('th_outcome', 'done')}"/></extensionElements></intermediateThrowEvent>
            <serviceTask id="th_end" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="end"/></extensionElements></serviceTask>
            <endEvent id="end"/>
            <sequenceFlow id="f1" sourceRef="start" targetRef="th_started"/>
            <sequenceFlow id="f2" sourceRef="th_started" targetRef="a_task"/>
            <sequenceFlow id="f3" sourceRef="a_task" targetRef="a_rec"/>
            <sequenceFlow id="f4" sourceRef="a_rec" targetRef="pause_wait"/>
            <sequenceFlow id="f5" sourceRef="pause_wait" targetRef="th_text"/>
            <sequenceFlow id="f6" sourceRef="th_text" targetRef="b_task"/>
            <sequenceFlow id="f7" sourceRef="b_task" targetRef="b_rec"/>
            <sequenceFlow id="f8" sourceRef="b_rec" targetRef="c_task"/>
            <sequenceFlow id="f9" sourceRef="c_task" targetRef="c_rec"/>
            <sequenceFlow id="f10" sourceRef="c_rec" targetRef="th_endpoint_done"/>
            <sequenceFlow id="f11" sourceRef="th_endpoint_done" targetRef="th_end"/>
            <sequenceFlow id="f12" sourceRef="th_end" targetRef="end"/>
          </process>
        </definitions>
        """;
    }

    static JsonObject Plan(string e2e, string workerUrl)
    {
        var key = FlowableBinding.Safe($"{Workflow}@1.0.0");
        return new JsonObject
        {
            ["built"] = new JsonObject
            {
                ["id"] = "T-RESTART", ["spec"] = new JsonObject(), ["workflow"] = Workflow,
                ["config"] = new JsonObject { ["evaluationDate"] = "2026-11-17", ["tenant"] = "test-bank", ["values"] = new JsonArray() },
                ["context"] = new JsonObject
                {
                    ["header"] = new JsonObject { ["conversation"] = $"conv-{e2e}", ["scheme"] = "p", ["direction"] = "outward", ["endToEndId"] = e2e },
                    ["payment"] = new JsonObject { ["endToEndId"] = e2e, ["instructedAmount"] = new JsonObject { ["value"] = "10.00", ["currency"] = "AED" } },
                },
            },
            ["documents"] = new JsonObject { [$"{Workflow}@1.0.0"] = new JsonObject { ["kind"] = "workflow", ["name"] = Workflow, ["release"] = "1.0.0" } },
            ["bpmn"] = new JsonObject { [$"{Workflow}@1.0.0.bpmn20.xml"] = Three(key) },
            ["workers"] = new JsonObject { ["th-p"] = workerUrl },
            ["queues"] = new JsonObject { ["p"] = "th-p" },
        };
    }

    sealed record Exit(int Code, string Stdout, string Stderr)
    {
        public JsonObject Observation => Stdout.Split('\n').FirstOrDefault(l => l.StartsWith("OBSERVATION ")) is string l
            ? (JsonObject)JsonNode.Parse(l["OBSERVATION ".Length..])!
            : throw new Xunit.Sdk.XunitException($"the harness gave no observation (exit {Code}):\n{Stdout}\n{Stderr}");
    }

    Process Start(string planFile, string url, string mode, string run, string? fault)
    {
        var psi = new ProcessStartInfo("dotnet", [Harness, planFile]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["TH_FLOWABLE_URL"] = url;
        psi.Environment["TH_FLOWABLE_USER"] = FlowableServer.User;
        psi.Environment["TH_FLOWABLE_PASSWORD"] = FlowableServer.Password;
        psi.Environment["TH_FLOWABLE_MODE"] = mode;
        psi.Environment["TH_FLOWABLE_RUN"] = run;
        psi.Environment["TH_FLOWABLE_POLL"] = "PT0.1S";
        psi.Environment["TH_FLOWABLE_RETRY_WAIT"] = "PT1S";
        psi.Environment["TH_FLOWABLE_MAX_WAIT"] = "PT2M";
        psi.Environment["TH_STORE_PROVIDER"] = "PostgreSql";
        psi.Environment["TH_STORE_CONNECTION"] = store.Options.ConnectionString;
        psi.Environment["TH_FLOWABLE_FAULT"] = fault ?? "";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return Process.Start(psi)!;
    }

    static async Task<Exit> Wait(Process p)
    {
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
        return new Exit(p.ExitCode, await o, await e);
    }

    /// <summary>A payment's facts as a run leaves them, without ids and times: the steps (step, attempt, outcome) and the payload's changes.</summary>
    async Task<(List<string> steps, List<string> payload, int journal, string payloadRef)> Facts(string e2e, string run)
    {
        await using var db = StoreDbContext.Open(store.Options);
        var accepted = (await db.Facts.AsNoTracking().Where(f => f.Code == "accepted" && f.SubjectKind == "payload").ToListAsync())
            .Select(f => (f.SubjectId, payment: J.S(JsonNode.Parse(f.DataJson), "payment"))).ToList();
        var ids = await db.Payments.AsNoTracking().Select(p => p.Id).ToListAsync();
        var payloadRef = "";
        string? payment = null;
        foreach (var (r, pay) in accepted)
        {
            var created = await db.Facts.AsNoTracking().Where(f => f.SubjectId == r && f.Code == "create").Select(f => f.DataJson).FirstAsync();
            if (created.Contains(e2e)) { payloadRef = r; payment = pay; }
        }
        Assert.NotNull(payment);
        var steps = (await db.Facts.AsNoTracking().Where(f => f.SubjectId == payment && f.Kind == "step").OrderBy(f => f.Seq).ToListAsync())
            .Select(f => { var d = JsonNode.Parse(f.DataJson); return $"{J.S(d, "step")}#{J.Num(d, "attempt")}:{f.Code}:{J.Str(J.P(d, "writes"))}"; }).ToList();
        var payload = (await db.Facts.AsNoTracking().Where(f => f.SubjectId == payloadRef).OrderBy(f => f.Seq).ToListAsync())
            .Select(f => { var d = JsonNode.Parse(f.DataJson); return f.Code == "staged" ? $"staged:{J.Str(J.P(d, "writes"))}" : f.Code ?? ""; }).ToList();
        var verify = await new FactLog(db).Verify(payloadRef);
        Assert.True(verify.Ok);
        var journal = await db.Facts.AsNoTracking().CountAsync(f => f.SubjectId == $"flowable-run:{run}");
        return (steps, payload, journal, payloadRef);
    }

    async Task<(JsonObject obs, (List<string> steps, List<string> payload, int journal, string payloadRef) facts, List<string> calls)> Uninterrupted(string url, string mode)
    {
        using var worker = new HttpWorker();
        var e2e = $"E2E-R-{Guid.NewGuid():N}"[..20];
        var run = $"run-{Guid.NewGuid():N}"[..16];
        var file = Path.Combine(Path.GetTempPath(), $"th-restart-{run}.json");
        await File.WriteAllTextAsync(file, Plan(e2e, worker.Url).ToJsonString());
        try
        {
            var exit = await Wait(Start(file, url, mode, run, null));
            Assert.Equal(0, exit.Code);
            return (exit.Observation, await Facts(e2e, run), worker.Calls.ToList());
        }
        finally { File.Delete(file); }
    }

    /// <summary>Kills the binding mid-payment (a fault that exits at a step, or a kill while it waits), starts it again with the same run key, and compares.</summary>
    async Task Restarted(string url, string mode, string? exitAt, bool killWhileWaiting)
    {
        var (baseObs, baseFacts, baseCalls) = await Uninterrupted(url, mode);
        Assert.Equal(["a:ok", "wait:PT3S", "b:ok", "c:ok", "end:done"], J.Strings(J.P(J.A(baseObs, "instances")[0], "trace")));

        using var worker = new HttpWorker();
        var e2e = $"E2E-R-{Guid.NewGuid():N}"[..20];
        var run = $"run-{Guid.NewGuid():N}"[..16];
        var file = Path.Combine(Path.GetTempPath(), $"th-restart-{run}.json");
        await File.WriteAllTextAsync(file, Plan(e2e, worker.Url).ToJsonString());
        try
        {
            var first = Start(file, url, mode, run, exitAt is null ? null : $"exit:{exitAt}");
            if (killWhileWaiting)
            {
                // Killed while the payment waits on the server's timer, once its first step is in its journal.
                for (var until = DateTime.UtcNow.AddMinutes(2); DateTime.UtcNow < until; await Task.Delay(100))
                {
                    await using var db = StoreDbContext.Open(store.Options);
                    if (await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == $"flowable-run:{run}" && f.DataJson.Contains("\"a:ok\""))) break;
                }
                first.Kill(entireProcessTree: true);
                await first.WaitForExitAsync();
            }
            else
            {
                var exit1 = await Wait(first);
                Assert.True(exit1.Code == 75, $"the binding should have stopped at {exitAt} (exit {exit1.Code}):\n{exit1.Stdout}\n{exit1.Stderr}");
            }
            var second = await Wait(Start(file, url, mode, run, exitAt is null ? null : $"exit:{exitAt}"));
            Assert.True(second.Code == 0, second.Stdout + second.Stderr);
            var obs = second.Observation;
            var root = (JsonObject)J.A(obs, "instances")[0]!;
            Assert.Equal(J.Strings(J.P(J.A(baseObs, "instances")[0], "trace")), J.Strings(J.P(root, "trace")));
            Assert.Equal(1, (int)J.Num(J.P(obs, "engine"), "restarts")!);
            Assert.Contains(J.Strings(J.P(J.P(obs, "engine"), "log")), l => l.StartsWith("restarted (restart 1)"));
            // The same facts as the run that was never interrupted: each step once, the payload's changes once, in the same order.
            var facts = await Facts(e2e, run);
            // What the restarted binding holds is what the store rebuilds from the payload's facts.
            var mine = J.A(J.P(J.P(obs, "engine"), "store"), "payloads").Single(x => J.S(x, "ref") == facts.payloadRef);
            Assert.True(J.Truthy(J.P(mine, "rebuilt")) && J.Truthy(J.P(mine, "chain")));
            Assert.Equal(baseFacts.steps, facts.steps);
            Assert.Equal(baseFacts.payload, facts.payload);
            Assert.Equal(["a#1", "b#1", "c#1"], baseCalls);
            // A step answered before the stop is not called again.
            Assert.Equal(baseCalls, worker.Calls.ToList());
            // The journal is closed: the run is not run again.
            await using (var db = StoreDbContext.Open(store.Options))
                Assert.Equal("closed", (await db.Facts.AsNoTracking().Where(f => f.SubjectId == $"flowable-run:{run}").OrderByDescending(f => f.Seq).FirstAsync()).Code);
            var again = await Wait(Start(file, url, mode, run, null));
            Assert.NotEqual(0, again.Code);
            Assert.Contains("ended at", again.Stdout);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public Task Production_a_binding_killed_while_the_payment_waits_on_the_server_goes_on_from_the_store() =>
        Restarted(liveServer.Url, "production", null, killWhileWaiting: true);

    [Fact]
    public Task Production_a_binding_that_stops_after_a_steps_answer_completes_it_without_calling_again() =>
        Restarted(liveServer.Url, "production", "b", killWhileWaiting: false);

    [Fact]
    public Task Virtual_a_binding_that_stops_after_a_steps_answer_goes_on_with_its_clock() =>
        Restarted(virtualServer.Url, "virtual", "c", killWhileWaiting: false);
}
