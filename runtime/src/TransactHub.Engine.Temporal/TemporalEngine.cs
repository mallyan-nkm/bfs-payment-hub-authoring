using System.Text.Json.Nodes;
using Temporalio.Workflows;
using TransactHub.Engines;

namespace TransactHub.Engine.Temporal;

// The Temporal binding (plan W3; spec Part 9). One generic workflow interprets the expanded DSL
// documents of a release, with the studio's own interpreter (studio/engines/shared). Each activity
// release is a Temporal activity type `name@release` on the task queue of its scope, so two releases
// run side by side and a payment calls only the code its release names. Timers, retries, children,
// signals and continue-as-new are Temporal's own; nothing outside the engine keeps time.

/// <summary>What every workflow input carries besides the interpreter's own: the virtual epoch.</summary>
public static class Envelope
{
    /// <summary>
    /// Workflows read Now as Temporal's time plus this offset (milliseconds), fixed in the input, so a run
    /// can start at a business date that is not today and stay deterministic on replay.
    /// </summary>
    public static JsonObject Wrap(JsonObject inner, long offset, string frontDoor, long now) =>
        new() { ["offset"] = offset, ["frontDoor"] = frontDoor, ["now"] = now, ["input"] = inner };
}

/// <summary>
/// Business time is logical: it moves on a timer firing, a party's declared latency and an arrival
/// (a message, a signal, a child's report), never on the time the engine takes to process. So a
/// one-hour wait is one hour on Temporal as on every engine, and the clock is the same on replay.
/// An attempt's timeout measures that attempt's own execution only (R-05-54): the engine keeps the deadline
/// itself (<see cref="IAttemptDeadlines"/>), from the party's declared time and, on a real server, from the
/// activity's own Start-To-Close; it never races a timer that the engine's processing or queueing could spend.
/// </summary>
public sealed class TemporalEngine : IEngine, IAttemptDeadlines
{
    readonly long offset;
    readonly string frontDoor;
    readonly Func<string, string> queueOf;
    long logical;
    int asked, acked;
    // Attempts in call order: when each is due (known once the party has answered) and whether its
    // result has been handed back. Results due at the same business time are handed back in call
    // order, as every engine does (parallel branches that end together keep their order).
    // Kept so that each check is constant time: a batch has thousands of attempts in flight, and Temporal
    // evaluates every waiting condition on every event.
    int calls;
    readonly SortedSet<int> unknown = new();
    readonly SortedSet<(long due, int seq)> pending = new();
    readonly Dictionary<int, long> dueAt = new();

    public TemporalEngine(long offset, string frontDoor, Func<string, string> queueOf, long start)
    {
        this.offset = offset;
        this.frontDoor = frontDoor;
        this.queueOf = queueOf;
        logical = start;
    }

    /// <summary>Temporal's own time on the business calendar: what timers are set against.</summary>
    long ServerNow => new DateTimeOffset(Workflow.UtcNow, TimeSpan.Zero).ToUnixTimeMilliseconds() + offset;
    public long Now => logical;
    /// <summary>Something arrived at a business time (a message, a signal): the clock is at least there.</summary>
    public void Arrived(long at) => logical = Math.Max(logical, at);
    /// <summary>The front door has routed every delivery this run handed it up to number n.</summary>
    public void Acked(int n) => acked = Math.Max(acked, n);

    async Task Until(long at, CancellationToken ct = default)
    {
        // A time already reached needs no timer: on Temporal even a zero-length timer is a server round trip.
        var wait = at - ServerNow;
        if (wait > 0) await Workflow.DelayAsync(TimeSpan.FromMilliseconds(wait), ct);
        else ct.ThrowIfCancellationRequested();
        logical = Math.Max(logical, at);
    }
    public string InstanceId => Workflow.Info.WorkflowId;
    public bool Durable => true;

    /// <summary>
    /// One attempt as one Temporal activity with no retry of its own: the interpreter applies the
    /// policy (R-05-16). What a party takes to answer, or never answering, is played here on
    /// Temporal's timers, so the attempt timeout the interpreter races against it sees it.
    /// </summary>
    public static readonly bool Debug = Environment.GetEnvironmentVariable("TH_DEBUG") == "1";
    /// <summary>Claim checks as local activities (default) or as regular ones (TH_LOCAL_CLAIMCHECK=0).</summary>
    public static readonly bool LocalClaimCheck = Environment.GetEnvironmentVariable("TH_LOCAL_CLAIMCHECK") != "0";
    void Log(string what) { if (Debug && !Workflow.Unsafe.IsReplaying) Console.Error.WriteLine($"[temporal] {Workflow.Info.WorkflowId} {what} logical={Duration.Iso(logical)} server={Duration.Iso(ServerNow)}"); }

