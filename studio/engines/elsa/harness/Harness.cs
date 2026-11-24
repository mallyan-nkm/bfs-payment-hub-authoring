using System.Text.Json;
using System.Text.Json.Nodes;
using TransactHub.Engines;

namespace ElsaHarness;

// The Elsa harness for one conformance scenario or one deployment (I-30). It takes the same input as
// the DTFx harness and prints the same observation. Elsa runs each engine instance as a workflow
// instance of the one generic activity; this host carries out what an instance asks for (an activity
// attempt through the party service or a worker, a timer, a child instance, an event to another
// instance), on a virtual clock, and resumes the instance's bookmark with each result. It also plays
// the front door for the party's later messages and raises signals as events.

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Console.Out.WriteLine("ElsaHarness: give the input file"); return 2; }
        // `serve <plan>`: one service for a distribution on the runtime's engine host, kept in the store (Issue I-165).
        if (args.Length == 2 && args[0] == "serve") return await Harness.ServeMain(args[1]);
        var input = (JsonObject)J.Parse(await File.ReadAllTextAsync(args[0]))!;
        var h = new Harness(input);
        int code;
        try
        {
            // A span for the run and the payments it ended (EngineTelemetry: nothing listens unless a runtime host attached OpenTelemetry).
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var run = EngineTelemetry.StartRun("elsa", input);
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
        Environment.Exit(code);
        return code;
    }
}

/// <summary>One engine instance: one or more executions, each a workflow instance in Elsa.</summary>
public sealed class Instance
{
    public string Id = "";
    public JsonObject Input = new();
    public string? State;                // the latest serialised Elsa workflow state
    public readonly List<string> Stored = new(); // the final state of each execution, for the scan
    public int Executions, Episodes;
    public bool Completed;
    public JsonObject? Output;
    public string? Awaiting;
    public HashSet<int> Cancelled = new();
    public Instance? Parent;
    public int ParentOp;
    public int LateResults;
}

public sealed partial class Harness : IDisposable
{
    readonly JsonObject input;
    readonly JsonObject built;
    readonly JsonObject spec;
    readonly JsonObject config;
    public VirtualClock Clock = null!;
    public readonly ClaimCheckStore Store = new();
    ElsaHost elsa = null!;
    IParty party = null!;
    Deployment dep = null!;
    readonly List<Instance> instances = new();
    readonly List<(string label, string id)> roots = new();
    readonly List<JsonObject> handlerOutputs = new();
    int frontDoorUnmatched, frontDoorDuplicates, handlerSeq, activityRuns, elsaRuns, dropped;
    readonly List<string> log = new();

    public Harness(JsonObject input)
    {
        this.input = input;
        built = (JsonObject)J.P(input, "built")!;
        // A bank's distribution runs as its own copy (R-09-55): its workflow definition carries it, and its payments are pinned to it.
        if (J.S(input, "distribution") is { } dist) { ElsaHost.Distribution = dist; ClaimCheckStore.Distribution = dist; }
        spec = (JsonObject)J.P(built, "spec")!;
        config = (JsonObject)J.P(built, "config")!;
    }

    public void Dispose() => party?.Dispose();

