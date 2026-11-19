using System.Text.Json.Nodes;
using TransactHub.Engines;

namespace ElsaHarness;

// The interpreter on Elsa (I-30). Elsa keeps a workflow instance's state, suspends it at a bookmark
// and resumes it with input; it does not replay code. So the one generic activity keeps a journal in
// the workflow's own state: the instance input, and every result that has reached it, in order. Each
// time Elsa resumes the activity, the interpreter runs again from the start against the journal. An
// engine operation whose result is in the journal returns it at once; one that is not becomes a
// pending operation for the host to carry out. This is the replay model of DTFx and Temporal, built on
// Elsa's state and bookmarks, and it is what makes a run the same however often it is resumed.

/// <summary>One engine operation of an episode. Ids are positional, so a replay gives the same ids.</summary>
public sealed class Op
{
    public int Id;
    public string Type = "";            // activity, timer, child, send
    public string? Name;                // activity name, or event name for send
    public JsonObject? Input;           // activity or child input, or event data
    public string? Target;              // child instance id, or the instance an event goes to
    public long At;                     // timer due time
    public bool Completed, Cancelled;
    public readonly TaskCompletionSource<JsonObject?> Tcs = new();
}

/// <summary>Runs posted continuations on the episode's own thread, in order, until none is left.</summary>
sealed class Pump : SynchronizationContext
{
    readonly Queue<(SendOrPostCallback cb, object? state)> q = new();
    public override void Post(SendOrPostCallback d, object? state) => q.Enqueue((d, state));
    public override void Send(SendOrPostCallback d, object? state) => d(state);
    public void Drain() { while (q.Count > 0) { var (cb, s) = q.Dequeue(); cb(s); } }
}

/// <summary>The interpreter's engine operations on the journal.</summary>
public sealed class ReplayEngine : IEngine
{
    public readonly List<Op> Ops = new();
    public long NowMs;
    public JsonObject? ContinueInput;

    public ReplayEngine(string instanceId, long startAt) { InstanceId = instanceId; NowMs = startAt; }

    public long Now => NowMs;
    public string InstanceId { get; }
    public bool Durable => true;

    Op New(string type) { var op = new Op { Id = Ops.Count, Type = type }; Ops.Add(op); return op; }

    public async Task<JsonObject> Activity(string name, JsonObject input)
    {
        var op = New("activity");
        op.Name = name;
        op.Input = input;
        return (await op.Tcs.Task)!;
    }

    public async Task Timer(long at, CancellationToken ct)
    {
        var op = New("timer");
        op.At = at;
        // A cancelled timer ends as cancelled, as a DTFx durable timer does; the host drops it.
        if (ct.IsCancellationRequested) { op.Cancelled = true; op.Tcs.TrySetCanceled(); }
        else ct.Register(() => { op.Cancelled = true; op.Tcs.TrySetCanceled(); });
        await op.Tcs.Task;
    }

    public async Task<JsonObject> Child(string instanceId, JsonObject input)
    {
        var op = New("child");
        op.Target = instanceId;
        op.Input = input;
        return (await op.Tcs.Task)!;
    }

    /// <summary>The retry rule of DTFx's RetryInterceptor, with each backoff a journalled timer.</summary>
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

    public void SendEvent(string instanceId, string name, JsonObject data)
    {
        var op = New("send");
        op.Target = instanceId;
        op.Name = name;
        op.Input = data;
    }

    public void ContinueAsNew(JsonObject input) => ContinueInput = input;
}

public static class Replay
{
    /// <summary>
    /// One episode: run the interpreter from the start against the journal, then record in the journal
    /// what is now pending (operations not yet sent to the host), whether the instance has ended and
    /// with what output, what it awaits, and which of its timers were cancelled.
    /// </summary>
    public static void Episode(JsonObject j, Deployment dep)
    {
        var id = J.S(j, "id")!;
        var prev = SynchronizationContext.Current;
        var pump = new Pump();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var eng = new ReplayEngine(id, (long)J.Num(j, "startAt")!.Value);
            var interp = new Interp(eng, dep);
            var run = interp.Run((JsonObject)J.Clone(J.P(j, "input"))!);
            pump.Drain();
            var n = 0;
            foreach (var e in J.A(j, "entries"))
            {
                n++;
                eng.NowMs = (long)J.Num(e, "at")!.Value;
                if (J.Num(e, "op") is double opd)
                {
                    var opId = (int)opd;
                    if (opId >= eng.Ops.Count) throw new InvalidOperationException($"{id}: journal entry {n} completes operation {opId}, which the replay did not reach: the run is not deterministic");
                    var op = eng.Ops[opId];
                    op.Completed = true;
                    op.Tcs.TrySetResult(J.P(e, "result") as JsonObject is JsonObject r ? (JsonObject)J.Clone(r)! : null);
                }
                else
                {
                    var data = (JsonObject)J.Clone(J.P(e, "data") ?? new JsonObject())!;
                    switch (J.S(e, "event"))
                    {
                        case "signal": interp.OnSignal(J.S(data, "signal")!); break;
                        case "message": interp.OnMessage(data); break;
                        case "chunk": interp.OnChunk(data); break;
                    }
                }
                pump.Drain();
            }

            var dispatched = new HashSet<int>(J.A(j, "dispatched").Select(x => (int)J.D(x)!.Value));
            var pending = eng.Ops.Where(o => !o.Completed && !o.Cancelled && !dispatched.Contains(o.Id)).ToList();
            foreach (var o in pending) dispatched.Add(o.Id);
            j["dispatched"] = new JsonArray(dispatched.OrderBy(x => x).Select(x => (JsonNode?)x).ToArray());
            j["pending"] = new JsonArray(pending.Select(o => (JsonNode?)new JsonObject
            {
                ["id"] = o.Id, ["type"] = o.Type, ["name"] = o.Name, ["input"] = J.Clone(o.Input), ["target"] = o.Target, ["at"] = o.At,
            }).ToArray());
            j["cancelled"] = new JsonArray(eng.Ops.Where(o => o.Cancelled).Select(o => (JsonNode?)o.Id).ToArray());
            j["awaiting"] = interp.AwaitingType;
            j["episodes"] = (int)(J.Num(j, "episodes") ?? 0) + 1;
            if (run.IsCompleted)
            {
                j["done"] = true;
                if (eng.ContinueInput is not null) j["continueAsNew"] = J.Clone(eng.ContinueInput);
                else if (run.IsFaulted)
                {
                    var o = interp.Output();
                    o["failed"] = run.Exception!.GetBaseException().Message;
                    o["outcome"] ??= "failed";
                    j["output"] = o;
                }
                else j["output"] = J.Clone(run.Result);
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prev);
        }
    }
}
