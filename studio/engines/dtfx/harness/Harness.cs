using System.Text.Json;
using System.Text.Json.Nodes;
using DurableTask.Core;
using DurableTask.Core.History;
using Newtonsoft.Json.Linq;

using TransactHub.Engines;

namespace DtfxHarness;

// The DTFx harness for one conformance scenario (C21, C23-S2). It deploys the expanded documents,
// starts a TaskHubWorker on the virtual-time orchestration service, runs every activity attempt
// through the party service, plays the front door for the party's later messages, raises signals
// as external events, drives the virtual clock to the end and prints what it observed.

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var input = (JsonObject)J.Parse(await File.ReadAllTextAsync(args[0]))!;
        var h = new Harness(input);
        int code;
        try
        {
            // A span for the run and the payments it ended (EngineTelemetry: nothing listens unless a runtime host attached OpenTelemetry).
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var run = EngineTelemetry.StartRun("dtfx", input);
            var obs = await h.Run();
            EngineTelemetry.Observed(input, obs, watch.Elapsed);
            run?.Dispose();
            Console.Out.WriteLine("OBSERVATION " + J.Str(obs));
            code = 0;
        }
        catch (Exception e)
        {
            Console.Out.WriteLine("ERROR " + JsonSerializer.Serialize(e.ToString()));
            code = 1;
        }
        await Console.Out.FlushAsync();
        h.Dispose();
        Environment.Exit(code); // dispatchers and hung activities need no graceful stop
        return code;
    }
}

public sealed class Harness : IDisposable
{
    readonly JsonObject input;
    readonly JsonObject built;
    readonly JsonObject spec;
    readonly JsonObject config;
    public VirtualClock Clock = null!;
    public VirtualTimeOrchestrationService Service = null!;
    public readonly ClaimCheckStore Store = new();
    IParty party = null!;
    Deployment dep = null!;
    TaskHubClient client = null!;
    readonly List<(string label, string id)> roots = new();
    readonly List<JsonObject> handlerOutputs = new();
    int frontDoorUnmatched, frontDoorDuplicates, handlerSeq;
    readonly List<string> log = new();

    public Harness(JsonObject input)
    {
        this.input = input;
        built = (JsonObject)J.P(input, "built")!;
        // A bank's distribution runs as its own copy (R-09-55): its orchestration's name carries it, and its payments are pinned to it.
        if (J.S(input, "distribution") is { } dist) { DtfxEngine.OrchestrationName = $"transacthub.interpreter@{dist}"; ClaimCheckStore.Distribution = dist; }
        spec = (JsonObject)J.P(built, "spec")!;
        config = (JsonObject)J.P(built, "config")!;
    }

    public void Dispose() => party?.Dispose();