    public async Task<JsonObject> Run()
    {
        var start = DateTimeOffset.Parse($"{J.S(config, "evaluationDate")}T{J.S(config, "startTime") ?? "06:00:00"}Z").ToUnixTimeMilliseconds();
        dep = new Deployment((JsonObject)J.P(input, "documents")!, config, start, (int)(J.Num(input, "chunkSize") ?? 100)) { ItemConcurrency = (int)(J.Num(input, "itemConcurrency") ?? 1) };
        Deployment.Current = dep;
        InterpreterActivity.Dep = dep;
        Clock = new VirtualClock(start);
        elsa = new ElsaHost();

        // A deployment calls the separate workers; a kit run calls the party service.
        party = J.P(input, "workers") is JsonObject workers
            ? new WorkerRouter(workers, (JsonObject)J.P(input, "queues")!, J.S(input, "distribution"))
            : new PartyClient(J.S(input, "studioRoot")!);
        await party.Call(new JsonObject { ["op"] = "open", ["scenario"] = J.S(built, "id") });

        var ctx = (JsonObject)J.Clone(J.P(built, "context"))!;
        var workflow = J.S(built, "workflow")!;
        if (J.P(spec, "instances") is JsonArray list)
        {
            var n = 0;
            foreach (var s in list)
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
            // A long-running host runs many payments on one store: each root's instance id is its own (rootId), its label stays root.
            var rootId = J.S(input, "rootId") ?? "root";
            roots.Add(("root", rootId));
            Clock.Schedule(start, () => StartRoot(rootId, $"{workflow}@{J.S(doc, "release")}", ctx));
        }

        // Run on after the roots end, so that late messages reach the front door and are recorded.
        bool Done()
        {
            if (J.Truthy(J.P(spec, "handler"))) return handlerOutputs.Count > 0 && Clock.PendingTimers == 0;
            // In real time (the engine host, R-09-93) the run ends with its roots: the timers left are for nothing that runs.
            return roots.All(r => Find(r.id)?.Completed == true) && (Clock.RealTime || Clock.PendingTimers == 0);
        }
        var finished = Clock.RunUntil(Done, start + Duration.Ms("P60D"), TimeSpan.FromSeconds(60));
        liveStop.Cancel();
        if (!finished) log.Add("the clock ran out of work before every root ended");
        return await Observe(start);
    }

    Instance? Find(string id) => instances.LastOrDefault(i => i.Id == id);

    /// <summary>
    /// The live path from the runtime (R-05-57): with a store, the root's payment is followed on the outbox, and each row is raised on
    /// the instance at the clock's now: a signal or an operator's control as a signal event, a hold's decision as its message.
    /// </summary>
    void FollowLive(string id, string payloadRef, JsonNode? header, string engine)
    {
        if (LiveSignals.Source is not { } source) return;
        Clock.Enter();
        _ = Task.Run(async () =>
        {
            ILiveFeed? feed;
            try { feed = await source.Follow(payloadRef, engine, id, null); }
            finally { Clock.Exit(); }
            if (feed is null) return;
            liveFeeds++;
            Clock.Hold = () => LiveSignals.HoldClock(true);
            await LiveSignals.Run(feed, row =>
            {
                var (name, data) = LiveSignals.EventOf(row, header);
                var done = new TaskCompletionSource<string>();
                Clock.Schedule(Clock.Now, () => done.TrySetResult(RaiseLive(id, name, data)));
                return done.Task;
            }, liveStop.Token);
        });
    }

    int liveFeeds;
    readonly CancellationTokenSource liveStop = new();

    string RaiseLive(string id, string name, JsonObject data)
    {
        var inst = Find(id);
        if (inst is null || inst.Completed) return "ended";
        Raise(id, name, data);
        return "delivered";
    }

    // ------------------------------------------------------------------ instances on Elsa

    void StartRoot(string id, string docKey, JsonObject ctx)
    {
        var (state, payload) = ClaimCheckStore.Split(ctx);
        var r = $"{id}#ctx";
        Store.Create(r, payload, ClaimCheckStore.AcceptFor(state, J.S(config, "tenant"), docKey));
        var inp = new JsonObject { ["kind"] = "workflow", ["doc"] = docKey, ["state"] = state, ["payloadRef"] = r };
        // The legs the front door chose from the dispatch table fill the platform workflow's slots (R-05-08).
        if (J.P(built, "legs") is JsonObject lg) inp["legs"] = J.Clone(lg);
        StartInstance(id, inp, null, 0);
        if (id == (J.S(input, "rootId") ?? "root")) FollowLive(id, r, J.P(state, "header"), "elsa");
        // Signals are events on the instance, raised at their virtual times (R-05-12).
        foreach (var s in J.A(config, "signals"))
        {
            var sig = J.S(s, "signal")!;
            Clock.Schedule(Clock.Start + Duration.Ms(J.S(s, "after")!), () => Raise(id, "signal", new JsonObject { ["signal"] = sig }));
        }
    }

