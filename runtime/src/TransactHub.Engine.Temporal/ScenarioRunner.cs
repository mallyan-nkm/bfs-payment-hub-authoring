using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using TransactHub.Engines;
using TransactHub.Engines.Durable;
using TransactHub.Store;

namespace TransactHub.Engine.Temporal;

/// <summary>
/// Runs one plan on Temporal: a conformance scenario (the studio's party service plays every party)
/// or a deployment (each activity goes to the worker process of its task queue). The plan and the
/// observation are those of the studio's DTFx and Elsa harnesses, so the kit runner and the deploy
/// runner compare them unchanged. Time is Temporal's: on the time-skipping test server a run that
/// waits days takes seconds.
/// </summary>
public sealed class ScenarioRunner
{
    public const string HostQueue = "transacthub";
    static readonly string[] Forbidden = { "payment", "items", "ext" };

    readonly JsonObject input;
    readonly JsonObject built, spec, config;
    readonly List<string> log = new();

    public ScenarioRunner(JsonObject input)
    {
        this.input = input;
        built = (JsonObject)J.P(input, "built")!;
        spec = (JsonObject)J.P(built, "spec")!;
        config = (JsonObject)J.P(built, "config")!;
    }

    /// <summary>Where the time-skipping test server is started from (runtime/tools/temporal-test-server/run.sh).</summary>
    public static string TestServerScript()
    {
        if (Environment.GetEnvironmentVariable("TH_TEMPORAL_TEST_SERVER") is { Length: > 0 } p) return p;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "tools", "temporal-test-server", "run.sh");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("runtime/tools/temporal-test-server/run.sh (run its setup.sh once)");
    }

    public static Task<WorkflowEnvironment> StartTestServer(Temporalio.Runtime.TemporalRuntime? runtime = null, IReadOnlyCollection<Temporalio.Client.Interceptors.IClientInterceptor>? interceptors = null) =>
        WorkflowEnvironment.StartTimeSkippingAsync(new() { TestServer = new() { ExistingPath = TestServerScript() }, Runtime = runtime, Interceptors = interceptors });

    public async Task<JsonObject> Run(WorkflowEnvironment env)
    {
        var start = DateTimeOffset.Parse($"{J.S(config, "evaluationDate")}T{J.S(config, "startTime") ?? "06:00:00"}Z").ToUnixTimeMilliseconds();
        var dep = new Deployment((JsonObject)J.P(input, "documents")!, config, start, (int)(J.Num(input, "chunkSize") ?? 100)) { ItemConcurrency = (int)(J.Num(input, "itemConcurrency") ?? 1) };
        Deployment.Current = dep;
        using IParty party = J.P(input, "workers") is JsonObject workers
            ? new WorkerRouter(workers, (JsonObject)J.P(input, "queues")!)
            : new PartyClient(J.S(input, "studioRoot")!);
        await party.Call(new JsonObject { ["op"] = "open", ["scenario"] = J.S(built, "id") });
        // A deployment keeps every payload in the store (plan W2); a kit run keeps it in memory.
        var storeOpts = StoreFromEnvironment();
        var durable = storeOpts is null ? null : new DurableClaimCheck(storeOpts);
        if (storeOpts is not null)
        {
            await using var db = StoreDbContext.Open(storeOpts);
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database);
            StepFacts.Install(storeOpts);
        }
        var host = new Host(party, dep, durable);

        // A deployment puts each activity on the task queue of its scope (R-09-05); a kit run, on one.
        var queues = J.P(input, "queues") as JsonObject;
        // A bank's distribution runs as its own copy beside the others (R-09-55): its task queues carry the distribution, and
        // its workers carry it as their build id, so two distributions never take each other's tasks.
        var distribution = J.S(input, "distribution");
        string Q(string q) => distribution is null ? q : $"{q}@{distribution}";
        string queueOf(string activity)
        {
            if (queues is null || activity == Interp.ClaimCheck) return Q(HostQueue);
            var parts = activity.Split('@')[0].Split('.');
            var scope = parts[0] is "p" or "b" ? parts[0] : $"{parts[0]}.{parts[1]}";
            return Q(J.S(queues, scope) ?? HostQueue);
        }
        InterpreterWorkflow.QueueOf = queueOf;

        var serverNow = (await env.GetCurrentTimeAsync()).ToUniversalTime();
        var offset = start - new DateTimeOffset(serverNow, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var runId = Guid.NewGuid().ToString("N")[..8];
        var frontDoorId = distribution is null ? $"front-door-{runId}" : $"front-door-{runId}@{distribution}";

        var taskQueues = new HashSet<string> { Q(HostQueue) };
        if (queues is not null) foreach (var (_, q) in queues) taskQueues.Add(Q(q!.GetValue<string>()));
        var workersRunning = new List<(TemporalWorker w, Task t)>();
        using var stop = new CancellationTokenSource();
        foreach (var q in taskQueues)
        {
            // A batch runs many short attempts at once: poll and run them in parallel.
            var opts = new TemporalWorkerOptions(q)
            {
                MaxConcurrentActivities = 500, MaxConcurrentWorkflowTasks = 200,
                MaxConcurrentActivityTaskPolls = 8, MaxConcurrentWorkflowTaskPolls = 8,
            }.AddActivity(host.AnyAsync);
#pragma warning disable CS0618 // the build id names the distribution the worker runs
            if (distribution is not null) opts.BuildId = distribution;
#pragma warning restore CS0618
            if (q == Q(HostQueue)) opts.AddWorkflow<InterpreterWorkflow>().AddWorkflow<FrontDoorWorkflow>();
            var w = new TemporalWorker(env.Client, opts);
            workersRunning.Add((w, w.ExecuteAsync(stop.Token)));
        }

        // What starts when: roots, a party's first messages, signals (R-05-12).
        var ctx = (JsonObject)J.Clone(J.P(built, "context"))!;
        var workflow = J.S(built, "workflow")!;
        var starts = new JsonArray();
        var signals = new JsonArray();
        var deliveries = new JsonArray();
        var roots = new List<(string label, string id)>();
        var tenant = J.S(input, "tenant") ?? J.S(config, "tenant") ?? "tenant";
        async Task<JsonObject> rootInput(string id, string docKey, JsonObject c)
        {
            var (state, payload) = ClaimCheckStore.Split(c);
            var header = J.P(state, "header");
            var direction = J.S(header, "direction") == "inward" ? TransactHub.Model.Direction.Inward : TransactHub.Model.Direction.Outward;
            await host.Store.Create($"{id}#ctx", payload, new AcceptInfo(tenant, J.S(header, "scheme") ?? docKey.Split('.')[0], direction, distribution ?? docKey));
            var inp = new JsonObject { ["kind"] = "workflow", ["doc"] = docKey, ["state"] = state, ["payloadRef"] = $"{id}#ctx" };
            if (J.P(built, "legs") is JsonObject lg) inp["legs"] = J.Clone(lg);
            return inp;
        }
        async Task root(string label, string id, string docKey, long at, JsonObject c)
        {
            roots.Add((label, id));
            starts.Add(new JsonObject { ["id"] = id, ["at"] = at, ["input"] = await rootInput(id, docKey, c) });
            foreach (var s in J.A(config, "signals"))
                signals.Add(new JsonObject { ["id"] = id, ["at"] = start + Duration.Ms(J.S(s, "after")!), ["signal"] = J.S(s, "signal") });
        }
        void firstMessages(JsonObject res) { foreach (var d in J.A(res, "deliveries")) deliveries.Add(J.Clone(d)); }

        var handlerOnly = J.Truthy(J.P(spec, "handler"));
        if (J.P(spec, "instances") is JsonArray instances)
        {
            var n = 0;
            foreach (var s in instances)
            {
                var label = J.S(s, "label")!;
                await root(label, $"{label}-{++n}-{runId}", $"{workflow}@{J.S(s, "workflowRelease")}", start + Duration.Ms(J.S(s, "at") ?? "PT0S"), (JsonObject)J.Clone(ctx)!);
            }
        }
        else if (handlerOnly)
        {
            firstMessages(await party.Call(new JsonObject { ["op"] = "start", ["now"] = start, ["conversation"] = "conv-1" }));
        }
        else
        {
            firstMessages(await party.Call(new JsonObject { ["op"] = "start", ["now"] = start, ["conversation"] = J.S(J.P(ctx, "header"), "conversation") ?? "conv-1" }));
            var doc = dep.Resolve(workflow, null, 0) ?? throw new InvalidOperationException($"{workflow} does not resolve");
            await root("root", $"root-{runId}", $"{workflow}@{J.S(doc, "release")}", start, ctx);
        }

        var fdInput = new JsonObject
        {
            ["offset"] = offset, ["roots"] = J.Arr(roots.Select(r => r.id)), ["handlerOnly"] = handlerOnly,
            ["horizon"] = start + Duration.Ms("P60D"), ["starts"] = starts, ["signals"] = signals, ["deliveries"] = deliveries,
        };
        var fd = await env.Client.StartWorkflowAsync((FrontDoorWorkflow wf) => wf.RunAsync(J.Str(fdInput)), new WorkflowOptions(frontDoorId, Q(HostQueue)));
        var fdOut = (JsonObject)J.Parse(await fd.GetResultAsync())!;
        foreach (var l in J.Strings(J.P(fdOut, "log"))) log.Add(l);

        // What each root ended with, and what the engine stored across every run.
        var outputs = new List<JsonObject>();
        var obsInstances = new JsonArray();
        var stored = new HashSet<string>();
        var historyEvents = 0;
        var executions = 0;
        var late = 0;
        var runIds = J.A(fdOut, "runs").Select(r => J.S(r, "id")!).ToList();
        foreach (var id in runIds)
        {
            var h = env.Client.GetWorkflowHandle(id);
            var hist = await h.FetchHistoryAsync();
            executions++;
            historyEvents += hist.Events.Count;
            foreach (var text in PayloadTexts(hist.ToJson()))
            {
                ScanNames(text, stored);
                if (text.Contains("\\\"late\\\":true") || text.Contains("\"late\":true")) late++;
            }
        }
        var storedForbidden = Forbidden.Where(stored.Contains).ToList();
        JsonObject Inst(string label, JsonObject o)
        {
            var keys = new SortedSet<string>(J.Strings(J.P(o, "engineStateKeys")));
            foreach (var k in storedForbidden) keys.Add(k);
            return new JsonObject
            {
                ["label"] = label, ["trace"] = J.Clone(J.P(o, "trace")), ["detail"] = J.Clone(J.P(o, "detail")), ["releaseTrace"] = J.Clone(J.P(o, "releaseTrace")),
                ["steps"] = J.Clone(J.P(o, "steps")), ["attempts"] = J.Clone(J.P(o, "attempts")), ["ruleTraceSteps"] = J.Clone(J.P(o, "ruleTraceSteps")),
                ["engineStateKeys"] = J.Arr(keys), ["startedAt"] = (long)(J.Num(o, "startedAt") ?? start) - start,
                ["endedAt"] = J.Num(o, "endedAt") is double e ? (long)e - start : null, ["failed"] = J.S(o, "failed"),
                ["ctx"] = host.Store.Full(J.P(o, "state"), J.S(o, "payloadRef") ?? ""),
            };
        }
        foreach (var id in runIds)
        {
            try
            {
                outputs.Add(Resolve(await env.Client.GetWorkflowHandle(id).GetResultAsync<string>()));
            }
            catch (Exception e) { log.Add($"{id} did not complete: {e.Message}"); }
        }
        var handlerOutputs = J.A(fdOut, "handlerOutputs").Select(x => (JsonObject)J.Clone(x)!).ToList();
        outputs.AddRange(handlerOutputs);
        if (handlerOnly)
        {
            if (handlerOutputs.Count > 0) obsInstances.Add(Inst("root", handlerOutputs[0]));
        }
        else
        {
            foreach (var (label, id) in roots)
            {
                try { obsInstances.Add(Inst(label, Resolve(await env.Client.GetWorkflowHandle(id).GetResultAsync<string>()))); }
                catch (Exception e) { log.Add($"{label} ({id}) did not complete: {e.Message}"); }
            }
        }
        var sent = await party.Call(new JsonObject { ["op"] = "sent" });
        JsonObject? storeReport = null;
        if (storeOpts is not null && durable is not null) storeReport = await StoreReport.Of(storeOpts, r => durable.Full(null, r));
        stop.Cancel();
        foreach (var (_, t) in workersRunning) { try { await t; } catch (OperationCanceledException) { } }

        int sum(string k) => outputs.Sum(o => (int)(J.Num(o, k) ?? 0));
        JsonObject Resolve(string result)
        {
            var o = (JsonObject)J.Parse(result)!;
            return J.S(o, "outputRef") is string r && host.Outputs.TryGetValue(r, out var kept) ? (JsonObject)J.Parse(kept)! : o;
        }
        return new JsonObject
        {
            ["target"] = "temporal",
            ["instances"] = obsInstances,
            ["messages"] = J.Clone(J.P(sent, "messages")),
            ["unmatched"] = (int)(J.Num(fdOut, "unmatched") ?? 0) + sum("unmatched"),
            ["duplicates"] = (int)(J.Num(fdOut, "duplicates") ?? 0) + sum("duplicates"),
            ["earlyKept"] = sum("earlyKept"),
            ["durableInstances"] = runIds.Count,
            ["startMs"] = start,
            ["engine"] = new JsonObject
            {
                ["distribution"] = distribution is null ? null : new JsonObject { ["id"] = distribution, ["buildId"] = distribution, ["taskQueues"] = J.Arr(taskQueues.Order(StringComparer.Ordinal)) },
                ["orchestrationInstances"] = runIds.Count,
                ["executions"] = executions,
                ["historyEvents"] = historyEvents,
                ["episodes"] = 0,
                ["activityRuns"] = host.ActivityRuns,
                ["lateResultsAbandoned"] = late,
                ["droppedMessages"] = 0,
                ["virtualEnd"] = Duration.Iso((long)(J.Num(fdOut, "end") ?? start)),
                ["log"] = J.Arr(log),
                ["store"] = storeReport,
            },
        };
    }

    /// <summary>The store a deployment uses: TH_STORE_PROVIDER (SqlServer or PostgreSql) and TH_STORE_CONNECTION.</summary>
    public static StoreOptions? StoreFromEnvironment()
    {
        var p = Environment.GetEnvironmentVariable("TH_STORE_PROVIDER");
        var c = Environment.GetEnvironmentVariable("TH_STORE_CONNECTION");
        return string.IsNullOrEmpty(p) || string.IsNullOrEmpty(c) ? null : new StoreOptions(Enum.Parse<StoreProvider>(p, true), c);
    }

    /// <summary>Every payload in a history, decoded: what the engine actually holds.</summary>
    static IEnumerable<string> PayloadTexts(string historyJson)
    {
        foreach (Match m in Regex.Matches(historyJson, "\"data\"\\s*:\\s*\"([A-Za-z0-9+/=]+)\""))
        {
            string? text = null;
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value)); } catch { }
            if (text is not null) yield return text;
        }
    }

    static void ScanNames(string text, HashSet<string> names)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(text); } catch { return; }
        Walk(n, names, 0);
    }

    static void Walk(JsonNode? n, HashSet<string> names, int depth)
    {
        if (depth > 64 || n is null) return;
        switch (n)
        {
            case JsonObject o:
                foreach (var (k, v) in o) { names.Add(k); Walk(v, names, depth + 1); }
                break;
            case JsonArray a:
                foreach (var x in a) Walk(x, names, depth + 1);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                var s = v.GetValue<string>().TrimStart();
                if (s.Length > 1 && (s[0] == '{' || s[0] == '[')) { try { Walk(JsonNode.Parse(s), names, depth + 1); } catch { } }
                break;
        }
    }
}
