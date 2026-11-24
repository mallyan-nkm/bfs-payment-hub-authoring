using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TransactHub.Engines;
using TransactHub.Store;

namespace TransactHub.EngineHost.Tests;

/// <summary>
/// The servers the long-running hosts use, once for the run: the store on PostgreSQL 16, a real Temporal server (the CLI image's
/// development server, worker versioning on), and the Flowable REST server with its async executor on (production mode).
/// </summary>
public sealed class Servers : IAsyncLifetime
{
    public const string FlowableUser = "th-host", FlowablePassword = "th-test-only";
    readonly PostgreSqlContainer pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
    readonly IContainer temporal = new ContainerBuilder("temporalio/temporal:1.5.1")
        .WithCommand("server", "start-dev", "--ip", "0.0.0.0", "--headless",
            "--dynamic-config-value", "system.enableDeploymentVersions=true",
            "--dynamic-config-value", "frontend.workerVersioningWorkflowAPIs=true",
            "--dynamic-config-value", "frontend.workerVersioningDataAPIs=true")
        .WithPortBinding(7233, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("temporal", "operator", "cluster", "health"))
        .Build();
    readonly IContainer flowable = new ContainerBuilder("flowable/flowable-rest:7.1.0@sha256:b7ae287502cd5c243b577a0ca2894172e1c18cd75aea4fd67dc7d58f1b40db44")
        .WithEnvironment("FLOWABLE_ASYNC_EXECUTOR_ACTIVATE", "true")
        .WithEnvironment("FLOWABLE_PROCESS_ASYNC_EXECUTOR_DEFAULT_TIMER_JOB_ACQUIRE_WAIT_TIME", "PT1S")
        .WithEnvironment("FLOWABLE_PROCESS_ASYNC_EXECUTOR_DEFAULT_ASYNC_JOB_ACQUIRE_WAIT_TIME", "PT1S")
        .WithEnvironment("FLOWABLE_PROCESS_ASYNC_EXECUTOR_RESET_EXPIRED_JOBS_INTERVAL", "PT1S")
        .WithEnvironment("FLOWABLE_REST_APP_CREATE_DEMO_DEFINITIONS", "false")
        .WithEnvironment("FLOWABLE_REST_APP_ADMIN_USER_ID", FlowableUser)
        .WithEnvironment("FLOWABLE_REST_APP_ADMIN_PASSWORD", FlowablePassword)
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/flowable-rest/service/management/engine").WithBasicAuthentication(FlowableUser, FlowablePassword)))
        .Build();

