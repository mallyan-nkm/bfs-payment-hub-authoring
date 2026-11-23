using System.Diagnostics;
using System.Text.Json.Nodes;
using TransactHub.Engines;
using TransactHub.Store;

namespace TransactHub.EngineHost;

/// <summary>
/// The registry of the distributions one engine host serves (Issue I-114, R-09-66): a host per distribution, started when the
/// distribution is installed and stopped when it is removed; side by side while several are installed, active, draining or
/// drained. The store is the only source of the states: the release API's activation, draining and removal (R-09-48 to R-09-57)
/// are what the supervisor follows. A switch-over in the store makes the new distribution's host current on the engine (on
/// Temporal, the worker deployment's current version) in the next reconcile, and every payment accepted from then is pinned to
/// it; a payment pinned before keeps running on the host of its own distribution. A draining distribution is recorded drained
/// once none of its payments is open, and its host keeps running, for late messages, until the distribution is removed (which the
/// registry refuses while a payment is open or before the longest declared wait has passed).
/// </summary>
public sealed class Supervisor(StoreOptions store, string engine, string bank, string environment, string plansDir)
{
    public const string Actor = "engine-host";

    sealed class Child
    {
        public required string Distribution;
        public required Process Process;
        public string Url = "";
        public bool Current, Stopping;
        public DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly Dictionary<string, Child> children = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> states = new(StringComparer.Ordinal);
    readonly List<JsonObject> events = new();
    readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>The plan of a distribution's host: the distribution's copy, as the front door keeps it (R-09-55).</summary>
    public static string PlanFile(string dir, string distribution) => Path.Combine(dir, $"{Safe(distribution)}.json");
    public static string Safe(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[^A-Za-z0-9._-]+", "_");

    void Event(string what, string distribution, JsonObject? more = null)
    {
        var e = new JsonObject { ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["event"] = what, ["distribution"] = distribution };
        foreach (var (k, v) in more ?? new JsonObject()) e[k] = J.Clone(v);
        lock (events) events.Add(e);
        Console.Error.WriteLine($"engine host {engine}: {what} {distribution}{(more is null ? "" : " " + J.Str(more))}");
    }

    /// <summary>The token of this run's hosts (R-08-22): the configured one, else one made for this run.</summary>
    string hostToken = "";

    public async Task Run(string urls, CancellationToken ct)
    {
        // The supervisor's callers send TH_ENGINE_HOST_TOKEN when one is set; its hosts always take a token, this run's own if none is.
        var configured = await TransactHub.Security.HostToken.FromEnvironment();
        hostToken = configured ?? TransactHub.Security.HostToken.New();
        Http.CallerToken = hostToken;
        if (configured is null) Console.Error.WriteLine($"engine host {engine}: {TransactHub.Security.HostToken.Setting} is not set; the supervisor answers on loopback only and its hosts take this run's own token (R-08-22)");
        using var http = new Http(Route, configured);
        http.Start(urls);
        var serving = http.Serve(ct);
        Console.Out.WriteLine($"LISTENING {http.Url}");
        await Console.Out.FlushAsync(ct);
        var every = HostStore.Duration("TH_HOST_POLL", TimeSpan.FromSeconds(2));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Reconcile(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"engine host {engine}: reconcile failed: {e.Message}"); }
                await Task.Delay(every, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // The supervisor stops; the distributions stay registered on the engine: a supervisor that starts again takes them back.
            foreach (var c in children.Values) { try { c.Process.Kill(entireProcessTree: true); } catch { } }
            await serving;
        }
    }

    /// <summary>Once: every distribution of the bank brought to the state the store gives it.</summary>
    public async Task Reconcile(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = StoreDbContext.Open(store);
            var reg = new DistributionRegistry(db);
            foreach (var dist in await reg.Of(bank, ct))
            {
                var state = await reg.StateIn(dist, environment, ct);
                if (state == "draining") state = await reg.Refresh(dist, environment, Actor, ct);
                if (state is null) continue;
                if (!states.TryGetValue(dist, out var was) || was != state) { states[dist] = state; Event($"state {state}", dist); }
                children.TryGetValue(dist, out var child);
                if (state == "removed")
                {
                    if (child is not null && !child.Stopping) await StopChild(child);
                    continue;
                }
                if (child is null || child.Process.HasExited)
                {
                    var plan = PlanFile(plansDir, dist);
                    if (!File.Exists(plan)) { if (child is null) Event("no plan for its host", dist, new JsonObject { ["plan"] = plan }); continue; }
                    child = Spawn(dist, plan);
                    children[dist] = child;
                }
                if (state == "active" && !child.Current)
                {
                    await child.Ready.Task.WaitAsync(TimeSpan.FromMinutes(2), ct);
                    var (status, body) = await Http.Call("POST", $"{child.Url}/current");
                    if (status == 200)
                    {
                        foreach (var o in children.Values) o.Current = false;
                        child.Current = true;
                        Event("current on the engine", dist, body);
                    }
                }
            }
        }
        finally { gate.Release(); }
    }