    public async Task<JsonObject> Activity(string name, JsonObject input)
    {
        Log($"call {name} deadline={(J.Num(input, "deadline") is double dd ? Duration.Iso((long)dd) : "-")}");
        var seq = ++calls;
        unknown.Add(seq);
        try { return await Attempt(name, input, seq); }
        finally
        {
            unknown.Remove(seq);
            if (dueAt.Remove(seq, out var d)) pending.Remove((d, seq));
        }
    }

    /// <summary>Wait until no earlier call can be due before or with this one, then until this one is due.</summary>
    async Task Release(int seq, long due)
    {
        Known(seq, due);
        // Every earlier call has answered (so its business time is known) ...
        await Workflow.WaitConditionAsync(() => unknown.Count == 0 || unknown.Min > seq);
        await Until(due);
        // ... and nothing due before this one, or with it and called earlier, is still to be handed back.
        await Workflow.WaitConditionAsync(() => pending.Min == (due, seq));
    }

    void Known(int seq, long due)
    {
        unknown.Remove(seq);
        dueAt[seq] = due;
        pending.Add((due, seq));
    }

    /// <summary>
    /// Set by a host on a real Temporal server: an attempt's Start-To-Close is then its own budget (its attempt timeout, or what is
    /// left of its total timeout), counted by Temporal from when a worker starts it, so time in the queue is not counted. On the
    /// time-skipping test server it stays an hour: that server may skip time while an activity runs.
    /// </summary>
    public static bool RealServer;

    /// <summary>A late attempt: its result, if one comes, is abandoned (R-09-11); the interpreter reads it as a Timeout.</summary>
    JsonObject Late() => new() { ["kind"] = "error", ["name"] = "Timeout", ["retryable"] = true, ["message"] = "no answer within the attempt's own timeout", ["late"] = true, ["at"] = Now };

