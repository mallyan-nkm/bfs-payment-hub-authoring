using System.Diagnostics;
using System.Text.Json.Nodes;

namespace TransactHub.Engines;

/// <summary>
/// What is deployed with the worker: the exported documents (workflows in their expanded form only,
/// R-09-17), activity contracts, the scheme catalogue and the run configuration. It is immutable,
/// so reading it from orchestration code is deterministic, as reading code is.
/// </summary>
public sealed class Deployment
{
    public static Deployment Current = null!;

    readonly Dictionary<string, JsonObject> docs = new();
    public readonly JsonObject Config;
    public readonly long Start;
    public readonly int ChunkSize;
    /// <summary>
    /// How many items of a forEach run side by side when the forEach declares no concurrency and its body holds no state:
    /// the binding's <c>defaults.forEachConcurrency</c> (R-09-45). One, item by item, unless the binding says more.
    /// </summary>
    public int ItemConcurrency { get; init; } = 1;

    public Deployment(JsonObject documents, JsonObject config, long start, int chunkSize)
    {
        foreach (var (k, v) in documents) if (v is JsonObject o) docs[k] = o;
        Config = config;
        Start = start;
        ChunkSize = chunkSize;
    }

    public JsonObject? Doc(string key) => docs.TryGetValue(key, out var d) ? d : null;
    public IEnumerable<JsonObject> AllDocs() => docs.Values;
    public IEnumerable<KeyValuePair<string, JsonObject>> Entries() => docs;

    bool Available(string key, long elapsed)
    {
        var from = J.S(J.P(Config, "availableFrom"), key);
        return from is null || elapsed >= Duration.Ms(from);
    }

    static int CompareReleases(string a, string b)
    {
        var x = a.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var y = b.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>A major line to a document: an exact release, else the latest release available at this virtual time.</summary>
    public JsonObject? Resolve(string name, string? release, long elapsed)
    {
        if (release is not null) return Doc($"{name}@{release}");
        if (docs.TryGetValue(name, out var d)) return d;
        var keys = docs.Keys.Where(k => k.StartsWith(name + "@") && Available(k, elapsed)).ToList();
        keys.Sort((a, b) => CompareReleases(a.Split('@')[1], b.Split('@')[1]));
        return keys.Count > 0 ? docs[keys[^1]] : null;
    }

    public JsonObject? Scheme(string scope) => docs.Values.FirstOrDefault(d => J.S(d, "kind") == "scheme" && J.S(d, "scope") == scope);

    public JsonObject? CatalogueEntry(string type)
    {
        var scope = string.Join('.', type.Split('.').Take(2));
        return J.A(Scheme(scope), "messages").FirstOrDefault(m => J.S(m, "id") == type) as JsonObject;
    }

    /// <summary>Hook bindings in force on a date (R-06-13).</summary>
    public List<JsonObject> BindingsAt(string date) => J.A(Config, "bindings").OfType<JsonObject>()
        .Where(b => string.CompareOrdinal(J.S(b, "validFrom") ?? "", date) <= 0 && (J.S(b, "validTo") is not string to || string.CompareOrdinal(to, date) >= 0)).ToList();

    /// <summary>
    /// The value of a parameter (<c>owner#name</c>) in force on a date, from the configuration's <c>values</c>, as the studio looks it
    /// up (R-04-09, R-05-47): the latest validFrom not after the date, within its validTo. Null when none is set.
    /// </summary>
    public string? ValueAt(string key, string date) => J.A(Config, "values").OfType<JsonObject>()
        .Where(v => J.S(v, "parameter") == key && string.CompareOrdinal(J.S(v, "validFrom") ?? "", date) <= 0 && (J.S(v, "validTo") is not string to || string.CompareOrdinal(to, date) >= 0))
        .OrderBy(v => J.S(v, "validFrom"), StringComparer.Ordinal).Select(v => J.S(v, "value")).LastOrDefault();

    public JsonObject? Stub(string step, string activity)
    {
        var stubs = J.P(Config, "stubs");
        return (J.P(stubs, $"step:{step}") ?? J.P(stubs, activity)) as JsonObject;
    }
}

/// <summary>
/// The claim-check store (R-02-08, R-09-10): the payment payload lives here, outside the engine, and
/// the orchestration holds a reference. An attempt stages its writes; the orchestration commits them
/// only when it accepts the result, so a late result that was abandoned never reaches the payment.
/// </summary>
/// <summary>
/// What a durable store is told of every change to a payload, before the change is made (the runtime's
/// store, plan W2). The studio's harnesses run without one; the runtime's builds of them install one.
/// </summary>
public interface IClaimCheckJournal
{
    /// <summary>A payload was created; `accept` (tenant, scheme, direction, release) when it is a root's, to accept its payments.</summary>
    void Created(string reff, JsonObject payload, JsonObject? accept);
    void Staged(string token, string reff, IReadOnlyList<(string path, JsonNode? value)> writes);
    void Committed(string token, string reff);
    void Forked(string from, string to, JsonObject payload);
    void Copied(string from, string to, JsonObject values);
    /// <summary>A store that reports to this journal was made; the journal checks what it rebuilds against it.</summary>
    void Attached(ClaimCheckStore store);
    /// <summary>What the journal's store holds after the run (the observation's engine.store), or null.</summary>
    JsonObject? Report();
}

public sealed class ClaimCheckStore
{
    /// <summary>The journal every new store reports to, when one is installed (by the runtime's module initializer).</summary>
    public static IClaimCheckJournal? DefaultJournal;
    readonly IClaimCheckJournal? journal = DefaultJournal;
    public ClaimCheckStore() { journal?.Attached(this); }
    static readonly string[] StateParts = { "header", "facts", "status", "messages" };
    // A lock per payload, never one for the store (R-09-46, I-78): the items of one payload and other payloads go side by side.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> gates = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject> payloads = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string reff, List<(string path, JsonNode? value)> writes)> staged = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> committed = new();
    public readonly Dictionary<string, JsonNode?> RuleTraces = new();
    long seq;

