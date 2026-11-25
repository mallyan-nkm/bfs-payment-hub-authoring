using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Temporalio.Client;
using TransactHub.Engine.Temporal;
using TransactHub.Engines;
using TransactHub.Engines.Durable;
using TransactHub.Store;

namespace TransactHub.EngineHost;

/// <summary>
/// The host of one distribution, for as long as the distribution is not removed: it runs every payment pinned to it, records each
/// payment's end in the store (Issue I-97), and on removal unregisters the distribution from its engine and stops. It refuses to
/// stop while a payment it runs is open.
/// </summary>
public abstract class DistributionHost(StoreOptions store, string engine, string distribution, JsonObject plan, string bank, string environment)
{
    protected readonly StoreOptions Store = store;
    protected readonly string Engine = engine, Distribution = distribution, Bank = bank, Environment = environment;
    protected readonly JsonObject Plan = plan;
    protected readonly ConcurrentDictionary<string, JsonObject> Payments = new(StringComparer.Ordinal);
    /// <summary>The platform workflow each payment runs, as the front door routed it.</summary>
    protected readonly ConcurrentDictionary<string, string> Workflows = new(StringComparer.Ordinal);

    /// <summary>
    /// The plan of a platform workflow of the distribution (R-09-94): a plan built from the stored package holds one per platform
    /// workflow under <c>plans</c>; a plan a caller wrote holds one workflow. Null asks for the plan's own workflow.
    /// </summary>
    protected JsonObject PlanFor(string? workflow) =>
        workflow is not null && J.P(Plan, "plans") is JsonObject ps && ps[workflow] is JsonObject p ? p : J.P(Plan, "plans") is JsonObject all && all.Count > 0 && J.P(Plan, "built") is null ? (JsonObject)all.First().Value! : Plan;

    /// <summary>Whether this host runs a platform workflow.</summary>
    protected bool Runs(string workflow) => J.P(Plan, "plans") is JsonObject ps ? ps.ContainsKey(workflow) : J.S(J.P(Plan, "built"), "workflow") == workflow;
    protected bool Current;
    readonly CancellationTokenSource exit = new();

    /// <summary>The distribution's id without its bank: a build id, a name part.</summary>
    protected string IdPart => DistributionRegistry.Split(Distribution).id;

    protected abstract Task Open(CancellationToken ct);
    protected abstract Task<JsonObject> RunPayment(string paymentId, JsonObject context, JsonObject? legs);

    /// <summary>
    /// Hands a message the runtime's front door correlated to a running payment of this host (R-09-85). A host that runs each payment
    /// to its end in one harness run takes none once the run has started (Issue I-134): it is refused, and the front door says so.
    /// </summary>
    protected virtual Task<(int, JsonNode?)> Deliver(string paymentId, JsonObject message) =>
        Task.FromResult<(int, JsonNode?)>((409, new JsonObject { ["error"] = $"{Engine} runs {paymentId} in one harness run and takes no message once it has started (Issue I-134)" }));
    protected virtual Task<JsonObject> MakeCurrent() => Task.FromResult(new JsonObject { ["engine"] = Engine, ["note"] = "new payments are pinned to this distribution in the store and sent to this host" });
    protected virtual Task<JsonObject> Status() => Task.FromResult(new JsonObject());
    protected abstract Task<JsonObject> Unregister();
    protected virtual long OpenOnEngine() => Payments.Values.Count(p => J.S(p, "status") == "running");

