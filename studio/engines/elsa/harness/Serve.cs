using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TransactHub.Engines;

namespace ElsaHarness;

/// <summary>
/// The harness as a distribution's one service on the runtime's engine host (Issue I-165, R-09-196, R-09-197), as the DTFx
/// harness is one. Elsa keeps an instance as a journal it replays (Replay.cs), so the history holds what makes the journal: each
/// execution's start, everything delivered to it, and the operations it handed out. Elsa's own state is not kept: a service that
/// starts again gives Elsa each open execution's journal, Elsa replays it, and the operations that were handed out and not
/// answered are carried out again (an activity at least once; a timer at its own time; a child or a send only where it did not
/// happen). No store of Elsa's own beside the runtime's.
/// </summary>
public sealed partial class Harness
{
    IEngineHistory? history;
    ServiceParty serviceParty = null!;
    readonly ConcurrentDictionary<string, JsonObject> served = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> reported = new(StringComparer.Ordinal), told = new(StringComparer.Ordinal);
    JsonObject rebuilt = new();

    public static async Task<int> ServeMain(string planFile)
    {
        var plan = (JsonObject)J.Parse(await File.ReadAllTextAsync(planFile))!;
        var distribution = J.S(plan, "distribution") ?? throw new InvalidOperationException("a service runs one distribution: the plan names none");
        var (plans, documents, first) = ServiceLines.Of(plan);
        var input = (JsonObject)J.Clone(first)!;
        input["distribution"] = distribution;
        input["documents"] = documents;
        var h = new Harness(input);
        try
        {
            h.Serve(plans, distribution);
            return 0;
        }
        catch (Exception e)
        {
            ServiceLines.Out("ERROR", new JsonObject { ["error"] = e.ToString() });
            Environment.Exit(1);
            return 1;
        }
    }

    sealed class Execution
    {
        public string Id = "", Parent = "";
        public int Number, ParentOp;
        public JsonObject Input = new();
        public long StartAt;
        public readonly JsonArray Entries = new();
        public readonly SortedDictionary<int, JsonObject> Ops = new();
    }

    void Serve(JsonObject plans, string distribution)
    {
        history = EngineHistory.Open?.Invoke("elsa", distribution) ?? throw new InvalidOperationException("a service keeps its history in the store: set TH_STORE_PROVIDER and TH_STORE_CONNECTION");
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        dep = new Deployment((JsonObject)J.P(input, "documents")!, config, start, (int)(J.Num(input, "chunkSize") ?? 100)) { ItemConcurrency = (int)(J.Num(input, "itemConcurrency") ?? 1) };
        Deployment.Current = dep;
        InterpreterActivity.Dep = dep;
        Clock = new VirtualClock(start) { RealTime = true };
        elsa = new ElsaHost();
        party = serviceParty = new ServiceParty(plans, distribution);

        var facts = history.Load();
        var starts = new List<JsonObject>();
        foreach (var (code, d) in facts)
        {
            if (code == "start") starts.Add(d);
            else if (code == "reported" && J.S(d, "rootId") is string r) reported[r] = true;
        }
        foreach (var s in starts)
        {
            var rootId = J.S(s, "rootId")!;
            served[rootId] = s;
            serviceParty.Root(rootId, J.S(s, "workflow")!);
            if (!reported.ContainsKey(rootId)) history.RestorePayloads(Store, rootId);
        }
        rebuilt = Rebuild(facts);
        history.Append(facts.Count == 0 ? "opened" : "restarted", new JsonObject
        {
            ["engine"] = "elsa", ["distribution"] = distribution, ["definition"] = $"transacthub.interpreter@{distribution}", ["pid"] = Environment.ProcessId,
            ["rebuilt"] = J.Clone(rebuilt), ["payments"] = starts.Count, ["open"] = starts.Count(s => !reported.ContainsKey(J.S(s, "rootId")!)),
        });
        foreach (var s in starts)
        {
            var rootId = J.S(s, "rootId")!;
            if (reported.ContainsKey(rootId)) continue;
            // Its start was recorded but the service stopped before the instance was: it starts now, as it would have.
            if (Find(rootId) is null) StartInstance(rootId, (JsonObject)J.Clone(J.P(s, "input"))!, null, 0);
            if (Find(rootId) is { Completed: false }) FollowLive(rootId, $"{rootId}#ctx", J.Get(J.P(s, "input"), "state.header"), "elsa");
            ServiceLines.Out("RESUMED", new JsonObject { ["paymentId"] = J.S(s, "paymentId"), ["rootId"] = rootId, ["workflow"] = J.S(s, "workflow") });
        }

        ServiceLines.Read(c => Clock.Schedule(Clock.Now, () =>
        {
            try { Command(c); }
            catch (Exception e) { ServiceLines.Out("ERROR", new JsonObject { ["error"] = e.Message, ["paymentId"] = J.S(c, "paymentId") }); }
        }));
        ServiceLines.Out("READY", new JsonObject { ["engine"] = "elsa", ["distribution"] = distribution, ["history"] = history.Subject, ["rebuilt"] = J.Clone(rebuilt) });
        Clock.RunUntil(() => { Tell(); return false; }, long.MaxValue / 4, TimeSpan.FromHours(12));
    }

