using System.Diagnostics;
using System.Text.Json.Nodes;

namespace DtfxHarness;

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
public sealed class ClaimCheckStore
{
    static readonly string[] StateParts = { "header", "facts", "status", "messages" };
    readonly object gate = new();
    readonly Dictionary<string, JsonObject> payloads = new();
    readonly Dictionary<string, (string reff, List<(string path, JsonNode? value)> writes)> staged = new();
    readonly HashSet<string> committed = new();
    public readonly Dictionary<string, JsonNode?> RuleTraces = new();
    int seq;

    public static bool IsStatePath(string p) => StateParts.Contains(p.Split('.')[0].Replace("[]", ""));

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

    public void Create(string r, JsonObject payload) { lock (gate) payloads[r] = payload; }

    public JsonObject Payload(string r) { lock (gate) return (JsonObject)J.Clone(payloads.TryGetValue(r, out var p) ? p : new JsonObject())!; }

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
        lock (gate)
        {
            var p = payloads.TryGetValue(r, out var x) ? x : new JsonObject();
            foreach (var (k, v) in p) if (k != "items") o[k] = J.Clone(v);
            o["__item"] = J.Clone(p["items"] is JsonArray a && index < a.Count ? a[index] : new JsonObject());
        }
        return o;
    }

    public string Stage(string r, List<(string path, JsonNode? value)> writes)
    {
        lock (gate)
        {
            var token = $"{r}!{++seq}";
            staged[token] = (r, writes);
            return token;
        }
    }

    public void Commit(string token)
    {
        lock (gate)
        {
            if (!committed.Add(token) || !staged.TryGetValue(token, out var s)) return;
            if (!payloads.TryGetValue(s.reff, out var p)) payloads[s.reff] = p = new JsonObject();
            foreach (var (path, v) in s.writes) J.Set(p, path, J.Clone(v));
        }
    }

    public void Fork(string from, string to) { lock (gate) payloads[to] = (JsonObject)J.Clone(payloads.TryGetValue(from, out var p) ? p : new JsonObject())!; }

    public void Copy(string from, string to, IEnumerable<string> paths)
    {
        lock (gate)
        {
            var src = payloads.TryGetValue(from, out var a) ? a : new JsonObject();
            if (!payloads.TryGetValue(to, out var dst)) payloads[to] = dst = new JsonObject();
            foreach (var p in paths)
            {
                var v = J.Get(src, p);
                if (v is not null) J.Set(dst, p, J.Clone(v));
            }
        }
    }

    public int Count(string r, string path) { lock (gate) return J.Get(payloads.TryGetValue(r, out var p) ? p : null, path) is JsonArray a ? a.Count : 0; }
}

/// <summary>The party service over JSON lines (studio/src/conformance/party-server.ts).</summary>
public sealed class PartyClient : IDisposable
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
        await lk.WaitAsync();
        try
        {
            await proc.StandardInput.WriteLineAsync(J.Str(req));
            await proc.StandardInput.FlushAsync();
            var line = await proc.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("party service closed");
            var res = (JsonObject)J.Parse(line)!;
            if (J.S(res, "error") is string err) throw new InvalidOperationException($"party: {err}");
            return res;
        }
        finally { lk.Release(); }
    }

    public void Dispose()
    {
        try { proc.StandardInput.Close(); if (!proc.WaitForExit(2000)) proc.Kill(); } catch { }
    }
}