    public async Task Run(string urls, CancellationToken ct)
    {
        await Open(ct);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, exit.Token);
        // A host takes only its supervisor's token (R-08-22): without one it does not start.
        var token = await TransactHub.Security.HostToken.FromEnvironment() ?? throw new InvalidOperationException($"The host of {Distribution} takes its callers' token: set {TransactHub.Security.HostToken.Setting} (the supervisor does, R-08-22)");
        using var http = new Http(Route, token);
        http.Start(urls);
        var serving = http.Serve(both.Token);
        Console.Out.WriteLine($"LISTENING {http.Url}");
        await Console.Out.FlushAsync(ct);
        await serving;
    }

    static string Outcome(JsonObject obs)
    {
        var root = J.A(obs, "instances").FirstOrDefault();
        var last = J.Strings(J.P(root, "trace")).LastOrDefault() ?? "";
        return last.StartsWith("end:") ? last[4..] : J.S(root, "failed") is not null ? "failed" : "unknown";
    }

    /// <summary>
    /// Once its start is in the engine's own record (a distribution's service writes it in its history): the front door's answer
    /// waits for it, so that a payment accepted is one the engine goes on with after a restart (Issue I-165). At once by default.
    /// </summary>
    protected virtual Task Begun(string paymentId) => Task.CompletedTask;

    /// <summary>The payment's end is recorded in the store: an engine service that told it is told it need not tell it again.</summary>
    protected virtual void Recorded(string paymentId, string outcome) { }

    Task Start(string paymentId, JsonObject context, JsonObject? legs) => Track(paymentId, () => RunPayment(paymentId, context, legs), resumed: false);

    /// <summary>
    /// A payment the engine's service took back from its history when the host started again (Issue I-165): it is run on to its end
    /// and its end recorded, as one started here is.
    /// </summary>
    protected void Adopt(string paymentId, Func<Task<JsonObject>> run) => _ = Task.Run(() => Track(paymentId, run, resumed: true));

    async Task Track(string paymentId, Func<Task<JsonObject>> run, bool resumed)
    {
        var rec = new JsonObject { ["paymentId"] = paymentId, ["status"] = "running", ["distribution"] = Distribution, ["startedAt"] = DateTimeOffset.UtcNow.ToString("O") };
        if (resumed) rec["resumed"] = true;
        if (!Payments.TryAdd(paymentId, rec)) return;
        try
        {
            var obs = await run();
            // The rule traces of the payment's steps, read from its chain (R-09-240, Issue I-304): a payment the service took back
            // after a restart has those of the steps it ran before, which the service's memory lost.
            try { obs["ruleTraces"] = await TransactHub.Engines.Durable.StepFacts.RuleTraces(Store, paymentId); }
            catch (Exception e) { Console.Error.WriteLine($"{paymentId}: its rule traces were not read: {e.GetType().Name}"); }
            var outcome = Outcome(obs);
            await using (var db = StoreDbContext.Open(Store))
            {
                // The engine failed the payment: a fact with the dictionary's code, beside engine started (R-09-70, Issue I-103).
                if (outcome == "failed")
                {
                    var root = J.A(obs, "instances").FirstOrDefault();
                    await EngineFailures.Record(db, paymentId, Engine, J.S(root, "id") ?? J.S(root, "instance"), J.S(root, "failed"), distribution: Distribution);
                }
                await new DistributionRegistry(db).End(paymentId, outcome, $"engine:{Engine}");
            }
            Recorded(paymentId, outcome);
            rec["observation"] = obs;
            rec["outcome"] = outcome;
            rec["status"] = "ended";
        }
        catch (Exception e)
        {
            rec["status"] = "failed";
            rec["error"] = e.Message;
            Console.Error.WriteLine($"{paymentId}: {e}");
            try
            {
                await using var db = StoreDbContext.Open(Store);
                await EngineFailures.Record(db, paymentId, Engine, null, e.Message, distribution: Distribution);
            }
            catch (Exception r) { Console.Error.WriteLine($"{paymentId}: the engine's failure was not recorded: {r.Message}"); }
        }
        rec["endedAt"] = DateTimeOffset.UtcNow.ToString("O");
    }

    async Task<(int, JsonNode?)> Route(string method, string path, JsonObject? body)
    {
        switch (method, path)
        {
            case ("POST", "/payments"):
            {
                var id = J.S(body, "paymentId") ?? throw new ArgumentException("paymentId is required");
                string? pinned;
                await using (var db = StoreDbContext.Open(Store)) pinned = await new DistributionRegistry(db).PinOf(id);
                // A payment runs only on the distribution it is pinned to (R-09-53).
                if (pinned != Distribution) return (409, new JsonObject { ["error"] = $"{id} is pinned to {pinned ?? "no distribution"}, not {Distribution}" });
                var ctx = (JsonObject)J.Clone(J.P(body, "context") ?? J.P(J.P(PlanFor(J.S(body, "workflow")), "built"), "context") ?? new JsonObject())!;
                // The runtime's front door names the workflow it routed the payment to and the legs in its slots (R-09-85); a host
                // whose plan came from the stored package runs every platform workflow of the distribution (R-09-94).
                if (J.S(body, "workflow") is string wf)
                {
                    if (!Runs(wf)) return (409, new JsonObject { ["error"] = $"{Distribution}'s host does not run {wf}" });
                    Workflows[id] = wf;
                }
                var legs = J.P(body, "legs") is JsonObject l ? (JsonObject)J.Clone(l)! : null;
                var run = Task.Run(() => Start(id, ctx, legs));
                // Accepted once the engine has it: a run that fails before is answered as failed.
                var begun = Begun(id);
                await Task.WhenAny(begun, run, Task.Delay(TimeSpan.FromMinutes(5)));
                if (!begun.IsCompletedSuccessfully)
                    return (503, new JsonObject { ["error"] = $"{id} did not start on {Engine}: {(Payments.TryGetValue(id, out var failed) ? J.S(failed, "error") : "no answer")}" });
                return (202, new JsonObject { ["paymentId"] = id, ["host"] = Distribution });
            }
            case ("POST", "/current"):
                Current = true;
                return (200, await MakeCurrent());
            case ("GET", "/status"):
            {
                var s = await Status();
                s["distribution"] = Distribution;
                s["current"] = Current;
                s["running"] = Payments.Values.Count(p => J.S(p, "status") == "running");
                s["ended"] = Payments.Values.Count(p => J.S(p, "status") != "running");
                return (200, s);
            }
            case ("POST", "/stop"):
            {
                var open = OpenOnEngine();
                if (open > 0) return (409, new JsonObject { ["error"] = $"{Distribution} still runs {open} payment(s) on {Engine}" });
                var r = await Unregister();
                _ = Task.Run(async () => { await Task.Delay(200); exit.Cancel(); });
                return (200, r);
            }
        }
        if (method == "POST" && path.StartsWith("/payments/") && path.EndsWith("/messages"))
        {
            var id = Uri.UnescapeDataString(path["/payments/".Length..^"/messages".Length]);
            if (!Payments.TryGetValue(id, out var rec) || J.S(rec, "status") != "running") return (404, new JsonObject { ["error"] = $"{id} is not running on {Distribution}" });
            return await Deliver(id, J.P(body, "message") as JsonObject ?? throw new ArgumentException("message is required"));
        }
        if (method == "GET" && path.StartsWith("/payments/") && Payments.TryGetValue(Uri.UnescapeDataString(path["/payments/".Length..]), out var p))
            return (200, (JsonObject)J.Clone(p)!);
        return (404, new JsonObject { ["error"] = $"{method} {path}" });
    }
}