    public async Task<JsonObject> Run()
    {
        var start = DateTimeOffset.Parse($"{J.S(config, "evaluationDate")}T{J.S(config, "startTime") ?? "06:00:00"}Z").ToUnixTimeMilliseconds();
        dep = new Deployment((JsonObject)J.P(input, "documents")!, config, start, (int)(J.Num(input, "chunkSize") ?? 100)) { ItemConcurrency = (int)(J.Num(input, "itemConcurrency") ?? 1) };
        Deployment.Current = dep;
        Clock = new VirtualClock(start);
        Service = new VirtualTimeOrchestrationService(Clock);
        PartyActivity.H = this;
        ClaimCheckActivity.H = this;

        // A deployment calls the separate workers; a kit run calls the party service.
        party = J.P(input, "workers") is JsonObject workers
            ? new WorkerRouter(workers, (JsonObject)J.P(input, "queues")!)
            : new PartyClient(J.S(input, "studioRoot")!);
        await party.Call(new JsonObject { ["op"] = "open", ["scenario"] = J.S(built, "id") });

        // The worker: one generic orchestration, one task activity per activity release (R-09-04).
        var worker = new TaskHubWorker(Service);
        worker.AddTaskOrchestrations(new Creator<TaskOrchestration>(DtfxEngine.OrchestrationName, () => new InterpreterOrchestration()));
        var acts = new List<ObjectCreator<TaskActivity>> { new Creator<TaskActivity>(Interp.ClaimCheck, () => new ClaimCheckActivity()) };
        foreach (var (key, d) in dep.Entries())
            if (J.S(d, "kind") == "activity") acts.Add(new Creator<TaskActivity>(key, () => new PartyActivity()));
        worker.AddTaskActivities(acts.ToArray());
        // The orchestration's clock is the virtual clock: each episode's OrchestratorStarted event
        // carries the virtual time, so CurrentUtcDateTime, timers and RetryInterceptor all use it.
        worker.AddOrchestrationDispatcherMiddleware(async (ctx, next) =>
        {
            var rs = ctx.GetProperty<OrchestrationRuntimeState>();
            var started = rs.NewEvents.OfType<OrchestratorStartedEvent>().LastOrDefault();
            if (started is not null) started.Timestamp = Clock.NowUtc;
            await next();
        });
        await worker.StartAsync();
        client = new TaskHubClient(Service);

        var ctx = (JsonObject)J.Clone(J.P(built, "context"))!;
        var workflow = J.S(built, "workflow")!;
        if (J.P(spec, "instances") is JsonArray instances)
        {
            var n = 0;
            foreach (var s in instances)
            {
                var label = J.S(s, "label")!;
                var key = $"{workflow}@{J.S(s, "workflowRelease")}";
                var id = $"{label}-{++n}";
                roots.Add((label, id));
                Clock.Schedule(start + Duration.Ms(J.S(s, "at") ?? "PT0S"), () => StartRoot(id, key, (JsonObject)J.Clone(ctx)!));
            }
        }
        else if (J.Truthy(J.P(spec, "handler")))
        {
            Schedule(await party.Call(new JsonObject { ["op"] = "start", ["now"] = start, ["conversation"] = "conv-1" }));
        }
        else
        {
            Schedule(await party.Call(new JsonObject { ["op"] = "start", ["now"] = start, ["conversation"] = J.S(J.P(ctx, "header"), "conversation") ?? "conv-1" }));
            var doc = dep.Resolve(workflow, null, 0) ?? throw new InvalidOperationException($"{workflow} does not resolve");
            roots.Add(("root", "root"));
            Clock.Schedule(start, () => StartRoot("root", $"{workflow}@{J.S(doc, "release")}", ctx));
        }

        // Run on after the roots end, so that late messages reach the front door and are recorded.
        bool Done()
        {
            if (J.Truthy(J.P(spec, "handler"))) return handlerOutputs.Count > 0 && Clock.PendingTimers == 0;
            return roots.All(r => Service.Find(r.id)?.Completed == true) && Clock.PendingTimers == 0;
        }
        var finished = Clock.RunUntil(Done, start + Duration.Ms("P60D"), TimeSpan.FromSeconds(60));
        if (!finished) log.Add("the clock ran out of work before every root ended");
        return await Observe(start);
    }

    void StartRoot(string id, string docKey, JsonObject ctx)
    {
        var (state, payload) = ClaimCheckStore.Split(ctx);
        var r = $"{id}#ctx";
        Store.Create(r, payload, ClaimCheckStore.AcceptFor(state, J.S(config, "tenant"), docKey));
        var inp = new JsonObject { ["kind"] = "workflow", ["doc"] = docKey, ["state"] = state, ["payloadRef"] = r };
        // The legs the front door chose from the dispatch table fill the platform workflow's slots (R-05-08).
        if (J.P(built, "legs") is JsonObject lg) inp["legs"] = J.Clone(lg);
        client.CreateOrchestrationInstanceAsync(DtfxEngine.OrchestrationName, "", id, J.Str(inp)).GetAwaiter().GetResult();
        // Signals are external events, raised at their virtual times (R-05-12).
        foreach (var s in J.A(config, "signals"))
        {
            var sig = J.S(s, "signal")!;
            Clock.Schedule(Clock.Start + Duration.Ms(J.S(s, "after")!), () => Raise(id, "signal", new JsonObject { ["signal"] = sig }));
        }
    }

    void Raise(string id, string name, JsonObject data)
    {
        var rec = Service.Find(id);
        if (rec is null || rec.Completed) return;
        client.RaiseEventAsync(new OrchestrationInstance { InstanceId = id }, name, J.Str(data)).GetAwaiter().GetResult();
    }

    void Schedule(JsonObject res)
    {
        foreach (var d in J.A(res, "deliveries"))
        {
            var at = (long)J.Num(d, "at")!.Value;
            var m = (JsonObject)J.Clone(J.P(d, "message"))!;
            Clock.Schedule(at, () => FrontDoor(m));
        }
    }

    static JsonNode? Unwrap(string? s)
    {
        var n = J.Parse(s);
        return n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? J.Parse(v.GetValue<string>()) : n;
    }

    static bool Correlates(JsonNode? header, JsonObject m)
    {
        if (J.S(m, "conversation") is string conv && J.S(header, "conversation") == conv) return true;
        var c = J.P(m, "correlation");
        foreach (var k in new[] { "paymentId", "endToEndId", "fileId" })
            if (J.S(c, k) is string v && J.S(header, k) == v) return true;
        return false;
    }

