using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Store;

namespace TransactHub.Native;

/// <summary>
/// Which distribution serves an attempt (spec R-09-130 to R-09-132, Issue I-222). The engine names the payment's distribution in
/// the attempt (<c>distribution</c>), and the worker hands it on with its own <c>TH_DISTRIBUTION</c>, if it has one
/// (<c>workerDistribution</c>). The host serves the attempt from the distribution it names. It refuses one that another worker's
/// distribution names (<c>WrongDistribution</c>), and one the payment is not pinned to (<c>NotPinned</c>). An attempt that names
/// none is served as before: from the worker's distribution, then from the development modules.
/// </summary>
public sealed class AttemptRouting(StoreOptions? store)
{
    /// <summary>A payment's pin never changes (R-09-53): read once per payment.</summary>
    readonly ConcurrentDictionary<string, string> pins = new(StringComparer.Ordinal);

    public async Task<string?> DistributionOf(JsonObject body, CancellationToken ct = default)
    {
        var named = (string?)body["distribution"] is { Length: > 0 } d ? d : null;
        var worker = (string?)body["workerDistribution"] is { Length: > 0 } w ? w : null;
        if (named is null) return worker;
        if (worker is not null && worker != named)
            throw new NativeRefusal("WrongDistribution", $"The attempt names {named}, but its worker runs {worker} (TH_DISTRIBUTION): a worker of one distribution does not serve another's payment");
        var paymentId = body["view"] is JsonObject view ? NativeContext.S(view, "header.paymentId") : null;
        if (store is null || paymentId is not { Length: > 0 }) return named;
        var pinned = await PinOf(paymentId, ct);
        if (pinned is not null && pinned != named)
            throw new NativeRefusal("NotPinned", $"{paymentId} is pinned to {pinned}, not {named}: a payment runs only the native activities of the distribution it is pinned to");
        return named;
    }

    async Task<string?> PinOf(string paymentId, CancellationToken ct)
    {
        if (pins.TryGetValue(paymentId, out var p)) return p;
        await using var db = StoreDbContext.Open(store!);
        var pinned = await new DistributionRegistry(db).PinOf(paymentId, ct);
        if (pins.Count > 100_000) pins.Clear();
        if (pinned is not null) pins[paymentId] = pinned;
        return pinned;
    }
}
