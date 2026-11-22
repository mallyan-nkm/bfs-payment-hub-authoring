using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TransactHub.Engines;
using TransactHub.Engines.Durable;
using TransactHub.Store;

namespace TransactHub.Engine.Flowable;

/// <summary>
/// Runs one plan on the Flowable REST server (plan D3): the BPMN the studio exported for a REST binding
/// (studio/src/export/bpmn.ts, binding 'rest') is deployed through /repository/deployments, each root is
/// started through /runtime/process-instances, and this binding is the external worker that fetches and
/// completes every job over REST: the activity tasks, whose attempts go to the worker process of their
/// task queue, and the binding's own tasks (topic th.binding), where it writes the trace, names the leg
/// of a slot, counts a batch, checks a hook's contract and learns of each process instance.
///
/// The engine holds references and flow variables only. A payload lives in the claim check: in memory,
/// or, with TH_STORE_PROVIDER set, in the store, as the Temporal binding keeps it (<see cref="DurableClaimCheck"/>).
///
/// Time is the binding's: the server's async executor is off, so the binding runs the jobs it would run,
/// and fires a timer when the binding's virtual clock reaches it. A wait of days takes no wall time.
/// The plan and the observation are those of the other engines' harnesses, so the kit runner and the
/// studio's deploy runner read them unchanged.
/// </summary>
public sealed class FlowableBinding
{
    public const string WorkerId = "th-flowable-binding";
    public const string BindingTopic = "th.binding";
    static readonly TimeSpan LockFor = TimeSpan.FromMinutes(10);
    static readonly Dictionary<string, string> SignalMessages = new() { ["pause"] = "th.signal.pause", ["resume"] = "th.signal.resume", ["cancel"] = "th.signal.cancel", ["re-screen"] = "th.signal.re-screen" };

    readonly JsonObject input, built, spec, config;
    readonly FlowableRest rest;
    readonly List<string> log = new(), notes = new();
    Deployment dep = null!;
    IParty party = null!;
    IClaimCheck claim = null!;
    long start, now;
    string tenant = "";

    readonly Dictionary<string, BpmnProcess> byKey = new(), byDef = new();
    readonly List<(long wall, long virt)> marks = new();
    readonly Dictionary<string, Inst> insts = new();
    readonly List<Inst> roots = new();
    readonly PriorityQueue<Ev, (long at, long wall, long seq)> queue = new();
    long seq;
    readonly List<JsonObject> mailbox = new();
    readonly Dictionary<JsonObject, long> receivedAt = new(ReferenceEqualityComparer.Instance);
    /// <summary>The value a timer from a duration parameter took (R-05-45): by timer job id, and by instance and timer element.</summary>
    readonly Dictionary<string, string> timerValue = new(), lastTimerValue = new();
    int unmatched, duplicates, earlyKept, jobsWorked, bindingJobs, asyncJobs, timersFired, lateAbandoned, retriedOnFailure;

    sealed record Ev(long At, string Kind, Func<Task> Run);

    sealed class Inst
    {
        public string Pid = "", Label = "", Name = "?", Release = "?", Kind = "workflow", Key = "", Ref = "";
        public Inst? Parent;
        public JsonObject State = new();
        public readonly List<string> Trace = new(), Detail = new(), ReleaseTrace = new(), Pending = new(), RuleTraceSteps = new();
        public readonly JsonArray Steps = new();
        public readonly Dictionary<string, int> Attempts = new(), Calls = new(), NotifyAt = new();
        public readonly Dictionary<string, JsonObject> OpenStep = new();
        public readonly HashSet<string> Expired = new(), Consumed = new(), Superseded = new();
        public readonly Dictionary<string, string> LastChild = new();
        public readonly SortedSet<string> StateKeys = new(StringComparer.Ordinal);
        public readonly HashSet<string> Awaited = new(); // the message of every await this instance has waited for (R-05-43)
        public readonly Dictionary<string, long> BlockStarted = new(); // block element id -> when it last began
        public int Snapshots;
        public bool Registered, Ended;
        public long StartedAt;
        public long? EndedAt;
        public string? Failed, LastFatal;
        public List<JsonObject> Bindings = new();
    }

    readonly IParty? given;

    /// <param name="party">Who answers the attempts; by default the plan's workers (a deployment) or the studio's party service (a kit run).</param>
    public FlowableBinding(JsonObject input, FlowableRest rest, IParty? party = null)
    {
        this.input = input;
        this.rest = rest;
        given = party;
        built = (JsonObject)J.P(input, "built")!;
        spec = J.P(built, "spec") as JsonObject ?? new JsonObject();
        config = (JsonObject)J.P(built, "config")!;
    }

    /// <summary>A process key as the generator writes it: safe(name@release).</summary>
    public static string Safe(string s) => Regex.Replace(s, "[^A-Za-z0-9_]", "_");

    /// <summary>The store a deployment uses: TH_STORE_PROVIDER (SqlServer or PostgreSql) and TH_STORE_CONNECTION.</summary>
    public static StoreOptions? StoreFromEnvironment()
    {
        var p = Environment.GetEnvironmentVariable("TH_STORE_PROVIDER");
        var c = Environment.GetEnvironmentVariable("TH_STORE_CONNECTION");
        return string.IsNullOrEmpty(p) || string.IsNullOrEmpty(c) ? null : new StoreOptions(Enum.Parse<StoreProvider>(p, true), c);
    }

    long Elapsed => now - start;

    // ================================================================ the clock