    /// <summary>
    /// The front door (Part 5, 5.3): a message that starts a handler runs it; any other is delivered
    /// by correlation as an external event to the orchestration it belongs to, which keeps it if it
    /// is early and ignores it if it is a copy. One that finds no running instance is unmatched.
    /// </summary>
    void FrontDoor(JsonObject m)
    {
        var type = J.S(m, "type")!;
        var disp = J.P(dep.CatalogueEntry(type), "disposition");
        if (J.Has(disp, "start") || J.Has(disp, "handler"))
        {
            if (J.Truthy(J.P(m, "copy"))) { frontDoorDuplicates++; return; }
            if (J.S(disp, "handler") is string handler) StartHandler(m, dep.CatalogueEntry(type)!, handler);
            else { log.Add($"{type} starts a workflow through intake, which this harness does not run"); frontDoorUnmatched++; }
            return;
        }
        var candidates = Service.Instances.Where(r =>
        {
            var inp = Unwrap(r.Input);
            return J.S(inp, "kind") != "batch" && Correlates(J.Get(inp, "state.header"), m);
        }).ToList();
        string? Awaits(VirtualTimeOrchestrationService.InstanceRecord r) => J.S(Unwrap(r.CustomStatus), "awaiting");
        var alive = candidates.FirstOrDefault(c => !c.Completed && Awaits(c) == type) ?? candidates.LastOrDefault(c => !c.Completed);
        var target = alive ?? candidates.LastOrDefault();
        if (target is null) { frontDoorUnmatched++; return; }
        if (target.Completed)
        {
            var key = J.S(m, "inReplyTo") is string irt ? $"{type}:{irt}:{J.S(m, "status") ?? ""}" : J.S(m, "id")!;
            var consumed = candidates.Where(c => c.Completed).Any(c => J.Strings(J.P(Unwrap(c.State?.Output), "consumed")).Contains(key));
            if (consumed) frontDoorDuplicates++; else frontDoorUnmatched++;
            return;
        }
        Raise(target.Id, "message", m);
    }