    async Task<JsonObject> Attempt(string name, JsonObject input, int seq)
    {
        var called = logical;
        long? deadline = J.Num(input, "deadline") is double dd ? (long)dd : null;
        string raw;
        try
        {
            // The claim check is the host's own data operation, short and on this worker: a local activity, with
            // no task-queue round trip. Every party attempt stays a full activity on its scope's task queue.
            raw = name == Interp.ClaimCheck && LocalClaimCheck
                ? await Workflow.ExecuteLocalActivityAsync<string>(name, new object?[] { J.Str(input) }, new LocalActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
                    RetryPolicy = new() { MaximumAttempts = 1 },
                })
                : await Workflow.ExecuteActivityAsync<string>(name, new object?[] { J.Str(input) }, new ActivityOptions
                {
                    TaskQueue = queueOf(name),
                    // The attempt's own execution (R-05-54); no Schedule-To-Close: time in the queue is the engine's, not the attempt's.
                    StartToCloseTimeout = RealServer && deadline is long budgetAt ? TimeSpan.FromMilliseconds(Math.Max(1000, budgetAt - called)) : TimeSpan.FromHours(1),
                    RetryPolicy = new() { MaximumAttempts = 1 },
                });
        }
        catch (Temporalio.Exceptions.ActivityFailureException e) when (deadline is long dl && e.InnerException is Temporalio.Exceptions.TimeoutFailureException)
        {
            // The attempt ran longer than it may: a port that does not answer in time.
            Log($"late {name}: its own execution passed {Duration.Iso(dl)}");
            await Release(seq, dl);
            return Late();
        }
        var res = (JsonObject)J.Parse(raw)!;
        if (J.P(res, "deliveries") is JsonArray d && d.Count > 0)
        {
            // What the party sent during this attempt is routed before the attempt ends, so a reply
            // that is due now reaches its run before the run goes on to await it (CK-AWAIT-04).
            var n = ++asked;
            await Workflow.GetExternalWorkflowHandle(frontDoor).SignalAsync(FrontDoorWorkflow.Deliver,
                new object?[] { J.Str(new JsonObject { ["deliveries"] = J.Clone(d), ["ackTo"] = Workflow.Info.WorkflowId, ["ack"] = n }) });
            await Workflow.WaitConditionAsync(() => acked >= n);
        }
        res.Remove("deliveries");
        var due = called + (long)(J.Num(res, "latency") ?? 0);
        res.Remove("latency");
        if (J.S(res, "kind") == "hang")
        {
            if (deadline is long dl)
            {
                await Release(seq, dl);
                return Late();
            }
            Known(seq, long.MaxValue);
            await Workflow.WaitConditionAsync(() => false);
        }
        // The party took longer than the attempt may (its declared time is the attempt's own execution): the attempt timed
        // out at its deadline, and the result that came is abandoned.
        if (deadline is long limit && due > limit)
        {
            await Release(seq, limit);
            return Late();
        }
        await Release(seq, due);
        res["at"] = Now;
        Log($"done {name} {J.S(res, "kind")} {J.S(res, "outcome")}");
        return res;
    }

    /// <summary>
    /// A business timer (an await's timeout, a wait, a deadline, a loop's interval, a retry's backoff) runs for its business
    /// length from the logical now (R-05-54, I-77), so the time a real server spends processing never uses it up.
    /// TH_TEMPORAL_TIMERS=server keeps the binding's behaviour before 22 November 2026, a timer set on Temporal's clock, for
    /// a comparison run only (docs/runtime/LOAD-TEST.md).
    /// </summary>
    public static readonly bool ServerTimers = Environment.GetEnvironmentVariable("TH_TEMPORAL_TIMERS") == "server";

    public async Task Timer(long at, CancellationToken ct)
    {
        Log($"timer {Duration.Iso(at)}");
        await Logical(at, ct);
    }

    async Task Logical(long at, CancellationToken ct = default)
    {
        if (ServerTimers || ServerNow <= logical) { await Until(at, ct); return; }
        var wait = at - logical;
        if (wait > 0) await Workflow.DelayAsync(TimeSpan.FromMilliseconds(wait), ct);
        else ct.ThrowIfCancellationRequested();
        logical = Math.Max(logical, at);
    }

    public async Task<JsonObject> Child(string instanceId, JsonObject input)
    {
        var raw = await Workflow.ExecuteChildWorkflowAsync<string>(InterpreterWorkflow.Name, new object?[] { J.Str(Envelope.Wrap(input, offset, frontDoor, logical)) },
            new ChildWorkflowOptions { Id = instanceId, TaskQueue = Workflow.Info.TaskQueue });
        var o = (JsonObject)J.Parse(raw)!;
        // The child's end is an arrival: the parent goes on from the business time the child ended at.
        if (J.Num(o, "endedAt") is double ended) Arrived((long)ended);
        return o;
    }

    /// <summary>The retry rule of RetryInterceptor (DTFx), on Temporal's timers.</summary>
    public async Task<T> WithRetry<T>(RetryPlan o, Func<Task<T>> call)
    {
        Exception? last = null;
        var first = Now;
        for (var n = 0; n < o.MaxNumberOfAttempts; n++)
        {
            try { return await call(); }
            catch (Exception e) when (e is not Cancelled) { last = e; }
            if (n + 1 == o.MaxNumberOfAttempts) break;
            var deadline = o.RetryTimeout == TimeSpan.MaxValue ? long.MaxValue : first + (long)o.RetryTimeout.TotalMilliseconds;
            if (!o.Handle(last) || Now >= deadline) break;
            await Logical(Now + (long)o.Delay(n).TotalMilliseconds);
        }
        throw last!;
    }

    // An event is sent at once, but the run does not end or continue as new until every event it sent has
    // gone: a batch generation reports its chunk to the parent, then continues as new.
    readonly List<Task> sent = new();
    public void SendEvent(string instanceId, string name, JsonObject data) =>
        sent.Add(Workflow.GetExternalWorkflowHandle(instanceId).SignalAsync(name, new object?[] { J.Str(data), logical }));

    public Task Flush() => sent.Count == 0 ? Task.CompletedTask : Workflow.WhenAllAsync(sent.ToArray());

    public void ContinueAsNew(JsonObject input) =>
        throw Workflow.CreateContinueAsNewException((InterpreterWorkflow wf) => wf.RunAsync(J.Str(Envelope.Wrap(input, offset, frontDoor, logical))));
}

/// <summary>The one generic workflow: it interprets the expanded document its input names, at its exact release.</summary>
[Workflow(Name)]
public sealed class InterpreterWorkflow
{
    public const string Name = "transacthub.interpreter";
    /// <summary>The largest output a run returns as it is; a larger one is kept by the host.</summary>
    public const int OutputInline = 256 * 1024;
    /// <summary>Which task queue serves an activity type; set by the host before its workers start, and never changed.</summary>
    public static Func<string, string> QueueOf = _ => Workflow.Info.TaskQueue;

