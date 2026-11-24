using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TransactHub.Engines;
using TransactHub.Engines.Durable;

namespace TransactHub.Engine.Flowable;

/// <summary>
/// The binding as a distribution's one binding on the runtime's engine host (Issue I-165, R-09-198): it deploys the distribution's
/// BPMN once, in the distribution's own tenant, and is the external worker of every instance of that tenant, so one binding serves
/// every payment of the distribution and no payment's jobs are taken by another's binding. Its state is its journal in the store
/// (I-89), keyed by the distribution (TH_FLOWABLE_RUN): a binding that starts again takes back the deployment, the tenant and every
/// payment that had not ended, and goes on. Production mode only: the server's executor runs the jobs and fires the timers. It speaks
/// the service lines of <see cref="ServiceLines"/> with its host.
/// </summary>
public sealed partial class FlowableBinding
{
    /// <summary>The payments the binding serves, by root id: the payment's id, its platform workflow, and whether its end is recorded.</summary>
    readonly JsonObject servedRoots = new();
    readonly ConcurrentQueue<JsonObject> commands = new();
    readonly Dictionary<string, ILiveFeed?> feeds = new(StringComparer.Ordinal);
    readonly HashSet<string> told = new(StringComparer.Ordinal);
    long servedPolled;
    ServiceParty? serviceParty;

    /// <summary>Serves the distribution of the plan until its host unregisters it (or the host's input ends).</summary>
    public async Task Serve()
    {
        if (!opts.Production) throw new InvalidOperationException("a distribution's binding runs in production mode (TH_FLOWABLE_MODE=production)");
        var storeOpts = StoreFromEnvironment() ?? throw new InvalidOperationException("a distribution's binding keeps its journal in the store: set TH_STORE_PROVIDER and TH_STORE_CONNECTION");
        var distribution = J.S(input, "distribution") ?? throw new InvalidOperationException("a distribution's binding runs one distribution: the plan names none");
        await using (var db = TransactHub.Store.StoreDbContext.Open(storeOpts))
            if (TransactHub.Store.StoreMigration.Allowed()) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database);
        StoreSignals.Install(storeOpts, StepFacts.Install(storeOpts));
        StoreValues.Install(storeOpts);
        claim = new DurableClaimCheck(storeOpts);
        var runKey = J.S(input, "run") ?? Environment.GetEnvironmentVariable("TH_FLOWABLE_RUN") ?? $"dist-{EngineHistory.Key(distribution)}";
        journal = new BindingJournal(storeOpts, runKey);
        var saved = await journal.Last();
        if (saved is not null && J.S(saved, "closed") is string closedAt) throw new InvalidOperationException($"the binding of {distribution} was unregistered at {closedAt}");
        var resumed = saved is not null;
        if (resumed) Restore(saved!);
        else
        {
            // The distribution's tenant: one per distribution, so its binding works its jobs and no other's (R-09-198).
            runId = EngineHistory.Key(distribution)[..16];
            tenant = $"th-{runId}";
            start = now = wallStart = Wall();
            offset = 0;
        }
        var (plans, documents, _) = ServiceLines.Of(input);
        dep = new Deployment(documents, config, start, (int)(J.Num(input, "chunkSize") ?? 100)) { ItemConcurrency = (int)(J.Num(input, "itemConcurrency") ?? 1) };
        Deployment.Current = dep;
        party = serviceParty = new ServiceParty(plans, distribution);

