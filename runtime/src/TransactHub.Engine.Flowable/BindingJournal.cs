using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Engines;
using TransactHub.Engines.Durable;
using TransactHub.Store;

namespace TransactHub.Engine.Flowable;

/// <summary>
/// The Flowable binding's state as facts in the store (Issue I-89, R-09-65): insert-only, hash-chained, on a subject of its own
/// (<c>flowable-run:&lt;run key&gt;</c>, kind <c>engine-run</c>). Before every command that moves the engine the binding records
/// what it knows (each instance's trace and detail, its attempts and steps, held messages, the hook bindings taken at start, the
/// timers it placed, what it has scheduled) and the command it is about to send. A binding that starts with the same run key
/// (TH_FLOWABLE_RUN) reads the last of these facts, sends that command again where it may not have reached the server, and goes
/// on from there, as the shared <see cref="StoreSignals"/> feed goes on from its <c>signal delivered</c> facts.
/// </summary>
public sealed class BindingJournal(StoreOptions store, string key)
{
    public const string SubjectKind = "engine-run";
    public string Key => key;
    /// <summary>The journal's subject: the run key, or its hash where the key is longer than a subject id may be (64 characters).</summary>
    public string Subject => key.Length <= 48 ? $"flowable-run:{key}"
        : $"flowable-run:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..48]}";
    /// <summary>The facts this process wrote.</summary>
    public int Written { get; private set; }

    /// <summary>The binding's last state, or <c>{ closed }</c> once the run has ended, or null for a run not yet begun.</summary>
    public async Task<JsonObject?> Last(CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var f = await db.Facts.AsNoTracking().Where(x => x.SubjectId == Subject).OrderByDescending(x => x.Seq).FirstOrDefaultAsync(ct);
        if (f is null) return null;
        if (f.Code == "closed") return new JsonObject { ["closed"] = f.At.ToString("O") };
        return JsonNode.Parse(f.DataJson) as JsonObject;
    }

    public async Task Save(JsonObject state, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        await new FactLog(db).Append(Subject, SubjectKind, new NewFact("binding", "state", state, Actor: "engine:flowable"), ct);
        Written++;
    }

    public async Task Close(JsonObject summary, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        await new FactLog(db).Append(Subject, SubjectKind, new NewFact("binding", "closed", summary, Actor: "engine:flowable"), ct);
        Written++;
    }
}

public sealed partial class FlowableBinding
{
    BindingJournal? journal;
    string runId = "", deploymentId = "";
    int restarts;
    bool exited;
    /// <summary>The answer each job's attempt was given, until the job is completed: a job handed out again after a restart is answered from here.</summary>
    readonly Dictionary<string, JsonObject> answered = new();

    // ================================================================ writing the journal

    /// <summary>The binding's state, and the command it is about to send (<paramref name="pending"/>), as one fact. Nothing without a journal.</summary>
    async Task Save(JsonObject? pending)
    {
        if (journal is null) return;
        await journal.Save(Snapshot(pending));
    }

    static JsonArray Strs(IEnumerable<string> xs) => J.Arr(xs);
    static JsonObject Map<T>(IEnumerable<KeyValuePair<string, T>> xs, Func<T, JsonNode?> f)
    {
        var o = new JsonObject();
        foreach (var (k, v) in xs) o[k] = f(v);
        return o;
    }