    void StartInstance(string id, JsonObject inp, Instance? parent, int parentOp)
    {
        var inst = Find(id);
        if (inst is null || inst.Completed && inst.Executions == 0)
        {
            inst = new Instance { Id = id, Parent = parent, ParentOp = parentOp };
            instances.Add(inst);
        }
        inst.Input = inp;
        inst.Executions++;
        inst.Completed = false;
        var journal = new JsonObject { ["id"] = id, ["input"] = J.Clone(inp), ["startAt"] = Clock.Now, ["entries"] = new JsonArray() };
        // On the runtime's engine host, the execution is on the distribution's history before Elsa runs it (Issue I-165).
        history?.Append("instance", new JsonObject { ["id"] = id, ["execution"] = inst.Executions, ["input"] = J.Clone(inp), ["startAt"] = Clock.Now, ["parent"] = parent?.Id, ["parentOp"] = parentOp });
        elsaRuns++;
        After(inst, elsa.Start($"{id}#{inst.Executions}", journal));
    }

    /// <summary>Resume an instance's bookmark with results or events. One that has ended takes nothing.</summary>
    /// <param name="from">The send this delivers (instance, execution and operation), so that a rebuilt service sends it once.</param>
    void Deliver(Instance inst, JsonArray entries, string? from = null)
    {
        if (inst.Completed || inst.State is null) { dropped += entries.Count; return; }
        // What reaches the instance is on the history before it does: a rebuilt service gives Elsa the same journal (Issue I-165).
        history?.Append("deliver", new JsonObject { ["id"] = inst.Id, ["execution"] = inst.Executions, ["entries"] = J.Clone(entries), ["from"] = from });
        elsaRuns++;
        After(inst, elsa.Resume(inst.State, entries));
    }

    void After(Instance inst, ElsaHost.Result r)
    {
        inst.State = r.State;
        var j = r.Journal;
        inst.Episodes = (int)(J.Num(j, "episodes") ?? 0);
        inst.Awaiting = J.S(j, "awaiting");
        inst.Cancelled = new HashSet<int>(J.A(j, "cancelled").Select(x => (int)J.D(x)!.Value));
        var immediate = new JsonArray();
        // The operations handed out, on the history before they are carried out (Issue I-165).
        if (history is not null && J.A(j, "pending").Count > 0)
            history.Append("dispatch", new JsonObject { ["id"] = inst.Id, ["execution"] = inst.Executions, ["ops"] = J.Clone(J.P(j, "pending")) });
        foreach (var op in J.A(j, "pending")) Dispatch(inst, (JsonObject)op!, immediate);
        if (immediate.Count > 0) Clock.Schedule(Clock.Now, () => Deliver(inst, Stamp(immediate)));
        if (!J.Truthy(J.P(j, "done"))) return;
        inst.Stored.Add(r.State);
        inst.LateResults += J.A(j, "entries").Count(e => J.Truthy(J.P(J.P(e, "result"), "late")));
        if (J.P(j, "continueAsNew") is JsonObject next)
        {
            // A new execution of the same instance, with a fresh journal (continue-as-new).
            Clock.Schedule(Clock.Now, () => StartInstance(inst.Id, next, inst.Parent, inst.ParentOp));
            inst.Completed = true;
            return;
        }
        inst.Completed = true;
        inst.Output = (JsonObject?)J.P(j, "output");
        if (inst.Parent is Instance p)
        {
            var e = new JsonObject { ["op"] = inst.ParentOp, ["result"] = J.Clone(inst.Output) };
            Clock.Schedule(Clock.Now, () => Deliver(p, Stamp(new JsonArray(e))));
        }
    }

    /// <summary>Entries carry the virtual time at which they reach the instance.</summary>
    JsonArray Stamp(JsonArray entries)
    {
        foreach (var e in entries) ((JsonObject)e!)["at"] = Clock.Now;
        return entries;
    }

