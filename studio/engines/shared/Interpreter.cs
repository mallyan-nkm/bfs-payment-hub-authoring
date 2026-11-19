using System.Text.Json.Nodes;

namespace TransactHub.Engines;

// The DSL interpreter that runs inside the generic DTFx orchestration (C21-S1). It is a port of
// the studio interpreter (studio/src/interp/interpreter.ts) onto the engine operations in IEngine:
// activities, durable timers, sub-orchestrations and external events. It holds no state outside
// the orchestration: everything is rebuilt by replaying the history, so it must be deterministic.
// It never evaluates rules, reads values or calls ports inline (R-09-06): those run in activities.

public sealed class Cancelled : Exception
{
    public readonly CancelReason Reason;
    public Cancelled(CancelReason r) : base($"cancelled: {r.Kind}") { Reason = r; }
}

public sealed record CancelReason(string Kind, object? Data = null);

public sealed class EndFlow : Exception
{
    public readonly string Outcome;
    public EndFlow(string outcome) : base($"end {outcome}") { Outcome = outcome; }
}

public sealed class ReturnFlow : Exception
{
    public readonly string Outcome;
    public ReturnFlow(string outcome) : base($"return {outcome}") { Outcome = outcome; }
}

public sealed class StepFailure : Exception
{
    public readonly string Step, ErrorName;
    public StepFailure(string step, string message, string errorName = "Failure") : base(message) { Step = step; ErrorName = errorName; }
}

/// <summary>An attempt that failed: an error from the activity, or the attempt timer won the race.</summary>
public sealed class AttemptFailed : Exception
{
    public readonly string Name;
    public readonly bool Retryable;
    public AttemptFailed(string name, bool retryable, string message) : base(message) { Name = name; Retryable = retryable; }
}

/// <summary>A cancellation scope, as the studio's Scope.</summary>
public sealed class Scope
{
    public CancelReason? Reason;
    readonly TaskCompletionSource<bool> tcs = new();
    readonly List<Scope> children = new();
    readonly Scope? parent;

    public Scope(Scope? parent = null)
    {
        this.parent = parent;
        if (parent is not null)
        {
            parent.children.Add(this);
            if (parent.Reason is not null) { Reason = parent.Reason; tcs.TrySetResult(true); }
        }
    }

    public Scope Child() => new(this);
    public bool IsCancelled => Reason is not null;
    public Task CancelledTask => tcs.Task;

    public void Cancel(CancelReason r)
    {
        if (Reason is not null) return;
        Reason = r;
        foreach (var c in children.ToList()) c.Cancel(r);
        tcs.TrySetResult(true);
    }

    public void Dispose() => parent?.children.Remove(this);
    public void ThrowIfCancelled() { if (Reason is not null) throw new Cancelled(Reason); }
}

public sealed class TryFrame { public List<CompletedWrite> Writes = new(); }
public sealed class CompletedWrite { public string Step = "", Activity = "", Compensation = ""; }

public sealed class Frame
{
    public JsonObject State = new(); // the context held by the engine: header, facts, status, messages
    public string PayloadRef = ""; // the claim-check reference for the rest of the context
    public Scope Scope = new();
    public string HookOwner = "";
    public List<TryFrame> TryStack = new();
    public bool InFinally;
    public bool Detail;
    public int? Item;
    public Func<string, string>? Label;

    public Frame With(Action<Frame> change)
    {
        var f = (Frame)MemberwiseClone();
        change(f);
        return f;
    }
}

/// <summary>The operations the interpreter needs from an engine.</summary>
/// <summary>
/// A retry plan, engine-neutral: the first interval, the number of attempts, the backoff, the total
/// time allowed and which failures to retry. DTFx runs it as RetryOptions with its RetryInterceptor;
/// Elsa runs it as journalled timers. The defaults are DTFx's.
/// </summary>
public sealed class RetryPlan
{
    public RetryPlan(TimeSpan firstRetryInterval, int maxNumberOfAttempts) { FirstRetryInterval = firstRetryInterval; MaxNumberOfAttempts = maxNumberOfAttempts; }
    public TimeSpan FirstRetryInterval { get; }
    public int MaxNumberOfAttempts { get; }
    public double BackoffCoefficient { get; set; } = 1;
    public TimeSpan MaxRetryInterval { get; set; } = TimeSpan.MaxValue;
    public TimeSpan RetryTimeout { get; set; } = TimeSpan.MaxValue;
    public Func<Exception, bool> Handle { get; set; } = _ => true;

    /// <summary>The wait before retry number n (0 for the first retry), as RetryInterceptor computes it.</summary>
    public TimeSpan Delay(int n)
    {
        var ms = FirstRetryInterval.TotalMilliseconds * Math.Pow(BackoffCoefficient, n);
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxRetryInterval == TimeSpan.MaxValue ? double.MaxValue : MaxRetryInterval.TotalMilliseconds));
    }
}

public interface IEngine
{
    long Now { get; }
    string InstanceId { get; }
    bool Durable { get; }
    Task<JsonObject> Activity(string name, JsonObject input);
    Task Timer(long at, CancellationToken ct);
    Task<JsonObject> Child(string instanceId, JsonObject input);
    Task<T> WithRetry<T>(RetryPlan options, Func<Task<T>> call);
    void SendEvent(string instanceId, string name, JsonObject data);
    void ContinueAsNew(JsonObject input);
}

/// <summary>Effective policy (R-05-16): type default, then activity, then step, as policy.ts.</summary>
public sealed class Policy
{
    public (string? v, string src) AttemptTimeout, TotalTimeout;
    public (int v, string src) MaxAttempts;
    public (string v, string src) FirstInterval;
    public (double v, string src) Backoff;
    public (List<string>? v, bool all, string src) NonRetryable;

    static readonly Dictionary<string, (string? at, int max, string[]? nr)> TypeDefaults = new()
    {
        ["map"] = ("PT5S", 1, null), ["validate"] = ("PT5S", 1, null), ["decide"] = ("PT5S", 1, null),
        ["enrich"] = ("PT5S", 3, Array.Empty<string>()), ["reconcile"] = ("PT5S", 3, Array.Empty<string>()),
        ["screen"] = ("PT10S", 3, new[] { "BusinessReject" }), ["exchange"] = ("PT10S", 3, new[] { "BusinessReject" }),
        ["post"] = ("PT10S", 3, new[] { "BusinessReject" }), ["reserve"] = ("PT10S", 3, new[] { "BusinessReject" }),
        ["notify"] = ("PT5S", 5, Array.Empty<string>()), ["hold"] = (null, 1, null),
    };

