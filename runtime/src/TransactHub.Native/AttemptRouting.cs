using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Store;

namespace TransactHub.Native;

/// <summary>
/// Which distribution serves an attempt (spec R-09-130 to R-09-132, Issue I-222). The engine names the payment's distribution in
/// the attempt (<c>distribution</c>), and the worker hands it on with its own <c>TH_DISTRIBUTION</c>, if it has one
/// (<c>workerDistribution</c>). The host serves the attempt from the distribution it names. It refuses one that another worker's
/// distribution names (<c>native.WrongDistribution</c>), and one the payment is not pinned to (<c>native.NotPinned</c>). An attempt that names
/// none is served as before: from the worker's distribution, then from the development modules. On a runtime (a host with
/// <c>TH_BANK</c>) every payment is pinned at acceptance (R-09-53), so an attempt that names a distribution for a payment with no
/// pin is refused as <c>native.NotPinned</c> too (R-09-143, Issue I-231), unless the host runs in an explicit development or test mode
/// (<c>TH_NATIVE_MODE</c> <c>dev</c> or <c>test</c>), as the studio's test runs and the native host's own tests do.
/// </summary>
public sealed class AttemptRouting(StoreOptions? store, bool runtime = false, bool allowUnpinned = false)
{
    /// <summary>The host's routing from its environment: a runtime's when <c>TH_BANK</c> is set; unpinned payments only in <c>TH_NATIVE_MODE</c> dev or test.</summary>
    public static AttemptRouting FromEnvironment(StoreOptions? store, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var mode = env("TH_NATIVE_MODE");
        if (mode is { Length: > 0 } && mode is not ("dev" or "test" or "production"))
            throw new InvalidOperationException($"TH_NATIVE_MODE is {mode}: it is dev, test or production (the default)");
        return new AttemptRouting(store, env("TH_BANK") is { Length: > 0 }, mode is "dev" or "test");
    }

    /// <summary>A payment's pin never changes (R-09-53): read once per payment.</summary>
    readonly ConcurrentDictionary<string, string> pins = new(StringComparer.Ordinal);

    public async Task<string?> DistributionOf(JsonObject body, CancellationToken ct = default)
    {
        var named = (string?)body["distribution"] is { Length: > 0 } d ? d : null;
        var worker = (string?)body["workerDistribution"] is { Length: > 0 } w ? w : null;
        if (named is null) return worker;
        if (worker is not null && worker != named)
            throw new NativeRefusal("native.WrongDistribution", $"The attempt names {named}, but its worker runs {worker} (TH_DISTRIBUTION): a worker of one distribution does not serve another's payment");
        var paymentId = body["view"] is JsonObject view ? NativeContext.S(view, "header.paymentId") : null;
        var strict = runtime && !allowUnpinned;
        if (store is null || paymentId is not { Length: > 0 })
            return strict ? throw new NativeRefusal("native.NotPinned", $"The attempt names {named} for a payment the runtime has no pin of{(paymentId is { Length: > 0 } ? "" : " (no header.paymentId)")}: on a runtime every payment is pinned at acceptance") : named;
        var pinned = await PinOf(paymentId, ct);
        if (pinned is null && strict)
            throw new NativeRefusal("native.NotPinned", $"{paymentId} is pinned to no distribution: on a runtime every payment is pinned at acceptance, so an attempt for {named} is refused (TH_NATIVE_MODE dev or test serves it)");
        if (pinned is not null && pinned != named)
            throw new NativeRefusal("native.NotPinned", $"{paymentId} is pinned to {pinned}, not {named}: a payment runs only the native activities of the distribution it is pinned to");
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
