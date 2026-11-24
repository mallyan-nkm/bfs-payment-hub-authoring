using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using TransactHub.Engines;

namespace TransactHub.EngineHost;

/// <summary>
/// A distribution's one engine service (Issue I-165, R-09-196 to R-09-199): the engine's runtime harness started once by the
/// distribution's host with <c>serve &lt;plan&gt;</c>, for as long as the host runs, serving every payment the host starts. On
/// DTFx and Elsa it keeps its history on the distribution's chain in the store; on Flowable it is the one binding of the
/// distribution's tenant, with its one deployment. The service speaks lines (<see cref="ServiceLines"/>): the host writes
/// <c>start</c> and <c>ack</c>, the service answers <c>READY</c>, <c>RESUMED</c>, <c>STARTED</c> and <c>ENDED</c>. A service that
/// ends while its host runs ends the host too, so the supervisor starts both again and the service rebuilds from its history.
/// </summary>
public sealed class HarnessService(string engine, string dll, string planFile, IReadOnlyDictionary<string, string> environment) : IAsyncDisposable
{
    Process process = null!;
    readonly TaskCompletionSource<JsonObject> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> started = new(StringComparer.Ordinal), ended = new(StringComparer.Ordinal);
    readonly object write = new();
    bool stopping;

    /// <summary>A payment the service took back from its history when it started: its id, root and platform workflow.</summary>
    public Action<string, string, string?>? Resumed;
    /// <summary>The service ended on its own, with its exit code.</summary>
    public Action<int>? Exited;
    public JsonObject Ready => ready.Task.IsCompletedSuccessfully ? ready.Task.Result : new JsonObject();
    public int Pid => process?.Id ?? 0;
    public int Resumes { get; private set; }

    TaskCompletionSource<JsonObject> Started(string paymentId) => started.GetOrAdd(paymentId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
    TaskCompletionSource<JsonObject> Ended(string paymentId) => ended.GetOrAdd(paymentId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    public async Task Start(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", [dll, "serve", planFile]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var (k, v) in environment) psi.Environment[k] = v;
        process = Process.Start(psi)!;
        _ = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is string l) Console.Error.WriteLine($"[{engine} service] {l}"); });
        _ = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is string line) Line(line);
            await process.WaitForExitAsync();
            var why = new InvalidOperationException($"the {engine} service ended ({process.ExitCode})");
            ready.TrySetException(why);
            foreach (var t in started.Values) t.TrySetException(why);
            foreach (var t in ended.Values) t.TrySetException(why);
            if (!stopping) Exited?.Invoke(process.ExitCode);
        });
        await ready.Task.WaitAsync(TimeSpan.FromMinutes(5), ct);
    }

    void Line(string line)
    {
        var sp = line.IndexOf(' ');
        var (kind, body) = sp < 0 ? (line, null) : (line[..sp], J.Parse(line[(sp + 1)..]) as JsonObject);
        var paymentId = J.S(body, "paymentId");
        switch (kind)
        {
            case "READY": ready.TrySetResult(body ?? new JsonObject()); break;
            case "RESUMED":
                Resumes++;
                Resumed?.Invoke(paymentId!, J.S(body, "rootId")!, J.S(body, "workflow"));
                break;
            case "STARTED": Started(paymentId!).TrySetResult(body!); break;
            case "ENDED": Ended(paymentId!).TrySetResult(body!); break;
            case "UNREGISTERED": unregistered.TrySetResult(body ?? new JsonObject()); break;
            case "ERROR":
                Console.Error.WriteLine($"[{engine} service] {line}");
                if (!ready.Task.IsCompleted) ready.TrySetException(new InvalidOperationException($"the {engine} service did not start: {J.S(body, "error")}"));
                break;
            default: Console.Error.WriteLine($"[{engine} service] {line}"); break;
        }
    }

    void Send(JsonObject command)
    {
        lock (write)
        {
            process.StandardInput.WriteLine(J.Str(command));
            process.StandardInput.Flush();
        }
    }

    /// <summary>Starts a payment on the service; ends once its start is in the service's history.</summary>
    public async Task Begin(string paymentId, string rootId, string? workflow, JsonObject context, JsonObject? legs)
    {
        var s = Started(paymentId);
        Send(new JsonObject { ["op"] = "start", ["paymentId"] = paymentId, ["rootId"] = rootId, ["workflow"] = workflow, ["context"] = J.Clone(context), ["legs"] = J.Clone(legs) });
        await s.Task.WaitAsync(TimeSpan.FromMinutes(5));
    }

    /// <summary>Whether a payment's start is in the service's history, once it is.</summary>
    public Task WhenStarted(string paymentId) => Started(paymentId).Task;

    /// <summary>The payment's observation, once it ends.</summary>
    public async Task<JsonObject> EndOf(string paymentId) => (JsonObject)J.Clone((await Ended(paymentId).Task)["observation"])!;

    /// <summary>The host recorded the payment's end in the store: the service records that it need not tell it again.</summary>
    public void Ack(string paymentId, string rootId, string outcome) => Send(new JsonObject { ["op"] = "ack", ["paymentId"] = paymentId, ["rootId"] = rootId, ["outcome"] = outcome });

    public void Command(JsonObject command) => Send(command);

    readonly TaskCompletionSource<JsonObject> unregistered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Flowable: the binding removes the distribution's deployment and closes its journal; answers what it removed.</summary>
    public async Task<JsonObject> Unregister()
    {
        Send(new JsonObject { ["op"] = "unregister" });
        return await unregistered.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    public async ValueTask DisposeAsync()
    {
        stopping = true;
        if (process is null || process.HasExited) return;
        try { process.StandardInput.Close(); } catch { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); } catch { try { process.Kill(entireProcessTree: true); } catch { } }
    }
}