    JsonObject Snapshot(JsonObject? pending)
    {
        var q = new JsonArray();
        foreach (var (ev, k) in queue.UnorderedItems.OrderBy(x => x.Priority.at).ThenBy(x => x.Priority.wall).ThenBy(x => x.Priority.seq))
            q.Add(new JsonObject { ["at"] = k.at, ["wall"] = k.wall, ["seq"] = k.seq, ["kind"] = ev.Kind, ["data"] = J.Clone(ev.Data) });
        var counters = new JsonObject
        {
            ["timersPlaced"] = timersPlaced, ["handedOutAgain"] = handedOutAgain, ["crashes"] = crashes, ["pendingStarts"] = pendingStarts,
            ["unmatched"] = unmatched, ["duplicates"] = duplicates, ["earlyKept"] = earlyKept, ["jobsWorked"] = jobsWorked, ["bindingJobs"] = bindingJobs,
            ["asyncJobs"] = asyncJobs, ["timersFired"] = timersFired, ["lateAbandoned"] = lateAbandoned, ["retriedOnFailure"] = retriedOnFailure,
            ["attemptTimersParked"] = attemptTimersParked, ["attemptTimersArmed"] = attemptTimersArmed,
        };
        JsonObject? live = liveFeed is StoreSignals.Feed f ? new JsonObject { ["subject"] = f.Subject, ["kind"] = f.SubjectKind, ["instance"] = f.Instance } : null;
        return new JsonObject
        {
            ["v"] = 1, ["runId"] = runId, ["tenant"] = tenant, ["deployment"] = deploymentId,
            ["start"] = start, ["now"] = now, ["wallStart"] = wallStart, ["offset"] = offset, ["seq"] = seq,
            ["workerId"] = workerId, ["crashed"] = crashed, ["exited"] = exited, ["executorWarned"] = executorWarned, ["restarts"] = restarts,
            ["begun"] = Strs(begun.Order(StringComparer.Ordinal)), ["hung"] = Strs(hung.Order(StringComparer.Ordinal)),
            ["placed"] = Strs(placed.Order(StringComparer.Ordinal)), ["placedDue"] = Map(placedDue, v => v),
            ["armed"] = Strs(armed.Order(StringComparer.Ordinal)),
            ["deadLetters"] = new JsonArray(deadLetters.Select(d => (JsonNode?)J.Clone(d)).ToArray()), ["deadSeen"] = Strs(deadSeen.Order(StringComparer.Ordinal)),
            ["counters"] = counters, ["log"] = Strs(log), ["notes"] = Strs(notes),
            ["marks"] = new JsonArray(marks.Select(m => (JsonNode?)new JsonArray(m.wall, m.virt)).ToArray()),
            ["insts"] = new JsonArray(insts.Values.Select(i => (JsonNode?)i.ToJson()).ToArray()),
            ["roots"] = Strs(roots.Select(r => r.Pid)),
            ["queue"] = q,
            ["mailbox"] = new JsonArray(mailbox.Select(m => (JsonNode?)new JsonObject { ["message"] = J.Clone(m), ["at"] = receivedAt.TryGetValue(m, out var at) ? at : null }).ToArray()),
            ["timerValue"] = Map(timerValue, v => v), ["lastTimerValue"] = Map(lastTimerValue, v => v),
            ["liveTried"] = liveTried, ["live"] = live,
            ["waitingSince"] = waitingSince?.ToUnixTimeMilliseconds(),
            ["answered"] = Map(answered, v => J.Clone(v)),
            ["ruleTraces"] = Map(claim.RuleTraces, v => J.Clone(v)),
            ["pending"] = pending is null ? null : J.Clone(pending),
            // A distribution's binding (R-09-198): the payments it serves, by root, and which ends its host has recorded.
            ["served"] = J.Clone(servedRoots),
        };
    }

    // ================================================================ reading it back

