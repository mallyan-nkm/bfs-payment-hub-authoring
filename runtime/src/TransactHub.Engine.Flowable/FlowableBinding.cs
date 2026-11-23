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
/// Time has two modes (<see cref="FlowableMode"/>). Virtual: the server's async executor is off, so the
/// binding runs the jobs it would run, and fires a timer when the binding's virtual clock reaches it; a
/// wait of days takes no wall time (the kit and the tests). Production: the executor is on and time is
/// real; the binding is a pure external worker, Flowable runs its own jobs, fires its timers, retries a
/// failed job and hands out a job whose lock expired, and the binding surfaces every dead letter.
/// The plan and the observation are those of the other engines' harnesses, so the kit runner and the
/// studio's deploy runner read them unchanged.
/// </summary>
public sealed partial class FlowableBinding
{
    public const string WorkerId = "th-flowable-binding";
    public const string BindingTopic = "th.binding";
    static readonly Dictionary<string, string> SignalMessages = new() { ["pause"] = "th.signal.pause", ["resume"] = "th.signal.resume", ["cancel"] = "th.signal.cancel", ["re-screen"] = "th.signal.re-screen" };

    readonly JsonObject input, built, spec, config;
    readonly FlowableRest rest;
    readonly FlowableOptions opts;
    /// <summary>The worker id the binding locks jobs with; a new one after a crash (test fault), as a restarted worker would have.</summary>
    string workerId = WorkerId;
    /// <summary>Production: business time is the wall clock plus this offset, so a run starts at the plan's evaluation date and start time.</summary>
    long offset, wallStart;
    bool crashed, executorWarned;
    int idle;
    /// <summary>Every job the binding has begun; a job handed out again (a failure, an expired lock) is the same attempt.</summary>
    readonly HashSet<string> begun = new(), hung = new();
    /// <summary>Production: timers the binding has placed, by job id, and the due date it gave each execution's timer.</summary>
    readonly HashSet<string> placed = new();
    readonly Dictionary<string, long> placedDue = new();
    /// <summary>Production: the executions whose attempt timer the binding set when it began the attempt (R-05-54).</summary>
    readonly HashSet<string> armed = new();
    int attemptTimersParked, attemptTimersArmed;
    readonly List<JsonObject> deadLetters = new();
    readonly HashSet<string> deadSeen = new();
    int timersPlaced, handedOutAgain, crashes, pendingStarts;
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

    /// <summary>What the binding has scheduled: data only, so that it can be kept in the journal (Issue I-89).</summary>
    sealed record Ev(long At, string Kind, JsonObject Data);

    sealed partial class Inst
    {
        public string Pid = "", Label = "", Name = "?", Release = "?", Kind = "workflow", Key = "", Ref = "";
        public Inst? Parent;
        public JsonObject State = new();
        readonly List<string> trace = new(), detail = new();
        public readonly List<string> ReleaseTrace = new(), Pending = new(), RuleTraceSteps = new();
        /// <summary>
        /// What the items of a forEach recorded, by item, while they ran side by side (R-05-53): each item's entries go into the
        /// trace together, in item order, before anything else the instance records.
        /// </summary>
        public readonly SortedDictionary<int, List<(bool detail, string entry)>> Items = new();
        /// <summary>The first item that stopped the forEach (R-05-53, Issue I-107): no entry of a later item reaches the trace.</summary>
        public int StopAt = int.MaxValue;
        void FlushItems()
        {
            if (Items.Count == 0) return;
            foreach (var (n, es) in Items) if (n <= StopAt) foreach (var (d, e) in es) (d ? detail : trace).Add(e);
            Items.Clear();
            StopAt = int.MaxValue;
        }
        public List<string> Trace { get { FlushItems(); return trace; } }
        public List<string> Detail { get { FlushItems(); return detail; } }
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
        public string? Failed, LastFatal, DeadLetter, DeadLetterJob;
        public List<JsonObject> Bindings = new();
    }

    readonly IParty? given;

    /// <param name="party">Who answers the attempts; by default the plan's workers (a deployment) or the studio's party service (a kit run).</param>
    /// <param name="options">Virtual (the default) or production mode, and its timings.</param>
    public FlowableBinding(JsonObject input, FlowableRest rest, IParty? party = null, FlowableOptions? options = null)
    {
        this.input = input;
        this.rest = rest;
        opts = options ?? new FlowableOptions();
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
        if (opts.Production) return; // the server's own clock places its timers
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

    void Schedule(long at, string kind, JsonObject data) => queue.Enqueue(new Ev(at, kind, data), (at, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), seq++));

    /// <summary>Runs what was scheduled: a root's start, a party's message, a signal, or an attempt's answer after its latency.</summary>
    async Task RunEv(Ev ev)
    {
        var d = ev.Data;
        switch (ev.Kind)
        {
            case "start":
                pendingStarts--;
                await StartRoot(J.S(d, "label")!, J.S(d, "workflow")!, J.S(d, "release")!, (JsonObject)J.Clone(J.P(d, "ctx"))!);
                break;
            case "delivery": await FrontDoor((JsonObject)J.Clone(J.P(d, "message"))!); break;
            case "signal": await Signal(J.S(d, "signal")!); break;
            case "complete":
            {
                var job = JobOf((JsonObject)J.P(d, "job")!);
                if (!insts.TryGetValue(job.ProcessInstanceId, out var inst) || !byDef.TryGetValue(job.ProcessDefinitionId, out var def)) { notes.Add($"a late answer for an unknown instance {job.ProcessInstanceId}"); break; }
                int? index = J.Num(d, "index") is double ix ? (int)ix : null;
                await Finish(job, inst, J.S(d, "step")!, J.S(d, "label")!, index, def.Ann(job.ElementId, "task")!, (JsonObject)J.Clone(J.P(d, "res"))!);
                break;
            }
            default: notes.Add($"an event of an unknown kind {ev.Kind}"); break;
        }
    }