    object Gate(string r) => gates.GetOrAdd(r, _ => new object());

    public static bool IsStatePath(string p) => StateParts.Contains(p.Split('.')[0].Replace("[]", ""));

    /// <summary>What the store needs to accept a root's payments: the tenant, and the scheme and direction from the header.</summary>
    /// <summary>The distribution a payment is pinned to, when the host runs one (R-09-53): its release id in the store.</summary>
    public static string? Distribution;

    public static JsonObject AcceptFor(JsonObject state, string? tenant, string docKey)
    {
        var header = J.P(state, "header");
        return new JsonObject { ["tenant"] = tenant ?? "tenant", ["scheme"] = J.S(header, "scheme") ?? docKey.Split('.')[0], ["direction"] = J.S(header, "direction") ?? "outward", ["release"] = Distribution ?? docKey };
    }

    /// <summary>Split a context into what the engine holds and what the store holds.</summary>
    public static (JsonObject state, JsonObject payload) Split(JsonObject ctx)
    {
        var state = new JsonObject();
        var payload = new JsonObject();
        foreach (var (k, v) in ctx.ToList())
        {
            if (StateParts.Contains(k)) state[k] = J.Clone(v);
            else payload[k] = J.Clone(v);
        }
        return (state, payload);
    }

    public void Create(string r, JsonObject payload, JsonObject? accept = null)
    {
        lock (Gate(r))
        {
            journal?.Created(r, (JsonObject)J.Clone(payload)!, accept);
            payloads[r] = payload;
        }
    }

    public JsonObject Payload(string r) { lock (Gate(r)) return (JsonObject)J.Clone(payloads.TryGetValue(r, out var p) ? p : new JsonObject())!; }

    /// <summary>The whole context: the engine's state and the payload behind the reference.</summary>
    public JsonObject Full(JsonNode? state, string r)
    {
        var o = new JsonObject();
        foreach (var (k, v) in J.Obj(state).ToList()) o[k] = J.Clone(v);
        foreach (var (k, v) in Payload(r).ToList()) o[k] = J.Clone(v);
        return o;
    }

    /// <summary>The context of one item's attempt: the item itself rather than the whole item list.</summary>
    public JsonObject FullForItem(JsonNode? state, string r, int index)
    {
        var o = new JsonObject();
        foreach (var (k, v) in J.Obj(state).ToList()) o[k] = J.Clone(v);
        lock (Gate(r))
        {
            var p = payloads.TryGetValue(r, out var x) ? x : new JsonObject();
            foreach (var (k, v) in p) if (k != "items") o[k] = J.Clone(v);
            o["__item"] = J.Clone(p["items"] is JsonArray a && index < a.Count ? a[index] : new JsonObject());
        }
        return o;
    }