    public static Policy Of(JsonObject act, JsonNode? stepPolicy)
    {
        var type = J.S(act, "type") ?? "";
        var known = TypeDefaults.TryGetValue(type, out var td);
        if (!known) td = ("PT5S", 1, null);
        var a = J.P(act, "policy");
        var s = stepPolicy;
        (string?, string) pickS(string key, string? tv, bool hasType = true)
        {
            if (J.Has(s, key)) return (J.S(s, key), "step");
            if (J.Has(a, key)) return (J.S(a, key), "activity");
            return (tv, hasType ? "type default" : "none");
        }
        JsonNode? retry(JsonNode? x, string k) => J.P(J.P(x, "retry"), k);
        var p = new Policy
        {
            AttemptTimeout = pickS("attemptTimeout", td.at),
            TotalTimeout = pickS("totalTimeout", null, false),
        };
        var smax = retry(s, "maxAttempts"); var amax = retry(a, "maxAttempts");
        p.MaxAttempts = smax is not null ? ((int)J.D(smax)!.Value, "step") : amax is not null ? ((int)J.D(amax)!.Value, "activity") : (td.max, "type default");
        var sfi = retry(s, "firstInterval"); var afi = retry(a, "firstInterval");
        p.FirstInterval = sfi is not null ? (sfi.GetValue<string>(), "step") : afi is not null ? (afi.GetValue<string>(), "activity") : ("PT1S", "type default");
        var sb = retry(s, "backoff"); var ab = retry(a, "backoff");
        p.Backoff = sb is not null ? (J.D(sb)!.Value, "step") : ab is not null ? (J.D(ab)!.Value, "activity") : (2, "type default");
        var snr = retry(s, "nonRetryable"); var anr = retry(a, "nonRetryable");
        if (snr is not null) p.NonRetryable = (J.Strings(snr), false, "step");
        else if (anr is not null) p.NonRetryable = (J.Strings(anr), false, "activity");
        else p.NonRetryable = (td.nr?.ToList(), td.nr is null, "type default");
        return p;
    }

    public JsonObject Values() => new()
    {
        ["attemptTimeout"] = AttemptTimeout.v, ["totalTimeout"] = TotalTimeout.v, ["maxAttempts"] = MaxAttempts.v,
        ["firstInterval"] = FirstInterval.v, ["backoff"] = Backoff.v,
        ["nonRetryable"] = NonRetryable.all ? JsonValue.Create("all") : J.Arr(NonRetryable.v ?? new()),
    };

    public JsonObject Sources() => new()
    {
        ["attemptTimeout"] = AttemptTimeout.src, ["totalTimeout"] = TotalTimeout.src, ["maxAttempts"] = MaxAttempts.src,
        ["firstInterval"] = FirstInterval.src, ["backoff"] = Backoff.src, ["nonRetryable"] = NonRetryable.src,
    };

    public long RetryInterval(int attempt) => (long)(Duration.Ms(FirstInterval.v) * Math.Pow(Backoff.v, attempt - 1));

    public bool IsRetryable(string errorName, JsonArray? declared)
    {
        if (NonRetryable.all) return false;
        if (NonRetryable.v!.Contains(errorName)) return false;
        var d = declared?.FirstOrDefault(e => J.S(e, "name") == errorName);
        if (d is not null) return J.Truthy(J.P(d, "retryable"));
        return errorName == "Timeout" || errorName == "TransientError";
    }
}

public sealed partial class Interp
{
    public const string ClaimCheck = "claim-check@1.0.0";
    static readonly string[] Signals = { "pause", "resume", "cancel", "re-screen" };
    static readonly string[] StateParts = { "header", "facts", "status", "messages" };

    readonly IEngine eng;
    readonly Deployment dep;
    JsonObject doc = new();
    string kind = "workflow";
    List<string> exitOutcomes = new();

    public List<string> Trace = new(), Detail = new(), ReleaseTrace = new(), RuleTraceSteps = new(), PendingTrace = new();
    public JsonArray Steps = new();
    public Dictionary<string, int> Attempts = new();
    readonly Dictionary<string, string> outcomes = new();
    JsonObject decisions = new();
    public List<CompletedWrite> CompletedWrites = new();
    Dictionary<string, string> pins = new();
    List<JsonObject> bindings = new();
    readonly HashSet<string> supersessionsApplied = new();
    readonly List<JsonObject> mailbox = new();
    public readonly HashSet<string> Consumed = new();
    readonly HashSet<string> expiredAwaits = new();
    (string type, string stepId, TaskCompletionSource<JsonObject> tcs)? awaiting;
    bool paused;
    readonly List<TaskCompletionSource<bool>> resumeWaiters = new();
    Scope bodyScope = new();
    readonly List<(JsonObject step, Frame frame)> screenSteps = new();
    Dictionary<string, string> legs = new();
    public readonly SortedSet<string> EngineStateKeys = new(StringComparer.Ordinal);
    Dictionary<string, int> callCounts = new();
    public int Duplicates, Unmatched, EarlyKept;
    readonly List<Task> background = new();
    public bool Ended;
    public string? Outcome, Failed, ReasonCode;
    public long StartedAt, EndedAt;
    public JsonObject State = new();
    public string PayloadRef = "";
    string partyKey = "";
    int childSeq;
    readonly Dictionary<int, JsonObject> chunkEvents = new();
    TaskCompletionSource<bool>? chunkWaiter;
    int chunkWant = -1;

    public Interp(IEngine eng, Deployment dep) { this.eng = eng; this.dep = dep; }

    public string? AwaitingType => awaiting?.type;

    long Elapsed => eng.Now - dep.Start;

    // =================================================================== entry

    public async Task<JsonObject> Run(JsonObject input)
    {
        kind = J.S(input, "kind") ?? "workflow";
        if (kind == "batch") return await RunBatch(input);
        var docKey = J.S(input, "doc")!;
        doc = dep.Doc(docKey) ?? throw new InvalidOperationException($"{docKey} is not deployed");
        exitOutcomes = J.Has(input, "exitOutcomes") ? J.Strings(J.P(input, "exitOutcomes")) : J.Strings(J.P(J.P(doc, "exit"), "outcomes"));
        State = (JsonObject)J.Clone(J.P(input, "state"))!;
        PayloadRef = J.S(input, "payloadRef")!;
        partyKey = J.S(input, "partyKey") ?? eng.InstanceId;
        if (J.P(input, "legs") is JsonObject lg) foreach (var kv in lg) legs[kv.Key] = J.AsKey(kv.Value)!;
        StartedAt = eng.Now;
        // Pins are taken when the instance starts (R-01-16); hook bindings likewise (R-06-13).
        foreach (var a in J.A(doc, "artefacts")) pins[J.S(a, "name")!] = J.S(a, "release")!;
        bindings = dep.BindingsAt(Duration.Today(eng.Now));
        ReleaseTrace.Add($"{ShortName(J.S(doc, "name")!)}@{J.S(doc, "release")}");
        await RunWorkflowBody(new Scope());
        // A notify hook does not delay the flow (R-06-09), but the instance completes with its result.
        await Task.WhenAll(background.ToList());
        return Output();
    }