    public StoreOptions Store => new(StoreProvider.PostgreSql, pg.GetConnectionString());
    public string TemporalAddress => $"{temporal.Hostname}:{temporal.GetMappedPublicPort(7233)}";
    public string FlowableUrl => $"http://{flowable.Hostname}:{flowable.GetMappedPublicPort(8080)}/flowable-rest";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(pg.StartAsync(), temporal.StartAsync(), flowable.StartAsync());
        await using var db = StoreDbContext.Open(Store);
        await db.Database.MigrateAsync();
    }

    /// <summary>The store on SQL Server 2022 as well, started the first time a test asks for it (the restart tests run on both, I-165).</summary>
    Testcontainers.MsSql.MsSqlContainer? sql;
    readonly SemaphoreSlim sqlGate = new(1, 1);

    public async Task<StoreOptions> SqlServer()
    {
        await sqlGate.WaitAsync();
        try
        {
            if (sql is null)
            {
                var c = new Testcontainers.MsSql.MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await c.StartAsync();
                await using (var db = StoreDbContext.Open(new StoreOptions(StoreProvider.SqlServer, c.GetConnectionString())))
                    await db.Database.MigrateAsync();
                sql = c;
            }
            return new StoreOptions(StoreProvider.SqlServer, sql.GetConnectionString());
        }
        finally { sqlGate.Release(); }
    }

    public async Task DisposeAsync()
    {
        if (sql is not null) await sql.DisposeAsync();
        await pg.DisposeAsync();
        await temporal.DisposeAsync();
        await flowable.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class HostCollection : ICollectionFixture<Servers> { public const string Name = "engine-hosts"; }

/// <summary>A worker of queue th-p, as a deployment's are: a check answers ok with its release; an approval opens a hold.</summary>
sealed class Worker : IDisposable
{
    readonly HttpListener listener = new();
    public readonly string Url;
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Calls = new();
    /// <summary>Each attempt's payment and the distribution it names (R-09-130): <c>payment=distribution</c>.</summary>
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Named = new();

    public Worker()
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
        for (;;)
        {
            HttpListenerContext c;
            try { c = await listener.GetContextAsync(); } catch { return; }
            var path = c.Request.Url!.AbsolutePath;
            JsonObject answer;
            if (path.EndsWith("/attempt"))
            {
                using var r = new StreamReader(c.Request.InputStream);
                var req = (JsonObject)JsonNode.Parse(await r.ReadToEndAsync())!;
                var payment = J.S(J.P(J.P(req, "ctx"), "header"), "paymentId");
                var activity = J.S(req, "activity")!;
                var release = J.S(req, "release");
                Calls.Enqueue($"{payment}:{J.S(req, "step")}@{release}");
                Named.Enqueue($"{payment}={J.S(req, "distribution")}");
                answer = activity.StartsWith("p.hold.")
                    ? new JsonObject { ["kind"] = "held", ["hold"] = $"HOLD-{payment}", ["expiry"] = "P1D" }
                    : new JsonObject { ["kind"] = "result", ["outcome"] = "ok", ["writes"] = new JsonObject { [$"facts.{J.S(req, "step")}"] = release } };
            }
            else if (path.EndsWith("/sent")) answer = new JsonObject { ["messages"] = new JsonArray() };
            else answer = new JsonObject { ["deliveries"] = new JsonArray() };
            var bytes = Encoding.UTF8.GetBytes(answer.ToJsonString());
            c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
    }

    public void Dispose() => listener.Close();
}

/// <summary>
/// Issues I-114 and I-127, R-09-66 to R-09-68: a long-running engine host serves several distributions of a bank side by side,
/// each its own copy. A payment pinned to distribution A keeps running on A after B becomes active; new payments go to B; A drains,
/// is recorded drained once its last payment ends, may be removed only then, and on removal its host unregisters it from the engine
/// and stops while B's goes on. On Temporal this is worker versioning by build id on a real server: A's payment is pinned to A's
/// version, which Temporal reports draining once B's is current. A payment waits for an operator's decision on its hold in the host,
/// in wall time, with no harness patience.
/// </summary>
[Collection(HostCollection.Name)]
public partial class EngineHostTests(Servers servers)
{
    /// <summary>The store a test runs on: PostgreSQL unless it asks for SQL Server.</summary>
    StoreOptions? store;
    StoreOptions Db => store ?? servers.Store;

    const string Environment = "test";
    /// <summary>A synthetic token for this test run's supervisor (R-08-22).</summary>
    static readonly string HostToken = $"synthetic-host-token-{Guid.NewGuid():N}";
    static readonly string Runtime = RuntimeDir();

    static string RuntimeDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TransactHub.Runtime.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("runtime/");
    }

    static readonly object buildGate = new();

    /// <summary>The engine host and an engine's runtime harness, built once where they are missing.</summary>
    static string Built(string tool, string dll)
    {
        lock (buildGate)
        {
            var dir = Path.Combine(Runtime, "tools", tool);
            var found = Directory.Exists(Path.Combine(dir, "bin", "Release")) ? Directory.GetFiles(Path.Combine(dir, "bin", "Release"), dll, SearchOption.AllDirectories).FirstOrDefault() : null;
            if (found is not null) return found;
            var b = Process.Start(new ProcessStartInfo("dotnet", ["build", "-c", "Release", "--nologo", "-v", "q", "-nodeReuse:false"]) { WorkingDirectory = dir, RedirectStandardOutput = true, UseShellExecute = false })!;
            var output = b.StandardOutput.ReadToEnd();
            b.WaitForExit();
            Assert.True(b.ExitCode == 0, output);
            return Directory.GetFiles(Path.Combine(dir, "bin", "Release"), dll, SearchOption.AllDirectories).First();
        }
    }

    static JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>A distribution's documents: the payment workflow and its activities at the distribution's release.</summary>
    static JsonObject Documents(string release) => new()
    {
        [$"p.wf.pay.v1@{release}"] = Doc($$"""
            { "kind": "workflow", "name": "p.wf.pay.v1", "release": "{{release}}", "do": [
              { "id": "a", "call": "p.enrich.check.v1" },
              { "id": "h", "call": "p.hold.approve.v1", "handle": { "declined": { "end": "declined" }, "expired": { "end": "expired" } } },
              { "id": "b", "call": "p.enrich.check.v1" },
              { "end": "done" } ] }
            """),
        [$"p.enrich.check.v1@{release}"] = Doc($$"""
            { "kind": "activity", "name": "p.enrich.check.v1", "release": "{{release}}", "type": "enrich", "reads": ["header"], "writes": ["facts.a", "facts.b"],
              "outcomes": [ { "name": "ok", "primary": true } ], "effect": "read" }
            """),
        [$"p.hold.approve.v1@{release}"] = Doc($$"""
            { "kind": "activity", "name": "p.hold.approve.v1", "release": "{{release}}", "type": "hold", "reads": ["header"], "writes": ["facts.approval"],
              "outcomes": [ { "name": "approved", "primary": true }, { "name": "declined" }, { "name": "expired" } ], "effect": "write",
              "idempotencyKey": "concat(header.paymentId, ':approve')", "parameters": { "expiry": { "type": "duration", "default": "P1D" } } }
            """),
    };

    /// <summary>On Flowable, the same payment with a wait instead of the hold (A waits twenty seconds, B one), as the generator writes it for a REST binding.</summary>
    static string Bpmn(string release, string wait)
    {
        var key = $"p_wf_pay_v1_{release.Replace('.', '_')}";
        string Call(string s) => $$"""
            <serviceTask id="{{s}}_task" flowable:type="external-worker" flowable:topic="th-p:p.enrich.check.v1@{{release}}" flowable:exclusive="false">
              <extensionElements>
                <flowable:executionListener event="start" expression="${execution.setVariable('th_attempt', 1)}"/>
                <th:task step="{{s}}" activity="p.enrich.check.v1" release="{{release}}" record="business"/>
              </extensionElements>
            </serviceTask>
            <serviceTask id="{{s}}_rec" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="call" step="{{s}}" var="outcome_{{s}}" record="business"/></extensionElements></serviceTask>
            """;
        return $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:flowable="http://flowable.org/bpmn" xmlns:th="urn:transacthub:flowable:1" targetNamespace="urn:test">
          <process id="{{key}}" isExecutable="true">
            <extensionElements><th:workflow name="p.wf.pay.v1" release="{{release}}" kind="workflow"/></extensionElements>
            <startEvent id="start"/>
            <serviceTask id="th_started" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="processStart"/></extensionElements></serviceTask>
            {{Call("a")}}
            <intermediateCatchEvent id="w_wait" name="wait"><timerEventDefinition><timeDuration>{{wait}}</timeDuration></timerEventDefinition></intermediateCatchEvent>
            <serviceTask id="th_text" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="text" text="wait:{{wait}}"/></extensionElements></serviceTask>
            {{Call("b")}}
            <intermediateThrowEvent id="th_endpoint_done"><extensionElements><flowable:executionListener event="start" expression="${execution.setVariable('th_outcome', 'done')}"/></extensionElements></intermediateThrowEvent>
            <serviceTask id="th_end" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="end"/></extensionElements></serviceTask>
            <endEvent id="end"/>
            <sequenceFlow id="f1" sourceRef="start" targetRef="th_started"/>
            <sequenceFlow id="f2" sourceRef="th_started" targetRef="a_task"/>
            <sequenceFlow id="f3" sourceRef="a_task" targetRef="a_rec"/>
            <sequenceFlow id="f4" sourceRef="a_rec" targetRef="w_wait"/>
            <sequenceFlow id="f5" sourceRef="w_wait" targetRef="th_text"/>
            <sequenceFlow id="f6" sourceRef="th_text" targetRef="b_task"/>
            <sequenceFlow id="f7" sourceRef="b_task" targetRef="b_rec"/>
            <sequenceFlow id="f8" sourceRef="b_rec" targetRef="th_endpoint_done"/>
            <sequenceFlow id="f9" sourceRef="th_endpoint_done" targetRef="th_end"/>
            <sequenceFlow id="f10" sourceRef="th_end" targetRef="end"/>
          </process>
        </definitions>
        """;
    }

    static JsonObject Plan(string engine, string release, string workerUrl, string wait) => new()
    {
        ["built"] = new JsonObject
        {
            ["id"] = "HOST", ["spec"] = new JsonObject(), ["workflow"] = "p.wf.pay.v1",
            ["config"] = new JsonObject { ["evaluationDate"] = "2026-11-17", ["tenant"] = "test-bank", ["values"] = new JsonArray() },
            ["context"] = new JsonObject(),
        },
        ["documents"] = Documents(release),
        ["workers"] = new JsonObject { ["th-p"] = workerUrl },
        ["queues"] = new JsonObject { ["p"] = "th-p" },
        ["chunkSize"] = 100, ["itemConcurrency"] = 1,
        ["bpmn"] = engine == "flowable" ? new JsonObject { [$"p.wf.pay.v1@{release}.bpmn20.xml"] = Bpmn(release, wait) } : null,
    };

    static JsonObject Context(string paymentId) => new()
    {
        ["header"] = new JsonObject { ["conversation"] = $"conv-{paymentId}", ["scheme"] = "p", ["direction"] = "outward", ["paymentId"] = paymentId, ["endToEndId"] = paymentId },
        ["payment"] = new JsonObject { ["endToEndId"] = paymentId, ["instructedAmount"] = new JsonObject { ["value"] = "10.00", ["currency"] = "AED" } },
    };

    async Task<T> WithRegistry<T>(Func<DistributionRegistry, Task<T>> f)
    {
        await using var db = StoreDbContext.Open(Db);
        return await f(new DistributionRegistry(db));
    }

    async Task Install(string bank, string id)
    {
        var package = Encoding.UTF8.GetBytes($"synthetic distribution {bank} {id}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(package));
        await WithRegistry(r => r.Store(bank, id, package, new JsonObject { ["id"] = id, ["bank"] = bank }, hash, "test", "e2e"));
    }

    /// <summary>The store's payment a run accepted for a payment id (its payload's create fact carries the id).</summary>
    async Task<string?> SubjectOf(string paymentId)
    {
        await using var db = StoreDbContext.Open(Db);
        var creates = await db.Facts.AsNoTracking().Where(f => f.SubjectKind == "payload" && f.Code == "create" && f.DataJson.Contains(paymentId)).Select(f => f.SubjectId).ToListAsync();
        foreach (var r in creates)
        {
            var acc = await db.Facts.AsNoTracking().Where(f => f.SubjectId == r && f.Code == "accepted").Select(f => f.DataJson).FirstOrDefaultAsync();
            if (acc is not null && J.S(JsonNode.Parse(acc), "payment") is string p) return p;
        }
        return null;
    }

    async Task<bool> StepRecorded(string paymentId, string code)
    {
        if (await SubjectOf(paymentId) is not string subject) return false;
        await using var db = StoreDbContext.Open(Db);
        return await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == subject && f.Kind == "step" && f.Code == code);
    }

    /// <summary>The operator's decision on the payment's hold, as the operations console puts it on the outbox (R-05-57).</summary>
    async Task Decide(string paymentId)
    {
        var subject = await SubjectOf(paymentId) ?? throw new Xunit.Sdk.XunitException($"{paymentId} was not accepted");
        await using var db = StoreDbContext.Open(Db);
        db.Outbox.Add(new OutboxRow
        {
            Topic = "engine-signal", Key = subject, At = DateTimeOffset.UtcNow,
            PayloadJson = CanonicalJson.Of(new JsonObject { ["tenant"] = "test-bank", ["subject"] = subject, ["signal"] = "decide", ["action"] = Guid.NewGuid().ToString("N"), ["hold"] = $"HOLD-{paymentId}", ["decision"] = "approved", ["by"] = "test.supervisor" }),
        });
        await db.SaveChangesAsync();
    }

    static async Task Until(Func<Task<bool>> ok, TimeSpan within, string what)
    {
        for (var until = DateTime.UtcNow + within; ; await Task.Delay(250))
        {
            if (await ok()) return;
            if (DateTime.UtcNow > until) throw new Xunit.Sdk.XunitException($"timed out waiting for {what}");
        }
    }

    sealed class Supervisor(Process p, string url, StringBuilder log) : IAsyncDisposable
    {
        public readonly string Url = url;
        public string Log { get { lock (log) return log.ToString(); } }
        public Process Process => p;
        public async Task<(int status, JsonObject body)> Call(string method, string path, JsonObject? body = null)
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var req = new HttpRequestMessage(new HttpMethod(method), $"{Url}{path}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", HostToken);
            if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await h.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            return ((int)res.StatusCode, JsonNode.Parse(text.Length == 0 ? "{}" : text) as JsonObject ?? new JsonObject());
        }
        public async Task<JsonObject> Distribution(string id) =>
            (JsonObject)J.A((await Call("GET", "/distributions")).body, "distributions").First(d => J.S(d, "id") == id)!;
        public async ValueTask DisposeAsync()
        {
            try { p.Kill(entireProcessTree: true); await p.WaitForExitAsync(); } catch { }
        }
    }

    async Task<Supervisor> StartSupervisor(string engine, string bank, string plans)
    {
        var dll = Built("TransactHub.EngineHost", "TransactHub.EngineHost.dll");
        if (engine != "temporal") Built($"TransactHub.{char.ToUpperInvariant(engine[0])}{engine[1..]}Harness", $"TransactHub.{char.ToUpperInvariant(engine[0])}{engine[1..]}Harness.dll");
        var psi = new ProcessStartInfo("dotnet", [dll, "supervise", "--engine", engine, "--bank", bank, "--environment", Environment, "--plans", plans])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["TH_STORE_PROVIDER"] = Db.Provider.ToString();
        psi.Environment["TH_STORE_CONNECTION"] = Db.ConnectionString;
        psi.Environment["TH_TEMPORAL_ADDRESS"] = servers.TemporalAddress;
        psi.Environment["TH_FLOWABLE_URL"] = servers.FlowableUrl;
        psi.Environment["TH_FLOWABLE_USER"] = Servers.FlowableUser;
        psi.Environment["TH_FLOWABLE_PASSWORD"] = Servers.FlowablePassword;
        psi.Environment["TH_FLOWABLE_POLL"] = "PT0.1S";
        psi.Environment["TH_HOST_POLL"] = "PT0.5S";
        psi.Environment["TH_HOST_UNREGISTER_WAIT"] = "PT7M";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // The supervisor and its hosts take their callers' token (R-08-22), here by reference to a variable.
        psi.Environment["TH_ENGINE_HOST_TOKEN"] = "env:TH_TEST_ENGINE_HOST_TOKEN";
        psi.Environment["TH_TEST_ENGINE_HOST_TOKEN"] = HostToken;
        var p = Process.Start(psi)!;
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new StringBuilder();
        _ = Task.Run(async () => { while (await p.StandardOutput.ReadLineAsync() is string l) if (l.StartsWith("LISTENING ")) ready.TrySetResult(l[10..].Trim()); ready.TrySetException(new Exception("the supervisor exited:\n" + log)); });
        _ = Task.Run(async () => { while (await p.StandardError.ReadLineAsync() is string l) lock (log) log.AppendLine(l); });
        return new Supervisor(p, await ready.Task.WaitAsync(TimeSpan.FromMinutes(2)), log);
    }

    /// <summary>The switch-over on one engine, end to end through the supervisor, the store and the engine.</summary>
    async Task SwitchOver(string engine)
    {
        var bank = $"host-{engine}-{Guid.NewGuid():N}"[..20];
        var (idA, idB) = ("2026.11.22-1", "2026.11.22-2");
        var (distA, distB) = (DistributionRegistry.IdOf(bank, idA), DistributionRegistry.IdOf(bank, idB));
        using var worker = new Worker();
        var plans = Path.Combine(Path.GetTempPath(), $"th-hosts-{bank}");
        Directory.CreateDirectory(plans);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(plans, $"{TransactHub.EngineHost.Tests.Safe.Of(distA)}.json"), Plan(engine, "1.0.0", worker.Url, "PT20S").ToJsonString());
            await File.WriteAllTextAsync(Path.Combine(plans, $"{TransactHub.EngineHost.Tests.Safe.Of(distB)}.json"), Plan(engine, "1.1.0", worker.Url, "PT1S").ToJsonString());
            await Install(bank, idA);
            await Install(bank, idB);
            await WithRegistry(r => r.Activate(distA, Environment, "test", "e2e"));
            await using var sup = await StartSupervisor(engine, bank, plans);
            try { await Steps(sup); }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException || !e.Message.Contains("--- the supervisor's log ---"))
            {
                throw new Xunit.Sdk.XunitException($"{e.Message}\n{e.StackTrace}\n--- the supervisor's log ---\n{sup.Log}");
            }
        }
        finally { try { Directory.Delete(plans, true); } catch { } }

        async Task Steps(Supervisor sup)
        {
            // Both installed distributions have a host side by side; A's is current on the engine.
            await Until(async () => J.Truthy(J.P(J.P(await sup.Distribution(distA), "host"), "current")), TimeSpan.FromMinutes(3), $"{distA} current");
            Assert.NotNull(J.P(await sup.Distribution(distB), "host"));

            // P1 is accepted while A is active: pinned to A, it waits on A (a hold, or on Flowable a long wait).
            var p1 = $"P1-{bank}";
            var r1 = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p1, ["context"] = Context(p1) });
            Assert.True(r1.status == 202, r1.body.ToJsonString());
            Assert.Equal(distA, J.S(r1.body, "distribution"));
            await Until(() => StepRecorded(p1, engine == "flowable" ? "ok" : "held"), TimeSpan.FromMinutes(3), $"{p1} on its hold");

            // The switch-over: B active, A draining in the same step. From now new payments are pinned to B and run on B's host.
            var (active, draining) = await WithRegistry(r => r.Activate(distB, Environment, "test", "e2e"));
            Assert.Equal((distB, distA), (active, draining));
            await Until(async () => J.Truthy(J.P(J.P(await sup.Distribution(distB), "host"), "current")), TimeSpan.FromMinutes(3), $"{distB} current");
            var p2 = $"P2-{bank}";
            var r2 = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p2, ["context"] = Context(p2) });
            Assert.Equal(distB, J.S(r2.body, "distribution"));
            if (engine == "temporal")
            {
                // On the engine too: A's version drains once B's is current, while P1 is pinned to it.
                await Until(async () => J.S(J.P(await sup.Distribution(distA), "engine"), "drainage") == "draining", TimeSpan.FromMinutes(2), "A draining on Temporal");
                Assert.True((long)J.Num(J.P(await sup.Distribution(distA), "engine"), "openOnServer")! >= 1);
            }

            // A may not be removed while P1 is open on it.
            var refused = await Assert.ThrowsAsync<DistributionRefused>(() => WithRegistry(async r => { await r.Remove(distA, Environment, "test", "e2e"); return 0; }));
            Assert.Contains("open payment", refused.Message);
            Assert.Equal(1, (int)J.Num(await sup.Distribution(distA), "open")!);

            // P1 goes on on A, P2 on B: the operator decides each hold (Flowable: the waits end on the server's clock).
            if (engine != "flowable")
            {
                await Until(() => StepRecorded(p2, "held"), TimeSpan.FromMinutes(3), $"{p2} on its hold");
                await Decide(p2);
                await Decide(p1);
            }
            JsonObject? e1 = null, e2 = null;
            await Until(async () => J.S(e1 = (await sup.Call("GET", $"/payments/{p1}")).body, "status") is "ended" or "failed", TimeSpan.FromMinutes(5), $"{p1} ended");
            await Until(async () => J.S(e2 = (await sup.Call("GET", $"/payments/{p2}")).body, "status") is "ended" or "failed", TimeSpan.FromMinutes(5), $"{p2} ended");
            Assert.True(J.S(e1, "outcome") == "done", e1!.ToJsonString());
            Assert.True(J.S(e2, "outcome") == "done", e2!.ToJsonString());
            var rt1 = J.Strings(J.P(J.A(J.P(e1, "observation"), "instances")[0], "releaseTrace"));
            var rt2 = J.Strings(J.P(J.A(J.P(e2, "observation"), "instances")[0], "releaseTrace"));
            Assert.Contains("pay@1.0.0", rt1);
            Assert.DoesNotContain(rt1, x => x.Contains("1.1.0"));
            Assert.Contains("pay@1.1.0", rt2);
            Assert.DoesNotContain(rt2, x => x.Contains("1.0.0"));
            Assert.Contains($"{p1}:b@1.0.0", worker.Calls);
            Assert.Contains($"{p2}:b@1.1.0", worker.Calls);
            // One worker serves both distributions: every attempt names its payment's distribution, for the native host (R-09-130, R-09-133).
            Assert.All(worker.Named.Where(x => x.StartsWith($"{p1}=", StringComparison.Ordinal)), x => Assert.Equal($"{p1}={distA}", x));
            Assert.All(worker.Named.Where(x => x.StartsWith($"{p2}=", StringComparison.Ordinal)), x => Assert.Equal($"{p2}={distB}", x));
            Assert.Contains($"{p1}={distA}", worker.Named);
            Assert.Contains($"{p2}={distB}", worker.Named);
            if (engine == "temporal")
            {
                // Every workflow of each payment ran on its own distribution's version of the worker deployment, pinned.
                foreach (var (e, id) in new[] { (e1, idA), (e2, idB) })
                {
                    var served = J.A(J.P(J.P(J.P(e, "observation"), "engine"), "distribution"), "servedOn");
                    Assert.NotEmpty(served);
                    Assert.All(served, s => { Assert.Equal(id, J.S(s, "buildId")); Assert.Equal("Pinned", J.S(s, "behavior")); Assert.Equal($"transacthub-{bank}-{Environment}", J.S(s, "deployment")); });
                }
            }
            else Assert.Equal(distA, J.S(J.P(J.P(J.P(e1, "observation"), "engine"), "distribution"), "id"));

            // A drains: recorded drained once its last payment ended, and only then removed; its host unregisters it and stops, B's goes on.
            await Until(async () => J.S(await sup.Distribution(distA), "state") == "drained", TimeSpan.FromMinutes(2), "A drained");
            await WithRegistry(async r => { await r.Remove(distA, Environment, "test", "e2e"); return 0; });
            await Until(async () => J.P(await sup.Distribution(distA), "host") is null, TimeSpan.FromMinutes(8), "A's host stopped");
            var b = await sup.Distribution(distB);
            Assert.Equal("active", J.S(b, "state"));
            Assert.True(J.Truthy(J.P(J.P(b, "host"), "current")));
            var events = J.A((await sup.Call("GET", "/events")).body, "events");
            var unregistered = events.First(x => J.S(x, "event") == "unregistered from the engine" && J.S(x, "distribution") == distA);
            if (engine == "temporal")
            {
                Assert.True(J.Truthy(J.P(unregistered, "workersStopped")));
                Assert.True(J.Truthy(J.P(unregistered, "versionDeleted")), unregistered!.ToJsonString());
            }
            // A third payment goes to B.
            var p3 = $"P3-{bank}";
            Assert.Equal(distB, J.S((await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p3, ["context"] = Context(p3) })).body, "distribution"));
        }
    }

    /// <summary>A distribution's stored package as the studio builds it: an engine package per platform workflow (engine/&lt;wf&gt;/).</summary>
    /// <param name="flowable">The package as the studio exports it for Flowable too: the platform workflow's BPMN beside its documents (I-269).</param>
    static byte[] Package(string release, bool flowable = false)
    {
        var docs = Documents(release);
        docs[$"p.wf.ask.v1@{release}"] = Doc($$"""
            { "kind": "workflow", "wfKind": "platform", "name": "p.wf.ask.v1", "release": "{{release}}", "do": [
              { "id": "a", "call": "p.enrich.check.v1" },
              { "id": "w", "await": { "message": "p.test.answer", "timeout": "PT3M", "onTimeout": { "end": "timedout" } } },
              { "id": "b", "call": "p.enrich.check.v1" },
              { "end": "done" } ] }
            """);
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Put(string name, string text) { using var w = new StreamWriter(z.CreateEntry(name).Open()); w.Write(text); }
            Put("manifest.yaml", $"kind: distribution\nrelease: {release}\n");
            Put("engine/p.wf.ask.v1/binding.yaml", "kind: engine-binding\nqueues:\n  p: th-p\n");
            foreach (var (k, v) in docs) Put($"engine/p.wf.ask.v1/{(k.StartsWith("p.wf.") ? "workflows" : "activities")}/{k}.yaml", v!.ToJsonString());
            if (flowable) Put($"engine/p.wf.ask.v1/flowable/p.wf.ask.v1@{release}.bpmn20.xml", AskBpmn(release));
        }
        return ms.ToArray();
    }

    /// <summary>
    /// R-09-93, R-09-94: with no plan written by a caller, the supervisor builds the host's plan from the stored package; a payment
    /// runs in real time in the host and takes a message the front door correlated to it after its run started (here a message its
    /// platform workflow awaits). Before, DTFx and Elsa refused every message once a run had started (Issue I-158 (1)).
    /// </summary>
    async Task MessageAfterStart(string engine)
    {
        var bank = $"msg-{engine}-{Guid.NewGuid():N}"[..20];
        const string id = "2026.11.23-1";
        var dist = DistributionRegistry.IdOf(bank, id);
        using var worker = new Worker();
        var plans = Path.Combine(Path.GetTempPath(), $"th-hosts-{bank}");
        try
        {
            // Flowable runs the BPMN the studio exported, so its stored package carries it (I-269); the others build from the documents.
            var package = Package("1.0.0", flowable: engine == "flowable");
            await WithRegistry(r => r.Store(bank, id, package, new JsonObject { ["id"] = id, ["bank"] = bank }, Convert.ToHexStringLower(SHA256.HashData(package)), "test", "e2e"));
            await WithRegistry(r => r.Activate(dist, Environment, "test", "e2e"));
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", new JsonObject { ["th-p"] = worker.Url }.ToJsonString());
            await using var sup = await StartSupervisor(engine, bank, plans);
            try
            {
                await Until(async () => J.Truthy(J.P(J.P(await sup.Distribution(dist), "host"), "current")), TimeSpan.FromMinutes(3), $"{dist} current");
                Assert.Contains(J.A((await sup.Call("GET", "/events")).body, "events"), e => J.S(e, "event") == "plan built from the stored package");
                var p = $"M1-{bank}";
                var r = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p, ["context"] = Context(p), ["workflow"] = "p.wf.ask.v1" });
                Assert.True(r.status == 202, r.body.ToJsonString());
                await Until(() => StepRecorded(p, "ok"), TimeSpan.FromMinutes(3), $"{p}'s first step");
                // The message, after the run started: delivered on the payment's live path.
                (int status, JsonObject body) m = default;
                await Until(async () => (m = await sup.Call("POST", $"/payments/{p}/messages", new JsonObject { ["message"] = new JsonObject { ["type"] = "p.test.answer", ["id"] = $"answer-{p}" } })).status == 202, TimeSpan.FromMinutes(2), "the message taken");
                JsonObject? e = null;
                await Until(async () => J.S(e = (await sup.Call("GET", $"/payments/{p}")).body, "status") is "ended" or "failed", TimeSpan.FromMinutes(3), $"{p} ended");
                Assert.True(J.S(e, "outcome") == "done", e!.ToJsonString());
                Assert.Contains("w:received", J.Strings(J.P(J.A(J.P(e, "observation"), "instances")[0], "trace")));
            }
            catch (Exception x) when (x is not Xunit.Sdk.XunitException || !x.Message.Contains("--- the supervisor's log ---"))
            {
                throw new Xunit.Sdk.XunitException($"{x.Message}\n--- the supervisor's log ---\n{sup.Log}");
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", null);
            try { Directory.Delete(plans, true); } catch { }
        }
    }

    /// <summary>
    /// The waiting workflow's BPMN, as the generator writes it for a REST binding (studio/src/export/bpmn.ts): each wait from a parameter is
    /// a timer marked <c>th:timer parameter default</c>, written with its default, and the text after it names the value the timer took.
    /// </summary>
    static string WaitingBpmn(string release)
    {
        var key = $"p_wf_wait_v1_{release.Replace('.', '_')}";
        string Call(string s) => $$"""
            <serviceTask id="{{s}}_task" flowable:type="external-worker" flowable:topic="th-p:p.enrich.check.v1@{{release}}" flowable:exclusive="false">
              <extensionElements>
                <flowable:executionListener event="start" expression="${execution.setVariable('th_attempt', 1)}"/>
                <th:task step="{{s}}" activity="p.enrich.check.v1" release="{{release}}" record="business"/>
              </extensionElements>
            </serviceTask>
            <serviceTask id="{{s}}_rec" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="call" step="{{s}}" var="outcome_{{s}}" record="business"/></extensionElements></serviceTask>
            """;
        string Wait(string s, string? parameter, string literal) => $$"""
            <intermediateCatchEvent id="{{s}}" name="wait:{{literal}}">{{(parameter is null ? "" : $"<extensionElements><th:timer parameter=\"{parameter}\" default=\"{literal}\"/></extensionElements>")}}<timerEventDefinition><timeDuration>{{literal}}</timeDuration></timerEventDefinition></intermediateCatchEvent>
            <serviceTask id="{{s}}_text" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="text" text="{{s}}:{{literal}}" record="business"{{(parameter is null ? "" : $" timer=\"{s}\" prefix=\"{s}\"")}}/></extensionElements></serviceTask>
            """;
        string[] chain = ["start", "th_started", "a_task", "a_rec", "w1", "w1_text", "pause", "pause_text", "w2", "w2_text", "w3", "w3_text", "b_task", "b_rec", "th_endpoint_done", "th_end", "end"];
        var flows = string.Join("\n", chain.Zip(chain.Skip(1), (f, t) => $"<sequenceFlow id=\"f_{f}_{t}\" sourceRef=\"{f}\" targetRef=\"{t}\"/>"));
        return $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:flowable="http://flowable.org/bpmn" xmlns:th="urn:transacthub:flowable:1" targetNamespace="urn:test">
          <process id="{{key}}" isExecutable="true">
            <extensionElements><th:workflow name="p.wf.wait.v1" release="{{release}}" kind="workflow"/></extensionElements>
            <startEvent id="start"/>
            <serviceTask id="th_started" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="processStart"/></extensionElements></serviceTask>
            {{Call("a")}}
            {{Wait("w1", "p.wf.wait.v1#gap", "PT20M")}}
            {{Wait("pause", null, "PT20S")}}
            {{Wait("w2", "p.wf.wait.v1#gap", "PT20M")}}
            {{Wait("w3", "p.wf.wait.v1#late", "PT20M")}}
            {{Call("b")}}
            <intermediateThrowEvent id="th_endpoint_done"><extensionElements><flowable:executionListener event="start" expression="${execution.setVariable('th_outcome', 'done')}"/></extensionElements></intermediateThrowEvent>
            <serviceTask id="th_end" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="end"/></extensionElements></serviceTask>
            <endEvent id="end"/>
            {{flows}}
          </process>
        </definitions>
        """;
    }

    /// <summary>A stored package whose platform workflow waits as long as the bank says, twice as of the step and once as of the payment's date.</summary>
    static byte[] WaitingPackage(string release, bool bpmn = false)
    {
        var docs = Documents(release);
        docs[$"p.wf.wait.v1@{release}"] = Doc($$"""
            { "kind": "workflow", "wfKind": "platform", "name": "p.wf.wait.v1", "release": "{{release}}",
              "parameters": {
                "gap": { "type": "duration", "default": "PT20M", "bounds": { "min": "PT0S", "max": "PT1H" } },
                "late": { "type": "duration", "default": "PT20M", "bounds": { "min": "PT0S", "max": "PT1H" }, "lookupDate": "payment-date" } },
              "do": [
              { "id": "a", "call": "p.enrich.check.v1" },
              { "id": "w1", "wait": { "parameter": "p.wf.wait.v1#gap", "default": "PT20M" } },
              { "id": "pause", "wait": "PT20S" },
              { "id": "w2", "wait": { "parameter": "p.wf.wait.v1#gap", "default": "PT20M" } },
              { "id": "w3", "wait": { "parameter": "p.wf.wait.v1#late", "default": "PT20M" } },
              { "id": "b", "call": "p.enrich.check.v1" },
              { "end": "done" } ] }
            """);
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Put(string name, string text) { using var w = new StreamWriter(z.CreateEntry(name).Open()); w.Write(text); }
            Put("manifest.yaml", $"kind: distribution\nrelease: {release}\n");
            Put("engine/p.wf.wait.v1/binding.yaml", "kind: engine-binding\nqueues:\n  p: th-p\n");
            foreach (var (k, v) in docs) Put($"engine/p.wf.wait.v1/{(k.StartsWith("p.wf.") ? "workflows" : "activities")}/{k}.yaml", v!.ToJsonString());
            // On Flowable the package carries the exported BPMN, as the studio's export for flowable does.
            if (bpmn) Put($"engine/p.wf.wait.v1/bpmn/p.wf.wait.v1@{release}.bpmn20.xml", WaitingBpmn(release));
        }
        return ms.ToArray();
    }

    /// <summary>A configuration release put in force: each value a version of kind config, as BankValues puts it in force (R-09-111).</summary>
    async Task PutInForce(string bank, string release, params (string parameter, string value, DateTimeOffset from, DateTimeOffset? to)[] values)
    {
        await using var db = StoreDbContext.Open(Db);
        foreach (var (parameter, value, from, to) in values)
            await new VersionedValues(db).Publish(ConfigurationValues.Kind, bank, parameter, new JsonObject { ["value"] = value, ["configurationSet"] = release }, from, to, "test.approver");
    }

    /// <summary>
    /// R-09-135 (the north-star review of 23 November 2026): the engine host reads the bank's values from the configuration release in
    /// force as of each step, not an empty list (the factory's twenty minutes) and not a list frozen at its start. A later release
    /// put in force while the payment runs reaches its later steps with no restart; a value read as of the payment's date keeps the
    /// value of its acceptance.
    /// </summary>
    async Task ValuesAsOfEachStep(string engine)
    {
        var bank = $"val-{engine}-{Guid.NewGuid():N}"[..20];
        const string id = "2026.11.23-1";
        var dist = DistributionRegistry.IdOf(bank, id);
        using var worker = new Worker();
        var plans = Path.Combine(Path.GetTempPath(), $"th-hosts-{bank}");
        var since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        try
        {
            var package = WaitingPackage("1.0.0", bpmn: engine == "flowable");
            await WithRegistry(r => r.Store(bank, id, package, new JsonObject { ["id"] = id, ["bank"] = bank }, Convert.ToHexStringLower(SHA256.HashData(package)), "test", "e2e"));
            await WithRegistry(r => r.Activate(dist, Environment, "test", "e2e"));
            await PutInForce(bank, "2026.11.23", ("p.wf.wait.v1#gap", "PT1S", since, null), ("p.wf.wait.v1#late", "PT1S", since, null));
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", new JsonObject { ["th-p"] = worker.Url }.ToJsonString());
            await using var sup = await StartSupervisor(engine, bank, plans);
            try
            {
                await Until(async () => J.Truthy(J.P(J.P(await sup.Distribution(dist), "host"), "current")), TimeSpan.FromMinutes(3), $"{dist} current");
                var p = $"V1-{bank}";
                var r = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p, ["context"] = Context(p), ["workflow"] = "p.wf.wait.v1" });
                Assert.True(r.status == 202, r.body.ToJsonString());
                await Until(() => StepRecorded(p, "ok"), TimeSpan.FromMinutes(3), $"{p}'s first step");
                // The payment pauses for twenty seconds after w1. A later configuration release is put in force meanwhile, with no
                // restart of any host: from now on the gap is two seconds, and so is the late value, but the payment was accepted before.
                await Task.Delay(TimeSpan.FromSeconds(4));
                var cut = DateTimeOffset.UtcNow;
                await PutInForce(bank, "2026.11.24", ("p.wf.wait.v1#gap", "PT1S", since, cut), ("p.wf.wait.v1#gap", "PT2S", cut, null),
                    ("p.wf.wait.v1#late", "PT1S", since, cut), ("p.wf.wait.v1#late", "PT2S", cut, null));
                JsonObject? e = null;
                try { await Until(async () => J.S(e = (await sup.Call("GET", $"/payments/{p}")).body, "status") is "ended" or "failed", TimeSpan.FromMinutes(4), $"{p} ended"); }
                catch (Xunit.Sdk.XunitException x)
                {
                    await using var db = StoreDbContext.Open(Db);
                    var subject = await SubjectOf(p);
                    var steps = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subject).OrderBy(f => f.Seq).Select(f => f.Kind + ":" + f.Code).ToListAsync();
                    throw new Xunit.Sdk.XunitException($"{x.Message}: {e?.ToJsonString()}; facts: {string.Join(", ", steps)}");
                }
                Assert.True(J.S(e, "outcome") == "done", e!.ToJsonString());
                var trace = J.Strings(J.P(J.A(J.P(e, "observation"), "instances")[0], "trace"));
                // As of each step: the first release's second before, the later release's two seconds after; as of acceptance, a second.
                Assert.Contains("w1:PT1S", trace);
                Assert.Contains("w2:PT2S", trace);
                Assert.Contains("w3:PT1S", trace);
                Assert.DoesNotContain(trace, x => x.Contains("PT20M"));
            }
            catch (Exception x) when (x is not Xunit.Sdk.XunitException || !x.Message.Contains("--- the supervisor's log ---"))
            {
                throw new Xunit.Sdk.XunitException($"{x.Message}\n--- the supervisor's log ---\n{sup.Log}");
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", null);
            try { Directory.Delete(plans, true); } catch { }
        }
    }

    [Fact] public Task Temporal_reads_the_banks_values_as_of_each_step() => ValuesAsOfEachStep("temporal");
    [Fact] public Task Dtfx_reads_the_banks_values_as_of_each_step() => ValuesAsOfEachStep("dtfx");
    [Fact] public Task Elsa_reads_the_banks_values_as_of_each_step() => ValuesAsOfEachStep("elsa");
    // R-09-186 (Issue I-240): on Flowable the binding gives the timer's read the payment's id (its root's header.paymentId), so the late
    // wait, declared payment-date, takes the value of the payment's acceptance, not the value in force when the timer starts.
    [Fact] public Task Flowable_reads_the_banks_values_as_of_each_step_and_as_of_the_payments_date() => ValuesAsOfEachStep("flowable");

    /// <summary>A workflow with a hook point after its first step, and a bank's notify hook for it, in one stored distribution.</summary>
    static byte[] HookedPackage()
    {
        var docs = Documents("1.0.0");
        docs["p.wf.hooked.v1@1.0.0"] = Doc("""
            { "kind": "workflow", "wfKind": "platform", "name": "p.wf.hooked.v1", "release": "1.0.0",
              "hookPoints": [ { "name": "after-a", "type": "notify", "contract": { "reads": ["header"] } } ],
              "do": [ { "id": "a", "call": "p.enrich.check.v1" }, { "hook": "after-a" }, { "id": "b", "call": "p.enrich.check.v1" }, { "end": "done" } ] }
            """);
        docs["b.hook.tell.v1@1.0.0"] = Doc("""
            { "kind": "hook", "name": "b.hook.tell.v1", "release": "1.0.0", "implements": { "hookPoint": "after-a", "of": "p.wf.hooked.v1" },
              "type": "notify", "outcomes": ["done"], "do": [ { "id": "told", "call": "p.enrich.check.v1" }, { "end": "done" } ] }
            """);
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Put(string name, string text) { using var w = new StreamWriter(z.CreateEntry(name).Open()); w.Write(text); }
            Put("manifest.yaml", "kind: distribution\nrelease: 1.0.0\n");
            Put("engine/p.wf.hooked.v1/binding.yaml", "kind: engine-binding\nqueues:\n  p: th-p\n  b: th-p\n");
            foreach (var (k, v) in docs) Put($"engine/p.wf.hooked.v1/{(k.StartsWith("p.wf.") ? "workflows" : k.StartsWith("b.hook.") ? "hooks" : "activities")}/{k}.yaml", v!.ToJsonString());
        }
        return ms.ToArray();
    }

    /// <summary>
    /// R-09-217 (Issue I-335): a bank's hook binding reaches a payment on the long-running engine host from the configuration release in
    /// force, as its values do (R-09-135). Before, the host's plan carried no bindings, so no bank hook ran on a runtime: a payment ran
    /// past a bound hook point as past an unbound one. A payment that starts before the release is put in force runs no hook; one that
    /// starts after runs it.
    /// </summary>
    async Task HookBoundInForce(string engine)
    {
        var bank = $"hk-{engine}-{Guid.NewGuid():N}"[..20];
        const string id = "2026.11.24-1";
        var dist = DistributionRegistry.IdOf(bank, id);
        using var worker = new Worker();
        var plans = Path.Combine(Path.GetTempPath(), $"th-hosts-{bank}");
        try
        {
            var package = HookedPackage();
            await WithRegistry(r => r.Store(bank, id, package, new JsonObject { ["id"] = id, ["bank"] = bank }, Convert.ToHexStringLower(SHA256.HashData(package)), "test", "e2e"));
            await WithRegistry(r => r.Activate(dist, Environment, "test", "e2e"));
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", new JsonObject { ["th-p"] = worker.Url }.ToJsonString());
            await using var sup = await StartSupervisor(engine, bank, plans);
            try
            {
                await Until(async () => J.Truthy(J.P(J.P(await sup.Distribution(dist), "host"), "current")), TimeSpan.FromMinutes(3), $"{dist} current");
                async Task<JsonObject> Pay(string p)
                {
                    var r = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p, ["context"] = Context(p), ["workflow"] = "p.wf.hooked.v1" });
                    Assert.True(r.status == 202, r.body.ToJsonString());
                    JsonObject? e = null;
                    await Until(async () => J.S(e = (await sup.Call("GET", $"/payments/{p}")).body, "status") is "ended" or "failed", TimeSpan.FromMinutes(3), $"{p} ended");
                    Assert.True(J.S(e, "outcome") == "done", e!.ToJsonString());
                    return e!;
                }
                await Pay($"H1-{bank}");
                await Until(() => Task.FromResult(worker.Calls.Contains($"H1-{bank}:b@1.0.0")), TimeSpan.FromMinutes(1), "H1's last step");
                Assert.DoesNotContain($"H1-{bank}:told@1.0.0", worker.Calls);
                // The bank's configuration release with the binding is put in force, as DistributionService puts one (R-09-111).
                await using (var db = StoreDbContext.Open(Db))
                    await HookBindings.Publish(db, TimeProvider.System, bank, "1.1.0", new JsonArray(new JsonObject
                    {
                        ["hookPoint"] = "p.wf.hooked.v1#after-a", ["hook"] = "b.hook.tell.v1", ["release"] = "1.0.0", ["validFrom"] = "2026-01-01",
                    }), "test.approver");
                await Pay($"H2-{bank}");
                await Until(() => Task.FromResult(worker.Calls.Contains($"H2-{bank}:told@1.0.0")), TimeSpan.FromMinutes(1), "H2's hook ran on the host");
            }
            catch (Exception x) when (x is not Xunit.Sdk.XunitException || !x.Message.Contains("--- the supervisor's log ---"))
            {
                throw new Xunit.Sdk.XunitException($"{x.Message}\n--- the supervisor's log ---\n{sup.Log}\n--- the worker's calls ---\n{string.Join(", ", worker.Calls)}");
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", null);
            try { Directory.Delete(plans, true); } catch { }
        }
    }

    [Fact] public Task Temporal_runs_the_hook_the_configuration_in_force_binds() => HookBoundInForce("temporal");
    [Fact] public Task Dtfx_runs_the_hook_the_configuration_in_force_binds() => HookBoundInForce("dtfx");
    [Fact] public Task Elsa_runs_the_hook_the_configuration_in_force_binds() => HookBoundInForce("elsa");

    [Fact] public Task Dtfx_takes_a_message_after_the_run_started_with_a_plan_from_the_store() => MessageAfterStart("dtfx");
    [Fact] public Task Elsa_takes_a_message_after_the_run_started_with_a_plan_from_the_store() => MessageAfterStart("elsa");
    // The bug the last change found (R-09-161): on Temporal a message sent to a payment after its run started, as the Central Bank's
    // ACK arrives through the File API, was counted unmatched by the payment's front door workflow and never reached the run. Flowable
    // runs the exported BPMN, so its stored package carries it; its live path stamps the message as DTFx's and Elsa's do (I-269, R-09-207).
    [Fact] public Task Temporal_takes_a_message_after_the_run_started_with_a_plan_from_the_store() => MessageAfterStart("temporal");
    [Fact] public Task Flowable_takes_a_message_after_the_run_started_with_a_plan_from_the_store() => MessageAfterStart("flowable");

    [Fact] public Task Temporal_worker_versioning_on_a_real_server() => SwitchOver("temporal");
    [Fact] public Task Dtfx() => SwitchOver("dtfx");
    [Fact] public Task Elsa() => SwitchOver("elsa");
    [Fact] public Task Flowable() => SwitchOver("flowable");
}

static class Safe
{
    public static string Of(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[^A-Za-z0-9._-]+", "_");
}