    void Dispatch(Instance inst, JsonObject op, JsonArray immediate)
    {
        var id = (int)J.Num(op, "id")!.Value;
        switch (J.S(op, "type"))
        {
            case "activity":
            {
                var name = J.S(op, "name")!;
                var a = (JsonObject)J.P(op, "input")!;
                if (name == Interp.ClaimCheck) { immediate.Add(new JsonObject { ["op"] = id, ["result"] = ClaimCheckOp(a) }); break; }
                activityRuns++;
                var (result, at) = Attempt(a);
                if (result is null) break; // never answers and has no deadline
                var e = new JsonObject { ["op"] = id, ["result"] = result };
                if (at <= Clock.Now) immediate.Add(e);
                else Clock.Schedule(at, () => Deliver(inst, Stamp(new JsonArray(e))));
                break;
            }
            case "timer":
            {
                var at = (long)J.Num(op, "at")!.Value;
                Clock.Schedule(at, () =>
                {
                    if (inst.Completed || inst.Cancelled.Contains(id)) return;
                    Deliver(inst, Stamp(new JsonArray(new JsonObject { ["op"] = id, ["result"] = null })));
                });
                break;
            }
            case "child":
            {
                var target = J.S(op, "target")!;
                var cin = (JsonObject)J.Clone(J.P(op, "input"))!;
                Clock.Schedule(Clock.Now, () => StartInstance(target, cin, inst, id));
                break;
            }
            case "send":
            {
                var target = J.S(op, "target")!;
                var ev = new JsonObject { ["event"] = J.S(op, "name"), ["data"] = J.Clone(J.P(op, "input")) };
                var from = $"{inst.Id}#{inst.Executions}#{id}";
                Clock.Schedule(Clock.Now, () =>
                {
                    var t = Find(target);
                    if (t is not null) Deliver(t, Stamp(new JsonArray(ev)), from);
                });
                break;
            }
            default: throw new InvalidOperationException($"unknown operation {J.S(op, "type")}");
        }
    }

    void Raise(string id, string name, JsonObject data)
    {
        var inst = Find(id);
        if (inst is null || inst.Completed) return;
        Deliver(inst, Stamp(new JsonArray(new JsonObject { ["event"] = name, ["data"] = data })));
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
    /// by correlation as an event to the instance it belongs to, which keeps it if it is early and
    /// ignores it if it is a copy. One that finds no running instance is unmatched.
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
        var candidates = instances.Where(i => J.S(i.Input, "kind") != "batch" && Correlates(J.Get(i.Input, "state.header"), m)).ToList();
        var alive = candidates.FirstOrDefault(c => !c.Completed && c.Awaiting == type) ?? candidates.LastOrDefault(c => !c.Completed);
        var target = alive ?? candidates.LastOrDefault();
        if (target is null) { frontDoorUnmatched++; return; }
        if (target.Completed)
        {
            var key = J.S(m, "inReplyTo") is string irt ? $"{type}:{irt}:{J.S(m, "status") ?? ""}" : J.S(m, "id")!;
            var consumed = candidates.Where(c => c.Completed).Any(c => J.Strings(J.P(c.Output, "consumed")).Contains(key));
            if (consumed) frontDoorDuplicates++; else frontDoorUnmatched++;
            return;
        }
        Deliver(target, Stamp(new JsonArray(new JsonObject { ["event"] = "message", ["data"] = m })));
    }

