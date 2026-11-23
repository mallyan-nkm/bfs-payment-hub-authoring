using System.Text.Json.Nodes;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;
using TransactHub.Engines;
using TransactHub.Engines.Durable;
using TransactHub.Store;

namespace TransactHub.Engine.Temporal;

/// <summary>
/// One distribution's workers on a real Temporal server, for a host that runs for days (Issue I-114, R-09-67): Temporal's worker
/// versioning by build id. Every distribution of a bank polls the same task queues as one version of the bank's worker
/// deployment (<c>transacthub-&lt;bank&gt;-&lt;environment&gt;</c>), its build id the distribution's id. Workflows are pinned: a
/// payment started on a version runs every workflow task, child and activity on it to its end, whatever version becomes current
/// later. Activation makes this version the deployment's current one (<see cref="MakeCurrentAsync"/>); the one current before
/// drains in Temporal as in the store; removal stops the workers and deletes the version from the server.
/// The plan given at start is the distribution's: its documents, its binding's queues and its workers; each payment then brings
/// only its context (<see cref="RunPaymentAsync"/>).
/// </summary>
public sealed class DistributionWorkers : IAsyncDisposable
{
    readonly TemporalClient client;
    readonly List<(TemporalWorker w, Task t)> running = new();
    readonly CancellationTokenSource stop = new();
    readonly IParty? ownParty;
    public readonly JsonObject Plan;
    public readonly string Distribution, DeploymentName;
    public readonly WorkerDeploymentVersion Version;
    Temporalio.Api.Deployment.V1.WorkerDeploymentVersion VersionProto => new() { DeploymentName = DeploymentName, BuildId = Distribution };
    public readonly Deployment Dep;
    public readonly IParty Party;
    public readonly Host Host;
    public readonly DurableClaimCheck? Claim;
    public readonly StoreOptions? Store;
    public readonly Func<string, string> QueueOf;
    public readonly SortedSet<string> TaskQueues = new(StringComparer.Ordinal);
    public int PaymentsRun;

    /// <param name="pinnedAs">The distribution's id as payments are pinned to it (<c>dist/&lt;bank&gt;@&lt;id&gt;</c>), named in every attempt
    /// the workers send (R-09-130); by default the plan's <c>distribution</c>.</param>
    public DistributionWorkers(TemporalClient client, JsonObject plan, string deploymentName, string distribution, IParty? party = null, StoreOptions? store = null, string? pinnedAs = null)
    {
        this.client = client;
        Plan = plan;
        Distribution = distribution;
        DeploymentName = deploymentName;
        Version = new WorkerDeploymentVersion(deploymentName, distribution);
        var config = (JsonObject)J.P(J.P(plan, "built"), "config")!;
        var start = DateTimeOffset.Parse($"{J.S(config, "evaluationDate")}T{J.S(config, "startTime") ?? "06:00:00"}Z").ToUnixTimeMilliseconds();
        // One distribution per process: the interpreter's workflow code reads its documents from here (I-127: one host per engine and distribution).
        Dep = new Deployment((JsonObject)J.P(plan, "documents")!, config, start, (int)(J.Num(plan, "chunkSize") ?? 100)) { ItemConcurrency = (int)(J.Num(plan, "itemConcurrency") ?? 1) };
        Deployment.Current = Dep;
        ownParty = party is null ? new WorkerRouter((JsonObject)J.P(plan, "workers")!, (JsonObject)J.P(plan, "queues")!, pinnedAs ?? J.S(plan, "distribution")) : null;
        Party = party ?? ownParty!;
        Store = store;
        Claim = store is null ? null : new DurableClaimCheck(store);
        Host = new Host(Party, Dep, Claim);
        var queues = J.P(plan, "queues") as JsonObject;
        // The same task queues for every distribution: the build id, not the queue's name, keeps them apart.
        QueueOf = activity =>
        {
            if (queues is null || activity == Interp.ClaimCheck) return ScenarioRunner.HostQueue;
            var parts = activity.Split('@')[0].Split('.');
            var scope = parts[0] is "p" or "b" ? parts[0] : $"{parts[0]}.{parts[1]}";
            return J.S(queues, scope) ?? ScenarioRunner.HostQueue;
        };
        InterpreterWorkflow.QueueOf = QueueOf;
        TaskQueues.Add(ScenarioRunner.HostQueue);
        if (queues is not null) foreach (var (_, q) in queues) TaskQueues.Add(q!.GetValue<string>());
    }

