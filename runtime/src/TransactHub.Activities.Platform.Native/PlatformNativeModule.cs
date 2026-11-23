using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

[assembly: NativeModule(typeof(TransactHub.Activities.Platform.Native.PlatformNativeModule))]

namespace TransactHub.Activities.Platform.Native;

/// <summary>
/// The platform's native activities as a distribution carries them (spec R-09-126, Issue I-221): the code-behinds of the platform
/// pack's activities that say the runtime implements them (<c>implementation.native: true</c>). This assembly is built from this
/// project when the studio builds a distribution, carried with its SHA-256 in the bill of materials under
/// <c>activities/native/p/</c>, and loaded by the native host in the distribution's own context, beside the schemes' modules. A fix
/// here reaches a payment only through a platform release and a new distribution (scenario 5); a payment pinned to an older
/// distribution keeps that one's build. The runtime image keeps the ports, their adapters and the store, behind the native contract.
/// </summary>
public sealed class PlatformNativeModule : INativeModule
{
    public string Scope => "p";

    public IReadOnlyList<INativeActivity> Activities(INativeServices services) =>
    [
        new EnrichFxRate(), new EnrichCharges(), new DecidePostingWindow(), new PostAccountingV2(services.CoreBanking),
        new EnrichPartyRegister(services.Ports, services.Contribution), new EncryptFile(services.Files, services.Ports), new DecryptFile(services.Files, services.Ports),
    ];
}

/// <summary>
/// <c>p.decide.posting-window.v1</c>: whether core banking takes postings now, by the bank's window (<c>windowStart</c>, <c>windowEnd</c>,
/// HH:mm business-local), with the source's call-hours gate (<c>CbCallWindowGate</c>, here <c>PostingWindow</c>). An invalid window fails
/// closed. The composite <c>p.exchange.await-posting-window.v1</c> waits and asks again while it is closed (the schedule-gated await).
/// </summary>
public sealed class DecidePostingWindow : INativeActivity
{
    public string Name => "p.decide.posting-window.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var w = PostingWindow.Evaluate(NativeContext.Param(a.Params, "windowStart"), NativeContext.Param(a.Params, "windowEnd"), NativeContext.Business(a.Now));
        var state = w.State == PostingWindowState.Open ? "open" : "closed";
        var fact = new JsonObject { ["outcome"] = state, ["result"] = state, ["windowId"] = w.WindowId, ["at"] = w.NowLocal.ToString("HH:mm", CultureInfo.InvariantCulture) };
        if (w.State == PostingWindowState.ConfigInvalid) fact["invalid"] = true;
        return new NativeResult(state, null, new JsonObject { ["facts.postingWindow"] = fact });
    }
}