    public string Stage(string r, List<(string path, JsonNode? value)> writes)
    {
        lock (Gate(r))
        {
            var token = $"{r}!{Interlocked.Increment(ref seq)}";
            journal?.Staged(token, r, writes);
            staged[token] = (r, writes);
            return token;
        }
    }

    public void Commit(string token)
    {
        if (!staged.TryGetValue(token, out var s)) return;
        lock (Gate(s.reff))
        {
            if (committed.ContainsKey(token)) return;
            journal?.Committed(token, s.reff);
            committed[token] = true;
            var p = payloads.GetOrAdd(s.reff, _ => new JsonObject());
            foreach (var (path, v) in s.writes) J.Set(p, path, J.Clone(v));
        }
    }

    public void Fork(string from, string to)
    {
        var copy = Payload(from);
        lock (Gate(to))
        {
            journal?.Forked(from, to, (JsonObject)J.Clone(copy)!);
            payloads[to] = copy;
        }
    }

    public void Copy(string from, string to, IEnumerable<string> paths)
    {
        var src = Payload(from);
        var values = new JsonObject();
        foreach (var p in paths) values[p] = J.Clone(J.Get(src, p));
        lock (Gate(to))
        {
            journal?.Copied(from, to, (JsonObject)J.Clone(values)!);
            Apply(to, values);
        }
    }

    /// <summary>Values copied into a payload as a copy fact records them: a path whose value is null is left as it is.</summary>
    public void CopyValues(string to, JsonObject values)
    {
        lock (Gate(to)) Apply(to, values);
    }

    void Apply(string to, JsonObject values)
    {
        var dst = payloads.GetOrAdd(to, _ => new JsonObject());
        foreach (var (p, v) in values) if (v is not null) J.Set(dst, p, J.Clone(v));
    }

    public int Count(string r, string path) { lock (Gate(r)) return J.Get(payloads.TryGetValue(r, out var p) ? p : null, path) is JsonArray a ? a.Count : 0; }
}

/// <summary>The party service over JSON lines (studio/src/conformance/party-server.ts).</summary>
/// Everything outside the orchestration: the party service in a kit run, or the deployed workers.
public interface IParty : IDisposable
{
    Task<JsonObject> Call(JsonObject req);
}

public sealed class PartyClient : IParty
{
    readonly Process proc;
    readonly SemaphoreSlim lk = new(1, 1);

    public PartyClient(string studioRoot)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(studioRoot, "node_modules", ".bin", "tsx"),
            // DTFX_PARTY_SERVER names another copy of the party server, to try a change to it out of tree.
            Arguments = Environment.GetEnvironmentVariable("DTFX_PARTY_SERVER") ?? "src/conformance/party-server.ts",
            WorkingDirectory = studioRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        proc = Process.Start(psi) ?? throw new InvalidOperationException("party service did not start");
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"[party] {e.Data}"); };
        proc.BeginErrorReadLine();
    }

    public async Task<JsonObject> Call(JsonObject req)
    {
        using var span = J.S(req, "op") == "attempt" ? EngineTelemetry.Attempt(req, null) : null;
        await lk.WaitAsync();
        try
        {
            await proc.StandardInput.WriteLineAsync(J.Str(req));
            await proc.StandardInput.FlushAsync();
            var line = await proc.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("party service closed");
            var res = (JsonObject)J.Parse(line)!;
            if (J.S(res, "error") is string err) throw new InvalidOperationException($"party: {err}");
            span?.Answered(res);
            return res;
        }
        finally { lk.Release(); }
    }

    public void Dispose()
    {
        try { proc.StandardInput.Close(); if (!proc.WaitForExit(2000)) proc.Kill(); } catch { }
    }
}