    /// <summary>Puts back what the binding knew when it last wrote its state.</summary>
    void Restore(JsonObject s)
    {
        runId = J.S(s, "runId")!;
        tenant = J.S(s, "tenant")!;
        deploymentId = J.S(s, "deployment")!;
        start = (long)J.Num(s, "start")!;
        now = (long)J.Num(s, "now")!;
        wallStart = (long)J.Num(s, "wallStart")!;
        offset = (long)J.Num(s, "offset")!;
        seq = (long)J.Num(s, "seq")!;
        workerId = J.S(s, "workerId") ?? WorkerId;
        crashed = J.Truthy(J.P(s, "crashed"));
        exited = J.Truthy(J.P(s, "exited"));
        executorWarned = J.Truthy(J.P(s, "executorWarned"));
        restarts = (int)(J.Num(s, "restarts") ?? 0) + 1;
        foreach (var x in J.Strings(J.P(s, "begun"))) begun.Add(x);
        foreach (var x in J.Strings(J.P(s, "hung"))) hung.Add(x);
        foreach (var x in J.Strings(J.P(s, "placed"))) placed.Add(x);
        foreach (var (k, v) in J.Obj(J.P(s, "placedDue"))) placedDue[k] = (long)J.D(v)!.Value;
        foreach (var x in J.Strings(J.P(s, "armed"))) armed.Add(x);
        foreach (var d in J.A(s, "deadLetters")) deadLetters.Add((JsonObject)J.Clone(d)!);
        foreach (var x in J.Strings(J.P(s, "deadSeen"))) deadSeen.Add(x);
        var c = J.P(s, "counters");
        int C(string k) => (int)(J.Num(c, k) ?? 0);
        timersPlaced = C("timersPlaced"); handedOutAgain = C("handedOutAgain"); crashes = C("crashes"); pendingStarts = C("pendingStarts");
        unmatched = C("unmatched"); duplicates = C("duplicates"); earlyKept = C("earlyKept"); jobsWorked = C("jobsWorked"); bindingJobs = C("bindingJobs");
        asyncJobs = C("asyncJobs"); timersFired = C("timersFired"); lateAbandoned = C("lateAbandoned"); retriedOnFailure = C("retriedOnFailure");
        attemptTimersParked = C("attemptTimersParked"); attemptTimersArmed = C("attemptTimersArmed");
        log.AddRange(J.Strings(J.P(s, "log")));
        notes.AddRange(J.Strings(J.P(s, "notes")));
        foreach (var m in J.A(s, "marks")) marks.Add(((long)J.D(m![0])!.Value, (long)J.D(m[1])!.Value));
        foreach (var o in J.A(s, "insts"))
        {
            var i = Inst.FromJson((JsonObject)o!, pid => insts.GetValueOrDefault(pid));
            insts[i.Pid] = i;
        }
        foreach (var pid in J.Strings(J.P(s, "roots"))) if (insts.TryGetValue(pid, out var r)) roots.Add(r);
        foreach (var e in J.A(s, "queue"))
            queue.Enqueue(new Ev((long)J.Num(e, "at")!, J.S(e, "kind")!, (JsonObject)J.Clone(J.P(e, "data"))!), ((long)J.Num(e, "at")!, (long)J.Num(e, "wall")!, (long)J.Num(e, "seq")!));
        foreach (var e in J.A(s, "mailbox"))
        {
            var m = (JsonObject)J.Clone(J.P(e, "message"))!;
            mailbox.Add(m);
            if (J.Num(e, "at") is double at) receivedAt[m] = (long)at;
        }
        foreach (var (k, v) in J.Obj(J.P(s, "timerValue"))) timerValue[k] = J.AsKey(v)!;
        foreach (var (k, v) in J.Obj(J.P(s, "lastTimerValue"))) lastTimerValue[k] = J.AsKey(v)!;
        liveTried = J.Truthy(J.P(s, "liveTried"));
        if (J.Num(s, "waitingSince") is double ws) waitingSince = DateTimeOffset.FromUnixTimeMilliseconds((long)ws);
        foreach (var (k, v) in J.Obj(J.P(s, "answered"))) answered[k] = (JsonObject)J.Clone(v)!;
        foreach (var (k, v) in J.Obj(J.P(s, "ruleTraces"))) claim.RuleTraces[k] = J.Clone(v);
        foreach (var (k, v) in J.Obj(J.P(s, "served"))) servedRoots[k] = J.Clone(v);
    }

    /// <summary>
    /// After the state is back: the claim check rebuilt from its facts, the live feed followed again from its own facts, the command
    /// the binding was sending sent again, and every job the old process had locked handed back, so that it is taken again as the
    /// attempt it was (Issue I-89).
    /// </summary>
    async Task Resume(JsonObject s)
    {
        var storeOpts = StoreFromEnvironment()!;
        // The payloads of starts not yet registered too: the pending start's and the scheduled ones' (I-302).
        var startRefs = queue.UnorderedItems.Where(x => x.Element.Kind == "start").Select(x => J.S(x.Element.Data, "payloadRef"))
            .Append(J.S(J.P(s, "pending"), "payloadRef")).OfType<string>()
            // And those of the roots already started: their start's payload is the store's too, so the claim check holds it as the
            // store rebuilds it and the run's store report matches (Issue I-407).
            .Concat(roots.Select(r => StartRef(r.Label)));
        if (claim is DurableClaimCheck durable) await durable.Resume(insts.Values.Select(i => i.Ref).Concat(startRefs));
        if (J.P(s, "live") is JsonObject live && LiveSignals.Source is not null)
            liveFeed = new StoreSignals.Feed(storeOpts, TimeProvider.System, J.S(live, "subject")!, J.S(live, "kind")!, "flowable", J.S(live, "instance")!);
        // A step answered again after the restart is not recorded twice (R-05-50).
        StepFacts.Current?.SkipRecorded();
        log.Add($"restarted (restart {restarts}): run {journal!.Key}, tenant {tenant}, deployment {deploymentId}, {insts.Count} instance(s), {queue.Count} scheduled, {mailbox.Count} held message(s)");
        if (J.P(s, "pending") is JsonObject pending) await Replay(pending);
        await rest.Unacquire(workerId, tenant);
        await Save(null);
    }