    /// <summary>Starts a worker per task queue as this version, and waits until the server knows the version.</summary>
    public async Task StartAsync(TimeSpan? wait = null)
    {
        foreach (var q in TaskQueues)
        {
            var o = new TemporalWorkerOptions(q)
            {
                MaxConcurrentActivities = 200, MaxConcurrentWorkflowTasks = 100,
                DeploymentOptions = new WorkerDeploymentOptions(Version, true) { DefaultVersioningBehavior = Temporalio.Common.VersioningBehavior.Pinned },
            }.AddActivity(Host.AnyAsync);
            if (q == ScenarioRunner.HostQueue) o.AddWorkflow<InterpreterWorkflow>().AddWorkflow<FrontDoorWorkflow>();
            var w = new TemporalWorker(client, o);
            running.Add((w, w.ExecuteAsync(stop.Token)));
        }
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(60));
        for (;;)
        {
            try { await DescribeAsync(); return; }
            catch (Temporalio.Exceptions.RpcException) when (DateTime.UtcNow < until) { await Task.Delay(500); }
        }
    }

    public Task<DescribeWorkerDeploymentVersionResponse> DescribeAsync() =>
        client.WorkflowService.DescribeWorkerDeploymentVersionAsync(new DescribeWorkerDeploymentVersionRequest { Namespace = client.Options.Namespace, DeploymentVersion = VersionProto });

    /// <summary>Activation on the engine: this version becomes the deployment's current one; the one before it drains.</summary>
    public async Task MakeCurrentAsync(string identity)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        for (;;)
        {
            try
            {
                await client.WorkflowService.SetWorkerDeploymentCurrentVersionAsync(new SetWorkerDeploymentCurrentVersionRequest
                {
                    Namespace = client.Options.Namespace, DeploymentName = DeploymentName, BuildId = Distribution, Identity = identity, IgnoreMissingTaskQueues = true,
                });
                return;
            }
            catch (Temporalio.Exceptions.RpcException) when (DateTime.UtcNow < until) { await Task.Delay(500); }
        }
    }

    /// <summary>The version's drainage in Temporal: draining while workflows pinned to it may still run, drained when none can.</summary>
    public async Task<string> DrainageAsync()
    {
        var d = await DescribeAsync();
        return d.WorkerDeploymentVersionInfo?.DrainageInfo?.Status switch
        {
            VersionDrainageStatus.Draining => "draining",
            VersionDrainageStatus.Drained => "drained",
            _ => d.WorkerDeploymentVersionInfo?.Status.ToString().ToLowerInvariant().Replace("workerdeploymentversionstatus", "") ?? "unknown",
        };
    }

    /// <summary>
    /// The workflows pinned to this version still running, as the server records them: what removal waits for on the engine. The
    /// running workflows of the host's queue are listed and each one's versioning read, so the count does not depend on how a
    /// server release names the version in its search attributes.
    /// </summary>
    public async Task<long> OpenOnServerAsync()
    {
        long n = 0;
        await foreach (var w in client.ListWorkflowsAsync($"TaskQueue = '{ScenarioRunner.HostQueue}' AND ExecutionStatus = 'Running'"))
        {
            var d = await client.WorkflowService.DescribeWorkflowExecutionAsync(new DescribeWorkflowExecutionRequest
            {
                Namespace = client.Options.Namespace, Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = w.Id, RunId = w.RunId },
            });
            var v = d.WorkflowExecutionInfo?.VersioningInfo;
            if (v?.DeploymentVersion?.DeploymentName == DeploymentName && v.DeploymentVersion.BuildId == Distribution) n++;
        }
        return n;
    }

    /// <summary>One payment, run from this distribution's documents on its pinned version.</summary>
    public async Task<JsonObject> RunPaymentAsync(JsonObject payment)
    {
        Interlocked.Increment(ref PaymentsRun);
        var input = (JsonObject)J.Clone(Plan)!;
        var built = (JsonObject)input["built"]!;
        if (J.P(payment, "context") is JsonObject ctx) built["context"] = J.Clone(ctx);
        if (J.S(payment, "id") is string id) built["id"] = id;
        // A payment's run starts at its own time on the host's clock (R-09-135), not at the host's start: a wait it reads as of
        // the step's day reads that day.
        if (J.S(payment, "now") is string now && DateTimeOffset.TryParse(now, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
        {
            var cfg = (JsonObject)built["config"]!;
            cfg["evaluationDate"] = t.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            cfg["startTime"] = t.UtcDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
        input["distribution"] = Distribution;
        if (J.P(payment, "legs") is JsonObject legs) built["legs"] = J.Clone(legs);
        var paymentId = J.S(payment, "id");
        return await new ScenarioRunner(input) { OnFrontDoor = fd => { if (paymentId is not null) FrontDoors[paymentId] = fd; } }.Run(new WorkflowEnvironment(client), this);
    }

    /// <summary>The front door workflow of each payment this host runs, by the payment's id.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> FrontDoors = new(StringComparer.Ordinal);

    /// <summary>
    /// Hands a message for a running payment to its front door workflow, which routes it by correlation to the run that awaits it
    /// (R-09-85). Answers false when this host runs no such payment.
    /// </summary>
    public async Task<bool> DeliverAsync(string paymentId, JsonObject message)
    {
        if (!FrontDoors.TryGetValue(paymentId, out var fd)) return false;
        var d = new JsonObject { ["deliveries"] = new JsonArray(new JsonObject { ["at"] = 0, ["message"] = J.Clone(message) }) };
        await client.GetWorkflowHandle(fd).SignalAsync(FrontDoorWorkflow.Deliver, new object?[] { J.Str(d) });
        return true;
    }

    /// <summary>Removal on the engine: the workers stop polling, and the version is deleted from the server once it lets it go.</summary>
    public async Task<bool> RemoveAsync(string identity, TimeSpan? wait = null)
    {
        await StopAsync();
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromMinutes(6));
        for (;;)
        {
            try
            {
                await client.WorkflowService.DeleteWorkerDeploymentVersionAsync(new DeleteWorkerDeploymentVersionRequest
                {
                    // Removal is guarded by the store (no open payment, the longest wait passed) and by OpenOnServerAsync; Temporal's own
                    // drainage status is refreshed only every few minutes, so it is not waited for again here.
                    Namespace = client.Options.Namespace, DeploymentVersion = VersionProto, SkipDrainage = true, Identity = identity,
                });
                return true;
            }
            // The server refuses while the version's pollers are recent; it lets them go a few minutes after the last poll.
            catch (Temporalio.Exceptions.RpcException) when (DateTime.UtcNow < until) { await Task.Delay(5000); }
            catch (Temporalio.Exceptions.RpcException) { return false; }
        }
    }

    public async Task StopAsync()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        foreach (var (_, t) in running) { try { await t; } catch (OperationCanceledException) { } }
        running.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        ownParty?.Dispose();
    }
}