    static long Wall() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Production: business time follows the wall clock.</summary>
    void Tick()
    {
        if (opts.Production) now = Math.Max(now, Wall() + offset);
    }

    void Deliveries(JsonNode? ds)
    {
        foreach (var d in ds as JsonArray ?? new JsonArray())
        {
            var m = (JsonObject)J.Clone(J.P(d, "message"))!;
            var at = (long)(J.Num(d, "at") ?? now);
            // A simulator's reply delay, scaled in production as the stub's --time-scale scales it.
            if (opts.Production && opts.SimScale != 1 && at > now) at = now + (long)Math.Round((at - now) * opts.SimScale);
            Schedule(at, "delivery", new JsonObject { ["message"] = m });
        }
    }

    // ================================================================ the run

    public async Task<JsonObject> Run()
    {
        var storeOpts = StoreFromEnvironment();
        if (storeOpts is not null)
        {
            await using var db = StoreDbContext.Open(storeOpts);
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database);
            StoreSignals.Install(storeOpts, StepFacts.Install(storeOpts));
        }
        claim = storeOpts is null ? new MemoryClaimCheck() : new DurableClaimCheck(storeOpts);
        // The binding's state as facts in the store (Issue I-89): a binding that restarts with the same run key goes on from them.
        var runKey = J.S(input, "run") ?? Environment.GetEnvironmentVariable("TH_FLOWABLE_RUN");
        if (storeOpts is not null && !string.IsNullOrWhiteSpace(runKey)) journal = new BindingJournal(storeOpts, runKey);
        var saved = journal is null ? null : await journal.Last();
        if (saved is not null && J.S(saved, "closed") is string closedAt) throw new InvalidOperationException($"run {runKey} ended at {closedAt}; a new run needs a new key (TH_FLOWABLE_RUN)");
        var resumed = saved is not null;
        if (resumed) Restore(saved!);
        else
        {
            runId = Guid.NewGuid().ToString("N")[..8];
            tenant = $"th-{runId}";
            start = DateTimeOffset.Parse($"{J.S(config, "evaluationDate")}T{J.S(config, "startTime") ?? "06:00:00"}Z").ToUnixTimeMilliseconds();
            now = start;
            wallStart = Wall();
            offset = start - wallStart;
        }
        dep = new Deployment((JsonObject)J.P(input, "documents")!, config, start, (int)(J.Num(input, "chunkSize") ?? 100)) { ItemConcurrency = (int)(J.Num(input, "itemConcurrency") ?? 1) };
        Deployment.Current = dep;
        using var p = given is not null ? null : J.P(input, "workers") is JsonObject workers ? (IParty)new WorkerRouter(workers, (JsonObject)J.P(input, "queues")!, J.S(input, "distribution")) : new PartyClient(J.S(input, "studioRoot")!);
        party = given ?? p!;

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
        // One deployment per distribution (R-09-55): its name carries the distribution, and instances start from its own definitions.
        var distribution = J.S(input, "distribution");
        if (!resumed)
        {
            log.Add($"Flowable {J.S(server, "version")} at the REST server, tenant {tenant}");
            log.Add(opts.Production
                ? $"production mode: the server's async executor runs the jobs and fires the timers in real time; the binding is its external worker (lock {System.Xml.XmlConvert.ToString(opts.LockFor)}, retry after {System.Xml.XmlConvert.ToString(opts.RetryWait)})"
                : "virtual mode: the server's async executor is off; the binding runs the jobs and fires the timers on its clock");
            deploymentId = await rest.Deploy(distribution is null ? $"transacthub-{runId}" : $"transacthub-{distribution}-{runId}", files, tenant);
            log.Add($"deployed {files.Count} process(es) as deployment {deploymentId}");
        }
        foreach (var d in await rest.ProcessDefinitions(deploymentId))
            if (byKey.TryGetValue(J.S(d, "key")!, out var proc)) byDef[J.S(d, "id")!] = proc;
        var missing = byKey.Keys.Where(k => !byDef.Values.Any(v => v.Key == k)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"The server did not deploy {string.Join(", ", missing)}");
        if (resumed) await Resume(saved!);