/// <summary>
/// Temporal on a real server (TH_TEMPORAL_ADDRESS, TH_TEMPORAL_NAMESPACE): the distribution's workers run for as long as this
/// host does, as the version <c>transacthub-&lt;bank&gt;-&lt;environment&gt;</c> / <c>&lt;distribution id&gt;</c> of the bank's worker
/// deployment; payments are pinned to it, and a hold waits on the server's clock for as long as its expiry says (Issue I-127).
/// </summary>
public sealed class TemporalDistributionHost(StoreOptions store, string distribution, JsonObject plan, string bank, string environment)
    : DistributionHost(store, "temporal", distribution, plan, bank, environment)
{
    DistributionWorkers workers = null!;
    long open;

    public static string DeploymentName(string bank, string environment) => $"transacthub-{bank}-{environment}";

    protected override async Task Open(CancellationToken ct)
    {
        var address = System.Environment.GetEnvironmentVariable("TH_TEMPORAL_ADDRESS") ?? throw new InvalidOperationException("A long-running Temporal host needs a real server: set TH_TEMPORAL_ADDRESS");
        var ns = System.Environment.GetEnvironmentVariable("TH_TEMPORAL_NAMESPACE") ?? "default";
        var client = await TemporalClient.ConnectAsync(new(address) { Namespace = ns });
        TemporalEngine.RealServer = true;
        // The schema is the migrate step's, as the owner, on a runtime (R-09-141): this host migrates only where the installation lets it.
        if (StoreMigration.Allowed()) { await using var db = StoreDbContext.Open(Store); await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database, ct); }
        StoreSignals.Install(Store, StepFacts.Install(Store));
        // The bank's values as of each step, from the configuration releases in force (R-09-135).
        StoreValues.Install(Store);
        workers = new DistributionWorkers(client, EveryWorkflow(), DeploymentName(Bank, Environment), IdPart, null, Store, pinnedAs: Distribution);
        await workers.StartAsync();
    }

    /// <summary>
    /// The plan the distribution's workers run: the first platform workflow's, with the documents of every platform workflow the host runs,
    /// so a payment the front door routed to another one runs that one (R-09-94). Before, every payment ran the first workflow.
    /// </summary>
    JsonObject EveryWorkflow()
    {
        var plan = (JsonObject)J.Clone(PlanFor(null))!;
        if (J.P(Plan, "plans") is JsonObject plans && plan["documents"] is JsonObject docs)
            foreach (var (_, p) in plans)
                foreach (var (k, v) in J.Obj(J.P(p, "documents")))
                    if (!docs.ContainsKey(k)) docs[k] = J.Clone(v);
        return plan;
    }

    protected override async Task<JsonObject> RunPayment(string paymentId, JsonObject context, JsonObject? legs)
    {
        Interlocked.Increment(ref open);
        try { return await workers.RunPaymentAsync(new JsonObject { ["id"] = paymentId, ["context"] = context, ["legs"] = legs, ["now"] = DateTimeOffset.UtcNow.ToString("O"), ["workflow"] = Workflows.GetValueOrDefault(paymentId) }); }
        finally { Interlocked.Decrement(ref open); }
    }

    protected override async Task<(int, JsonNode?)> Deliver(string paymentId, JsonObject message) =>
        await workers.DeliverAsync(paymentId, message)
            ? (202, new JsonObject { ["answer"] = "delivered", ["paymentId"] = paymentId })
            : (409, new JsonObject { ["error"] = $"{paymentId} has no front door on {Distribution} yet" });

    protected override async Task<JsonObject> MakeCurrent()
    {
        await workers.MakeCurrentAsync($"engine-host:{Distribution}");
        return new JsonObject { ["engine"] = "temporal", ["workerDeployment"] = workers.DeploymentName, ["buildId"] = workers.Distribution, ["current"] = true };
    }

    protected override async Task<JsonObject> Status()
    {
        var d = await workers.DescribeAsync();
        return new JsonObject
        {
            ["engine"] = "temporal", ["workerDeployment"] = workers.DeploymentName, ["buildId"] = workers.Distribution,
            ["taskQueues"] = J.Arr(workers.TaskQueues), ["drainage"] = await workers.DrainageAsync(), ["openOnServer"] = await workers.OpenOnServerAsync(),
            ["versionStatus"] = d.WorkerDeploymentVersionInfo?.Status.ToString(), ["paymentsRun"] = workers.PaymentsRun,
        };
    }

    protected override long OpenOnEngine() => Interlocked.Read(ref open) + workers.OpenOnServerAsync().GetAwaiter().GetResult();

    protected override async Task<JsonObject> Unregister()
    {
        var deleted = await workers.RemoveAsync($"engine-host:{Distribution}", HostStore.Duration("TH_HOST_UNREGISTER_WAIT", TimeSpan.FromMinutes(6)));
        return new JsonObject { ["engine"] = "temporal", ["workersStopped"] = true, ["versionDeleted"] = deleted, ["version"] = $"{workers.DeploymentName}/{workers.Distribution}" };
    }
}

