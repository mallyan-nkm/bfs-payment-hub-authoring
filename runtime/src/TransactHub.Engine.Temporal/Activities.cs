using System.Text.Json.Nodes;
using Temporalio.Activities;
using Temporalio.Converters;
using TransactHub.Engines;
using TransactHub.Engines.Durable;

namespace TransactHub.Engine.Temporal;

/// <summary>
/// Everything outside the workflows: activity attempts go to the party (the party service in a kit
/// run, the separate workers of a deployment), payloads stay in the claim-check store, and handlers
/// run here, as plain request handlers. Workflow history holds references only (R-09-10).
/// </summary>
public sealed class Host
{
    public readonly IClaimCheck Store;
    public const string KeepOutput = "transacthub.keep-output";
    /// <summary>Outputs of runs too large for engine history, by reference.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Outputs = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<int, string>> pieces = new();
    public readonly IParty Party;
    public readonly Deployment Dep;
    public int ActivityRuns;

    public Host(IParty party, Deployment dep, IClaimCheck? store = null)
    {
        Party = party;
        Dep = dep;
        Store = store ?? new MemoryClaimCheck();
    }

    /// <summary>The one activity type of every task queue: it serves `name@release`, the claim check and handlers.</summary>
    [Activity(Dynamic = true)]
    public async Task<string> AnyAsync(IRawValue[] args)
    {
        var ctx = ActivityExecutionContext.Current;
        var name = ctx.Info.ActivityType;
        var raw = ctx.PayloadConverter.ToValue<string>(args[0]);
        if (name == KeepOutput)
        {
            var piece = (JsonObject)J.Parse(raw)!;
            var key = J.S(piece, "key")!;
            pieces.GetOrAdd(key, _ => new System.Collections.Concurrent.ConcurrentDictionary<int, string>())[(int)J.Num(piece, "piece")!.Value] = J.S(piece, "text")!;
            var all = pieces[key];
            Outputs[key] = string.Concat(Enumerable.Range(0, all.Count).Select(i => all.TryGetValue(i, out var t) ? t : ""));
            return J.Str(JsonValue.Create(key));
        }
        var input = (JsonObject)J.Parse(raw)!;
        Interlocked.Increment(ref ActivityRuns);
        if (name == Interp.ClaimCheck) return J.Str(await ClaimCheckOp(input));
        if (name == FrontDoorWorkflow.HandlerActivity) return J.Str(await RunHandler(input));
        return J.Str(await Attempt(input));
    }

    /// <summary>
    /// One attempt. The party answers at once; how long the answer takes (a stub's latency) and an
    /// answer that never comes are handed back for the workflow to play on Temporal's timers.
    /// </summary>
    public async Task<JsonObject> Attempt(JsonObject a)
    {
        var step = J.S(a, "step")!;
        var activity = J.S(a, "activity")!;
        var payloadRef = J.S(a, "payloadRef")!;
        var item = J.Num(a, "item") is double d ? (int?)d : null;
        var ctx = item is int ix ? Store.FullForItem(J.P(a, "state"), payloadRef, ix) : Store.Full(J.P(a, "state"), payloadRef);
        var req = new JsonObject
        {
            ["op"] = "attempt", ["instance"] = J.S(a, "instance"), ["activity"] = activity, ["release"] = J.S(a, "release"), ["step"] = step,
            ["call"] = J.Num(a, "call"), ["attempt"] = J.Num(a, "attempt"), ["now"] = J.Num(a, "now"), ["ctx"] = ctx, ["level"] = J.S(a, "level"), ["policy"] = J.Clone(J.P(a, "policy")),
            // The step whose handler the attempt is in (R-09-241, Issue I-290).
            ["handlerOf"] = J.Clone(J.P(a, "handlerOf")),
        };
        var res = await Party.Call(req);
        var deliveries = J.Clone(J.P(res, "deliveries")) ?? new JsonArray();
        double? latency = J.S(Dep.Stub(step, activity), "latency") is string l ? Duration.Ms(l) : null;
        var kind = J.S(res, "kind");
        JsonObject outp;
        if (kind == "hang") outp = new JsonObject { ["kind"] = "hang" };
        // A hold opened its item: the workflow awaits the operator's decision (Issue I-98).
        else if (kind == "held") outp = new JsonObject { ["kind"] = "held", ["hold"] = J.S(res, "hold"), ["expiry"] = J.S(res, "expiry"), ["expiryCalendar"] = J.S(res, "expiryCalendar"), ["calendar"] = J.Clone(J.P(res, "calendar")) };
        else if (kind == "error")
            outp = new JsonObject { ["kind"] = "error", ["name"] = J.S(res, "name"), ["retryable"] = J.P(res, "retryable")?.DeepClone(), ["message"] = J.S(res, "message") };
        else
        {
            var stateWrites = new JsonObject();
            var staged = new List<(string, JsonNode?)>();
            foreach (var (p, v) in J.Obj(J.P(res, "writes")).ToList())
            {
                if (item is int i) staged.Add(($"items[{i}].{p}", J.Clone(v)));
                else if (ClaimCheckStore.IsStatePath(p)) stateWrites[p] = J.Clone(v);
                else staged.Add((p, J.Clone(v)));
            }
            outp = new JsonObject
            {
                ["kind"] = "result", ["outcome"] = J.S(res, "outcome"), ["reasonCode"] = J.S(res, "reasonCode"), ["stateWrites"] = stateWrites,
                ["stagedPaths"] = J.Arr(staged.Select(w => w.Item1)), ["decisions"] = J.Clone(J.P(res, "decisions")),
            };
            if (staged.Count > 0) outp["token"] = await Store.Stage(payloadRef, staged);
            if (J.P(res, "ruleTrace") is JsonNode rt)
            {
                lock (Store.RuleTraces) Store.RuleTraces[$"{payloadRef}|{step}|{J.Num(a, "call")}"] = J.Clone(rt);
                outp["ruleTrace"] = true;
            }
        }
        outp["deliveries"] = deliveries;
        if (latency is double ms) outp["latency"] = ms;
        return outp;
    }

