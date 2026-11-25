namespace TransactHub.Activities.Native;

/// <summary>
/// The native contract's version and the rule by which the runtime takes a distribution's native assemblies (spec R-09-123):
/// the same major, at a minor no later than the runtime's own. The version is this assembly's (its project's <c>Version</c>),
/// and the studio writes it in a distribution's manifest (<c>native.contract</c>).
/// </summary>
public static class NativeContract
{
    /// <summary>
    /// The contract this runtime supports. Keep it the project's <c>Version</c>. 2.0.0 (R-09-262) removed <c>Service&lt;T&gt;</c> and added
    /// bound ports, so a module built against 1.x is refused (<c>native.ContractUnsupported</c>) and rebuilt. 2.1.0 (R-09-276), a minor, added
    /// the balance enquiry of a core-banking binding and, on the bound ports, a body of bytes and a file put: a 2.0 module runs on it.
    /// 2.2.0 (R-09-293), a minor, added the scheme message a file put names (<see cref="NativeFilePut.Message"/>): a 2.0 or 2.1 module runs on it.
    /// </summary>
    public const string Version = "2.2.0";

    /// <summary>The contract assembly's name, which a module references and never carries.</summary>
    public const string AssemblyName = "TransactHub.Activities.Contract";

    /// <summary>Whether a module built against <paramref name="version"/> runs on this runtime.</summary>
    public static bool Supports(string? version) => Supports(version, Version);

    /// <summary>
    /// Whether a module built against <paramref name="version"/> runs on a runtime of contract <paramref name="own"/>: the same major, at a
    /// minor no later (R-09-123, R-09-276). So a 2.0 module runs on a 2.1 runtime, and a 2.1 module is refused by a 2.0 runtime.
    /// </summary>
    public static bool Supports(string? version, string own)
    {
        if (!System.Version.TryParse(version, out var v) || !System.Version.TryParse(own, out var o)) return false;
        return v.Major == o.Major && v.Minor <= o.Minor;
    }

    /// <summary>Why a version is refused, for a refusal's message.</summary>
    public static string Refusal(string? version) => Refusal(version, Version);

    /// <summary>Why a version is refused by a runtime of contract <paramref name="own"/>.</summary>
    public static string Refusal(string? version, string own)
    {
        var o = System.Version.Parse(own);
        var why = $"the distribution's native assemblies were built against native contract {version ?? "(none)"}; this runtime supports {o.Major}.x up to {own}";
        if (!System.Version.TryParse(version, out var v)) return why;
        if (v.Major < o.Major) return why + $": rebuild every module against {own} and build a new distribution (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3.2)";
        // The images move first (R-09-309, Issue I-398): a studio of a later minor builds modules this runtime does not take.
        if (v.Major == o.Major && v.Minor > o.Minor) return why + $": take the runtime images of contract {v.Major}.{v.Minor} first, then install the distribution, or build the distribution's modules against {own} (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3.4)";
        return why;
    }
}

/// <summary>
/// The entry point of a distribution's native assembly (spec R-09-122): a scheme's, a bank's or the platform's native activities,
/// built from the activity's code-behind project and loaded side by side, one collectible context per distribution. An assembly names
/// its module with <see cref="NativeModuleAttribute"/>. A module references this assembly and no other of TransactHub (R-09-125): the
/// image's code (ports, adapters, store) reaches it only through <see cref="INativeServices"/>.
/// </summary>
public interface INativeModule
{
    /// <summary>The pack scope it implements, e.g. <c>uae.wps</c> or <c>p</c>.</summary>
    string Scope { get; }

    /// <summary>Its activities, made with what the host gives it. Called once per distribution context.</summary>
    IReadOnlyList<INativeActivity> Activities(INativeServices services);
}

/// <summary>What the runtime image gives a module of a distribution.</summary>
public interface INativeServices
{
    /// <summary>The distribution the module was loaded for (<c>dist/&lt;bank&gt;@&lt;id&gt;</c>), or null in a development context.</summary>
    string? Distribution { get; }

    /// <summary>Keeps a file an activity rendered, under the hash its builder gave, with the attempt's tenant, payment and leg.</summary>
    void KeepRendered(string sha256, string fileName, byte[] content);

    /// <summary>
    /// Gives the other modules of the same distribution, and the image's services for it, something of this module's, by a key they name
    /// (<see cref="NativeContributions"/>: for <c>p.enrich.party-register.v1</c>, a <see cref="PartyMatcher"/> under
    /// <c>party-matcher:&lt;scheme&gt;</c>; for a port's binding, a <see cref="NativeBindingGate"/> under <c>binding-gate:&lt;port&gt;</c>). It lives
    /// as long as the distribution's context.
    /// </summary>
    void Contribute(string key, object value);

    /// <summary>Since 1.1.0. What a module of the same distribution contributed under <paramref name="key"/>, or null. Read it when an attempt runs.</summary>
    object? Contribution(string key);

    /// <summary>Since 1.1.0. The bank's core banking, through its binding in force at an attempt's time (<c>p.port.core-banking.v1</c>).</summary>
    INativeCoreBanking CoreBanking { get; }

    /// <summary>Since 1.1.0. The other ports the bank binds, as the image's adapters answer them.</summary>
    INativePorts Ports { get; }

    /// <summary>
    /// Since 2.0.0 (R-09-261, Issue I-315). The bank's binding in force of any port, so a module builds the adapter of its own scheme's
    /// port: its adapter and settings, and a request sent to its endpoint with its credential, circuit breaker and timeout applied by the image.
    /// Since 2.1.0 (R-09-280, R-09-281, Issue I-383) a request may carry a body of bytes with its content type, and a file may be put
    /// through the binding's transfer, so a scheme's port on another wire is the module's too.
    /// </summary>
    INativeBoundPorts BoundPorts { get; }

    /// <summary>Since 1.1.0. The files the host keeps: what an activity renders, encrypts, receives or decrypts.</summary>
    INativeFiles Files { get; }
}

/// <summary>The keys under which a module contributes to its distribution (<see cref="INativeServices.Contribute"/>).</summary>
public static class NativeContributions
{
    /// <summary>A scheme's <see cref="PartyMatcher"/>, for the platform's party register lookup.</summary>
    public static string PartyMatcher(string scheme) => $"party-matcher:{scheme}";

    /// <summary>A <see cref="NativeBindingGate"/> on a port's binding: the image asks it before each call on that port (R-09-264).</summary>
    public static string BindingGate(string port) => $"binding-gate:{port}";
}

/// <summary>Names the module of a native assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class NativeModuleAttribute(Type module) : Attribute
{
    public Type Module { get; } = module;
}