/// <summary>
/// DTFx, Elsa and Flowable: the host runs one service of the engine's runtime harness (runtime/tools/TransactHub.*Harness) for its
/// distribution, for as long as it runs, and every payment of the distribution on it (Issue I-165, R-09-196 to R-09-199). The
/// engine names what it registers after the distribution (DTFx: the orchestration, Elsa: the workflow definition, Flowable: the
/// deployment, R-09-55). DTFx and Elsa keep the service's history in the store, one chain per distribution, and a service that
/// starts again rebuilds itself from it; Flowable's binding keeps its journal there (I-89) and serves every instance of the
/// distribution's tenant, from one deployment. A payment the service had not ended when its host stopped is taken back and run to
/// its end. An operator wait on DTFx and Elsa waits in wall time for at most TH_HOST_OPERATOR_PATIENCE (default P30D), not the
/// harness's test patience (Issue I-127).
/// </summary>
public sealed class HarnessDistributionHost(StoreOptions store, string engine, string distribution, JsonObject plan, string bank, string environment)
    : DistributionHost(store, engine, distribution, plan, bank, environment)
{
    HarnessService service = null!;
    string planFile = "";

    static string HarnessDll(string engine)
    {
        var name = engine switch { "dtfx" => "Dtfx", "elsa" => "Elsa", "flowable" => "Flowable", _ => throw new ArgumentException($"no harness for {engine}") };
        if (System.Environment.GetEnvironmentVariable($"TH_HOST_HARNESS_{engine.ToUpperInvariant()}") is { Length: > 0 } p) return p;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "tools", $"TransactHub.{name}Harness", "bin", "Release", "net10.0", $"TransactHub.{name}Harness.dll");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException($"TransactHub.{name}Harness.dll: build runtime/tools/TransactHub.{name}Harness");
    }

    /// <summary>A short key of its own for a payment's run: the store's subject ids are at most 64 characters.</summary>
    static string Key(string s) => EngineHistory.Key(s);

    /// <summary>The host's setting of its service's work items at once (R-09-238): 1 to 64, default 4; anything else is refused.</summary>
    public static int ServiceConcurrency(string? setting = null)
    {
        setting ??= System.Environment.GetEnvironmentVariable("TH_HOST_SERVICE_CONCURRENCY");
        if (string.IsNullOrWhiteSpace(setting)) return 4;
        return int.TryParse(setting, out var n) && n >= EngineHistory.MinConcurrency && n <= EngineHistory.MaxConcurrency ? n
            : throw new InvalidOperationException($"TH_HOST_SERVICE_CONCURRENCY is {setting}: a distribution's service runs from {EngineHistory.MinConcurrency} to {EngineHistory.MaxConcurrency} work items at once");
    }

    /// <summary>The host's setting of how many history facts its service writes between checkpoints (R-09-239): 50 to 100000, default 500.</summary>
    public static int CheckpointEvery(string? setting = null)
    {
        setting ??= System.Environment.GetEnvironmentVariable("TH_HOST_HISTORY_CHECKPOINT_EVERY");
        if (string.IsNullOrWhiteSpace(setting)) return 500;
        return int.TryParse(setting, out var n) && n >= 50 && n <= 100_000 ? n
            : throw new InvalidOperationException($"TH_HOST_HISTORY_CHECKPOINT_EVERY is {setting}: a service checkpoints its history every 50 to 100000 facts");
    }

    /// <summary>A payment's root instance on the service: its own, so that its payload's chain is the payment's own.</summary>
    static string RootOf(string paymentId) => $"root-{Key(paymentId)}";

    protected override async Task Open(CancellationToken ct)
    {
        var dll = HarnessDll(Engine);
        var dir = Path.Combine(Path.GetTempPath(), $"th-service-{Supervisor.Safe(Distribution)}-{Guid.NewGuid():N}"[..56]);
        Directory.CreateDirectory(dir);
        planFile = Path.Combine(dir, "plan.json");
        var input = (JsonObject)J.Clone(Plan)!;
        input["distribution"] = Distribution;
        await File.WriteAllTextAsync(planFile, J.Str(input), ct);
        var patience = HostStore.Duration("TH_HOST_OPERATOR_PATIENCE", TimeSpan.FromDays(30));
        var env = new Dictionary<string, string>
        {
            ["TH_OPERATOR_PATIENCE_SECONDS"] = ((long)patience.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["TH_HARNESS_REAL_TIME"] = "1",
            // How many work items the service runs at once, its payments side by side (R-09-238, Issue I-300): the host's
            // TH_HOST_SERVICE_CONCURRENCY, from 1 to 64, default 4. A setting refused out of its bounds stops the host.
            ["TH_SERVICE_CONCURRENCY"] = ServiceConcurrency().ToString(System.Globalization.CultureInfo.InvariantCulture),
            // How often the service compacts its history into a checkpoint of its open payments (R-09-239, Issue I-301).
            ["TH_HISTORY_CHECKPOINT_EVERY"] = CheckpointEvery().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (Engine == "flowable")
        {
            env["TH_FLOWABLE_MODE"] = System.Environment.GetEnvironmentVariable("TH_FLOWABLE_MODE") ?? "production";
            // The distribution's one binding: its journal, its tenant and its deployment are the distribution's (R-09-198).
            env["TH_FLOWABLE_RUN"] = $"dist-{Key(Distribution)}";
        }
        service = new HarnessService(Engine, dll, planFile, env)
        {
            // A payment the service took back from its history: its end is recorded when it comes (R-09-197).
            Resumed = (paymentId, _, workflow) =>
            {
                if (workflow is not null) Workflows[paymentId] = workflow;
                Adopt(paymentId, () => service.EndOf(paymentId));
            },
            // The service ended under the host: the host ends too, the supervisor starts it again and the service rebuilds.
            Exited = code => { Console.Error.WriteLine($"{Distribution}: the {Engine} service ended ({code}); the host ends for its supervisor to start it again"); System.Environment.Exit(3); },
        };
        await service.Start(ct);
    }

    protected override async Task<JsonObject> RunPayment(string paymentId, JsonObject context, JsonObject? legs)
    {
        await service.Begin(paymentId, RootOf(paymentId), Workflows.GetValueOrDefault(paymentId) ?? J.S(J.P(PlanFor(null), "built"), "workflow"), context, legs);
        return await service.EndOf(paymentId);
    }

    protected override Task Begun(string paymentId) => service.WhenStarted(paymentId);

    protected override void Recorded(string paymentId, string outcome) => service.Ack(paymentId, RootOf(paymentId), outcome);

    /// <summary>
    /// A message for a payment whose run has started (R-09-93): on DTFx, Elsa and Flowable it goes on the payment's outbox, and its
    /// run, which follows the payment's live path, hands it to the instance that awaits it (a leg in its slot takes what its platform
    /// workflow does not await). Refused while the run has not yet been accepted on the store; the front door tries again.
    /// </summary>
    protected override async Task<(int, JsonNode?)> Deliver(string paymentId, JsonObject message)
    {
        var subject = await new StoreSignals(Store).DeliverToRun(RootOf(paymentId), message, $"engine-host:{Distribution}");
        return subject is null
            ? (409, new JsonObject { ["error"] = $"{paymentId}'s run on {Engine} has not started on the store yet; send the message again" })
            : (202, new JsonObject { ["answer"] = "delivered", ["paymentId"] = paymentId, ["path"] = "outbox", ["subject"] = subject });
    }

    protected override Task<JsonObject> Status() => Task.FromResult(new JsonObject
    {
        ["engine"] = Engine, ["naming"] = Engine switch
        {
            "dtfx" => $"orchestration transacthub.interpreter@{Distribution}",
            "elsa" => $"workflow definition transacthub.interpreter@{Distribution}",
            _ => $"deployment transacthub-{Distribution}",
        },
        // One service for every payment of the distribution (R-09-196): its process, and what it rebuilt when it started.
        ["service"] = new JsonObject { ["pid"] = service.Pid, ["resumed"] = service.Resumes, ["ready"] = J.Clone(service.Ready) },
    });

    protected override async Task<JsonObject> Unregister()
    {
        // Flowable: the distribution's deployment is removed with its binding; DTFx and Elsa hold nothing on an engine of their own.
        var removed = Engine == "flowable" ? await service.Unregister() : null;
        await service.DisposeAsync();
        return new JsonObject { ["engine"] = Engine, ["registered"] = 0, ["removed"] = removed };
    }
}
