using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DurableTask.Core;

using TransactHub.Engines;

namespace DtfxHarness;

/// <summary>
/// The harness as a distribution's one service on the runtime's engine host (Issue I-165, R-09-196, R-09-197): one in-memory
/// orchestration service and one worker run every payment the host starts, in real time, and every change the service makes is
/// first a fact on the distribution's history chain in the store. A service that starts again (its host stopped, mid-run or not)
/// folds that chain back into the service it was, takes back each payment that had not ended, and goes on: a message or a timer
/// due meanwhile reaches the instance that waits for it. The lines it exchanges with its host are <see cref="ServiceLines"/>.
/// </summary>
public sealed partial class Harness
{
    IEngineHistory history = null!;
    ServiceParty serviceParty = null!;
    /// <summary>The payments of the service by root id: the payment's id, its platform workflow and the root's input.</summary>
    readonly ConcurrentDictionary<string, JsonObject> served = new(StringComparer.Ordinal);
    /// <summary>Roots whose end the host has recorded (a <c>reported</c> fact), and roots whose end was told to the host.</summary>
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

    void Serve(JsonObject plans, string distribution)
    {
        history = EngineHistory.Open?.Invoke("dtfx", distribution) ?? throw new InvalidOperationException("a service keeps its history in the store: set TH_STORE_PROVIDER and TH_STORE_CONNECTION");
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        dep = new Deployment((JsonObject)J.P(input, "documents")!, config, start, (int)(J.Num(input, "chunkSize") ?? 100)) { ItemConcurrency = (int)(J.Num(input, "itemConcurrency") ?? 1) };
        Deployment.Current = dep;
        Clock = new VirtualClock(start) { RealTime = true };
        Service = new VirtualTimeOrchestrationService(Clock) { History = history };
        party = serviceParty = new ServiceParty(plans, distribution);

        // The service its history leaves: its instances, their messages, the activities to run again and the timers to fire.
        var facts = history.Load();
        var starts = new List<JsonObject>();
        foreach (var (code, d) in facts)
        {
            if (code == "start") starts.Add(d);
            else if (code == "reported" && J.S(d, "rootId") is string r) reported[r] = true;
        }
        rebuilt = Service.Rebuild(facts);
        foreach (var s in starts)
        {
            var rootId = J.S(s, "rootId")!;
            served[rootId] = s;
            serviceParty.Root(rootId, J.S(s, "workflow")!);
            if (reported.ContainsKey(rootId)) continue;
            // The payment's payload and its staged writes, from the claim check's own facts.
            history.RestorePayloads(Store, rootId);
        }
        history.Append(facts.Count == 0 ? "opened" : "restarted", new JsonObject
        {
            ["engine"] = "dtfx", ["distribution"] = distribution, ["orchestration"] = DtfxEngine.OrchestrationName, ["pid"] = Environment.ProcessId,
            ["rebuilt"] = J.Clone(rebuilt), ["payments"] = starts.Count, ["open"] = starts.Count(s => !reported.ContainsKey(J.S(s, "rootId")!)),
        });
        StartWorker().GetAwaiter().GetResult();
        foreach (var s in starts)
        {
            var rootId = J.S(s, "rootId")!;
            if (reported.ContainsKey(rootId)) continue;
            var rec = Service.Find(rootId);
            // Its start was recorded but the service stopped before the instance was: it is created now, as it would have been.
            if (rec is null) Create(rootId, (JsonObject)J.P(s, "input")!);
            if (Service.Find(rootId) is { Completed: false }) FollowLive(rootId, $"{rootId}#ctx", J.Get(J.P(s, "input"), "state.header"), "dtfx");
            ServiceLines.Out("RESUMED", new JsonObject { ["paymentId"] = J.S(s, "paymentId"), ["rootId"] = rootId, ["workflow"] = J.S(s, "workflow") });
        }

        ServiceLines.Read(Command);
        ServiceLines.Out("READY", new JsonObject { ["engine"] = "dtfx", ["distribution"] = distribution, ["history"] = history.Subject, ["rebuilt"] = J.Clone(rebuilt) });
        // In real time, for as long as the host runs: each payment that ends is told to the host.
        Clock.RunUntil(() => { Tell(); return false; }, long.MaxValue / 4, TimeSpan.FromHours(12));
    }