        var files = new Dictionary<string, string>();
        foreach (var (_, p) in plans)
            foreach (var (file, xml) in J.Obj(J.P(p, "bpmn")))
            {
                var text = J.AsKey(xml)!;
                var proc = BpmnProcess.Parse(text);
                byKey[proc.Key] = proc;
                files[file.EndsWith(".bpmn20.xml") ? file : $"{proc.Key}.bpmn20.xml"] = text;
            }
        if (files.Count == 0) throw new InvalidOperationException("The plan has no BPMN: the package must be exported for flowable");
        var server = await rest.Engine();
        if (!resumed)
        {
            log.Add($"Flowable {J.S(server, "version")} at the REST server: the binding of {distribution}, tenant {tenant}");
            // Deployed once for the distribution, whatever the number of its payments (R-09-198).
            deploymentId = await rest.Deploy($"transacthub-{distribution}", files, tenant);
            log.Add($"deployed {files.Count} process(es) as deployment {deploymentId}");
        }
        foreach (var d in await rest.ProcessDefinitions(deploymentId))
            if (byKey.TryGetValue(J.S(d, "key")!, out var proc)) byDef[J.S(d, "id")!] = proc;
        var missing = byKey.Keys.Where(k => !byDef.Values.Any(v => v.Key == k)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"The server did not deploy {string.Join(", ", missing)}");
        if (resumed) await Resume(saved!);
        else await Save(null);

        // Every payment it had not ended, taken back.
        foreach (var (rootId, s) in servedRoots)
        {
            if (J.Truthy(J.P(s, "reported"))) continue;
            if (RootOf(rootId) is { } r) serviceParty.Root(r.Pid, J.S(s, "workflow")!);
            ServiceLines.Out("RESUMED", new JsonObject { ["paymentId"] = J.S(s, "paymentId"), ["rootId"] = rootId, ["workflow"] = J.S(s, "workflow") });
        }
        ServiceLines.Read(c => commands.Enqueue(c));
        ServiceLines.Out("READY", new JsonObject { ["engine"] = "flowable", ["distribution"] = distribution, ["tenant"] = tenant, ["deployment"] = deploymentId, ["journal"] = journal.Subject, ["restarts"] = restarts });