    /// <summary>The command the binding was sending when it stopped: sent again; one that had already reached the server is refused there, and that is fine.</summary>
    async Task Replay(JsonObject p)
    {
        var op = J.S(p, "op");
        try
        {
            switch (op)
            {
                case "complete":
                {
                    var ok = await rest.Complete(J.S(p, "job")!, workerId, VarsOf(J.P(p, "vars")));
                    // Gone: either it was completed before the stop, or a boundary ended it. A job that answered is taken as completed.
                    if (J.P(p, "after") is JsonObject after) await After(after);
                    log.Add($"replayed the completion of job {J.S(p, "job")} ({(ok ? "sent now" : "had reached the server")})");
                    answered.Remove(J.S(p, "job")!);
                    break;
                }
                case "bpmnError":
                    await rest.BpmnError(J.S(p, "job")!, workerId, J.S(p, "code")!);
                    answered.Remove(J.S(p, "job")!);
                    break;
                case "fail":
                    await rest.Fail(J.S(p, "job")!, workerId, J.S(p, "message") ?? "", (int)(J.Num(p, "retries") ?? 0), System.Xml.XmlConvert.ToTimeSpan(J.S(p, "wait") ?? "PT0S"));
                    break;
                case "message":
                {
                    var vars = J.P(p, "vars") is JsonObject vo ? vo.ToDictionary(kv => kv.Key, kv => J.AsKey(kv.Value) ?? "") : null;
                    await rest.MessageReceived(J.S(p, "execution")!, J.S(p, "name")!, vars);
                    break;
                }
                case "moveTimer": await rest.MoveTimer(J.S(p, "id")!); break;
                case "reschedule": await rest.RescheduleTimer(J.S(p, "id")!, DateTimeOffset.FromUnixTimeMilliseconds((long)J.Num(p, "due")!)); break;
                case "start":
                {
                    var label = J.S(p, "label")!;
                    var existing = await rest.ProcessInstances(tenant, $"{label}-{tenant}");
                    if (existing.Count > 0 && J.S(existing[0], "id") is string pid)
                    {
                        await RegisterRoot(label, J.S(p, "workflow")!, J.S(p, "release")!, ContextOf(p), pid);
                        log.Add($"replayed the start of {label}: the server had started it as {pid}");
                    }
                    else await StartRoot(p);
                    break;
                }
            }
        }
        catch (FlowableException e)
        {
            log.Add($"replayed {op}: the server refused it ({(int)e.Status}): it had already taken effect");
        }
    }

    /// <summary>What follows a completed attempt: its staged writes committed, the state writes made, the step's delta recorded.</summary>
    async Task After(JsonObject a)
    {
        if (J.S(a, "token") is string token) await claim.Commit(token);
        if (!insts.TryGetValue(J.S(a, "pid") ?? "", out var inst)) return;
        foreach (var w in J.A(a, "stateWrites")) J.Set(inst.State, J.S(w, "path")!, J.Clone(J.P(w, "value")));
        if (inst.OpenStep.TryGetValue(J.S(a, "label") ?? "", out var rec)) rec["delta"] = J.Clone(J.P(a, "delta"));
    }

    static JsonObject VarsJson(IEnumerable<KeyValuePair<string, JsonNode?>> vars) => Map(vars, v => J.Clone(v));
    static Dictionary<string, JsonNode?> VarsOf(JsonNode? o) => J.Obj(o).ToDictionary(kv => kv.Key, kv => J.Clone(kv.Value));

    // ================================================================ engine commands, recorded first

    async Task<bool> CompleteJob(string jobId, Dictionary<string, JsonNode?> vars, JsonObject? after = null)
    {
        await Save(new JsonObject { ["op"] = "complete", ["job"] = jobId, ["vars"] = VarsJson(vars), ["after"] = after });
        Mark();
        var ok = await rest.Complete(jobId, workerId, vars);
        answered.Remove(jobId);
        return ok;
    }

