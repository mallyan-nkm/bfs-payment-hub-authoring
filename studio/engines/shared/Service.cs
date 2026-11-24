using System.Text.Json.Nodes;

namespace TransactHub.Engines;

// One engine service per distribution on the runtime's engine host (Issue I-165, R-09-196 to R-09-199). A harness started with
// `serve <plan>` runs every payment of its distribution in one in-memory service that keeps its history in the runtime's store:
// each change to the service (a message an instance takes, an episode it ran, a payment it started) is a fact on the
// distribution's own chain, written before the change is made, and a service that starts again rebuilds itself from those facts.
// The studio's kit runs never serve and never keep a history; nothing here runs for them.

/// <summary>An engine service's history, as the runtime keeps it: insert-only facts on one chain per engine and distribution.</summary>
public interface IEngineHistory
{
    /// <summary>The chain's subject in the store.</summary>
    string Subject { get; }
    /// <summary>Every fact of the chain, in order: its code and its data.</summary>
    IReadOnlyList<(string code, JsonObject data)> Load();
    /// <summary>One fact, written before the caller goes on.</summary>
    void Append(string code, JsonObject data);
    /// <summary>
    /// The payloads whose references start with <paramref name="prefix"/> (a root instance and its children), as their claim-check
    /// facts give them, put back into <paramref name="store"/> without a journal entry. False when the store holds none.
    /// </summary>
    bool RestorePayloads(ClaimCheckStore store, string prefix);
    /// <summary>The chain checked: its facts and whether every link holds.</summary>
    JsonObject Verify();
}

public static class EngineHistory
{
    /// <summary>Set by a runtime build of a harness when the deployment names a store: the history of an engine's distribution.</summary>
    public static Func<string, string, IEngineHistory>? Open;

    /// <summary>A short key of its own for a payment's run, as the engine host makes it: the store's subject ids are at most 64 characters.</summary>
    public static string Key(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant()[..24];
}

/// <summary>
/// The workers of a distribution's service: one router per platform workflow (each engine package names its own queues and an
/// installation may serve each workflow's queues apart), and an attempt goes to the router of its payment's workflow, found by
/// the root of its instance id. Every attempt names the distribution (R-09-130).
/// </summary>
public sealed class ServiceParty : IParty
{
    readonly Dictionary<string, WorkerRouter> routers = new(StringComparer.Ordinal);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> roots = new(StringComparer.Ordinal);
    readonly WorkerRouter first;

    /// <param name="plans">Each platform workflow's plan (its <c>workers</c> and <c>queues</c>), by workflow.</param>
    public ServiceParty(JsonObject plans, string? distribution)
    {
        foreach (var (wf, p) in plans)
            routers[wf] = new WorkerRouter(J.P(p, "workers") as JsonObject ?? new JsonObject(), J.P(p, "queues") as JsonObject ?? new JsonObject(), distribution);
        if (routers.Count == 0) throw new InvalidOperationException("a service runs at least one platform workflow");
        first = routers.Values.First();
    }

    /// <summary>A root instance of a payment of this workflow.</summary>
    public void Root(string rootId, string workflow) => roots[rootId] = workflow;

    WorkerRouter For(string? instance)
    {
        var root = (instance ?? "").Split('/')[0];
        return roots.TryGetValue(root, out var wf) && routers.TryGetValue(wf, out var r) ? r : first;
    }

    public Task<JsonObject> Call(JsonObject req) => J.S(req, "op") switch
    {
        "attempt" => For(J.S(req, "instance")).Call(req),
        "start" => (J.S(req, "workflow") is string wf && routers.TryGetValue(wf, out var r) ? r : first).Call(req),
        "sent" => Task.FromResult(new JsonObject { ["messages"] = new JsonArray() }),
        _ => first.Call(req),
    };

    public void Dispose() { foreach (var r in routers.Values) r.Dispose(); }
}

/// <summary>
/// A serving harness's lines with its engine host. It reads commands, one JSON object a line, on standard input (<c>start</c>,
/// <c>ack</c>), and answers on standard output: <c>READY</c> once it serves, <c>RESUMED</c> for each payment it took back from its
/// history, <c>STARTED</c> once a payment's start is in the history, <c>ENDED</c> with a payment's observation, <c>ERROR</c>. When
/// its input ends (its host has gone) it ends too, so that two services never write one chain.
/// </summary>
public static class ServiceLines
{
    static readonly object gate = new();

    public static void Out(string kind, JsonNode? body)
    {
        lock (gate)
        {
            Console.Out.WriteLine(body is null ? kind : $"{kind} {J.Str(body)}");
            Console.Out.Flush();
        }
    }

    /// <summary>Reads the host's commands until its input ends, then ends the process.</summary>
    public static void Read(Action<JsonObject> command) => new Thread(() =>
    {
        while (Console.In.ReadLine() is string line)
        {
            if (line.Trim().Length == 0) continue;
            try { command((JsonObject)J.Parse(line)!); }
            catch (Exception e) { Out("ERROR", new JsonObject { ["error"] = e.Message, ["line"] = line.Length > 200 ? line[..200] : line }); }
        }
        Console.Error.WriteLine("[service] the host's input ended: the service stops");
        Environment.Exit(0);
    }) { IsBackground = true, Name = "th-service-input" }.Start();

    /// <summary>
    /// The plan of a distribution's service from its host's plan: one plan per platform workflow under <c>plans</c> (or the plan's own
    /// workflow), the documents of them all in one deployment, and the first one's settings.
    /// </summary>
    public static (JsonObject plans, JsonObject documents, JsonObject first) Of(JsonObject input)
    {
        var plans = J.P(input, "plans") is JsonObject ps && ps.Count > 0 ? (JsonObject)J.Clone(ps)! : new JsonObject { [J.S(J.P(input, "built"), "workflow") ?? "workflow"] = J.Clone(input) };
        var documents = new JsonObject();
        foreach (var (_, p) in plans)
            foreach (var (k, d) in J.Obj(J.P(p, "documents"))) if (!documents.ContainsKey(k)) documents[k] = J.Clone(d);
        return (plans, documents, (JsonObject)plans.First().Value!);
    }
}