    Child Spawn(string dist, string plan)
    {
        var self = typeof(Supervisor).Assembly.Location;
        var psi = new ProcessStartInfo("dotnet", [self, "serve", "--engine", engine, "--distribution", dist, "--plan", plan, "--bank", bank, "--environment", environment])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // The host takes only this run's token, given by reference (R-08-22).
        psi.Environment[TransactHub.Security.HostToken.Setting] = "env:TH_ENGINE_HOST_TOKEN_VALUE";
        psi.Environment["TH_ENGINE_HOST_TOKEN_VALUE"] = hostToken;
        var p = Process.Start(psi)!;
        var c = new Child { Distribution = dist, Process = p };
        _ = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync() is string line)
                if (line.StartsWith("LISTENING ")) { c.Url = line["LISTENING ".Length..].Trim(); c.Ready.TrySetResult(); }
            c.Ready.TrySetException(new InvalidOperationException($"the host of {dist} exited before it served"));
        });
        _ = Task.Run(async () => { while (await p.StandardError.ReadLineAsync() is string line) Console.Error.WriteLine($"[{dist}] {line}"); });
        Event("host started", dist, new JsonObject { ["pid"] = p.Id });
        return c;
    }

    /// <summary>A removed distribution's host is asked to unregister from the engine and stop; it refuses while it still runs a payment.</summary>
    async Task StopChild(Child c)
    {
        c.Stopping = true;
        try
        {
            await c.Ready.Task.WaitAsync(TimeSpan.FromMinutes(1));
            var (status, body) = await Http.Call("POST", $"{c.Url}/stop");
            if (status != 200) { c.Stopping = false; Event("host refused to stop", c.Distribution, body); return; }
            Event("unregistered from the engine", c.Distribution, body);
        }
        catch (Exception e) { Event("host did not answer its stop", c.Distribution, new JsonObject { ["error"] = e.Message }); }
        if (!c.Process.WaitForExit(TimeSpan.FromSeconds(30))) c.Process.Kill(entireProcessTree: true);
        children.Remove(c.Distribution);
        Event("host stopped", c.Distribution);
    }

    async Task<(int, JsonNode?)> Route(string method, string path, JsonObject? body)
    {
        if (method == "POST" && path == "/payments")
        {
            var paymentId = J.S(body, "paymentId") ?? throw new ArgumentException("paymentId is required");
            string dist;
            await using (var db = StoreDbContext.Open(store))
                dist = await new DistributionRegistry(db).Pin(paymentId, bank, environment, Actor);
            // A distribution activated a moment ago may not have its host yet.
            Child? c = null;
            for (var until = DateTime.UtcNow.AddMinutes(2); DateTime.UtcNow < until; await Task.Delay(250))
            {
                if (children.TryGetValue(dist, out c) && c.Ready.Task.IsCompletedSuccessfully) break;
                await Reconcile();
            }
            if (c is null || !c.Ready.Task.IsCompletedSuccessfully) return (503, new JsonObject { ["error"] = $"no host serves {dist}" });
            var (status, res) = await Http.Call("POST", $"{c.Url}/payments", body);
            res["distribution"] = dist;
            return (status, res);
        }
        if (method == "GET" && path.StartsWith("/payments/"))
        {
            var id = Uri.UnescapeDataString(path["/payments/".Length..]);
            string? dist;
            await using (var db = StoreDbContext.Open(store)) dist = await new DistributionRegistry(db).PinOf(id);
            if (dist is null || !children.TryGetValue(dist, out var c)) return (404, new JsonObject { ["error"] = $"{id} is not running here" });
            var (status, res) = await Http.Call("GET", $"{c.Url}/payments/{Uri.EscapeDataString(id)}");
            res["distribution"] = dist;
            return (status, res);
        }
        if (method == "GET" && path == "/distributions")
        {
            await using var db = StoreDbContext.Open(store);
            var reg = new DistributionRegistry(db);
            var open = await reg.OpenPayments();
            var list = new JsonArray();
            foreach (var d in await reg.Of(bank))
            {
                children.TryGetValue(d, out var c);
                JsonObject? engineView = null;
                if (c is not null && c.Ready.Task.IsCompletedSuccessfully) { try { engineView = (await Http.Call("GET", $"{c.Url}/status")).body; } catch { } }
                list.Add(new JsonObject
                {
                    ["id"] = d, ["state"] = await reg.StateIn(d, environment), ["open"] = open.GetValueOrDefault(d),
                    ["host"] = c is null ? null : new JsonObject { ["pid"] = c.Process.HasExited ? null : c.Process.Id, ["current"] = c.Current, ["url"] = c.Url, ["since"] = c.StartedAt.ToString("O") },
                    ["engine"] = engineView,
                });
            }
            return (200, new JsonObject { ["engine"] = engine, ["bank"] = bank, ["environment"] = environment, ["distributions"] = list });
        }
        if (method == "GET" && path == "/events") { lock (events) return (200, new JsonObject { ["events"] = new JsonArray(events.Select(e => (JsonNode?)J.Clone(e)).ToArray()) }); }
        if (method == "POST" && path == "/reconcile") { await Reconcile(); return (200, new JsonObject { ["ok"] = true }); }
        return (404, new JsonObject { ["error"] = $"{method} {path}" });
    }
}