    async Task<bool> ErrorJob(string jobId, string code)
    {
        await Save(new JsonObject { ["op"] = "bpmnError", ["job"] = jobId, ["code"] = code });
        Mark();
        var ok = await rest.BpmnError(jobId, workerId, code);
        answered.Remove(jobId);
        return ok;
    }

    async Task FailJob(string jobId, string message, int retries, TimeSpan wait)
    {
        await Save(new JsonObject { ["op"] = "fail", ["job"] = jobId, ["message"] = message, ["retries"] = retries, ["wait"] = System.Xml.XmlConvert.ToString(wait) });
        Mark();
        await rest.Fail(jobId, workerId, message, retries, wait);
    }

    async Task Message(string executionId, string name, IReadOnlyDictionary<string, string>? vars = null)
    {
        await Save(new JsonObject { ["op"] = "message", ["execution"] = executionId, ["name"] = name, ["vars"] = vars is null ? null : Map(vars, v => v) });
        Mark();
        await rest.MessageReceived(executionId, name, vars);
    }

    async Task<bool> Reschedule(string id, DateTimeOffset due)
    {
        await Save(new JsonObject { ["op"] = "reschedule", ["id"] = id, ["due"] = due.ToUnixTimeMilliseconds() });
        return await rest.RescheduleTimer(id, due);
    }

    async Task MoveTimerNow(string id)
    {
        await Save(new JsonObject { ["op"] = "moveTimer", ["id"] = id });
        Mark();
        await rest.MoveTimer(id);
    }

    /// <summary>Test only (TH_FLOWABLE_FAULT=exit:&lt;step&gt;): the process stops as a killed host would, once, right after the step's answer is recorded.</summary>
    async Task ExitIfAsked(string step)
    {
        if (opts.ExitAt != step || exited || journal is null) return;
        exited = true;
        await Save(null);
        Console.Error.WriteLine($"the binding stops at {step} (TH_FLOWABLE_FAULT); a binding started with run key {journal.Key} goes on");
        Console.Error.Flush();
        Environment.Exit(75);
    }

    static JsonObject JobJson(ExternalJob j) => new()
    {
        ["id"] = j.Id, ["pid"] = j.ProcessInstanceId, ["def"] = j.ProcessDefinitionId, ["exec"] = j.ExecutionId, ["element"] = j.ElementId,
        ["created"] = j.CreateTime.ToUnixTimeMilliseconds(), ["retries"] = j.Retries, ["vars"] = VarsJson(j.Variables),
    };

    static ExternalJob JobOf(JsonObject o) => new(J.S(o, "id")!, J.S(o, "pid")!, J.S(o, "def")!, J.S(o, "exec") ?? "", J.S(o, "element")!,
        DateTimeOffset.FromUnixTimeMilliseconds((long)(J.Num(o, "created") ?? 0)), (int)(J.Num(o, "retries") ?? 0), VarsOf(J.P(o, "vars")));