        try
        {
            await party.Call(new JsonObject { ["op"] = "open", ["scenario"] = J.S(built, "id") });
            if (!resumed)
            {
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
                        pendingStarts++;
                        Schedule(start + Duration.Ms(J.S(s, "at") ?? "PT0S"), "start", new JsonObject { ["label"] = label, ["workflow"] = workflow, ["release"] = release, ["ctx"] = J.Clone(ctx) });
                    }
                }
                else
                {
                    var doc = dep.Resolve(workflow, null, 0) ?? throw new InvalidOperationException($"{workflow} does not resolve");
                    pendingStarts++;
                    Schedule(start, "start", new JsonObject { ["label"] = "root", ["workflow"] = workflow, ["release"] = J.S(doc, "release"), ["ctx"] = J.Clone(ctx) });
                }
                foreach (var s in J.A(config, "signals"))
                {
                    var signal = J.S(s, "signal")!;
                    Schedule(start + Duration.Ms(J.S(s, "after")!), "signal", new JsonObject { ["signal"] = signal });
                }
                await Save(null);
            }
            if (opts.Production) await LiveLoop(); else await Loop();
            await SurfaceDeadLetters();
            // The run has ended: its journal is closed, and a binding started with its key refuses to run it again.
            if (journal is not null) await journal.Close(new JsonObject { ["roots"] = J.Arr(roots.Select(r => r.Pid)), ["ended"] = roots.All(r => r.Ended), ["restarts"] = restarts });

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
                    ["mode"] = opts.Production ? "production" : "virtual",
                    ["deployment"] = deploymentId,
                    ["restarts"] = restarts,
                    ["journal"] = journal is null ? null : new JsonObject { ["run"] = journal.Key, ["facts"] = journal.Written },
                    ["distribution"] = distribution is null ? null : new JsonObject { ["id"] = distribution, ["deploymentName"] = $"transacthub-{distribution}-{runId}" },
                    ["processes"] = byDef.Count,
                    ["processInstances"] = insts.Count,
                    ["jobsWorked"] = jobsWorked,
                    ["bindingJobs"] = bindingJobs,
                    ["asyncJobs"] = asyncJobs,
                    ["timersFired"] = timersFired,
                    ["lateResultsAbandoned"] = lateAbandoned,
                    ["retriedOnFailure"] = retriedOnFailure,
                    ["handedOutAgain"] = handedOutAgain,
                    ["crashes"] = crashes,
                    ["timersPlaced"] = timersPlaced,
                    ["attemptTimersParked"] = attemptTimersParked, ["attemptTimersArmed"] = attemptTimersArmed,
                    ["deadLetters"] = new JsonArray(deadLetters.Select(d => (JsonNode?)J.Clone(d)).ToArray()),
                    ["wallMs"] = Wall() - wallStart,
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
        Tick();
        await Save(new JsonObject { ["op"] = "start", ["label"] = label, ["workflow"] = workflow, ["release"] = release, ["ctx"] = J.Clone(ctx) });
        Mark();
        var started = await rest.StartProcess(key, tenant, businessKey: $"{label}-{tenant}");
        await RegisterRoot(label, workflow, release, ctx, J.S(started, "id")!);
    }

    /// <summary>A root the server has started: the process waits at its first binding task (processStart); it is registered here.</summary>
    async Task RegisterRoot(string label, string workflow, string release, JsonObject ctx, string pid)
    {
        var key = Safe($"{workflow}@{release}");
        var (state, payload) = ClaimCheckStore.Split(ctx);
        var header = J.P(state, "header");
        var docKey = $"{workflow}@{release}";
        var i = new Inst
        {
            Pid = pid, Label = label, Name = workflow, Release = release, Key = key, Ref = $"{pid}#ctx", State = state, StartedAt = Elapsed,
            Bindings = dep.BindingsAt(Duration.Today(now)), // hook bindings are taken when the instance starts (R-06-13)
        };
        var direction = J.S(header, "direction") == "inward" ? TransactHub.Model.Direction.Inward : TransactHub.Model.Direction.Outward;
        await claim.Create(i.Ref, payload, new AcceptInfo(J.S(input, "tenant") ?? J.S(config, "tenant") ?? "tenant", J.S(header, "scheme") ?? workflow.Split('.')[0], direction, J.S(input, "distribution") ?? docKey));
        insts[pid] = i;
        roots.Add(i);
        i.ReleaseTrace.Add($"{ShortName(workflow)}@{release}");
        log.Add($"started {docKey} as {pid}");
    }

    /// <summary>
    /// An instance as the workers and the store know it: its root's process instance, then each child's, as the interpreter's
    /// instance ids are (root/leg). A leg's or a hook's steps are then the payment's facts, found by the root's payload (R-05-50).
    /// </summary>
    static string PathOf(Inst i) => i.Parent is null ? i.Pid : $"{PathOf(i.Parent)}/{i.Pid}";

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
            if (await PollLive()) await Settle();
            if (liveFeed is not null && await HoldClock())
            {
                if (queue.TryPeek(out var due, out var dk) && dk.at <= now) { queue.Dequeue(); await RunEv(due); await Settle(); continue; }
                await Task.Delay(250);
                continue;
            }
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
                timersFired++;
                await MoveTimerNow(J.S(timer, "id")!);
            }
            else
            {
                queue.Dequeue();
                await RunEv(next!);
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

    // ================================================================ production: real time, the server's executor

    /// <summary>
    /// Production mode. Flowable's async executor runs the jobs and fires the timers; the binding only works
    /// the external jobs, places the timers it computes, delivers held messages, and surfaces dead letters,
    /// until every payment has ended (or stopped at a dead letter), or the run's time is up.
    /// </summary>
    async Task LiveLoop()
    {
        var until = wallStart + (long)opts.MaxWait.TotalMilliseconds;
        for (;;)
        {
            Tick();
            var moved = false;
            while (queue.TryPeek(out var ev, out var k) && k.at <= now)
            {
                queue.Dequeue();
                await RunEv(ev);
                moved = true;
                Tick();
            }
            moved |= await PollLive();
            moved |= await PlaceTimers();
            moved |= await WorkExternalJobs();
            moved |= await DeliverHeld();
            moved |= await ReleaseCompensation();
            await SurfaceDeadLetters();
            // In production with the runtime's live path, a dead-lettered payment waits for an operator's requeue (R-09-91), up to the wait.
            if (pendingStarts == 0 && roots.All(r => (r.DeadLetter is not null && !(opts.Production && LiveSignals.Source is not null)) || r.Ended) && await Finished())
            {
                // What is still queued can only meet a payment that has ended: it is counted as the virtual run counts it.
                while (queue.TryDequeue(out var ev, out _)) await RunEv(ev);
                return;
            }
            if (Wall() > until)
            {
                notes.Add($"stopped after {System.Xml.XmlConvert.ToString(opts.MaxWait)} of real time with {roots.Count(r => !r.Ended)} payment(s) still running (TH_FLOWABLE_MAX_WAIT)");
                return;
            }
            if (moved) { idle = 0; continue; }
            if (++idle % 120 == 0) await CheckExecutor();
            var wait = (long)opts.Poll.TotalMilliseconds;
            if (queue.TryPeek(out _, out var nk)) wait = Math.Min(wait, Math.Max(0, nk.at - now));
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(5, wait)));
        }
    }

    /// <summary>Every root that ended has left the server: its finally, its notify hooks and its children are done.</summary>
    async Task<bool> Finished()
    {
        foreach (var r in roots.Where(r => r.Ended && r.DeadLetter is null))
            if ((await rest.Executions(r.Pid)).Count > 0) return false;
        return true;
    }

    /// <summary>An executable job nobody runs for a long while: the server's async executor is off, and production mode needs it on.</summary>
    async Task CheckExecutor()
    {
        if (executorWarned) return;
        var old = (await rest.Jobs(tenant)).Where(j => Wall() - FlowableRest.Time(J.S(j, "createTime")).ToUnixTimeMilliseconds() > 30_000).ToList();
        if (old.Count == 0) return;
        executorWarned = true;
        notes.Add($"{old.Count} executable job(s) have waited more than 30 s: production mode needs the server's async executor on (FLOWABLE_ASYNC_EXECUTOR_ACTIVATE=true)");
    }

    /// <summary>
    /// Production mode: a timer on the business calendar, or whose duration or calendar is a parameter, was
    /// written with the model's default (Issue I-55, R-05-45, R-05-47). When the server creates it, the
    /// binding computes its due date as the virtual mode would (the tenant's value in force on the date it
    /// was created, counted on the business calendar where it says so) and reschedules it to that date.
    /// Every other timer is fired by Flowable as written.
    /// </summary>
    async Task<bool> PlaceTimers()
    {
        var moved = false;
        foreach (var t in await rest.TimerJobs(tenant))
        {
            var id = J.S(t, "id")!;
            if (!placed.Add(id)) continue;
            var element = J.S(t, "elementId") ?? "";
            var def = byDef.GetValueOrDefault(J.S(t, "processDefinitionId") ?? "");
            // An attempt's timeout measures that attempt's own execution (R-05-54): Flowable starts its timer when the task is
            // created, so time in the queue before the binding takes the job would count. It is moved out of the way here and
            // set from when the binding begins the attempt (ArmAttemptTimer).
            if (IsAttemptTimer(def, element))
            {
                if (armed.Contains(J.S(t, "executionId") ?? "")) continue;
                if (FlowableRest.Time(J.S(t, "dueDate")).ToUnixTimeMilliseconds() - Wall() < TimeSpan.FromHours(12).TotalMilliseconds
                    && await Reschedule(id, DateTimeOffset.FromUnixTimeMilliseconds(Wall() + (long)TimeSpan.FromDays(1).TotalMilliseconds)))
                {
                    attemptTimersParked++;
                    moved = true;
                }
                continue;
            }
            var meta = def?.Ann(element, "timer");
            if (meta is null || !(meta.ContainsKey("parameter") || meta.ContainsKey("calendarParameter") || meta.GetValueOrDefault("calendar") == "business")) continue;
            var due = FlowableRest.Time(J.S(t, "dueDate")).ToUnixTimeMilliseconds();
            var where = $"{J.S(t, "executionId")}|{element}";
            // The timer the binding rescheduled comes back as a new job with the due date it was given.
            if (placedDue.TryGetValue(where, out var given) && Math.Abs(given - due) < 1000) continue;
            var created = FlowableRest.Time(J.S(t, "createTime")).ToUnixTimeMilliseconds();
            var vdue = VirtualDue(t, created + offset, due - created);
            var wallDue = vdue - offset;
            if (Math.Abs(wallDue - due) < 1000) continue;
            placedDue[where] = wallDue;
            if (await Reschedule(id, DateTimeOffset.FromUnixTimeMilliseconds(wallDue)))
            {
                timersPlaced++;
                moved = true;
                log.Add($"timer {element} placed at {Duration.Iso(vdue)} (business time), {Duration.Iso(wallDue)} on the server; written {Duration.Iso(due + offset)}");
            }
        }
        return moved;
    }

    /// <summary>The timer boundary of an attempt (studio/src/export/bpmn.ts: <c>{sid}_timeout</c> on <c>{sid}_task</c>).</summary>
    static bool IsAttemptTimer(BpmnProcess? def, string element) =>
        def is not null && element.EndsWith("_timeout", StringComparison.Ordinal) && def.Ann(element[..^"_timeout".Length] + "_task", "task") is not null;

    /// <summary>
    /// Production: the attempt's timer, set from now for the attempt timeout as written (R-05-54). Its boundary execution is the
    /// sibling of the task's, under the same parent; with one candidate in the instance, that one.
    /// </summary>
    async Task ArmAttemptTimer(ExternalJob job, BpmnProcess def)
    {
        if (!job.ElementId.EndsWith("_task", StringComparison.Ordinal)) return;
        var element = job.ElementId[..^"_task".Length] + "_timeout";
        if (def.DurationOf(element) is not string iso || !iso.StartsWith('P')) return;
        var timers = (await rest.TimerJobsOf(job.ProcessInstanceId)).Where(t => J.S(t, "elementId") == element).ToList();
        if (timers.Count > 1)
        {
            var execs = (await rest.Executions(job.ProcessInstanceId)).ToDictionary(e => J.S(e, "id") ?? "", e => J.S(e, "parentId"));
            var parent = execs.GetValueOrDefault(job.ExecutionId);
            timers = timers.Where(t => execs.GetValueOrDefault(J.S(t, "executionId") ?? "") == parent).ToList();
        }
        if (timers.Count != 1) return;
        var exec = J.S(timers[0], "executionId") ?? "";
        armed.Add(exec);
        if (await Reschedule(J.S(timers[0], "id")!, DateTimeOffset.FromUnixTimeMilliseconds(Wall() + Duration.Ms(iso)))) attemptTimersArmed++;
    }

    /// <summary>
    /// Every dead letter of this run's tenant: a job whose retries are spent. It is reported once, with its
    /// step, and the payment it stops is marked: an operator moves it back (retry) or ends the payment.
    /// </summary>
    async Task SurfaceDeadLetters()
    {
        var all = await rest.DeadLetterJobs(tenant);
        foreach (var d in all)
        {
            var id = J.S(d, "id")!;
            if (!deadSeen.Add(id)) continue;
            var pid = J.S(d, "processInstanceId") ?? "";
            var element = J.S(d, "elementId") ?? "";
            var def = byDef.GetValueOrDefault(J.S(d, "processDefinitionId") ?? "");
            var task = def?.Ann(element, "task");
            var step = task?.GetValueOrDefault("step") ?? element;
            var message = J.S(d, "exceptionMessage") ?? "";
            insts.TryGetValue(pid, out var inst);
            var root = inst;
            while (root?.Parent is not null) root = root.Parent;
            deadLetters.Add(new JsonObject
            {
                ["job"] = id, ["processInstance"] = pid, ["element"] = element, ["step"] = step, ["activity"] = task?.GetValueOrDefault("activity"),
                ["workflow"] = inst?.Name, ["payment"] = root?.Label, ["message"] = message, ["at"] = Elapsed,
            });
            notes.Add($"dead letter: the job of {step} ({element}) in {inst?.Name ?? pid} has no retries left: {message}. An operator moves it back to retry, or ends the payment");
            if (root is not null)
            {
                root.DeadLetter ??= $"{step}: {message}";
                root.DeadLetterJob ??= id;
                // A dead-letter job is the payment's dead-lettered step, as a parked step is on the other engines (R-09-91).
                try { if (inst is not null) Interp.Stuck?.Invoke(PathOf(inst), step, inst.Attempts.GetValueOrDefault(step)); } catch (Exception) { }
                root.Failed ??= $"dead letter at {step}: {message}";
            }
        }
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
        foreach (var t in topics) all.AddRange(await rest.Acquire(t, 1000, opts.LockFor, workerId, tenant));
        all.Sort((a, b) => a.CreateTime != b.CreateTime ? a.CreateTime.CompareTo(b.CreateTime) : string.CompareOrdinal(a.Id, b.Id));
        string? last = null;
        foreach (var j in all)
        {
            // Production: the server's executor runs each completion on a thread of its own, so two completions
            // of one instance (parallel branches) could create what follows them in either order. The binding
            // lets the first take effect before it works the next, so the jobs that follow come in the order the
            // virtual run gives them, and so does the trace.
            if (opts.Production && last == j.ProcessInstanceId) await Quiet(j.ProcessInstanceId);
            await Work(j);
            last = j.ProcessInstanceId;
        }
        return all.Count > 0;
    }

    /// <summary>Production: until the instance has no executable job, that is until the server has run what the binding's last completion started.</summary>
    async Task Quiet(string pid)
    {
        for (var until = Wall() + 30_000; Wall() < until; await Task.Delay(20))
            if ((await rest.Jobs(tenant, pid)).Count == 0) return;
        notes.Add($"instance {pid}: the server did not run its jobs within 30 s");
    }

    async Task Work(ExternalJob job)
    {
        Tick();
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
        if (opts.CrashAt == step && !crashed)
        {
            // Test fault: the worker took the job and stopped before it called anyone, holding the lock. The job
            // comes back only when Flowable finds the lock expired; the binding goes on as a restarted worker.
            crashed = true;
            crashes++;
            workerId = $"{WorkerId}-restarted";
            log.Add($"{label}: the worker stopped holding job {job.Id} (TH_FLOWABLE_FAULT); its lock expires after {System.Xml.XmlConvert.ToString(opts.LockFor)}, and the binding goes on as {workerId}");
            return;
        }
        // A job handed out again, after a technical failure or an expired lock, is the attempt it was.
        var first = begun.Add(job.Id);
        if (!first) handedOutAgain++;
        if (hung.Contains(job.Id)) return; // an attempt that never answers stays unanswered when its job comes back
        if (attempt == 1 && first) inst.Calls[step] = inst.Calls.GetValueOrDefault(step) + 1;
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
        if (attempt == 1 && first)
        {
            if (def.UndoOf(job.ElementId) is { } undo) inst.Trace.Add($"undo:{undo["step"]}");
            if (!silent) inst.ReleaseTrace.Add($"{ShortName(activity)}@{runRelease}");
            var rec = new JsonObject { ["step"] = label, ["release"] = runRelease, ["startedAt"] = Elapsed, ["policy"] = J.Parse(task.GetValueOrDefault("policy") ?? "{}"), ["delta"] = new JsonArray() };
            if (task.TryGetValue("sources", out var src)) rec["policySources"] = J.Parse(src);
            inst.Steps.Add(rec);
            inst.OpenStep[label] = rec;
        }
        if (first) inst.Attempts[step] = inst.Attempts.GetValueOrDefault(step) + 1;
        if (opts.Production && first) await ArmAttemptTimer(job, def);
        if (inst.Snapshots++ < 200) foreach (var k in job.Variables.Keys) inst.StateKeys.Add(k);
        var level = task.GetValueOrDefault("level");
        var ctx = index is int ix && level == "item" ? claim.FullForItem(inst.State, inst.Ref, ix) : claim.Full(inst.State, inst.Ref);
        var req = new JsonObject
        {
            ["op"] = "attempt", ["instance"] = PathOf(inst), ["workflow"] = inst.Name, ["activity"] = activity, ["release"] = runRelease, ["step"] = step,
            ["call"] = call, ["attempt"] = attempt, ["now"] = now, ["ctx"] = ctx, ["level"] = index is not null && level == "item" ? "item" : null,
            ["policy"] = task.TryGetValue("policy", out var pol) ? J.Parse(pol) : null, ["item"] = index,
        };
        // A worker that does not answer is a technical failure, not an attempt: the job's retries cover it.
        JsonObject? res = null;
        // A job the binding had answered before it restarted (Issue I-89) is answered from the journal, not called again.
        var reused = answered.TryGetValue(job.Id, out var kept);
        if (reused)
        {
            // Its answer waits for the stub's latency in the binding's schedule: that completes it.
            if (queue.UnorderedItems.Any(x => x.Element.Kind == "complete" && J.S(J.P(x.Element.Data, "job"), "id") == job.Id)) return;
            res = (JsonObject)J.Clone(kept)!;
            log.Add($"{label}: job {job.Id} handed out again after the restart; answered from the journal, not called again");
        }
        else if (opts.Production)
        {
            // Production: Flowable's own retry. The job is failed with one retry fewer; the server hands it out
            // again once its retry timeout has passed, and with none left it is a dead letter.
            try { res = await CallParty(req, step); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                retriedOnFailure++;
                var left = Math.Max(0, job.Retries - 1);
                if (left == 0) inst.Attempts[step]--;
                log.Add(left > 0
                    ? $"{label}: {e.Message}; failed with {left} retr{(left == 1 ? "y" : "ies")} left, Flowable hands it out again after {System.Xml.XmlConvert.ToString(opts.RetryWait)}"
                    : $"{label}: {e.Message}; failed with no retries left: a dead letter");
                await FailJob(job.Id, e.Message, left, left > 0 ? opts.RetryWait : TimeSpan.Zero);
                return;
            }
        }
        else
        {
            // Virtual: Flowable hands a failed job out again only when its async executor resets the expired
            // lock, and that executor is off (the binding keeps the clock), so the binding retries the call itself
            // while the job's retries last, and fails the job with none left when they are spent: a dead letter.
            for (var left = Math.Max(1, job.Retries); res is null; left--)
            {
                try { res = await CallParty(req, step); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    retriedOnFailure++;
                    if (left > 1) { await Task.Delay(TimeSpan.FromMilliseconds(250 * (job.Retries - left + 1))); continue; }
                    inst.Attempts[step]--;
                    notes.Add($"{label}: {e.Message}; no retries left, the job is a dead letter");
                    await FailJob(job.Id, e.Message, 0, TimeSpan.Zero);
                    return;
                }
            }
        }
        if (!reused)
        {
            Deliveries(J.P(res, "deliveries"));
            if (J.S(res, "kind") == "hang") hung.Add(job.Id);
            // The answer, before anything follows from it (Issue I-89): a binding that restarts from here does not call again.
            answered[job.Id] = (JsonObject)J.Clone(res)!;
            await Save(null);
            await ExitIfAsked(step);
        }
        if (J.S(res, "kind") == "hang") return; // the attempt never answers; the timer boundary ends it
        if (J.S(res, "kind") == "held")
        {
            // A hold opened its item: the instance awaits the operator's decision until the hold's expiry (Issue I-98, R-05-56).
            // On the bank's business calendar when the hold says so (R-09-88): the timer waits until the expiry so counted.
            var expiry = J.S(res, "expiry") ?? "P1D";
            if (J.S(res, "expiryCalendar") == "business") expiry = Duration.Seconds(Duration.Add(now, expiry, BusinessCalendar.Of(J.P(res, "calendar")) ?? BusinessCalendar.Default) - now);
            await CompleteJob(job.Id, new Dictionary<string, JsonNode?> { [$"outcome_{Safe(step)}"] = "held", [$"th_expiry_{Safe(step)}"] = expiry });
            return;
        }
        var latency = J.S(dep.Stub(step, activity), "latency") is string l ? Duration.Ms(l) : 0;
        if (latency > 0) Schedule(now + latency, "complete", new JsonObject { ["job"] = JobJson(job), ["step"] = step, ["label"] = label, ["index"] = index, ["res"] = J.Clone(res) });
        else await Finish(job, inst, step, label, index, task, res);
    }

    /// <summary>The party's answer to an attempt; a step whose worker is down (test fault) never answers.</summary>
    Task<JsonObject> CallParty(JsonObject req, string step) =>
        opts.DownAt == step ? throw new HttpRequestException($"the worker of {step} does not answer (TH_FLOWABLE_FAULT)") : party.Call(req);

    static int? Int(JsonNode? n) => J.D(n) is double d ? (int)d : n is JsonValue v && int.TryParse(J.AsKey(v), out var i) ? i : null;

    async Task Finish(ExternalJob job, Inst inst, string step, string label, int? index, IReadOnlyDictionary<string, string> task, JsonObject res)
    {
        if (J.S(res, "kind") == "error")
        {
            var retryable = J.Truthy(J.P(res, "retryable"));
            var name = J.S(res, "name") ?? "Error";
            inst.LastFatal = $"{task["activity"]} failed: {J.S(res, "message") ?? name}";
            if (!await ErrorJob(job.Id, retryable ? name : "th:nonRetryable")) lateAbandoned++;
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
        var token = staged.Count > 0 ? await claim.StageAs(inst.Ref, staged, job.Id) : null;
        var vars = new Dictionary<string, JsonNode?>();
        if (task.GetValueOrDefault("record") != "silent") vars[$"outcome_{Safe(step)}"] = J.S(res, "outcome");
        foreach (var (k, v) in J.Obj(J.P(res, "decisions")))
            vars[$"decision_{Safe(k)}"] = v is JsonValue ? J.AsKey(v) : J.Str(v);
        var after = new JsonObject
        {
            ["token"] = token, ["pid"] = inst.Pid, ["label"] = label, ["delta"] = J.Clone(delta),
            ["stateWrites"] = new JsonArray(stateWrites.Select(w => (JsonNode?)new JsonObject { ["path"] = w.path, ["value"] = J.Clone(w.value) }).ToArray()),
        };
        if (!await CompleteJob(job.Id, vars, after))
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

    static void Record(Inst i, IReadOnlyDictionary<string, string> p, string entry, ExternalJob? job = null)
    {
        var detail = p.GetValueOrDefault("record") == "detail";
        // An item's entry waits with its item's others, so the trace is in item order (R-05-53).
        if (job is not null && p.GetValueOrDefault("item") == "true" && Int(job.Variables.GetValueOrDefault("th_item")) is int n)
        {
            if (!i.Items.TryGetValue(n, out var es)) i.Items[n] = es = new();
            es.Add((detail, entry));
            return;
        }
        if (detail) i.Detail.Add(entry);
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
        Tick();
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
                Record(inst, p, p.GetValueOrDefault("record") == "finally" ? label : $"{label}:{Text(job.Variables.GetValueOrDefault(p["var"]))}", job);
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
            case "itemStop":
                // The item that stops a forEach whose items run side by side (R-05-53): a later item's entries are dropped.
                if (Int(job.Variables.GetValueOrDefault("th_item")) is int stopped) inst.StopAt = Math.Min(inst.StopAt, stopped);
                break;
            case "blockStart":
                // When the composite step began (R-05-43): the block sub-process this point opens.
                if (def.Ancestry(job.ElementId).Skip(1).FirstOrDefault(e => def.Ann(e, "block") is not null) is string block) inst.BlockStarted[block] = now;
                break;
            case "awaitReceived":
                inst.Awaited.Add(p["message"]);
                Record(inst, p, $"{LabelOf(job, p, p["step"])}:received", job);
                break;
            case "stuck":
                Record(inst, p, $"{LabelOf(job, p, p["step"])}:stuck", job);
                try { Interp.Stuck?.Invoke(PathOf(inst), p["step"], inst.Attempts.GetValueOrDefault(p["step"])); } catch (Exception) { }
                break;
            case "awaitTimeout":
                inst.Awaited.Add(p["message"]);
                inst.Expired.Add(p["message"]);
                Record(inst, p, $"{LabelOf(job, p, p["step"])}:timeout", job);
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
        if (!await CompleteJob(job.Id, vars)) notes.Add($"the binding task {job.ElementId} was gone before it completed");
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
        Tick();
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
        if (await Waiting(target, m) is { } w) { await Deliver(target, m, J.S(w, "executionId")!, w); return; }
        if (target.Expired.Contains(type)) { unmatched++; return; }
        // Early: the binding holds the message until a catch is waiting (R-09-16).
        receivedAt[m] = now;
        mailbox.Add(m);
        earlyKept++;
    }

    async Task Deliver(Inst target, JsonObject m, string executionId, JsonObject? subscription = null)
    {
        if (J.S(m, "type") == Interp.DecisionMessage) Decided(target, m, subscription);
        target.Consumed.Add(DedupeId(m));
        if (J.P(target.State, "messages") is not JsonObject msgs) target.State["messages"] = msgs = new JsonObject();
        if (J.P(msgs, "received") is not JsonObject received) msgs["received"] = received = new JsonObject();
        var reff = new JsonObject { ["id"] = J.S(m, "id"), ["type"] = J.S(m, "type") };
        received[J.S(m, "type")!] = reff;
        msgs["lastReceived"] = J.Clone(reff);
        Mark();
        // The operator's decision carries its status to the hold's gateway (R-05-56).
        if (J.S(m, "type") == Interp.DecisionMessage) await Message(executionId, J.S(m, "type")!, new Dictionary<string, string> { ["th_decision"] = J.S(m, "status") ?? "" });
        else await Message(executionId, J.S(m, "type")!);
    }

    /// <summary>
    /// A hold's decision taken by the instance (R-05-60): the hold step's fact, through the same path as the other engines
    /// (<see cref="Interp.Decided"/>). The outcome is the one the hold's gateway takes: the decision when it names an outcome of the
    /// hold other than <c>expired</c>, else the hold's primary outcome.
    /// </summary>
    void Decided(Inst target, JsonObject m, JsonObject? subscription)
    {
        try
        {
            if (Interp.Decided is not { } decided || subscription is null) return;
            var ann = byDef.GetValueOrDefault(J.S(subscription, "processDefinitionId") ?? "")?.Ann(J.S(subscription, "activityId") ?? "", "await");
            if (ann?.GetValueOrDefault("step") is not string step) return;
            var activity = ann.GetValueOrDefault("activity");
            var act = activity is null ? null : dep.AllDocs().Where(d => J.S(d, "name") == activity).OrderByDescending(d => J.S(d, "release"), StringComparer.Ordinal).FirstOrDefault();
            var names = J.A(act, "outcomes").Select(o => J.S(o, "name")!).ToList();
            var primary = J.A(act, "outcomes").FirstOrDefault(o => J.Truthy(J.P(o, "primary"))) is { } po ? J.S(po, "name")! : names.FirstOrDefault() ?? "";
            var status = J.S(m, "status");
            var outcome = status is not null && status != "expired" && names.Contains(status) ? status : primary;
            var fact = new JsonObject { ["outcome"] = outcome, ["result"] = outcome };
            if (status is not null) fact["status"] = status;
            if (J.S(m, "hold") is string hold) fact["hold"] = hold;
            if (J.S(m, "by") is string by) fact["by"] = by;
            fact["at"] = DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            var writes = new JsonObject();
            foreach (var p in J.Strings(J.P(act, "writes")).Where(p => p.StartsWith("facts", StringComparison.Ordinal))) writes[p] = fact.DeepClone();
            decided(new JsonObject
            {
                ["instance"] = PathOf(target), ["workflow"] = target.Name, ["step"] = step, ["activity"] = activity, ["release"] = J.S(act, "release"),
                ["call"] = target.Calls.GetValueOrDefault(step), ["attempt"] = 0, ["ctx"] = new JsonObject { ["header"] = J.Clone(J.P(target.State, "header")) },
            }, new JsonObject { ["kind"] = "result", ["outcome"] = outcome, ["writes"] = writes });
        }
        catch (Exception) { /* a fact not recorded does not stop the engine */ }
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
                await Deliver(i, m, J.S(w, "executionId")!, w);
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
            await Message(J.S(s, "executionId")!, "th.compensated");
            moved = true;
        }
        return moved;
    }

    // ================================================================ signals (R-05-12)

    /// <summary>An operator's control of a parked step (R-05-55): delivered only where a parked step waits for it.</summary>
    async Task<bool> Operator(string control)
    {
        var root = roots.FirstOrDefault();
        if (root is null || root.Ended) return false;
        // Requeue (R-09-91): a dead-letter job goes back to the executable jobs with its retries; a parked step is re-driven.
        if (control == "requeue" && root.DeadLetterJob is { } job)
        {
            await rest.MoveDeadLetter(job);
            notes.Add($"dead letter {job} requeued by an operator");
            root.Trace.Add("operator:requeue");
            root.DeadLetter = root.DeadLetterJob = null;
            root.Failed = null;
            Mark();
            return true;
        }
        var shown = control;
        if (control == "requeue") control = "redrive";
        var name = $"th.operator.{control}";
        var subs = await rest.EventSubscriptions(tenant, "message", name, root.Pid);
        if (subs.Count == 0) { notes.Add($"no parked step waits for {control}"); return false; }
        root.Trace.Add($"operator:{shown}");
        Mark();
        await Message(J.S(subs[0], "executionId")!, name);
        return true;
    }

    ILiveFeed? liveFeed;
    bool liveTried;
    long livePolled;

    /// <summary>
    /// The live path from the runtime (R-05-57): with a store, the root's payment is followed on the outbox; at most every quarter
    /// second of wall time the rows not yet delivered are delivered: a signal or an operator's control to the root, a hold's
    /// decision through the front door. Each is recorded as delivered on the payment's chain.
    /// </summary>
    async Task<bool> PollLive()
    {
        if (LiveSignals.Source is not { } source) return false;
        var root = roots.FirstOrDefault();
        if (root is null) return false;
        if (!liveTried) { liveTried = true; liveFeed = await source.Follow(root.Ref, "flowable", root.Pid, J.S(input, "rootId")); }
        if (liveFeed is null || Wall() - livePolled < 250) return false;
        livePolled = Wall();
        var moved = false;
        foreach (var row in await liveFeed.Poll())
        {
            var (name, data) = LiveSignals.EventOf(row, J.P(root.State, "header"));
            string result;
            if (root.Ended) result = "ended";
            else if (name == "message") { await FrontDoor(data); result = "delivered"; }
            else if (Interp.OperatorControls.Contains(J.S(data, "signal"))) result = await Operator(J.S(data, "signal")!) ? "delivered" : "nothing parked";
            else { await Signal(J.S(data, "signal")!); result = "delivered"; }
            await liveFeed.Ack(row, result);
            moved = true;
        }
        return moved;
    }

    /// <summary>An operator wait is open on the root: a hold awaiting its decision, or a parked step.</summary>
    async Task<bool> OperatorWaitOpen()
    {
        var root = roots.FirstOrDefault();
        if (root is null || root.Ended || liveFeed is null) return false;
        foreach (var name in new[] { Interp.DecisionMessage, "th.operator.retry" })
            if ((await rest.EventSubscriptions(tenant, "message", name, root.Pid)).Count > 0) return true;
        return false;
    }

    DateTimeOffset? waitingSince;

    async Task<bool> HoldClock()
    {
        if (!await OperatorWaitOpen()) { waitingSince = null; return false; }
        waitingSince ??= DateTimeOffset.UtcNow;
        return DateTimeOffset.UtcNow - waitingSince.Value < LiveSignals.Patience;
    }

    async Task Signal(string signal)
    {
        if (Interp.OperatorControls.Contains(signal)) { await Operator(signal); return; }
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
        await Message(J.S(subs[0], "executionId")!, SignalMessages[signal]);
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