    /// <summary>A handler runs as a plain request handler, with no workflow instance (R-09-14).</summary>
    void StartHandler(JsonObject m, JsonObject entry, string handler)
    {
        var docv = dep.Resolve(handler, null, Clock.Elapsed);
        if (docv is null) { frontDoorUnmatched++; return; }
        var type = J.S(m, "type")!;
        var scope = string.Join('.', type.Split('.').Take(2));
        var ctx = new JsonObject
        {
            ["header"] = new JsonObject { ["conversation"] = J.S(m, "conversation"), ["scheme"] = scope, ["direction"] = J.S(entry, "direction") == "in" ? "inward" : "outward", ["receivedAt"] = Duration.Iso(Clock.Now) },
            ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["id"] = J.S(m, "id"), ["type"] = type } },
        };
        var id = $"handler-{++handlerSeq}";
        var (state, payload) = ClaimCheckStore.Split(ctx);
        Store.Create($"{id}#ctx", payload);
        var inp = new JsonObject { ["kind"] = "handler", ["doc"] = $"{J.S(docv, "name")}@{J.S(docv, "release")}", ["state"] = state, ["payloadRef"] = $"{id}#ctx", ["partyKey"] = id };
        var interp = new Interp(new HandlerEngine(this, id), dep);
        var run = interp.Run(inp);
        run.ContinueWith(t =>
        {
            JsonObject o;
            if (t.IsFaulted) { o = interp.Output(); o["failed"] = t.Exception!.GetBaseException().Message; }
            else o = t.Result;
            handlerOutputs.Add(o);
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    // ------------------------------------------------------------------ activities

    /// <summary>
    /// One activity attempt, through the party service (or a worker) with the instance's time as now.
    /// The party's later messages come back as deliveries for the front door. Returns the result and
    /// the virtual time it reaches the instance: after the stub's latency; for an attempt that never
    /// answers, a late timeout at its own deadline, or nothing when it has none.
    /// </summary>
    public (JsonObject? result, long at) Attempt(JsonObject a)
    {
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
        var res = party.Call(req).GetAwaiter().GetResult();
        Schedule(res);
        var at = now;
        if (J.S(dep.Stub(step, activity), "latency") is string latency) at = now + Duration.Ms(latency);
        var kind = J.S(res, "kind");
        if (kind == "hang")
        {
            if (J.Num(a, "deadline") is double dl)
            {
                var late = (long)dl + 1000;
                return (new JsonObject { ["kind"] = "error", ["name"] = "Timeout", ["retryable"] = true, ["message"] = "no answer before the attempt's own deadline", ["late"] = true, ["at"] = late }, late);
            }
            return (null, long.MaxValue);
        }
        if (kind == "error")
            return (new JsonObject { ["kind"] = "error", ["name"] = J.S(res, "name"), ["retryable"] = J.P(res, "retryable")?.DeepClone(), ["message"] = J.S(res, "message"), ["at"] = at }, at);
        // A hold opened its item: the workflow awaits the operator's decision (Issue I-98).
        if (kind == "held") return (new JsonObject { ["kind"] = "held", ["hold"] = J.S(res, "hold"), ["expiry"] = J.S(res, "expiry"), ["expiryCalendar"] = J.S(res, "expiryCalendar"), ["calendar"] = J.Clone(J.P(res, "calendar")), ["at"] = at }, at);
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
            ["stagedPaths"] = J.Arr(stagedWrites.Select(w => w.Item1)), ["decisions"] = J.Clone(J.P(res, "decisions")), ["at"] = at,
        };
        if (stagedWrites.Count > 0) outp["token"] = Store.Stage(payloadRef, stagedWrites);
        // The rule trace is stored with the payment (R-03-*), when the party reports one.
        if (J.P(res, "ruleTrace") is JsonNode rt)
        {
            Store.RuleTraces[$"{payloadRef}|{step}|{J.Num(a, "call")}"] = J.Clone(rt);
            outp["ruleTrace"] = true;
        }
        return (outp, at);
    }

    public JsonObject ClaimCheckOp(JsonObject a)
    {
        switch (J.S(a, "op"))
        {
            case "commit": Store.Commit(J.S(a, "token")!); return new JsonObject { ["ok"] = true };
            case "fork": Store.Fork(J.S(a, "from")!, J.S(a, "to")!); return new JsonObject { ["ok"] = true };
            case "copy": Store.Copy(J.S(a, "from")!, J.S(a, "to")!, J.Strings(J.P(a, "paths"))); return new JsonObject { ["ok"] = true };
            case "count": return new JsonObject { ["count"] = Store.Count(J.S(a, "ref")!, J.S(a, "path")!) };
            // The bank's values as of a step, from the runtime's store, on an engine host that reads them live (R-09-135).
            case "values": return Deployment.ValuesOp(a);
            case "diff":
                var x = Store.Full(J.P(a, "aState"), J.S(a, "aRef")!);
                var y = Store.Full(J.P(a, "bState"), J.S(a, "bRef")!);
                return new JsonObject { ["paths"] = J.Arr(J.DiffPaths(x, y)) };
        }
        throw new InvalidOperationException($"claim-check: unknown op {J.S(a, "op")}");
    }

    // ------------------------------------------------------------------ observation

    static readonly string[] Forbidden = { "payment", "items", "ext" };

    /// <summary>Property names in a stored JSON document, looking into JSON held as strings too.</summary>
    static void ScanNames(JsonNode? n, HashSet<string> names, int depth = 0)
    {
        if (n is null || depth > 64) return;
        switch (n)
        {
            case JsonObject o:
                foreach (var (k, v) in o) { if (!k.StartsWith("$")) names.Add(k); ScanNames(v, names, depth + 1); }
                break;
            case JsonArray arr:
                foreach (var x in arr) ScanNames(x, names, depth + 1);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                var s = v.GetValue<string>().TrimStart();
                if (s.Length > 1 && (s[0] == '{' || s[0] == '['))
                {
                    try { ScanNames(JsonNode.Parse(s), names, depth + 1); } catch { }
                }
                break;
        }
    }

    /// <summary>One instance's part of an observation, from the output the interpreter gave it.</summary>
    JsonObject InstanceObservation(string label, JsonObject o, long start, IEnumerable<string> storedKeys)
    {
        var keys = new SortedSet<string>(J.Strings(J.P(o, "engineStateKeys")));
        foreach (var k in storedKeys) keys.Add(k);
        return new JsonObject
        {
            ["label"] = label, ["trace"] = J.Clone(J.P(o, "trace")), ["detail"] = J.Clone(J.P(o, "detail")), ["releaseTrace"] = J.Clone(J.P(o, "releaseTrace")),
            ["steps"] = J.Clone(J.P(o, "steps")), ["attempts"] = J.Clone(J.P(o, "attempts")), ["ruleTraceSteps"] = J.Clone(J.P(o, "ruleTraceSteps")),
            ["engineStateKeys"] = J.Arr(keys), ["startedAt"] = (long)(J.Num(o, "startedAt") ?? start) - start,
            ["endedAt"] = J.Num(o, "endedAt") is double e ? (long)e - start : null, ["failed"] = J.S(o, "failed"),
            ["ctx"] = Store.Full(J.P(o, "state"), J.S(o, "payloadRef") ?? ""),
        };
    }

    async Task<JsonObject> Observe(long start)
    {
        var outputs = instances.Where(i => i.Output is not null).Select(i => i.Output!).ToList();
        outputs.AddRange(handlerOutputs);
        // What Elsa actually stored: the serialised workflow state of every execution.
        var stored = new HashSet<string>();
        foreach (var i in instances)
        {
            foreach (var s in i.Stored) ScanNames(JsonNode.Parse(s), stored);
            if (!i.Completed && i.State is not null) ScanNames(JsonNode.Parse(i.State), stored);
        }
        var storedForbidden = Forbidden.Where(stored.Contains).ToList();

        JsonObject Inst(string label, JsonObject o) => InstanceObservation(label, o, start, storedForbidden);
        var obsInstances = new JsonArray();
        if (J.Truthy(J.P(spec, "handler")))
        {
            if (handlerOutputs.Count > 0) obsInstances.Add(Inst("root", handlerOutputs[0]));
        }
        else
        {
            foreach (var (label, id) in roots)
            {
                var inst = Find(id);
                if (inst?.Output is JsonObject o) obsInstances.Add(Inst(label, o));
                else log.Add($"{label} ({id}) did not complete");
            }
        }
        var sent = await party.Call(new JsonObject { ["op"] = "sent" });
        int sum(string k) => outputs.Sum(o => (int)(J.Num(o, k) ?? 0));
        return new JsonObject
        {
            ["target"] = "elsa",
            ["instances"] = obsInstances,
            ["messages"] = J.Clone(J.P(sent, "messages")),
            ["unmatched"] = frontDoorUnmatched + sum("unmatched"),
            ["duplicates"] = frontDoorDuplicates + sum("duplicates"),
            ["earlyKept"] = sum("earlyKept"),
            ["durableInstances"] = instances.Select(i => i.Id).Distinct().Count(),
            ["startMs"] = start,
            ["engine"] = new JsonObject
            {
                ["distribution"] = ClaimCheckStore.Distribution is { } dd ? new JsonObject { ["id"] = dd, ["definition"] = $"transacthub.interpreter@{dd}" } : null,
                ["workflowInstances"] = instances.Sum(i => i.Executions),
                ["orchestrationInstances"] = instances.Select(i => i.Id).Distinct().Count(),
                ["executions"] = instances.Sum(i => i.Executions),
                ["episodes"] = elsaRuns,
                ["activityRuns"] = activityRuns,
                ["historyEvents"] = instances.Sum(i => i.Stored.Sum(s => s.Length)) / 1024,
                ["lateResultsAbandoned"] = instances.Sum(i => i.LateResults),
                ["droppedMessages"] = dropped,
                ["virtualEnd"] = Duration.Iso(Clock.Now),
                ["log"] = J.Arr(log),
                // A runtime build of this harness journals the claim check to the store (TH_STORE_PROVIDER).
                ["store"] = ClaimCheckStore.DefaultJournal?.Report(),
            },
        };
    }
}