/// A deployment (Part 9; R-09-04, R-09-05): each activity attempt goes over HTTP to the worker
/// process of the task queue the binding derives from the activity's scope. Workers are separate
/// processes; a task that is not on a worker's queue is refused by that worker.
/// Each attempt names the distribution the engine runs (R-09-130): an installation's workers can then serve several
/// distributions, and the worker hands the name to the native host, which serves the attempt from that distribution's build.
public sealed class WorkerRouter : IParty
{
    readonly Dictionary<string, string> workers; // queue -> base URL
    readonly Dictionary<string, string> queues;  // scope -> queue
    /// <summary>The distribution every attempt names (the engine's input <c>distribution</c>, the payment's pin); null names none.</summary>
    public readonly string? Distribution;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Calls = new();
    /// <summary>
    /// Told of every attempt a worker answered, with the request and the answer, and awaited before the attempt is handed back.
    /// The studio's harnesses leave it unset; the runtime's builds set it to record each step's outcome as a fact in the store.
    /// </summary>
    public static Func<JsonObject, JsonObject, Task>? Answered;

    public WorkerRouter(JsonObject workers, JsonObject queues, string? distribution = null)
    {
        Distribution = string.IsNullOrEmpty(distribution) ? null : distribution;
        this.workers = workers.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>().TrimEnd('/'));
        this.queues = queues.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
    }

    static string ScopeOf(string name)
    {
        var parts = name.Split('.');
        return parts[0] is "p" or "b" ? parts[0] : $"{parts[0]}.{parts[1]}";
    }

    /// <summary>How long an attempt waits for a worker that drains or restarts (R-09-63) before it is answered as an error.</summary>
    public static TimeSpan DrainWait = TimeSpan.FromMinutes(10);

    async Task<JsonObject> Post(string url, JsonObject body)
    {
        // A worker that drains answers 503; one that restarts does not answer for a moment. The attempt waits for the worker to
        // take it again, so no attempt is lost to a drain (R-09-63).
        var until = DateTime.UtcNow + DrainWait;
        HttpResponseMessage res;
        while (true)
        {
            try
            {
                using var content = new StringContent(J.Str(body), System.Text.Encoding.UTF8, "application/json");
                res = await http.PostAsync(url, content);
                if (res.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable || DateTime.UtcNow > until) break;
                res.Dispose();
            }
            catch (HttpRequestException) when (DateTime.UtcNow < until) { }
            await Task.Delay(250);
        }
        using var _res = res;
        var text = await res.Content.ReadAsStringAsync();
        var o = (JsonObject)J.Parse(text)!;
        if (J.S(o, "error") is string err) throw new InvalidOperationException($"worker {url}: {err}");
        return o;
    }

    public async Task<JsonObject> Call(JsonObject req)
    {
        switch (J.S(req, "op"))
        {
            case "open":
                return new JsonObject { ["ok"] = true };
            case "start":
            {
                var all = new JsonArray();
                foreach (var url in workers.Values.Distinct())
                {
                    var r = await Post($"{url}/start", new JsonObject { ["now"] = J.Clone(J.P(req, "now")), ["conversation"] = J.S(req, "conversation") });
                    foreach (var d in J.A(r, "deliveries")) all.Add(J.Clone(d));
                }
                return new JsonObject { ["deliveries"] = all };
            }
            case "attempt":
            {
                var activity = J.S(req, "activity")!;
                var scope = ScopeOf(activity);
                if (!queues.TryGetValue(scope, out var queue) || !workers.TryGetValue(queue, out var url))
                    throw new InvalidOperationException($"No worker serves queue {queue ?? "?"} for {activity}");
                Calls.AddOrUpdate(queue, 1, (_, n) => n + 1);
                var body = (JsonObject)J.Clone(req)!;
                body.Remove("op");
                if (Distribution is not null && J.S(body, "distribution") is null) body["distribution"] = Distribution;
                // One span per attempt; HttpClient carries its context to the worker (traceparent).
                using var span = EngineTelemetry.Attempt(req, queue);
                try
                {
                    var res = await Post($"{url}/attempt", body);
                    span.Answered(res);
                    if (Answered is { } answered) await answered(body, res);
                    return res;
                }
                catch (Exception e) { span.Failed(e); throw; }
            }
            case "sent":
            {
                var all = new JsonArray();
                foreach (var url in workers.Values.Distinct())
                {
                    var text = await http.GetStringAsync($"{url}/sent");
                    foreach (var m in J.A((JsonObject)J.Parse(text)!, "messages")) all.Add(J.Clone(m));
                }
                return new JsonObject { ["messages"] = all };
            }
            default:
                throw new InvalidOperationException($"Unknown op {J.S(req, "op")}");
        }
    }

    public void Dispose() => http.Dispose();
}
