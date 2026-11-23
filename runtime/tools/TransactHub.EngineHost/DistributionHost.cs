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

    async Task Start(string paymentId, JsonObject context, JsonObject? legs)
    {
        var rec = new JsonObject { ["paymentId"] = paymentId, ["status"] = "running", ["distribution"] = Distribution, ["startedAt"] = DateTimeOffset.UtcNow.ToString("O") };
        if (!Payments.TryAdd(paymentId, rec)) return;
        try
        {
            var obs = await RunPayment(paymentId, context, legs);
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
                _ = Task.Run(() => Start(id, ctx, legs));
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
        await using (var db = StoreDbContext.Open(Store)) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database, ct);
        StoreSignals.Install(Store, StepFacts.Install(Store));
        workers = new DistributionWorkers(client, PlanFor(null), DeploymentName(Bank, Environment), IdPart, null, Store);
        await workers.StartAsync();
    }

    protected override async Task<JsonObject> RunPayment(string paymentId, JsonObject context, JsonObject? legs)
    {
        Interlocked.Increment(ref open);
        try { return await workers.RunPaymentAsync(new JsonObject { ["id"] = paymentId, ["context"] = context, ["legs"] = legs }); }
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
/// DTFx, Elsa and Flowable: the host runs each payment on the engine's runtime harness (runtime/tools/TransactHub.*Harness), from
/// this distribution's plan, so the engine names what it registers after the distribution (DTFx: the orchestrations, Elsa: the
/// workflow definitions, Flowable: the deployment, R-09-55). A Flowable payment runs in production mode with a run key of its
/// own, so that a host that restarts goes on with it (Issue I-89). An operator wait on DTFx and Elsa, whose engines keep a
/// virtual clock in the harness, waits in wall time for at most TH_HOST_OPERATOR_PATIENCE (default P30D), not the harness's test
/// patience (Issue I-127).
/// </summary>
public sealed class HarnessDistributionHost(StoreOptions store, string engine, string distribution, JsonObject plan, string bank, string environment)
    : DistributionHost(store, engine, distribution, plan, bank, environment)
{
    readonly ConcurrentDictionary<int, Process> running = new();

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
    static string Key(string s) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..24];

    protected override Task Open(CancellationToken ct)
    {
        HarnessDll(Engine);
        return Task.CompletedTask;
    }

    protected override async Task<JsonObject> RunPayment(string paymentId, JsonObject context, JsonObject? legs)
    {
        var input = (JsonObject)J.Clone(PlanFor(Workflows.GetValueOrDefault(paymentId)))!;
        var built = (JsonObject)input["built"]!;
        built["context"] = context;
        // DTFx and Elsa run in real time in the host (R-09-93): the run starts now, its timers fire on the wall clock, and a message
        // the front door correlates to it after it started reaches it on its live path (DeliverToRun).
        if (Engine is "dtfx" or "elsa")
        {
            var cfg = built["config"] as JsonObject ?? new JsonObject();
            var now = DateTimeOffset.UtcNow;
            cfg["evaluationDate"] = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            cfg["startTime"] = now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            built["config"] = cfg;
        }
        if (legs is not null) built["legs"] = legs;
        built["id"] = paymentId;
        input["distribution"] = Distribution;
        // Many payments on one store: each root's instance, and so its payload's chain, is the payment's own.
        input["rootId"] = $"root-{Key(paymentId)}";
        var dir = Path.Combine(Path.GetTempPath(), $"th-host-{Supervisor.Safe(Distribution)}-{Guid.NewGuid():N}"[..48]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "plan.json");
        await File.WriteAllTextAsync(file, J.Str(input));
        try
        {
            var psi = new ProcessStartInfo("dotnet", [HarnessDll(Engine), file]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            var patience = HostStore.Duration("TH_HOST_OPERATOR_PATIENCE", TimeSpan.FromDays(30));
            psi.Environment["TH_OPERATOR_PATIENCE_SECONDS"] = ((long)patience.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Engine is "dtfx" or "elsa") psi.Environment["TH_HARNESS_REAL_TIME"] = System.Environment.GetEnvironmentVariable("TH_HARNESS_REAL_TIME") ?? "1";
            if (Engine == "flowable")
            {
                psi.Environment["TH_FLOWABLE_MODE"] = System.Environment.GetEnvironmentVariable("TH_FLOWABLE_MODE") ?? "production";
                psi.Environment["TH_FLOWABLE_RUN"] = Key($"{Distribution}|{paymentId}");
                psi.Environment["TH_FLOWABLE_MAX_WAIT"] = System.Environment.GetEnvironmentVariable("TH_FLOWABLE_MAX_WAIT") ?? System.Xml.XmlConvert.ToString(patience);
            }
            for (var attempt = 0; ; attempt++)
            {
                using var p = Process.Start(psi)!;
                running[p.Id] = p;
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                running.TryRemove(p.Id, out _);
                var outText = await stdout;
                if (outText.Split('\n').FirstOrDefault(l => l.StartsWith("OBSERVATION ")) is string line) return (JsonObject)J.Parse(line["OBSERVATION ".Length..])!;
                // A Flowable binding that stopped goes on from its journal when it is started again with the same run key.
                if (Engine == "flowable" && attempt < 3 && outText.Split('\n').All(l => !l.StartsWith("ERROR "))) continue;
                throw new InvalidOperationException($"the {Engine} harness exited {p.ExitCode}: {outText.Split('\n').FirstOrDefault(l => l.StartsWith("ERROR ")) ?? (await stderr)[^Math.Min(2000, (await stderr).Length)..]}");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A message for a payment whose run has started (R-09-93): on DTFx, Elsa and Flowable it goes on the payment's outbox, and its
    /// run, which follows the payment's live path, hands it to the instance that awaits it (a leg in its slot takes what its platform
    /// workflow does not await). Refused while the run has not yet been accepted on the store; the front door tries again.
    /// </summary>
    protected override async Task<(int, JsonNode?)> Deliver(string paymentId, JsonObject message)
    {
        var subject = await new StoreSignals(Store).DeliverToRun($"root-{Key(paymentId)}", message, $"engine-host:{Distribution}");
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
            _ => $"a deployment transacthub-{Distribution}-<run> per payment",
        },
        ["harnessRuns"] = running.Count,
    });

    protected override Task<JsonObject> Unregister() => Task.FromResult(new JsonObject
    {
        ["engine"] = Engine,
        // Each payment's engine run deployed, and on its end removed, what it registered: nothing of the distribution is left on the engine.
        ["registered"] = 0,
    });
}
