using System.Text.Json.Nodes;

namespace TransactHub.Engines;

// The engine host's path from the runtime to a running instance (R-05-57, Issue I-99). The runtime puts every operator's
// signal, hold decision and control on its outbox (topic engine-signal) for the payment; the host of the engine that runs the
// payment reads them in outbox order, delivers each on the engine's own path (a raised event, a workflow signal, a message
// event) and records the delivery as a fact on the payment's chain. A row delivered before is not delivered again, and a host
// that restarts starts after the last row it recorded, so the reader is idempotent, ordered and resumable. In a kit run or a
// studio deployment without a store there is no source, and nothing here runs.

/// <summary>One payment's rows, followed by the host that runs it.</summary>
public interface ILiveFeed
{
    /// <summary>The rows not yet delivered, in outbox order. Each has <c>id</c> (the row), <c>signal</c>, and what it carries.</summary>
    Task<List<JsonObject>> Poll(CancellationToken ct = default);
    /// <summary>Records that a row was delivered (or why not) on the payment's chain.</summary>
    Task Ack(JsonObject row, string result, CancellationToken ct = default);
}

/// <summary>Where a host finds the feed of the payment it runs: the runtime's store (TransactHub.Engines.Durable).</summary>
public interface ILiveSignals
{
    /// <summary>The feed of the payment accepted with this payload reference, recording the instance's engine ids; null when there is none.</summary>
    Task<ILiveFeed?> Follow(string payloadRef, string engine, string instance, string? run, CancellationToken ct = default);
}

public static class LiveSignals
{
    /// <summary>Set by a runtime build of a harness when the deployment names a store.</summary>
    public static ILiveSignals? Source;

    /// <summary>How long, in wall time, a harness on virtual time waits for a live operator before it lets an operator wait's own timer fire.</summary>
    public static TimeSpan Patience = TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("TH_OPERATOR_PATIENCE_SECONDS"), out var s) ? s : 600);

    static DateTime? waitingSince;

    /// <summary>
    /// Whether a harness on virtual time should hold its clock: a live feed is followed and an operator wait is open (a hold awaiting
    /// its decision, a parked step) for less than <see cref="Patience"/> of wall time. The clock then fires only what is due now.
    /// </summary>
    public static bool HoldClock(bool live)
    {
        if (!live || Interp.OperatorWaits.IsEmpty) { waitingSince = null; return false; }
        waitingSince ??= DateTime.UtcNow;
        return DateTime.UtcNow - waitingSince.Value < Patience;
    }

    /// <summary>
    /// What a row is on the engine: a fixed signal or an operator's control is a <c>signal</c> event; a hold's decision is the
    /// <c>p.ops.decision</c> message the hold awaits, correlated to the payment by its conversation.
    /// </summary>
    public static (string name, JsonObject data) EventOf(JsonObject row, JsonNode? header)
    {
        var signal = J.S(row, "signal") ?? "";
        var id = J.S(row, "id") ?? "";
        if (signal == "decide")
            return ("message", new JsonObject
            {
                ["id"] = $"decision-{id}", ["type"] = Interp.DecisionMessage, ["status"] = J.S(row, "decision"), ["hold"] = J.S(row, "hold"), ["by"] = J.S(row, "by"),
                ["conversation"] = J.S(header, "conversation"), ["correlation"] = new JsonObject { ["paymentId"] = J.S(header, "paymentId") },
            });
        var data = new JsonObject { ["signal"] = signal, ["id"] = $"row-{id}" };
        if (J.S(row, "step") is string step) data["step"] = step;
        return ("signal", data);
    }

    /// <summary>
    /// Follows a feed until <paramref name="stop"/>: every <paramref name="every"/> it polls, hands each row to <paramref name="deliver"/>
    /// (which answers the result to record), and acknowledges it. A failure to reach the store is retried at the next poll.
    /// </summary>
    public static async Task Run(ILiveFeed feed, Func<JsonObject, Task<string>> deliver, CancellationToken stop, TimeSpan? every = null)
    {
        var wait = every ?? TimeSpan.FromMilliseconds(250);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                foreach (var row in await feed.Poll(stop))
                {
                    var result = await deliver(row);
                    await feed.Ack(row, result, stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) { Console.Error.WriteLine($"[signals] {e.GetType().Name}: the feed is read again at the next poll"); }
            try { await Task.Delay(wait, stop); } catch (OperationCanceledException) { return; }
        }
    }
}