    readonly Interp interp;
    readonly TemporalEngine engine;
    readonly JsonObject input;
    readonly string frontDoor;

    [WorkflowInit]
    public InterpreterWorkflow(string envelope)
    {
        var e = (JsonObject)J.Parse(envelope)!;
        input = (JsonObject)J.P(e, "input")!;
        frontDoor = J.S(e, "frontDoor")!;
        engine = new TemporalEngine((long)J.Num(e, "offset")!.Value, frontDoor, QueueOf, (long)J.Num(e, "now")!.Value);
        interp = new Interp(engine, Deployment.Current);
    }

    [WorkflowRun]
    public async Task<string> RunAsync(string envelope)
    {
        var fd = Workflow.GetExternalWorkflowHandle(frontDoor);
        await fd.SignalAsync(FrontDoorWorkflow.Register, new object?[] { J.Str(new JsonObject { ["id"] = Workflow.Info.WorkflowId, ["kind"] = J.S(input, "kind"), ["header"] = J.Clone(J.Get(input, "state.header")) }) });
        JsonObject o;
        try
        {
            o = await interp.Run(input);
        }
        catch (ContinueAsNewException)
        {
            await engine.Flush();
            throw;
        }
        catch (Exception e) when (e is not Temporalio.Exceptions.TemporalException || e is Temporalio.Exceptions.FailureException)
        {
            o = interp.Output();
            o["failed"] = e.Message;
            o["outcome"] ??= "failed";
        }
        await engine.Flush();
        await fd.SignalAsync(FrontDoorWorkflow.Completed, new object?[] { J.Str(new JsonObject { ["id"] = Workflow.Info.WorkflowId, ["consumed"] = J.Clone(J.P(o, "consumed")) }) });
        // A run's output (its trace and step records) grows with its items; Temporal refuses a payload over
        // about 2 MB. A large output is kept by the host and the run returns its reference (R-09-10).
        var text = J.Str(o);
        if (text.Length <= OutputInline) return text;
        // In pieces: each call is itself a payload under the limit. The key is the run's own id, so a retried
        // workflow task writes the same pieces again.
        var r = $"{Workflow.Info.WorkflowId}/{Workflow.Info.RunId}";
        const int Piece = 512 * 1024;
        for (int i = 0, n = 0; i < text.Length; i += Piece, n++)
            await Workflow.ExecuteLocalActivityAsync<string>(Host.KeepOutput, new object?[] { J.Str(new JsonObject { ["key"] = r, ["piece"] = n, ["text"] = text.Substring(i, Math.Min(Piece, text.Length - i)) }) },
                new LocalActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(5), RetryPolicy = new() { MaximumAttempts = 3 } });
        // The reference carries what a parent reads of a child (its outcome, reason, state, payload reference, times),
        // and leaves the per-step records, which grow with the items, with the host. Without them a leg of a large file
        // ended settled but its parent read no outcome, and failed it (found by the load harness, docs/runtime/LOAD-TEST.md).
        var small = (JsonObject)J.Clone(o)!;
        foreach (var big in new[] { "trace", "detail", "releaseTrace", "steps", "attempts", "ruleTraceSteps" }) small.Remove(big);
        small["outputRef"] = r;
        return J.Str(small);
    }

    // Each signal carries the business time it was sent at, so the receiver's clock moves to it.
    [WorkflowSignal("signal")]
    public Task SignalAsync(string data, long at)
    {
        engine.Arrived(at);
        interp.OnSignal(J.S((JsonObject)J.Parse(data)!, "signal")!);
        return Task.CompletedTask;
    }

    [WorkflowSignal("message")]
    public Task MessageAsync(string data, long at)
    {
        engine.Arrived(at);
        interp.OnMessage((JsonObject)J.Parse(data)!);
        return Task.CompletedTask;
    }

    [WorkflowSignal("ack")]
    public Task AckAsync(int n)
    {
        engine.Acked(n);
        return Task.CompletedTask;
    }

    [WorkflowSignal("chunk")]
    public Task ChunkAsync(string data, long at)
    {
        engine.Arrived(at);
        interp.OnChunk((JsonObject)J.Parse(data)!);
        return Task.CompletedTask;
    }

    [WorkflowQuery("status")]
    public string Status() => J.Str(new JsonObject { ["awaiting"] = interp.AwaitingType, ["ended"] = interp.Ended });
}
