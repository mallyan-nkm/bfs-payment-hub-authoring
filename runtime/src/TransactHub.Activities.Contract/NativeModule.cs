namespace TransactHub.Activities.Native;

/// <summary>
/// The native contract's version and the rule by which the runtime takes a distribution's native assemblies (spec R-09-123):
/// the same major, at a minor no later than the runtime's own. The version is this assembly's (its project's <c>Version</c>),
/// and the studio writes it in a distribution's manifest (<c>native.contract</c>).
/// </summary>
public static class NativeContract
{
    /// <summary>The contract this runtime supports. Keep it the project's <c>Version</c>.</summary>
    public const string Version = "1.0.0";

    /// <summary>The contract assembly's name, which a module references and never carries.</summary>
    public const string AssemblyName = "TransactHub.Activities.Contract";

    /// <summary>Whether a module built against <paramref name="version"/> runs on this runtime.</summary>
    public static bool Supports(string? version)
    {
        if (!System.Version.TryParse(version, out var v)) return false;
        var own = System.Version.Parse(Version);
        return v.Major == own.Major && v.Minor <= own.Minor;
    }

    /// <summary>Why a version is refused, for a refusal's message.</summary>
    public static string Refusal(string? version) =>
        $"the distribution's native assemblies were built against native contract {version ?? "(none)"}; this runtime supports {System.Version.Parse(Version).Major}.x up to {Version}";
}

/// <summary>
/// The entry point of a distribution's native assembly (spec R-09-122): a scheme's or a bank's native activities, built from
/// the activity's code-behind project and loaded side by side, one collectible context per distribution. An assembly names its
/// module with <see cref="NativeModuleAttribute"/>. The platform's own code (ports, store, the platform's activities) stays in
/// the runtime image and reaches a module only through <see cref="INativeServices"/>.
/// </summary>
public interface INativeModule
{
    /// <summary>The pack scope it implements, e.g. <c>uae.wps</c>.</summary>
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

    /// <summary>A service of the runtime image by its type (the bank's port bindings, the core-banking resolver), or null when the host has none.</summary>
    T? Service<T>() where T : class;

    /// <summary>
    /// Gives one of the image's platform activities something of this module's, by a key the platform activity names (for
    /// <c>p.enrich.party-register.v1</c>, <c>party-matcher:&lt;scheme&gt;</c>). It lives as long as the distribution's context.
    /// </summary>
    void Contribute(string key, object value);
}

/// <summary>Names the module of a native assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class NativeModuleAttribute(Type module) : Attribute
{
    public Type Module { get; } = module;
}