    sealed partial class Inst
    {
        public JsonObject ToJson()
        {
            var items = new JsonObject();
            foreach (var (n, es) in Items) items[n.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonArray(es.Select(e => (JsonNode?)new JsonArray(e.detail, e.entry)).ToArray());
            var open = new JsonObject();
            foreach (var (label, rec) in OpenStep)
            {
                var ix = -1;
                for (var k = 0; k < Steps.Count; k++) if (ReferenceEquals(Steps[k], rec)) { ix = k; break; }
                open[label] = ix;
            }
            return new JsonObject
            {
                ["pid"] = Pid, ["label"] = Label, ["name"] = Name, ["release"] = Release, ["kind"] = Kind, ["key"] = Key, ["ref"] = Ref, ["parent"] = Parent?.Pid,
                ["state"] = J.Clone(State), ["trace"] = J.Arr(trace), ["detail"] = J.Arr(detail), ["releaseTrace"] = J.Arr(ReleaseTrace), ["pendingTrace"] = J.Arr(Pending),
                ["ruleTraceSteps"] = J.Arr(RuleTraceSteps), ["items"] = items, ["stopAt"] = StopAt == int.MaxValue ? null : StopAt,
                ["steps"] = J.Clone(Steps), ["open"] = open,
                ["attempts"] = Map(Attempts, v => v), ["calls"] = Map(Calls, v => v), ["notifyAt"] = Map(NotifyAt, v => v),
                ["expired"] = J.Arr(Expired.Order(StringComparer.Ordinal)), ["consumed"] = J.Arr(Consumed.Order(StringComparer.Ordinal)), ["superseded"] = J.Arr(Superseded.Order(StringComparer.Ordinal)),
                ["lastChild"] = Map(LastChild, v => v), ["stateKeys"] = J.Arr(StateKeys), ["awaited"] = J.Arr(Awaited.Order(StringComparer.Ordinal)),
                ["blockStarted"] = Map(BlockStarted, v => v), ["snapshots"] = Snapshots, ["registered"] = Registered, ["ended"] = Ended,
                ["startedAt"] = StartedAt, ["endedAt"] = EndedAt, ["failed"] = Failed, ["lastFatal"] = LastFatal, ["deadLetter"] = DeadLetter,
                ["bindings"] = new JsonArray(Bindings.Select(b => (JsonNode?)J.Clone(b)).ToArray()),
            };
        }

        public static Inst FromJson(JsonObject o, Func<string, Inst?> byPid)
        {
            var i = new Inst
            {
                Pid = J.S(o, "pid")!, Label = J.S(o, "label")!, Name = J.S(o, "name")!, Release = J.S(o, "release")!, Kind = J.S(o, "kind")!, Key = J.S(o, "key")!, Ref = J.S(o, "ref")!,
                Parent = J.S(o, "parent") is string pp ? byPid(pp) : null, State = (JsonObject)J.Clone(J.P(o, "state"))!,
                Snapshots = (int)(J.Num(o, "snapshots") ?? 0), Registered = J.Truthy(J.P(o, "registered")), Ended = J.Truthy(J.P(o, "ended")),
                StartedAt = (long)(J.Num(o, "startedAt") ?? 0), EndedAt = J.Num(o, "endedAt") is double ea ? (long)ea : null,
                Failed = J.S(o, "failed"), LastFatal = J.S(o, "lastFatal"), DeadLetter = J.S(o, "deadLetter"),
                Bindings = J.A(o, "bindings").Select(b => (JsonObject)J.Clone(b)!).ToList(),
                StopAt = J.Num(o, "stopAt") is double sa ? (int)sa : int.MaxValue,
            };
            i.trace.AddRange(J.Strings(J.P(o, "trace")));
            i.detail.AddRange(J.Strings(J.P(o, "detail")));
            i.ReleaseTrace.AddRange(J.Strings(J.P(o, "releaseTrace")));
            i.Pending.AddRange(J.Strings(J.P(o, "pendingTrace")));
            i.RuleTraceSteps.AddRange(J.Strings(J.P(o, "ruleTraceSteps")));
            foreach (var (n, es) in J.Obj(J.P(o, "items")))
                i.Items[int.Parse(n, System.Globalization.CultureInfo.InvariantCulture)] = ((JsonArray)es!).Select(e => (J.Truthy(e![0]), J.AsKey(e[1])!)).ToList();
            foreach (var st in J.A(o, "steps")) i.Steps.Add(J.Clone(st));
            foreach (var (label, ix) in J.Obj(J.P(o, "open")))
                if (J.D(ix) is double d && d >= 0 && d < i.Steps.Count) i.OpenStep[label] = (JsonObject)i.Steps[(int)d]!;
            foreach (var (k, v) in J.Obj(J.P(o, "attempts"))) i.Attempts[k] = (int)J.D(v)!.Value;
            foreach (var (k, v) in J.Obj(J.P(o, "calls"))) i.Calls[k] = (int)J.D(v)!.Value;
            foreach (var (k, v) in J.Obj(J.P(o, "notifyAt"))) i.NotifyAt[k] = (int)J.D(v)!.Value;
            foreach (var x in J.Strings(J.P(o, "expired"))) i.Expired.Add(x);
            foreach (var x in J.Strings(J.P(o, "consumed"))) i.Consumed.Add(x);
            foreach (var x in J.Strings(J.P(o, "superseded"))) i.Superseded.Add(x);
            foreach (var (k, v) in J.Obj(J.P(o, "lastChild"))) i.LastChild[k] = J.AsKey(v)!;
            foreach (var x in J.Strings(J.P(o, "stateKeys"))) i.StateKeys.Add(x);
            foreach (var x in J.Strings(J.P(o, "awaited"))) i.Awaited.Add(x);
            foreach (var (k, v) in J.Obj(J.P(o, "blockStarted"))) i.BlockStarted[k] = (long)J.D(v)!.Value;
            return i;
        }
    }
}