    /// <summary>
    /// The service as its history leaves it: each instance's latest execution started again on Elsa with its whole journal, so that
    /// Elsa replays it to where it was; then what was handed out and not answered is carried out again.
    /// </summary>
    JsonObject Rebuild(IReadOnlyList<(string code, JsonObject data)> facts)
    {
        var execs = new Dictionary<string, Execution>(StringComparer.Ordinal);
        var order = new List<string>();
        var sent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (code, d) in facts)
        {
            var id = J.S(d, "id");
            if (id is null || reported.ContainsKey(id.Split('/')[0])) continue;
            var n = (int)(J.Num(d, "execution") ?? 1);
            switch (code)
            {
                case "instance":
                    execs[id] = new Execution { Id = id, Number = n, Input = (JsonObject)J.Clone(J.P(d, "input"))!, StartAt = (long)(J.Num(d, "startAt") ?? 0), Parent = J.S(d, "parent") ?? "", ParentOp = (int)(J.Num(d, "parentOp") ?? 0) };
                    if (!order.Contains(id)) order.Add(id);
                    break;
                case "deliver":
                    if (execs.TryGetValue(id, out var e) && e.Number == n) foreach (var x in J.A(d, "entries")) e.Entries.Add(J.Clone(x));
                    if (J.S(d, "from") is string from) sent.Add(from);
                    break;
                case "dispatch":
                    if (execs.TryGetValue(id, out var e2) && e2.Number == n) foreach (var op in J.A(d, "ops")) e2.Ops[(int)J.Num(op, "id")!.Value] = (JsonObject)J.Clone(op)!;
                    break;
            }
        }
        var results = new List<(Instance inst, Execution e, ElsaHost.Result r)>();
        foreach (var id in order)
        {
            var e = execs[id];
            var inst = new Instance { Id = id, Input = e.Input, Executions = e.Number, Parent = e.Parent.Length > 0 ? Find(e.Parent) : null, ParentOp = e.ParentOp };
            instances.Add(inst);
            var journal = new JsonObject
            {
                ["id"] = id, ["input"] = J.Clone(e.Input), ["startAt"] = e.StartAt, ["entries"] = J.Clone(e.Entries),
                ["dispatched"] = new JsonArray(e.Ops.Keys.Select(k => (JsonNode?)k).ToArray()),
            };
            elsaRuns++;
            results.Add((inst, e, elsa.Start($"{id}#{e.Number}", journal)));
        }
        int again = 0, open = 0;
        foreach (var (inst, e, r) in results)
        {
            var done = J.Truthy(J.P(r.Journal, "done"));
            // A child whose end its parent took already is not handed to it again.
            var parent = inst.Parent;
            if (done && parent is not null && execs.TryGetValue(parent.Id, out var pe) && pe.Entries.Any(x => J.Num(x, "op") is double o && (int)o == inst.ParentOp)) inst.Parent = null;
            After(inst, r);
            inst.Parent = parent;
            if (inst.Completed) continue;
            open++;
            var answered = e.Entries.Select(x => J.Num(x, "op")).Where(x => x is not null).Select(x => (int)x!.Value).ToHashSet();
            var cancelled = J.A(r.Journal, "cancelled").Select(x => (int)J.D(x)!.Value).ToHashSet();
            var immediate = new JsonArray();
            foreach (var (opId, op) in e.Ops)
            {
                if (answered.Contains(opId) || cancelled.Contains(opId)) continue;
                var type = J.S(op, "type");
                if (type == "child" && execs.ContainsKey(J.S(op, "target")!)) continue;
                if (type == "send" && sent.Contains($"{e.Id}#{e.Number}#{opId}")) continue;
                again++;
                Dispatch(inst, op, immediate);
            }
            if (immediate.Count > 0) { var i = inst; Clock.Schedule(Clock.Now, () => Deliver(i, Stamp(immediate))); }
        }
        return new JsonObject { ["instances"] = results.Count, ["open"] = open, ["operationsAgain"] = again, ["facts"] = facts.Count };
    }

    void Command(JsonObject c)
    {
        switch (J.S(c, "op"))
        {
            case "start": StartPayment(c); break;
            case "ack":
                if (J.S(c, "rootId") is string r && reported.TryAdd(r, true))
                    history!.Append("reported", new JsonObject { ["rootId"] = r, ["paymentId"] = J.S(c, "paymentId"), ["outcome"] = J.S(c, "outcome") });
                break;
            case "verify": ServiceLines.Out("VERIFIED", history!.Verify()); break;
            default: throw new InvalidOperationException($"unknown command {J.S(c, "op")}");
        }
    }

    void StartPayment(JsonObject c)
    {
        var paymentId = J.S(c, "paymentId")!;
        var rootId = J.S(c, "rootId")!;
        if (served.ContainsKey(rootId)) { ServiceLines.Out("STARTED", new JsonObject { ["paymentId"] = paymentId, ["rootId"] = rootId, ["known"] = true }); return; }
        var workflow = J.S(c, "workflow") ?? J.S(built, "workflow")!;
        var ctx = (JsonObject)J.Clone(J.P(c, "context") ?? new JsonObject())!;
        serviceParty.Root(rootId, workflow);
        Schedule(party.Call(new JsonObject { ["op"] = "start", ["now"] = Clock.Now, ["conversation"] = J.S(J.P(ctx, "header"), "conversation") ?? paymentId, ["workflow"] = workflow }).GetAwaiter().GetResult());
        var doc = dep.Resolve(workflow, null, 0) ?? throw new InvalidOperationException($"{workflow} does not resolve");
        var docKey = $"{workflow}@{J.S(doc, "release")}";
        var (state, payload) = ClaimCheckStore.Split(ctx);
        var r = $"{rootId}#ctx";
        if (!history!.RestorePayloads(Store, rootId)) Store.Create(r, payload, ClaimCheckStore.AcceptFor(state, J.S(config, "tenant"), docKey));
        var inp = new JsonObject { ["kind"] = "workflow", ["doc"] = docKey, ["state"] = state, ["payloadRef"] = r };
        if (J.P(c, "legs") is JsonObject lg) inp["legs"] = J.Clone(lg);
        var s = new JsonObject { ["paymentId"] = paymentId, ["rootId"] = rootId, ["workflow"] = workflow, ["input"] = J.Clone(inp) };
        history.Append("start", s);
        served[rootId] = s;
        StartInstance(rootId, inp, null, 0);
        FollowLive(rootId, r, J.P(state, "header"), "elsa");
        ServiceLines.Out("STARTED", new JsonObject { ["paymentId"] = paymentId, ["rootId"] = rootId });
    }

    void Tell()
    {
        foreach (var (rootId, s) in served)
        {
            if (reported.ContainsKey(rootId) || told.ContainsKey(rootId)) continue;
            if (Find(rootId) is not { Completed: true, Output: JsonObject o }) continue;
            told[rootId] = true;
            var obs = new JsonObject
            {
                ["target"] = "elsa",
                ["instances"] = new JsonArray(InstanceObservation("root", o, dep.Start, [])),
                ["engine"] = new JsonObject
                {
                    ["distribution"] = new JsonObject { ["id"] = ClaimCheckStore.Distribution, ["definition"] = $"transacthub.interpreter@{ClaimCheckStore.Distribution}" },
                    ["service"] = new JsonObject
                    {
                        ["history"] = history!.Subject, ["instances"] = instances.Count, ["payments"] = served.Count, ["episodes"] = elsaRuns,
                        ["activityRuns"] = activityRuns, ["rebuilt"] = J.Clone(rebuilt), ["pid"] = Environment.ProcessId,
                    },
                },
            };
            ServiceLines.Out("ENDED", new JsonObject { ["paymentId"] = J.S(s, "paymentId"), ["rootId"] = rootId, ["observation"] = obs });
        }
    }
}
