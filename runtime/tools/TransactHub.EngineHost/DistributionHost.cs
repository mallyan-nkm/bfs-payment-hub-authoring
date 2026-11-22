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
    protected bool Current;
    readonly CancellationTokenSource exit = new();

    /// <summary>The distribution's id without its bank: a build id, a name part.</summary>
    protected string IdPart => DistributionRegistry.Split(Distribution).id;

    protected abstract Task Open(CancellationToken ct);
    protected abstract Task<JsonObject> RunPayment(string paymentId, JsonObject context);
    protected virtual Task<JsonObject> MakeCurrent() => Task.FromResult(new JsonObject { ["engine"] = Engine, ["note"] = "new payments are pinned to this distribution in the store and sent to this host" });
    protected virtual Task<JsonObject> Status() => Task.FromResult(new JsonObject());
    protected abstract Task<JsonObject> Unregister();
    protected virtual long OpenOnEngine() => Payments.Values.Count(p => J.S(p, "status") == "running");

    public async Task Run(string urls, CancellationToken ct)
    {
        await Open(ct);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, exit.Token);
        using var http = new Http(Route);
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

    async Task Start(string paymentId, JsonObject context)
    {
        var rec = new JsonObject { ["paymentId"] = paymentId, ["status"] = "running", ["distribution"] = Distribution, ["startedAt"] = DateTimeOffset.UtcNow.ToString("O") };
        if (!Payments.TryAdd(paymentId, rec)) return;
        try
        {
            var obs = await RunPayment(paymentId, context);
            var outcome = Outcome(obs);
            await using (var db = StoreDbContext.Open(Store)) await new DistributionRegistry(db).End(paymentId, outcome, $"engine:{Engine}");
            rec["observation"] = obs;
            rec["outcome"] = outcome;
            rec["status"] = "ended";
        }
        catch (Exception e)
        {
            rec["status"] = "failed";
            rec["error"] = e.Message;
            Console.Error.WriteLine($"{paymentId}: {e}");
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
                var ctx = (JsonObject)J.Clone(J.P(body, "context") ?? J.P(J.P(Plan, "built"), "context"))!;
                _ = Task.Run(() => Start(id, ctx));
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
        workers = new DistributionWorkers(client, Plan, DeploymentName(Bank, Environment), IdPart, null, Store);
        await workers.StartAsync();
    }

    protected override async Task<JsonObject> RunPayment(string paymentId, JsonObject context)
    {
        Interlocked.Increment(ref open);
        try { return await workers.RunPaymentAsync(new JsonObject { ["id"] = paymentId, ["context"] = context }); }
        finally { Interlocked.Decrement(ref open); }
    }

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

    protected override Task Open(CancellationToken ct)
    {
        HarnessDll(Engine);
        return Task.CompletedTask;
    }

    protected override async Task<JsonObject> RunPayment(string paymentId, JsonObject context)
    {
        var input = (JsonObject)J.Clone(Plan)!;
        var built = (JsonObject)input["built"]!;
        built["context"] = context;
        built["id"] = paymentId;
        input["distribution"] = Distribution;
        var dir = Path.Combine(Path.GetTempPath(), $"th-host-{Supervisor.Safe(Distribution)}-{Guid.NewGuid():N}"[..48]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "plan.json");
        await File.WriteAllTextAsync(file, J.Str(input));
        try
        {
            var psi = new ProcessStartInfo("dotnet", [HarnessDll(Engine), file]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            var patience = HostStore.Duration("TH_HOST_OPERATOR_PATIENCE", TimeSpan.FromDays(30));
            psi.Environment["TH_OPERATOR_PATIENCE_SECONDS"] = ((long)patience.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Engine == "flowable")
            {
                psi.Environment["TH_FLOWABLE_MODE"] = System.Environment.GetEnvironmentVariable("TH_FLOWABLE_MODE") ?? "production";
                psi.Environment["TH_FLOWABLE_RUN"] = $"{Supervisor.Safe(Distribution)}:{paymentId}";
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