    public JsonObject Output() => new()
    {
        ["outcome"] = Outcome, ["failed"] = Failed, ["reasonCode"] = ReasonCode,
        ["trace"] = J.Arr(Trace), ["detail"] = J.Arr(Detail), ["releaseTrace"] = J.Arr(ReleaseTrace),
        ["steps"] = J.Clone(Steps), ["attempts"] = new JsonObject(Attempts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["ruleTraceSteps"] = J.Arr(RuleTraceSteps), ["engineStateKeys"] = J.Arr(EngineStateKeys),
        ["startedAt"] = StartedAt, ["endedAt"] = EndedAt, ["state"] = J.Clone(State), ["payloadRef"] = PayloadRef,
        ["consumed"] = J.Arr(Consumed), ["duplicates"] = Duplicates, ["unmatched"] = Unmatched, ["earlyKept"] = EarlyKept,
    };

    async Task RunWorkflowBody(Scope outer)
    {
        var body = outer.Child();
        bodyScope = body;
        var frame = new Frame { State = State, PayloadRef = PayloadRef, Scope = body, HookOwner = J.S(doc, "name")!, Detail = J.S(doc, "wfKind") == "subflow" };
        string outcome;
        CancellationTokenSource? deadlineCts = null;
        var deadline = J.P(doc, "deadline");
        if (J.S(deadline, "after") is string after)
        {
            deadlineCts = new CancellationTokenSource();
            var at = Duration.Add(eng.Now, after);
            _ = WatchDeadline(at, deadlineCts.Token, body);
        }
        try
        {
            await ExecSteps(J.A(doc, "do"), frame);
            outcome = exitOutcomes.FirstOrDefault() ?? "done";
        }
        catch (Exception e)
        {
            outcome = await Settle(e, frame);
        }
        finally
        {
            deadlineCts?.Cancel();
        }
        // Finally runs on every end, after any compensation (R-05-14).
        var fin = J.A(doc, "finally");
        if (fin.Count > 0)
        {
            var fframe = frame.With(f => { f.Scope = outer.Child(); f.InFinally = true; f.Label = id => $"finally:{id}"; });
            try { await ExecSteps(fin, fframe); }
            catch (Exception) { /* a failing finally is recorded by the studio as an event only */ }
        }
        Outcome = outcome;
        Ended = true;
        EndedAt = eng.Now;
        Trace.Add($"end:{outcome}");
        ReleaseTrace.Add($"end:{outcome}");
    }

    /// <summary>The deadline as a durable timer raced with the body (Part 9: timer race).</summary>
    async Task WatchDeadline(long at, CancellationToken ct, Scope body)
    {
        try { await eng.Timer(at, ct); }
        catch (TaskCanceledException) { return; }
        catch (OperationCanceledException) { return; }
        if (!ct.IsCancellationRequested) body.Cancel(new CancelReason("deadline"));
    }

    async Task<string> Settle(Exception e, Frame frame)
    {
        if (e is EndFlow ef) return ef.Outcome;
        if (e is ReturnFlow rf) return rf.Outcome;
        if (e is Cancelled c)
        {
            var r = c.Reason;
            if (r.Kind == "end") return ((EndFlow)r.Data!).Outcome;
            if (r.Kind == "failure") return Fail((Exception)r.Data!);
            if (r.Kind == "deadline")
            {
                Trace.Add("deadline:fired");
                var dframe = frame.With(f => f.Scope = new Scope());
                try
                {
                    await ExecSteps(J.A(J.P(doc, "deadline"), "do"), dframe);
                    return exitOutcomes.FirstOrDefault() ?? "late";
                }
                catch (EndFlow x) { return x.Outcome; }
                catch (Exception x) { return Fail(x); }
            }
            if (r.Kind == "signal-cancel")
            {
                // Cancel runs compensation for completed steps in reverse order, then finally (R-05-13).
                await Compensate(CompletedWrites, frame.With(f => f.Scope = new Scope()));
                return "cancelled";
            }
            return "cancelled";
        }
        return Fail(e);
    }

    string Fail(Exception e)
    {
        Failed = e.Message;
        return "failed";
    }

    // =================================================================== signals and messages

    /// <summary>A signal arrives as an external event; it is seen at the next step boundary (R-05-12).</summary>
    public void OnSignal(string signal)
    {
        if (!Signals.Contains(signal) || Ended) return;
        if (signal == "pause")
        {
            paused = true;
            if (awaiting is { } aw) Trace.AddRange(new[] { $"{aw.stepId}:waiting", "signal:pause" });
            else PendingTrace.Add("signal:pause");
            return;
        }
        if (awaiting is { } a) Trace.Add($"{a.stepId}:waiting");
        Trace.AddRange(PendingTrace);
        PendingTrace.Clear();
        Trace.Add($"signal:{signal}");
        if (signal == "resume")
        {
            paused = false;
            var ws = resumeWaiters.ToList();
            resumeWaiters.Clear();
            foreach (var w in ws) w.TrySetResult(true);
        }
        else if (signal == "cancel") bodyScope.Cancel(new CancelReason("signal-cancel"));
        else if (signal == "re-screen") background.Add(Rescreen());
    }

    async Task Rescreen()
    {
        foreach (var (step, frame) in screenSteps.ToList())
        {
            try { await ExecCall(step, frame.With(f => f.Scope = bodyScope.Child())); }
            catch (EndFlow e) { bodyScope.Cancel(new CancelReason("end", e)); return; }
            catch (Cancelled) { }
            catch (Exception e) { bodyScope.Cancel(new CancelReason("failure", e)); }
        }
    }

    static string MessageKey(JsonObject m) => J.S(m, "inReplyTo") is string irt ? $"{J.S(m, "type")}:{irt}:{J.S(m, "status") ?? ""}" : J.S(m, "id")!;

    /// <summary>
    /// A message that the front door correlated to this instance (R-05-30 to R-05-32, R-09-16). A
    /// message that comes before its await is kept in orchestration state; a copy of a consumed
    /// message is recorded and ignored; one after its await timed out is unmatched.
    /// </summary>
    public void OnMessage(JsonObject m)
    {
        var id = MessageKey(m);
        var type = J.S(m, "type");
        if (Consumed.Contains(id) || mailbox.Any(x => MessageKey(x) == id)) { Duplicates++; return; }
        if (Ended) { Unmatched++; return; }
        if (awaiting is { } aw && aw.type == type)
        {
            awaiting = null;
            Consumed.Add(id);
            aw.tcs.TrySetResult(m);
            return;
        }
        if (type is not null && expiredAwaits.Contains(type)) { Unmatched++; return; }
        mailbox.Add(m);
        EarlyKept++;
    }

    JsonObject? TakeEarly(string type)
    {
        var m = mailbox.FirstOrDefault(x => J.S(x, "type") == type);
        if (m is not null) mailbox.Remove(m);
        return m;
    }

    public void OnChunk(JsonObject data)
    {
        chunkEvents[(int)J.Num(data, "gen")!.Value] = data;
        if (chunkWant >= 0 && chunkEvents.Count >= chunkWant) chunkWaiter?.TrySetResult(true);
    }

    async Task Gate(Scope scope)
    {
        if (PendingTrace.Count > 0) { Trace.AddRange(PendingTrace); PendingTrace.Clear(); }
        while (paused)
        {
            var tcs = new TaskCompletionSource<bool>();
            resumeWaiters.Add(tcs);
            await Race(tcs.Task, scope);
        }
        scope.ThrowIfCancelled();
    }

    static async Task Race(Task t, Scope s)
    {
        s.ThrowIfCancelled();
        var w = await Task.WhenAny(t, s.CancelledTask);
        if (w != t) throw new Cancelled(s.Reason!);
        await t;
    }

    static async Task<T> Race<T>(Task<T> t, Scope s)
    {
        s.ThrowIfCancelled();
        var w = await Task.WhenAny(t, s.CancelledTask);
        if (w != t) throw new Cancelled(s.Reason!);
        return await t;
    }

    async Task Sleep(long at, Scope s)
    {
        s.ThrowIfCancelled();
        var cts = new CancellationTokenSource();
        var timer = eng.Timer(at, cts.Token);
        var w = await Task.WhenAny(timer, s.CancelledTask);
        if (w != timer) { cts.Cancel(); throw new Cancelled(s.Reason!); }
    }

    // =================================================================== resolution

    public static string ShortName(string name)
    {
        var p = name.Split('.');
        return p.Length > 2 ? p[^2] : name;
    }

    (JsonObject doc, string release, string pinned, string? superseded) ResolveFor(string name, string stepId)
    {
        if (!pins.TryGetValue(name, out var pinned))
        {
            var d = dep.Resolve(name, null, Elapsed) ?? throw new StepFailure(stepId, $"{name} does not resolve to a published release (L03)");
            pinned = J.S(d, "release")!;
            pins[name] = pinned;
        }
        var release = pinned;
        string? superseded = null;
        foreach (var s in J.A(dep.Config, "supersessions"))
        {
            if (J.S(s, "artefact") != name || J.S(s, "replaces") != pinned) continue;
            if (J.S(s, "effectiveAt") is string ea && Elapsed < Duration.Ms(ea)) continue;
            release = J.S(s, "with")!;
            superseded = release;
            if (supersessionsApplied.Add($"{name}|{stepId}")) ReleaseTrace.Add($"supersede:{ShortName(name)} {pinned} to {release}");
        }
        var doc = dep.Resolve(name, release, Elapsed) ?? throw new StepFailure(stepId, $"{name}@{release} does not resolve");
        return (doc, release, pinned, superseded);
    }

    public static string PrimaryOutcome(JsonObject act)
    {
        var outs = J.A(act, "outcomes");
        var p = outs.FirstOrDefault(o => J.Truthy(J.P(o, "primary")));
        return p is not null ? J.S(p, "name")! : outs.Count > 0 ? J.S(outs[0], "name")! : "";
    }

    // =================================================================== steps

    async Task ExecSteps(JsonArray steps, Frame frame)
    {
        foreach (var st in steps.ToList())
        {
            await Gate(frame.Scope);
            await ExecStep((JsonObject)st!, frame);
        }
    }

    static string Label(Frame f, string id) => f.Label is not null ? f.Label(id) : f.Item is int i ? $"{id}[{i}]" : id;

    static bool OnlyKeys(JsonObject st, params string[] keys) => st.All(kv => keys.Contains(kv.Key));

    async Task ExecStep(JsonObject st, Frame frame)
    {
        if (st.ContainsKey("call")) { await ExecCall(st, frame); return; }
        if (st.ContainsKey("end") && OnlyKeys(st, "end", "compensate", "id"))
        {
            if (IsCompensateAll(J.P(st, "compensate"))) await CompensateScope(frame);
            throw new EndFlow(J.S(st, "end")!);
        }
        if (st.ContainsKey("switch")) { await ExecSwitch(st, frame); return; }
        if (st.ContainsKey("parallel")) { await ExecParallel(st, frame); return; }
        if (st.ContainsKey("forEach")) { await ExecForEach(st, frame); return; }
        if (st.ContainsKey("loop")) { await ExecLoop(st, frame); return; }
        if (st.ContainsKey("await")) { await ExecAwait(st, frame); return; }
        if (st.ContainsKey("wait")) { await ExecWait(st, frame); return; }
        if (st.ContainsKey("slot")) { await ExecSlot(st, frame); return; }
        if (st.ContainsKey("use")) { await ExecUse(st, frame); return; }
        if (st.ContainsKey("try")) { await ExecTry(st, frame); return; }
        if (st.ContainsKey("hook")) { await ExecHook(st, frame); return; }
        if (st.ContainsKey("block")) { await ExecBlock(st, frame); return; }
        if (st.ContainsKey("return") && OnlyKeys(st, "return", "id")) throw new ReturnFlow(J.S(st, "return")!);
        throw new StepFailure(J.S(st, "id") ?? "?", $"Unknown step {string.Join(",", st.Select(kv => kv.Key))}");
    }

    static bool IsCompensateAll(JsonNode? c) => c is not null && (J.AsKey(c) == "all" || J.AsKey(c) == "true");

    async Task RunAction(JsonNode? action, Frame frame)
    {
        if (action is JsonArray arr) { await ExecSteps(arr, frame); return; }
        if (action is JsonObject o)
        {
            if (o.ContainsKey("end"))
            {
                if (IsCompensateAll(J.P(o, "compensate"))) await CompensateScope(frame);
                throw new EndFlow(J.S(o, "end")!);
            }
            if (o.ContainsKey("return")) throw new ReturnFlow(J.S(o, "return")!);
        }
    }

    void Record(Frame frame, string entry) => (frame.Detail ? Detail : Trace).Add(entry);

    /// <summary>A composite step in the expanded form: a frame in the interpreter, no engine construct (Part 9).</summary>
    async Task ExecBlock(JsonObject st, Frame frame)
    {
        var id = J.S(st, "id")!;
        var blk = J.P(st, "block");
        var act = ResolveFor(J.S(blk, "of")!, id).doc;
        if (J.S(blk, "by") is string by) ResolveFor(by, id);
        var outcome = PrimaryOutcome(act);
        try { await ExecSteps(J.A(blk, "do"), frame.With(f => f.Detail = true)); }
        catch (ReturnFlow r) { outcome = r.Outcome; }
        outcomes[id] = outcome;
        Record(frame, frame.InFinally ? $"finally:{id}" : $"{Label(frame, id)}:{outcome}");
        if (outcome == PrimaryOutcome(act)) return;
        var handle = J.P(st, "handle");
        if (!J.Has(handle, outcome)) throw new StepFailure(id, $"Outcome {outcome} of {id} is not handled (L05)");
        await RunAction(J.P(handle, outcome), frame);
    }

    async Task CompensateScope(Frame frame)
    {
        var top = frame.TryStack.LastOrDefault();
        await Compensate(top is not null ? top.Writes : CompletedWrites, frame);
    }

    /// <summary>Run the compensation of completed write steps in reverse order (R-05-15).</summary>
    async Task Compensate(List<CompletedWrite> writes, Frame frame)
    {
        foreach (var w in writes.ToList().AsEnumerable().Reverse())
        {
            var done = CompletedWrites.IndexOf(w);
            if (done < 0) continue;
            CompletedWrites.RemoveAt(done);
            foreach (var t in frame.TryStack) t.Writes.Remove(w);
            Trace.Add($"undo:{w.Step}");
            try { await RunActivityStep(new JsonObject { ["id"] = $"undo-{w.Step}", ["call"] = w.Compensation }, frame.With(f => f.Scope = new Scope()), silent: true); }
            catch (Exception) { /* a failed compensation is an event in the studio */ }
        }
    }

    // ------------------------------------------------------------------ call

    async Task ExecCall(JsonObject st, Frame frame)
    {
        var id = J.S(st, "id") ?? ShortName(J.S(st, "call")!);
        var (outcome, act) = await RunActivityStep(st, frame);
        var prim = PrimaryOutcome(act);
        outcomes[id] = outcome;
        if (J.S(act, "type") == "screen" && !frame.InFinally && !screenSteps.Any(s => ReferenceEquals(s.step, st))) screenSteps.Add((st, frame));
        if (outcome == prim)
        {
            var eff = J.S(act, "effect");
            if (J.S(act, "compensation") is string comp && (eff == "write" || eff == "external"))
            {
                var w = new CompletedWrite { Step = id, Activity = J.S(act, "name")!, Compensation = comp };
                CompletedWrites.Add(w);
                foreach (var t in frame.TryStack) t.Writes.Add(w);
            }
            return;
        }
        var handle = J.P(st, "handle");
        if (!J.Has(handle, outcome)) throw new StepFailure(id, $"Outcome {outcome} of {id} is not handled (L05)");
        await RunAction(J.P(handle, outcome), frame);
    }

    static bool IsStatePath(string p) => StateParts.Contains(p.Split('.')[0].Replace("[]", ""));

    async Task<(string outcome, JsonObject act)> RunActivityStep(JsonObject st, Frame frame, bool silent = false)
    {
        var call = J.S(st, "call")!;
        var id = J.S(st, "id") ?? ShortName(call);
        var resolved = ResolveFor(call, id);
        var act = resolved.doc;
        if (J.S(act, "kind") != "activity") throw new StepFailure(id, $"{call} is not an activity");
        var policy = Policy.Of(act, J.P(st, "policy"));
        var rec = new JsonObject
        {
            ["step"] = Label(frame, id), ["activity"] = J.S(act, "name"), ["release"] = resolved.release, ["pinned"] = resolved.pinned,
            ["supersededBy"] = resolved.superseded, ["attempts"] = 0, ["policy"] = policy.Values(), ["policySources"] = policy.Sources(),
            ["delta"] = new JsonArray(), ["startedAt"] = Elapsed, ["item"] = frame.Item,
        };
        Steps.Add(rec);
        if (!silent) ReleaseTrace.Add($"{ShortName(J.S(act, "name")!)}@{resolved.release}");
        var level = J.S(st, "level") ?? J.S(act, "level");
        var (result, attempts) = await AttemptLoop(act, st, policy, frame, id, level, resolved.release);
        rec["attempts"] = attempts;
        Attempts[id] = Attempts.GetValueOrDefault(id) + attempts;
        var outcome = J.S(result, "outcome")!;
        // Writes: the engine keeps references (header, facts, status, messages); the rest is
        // committed to the claim-check store, and only an accepted result is committed (R-09-10, R-09-11).
        var delta = new JsonArray();
        if (J.P(result, "stateWrites") is JsonObject sw)
            foreach (var (p, v) in sw.ToList())
            {
                if (frame.Item is null) J.Set(frame.State, p, J.Clone(v));
                delta.Add(new JsonObject { ["path"] = frame.Item is int ix ? $"items[{ix}].{p}" : p });
            }
        foreach (var p in J.Strings(J.P(result, "stagedPaths"))) delta.Add(new JsonObject { ["path"] = p });
        if (J.S(result, "token") is string token) await eng.Activity(ClaimCheck, new JsonObject { ["op"] = "commit", ["token"] = token });
        rec["delta"] = delta;
        rec["outcome"] = outcome;
        rec["endedAt"] = Elapsed;
        if (J.P(result, "decisions") is JsonObject ds) foreach (var (k, v) in ds.ToList()) decisions[k] = J.Clone(v);
        if (J.Truthy(J.P(result, "ruleTrace"))) RuleTraceSteps.Add(Label(frame, id));
        if (J.S(result, "reasonCode") is string rc) ReasonCode = rc;
        foreach (var kv in frame.State) EngineStateKeys.Add(kv.Key);
        if (!silent) Record(frame, frame.InFinally ? $"finally:{id}" : $"{Label(frame, id)}:{outcome}");
        return (outcome, act);
    }

    /// <summary>
    /// Attempts and retries (C21-S2). Retry is RetryOptions run by the framework's RetryInterceptor
    /// (what ScheduleWithRetry uses); totalTimeout is RetryOptions.RetryTimeout; attemptTimeout is a
    /// durable timer raced against the task, cancelled when the task wins. When the timer wins the
    /// task is not cancelled: its late result is abandoned (R-09-11).
    /// </summary>
    async Task<(JsonObject result, int attempts)> AttemptLoop(JsonObject act, JsonObject st, Policy pol, Frame frame, string id, string? level, string release)
    {
        var startAt = eng.Now;
        var total = pol.TotalTimeout.v is string tt ? Duration.Add(startAt, tt) : long.MaxValue;
        var max = Math.Max(1, pol.MaxAttempts.v);
        var callNo = callCounts.GetValueOrDefault(id) + 1;
        callCounts[id] = callNo;
        var attempt = 0;
        var options = new RetryPlan(TimeSpan.FromMilliseconds(Duration.Ms(pol.FirstInterval.v)), max)
        {
            BackoffCoefficient = pol.Backoff.v,
            RetryTimeout = total == long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(total - startAt),
            // Retry only what the policy calls retryable, and not when the wait would reach the total timeout.
            Handle = ex => ex is AttemptFailed af && af.Retryable && eng.Now + pol.RetryInterval(attempt) < total,
        };
        var errors = J.P(act, "errors") as JsonArray;
        try
        {
            var r = await eng.WithRetry(options, async () =>
            {
                frame.Scope.ThrowIfCancelled();
                attempt++;
                long? attemptMs = pol.AttemptTimeout.v is string at ? Duration.Ms(at) : null;
                var limitAt = Math.Min(attemptMs is long a ? eng.Now + a : long.MaxValue, total);
                var input = new JsonObject
                {
                    ["instance"] = partyKey, ["activity"] = J.S(act, "name"), ["release"] = release, ["step"] = id, ["call"] = callNo,
                    ["attempt"] = attempt, ["now"] = eng.Now, ["state"] = J.Clone(frame.State), ["payloadRef"] = frame.PayloadRef,
                    ["item"] = frame.Item, ["level"] = level, ["policy"] = J.Clone(J.P(st, "policy")),
                    ["deadline"] = limitAt == long.MaxValue ? null : limitAt,
                };
                var task = eng.Activity($"{J.S(act, "name")}@{release}", input);
                var cts = new CancellationTokenSource();
                var waits = new List<Task> { task, frame.Scope.CancelledTask };
                Task? timer = null;
                if (limitAt != long.MaxValue) { timer = eng.Timer(limitAt, cts.Token); waits.Add(timer); }
                var w = await Task.WhenAny(waits);
                if (w == task)
                {
                    cts.Cancel(); // the task won: cancel the timer
                    var res = task.Result;
                    if (J.S(res, "kind") == "error")
                    {
                        var name = J.S(res, "name") ?? "Error";
                        var retryable = J.Truthy(J.P(res, "retryable")) && pol.IsRetryable(name, errors);
                        throw new AttemptFailed(name, retryable, J.S(res, "message") ?? name);
                    }
                    return res;
                }
                cts.Cancel();
                if (w == timer) throw new AttemptFailed("Timeout", pol.IsRetryable("Timeout", errors), $"attempt {attempt} of {id} timed out; a late result is ignored");
                throw new Cancelled(frame.Scope.Reason!);
            });
            return (r, attempt);
        }
        catch (AttemptFailed e)
        {
            if (!e.Retryable) throw new StepFailure(id, $"{J.S(act, "name")} failed: {e.Message}", e.Name);
            // Retries are spent or the total timeout is reached: the unknown outcome, where declared.
            if (J.A(act, "outcomes").Any(o => J.S(o, "name") == "unknown"))
                return (new JsonObject { ["kind"] = "result", ["outcome"] = "unknown" }, attempt);
            throw new StepFailure(id, $"{J.S(act, "name")} failed after retries: {e.Message}", "RetriesExhausted");
        }
    }

    // ------------------------------------------------------------------ switch, parallel, forEach, loop

    async Task ExecSwitch(JsonObject st, Frame frame)
    {
        var sw = J.P(st, "switch");
        var by = J.P(sw, "by");
        string? key = null;
        if (J.S(by, "outcomeOf") is string of) key = outcomes.TryGetValue(of, out var o) ? o : null;
        else if (J.S(by, "decision") is string dn) key = J.AsKey(J.P(decisions, dn));
        var cases = J.P(sw, "cases");
        var steps = key is not null && J.Has(cases, key) ? J.P(cases, key) : J.P(sw, "default");
        if (steps is JsonArray arr) await ExecSteps(arr, frame);
    }

    /// <summary>Parallel: Task.WhenAll over the branches (Part 9).</summary>
    async Task ExecParallel(JsonObject st, Frame frame)
    {
        var par = J.P(st, "parallel");
        var ps = frame.Scope.Child();
        Exception? firstError = null;
        await Task.WhenAll(J.A(par, "branches").ToList().Select(async b =>
        {
            try { await ExecSteps((JsonArray)b!, frame.With(f => f.Scope = ps.Child())); }
            catch (Exception e)
            {
                if (firstError is null)
                {
                    if (e is not Cancelled || !ps.IsCancelled) { firstError = e; ps.Cancel(new CancelReason("sibling")); }
                    else if (frame.Scope.IsCancelled) firstError = e;
                }
            }
        }));
        ps.Dispose();
        if (firstError is not null) throw firstError;
        frame.Scope.ThrowIfCancelled();
        Record(frame, $"join:{J.S(par, "join") ?? "all"}");
    }

    async Task ExecForEach(JsonObject st, Frame frame)
    {
        var fe = J.P(st, "forEach");
        var n = (int)J.Num(await eng.Activity(ClaimCheck, new JsonObject { ["op"] = "count", ["ref"] = frame.PayloadRef, ["path"] = J.S(fe, "in") }), "count")!.Value;
        var concurrency = Math.Max(1, (int)(J.Num(fe, "concurrency") ?? 1));
        var onFail = J.S(fe, "onItemFailure") ?? "stop";
        // R-09-13: a large batch is chunked; each chunk runs in a batch orchestration that continues as new.
        if (eng.Durable && n > dep.ChunkSize) { await RunBatches(st, frame, n, concurrency, onFail); return; }
        await RunItems(st, frame, 0, n, concurrency, onFail);
    }

    async Task RunItems(JsonObject st, Frame frame, int from, int to, int concurrency, string onFail)
    {
        var fe = J.P(st, "forEach");
        var next = from;
        Exception? stopError = null;
        async Task Worker()
        {
            while (next < to && stopError is null)
            {
                var index = next++;
                var iframe = frame.With(f => f.Item = index);
                try { await ExecSteps(J.A(fe, "do"), iframe); }
                catch (EndFlow) when (onFail == "continue") { }
                catch (Exception e) { stopError = e; }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrency, Math.Max(1, to - from))).Select(_ => Worker()));
        if (stopError is not null) throw stopError;
    }

