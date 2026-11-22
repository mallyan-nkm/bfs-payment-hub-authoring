using System.Diagnostics;
using System.Text.Json.Nodes;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using TransactHub.Engines;
using TransactHub.Engines.Durable;
using TransactHub.Store;

namespace TransactHub.Engine.Temporal;

/// <summary>
/// Many payments of one deployment on Temporal, side by side (the runtime's load harness, R-09-38, I-68): the plan the
/// deploy runner writes, with <c>runs</c> (one context per file, each already through the front door's intake) and
/// <c>concurrency</c>. One set of Temporal workers and one claim check serve every run, as a runtime host does; each run
/// has its own front door and root workflow. It prints per run what it took and how it ended, and never what the
/// payment holds. This is not a functional test and it is not part of the studio (spec/CLAUDE.md).
/// </summary>
public sealed class LoadRunner(JsonObject input)
{
    readonly JsonObject built = (JsonObject)J.P(input, "built")!;

    /// <summary>A fresh server has no namespace: register the client's, and wait until the frontend knows it.</summary>
    public static async Task EnsureNamespace(ITemporalClient c)
    {
        var created = false;
        try
        {
            await c.WorkflowService.RegisterNamespaceAsync(new Temporalio.Api.WorkflowService.V1.RegisterNamespaceRequest
            {
                Namespace = c.Options.Namespace,
                WorkflowExecutionRetentionPeriod = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(1)),
            });
            created = true;
        }
        catch (Temporalio.Exceptions.RpcException e) when (e.Code == Temporalio.Exceptions.RpcException.StatusCode.AlreadyExists) { }
        // A namespace just registered reaches the frontend's cache within about ten seconds.
        if (created) await Task.Delay(TimeSpan.FromSeconds(12));
    }

    public async Task<JsonObject> Run(WorkflowEnvironment env)
    {
        var config = (JsonObject)J.P(built, "config")!;
        var start = DateTimeOffset.Parse($"{J.S(config, "evaluationDate")}T{J.S(config, "startTime") ?? "06:00:00"}Z").ToUnixTimeMilliseconds();
        var dep = new Deployment((JsonObject)J.P(input, "documents")!, config, start, (int)(J.Num(input, "chunkSize") ?? 100)) { ItemConcurrency = (int)(J.Num(input, "itemConcurrency") ?? 1) };
        Deployment.Current = dep;
        using IParty party = new WorkerRouter((JsonObject)J.P(input, "workers")!, (JsonObject)J.P(input, "queues")!);
        var storeOpts = ScenarioRunner.StoreFromEnvironment();
        var durable = storeOpts is null ? null : new DurableClaimCheck(storeOpts);
        if (storeOpts is not null)
        {
            await using var db = StoreDbContext.Open(storeOpts);
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database);
        }
        var host = new Host(party, dep, durable);
        var queues = (JsonObject)J.P(input, "queues")!;
        string queueOf(string activity)
        {
            if (activity == Interp.ClaimCheck) return ScenarioRunner.HostQueue;
            var parts = activity.Split('@')[0].Split('.');
            var scope = parts[0] is "p" or "b" ? parts[0] : $"{parts[0]}.{parts[1]}";
            return J.S(queues, scope) ?? ScenarioRunner.HostQueue;
        }
        InterpreterWorkflow.QueueOf = queueOf;

        var taskQueues = new HashSet<string> { ScenarioRunner.HostQueue };
        foreach (var (_, q) in queues) taskQueues.Add(q!.GetValue<string>());
        var w = J.P(input, "worker") as JsonObject;
        var running = new List<Task>();
        using var stop = new CancellationTokenSource();
        foreach (var q in taskQueues)
        {
            var opts = new TemporalWorkerOptions(q)
            {
                MaxConcurrentActivities = (int)(J.Num(w, "maxConcurrentActivities") ?? 500),
                MaxConcurrentWorkflowTasks = (int)(J.Num(w, "maxConcurrentWorkflowTasks") ?? 200),
                MaxConcurrentActivityTaskPolls = 8, MaxConcurrentWorkflowTaskPolls = 8,
            }.AddActivity(host.AnyAsync);
            if (q == ScenarioRunner.HostQueue) opts.AddWorkflow<InterpreterWorkflow>().AddWorkflow<FrontDoorWorkflow>();
            running.Add(new TemporalWorker(env.Client, opts).ExecuteAsync(stop.Token));
        }

        var tenant = J.S(input, "tenant") ?? J.S(config, "tenant") ?? "tenant";
        var workflow = J.S(built, "workflow")!;
        var doc = dep.Resolve(workflow, null, 0) ?? throw new InvalidOperationException($"{workflow} does not resolve");
        var docKey = $"{workflow}@{J.S(doc, "release")}";
        var runs = J.A(input, "runs");
        var concurrency = Math.Max(1, (int)(J.Num(input, "concurrency") ?? 1));
        var gate = new SemaphoreSlim(concurrency, concurrency);
        var results = new JsonObject?[runs.Count];
        var wall = Stopwatch.StartNew();

        async Task one(int i)
        {
            await gate.WaitAsync();
            try { results[i] = await RunOne((JsonObject)runs[i]!, i); }
            finally { gate.Release(); }
        }

        async Task<JsonObject> RunOne(JsonObject r, int i)
        {
            var label = J.S(r, "label") ?? $"run-{i + 1}";
            var ctx = (JsonObject)J.Clone(J.P(r, "context"))!;
            var lines = J.P(ctx, "items") is JsonArray items ? items.Count : 0;
            var header = J.P(ctx, "header");
            var startedAt = wall.Elapsed.TotalMilliseconds;
            var t0 = Stopwatch.StartNew();
            using var span = EngineTelemetry.Source.StartActivity("th.engine.run", ActivityKind.Internal, EngineTelemetry.ProcessParent);
            span?.SetTag("th.engine", "temporal");
            span?.SetTag("th.payment.id", J.S(header, "paymentId"));
            span?.SetTag("th.scheme", J.S(header, "scheme"));
            span?.SetTag("th.tenant", tenant);
            span?.SetTag("th.lines", lines);
            var runId = Guid.NewGuid().ToString("N")[..8];
            var rootId = $"{label}-{runId}";
            try
            {
                var deliveries = J.A(await party.Call(new JsonObject { ["op"] = "start", ["now"] = start, ["conversation"] = J.S(header, "conversation") ?? "conv-1" }), "deliveries");
                var (state, payload) = ClaimCheckStore.Split(ctx);
                var direction = J.S(J.P(state, "header"), "direction") == "inward" ? TransactHub.Model.Direction.Inward : TransactHub.Model.Direction.Outward;
                await host.Store.Create($"{rootId}#ctx", payload, new AcceptInfo(tenant, J.S(J.P(state, "header"), "scheme") ?? docKey.Split('.')[0], direction, docKey));
                var rootInput = new JsonObject { ["kind"] = "workflow", ["doc"] = docKey, ["state"] = state, ["payloadRef"] = $"{rootId}#ctx" };
                if (J.P(r, "legs") is JsonObject lg) rootInput["legs"] = J.Clone(lg);
                var accepted = t0.Elapsed.TotalMilliseconds;
                // Each run starts at the business start of the day, whenever the harness reaches it.
                var serverNow = (await env.GetCurrentTimeAsync()).ToUniversalTime();
                var offset = start - new DateTimeOffset(serverNow, TimeSpan.Zero).ToUnixTimeMilliseconds();
                var fdInput = new JsonObject
                {
                    ["offset"] = offset, ["roots"] = new JsonArray(rootId), ["handlerOnly"] = false, ["horizon"] = start + Duration.Ms("P60D"),
                    ["starts"] = new JsonArray(new JsonObject { ["id"] = rootId, ["at"] = start, ["input"] = rootInput }),
                    ["signals"] = new JsonArray(), ["deliveries"] = J.Clone(deliveries),
                };
                var fd = await env.Client.StartWorkflowAsync((FrontDoorWorkflow wf) => wf.RunAsync(J.Str(fdInput)), new WorkflowOptions($"front-door-{rootId}", ScenarioRunner.HostQueue));
                var fdOut = (JsonObject)J.Parse(await fd.GetResultAsync())!;
                var o = (JsonObject)J.Parse(await env.Client.GetWorkflowHandle(rootId).GetResultAsync<string>())!;
                if (J.S(o, "outputRef") is string reff && host.Outputs.TryGetValue(reff, out var kept)) o = (JsonObject)J.Parse(kept)!;
                var trace = J.Strings(J.P(o, "trace")).ToList();
                var last = trace.Count > 0 ? trace[^1] : "";
                var outcome = J.S(o, "failed") is not null ? "failed" : last.StartsWith("end:") ? last[4..] : "open";
                var ms = t0.Elapsed.TotalMilliseconds;
                var tags = new TagList { { "th.outcome", outcome }, { "th.scheme", J.S(header, "scheme") }, { "th.tenant", tenant }, { "th.engine", "temporal" } };
                EngineTelemetry.Payments.Add(1, tags);
                if (lines > 0) EngineTelemetry.PaymentLines.Add(lines, tags);
                EngineTelemetry.PaymentDuration.Record(ms / 1000, tags);
                span?.SetTag("th.outcome", outcome);
                return new JsonObject
                {
                    ["label"] = label, ["lines"] = lines, ["outcome"] = outcome, ["trace"] = J.Arr(trace), ["startedMs"] = Math.Round(startedAt),
                    ["acceptMs"] = Math.Round(accepted, 1), ["ms"] = Math.Round(ms, 1), ["failed"] = J.S(o, "failed"),
                    ["children"] = outcome == "settled" ? null : await Children(fdOut, rootId),
                };
            }
            catch (Exception e)
            {
                span?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
                EngineTelemetry.Payments.Add(1, new TagList { { "th.outcome", "error" }, { "th.scheme", J.S(header, "scheme") }, { "th.tenant", tenant }, { "th.engine", "temporal" } });
                return new JsonObject { ["label"] = label, ["lines"] = lines, ["outcome"] = "error", ["error"] = e.GetType().Name + ": " + e.Message, ["ms"] = Math.Round(t0.Elapsed.TotalMilliseconds, 1) };
            }
        }

        // Why a run did not settle: how each of its other runs (the leg, the hooks) ended, the last steps of each.
        async Task<JsonArray> Children(JsonObject fdOut, string rootId)
        {
            var list = new JsonArray();
            foreach (var id in J.A(fdOut, "runs").Select(x => J.S(x, "id")!).Where(x => x != rootId))
            {
                try
                {
                    var c = (JsonObject)J.Parse(await env.Client.GetWorkflowHandle(id).GetResultAsync<string>())!;
                    if (J.S(c, "outputRef") is string cr && host.Outputs.TryGetValue(cr, out var ck)) c = (JsonObject)J.Parse(ck)!;
                    var t = J.Strings(J.P(c, "trace")).ToList();
                    list.Add(new JsonObject { ["id"] = id, ["trace"] = J.Arr(t.Skip(Math.Max(0, t.Count - 6))), ["failed"] = J.S(c, "failed") });
                }
                catch (Exception e) { list.Add(new JsonObject { ["id"] = id, ["error"] = e.GetType().Name }); }
            }
            return list;
        }

        await Task.WhenAll(Enumerable.Range(0, runs.Count).Select(one));
        var wallMs = wall.Elapsed.TotalMilliseconds;
        stop.Cancel();
        foreach (var t in running) { try { await t; } catch (OperationCanceledException) { } }
        return new JsonObject
        {
            ["target"] = "temporal",
            ["concurrency"] = concurrency,
            ["wallMs"] = Math.Round(wallMs),
            ["activityRuns"] = host.ActivityRuns,
            ["store"] = storeOpts?.Provider.ToString(),
            ["runs"] = new JsonArray(results.Select(x => (JsonNode?)x).ToArray()),
        };
    }
}
