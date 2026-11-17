using System.Text.Json.Nodes;
using DurableTask.Core;

namespace DtfxHarness;

/// <summary>The interpreter's engine operations on a DTFx orchestration context.</summary>
public sealed class DtfxEngine : IEngine
{
    public const string OrchestrationName = "transacthub.interpreter";
    readonly OrchestrationContext ctx;
    public DtfxEngine(OrchestrationContext ctx) { this.ctx = ctx; }

    // The orchestration's own clock: the OrchestratorStarted timestamps in its history (virtual time).
    public long Now => Duration.MsOf(ctx.CurrentUtcDateTime);
    public string InstanceId => ctx.OrchestrationInstance.InstanceId;
    public bool Durable => true;

    public async Task<JsonObject> Activity(string name, JsonObject input) => (JsonObject)J.Parse(await ctx.ScheduleTask<string>(name, "", J.Str(input)))!;

    public Task Timer(long at, CancellationToken ct) => ctx.CreateTimer(Duration.Utc(at), "timer", ct);

    public async Task<JsonObject> Child(string instanceId, JsonObject input) =>
        (JsonObject)J.Parse(await ctx.CreateSubOrchestrationInstance<string>(OrchestrationName, "", instanceId, J.Str(input)))!;

    public async Task<T> WithRetry<T>(RetryOptions options, Func<Task<T>> call) => (await new RetryInterceptor<T>(ctx, options, call).Invoke())!;

    public void SendEvent(string instanceId, string name, JsonObject data) => ctx.SendEvent(new OrchestrationInstance { InstanceId = instanceId }, name, J.Str(data));

    public void ContinueAsNew(JsonObject input) => ctx.ContinueAsNew(J.Str(input));
}

/// <summary>
/// A handler runs as a plain request handler at the interop boundary, with no orchestration and no
/// durable state (Part 9; R-09-14). Its activities are called in process, on the same party service.
/// </summary>
public sealed class PlainEngine : IEngine
{
    readonly Harness h;
    public PlainEngine(Harness h, string id) { this.h = h; InstanceId = id; }
    public long Now => h.Clock.Now;
    public string InstanceId { get; }
    public bool Durable => false;

    public Task<JsonObject> Activity(string name, JsonObject input) =>
        name == Interp.ClaimCheck ? Task.FromResult(h.ClaimCheckOp(input)) : h.Attempt(input, durable: false);

    /// <summary>
    /// A timer raced against an activity (an attempt timeout). The handler stays busy while its
    /// activity runs, so this timer does not change the busy count; it fires only if the clock
    /// reaches it, that is when the handler is waiting on nothing else.
    /// </summary>
    public Task Timer(long at, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Clock.Schedule(at, () => tcs.TrySetResult());
        return tcs.Task;
    }

    public Task<JsonObject> Child(string instanceId, JsonObject input) => throw new NotSupportedException("a handler starts no sub-orchestration");

    /// <summary>The same retry rule as RetryInterceptor, without durable timers.</summary>
    public async Task<T> WithRetry<T>(RetryOptions o, Func<Task<T>> call)
    {
        Exception? last = null;
        var first = Now;
        for (var n = 0; n < o.MaxNumberOfAttempts; n++)
        {
            try { return await call(); }
            catch (Exception e) { last = e; }
            if (n + 1 == o.MaxNumberOfAttempts) break;
            var deadline = o.RetryTimeout == TimeSpan.MaxValue ? long.MaxValue : first + (long)o.RetryTimeout.TotalMilliseconds;
            if (!o.Handle(last) || Now >= deadline) break;
            // A backoff is a plain wait: the handler is not busy while it sleeps.
            await h.Clock.Sleep(Now + (long)(o.FirstRetryInterval.TotalMilliseconds * Math.Pow(o.BackoffCoefficient, n)));
        }
        throw last!;
    }

    public void SendEvent(string instanceId, string name, JsonObject data) => throw new NotSupportedException();
    public void ContinueAsNew(JsonObject input) => throw new NotSupportedException();
}

/// <summary>The one generic orchestration: it interprets the expanded DSL document named in its input.</summary>
public sealed class InterpreterOrchestration : TaskOrchestration<string, string, string, string>
{
    Interp? interp;

    public override async Task<string> RunTask(OrchestrationContext context, string input)
    {
        interp = new Interp(new DtfxEngine(context), Deployment.Current);
        try
        {
            return J.Str(await interp.Run((JsonObject)J.Parse(input)!));
        }
        catch (Exception e)
        {
            var o = interp.Output();
            o["failed"] = e.Message;
            o["outcome"] ??= "failed";
            return J.Str(o);
        }
    }

    public override void OnEvent(OrchestrationContext context, string name, string input)
    {
        if (interp is null) return;
        var data = (JsonObject)J.Parse(input)!;
        if (name == "signal") interp.OnSignal(J.S(data, "signal")!);
        else if (name == "message") interp.OnMessage(data);
        else if (name == "chunk") interp.OnChunk(data);
    }

    public override string OnGetStatus() => J.Str(new JsonObject { ["awaiting"] = interp?.AwaitingType, ["ended"] = interp?.Ended ?? false });
}

public sealed class PartyActivity : AsyncTaskActivity<string, string>
{
    public static Harness H = null!;
    protected override async Task<string> ExecuteAsync(TaskContext context, string input) => J.Str(await H.Attempt((JsonObject)J.Parse(input)!, durable: true));
}

public sealed class ClaimCheckActivity : AsyncTaskActivity<string, string>
{
    public static Harness H = null!;
    protected override Task<string> ExecuteAsync(TaskContext context, string input) => Task.FromResult(J.Str(H.ClaimCheckOp((JsonObject)J.Parse(input)!)));
}

public sealed class Creator<T> : ObjectCreator<T>
{
    readonly Func<T> make;
    public Creator(string name, Func<T> make) { Name = name; Version = ""; this.make = make; }
    public override T Create() => make();
}