    async Task RunBatches(JsonObject st, Frame frame, int n, int concurrency, string onFail)
    {
        var id = $"{eng.InstanceId}/batch{++childSeq}";
        chunkEvents.Clear();
        var input = new JsonObject
        {
            ["kind"] = "batch", ["doc"] = $"{J.S(doc, "name")}@{J.S(doc, "release")}", ["step"] = J.Clone(st), ["from"] = 0, ["n"] = n,
            ["chunk"] = dep.ChunkSize, ["concurrency"] = concurrency, ["onItemFailure"] = onFail, ["state"] = J.Clone(frame.State),
            ["payloadRef"] = frame.PayloadRef, ["partyKey"] = partyKey, ["parent"] = eng.InstanceId, ["gen"] = 0,
            ["hookOwner"] = frame.HookOwner, ["detail"] = frame.Detail,
            ["callCounts"] = new JsonObject(callCounts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["pins"] = new JsonObject(pins.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["legs"] = new JsonObject(legs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        };
        var result = await Race(eng.Child(id, input), frame.Scope);
        var chunks = (int)J.Num(result, "chunks")!.Value;
        if (chunkEvents.Count < chunks)
        {
            chunkWant = chunks;
            chunkWaiter = new TaskCompletionSource<bool>();
            await Race(chunkWaiter.Task, frame.Scope);
            chunkWant = -1;
        }
        for (var g = 0; g < chunks; g++)
        {
            var c = chunkEvents[g];
            foreach (var t in J.Strings(J.P(c, "trace"))) Trace.Add(t);
            foreach (var t in J.Strings(J.P(c, "detail"))) Detail.Add(t);
            foreach (var t in J.Strings(J.P(c, "releaseTrace"))) ReleaseTrace.Add(t);
            foreach (var t in J.Strings(J.P(c, "ruleTraceSteps"))) RuleTraceSteps.Add(t);
            foreach (var t in J.Strings(J.P(c, "engineStateKeys"))) EngineStateKeys.Add(t);
            foreach (var s in J.A(c, "steps")) Steps.Add(J.Clone(s));
            if (J.P(c, "attempts") is JsonObject at) foreach (var (k, v) in at) Attempts[k] = Attempts.GetValueOrDefault(k) + (int)J.D(v)!.Value;
            if (J.P(c, "callCounts") is JsonObject cc) foreach (var (k, v) in cc) callCounts[k] = (int)J.D(v)!.Value;
            if (J.P(c, "decisions") is JsonObject ds) foreach (var (k, v) in ds) decisions[k] = J.Clone(v);
            foreach (var w in J.A(c, "completedWrites"))
            {
                var cw = new CompletedWrite { Step = J.S(w, "step")!, Activity = J.S(w, "activity")!, Compensation = J.S(w, "compensation")! };
                CompletedWrites.Add(cw);
                foreach (var t in frame.TryStack) t.Writes.Add(cw);
            }
        }
        var stop = J.P(result, "stop");
        if (stop is not null)
        {
            if (J.S(stop, "kind") == "end") throw new EndFlow(J.S(stop, "outcome")!);
            throw new StepFailure(J.S(stop, "step") ?? "forEach", J.S(stop, "message") ?? "an item failed");
        }
    }

    /// <summary>One generation of a batch orchestration: one chunk of items, then continue as new.</summary>
    async Task<JsonObject> RunBatch(JsonObject input)
    {
        var docKey = J.S(input, "doc")!;
        doc = dep.Doc(docKey)!;
        State = (JsonObject)J.Clone(J.P(input, "state"))!;
        PayloadRef = J.S(input, "payloadRef")!;
        partyKey = J.S(input, "partyKey")!;
        foreach (var (k, v) in J.Obj(J.P(input, "callCounts"))) callCounts[k] = (int)J.D(v)!.Value;
        foreach (var (k, v) in J.Obj(J.P(input, "pins"))) pins[k] = J.AsKey(v)!;
        foreach (var (k, v) in J.Obj(J.P(input, "legs"))) legs[k] = J.AsKey(v)!;
        bindings = dep.BindingsAt(Duration.Today(eng.Now));
        var st = (JsonObject)J.P(input, "step")!;
        var from = (int)J.Num(input, "from")!.Value;
        var n = (int)J.Num(input, "n")!.Value;
        var chunk = (int)J.Num(input, "chunk")!.Value;
        var gen = (int)J.Num(input, "gen")!.Value;
        var to = Math.Min(n, from + chunk);
        var frame = new Frame { State = State, PayloadRef = PayloadRef, Scope = new Scope(), HookOwner = J.S(input, "hookOwner")!, Detail = J.Truthy(J.P(input, "detail")) };
        bodyScope = frame.Scope;
        JsonObject? stop = null;
        try { await RunItems(st, frame, from, to, (int)J.Num(input, "concurrency")!.Value, J.S(input, "onItemFailure")!); }
        catch (EndFlow e) { stop = new JsonObject { ["kind"] = "end", ["outcome"] = e.Outcome }; }
        catch (StepFailure e) { stop = new JsonObject { ["kind"] = "fail", ["step"] = e.Step, ["message"] = e.Message }; }
        catch (Exception e) { stop = new JsonObject { ["kind"] = "fail", ["message"] = e.Message }; }
        var records = new JsonObject
        {
            ["gen"] = gen, ["trace"] = J.Arr(Trace), ["detail"] = J.Arr(Detail), ["releaseTrace"] = J.Arr(ReleaseTrace),
            ["ruleTraceSteps"] = J.Arr(RuleTraceSteps), ["engineStateKeys"] = J.Arr(EngineStateKeys), ["steps"] = J.Clone(Steps),
            ["attempts"] = new JsonObject(Attempts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["callCounts"] = new JsonObject(callCounts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["decisions"] = J.Clone(decisions),
            ["completedWrites"] = new JsonArray(CompletedWrites.Select(w => (JsonNode?)new JsonObject { ["step"] = w.Step, ["activity"] = w.Activity, ["compensation"] = w.Compensation }).ToArray()),
        };
        eng.SendEvent(J.S(input, "parent")!, "chunk", records);
        if (stop is not null) return new JsonObject { ["chunks"] = gen + 1, ["stop"] = stop };
        if (to < n)
        {
            var nextInput = (JsonObject)J.Clone(input)!;
            nextInput["from"] = to;
            nextInput["gen"] = gen + 1;
            nextInput["callCounts"] = J.Clone(records["callCounts"]);
            eng.ContinueAsNew(nextInput);
            return new JsonObject { ["continued"] = true };
        }
        return new JsonObject { ["chunks"] = gen + 1 };
    }

    async Task ExecLoop(JsonObject st, Frame frame)
    {
        var lp = J.P(st, "loop");
        var max = (int)(J.Num(lp, "max") ?? 1);
        var until = J.P(lp, "until");
        for (var i = 0; i < max; i++)
        {
            await ExecSteps(J.A(lp, "do"), frame);
            if (J.S(until, "outcomeOf") is string of)
            {
                if (outcomes.TryGetValue(of, out var o) && J.Strings(J.P(until, "in")).Contains(o)) return;
            }
            if (i < max - 1 && J.S(lp, "every") is string every) await Sleep(eng.Now + Duration.Ms(every), frame.Scope);
        }
    }

    // ------------------------------------------------------------------ await, wait

    /// <summary>An await is an external event raced against a durable timer (C21-S2).</summary>
    async Task ExecAwait(JsonObject st, Frame frame)
    {
        var aw = J.P(st, "await");
        var id = J.S(st, "id") ?? "await";
        var type = J.S(aw, "message")!;
        JsonObject? msg;
        var early = TakeEarly(type);
        if (early is not null)
        {
            Consumed.Add(MessageKey(early));
            msg = early;
        }
        else
        {
            frame.Scope.ThrowIfCancelled();
            var tcs = new TaskCompletionSource<JsonObject>();
            awaiting = (type, id, tcs);
            var cts = new CancellationTokenSource();
            var waits = new List<Task> { tcs.Task, frame.Scope.CancelledTask };
            Task? timer = null;
            if (J.S(aw, "timeout") is string timeout)
            {
                var at = Duration.Add(eng.Now, timeout, J.S(aw, "calendar") == "business");
                timer = eng.Timer(at, cts.Token);
                waits.Add(timer);
            }
            var w = await Task.WhenAny(waits);
            if (w == tcs.Task) { cts.Cancel(); msg = tcs.Task.Result; }
            else if (w == timer) { awaiting = null; expiredAwaits.Add(type); msg = null; }
            else { cts.Cancel(); awaiting = null; throw new Cancelled(frame.Scope.Reason!); }
        }
        if (msg is null)
        {
            Record(frame, $"{Label(frame, id)}:timeout");
            if (J.P(aw, "onTimeout") is JsonNode ot) await RunAction(ot, frame);
            return;
        }
        Record(frame, $"{Label(frame, id)}:received");
        var msgs = frame.State["messages"] as JsonObject ?? new JsonObject();
        frame.State["messages"] = msgs;
        var received = msgs["received"] as JsonObject ?? new JsonObject();
        msgs["received"] = received;
        received[type] = new JsonObject { ["id"] = J.S(msg, "id"), ["type"] = type };
        msgs["lastReceived"] = new JsonObject { ["id"] = J.S(msg, "id"), ["type"] = type };
    }

    async Task ExecWait(JsonObject st, Frame frame)
    {
        var d = J.S(st, "wait");
        if (!Duration.IsDuration(d)) throw new StepFailure(J.S(st, "id") ?? "wait", $"wait needs a duration, not {d}");
        await Sleep(Duration.Add(eng.Now, d!), frame.Scope);
        Record(frame, $"{J.S(st, "id") ?? "wait"}:{d}");
    }

    // ------------------------------------------------------------------ slot: a sub-orchestration

    async Task<JsonObject> StartChild(string childKind, JsonObject childDoc, Frame frame, JsonObject? extra, Scope? raceWith)
    {
        var id = $"{eng.InstanceId}/{childKind}{++childSeq}";
        var childRef = $"{id}#ctx";
        await eng.Activity(ClaimCheck, new JsonObject { ["op"] = "fork", ["from"] = frame.PayloadRef, ["to"] = childRef });
        var input = new JsonObject
        {
            ["kind"] = childKind, ["doc"] = $"{J.S(childDoc, "name")}@{J.S(childDoc, "release")}",
            ["state"] = J.Clone(frame.State), ["payloadRef"] = childRef,
        };
        if (extra is not null) foreach (var (k, v) in extra.ToList()) input[k] = J.Clone(v);
        var t = eng.Child(id, input);
        return raceWith is null ? await t : await Race(t, raceWith);
    }

    /// <summary>Copy paths written by a child back to this frame: state here, the rest through the claim-check store.</summary>
    async Task CopyBack(Frame frame, JsonObject child, IEnumerable<string> paths)
    {
        var payloadPaths = new JsonArray();
        foreach (var w in paths)
        {
            if (IsStatePath(w))
            {
                var v = J.Get(J.P(child, "state"), w);
                if (v is not null) J.Set(frame.State, w, J.Clone(v));
            }
            else payloadPaths.Add(w);
        }
        if (payloadPaths.Count > 0)
            await eng.Activity(ClaimCheck, new JsonObject { ["op"] = "copy", ["from"] = J.S(child, "payloadRef"), ["to"] = frame.PayloadRef, ["paths"] = payloadPaths });
    }

    async Task ExecSlot(JsonObject st, Frame frame)
    {
        var id = J.S(st, "id") ?? J.S(st, "slot")!;
        var slotName = J.S(st, "slot")!;
        var slot = J.A(doc, "slots").FirstOrDefault(s => J.S(s, "name") == slotName) ?? throw new StepFailure(id, $"Slot {slotName} is not declared");
        var legName = ChooseLeg(slotName, frame.State) ?? throw new StepFailure(id, $"No leg fills slot {slotName} for this payment");
        // A leg is pinned when it starts (R-01-16).
        var legDoc = dep.Resolve(legName, null, Elapsed) ?? throw new StepFailure(id, $"Leg {legName} does not resolve");
        ReleaseTrace.Add($"leg@{J.S(legDoc, "release")}");
        var child = await StartChild("leg", legDoc, frame, null, frame.Scope);
        frame.Scope.ThrowIfCancelled();
        // The slot contract says what the leg may write back.
        await CopyBack(frame, child, J.Strings(J.P(J.P(slot, "contract"), "writes")));
        if (J.S(child, "reasonCode") is string rc) ReasonCode = rc;
        var outcome = J.S(child, "outcome") ?? "failed";
        outcomes[id] = outcome;
        Record(frame, $"{Label(frame, id)}:{outcome}");
        var contract = J.P(slot, "contract");
        var primary = J.S(contract, "primaryOutcome") ?? J.Strings(J.P(contract, "outcomes")).FirstOrDefault();
        if (outcome == primary) return;
        var handle = J.P(st, "handle");
        if (!J.Has(handle, outcome)) throw new StepFailure(id, $"Leg outcome {outcome} is not handled (L05)");
        await RunAction(J.P(handle, outcome), frame);
    }

    string? ChooseLeg(string slot, JsonObject state)
    {
        if (legs.TryGetValue(slot, out var l)) return l;
        var rail = J.S(J.Get(state, "facts.route"), "rail") ?? J.S(J.P(state, "header"), "scheme");
        var scheme = rail is null ? null : dep.Scheme(rail);
        var myName = J.S(doc, "name");
        foreach (var leg in J.Strings(J.P(scheme, "legs")))
        {
            var d = dep.Resolve(leg, null, Elapsed);
            var impl = J.P(d, "implements");
            if (J.S(impl, "slot") == slot && J.Strings(J.P(impl, "of")).Contains(myName!)) return leg;
        }
        foreach (var d in dep.AllDocs())
        {
            var impl = J.P(d, "implements");
            if (J.S(d, "kind") == "workflow" && J.S(d, "wfKind") == "leg" && J.S(impl, "slot") == slot && J.Strings(J.P(impl, "of")).Contains(myName!)) return J.S(d, "name");
        }
        return null;
    }

    // ------------------------------------------------------------------ use, try

    async Task ExecUse(JsonObject st, Frame frame)
    {
        var use = J.S(st, "use")!;
        var seg = ResolveFor(use, use).doc;
        if (J.S(seg, "wfKind") != "segment") throw new StepFailure(use, $"{use} is not a segment");
        // Segments are inlined: their steps run in this frame; their hook points belong to the segment.
        await ExecSteps(J.A(seg, "do"), frame.With(f => f.HookOwner = J.S(seg, "name")!));
    }

    async Task ExecTry(JsonObject st, Frame frame)
    {
        var tf = new TryFrame();
        var tframe = frame.With(f => f.TryStack = new List<TryFrame>(frame.TryStack) { tf });
        try { await ExecSteps(J.A(st, "try"), tframe); }
        catch (Exception e) when (e is not EndFlow && e is not Cancelled)
        {
            if (J.S(st, "compensate") == "all") await Compensate(tf.Writes, frame);
            if (J.P(st, "catch") is JsonArray c) { await ExecSteps(c, frame); return; }
            throw;
        }
    }

    // ------------------------------------------------------------------ hook: sub-orchestration from the tenant binding

    async Task ExecHook(JsonObject st, Frame frame)
    {
        var name = J.S(st, "hook")!;
        var ownerDoc = dep.Resolve(frame.HookOwner, null, Elapsed) ?? doc;
        var point = J.A(ownerDoc, "hookPoints").FirstOrDefault(h => J.S(h, "name") == name) ?? J.A(doc, "hookPoints").FirstOrDefault(h => J.S(h, "name") == name)
            ?? throw new StepFailure(name, $"Hook point {name} is not declared (L06)");
        var key = $"{frame.HookOwner}#{name}";
        var binding = bindings.FirstOrDefault(b => J.S(b, "hookPoint") == key);
        if (binding is null) return; // An unbound hook point does nothing and costs nothing (R-06-11).
        var hook = dep.Resolve(J.S(binding, "hook")!, J.S(binding, "release"), Elapsed) ?? throw new StepFailure(name, $"Hook {J.S(binding, "hook")}@{J.S(binding, "release")} does not resolve");
        ReleaseTrace.Add($"hook@{J.S(hook, "release")}");
        var policy = J.P(point, "policy");
        var traceAt = Trace.Count;
        Trace.Add("hook:pending");
        var type = J.S(point, "type");
        async Task<(string outcome, string? failed)> RunIt()
        {
            try
            {
                var extra = new JsonObject { ["exitOutcomes"] = J.Clone(J.P(hook, "outcomes")) };
                var childTask = StartChild("hook", hook, frame, extra, null);
                JsonObject child;
                if (J.S(policy, "timeout") is string to)
                {
                    var cts = new CancellationTokenSource();
                    var timer = eng.Timer(Duration.Add(eng.Now, to), cts.Token);
                    var w = await Task.WhenAny(childTask, timer);
                    if (w != childTask) return ("failed", "timeout");
                    cts.Cancel();
                }
                child = await childTask;
                if (J.S(child, "failed") is string f) return ("failed", f);
                // A hook reads and writes only what the hook point's contract allows (R-06-07).
                var allowed = J.Strings(J.P(J.P(point, "contract"), "writes"));
                var diff = await eng.Activity(ClaimCheck, new JsonObject
                {
                    ["op"] = "diff", ["aState"] = J.Clone(frame.State), ["aRef"] = frame.PayloadRef, ["bState"] = J.Clone(J.P(child, "state")), ["bRef"] = J.S(child, "payloadRef"),
                });
                // A hook's blocks may derive facts, as check L13 allows (R-06-07).
                var outside = J.Strings(J.P(diff, "paths")).Where(p => !J.CoveredByAny(allowed, p) && !p.StartsWith("facts.")).ToList();
                if (outside.Count > 0) return ("failed", $"wrote outside its contract: {string.Join(", ", outside)}");
                if (type == "transform") await CopyBack(frame, child, allowed);
                return (J.S(child, "outcome") ?? "done", null);
            }
            catch (Exception e) when (e is not Cancelled)
            {
                return ("failed", e.Message);
            }
        }
        if (type == "notify")
        {
            // A notify hook must not delay or change the flow (R-06-09).
            background.Add(((Func<Task>)(async () =>
            {
                var r = await RunIt();
                Trace[traceAt] = $"hook:{(r.outcome == "failed" ? "failed" : r.outcome)}";
            }))());
            return;
        }
        var res = await RunIt();
        if (res.failed is not null) Console.Error.WriteLine($"hook {J.S(hook, "name")} at {key} failed: {res.failed}");
        Trace[traceAt] = $"hook:{res.outcome}";
        var outcome = res.outcome;
        if (outcome == "failed") outcome = J.S(policy, "onFailure") == "fail-closed" ? "hold" : "continue";
        if (type == "transform")
        {
            if (outcome == "hold") await HoldFor(point, frame);
            return;
        }
        if (outcome == "hold") { await HoldFor(point, frame); return; }
        if (outcome == "reject")
        {
            await CompensateScope(frame);
            throw new EndFlow("rejected");
        }
    }

    async Task HoldFor(JsonNode point, Frame frame)
    {
        var holdBlock = J.S(J.P(point, "policy"), "onHold") ?? "p.hold.approval.v1";
        var (outcome, act) = await RunActivityStep(new JsonObject { ["id"] = "hold", ["call"] = holdBlock }, frame);
        if (outcome == PrimaryOutcome(act)) return;
        await CompensateScope(frame);
        throw new EndFlow(outcome == "expired" ? "review" : "rejected");
    }
}