    public async Task<JsonObject> ClaimCheckOp(JsonObject a)
    {
        switch (J.S(a, "op"))
        {
            case "commit": await Store.Commit(J.S(a, "token")!); return new JsonObject { ["ok"] = true };
            case "fork": await Store.Fork(J.S(a, "from")!, J.S(a, "to")!); return new JsonObject { ["ok"] = true };
            case "copy": await Store.Copy(J.S(a, "from")!, J.S(a, "to")!, J.Strings(J.P(a, "paths"))); return new JsonObject { ["ok"] = true };
            case "count": return new JsonObject { ["count"] = Store.Count(J.S(a, "ref")!, J.S(a, "path")!) };
            // The bank's values as of a step, from the runtime's store, on an engine host that reads them live (R-09-135).
            case "values": return Deployment.ValuesOp(a);
            case "bindings": return Deployment.BindingsOp(a);
            case "diff":
                var x = Store.Full(J.P(a, "aState"), J.S(a, "aRef")!);
                var y = Store.Full(J.P(a, "bState"), J.S(a, "bRef")!);
                return new JsonObject { ["paths"] = J.Arr(J.DiffPaths(x, y)) };
        }
        throw new InvalidOperationException($"claim-check: unknown op {J.S(a, "op")}");
    }

    /// <summary>A handler: a plain request handler with no durable state, run to its end here (R-09-14).</summary>
    async Task<JsonObject> RunHandler(JsonObject req)
    {
        var id = J.S(req, "id")!;
        var (state, payload) = ClaimCheckStore.Split((JsonObject)J.Clone(J.P(req, "ctx"))!);
        await Store.Create($"{id}#ctx", payload);
        var inp = new JsonObject { ["kind"] = "handler", ["doc"] = J.S(req, "doc"), ["state"] = state, ["payloadRef"] = $"{id}#ctx", ["partyKey"] = id };
        var eng = new HandlerEngine(this, id, (long)J.Num(req, "now")!.Value);
        var interp = new Interp(eng, Dep);
        JsonObject o;
        try { o = await interp.Run(inp); }
        catch (Exception e) { o = interp.Output(); o["failed"] = e.Message; }
        return new JsonObject { ["output"] = o, ["deliveries"] = eng.Deliveries };
    }
}

/// <summary>
/// The engine a handler runs on: no durability, no timers of its own. Time moves only by what the
/// handler's own calls take (a stub's latency, a retry's backoff), from the moment it was started.
/// </summary>
public sealed class HandlerEngine(Host host, string id, long start) : IEngine
{
    long now = start;
    public readonly JsonArray Deliveries = new();
    public long Now => now;
    public string InstanceId => id;
    public bool Durable => false;

    public async Task<JsonObject> Activity(string name, JsonObject input)
    {
        if (name == Interp.ClaimCheck) return await host.ClaimCheckOp(input);
        var res = await host.Attempt(input);
        foreach (var d in J.A(res, "deliveries")) Deliveries.Add(J.Clone(d));
        res.Remove("deliveries");
        if (J.Num(res, "latency") is double l) { now += (long)l; res.Remove("latency"); }
        if (J.S(res, "kind") == "hang")
        {
            if (J.Num(input, "deadline") is double dl) now = Math.Max(now, (long)dl + 1000);
            return new JsonObject { ["kind"] = "error", ["name"] = "Timeout", ["retryable"] = true, ["message"] = "no answer before the attempt's own deadline", ["late"] = true, ["at"] = now };
        }
        res["at"] = now;
        return res;
    }

    // A timer raced against an attempt fires only if the handler is waiting on nothing else; a
    // handler always is, so it never fires here.
    public Task Timer(long at, CancellationToken ct) => new TaskCompletionSource().Task;
    public Task<JsonObject> Child(string instanceId, JsonObject input) => throw new NotSupportedException("a handler starts no child");

    public async Task<T> WithRetry<T>(RetryPlan o, Func<Task<T>> call)
    {
        Exception? last = null;
        var first = now;
        for (var n = 0; n < o.MaxNumberOfAttempts; n++)
        {
            try { return await call(); }
            catch (Exception e) { last = e; }
            if (n + 1 == o.MaxNumberOfAttempts) break;
            var deadline = o.RetryTimeout == TimeSpan.MaxValue ? long.MaxValue : first + (long)o.RetryTimeout.TotalMilliseconds;
            if (!o.Handle(last) || now >= deadline) break;
            now += (long)o.Delay(n).TotalMilliseconds;
        }
        throw last!;
    }

    public void SendEvent(string instanceId, string name, JsonObject data) => throw new NotSupportedException("a handler sends no event");
    public void ContinueAsNew(JsonObject input) => throw new NotSupportedException("a handler does not continue as new");
}
