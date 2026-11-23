using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Posting;
using TransactHub.Ports;

[assembly: NativeModule(typeof(TransactHub.Activities.Wps.Native.WpsNativeModule))]

namespace TransactHub.Activities.Wps.Native;

/// <summary>
/// The WPS native activities as a distribution carries them (spec R-09-122): this assembly is built from this project when the
/// studio builds a distribution of a bank that licenses <c>uae.wps</c>, carried with its SHA-256 in the bill of materials, and
/// loaded by the native host in the distribution's own context. A fix here reaches a payment only through a new distribution
/// (scenario 4); a payment pinned to an older one keeps that one's build.
/// </summary>
public sealed class WpsNativeModule : INativeModule
{
    public string Scope => "uae.wps";

    public IReadOnlyList<INativeActivity> Activities(INativeServices services)
    {
        var bindings = services.Service<PortBindings>() ?? new PortBindings();
        var coreBanking = services.Service<CoreBankingResolver>() ?? new CoreBankingResolver(bindings);
        Action<string, string, byte[]> keep = services.KeepRendered;
        // WPS's own match of the corporate the platform's party register found (R-12-27).
        services.Contribute($"party-matcher:{WpsCorporateMatch.Scheme}", (PartyMatcher)WpsCorporateMatch.Match);
        return [new ValidateCbxFile(), new RenderCbSif(keep), .. WpsInwardActivities.All(keep), new PostReturn(coreBanking), EnrichEmployerStatus.On(bindings)];
    }
}