        for (;;)
        {
            Tick();
            var moved = false;
            while (commands.TryDequeue(out var c))
            {
                try { if (!await Command(c)) return; }
                catch (Exception e) { ServiceLines.Out("ERROR", new JsonObject { ["error"] = e.Message, ["paymentId"] = J.S(c, "paymentId") }); }
                moved = true;
            }
            while (queue.TryPeek(out var ev, out var k) && k.at <= now)
            {
                queue.Dequeue();
                await RunEv(ev);
                moved = true;
                Tick();
            }
            moved |= await PollServed();
            moved |= await PlaceTimers();
            moved |= await WorkExternalJobs();
            moved |= await DeliverHeld();
            moved |= await ReleaseCompensation();
            await SurfaceDeadLetters();
            await TellEnded();
            if (moved) continue;
            var wait = (long)opts.Poll.TotalMilliseconds;
            if (queue.TryPeek(out _, out var nk)) wait = Math.Min(wait, Math.Max(0, nk.at - now));
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(5, wait)));
        }
    }

    Inst? RootOf(string rootId) => roots.LastOrDefault(r => r.Label == rootId);

    /// <summary>One command of the host; false once the binding is unregistered.</summary>
    async Task<bool> Command(JsonObject c)
    {
        switch (J.S(c, "op"))
        {
            case "start":
            {
                var paymentId = J.S(c, "paymentId")!;
                var rootId = J.S(c, "rootId")!;
                if (servedRoots.ContainsKey(rootId)) { ServiceLines.Out("STARTED", new JsonObject { ["paymentId"] = paymentId, ["rootId"] = rootId, ["known"] = true }); return true; }
                var workflow = J.S(c, "workflow") ?? J.S(built, "workflow")!;
                var ctx = (JsonObject)J.Clone(J.P(c, "context") ?? new JsonObject())!;
                var doc = dep.Resolve(workflow, null, 0) ?? throw new InvalidOperationException($"{workflow} does not resolve");
                servedRoots[rootId] = new JsonObject { ["paymentId"] = paymentId, ["workflow"] = workflow };
                Deliveries(J.P(await party.Call(new JsonObject { ["op"] = "start", ["now"] = now, ["conversation"] = J.S(J.P(ctx, "header"), "conversation") ?? paymentId, ["workflow"] = workflow }), "deliveries"));
                // The start is in the journal before the server is asked (StartRoot), so a binding that stops here starts it once.
                await StartRoot(rootId, workflow, J.S(doc, "release")!, ctx);
                if (RootOf(rootId) is { } r) serviceParty!.Root(r.Pid, workflow);
                ServiceLines.Out("STARTED", new JsonObject { ["paymentId"] = paymentId, ["rootId"] = rootId });
                return true;
            }
            case "ack":
            {
                var rootId = J.S(c, "rootId")!;
                if (J.P(servedRoots, rootId) is JsonObject s) s["reported"] = true;
                // An ended payment the host recorded leaves the binding's state, so its journal stays the size of what is open.
                if (RootOf(rootId) is { } r)
                {
                    roots.Remove(r);
                    foreach (var i in insts.Values.Where(i => Top(i) == r).ToList()) insts.Remove(i.Pid);
                    feeds.Remove(r.Pid);
                }
                await Save(null);
                return true;
            }
            case "unregister":
            {
                await rest.DeleteDeployment(deploymentId);
                await journal!.Close(new JsonObject { ["deployment"] = deploymentId, ["tenant"] = tenant, ["payments"] = servedRoots.Count, ["restarts"] = restarts });
                ServiceLines.Out("UNREGISTERED", new JsonObject { ["deployment"] = deploymentId, ["tenant"] = tenant });
                return false;
            }
            default: throw new InvalidOperationException($"unknown command {J.S(c, "op")}");
        }
    }

    static Inst Top(Inst i) { while (i.Parent is not null) i = i.Parent; return i; }

    /// <summary>
    /// The live path of every payment the binding serves (R-05-57): each root's payment followed on the outbox, its rows delivered to
    /// that root (a message through the front door, which correlates it), at most every quarter second of wall time.
    /// </summary>
    async Task<bool> PollServed()
    {
        if (LiveSignals.Source is not { } source || Wall() - servedPolled < 250) return false;
        servedPolled = Wall();
        var moved = false;
        foreach (var root in roots.ToList())
        {
            if (root.Ended) continue;
            if (!feeds.TryGetValue(root.Pid, out var feed))
                feeds[root.Pid] = feed = await source.Follow(root.Ref, "flowable", root.Pid, root.Label);
            if (feed is null) continue;
            foreach (var row in await feed.Poll())
            {
                var (name, data) = LiveSignals.EventOf(row, J.P(root.State, "header"));
                string result;
                if (root.Ended) result = "ended";
                else if (name == "message") { await FrontDoor(data); result = "delivered"; }
                else if (Interp.OperatorControls.Contains(J.S(data, "signal"))) result = await Operator(J.S(data, "signal")!, root) ? "delivered" : "nothing parked";
                else { await Signal(J.S(data, "signal")!, root); result = "delivered"; }
                await feed.Ack(row, result);
                moved = true;
            }
        }
        return moved;
    }

    /// <summary>Each payment whose root ended and left the server, told to the host once with its observation.</summary>
    async Task TellEnded()
    {
        foreach (var root in roots.Where(r => r.Ended && !told.Contains(r.Label)).ToList())
        {
            if (J.P(servedRoots, root.Label) is not JsonObject s || J.Truthy(J.P(s, "reported"))) continue;
            if ((await rest.Executions(root.Pid)).Count > 0) continue;
            told.Add(root.Label);
            var obs = new JsonObject
            {
                ["target"] = "flowable",
                ["instances"] = new JsonArray(Observe(root)),
                ["notes"] = J.Arr(notes),
                ["engine"] = new JsonObject
                {
                    ["mode"] = "production", ["deployment"] = deploymentId, ["tenant"] = tenant, ["restarts"] = restarts,
                    ["journal"] = new JsonObject { ["run"] = journal!.Key, ["subject"] = journal.Subject },
                    ["distribution"] = new JsonObject { ["id"] = J.S(input, "distribution"), ["deploymentName"] = $"transacthub-{J.S(input, "distribution")}" },
                    ["service"] = new JsonObject { ["pid"] = Environment.ProcessId, ["payments"] = servedRoots.Count },
                },
            };
            ServiceLines.Out("ENDED", new JsonObject { ["paymentId"] = J.S(s, "paymentId"), ["rootId"] = root.Label, ["observation"] = obs });
        }
    }
}