/// <summary>
/// The engine operations of a handler: a plain request handler with no workflow instance and no
/// durable state. Its attempts and timers run on the harness's virtual clock.
/// </summary>
public sealed class HandlerEngine : IEngine
{
    readonly Harness h;
    public HandlerEngine(Harness h, string id) { this.h = h; InstanceId = id; }
    public long Now => h.Clock.Now;
    public string InstanceId { get; }
    public bool Durable => false;

    public Task<JsonObject> Activity(string name, JsonObject input)
    {
        if (name == Interp.ClaimCheck) return Task.FromResult(h.ClaimCheckOp(input));
        var tcs = new TaskCompletionSource<JsonObject>();
        var (result, at) = h.Attempt(input);
        if (result is not null) h.Clock.Schedule(at, () => tcs.TrySetResult(result));
        return tcs.Task;
    }

    public Task Timer(long at, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        h.Clock.Schedule(at, () => tcs.TrySetResult());
        ct.Register(() => tcs.TrySetCanceled());
        return tcs.Task;
    }

    public Task<JsonObject> Child(string instanceId, JsonObject input) => throw new NotSupportedException("a handler starts no child instance");

    public async Task<T> WithRetry<T>(RetryPlan o, Func<Task<T>> call)
    {
        Exception? last = null;
        var first = Now;
        for (var n = 0; n < o.MaxNumberOfAttempts; n++)
        {
            try { return await call(); }
            catch (Exception e) { last = e; }
            if (n + 1 == o.MaxNumberOfAttempts) break;
            var deadline = o.RetryTimeout == TimeSpan.MaxValue ? long.MaxValue : first + (long)o.RetryTimeout.TotalMilliseconds;
            if (!o.Handle(last) || Now >= deadline) break;
            await Timer(Now + (long)o.Delay(n).TotalMilliseconds, CancellationToken.None);
        }
        throw last!;
    }

    public void SendEvent(string instanceId, string name, JsonObject data) => throw new NotSupportedException();
    public void ContinueAsNew(JsonObject input) => throw new NotSupportedException();
}