    /// <summary>
    /// Before every command that moves the engine: the wall time and the virtual time it runs at, so a
    /// timer the server creates (stamped with the server's wall clock) is placed on the virtual clock.
    /// </summary>
    void Mark()
    {
        var wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (marks.Count > 0 && marks[^1].virt != now)
            while (wall <= marks[^1].wall) { Thread.SpinWait(200); wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        if (marks.Count == 0 || marks[^1].virt != now || marks[^1].wall != wall) marks.Add((wall, now));
    }

    long VirtualOf(long wall)
    {
        long v = start;
        foreach (var m in marks) { if (m.wall > wall) break; v = m.virt; }
        return v;
    }

    void Schedule(long at, string kind, Func<Task> run) => queue.Enqueue(new Ev(at, kind, run), (at, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), seq++));

    void Deliveries(JsonNode? ds)
    {
        foreach (var d in ds as JsonArray ?? new JsonArray())
        {
            var m = (JsonObject)J.Clone(J.P(d, "message"))!;
            Schedule((long)(J.Num(d, "at") ?? now), "delivery", () => FrontDoor(m));
        }
    }

    // ================================================================ the run

    public async Task<JsonObject> Run()
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        tenant = $"th-{runId}";
        start = DateTimeOffset.Parse($"{J.S(config, "evaluationDate")}T{J.S(config, "startTime") ?? "06:00:00"}Z").ToUnixTimeMilliseconds();
        now = start;
        dep = new Deployment((JsonObject)J.P(input, "documents")!, config, start, (int)(J.Num(input, "chunkSize") ?? 100));
        Deployment.Current = dep;
        using var p = given is not null ? null : J.P(input, "workers") is JsonObject workers ? (IParty)new WorkerRouter(workers, (JsonObject)J.P(input, "queues")!) : new PartyClient(J.S(input, "studioRoot")!);
        party = given ?? p!;
        var storeOpts = StoreFromEnvironment();
        if (storeOpts is not null)
        {
            await using var db = StoreDbContext.Open(storeOpts);
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database);
            StepFacts.Install(storeOpts);
        }
        claim = storeOpts is null ? new MemoryClaimCheck() : new DurableClaimCheck(storeOpts);

        // Deploy every process of the plan as one deployment, in a tenant of this run's own.
        var files = new Dictionary<string, string>();
        foreach (var (file, xml) in J.Obj(J.P(input, "bpmn")))
        {
            var text = J.AsKey(xml)!;
            var proc = BpmnProcess.Parse(text);
            byKey[proc.Key] = proc;
            files[file.EndsWith(".bpmn20.xml") ? file : $"{proc.Key}.bpmn20.xml"] = text;
        }
        if (files.Count == 0) throw new InvalidOperationException("The plan has no BPMN: the package must be exported for flowable");
        var server = await rest.Engine();
        log.Add($"Flowable {J.S(server, "version")} at the REST server, tenant {tenant}");
        var deploymentId = await rest.Deploy($"transacthub-{runId}", files, tenant);
        log.Add($"deployed {files.Count} process(es) as deployment {deploymentId}");
        foreach (var d in await rest.ProcessDefinitions(deploymentId))
            if (byKey.TryGetValue(J.S(d, "key")!, out var proc)) byDef[J.S(d, "id")!] = proc;
        var missing = byKey.Keys.Where(k => !byDef.Values.Any(v => v.Key == k)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"The server did not deploy {string.Join(", ", missing)}");

        try
        {
            await party.Call(new JsonObject { ["op"] = "open", ["scenario"] = J.S(built, "id") });
            var ctx = (JsonObject)J.Clone(J.P(built, "context"))!;
            var workflow = J.S(built, "workflow")!;
            if (J.Truthy(J.P(spec, "handler"))) throw new NotSupportedException("A handler runs as a plain request handler at the interop boundary, not on Flowable (R-09-14)");
            Deliveries(J.P(await party.Call(new JsonObject { ["op"] = "start", ["now"] = start, ["conversation"] = J.S(J.P(ctx, "header"), "conversation") ?? "conv-1" }), "deliveries"));
            if (J.P(spec, "instances") is JsonArray instances)
            {
                foreach (var s in instances)
                {
                    var label = J.S(s, "label")!;
                    var release = J.S(s, "workflowRelease")!;
                    Schedule(start + Duration.Ms(J.S(s, "at") ?? "PT0S"), "start", () => StartRoot(label, workflow, release, (JsonObject)J.Clone(ctx)!));
                }
            }
            else
            {
                var doc = dep.Resolve(workflow, null, 0) ?? throw new InvalidOperationException($"{workflow} does not resolve");
                Schedule(start, "start", () => StartRoot("root", workflow, J.S(doc, "release")!, ctx));
            }
            foreach (var s in J.A(config, "signals"))
            {
                var signal = J.S(s, "signal")!;
                Schedule(start + Duration.Ms(J.S(s, "after")!), "signal", () => Signal(signal));
            }
            await Loop();

            var sent = await party.Call(new JsonObject { ["op"] = "sent" });
            JsonObject? storeReport = storeOpts is null ? null : await StoreReport.Of(storeOpts, r => claim.Full(null, r));
            var requests = new JsonObject();
            foreach (var (k, n) in rest.Requests.OrderBy(kv => kv.Key, StringComparer.Ordinal)) requests[k] = n;
            return new JsonObject
            {
                ["target"] = "flowable",
                ["instances"] = new JsonArray(roots.Select(i => (JsonNode?)Observe(i)).ToArray()),
                ["messages"] = J.Clone(J.P(sent, "messages")),
                ["unmatched"] = unmatched,
                ["duplicates"] = duplicates,
                ["earlyKept"] = earlyKept,
                ["durableInstances"] = insts.Count,
                ["startMs"] = start,
                ["notes"] = J.Arr(notes),
                ["engine"] = new JsonObject
                {
                    ["server"] = J.S(server, "version"),
                    ["deployment"] = deploymentId,
                    ["processes"] = byDef.Count,
                    ["processInstances"] = insts.Count,
                    ["jobsWorked"] = jobsWorked,
                    ["bindingJobs"] = bindingJobs,
                    ["asyncJobs"] = asyncJobs,
                    ["timersFired"] = timersFired,
                    ["lateResultsAbandoned"] = lateAbandoned,
                    ["retriedOnFailure"] = retriedOnFailure,
                    ["requests"] = requests,
                    ["virtualEnd"] = Duration.Iso(now),
                    ["log"] = J.Arr(log),
                    ["store"] = storeReport,
                },
            };
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("TH_FLOWABLE_KEEP") != "1") await rest.DeleteDeployment(deploymentId);
        }
    }

    async Task StartRoot(string label, string workflow, string release, JsonObject ctx)
    {
        var key = Safe($"{workflow}@{release}");
        if (!byKey.ContainsKey(key)) throw new InvalidOperationException($"{workflow}@{release} is not in the deployment");
        Mark();
        var started = await rest.StartProcess(key, tenant, businessKey: $"{label}-{tenant}");
        var pid = J.S(started, "id")!;
        // The process waits at its first binding task (processStart); it is registered here.
        var (state, payload) = ClaimCheckStore.Split(ctx);
        var header = J.P(state, "header");
        var docKey = $"{workflow}@{release}";
        var i = new Inst
        {
            Pid = pid, Label = label, Name = workflow, Release = release, Key = key, Ref = $"{pid}#ctx", State = state, StartedAt = Elapsed,
            Bindings = dep.BindingsAt(Duration.Today(now)), // hook bindings are taken when the instance starts (R-06-13)
        };
        var direction = J.S(header, "direction") == "inward" ? TransactHub.Model.Direction.Inward : TransactHub.Model.Direction.Outward;
        await claim.Create(i.Ref, payload, new AcceptInfo(J.S(input, "tenant") ?? J.S(config, "tenant") ?? "tenant", J.S(header, "scheme") ?? workflow.Split('.')[0], direction, docKey));
        insts[pid] = i;
        roots.Add(i);
        i.ReleaseTrace.Add($"{ShortName(workflow)}@{release}");
        log.Add($"started {docKey} as {pid}");
    }

    static string ShortName(string n)
    {
        var p = n.Split('.');
        return p.Length > 2 ? p[^2] : n;
    }

    // ================================================================ the loop: timers, deliveries, jobs

    async Task Loop()
    {
        await Settle();
        for (var guard = 0; guard < 2_000_000; guard++)
        {
            JsonObject? timer = null;
            long tAt = long.MaxValue, tWall = long.MaxValue;
            foreach (var t in await rest.TimerJobs(tenant))
            {
                var created = FlowableRest.Time(J.S(t, "createTime")).ToUnixTimeMilliseconds();
                var due = FlowableRest.Time(J.S(t, "dueDate")).ToUnixTimeMilliseconds();
                var vdue = VirtualDue(t, VirtualOf(created), due - created);
                if (vdue < tAt || (vdue == tAt && created < tWall)) { tAt = vdue; tWall = created; timer = t; }
            }
            var hasNext = queue.TryPeek(out var next, out var nextKey);
            var useTimer = timer is not null && (!hasNext || tAt < nextKey.at || (tAt == nextKey.at && tWall <= nextKey.wall));
            if (!useTimer && !hasNext) break;
            var at = useTimer ? tAt : nextKey.at;
            if (at - start > 400L * 86_400_000L) { notes.Add("stopped at the 400-day limit"); break; }
            if (at > now) now = at;
            if (useTimer)
            {
                Mark();
                await rest.MoveTimer(J.S(timer, "id")!);
                timersFired++;
            }
            else
            {
                queue.Dequeue();
                await next!.Run();
            }
            await Settle();
        }
    }

    /// <summary>
    /// When a timer is due on the virtual clock: its duration from the model when it is written out (on the
    /// business calendar when the model says so), else the server's due date less its create time, which
    /// the server reads from its clock a moment apart, so it is rounded to a tenth of a second. A timer from
    /// a duration parameter (R-05-45) takes the tenant's value in force on the business date it was created,
    /// else the default the model was written with.
    /// </summary>
    long VirtualDue(JsonObject timer, long vCreated, long serverDuration)
    {
        var element = J.S(timer, "elementId") ?? "";
        var def = byDef.GetValueOrDefault(J.S(timer, "processDefinitionId") ?? "");
        var meta = def?.Ann(element, "timer");
        var calendar = meta?.GetValueOrDefault("calendar");
        // R-05-47: an await's wait a bank may set names its calendar as a parameter too, the policy giving the default.
        if (meta?.GetValueOrDefault("calendarParameter") is string calendarParameter)
            calendar = ParamValue(calendarParameter, calendar ?? "elapsed", Duration.Today(vCreated));
        var business = calendar == "business";
        var written = def?.DurationOf(element);
        if (meta?.GetValueOrDefault("parameter") is string parameter)
        {
            var id = J.S(timer, "id") ?? "";
            if (!timerValue.TryGetValue(id, out var value))
                timerValue[id] = value = ParamValue(parameter, meta.GetValueOrDefault("default") ?? written ?? "PT0S", Duration.Today(vCreated));
            lastTimerValue[$"{J.S(timer, "processInstanceId")}|{element}"] = value;
            written = value;
        }
        if (written is not null && Duration.IsDuration(written)) return Duration.Add(vCreated, written, business);
        return vCreated + (long)Math.Round(serverDuration / 100.0, MidpointRounding.AwayFromZero) * 100;
    }

    /// <summary>A parameter's value in force on a date (the tenant's configuration), else its default (R-01-17).</summary>
    string ParamValue(string parameter, string dflt, string date)
    {
        var inForce = J.A(dep.Config, "values").OfType<JsonObject>()
            .Where(x => J.S(x, "parameter") == parameter && string.CompareOrdinal(J.S(x, "validFrom") ?? "", date) <= 0
                        && (J.S(x, "validTo") is not string to || string.CompareOrdinal(to, date) >= 0))
            .OrderBy(x => J.S(x, "validFrom"), StringComparer.Ordinal).LastOrDefault();
        return inForce is not null ? J.S(inForce, "value") ?? dflt : dflt;
    }

    /// <summary>Until nothing moves: run the jobs the async executor would, work the external jobs, deliver held messages.</summary>
    async Task Settle()
    {
        for (var guard = 0; guard < 100_000; guard++)
        {
            var jobs = (await rest.Jobs(tenant)).OrderBy(j => J.S(j, "createTime"), StringComparer.Ordinal).ThenBy(j => J.S(j, "id"), StringComparer.Ordinal).ToList();
            if (jobs.Count > 0)
            {
                foreach (var j in jobs)
                {
                    Mark();
                    var err = await rest.ExecuteJob(J.S(j, "id")!);
                    asyncJobs++;
                    if (err is null || err.StartsWith("404")) continue;
                    notes.Add($"engine error at {Elapsed} ms on {J.S(j, "elementId")}: {err}");
                    await rest.DeleteJob(J.S(j, "id")!);
                }
                continue;
            }
            var moved = await WorkExternalJobs();
            moved |= await DeliverHeld();
            moved |= await ReleaseCompensation();
            if (!moved) return;
        }
        notes.Add("settle did not converge");
    }

    // ================================================================ the external worker

    async Task<bool> WorkExternalJobs()
    {
        var open = (await rest.ExternalJobs(tenant)).Where(j => J.S(j, "lockOwner") is null).ToList();
        if (open.Count == 0) return false;
        var topics = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var j in open)
        {
            var t = byDef.TryGetValue(J.S(j, "processDefinitionId") ?? "", out var def) ? def.TopicOf(J.S(j, "elementId") ?? "") : null;
            if (t is not null) topics.Add(t);
            else foreach (var d in byDef.Values) foreach (var x in d.Topics) topics.Add(x);
        }
        var all = new List<ExternalJob>();
        foreach (var t in topics) all.AddRange(await rest.Acquire(t, 1000, LockFor, WorkerId, tenant));
        all.Sort((a, b) => a.CreateTime != b.CreateTime ? a.CreateTime.CompareTo(b.CreateTime) : string.CompareOrdinal(a.Id, b.Id));
        foreach (var j in all) await Work(j);
        return all.Count > 0;
    }

    async Task Work(ExternalJob job)
    {
        jobsWorked++;
        if (!byDef.TryGetValue(job.ProcessDefinitionId, out var def)) { notes.Add($"a job of an unknown process {job.ProcessDefinitionId}"); return; }
        if (def.Ann(job.ElementId, "point") is { } point) { bindingJobs++; await Point(job, def, point); return; }
        var task = def.Ann(job.ElementId, "task");
        if (task is null || !insts.TryGetValue(job.ProcessInstanceId, out var inst))
        {
            notes.Add($"no annotation or instance for {job.ElementId}");
            return;
        }
        var step = task["step"];
        var activity = task["activity"];
        var release = task["release"];
        var silent = task.GetValueOrDefault("record") == "silent";
        var attempt = Int(job.Variables.GetValueOrDefault("th_attempt")) ?? 1;
        int? index = task.GetValueOrDefault("item") == "true" ? Int(job.Variables.GetValueOrDefault("th_item")) : null;
        var label = index is null ? step : $"{step}[{index}]";
        if (attempt == 1) inst.Calls[step] = inst.Calls.GetValueOrDefault(step) + 1;
        var call = inst.Calls.GetValueOrDefault(step, 1);
        // Supersession: a step not yet started runs the superseding release (CK-SUP-01).
        var runRelease = release;
        foreach (var s in J.A(config, "supersessions"))
        {
            if (J.S(s, "artefact") != activity || J.S(s, "replaces") != release) continue;
            if (J.S(s, "effectiveAt") is string ea && Elapsed < Duration.Ms(ea)) continue;
            runRelease = J.S(s, "with")!;
            if (inst.Superseded.Add($"{activity}|{step}")) inst.ReleaseTrace.Add($"supersede:{ShortName(activity)} {release} to {runRelease}");
        }
        if (attempt == 1)
        {
            if (def.UndoOf(job.ElementId) is { } undo) inst.Trace.Add($"undo:{undo["step"]}");
            if (!silent) inst.ReleaseTrace.Add($"{ShortName(activity)}@{runRelease}");
            var rec = new JsonObject { ["step"] = label, ["release"] = runRelease, ["startedAt"] = Elapsed, ["policy"] = J.Parse(task.GetValueOrDefault("policy") ?? "{}"), ["delta"] = new JsonArray() };
            if (task.TryGetValue("sources", out var src)) rec["policySources"] = J.Parse(src);
            inst.Steps.Add(rec);
            inst.OpenStep[label] = rec;
        }
        inst.Attempts[step] = inst.Attempts.GetValueOrDefault(step) + 1;
        if (inst.Snapshots++ < 200) foreach (var k in job.Variables.Keys) inst.StateKeys.Add(k);
        var level = task.GetValueOrDefault("level");
        var ctx = index is int ix && level == "item" ? claim.FullForItem(inst.State, inst.Ref, ix) : claim.Full(inst.State, inst.Ref);
        var req = new JsonObject
        {
            ["op"] = "attempt", ["instance"] = inst.Pid, ["activity"] = activity, ["release"] = runRelease, ["step"] = step,
            ["call"] = call, ["attempt"] = attempt, ["now"] = now, ["ctx"] = ctx, ["level"] = index is not null && level == "item" ? "item" : null,
            ["policy"] = task.TryGetValue("policy", out var pol) ? J.Parse(pol) : null,
        };
        // A worker that does not answer is a technical failure, not an attempt: the job's retries cover it.
        // Flowable hands a failed job out again only when its async executor resets the expired lock, and
        // that executor is off (the binding keeps the clock), so the binding retries the call itself while
        // the job's retries last, and fails the job with none left when they are spent: a dead letter.
        JsonObject? res = null;
        for (var left = Math.Max(1, job.Retries); res is null; left--)
        {
            try { res = await party.Call(req); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                retriedOnFailure++;
                if (left > 1) { await Task.Delay(TimeSpan.FromMilliseconds(250 * (job.Retries - left + 1))); continue; }
                inst.Attempts[step]--;
                notes.Add($"{label}: {e.Message}; no retries left, the job is a dead letter");
                Mark();
                await rest.Fail(job.Id, WorkerId, e.Message, 0, TimeSpan.Zero);
                return;
            }
        }
        Deliveries(J.P(res, "deliveries"));
        if (J.S(res, "kind") == "hang") return; // the attempt never answers; the timer boundary ends it
        var latency = J.S(dep.Stub(step, activity), "latency") is string l ? Duration.Ms(l) : 0;
        if (latency > 0) Schedule(now + latency, "complete", () => Finish(job, inst, step, label, index, task, res));
        else await Finish(job, inst, step, label, index, task, res);
    }

    static int? Int(JsonNode? n) => J.D(n) is double d ? (int)d : n is JsonValue v && int.TryParse(J.AsKey(v), out var i) ? i : null;

    async Task Finish(ExternalJob job, Inst inst, string step, string label, int? index, IReadOnlyDictionary<string, string> task, JsonObject res)
    {
        if (J.S(res, "kind") == "error")
        {
            var retryable = J.Truthy(J.P(res, "retryable"));
            var name = J.S(res, "name") ?? "Error";
            inst.LastFatal = $"{task["activity"]} failed: {J.S(res, "message") ?? name}";
            Mark();
            if (!await rest.BpmnError(job.Id, WorkerId, retryable ? name : "th:nonRetryable")) lateAbandoned++;
            return;
        }
        // The rule trace is stored with the payment record (R-04-20).
        if (J.P(res, "ruleTrace") is JsonNode rt)
        {
            if (!inst.RuleTraceSteps.Contains(label)) inst.RuleTraceSteps.Add(label);
            lock (claim.RuleTraces) claim.RuleTraces[$"{inst.Ref}|{step}|{inst.Calls.GetValueOrDefault(step, 1)}"] = J.Clone(rt);
        }
        // Writes: what the engine's state holds stays with the binding; the payload part is staged in the claim check.
        var stateWrites = new List<(string path, JsonNode? value)>();
        var staged = new List<(string path, JsonNode? value)>();
        var delta = new JsonArray();
        var before = claim.Full(inst.State, inst.Ref);
        foreach (var (p, v) in J.Obj(J.P(res, "writes")).ToList())
        {
            var path = index is int i ? $"items[{i}].{p}" : p;
            if (index is null && ClaimCheckStore.IsStatePath(p)) stateWrites.Add((p, J.Clone(v)));
            else staged.Add((path, J.Clone(v)));
            delta.Add(new JsonObject { ["path"] = path, ["before"] = J.Clone(J.Get(before, path)), ["after"] = J.Clone(v) });
        }
        var token = staged.Count > 0 ? await claim.Stage(inst.Ref, staged) : null;
        var vars = new Dictionary<string, JsonNode?>();
        if (task.GetValueOrDefault("record") != "silent") vars[$"outcome_{Safe(step)}"] = J.S(res, "outcome");
        foreach (var (k, v) in J.Obj(J.P(res, "decisions")))
            vars[$"decision_{Safe(k)}"] = v is JsonValue ? J.AsKey(v) : J.Str(v);
        Mark();
        if (!await rest.Complete(job.Id, WorkerId, vars))
        {
            // A timer boundary ended the attempt; the late result is abandoned and never reaches the payment.
            lateAbandoned++;
            return;
        }
        if (token is not null) await claim.Commit(token);
        foreach (var (p, v) in stateWrites) J.Set(inst.State, p, v);
        if (inst.OpenStep.TryGetValue(label, out var rec)) rec["delta"] = delta;
    }

    // ================================================================ the binding's own tasks

    static string LabelOf(ExternalJob job, IReadOnlyDictionary<string, string> p, string step)
    {
        if (p.GetValueOrDefault("record") == "finally") return $"finally:{step}";
        if (p.GetValueOrDefault("item") == "true" && Int(job.Variables.GetValueOrDefault("th_item")) is int n) return $"{step}[{n}]";
        return step;
    }

    static void Record(Inst i, IReadOnlyDictionary<string, string> p, string entry)
    {
        if (p.GetValueOrDefault("record") == "detail") i.Detail.Add(entry);
        else i.Trace.Add(entry);
    }

    static string Text(JsonNode? v) => v is null ? "null" : J.AsKey(v) ?? "null";
    static IEnumerable<string> Csv(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Copies contract paths from one instance to another: state paths in the binding, payload paths in the claim check.</summary>
    async Task CopyPaths(Inst from, Inst to, IEnumerable<string> paths)
    {
        var payloadPaths = new List<string>();
        foreach (var w in paths)
        {
            if (ClaimCheckStore.IsStatePath(w))
            {
                var v = J.Get(from.State, w);
                if (v is not null) J.Set(to.State, w, J.Clone(v));
            }
            else payloadPaths.Add(w);
        }
        if (payloadPaths.Count > 0) await claim.Copy(from.Ref, to.Ref, payloadPaths);
    }

    async Task Point(ExternalJob job, BpmnProcess def, IReadOnlyDictionary<string, string> p)
    {
        var vars = new Dictionary<string, JsonNode?>();
        var kind = p.GetValueOrDefault("kind");
        insts.TryGetValue(job.ProcessInstanceId, out var inst);
        if (kind == "processStart") inst = await Register(job, def, inst);
        if (inst is null) { notes.Add($"no instance for {kind} at {job.ElementId}"); await Complete(job, vars); return; }
        if (kind != "end" && inst.Pending.Count > 0) { inst.Trace.AddRange(inst.Pending); inst.Pending.Clear(); }
        switch (kind)
        {
            case "processStart":
                foreach (var b in inst.Bindings) vars[$"th_hook_{Safe(J.S(b, "hookPoint")!)}"] = Safe($"{J.S(b, "hook")}@{J.S(b, "release")}");
                break;
            case "call":
            {
                var step = p["step"];
                var label = LabelOf(job, p, step);
                Record(inst, p, p.GetValueOrDefault("record") == "finally" ? label : $"{label}:{Text(job.Variables.GetValueOrDefault(p["var"]))}");
                break;
            }
            case "slotStart":
            {
                var slot = p["slot"];
                var leg = ChooseLeg(slot, inst);
                var doc = leg is null ? null : dep.Resolve(leg, null, Elapsed);
                var key = doc is null ? null : Safe($"{J.S(doc, "name")}@{J.S(doc, "release")}");
                if (key is not null && byKey.ContainsKey(key))
                {
                    vars[$"th_leg_{Safe(slot)}"] = key;
                    inst.ReleaseTrace.Add($"leg@{J.S(doc, "release")}");
                }
                else notes.Add($"no leg fills slot {slot}{(leg is null ? "" : $" ({leg} is not deployed)")}");
                break;
            }
            case "slot":
            {
                var step = p["step"];
                if (inst.LastChild.TryGetValue($"slot:{step}", out var cid) && insts.TryGetValue(cid, out var child)) await CopyPaths(child, inst, Csv(p.GetValueOrDefault("writes")));
                Record(inst, p, $"{step}:{Text(job.Variables.GetValueOrDefault(p["var"]))}");
                break;
            }
            case "text":
                // A wait from a duration parameter records the value its timer took (R-05-45).
                Record(inst, p, p.GetValueOrDefault("timer") is string tm && lastTimerValue.TryGetValue($"{inst.Pid}|{tm}", out var tv) ? $"{p["prefix"]}:{tv}" : p["text"]);
                break;
            case "blockStart":
                // When the composite step began (R-05-43): the block sub-process this point opens.
                if (def.Ancestry(job.ElementId).Skip(1).FirstOrDefault(e => def.Ann(e, "block") is not null) is string block) inst.BlockStarted[block] = now;
                break;
            case "awaitReceived":
                inst.Awaited.Add(p["message"]);
                Record(inst, p, $"{LabelOf(job, p, p["step"])}:received");
                break;
            case "awaitTimeout":
                inst.Awaited.Add(p["message"]);
                inst.Expired.Add(p["message"]);
                Record(inst, p, $"{LabelOf(job, p, p["step"])}:timeout");
                break;
            case "hookStart":
                inst.NotifyAt[p["hookPoint"]] = inst.Trace.Count;
                inst.Trace.Add("hook:pending");
                break;
            case "hookResult": await HookResult(inst, job, p, vars); break;
            case "forEachStart":
            {
                var items = J.Get(claim.Full(inst.State, inst.Ref), p["in"].Replace("[]", ""));
                vars[p["count"]] = items is JsonArray a ? a.Count : 0;
                break;
            }
            case "end":
            {
                var outcome = Text(job.Variables.GetValueOrDefault("th_outcome") ?? JsonValue.Create("done"));
                inst.Trace.AddRange(inst.Pending);
                inst.Pending.Clear();
                inst.Trace.Add($"end:{outcome}");
                inst.ReleaseTrace.Add($"end:{outcome}");
                inst.Ended = true;
                inst.EndedAt = Elapsed;
                if (outcome == "failed") inst.Failed = inst.LastFatal ?? "failed";
                break;
            }
        }
        await Complete(job, vars);
    }

    async Task Complete(ExternalJob job, Dictionary<string, JsonNode?> vars)
    {
        Mark();
        if (!await rest.Complete(job.Id, WorkerId, vars)) notes.Add($"the binding task {job.ElementId} was gone before it completed");
    }

    /// <summary>A process instance, root or called, reaches its first task: the binding learns of it here.</summary>
    async Task<Inst?> Register(ExternalJob job, BpmnProcess def, Inst? known)
    {
        if (known is not null) { known.Registered = true; return known; }
        var parentPid = Text(job.Variables.GetValueOrDefault("th_parent"));
        if (!insts.TryGetValue(parentPid, out var parent)) { notes.Add($"process {job.ProcessInstanceId} ({def.Key}) has no known caller"); return null; }
        var i = new Inst
        {
            Pid = job.ProcessInstanceId, Label = "child", Parent = parent, Key = def.Key, Ref = $"{job.ProcessInstanceId}#ctx",
            Name = def.Workflow.GetValueOrDefault("name") ?? "?", Release = def.Workflow.GetValueOrDefault("release") ?? "?", Kind = def.Workflow.GetValueOrDefault("kind") ?? "workflow",
            State = (JsonObject)J.Clone(parent.State)!, StartedAt = Elapsed, Bindings = parent.Bindings, Registered = true,
        };
        await claim.Fork(parent.Ref, i.Ref);
        // Where the caller called from (slot:<step> or hook:<point>): the point after the call reads the child there.
        var via = Text(job.Variables.GetValueOrDefault("th_via"));
        if (via != "null") parent.LastChild[via] = i.Pid;
        insts[i.Pid] = i;
        i.ReleaseTrace.Add($"{ShortName(i.Name)}@{i.Release}");
        if (via.StartsWith("hook:")) foreach (var b in parent.Bindings) if ($"hook:{J.S(b, "hookPoint")}" == via) parent.ReleaseTrace.Add($"hook@{J.S(b, "release")}");
        return i;
    }

    /// <summary>The leg of a slot: the one the front door named, else the rail's scheme's, else any leg that implements it.</summary>
    string? ChooseLeg(string slot, Inst inst)
    {
        if (J.S(J.P(built, "legs"), slot) is string named) return named;
        var rail = J.S(J.Get(inst.State, "facts.route"), "rail") ?? J.S(J.P(inst.State, "header"), "scheme");
        var scheme = rail is null ? null : dep.Scheme(rail);
        foreach (var leg in J.Strings(J.P(scheme, "legs")))
        {
            var impl = J.P(dep.Resolve(leg, null, Elapsed), "implements");
            if (J.S(impl, "slot") == slot && J.Strings(J.P(impl, "of")).Contains(inst.Name)) return leg;
        }
        foreach (var d in dep.AllDocs())
        {
            var impl = J.P(d, "implements");
            if (J.S(d, "kind") == "workflow" && J.S(d, "wfKind") == "leg" && J.S(impl, "slot") == slot && J.Strings(J.P(impl, "of")).Contains(inst.Name)) return J.S(d, "name");
        }
        return null;
    }

    /// <summary>The binding enforces the hook point's contract (R-06-07) and maps a failure by the point's policy.</summary>
    async Task HookResult(Inst inst, ExternalJob job, IReadOnlyDictionary<string, string> p, Dictionary<string, JsonNode?> vars)
    {
        var key = p["hookPoint"];
        var name = p["name"];
        var raw = Text(job.Variables.GetValueOrDefault($"th_hookOutcome_{Safe(name)}"));
        if (raw == "null") raw = "failed";
        Inst? child = inst.LastChild.TryGetValue($"hook:{key}", out var cid) ? insts.GetValueOrDefault(cid) : null;
        if (raw != "failed" && child is not null)
        {
            if (child.Failed is not null) raw = "failed";
            else
            {
                var allowed = Csv(p.GetValueOrDefault("writes")).ToList();
                var changes = J.DiffPaths(claim.Full(inst.State, inst.Ref), claim.Full(child.State, child.Ref));
                // A hook's blocks may derive facts, as check L13 allows (R-06-07).
                if (changes.Any(x => !J.CoveredByAny(allowed, x) && !x.StartsWith("facts."))) raw = "failed";
                else if (p.GetValueOrDefault("type") == "transform") await CopyPaths(child, inst, allowed);
            }
        }
        if (p.GetValueOrDefault("type") == "notify")
        {
            var entry = $"hook:{raw}";
            if (inst.NotifyAt.TryGetValue(key, out var at)) inst.Trace[at] = entry;
            else inst.Trace.Add(entry);
            return;
        }
        inst.Trace.Add($"hook:{raw}");
        var eff = raw == "failed" ? (p.GetValueOrDefault("onFailure") == "fail-closed" ? "hold" : "continue") : raw;
        vars[$"th_hookEffect_{Safe(name)}"] = eff;
    }

    // ================================================================ the front door (R-09-16)

    static bool Correlates(Inst i, JsonObject m)
    {
        var h = J.P(i.State, "header");
        if (J.S(m, "conversation") is string conv && conv == J.S(h, "conversation")) return true;
        var c = J.P(m, "correlation");
        foreach (var k in new[] { "paymentId", "endToEndId", "fileId" })
            if (J.S(c, k) is string x && x == J.S(h, k)) return true;
        return false;
    }

    static string DedupeId(JsonObject m) =>
        J.S(m, "inReplyTo") is string r ? $"{J.S(m, "type")}:{r}:{J.S(m, "status") ?? ""}" : J.S(m, "id") ?? "";

    /// <summary>A catch of this instance waiting for this message: of its type and, where the await names one, its family (R-05-44).</summary>
    async Task<JsonObject?> Waiting(Inst i, JsonObject m)
    {
        foreach (var s in await rest.EventSubscriptions(tenant, "message", J.S(m, "type"), i.Pid))
        {
            var def = byDef.GetValueOrDefault(J.S(s, "processDefinitionId") ?? "");
            var family = def?.Ann(J.S(s, "activityId") ?? "", "await")?.GetValueOrDefault("family");
            if (family is null || J.S(m, "family") is not string got || got == family) return s;
        }
        return null;
    }

    /// <summary>
    /// R-05-43: once an earlier await of this instance has waited for this message, a held message received
    /// before the composite step that holds the waiting await began answered the earlier exchange.
    /// </summary>
    bool Stale(Inst i, JsonObject m, JsonObject sub)
    {
        if (!i.Awaited.Contains(J.S(m, "type") ?? "")) return false;
        var def = byDef.GetValueOrDefault(J.S(sub, "processDefinitionId") ?? "");
        var block = def?.Ancestry(J.S(sub, "activityId") ?? "").Skip(1).FirstOrDefault(e => def.Ann(e, "block") is not null);
        var since = block is not null && i.BlockStarted.TryGetValue(block, out var began) ? began : now;
        return receivedAt.TryGetValue(m, out var at) && at < since;
    }

    async Task FrontDoor(JsonObject m)
    {
        var type = J.S(m, "type") ?? "";
        var disposition = J.P(dep.CatalogueEntry(type), "disposition");
        if (J.P(disposition, "handler") is not null || J.P(disposition, "start") is not null)
        {
            if (J.Truthy(J.P(m, "copy"))) { duplicates++; return; }
            notes.Add($"a {(J.P(disposition, "handler") is not null ? "handler" : "start")} message {type} is the front door's, outside the engine binding");
            unmatched++;
            return;
        }
        var candidates = insts.Values.Where(i => Correlates(i, m)).ToList();
        Inst? alive = null;
        foreach (var i in candidates) if (!i.Ended && await Waiting(i, m) is not null) { alive = i; break; }
        alive ??= candidates.LastOrDefault(i => !i.Ended);
        var target = alive ?? candidates.LastOrDefault();
        if (target is null) { unmatched++; return; }
        var id = DedupeId(m);
        if (candidates.Any(c => c.Consumed.Contains(id)) || mailbox.Any(x => DedupeId(x) == id)) { duplicates++; return; }
        if (target.Ended) { unmatched++; return; }
        if (await Waiting(target, m) is { } w) { await Deliver(target, m, J.S(w, "executionId")!); return; }
        if (target.Expired.Contains(type)) { unmatched++; return; }
        // Early: the binding holds the message until a catch is waiting (R-09-16).
        receivedAt[m] = now;
        mailbox.Add(m);
        earlyKept++;
    }

    async Task Deliver(Inst target, JsonObject m, string executionId)
    {
        target.Consumed.Add(DedupeId(m));
        if (J.P(target.State, "messages") is not JsonObject msgs) target.State["messages"] = msgs = new JsonObject();
        if (J.P(msgs, "received") is not JsonObject received) msgs["received"] = received = new JsonObject();
        var reff = new JsonObject { ["id"] = J.S(m, "id"), ["type"] = J.S(m, "type") };
        received[J.S(m, "type")!] = reff;
        msgs["lastReceived"] = J.Clone(reff);
        Mark();
        await rest.MessageReceived(executionId, J.S(m, "type")!);
    }

    async Task<bool> DeliverHeld()
    {
        foreach (var m in mailbox.ToList())
        {
            foreach (var i in insts.Values)
            {
                if (i.Ended || !Correlates(i, m)) continue;
                if (await Waiting(i, m) is not { } w) continue;
                mailbox.Remove(m);
                if (Stale(i, m, w))
                {
                    unmatched++;
                    notes.Add($"a held {J.S(m, "type")} received before its step began is unmatched (R-05-43)");
                    return true;
                }
                await Deliver(i, m, J.S(w, "executionId")!);
                return true;
            }
        }
        return false;
    }

    /// <summary>Flowable 7.1 does not wait for compensation handlers; the binding releases the wait when none is left.</summary>
    async Task<bool> ReleaseCompensation()
    {
        var moved = false;
        foreach (var s in await rest.EventSubscriptions(tenant, "message", "th.compensated"))
        {
            var pid = J.S(s, "processInstanceId")!;
            var def = byDef.GetValueOrDefault(J.S(s, "processDefinitionId") ?? "");
            var busy = (await rest.Executions(pid)).Any(e => J.S(e, "activityId") is string a && def?.UndoOf(a) is not null);
            if (busy) continue;
            Mark();
            await rest.MessageReceived(J.S(s, "executionId")!, "th.compensated");
            moved = true;
        }
        return moved;
    }

    // ================================================================ signals (R-05-12)

    async Task Signal(string signal)
    {
        var root = roots.FirstOrDefault();
        if (root is null || root.Ended) return;
        string? aw = null;
        foreach (var s in await rest.EventSubscriptions(tenant, "message", null, root.Pid))
        {
            var def = byDef.GetValueOrDefault(J.S(s, "processDefinitionId") ?? "");
            if (def?.Ann(J.S(s, "activityId") ?? "", "await") is { } a) { aw = a["step"]; break; }
        }
        if (signal == "pause")
        {
            if (aw is not null) { root.Trace.Add($"{aw}:waiting"); root.Trace.Add("signal:pause"); }
            else root.Pending.Add("signal:pause");
        }
        else
        {
            if (aw is not null) root.Trace.Add($"{aw}:waiting");
            root.Trace.AddRange(root.Pending);
            root.Pending.Clear();
            root.Trace.Add($"signal:{signal}");
        }
        var subs = await rest.EventSubscriptions(tenant, "message", SignalMessages.GetValueOrDefault(signal) ?? signal, root.Pid);
        if (subs.Count == 0) { notes.Add($"no handler waits for signal {signal}"); return; }
        Mark();
        await rest.MessageReceived(J.S(subs[0], "executionId")!, SignalMessages[signal]);
    }

    // ================================================================ the observation

    JsonObject Observe(Inst i)
    {
        var att = new JsonObject();
        foreach (var (k, n) in i.Attempts) att[k] = n;
        return new JsonObject
        {
            ["label"] = i.Label,
            ["trace"] = J.Arr(i.Trace),
            ["detail"] = J.Arr(i.Detail),
            ["releaseTrace"] = J.Arr(i.ReleaseTrace),
            ["steps"] = J.Clone(i.Steps),
            ["attempts"] = att,
            ["ruleTraceSteps"] = J.Arr(i.RuleTraceSteps),
            ["engineStateKeys"] = J.Arr(i.StateKeys),
            ["startedAt"] = i.StartedAt,
            ["endedAt"] = i.EndedAt,
            ["failed"] = i.Failed,
            ["ctx"] = claim.Full(i.State, i.Ref),
        };
    }
}