    void Command(JsonObject c)
    {
        switch (J.S(c, "op"))
        {
            case "start": StartPayment(c); break;
            case "ack":
                if (J.S(c, "rootId") is string r && reported.TryAdd(r, true))
                    history.Append("reported", new JsonObject { ["rootId"] = r, ["paymentId"] = J.S(c, "paymentId"), ["outcome"] = J.S(c, "outcome") });
                break;
            case "verify": ServiceLines.Out("VERIFIED", history.Verify()); break;
            default: throw new InvalidOperationException($"unknown command {J.S(c, "op")}");
        }
    }

    /// <summary>
    /// A payment the host starts: its payload into the claim check (accepted into the store), its start on the history, then its
    /// root instance. A start the service already holds (the host asked again) is answered as started.
    /// </summary>
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
        // A start asked again after the service stopped half-way: the payload the store holds is taken, not accepted twice.
        if (!history.RestorePayloads(Store, rootId)) Store.Create(r, payload, ClaimCheckStore.AcceptFor(state, J.S(config, "tenant"), docKey));
        var inp = new JsonObject { ["kind"] = "workflow", ["doc"] = docKey, ["state"] = state, ["payloadRef"] = r };
        if (J.P(c, "legs") is JsonObject lg) inp["legs"] = J.Clone(lg);
        // The engine's state only (references, header, facts, status), never the payload (R-09-10).
        var s = new JsonObject { ["paymentId"] = paymentId, ["rootId"] = rootId, ["workflow"] = workflow, ["input"] = J.Clone(inp) };
        history.Append("start", s);
        served[rootId] = s;
        Create(rootId, inp);
        FollowLive(rootId, r, J.P(state, "header"), "dtfx");
        ServiceLines.Out("STARTED", new JsonObject { ["paymentId"] = paymentId, ["rootId"] = rootId });
    }

    void Create(string rootId, JsonObject inp) =>
        client.CreateOrchestrationInstanceAsync(DtfxEngine.OrchestrationName, "", rootId, J.Str(inp)).GetAwaiter().GetResult();

    /// <summary>Each payment whose root has ended, told to the host once with its observation, until the host records it.</summary>
    void Tell()
    {
        foreach (var (rootId, s) in served)
        {
            if (reported.ContainsKey(rootId) || told.ContainsKey(rootId)) continue;
            var rec = Service.Find(rootId);
            if (rec is not { Completed: true }) continue;
            told[rootId] = true;
            var obs = new JsonObject
            {
                ["target"] = "dtfx",
                ["instances"] = new JsonArray(),
                ["engine"] = new JsonObject
                {
                    ["distribution"] = new JsonObject { ["id"] = ClaimCheckStore.Distribution, ["orchestration"] = DtfxEngine.OrchestrationName },
                    ["service"] = new JsonObject
                    {
                        ["history"] = history.Subject, ["instances"] = Service.Instances.Count, ["payments"] = served.Count, ["episodes"] = Service.Episodes,
                        ["activityRuns"] = Service.ActivityRuns, ["rebuilt"] = J.Clone(rebuilt), ["pid"] = Environment.ProcessId,
                    },
                },
            };
            if (rec.State?.Output is string o && Unwrap(o) is JsonObject oo) ((JsonArray)obs["instances"]!).Add(InstanceObservation("root", oo, dep.Start, []));
            ServiceLines.Out("ENDED", new JsonObject { ["paymentId"] = J.S(s, "paymentId"), ["rootId"] = rootId, ["observation"] = obs });
        }
    }
}