    void StartHandler(JsonObject m, JsonObject entry, string handler)
    {
        var docv = dep.Resolve(handler, null, Clock.Elapsed);
        if (docv is null) { frontDoorUnmatched++; return; }
        var scope = string.Join('.', type(m).Split('.').Take(2));
        var ctx = new JsonObject
        {
            ["header"] = new JsonObject { ["conversation"] = J.S(m, "conversation"), ["scheme"] = scope, ["direction"] = J.S(entry, "direction") == "in" ? "inward" : "outward", ["receivedAt"] = Duration.Iso(Clock.Now) },
            ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["id"] = J.S(m, "id"), ["type"] = type(m) } },
        };
        var id = $"handler-{++handlerSeq}";
        var (state, payload) = ClaimCheckStore.Split(ctx);
        Store.Create($"{id}#ctx", payload);
        var inp = new JsonObject { ["kind"] = "handler", ["doc"] = $"{J.S(docv, "name")}@{J.S(docv, "release")}", ["state"] = state, ["payloadRef"] = $"{id}#ctx", ["partyKey"] = id };
        Clock.Enter();
        _ = Task.Run(async () =>
        {
            try
            {
                var interp = new Interp(new PlainEngine(this, id), dep);
                JsonObject o;
                try { o = await interp.Run(inp); }
                catch (Exception e) { o = interp.Output(); o["failed"] = e.Message; }
                lock (handlerOutputs) handlerOutputs.Add(o);
            }
            finally { Clock.Exit(); }
        });
        static string type(JsonObject x) => J.S(x, "type")!;
    }

    // ------------------------------------------------------------------ activities

    /// <summary>
    /// One activity attempt, through the party service with the orchestration's time as now. The
    /// party's later messages come back as deliveries for the front door. A stub's latency is the
    /// time the attempt takes; an attempt that never answers ends at its own deadline (the
    /// attempt timeout), after the orchestration has already abandoned it.
    /// </summary>
    public async Task<JsonObject> Attempt(JsonObject a, bool durable)
    {
        object? baton = durable ? Service.CurrentBaton : null;
        async Task SleepUntil(long at)
        {
            if (baton is not null) Service.ReleaseBaton(baton);
            await Clock.Sleep(at);
            if (baton is not null)
            {
                var id = baton is TaskActivityWorkItem w ? w.Id : ((VirtualTimeOrchestrationService.ActivityBaton)baton).WorkItemId;
                baton = new VirtualTimeOrchestrationService.ActivityBaton { WorkItemId = id };
                await Service.AcquireBaton(baton);
            }
        }
        var step = J.S(a, "step")!;
        var activity = J.S(a, "activity")!;
        var payloadRef = J.S(a, "payloadRef")!;
        var item = J.Num(a, "item") is double d ? (int?)d : null;
        // An item's attempt gets the item (the party binds payment to it, R-05-28), not the whole list.
        var ctx = item is int ix ? Store.FullForItem(J.P(a, "state"), payloadRef, ix) : Store.Full(J.P(a, "state"), payloadRef);
        var now = (long)J.Num(a, "now")!.Value;
        var req = new JsonObject
        {
            ["op"] = "attempt", ["instance"] = J.S(a, "instance"), ["activity"] = activity, ["release"] = J.S(a, "release"), ["step"] = step,
            ["call"] = J.Num(a, "call"), ["attempt"] = J.Num(a, "attempt"), ["now"] = now, ["ctx"] = ctx, ["level"] = J.S(a, "level"), ["policy"] = J.Clone(J.P(a, "policy")),
        };
        var res = await party.Call(req);
        Schedule(res);
        // The party service answers at once; the time an attempt takes is the stub's latency.
        if (J.S(dep.Stub(step, activity), "latency") is string latency) await SleepUntil(now + Duration.Ms(latency));
        var kind = J.S(res, "kind");
        if (kind == "hang")
        {
            if (J.Num(a, "deadline") is double dl)
            {
                await SleepUntil((long)dl + 1000);
                return new JsonObject { ["kind"] = "error", ["name"] = "Timeout", ["retryable"] = true, ["message"] = "no answer before the attempt's own deadline", ["late"] = true, ["at"] = Clock.Now };
            }
            if (baton is not null) Service.ReleaseBaton(baton);
            await Clock.SleepForever();
        }
        if (kind == "error")
            return new JsonObject { ["kind"] = "error", ["name"] = J.S(res, "name"), ["retryable"] = J.P(res, "retryable")?.DeepClone(), ["message"] = J.S(res, "message"), ["at"] = Clock.Now };
        // A hold opened its item: the orchestration awaits the operator's decision (Issue I-98).
        if (kind == "held") return new JsonObject { ["kind"] = "held", ["hold"] = J.S(res, "hold"), ["expiry"] = J.S(res, "expiry"), ["at"] = Clock.Now };
        // Writes: references go back to the engine; the payload part is staged in the claim-check store.
        var stateWrites = new JsonObject();
        var stagedWrites = new List<(string, JsonNode?)>();
        foreach (var (p, v) in J.Obj(J.P(res, "writes")).ToList())
        {
            if (item is int i) stagedWrites.Add(($"items[{i}].{p}", J.Clone(v)));
            else if (ClaimCheckStore.IsStatePath(p)) stateWrites[p] = J.Clone(v);
            else stagedWrites.Add((p, J.Clone(v)));
        }
        var outp = new JsonObject
        {
            ["kind"] = "result", ["outcome"] = J.S(res, "outcome"), ["reasonCode"] = J.S(res, "reasonCode"), ["stateWrites"] = stateWrites,
            ["stagedPaths"] = J.Arr(stagedWrites.Select(w => w.Item1)), ["decisions"] = J.Clone(J.P(res, "decisions")), ["at"] = Clock.Now,
        };
        if (stagedWrites.Count > 0) outp["token"] = Store.Stage(payloadRef, stagedWrites);
        // The rule trace is stored with the payment (R-03-*), when the party reports one.
        if (J.P(res, "ruleTrace") is JsonNode rt)
        {
            lock (Store.RuleTraces) Store.RuleTraces[$"{payloadRef}|{step}|{J.Num(a, "call")}"] = J.Clone(rt);
            outp["ruleTrace"] = true;
        }
        return outp;
    }

    public JsonObject ClaimCheckOp(JsonObject a)
    {
        switch (J.S(a, "op"))
        {
            case "commit": Store.Commit(J.S(a, "token")!); return new JsonObject { ["ok"] = true };
            case "fork": Store.Fork(J.S(a, "from")!, J.S(a, "to")!); return new JsonObject { ["ok"] = true };
            case "copy": Store.Copy(J.S(a, "from")!, J.S(a, "to")!, J.Strings(J.P(a, "paths"))); return new JsonObject { ["ok"] = true };
            case "count": return new JsonObject { ["count"] = Store.Count(J.S(a, "ref")!, J.S(a, "path")!) };
            case "diff":
                var x = Store.Full(J.P(a, "aState"), J.S(a, "aRef")!);
                var y = Store.Full(J.P(a, "bState"), J.S(a, "bRef")!);
                return new JsonObject { ["paths"] = J.Arr(J.DiffPaths(x, y)) };
        }
        throw new InvalidOperationException($"claim-check: unknown op {J.S(a, "op")}");
    }

    // ------------------------------------------------------------------ observation

    static readonly string[] Forbidden = { "payment", "items", "ext" };

    /// <summary>Property names of every JSON payload in a stored history: what the engine holds.</summary>
    static void ScanNames(JToken t, HashSet<string> names, int depth = 0)
    {
        if (depth > 64) return;
        switch (t)
        {
            case JObject o:
                foreach (var p in o.Properties()) { if (!p.Name.StartsWith("$")) names.Add(p.Name); ScanNames(p.Value, names, depth + 1); }
                break;
            case JArray arr:
                foreach (var x in arr) ScanNames(x, names, depth + 1);
                break;
            case JValue v when v.Type == JTokenType.String:
                var s = ((string)v!).TrimStart();
                if (s.Length > 1 && (s[0] == '{' || s[0] == '[' || s[0] == '"'))
                {
                    try { ScanNames(JToken.Parse(s), names, depth + 1); } catch { }
                }
                break;
        }
    }

    async Task<JsonObject> Observe(long start)
    {
        var obsInstances = new JsonArray();
        var outputs = new List<JsonObject>();
        foreach (var rec in Service.Instances)
            if (rec.State?.Output is string o && Unwrap(o) is JsonObject oo) outputs.Add(oo);
        outputs.AddRange(handlerOutputs);
        // What the engine actually stored, across every instance and execution.
        var stored = new HashSet<string>();
        foreach (var rec in Service.Instances)
            foreach (var hjson in Service.StoredHistories(rec)) ScanNames(JToken.Parse(hjson), stored);
        var storedForbidden = Forbidden.Where(stored.Contains).ToList();
        // R-09-11 evidence: task results that arrived after their attempt timer won, and were abandoned.
        var late = 0;
        foreach (var rec in Service.Instances)
            foreach (var hjson in Service.StoredHistories(rec))
                foreach (var ev in JArray.Parse(hjson).OfType<JObject>())
                    if (((string?)ev["$type"])?.Contains("TaskCompletedEvent") == true && J.Truthy(J.P(Unwrap((string?)ev["Result"]), "late"))) late++;

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
                ["ctx"] = Store.Full(J.P(o, "state"), J.S(o, "payloadRef") ?? ""),
            };
        }
        if (J.Truthy(J.P(spec, "handler")))
        {
            if (handlerOutputs.Count > 0) obsInstances.Add(Inst("root", handlerOutputs[0]));
        }
        else
        {
            foreach (var (label, id) in roots)
            {
                var rec = Service.Find(id);
                if (rec?.State?.Output is string o && Unwrap(o) is JsonObject oo) obsInstances.Add(Inst(label, oo));
                else log.Add($"{label} ({id}) did not complete: {rec?.State?.OrchestrationStatus}");
            }
        }
        var sent = await party.Call(new JsonObject { ["op"] = "sent" });
        int sum(string k) => outputs.Sum(o => (int)(J.Num(o, k) ?? 0));
        return new JsonObject
        {
            ["target"] = "dtfx",
            ["instances"] = obsInstances,
            ["messages"] = J.Clone(J.P(sent, "messages")),
            ["unmatched"] = frontDoorUnmatched + sum("unmatched"),
            ["duplicates"] = frontDoorDuplicates + sum("duplicates"),
            ["earlyKept"] = sum("earlyKept"),
            ["durableInstances"] = Service.Instances.Count,
            ["startMs"] = start,
            ["engine"] = new JsonObject
            {
                ["distribution"] = ClaimCheckStore.Distribution is { } dd ? new JsonObject { ["id"] = dd, ["orchestration"] = DtfxEngine.OrchestrationName } : null,
                ["orchestrationInstances"] = Service.Instances.Count,
                ["executions"] = Service.Instances.Sum(r => Service.StoredHistories(r).Count()),
                ["historyEvents"] = Service.Instances.Sum(r => Service.StoredHistories(r).Sum(hh => JArray.Parse(hh).Count)),
                ["episodes"] = Service.Episodes,
                ["activityRuns"] = Service.ActivityRuns,
                ["lateResultsAbandoned"] = late,
                ["droppedMessages"] = Service.DroppedMessages,
                ["virtualEnd"] = Duration.Iso(Clock.Now),
                ["log"] = J.Arr(log),
                // A runtime build of this harness journals the claim check to the store (TH_STORE_PROVIDER).
                ["store"] = ClaimCheckStore.DefaultJournal?.Report(),
            },
        };
    }
}
