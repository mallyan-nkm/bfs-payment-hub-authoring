using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace TransactHub.Store;

/// <summary>
/// Whether every activity of a distribution that the runtime's native host answers is served there (spec R-09-291, Issue I-384): by
/// the runtime image's own table, or by a native module the distribution carries. The release API runs it when it installs a
/// distribution, on what the manifest's modules list; the native host runs it again when it loads one, on what the loaded modules
/// serve. A distribution that carries such an activity and serves it nowhere is refused (<see cref="NativeAssemblies.ActivityUnserved"/>),
/// so the gap is found before a payment reaches the step (a distribution built at native contract 2.0 carries a platform module that
/// does not serve the reservation, which the image of contract 2.1 no longer answers).
/// </summary>
public static class NativeServed
{
    /// <summary>The core-banking port: no worker answers a builtin on it, the runtime's native host does, through a module (R-09-278).</summary>
    public const string CoreBankingPort = "p.port.core-banking.v1";

    /// <summary>
    /// What the runtime image's own table answers (R-09-122, R-09-278): the holds and the platform's production adapters, and since
    /// native contract 2.1.0 no core-banking activity. <c>NoCopyInTheImageTests</c> checks it is the image's table.
    /// </summary>
    public static readonly IReadOnlySet<string> ImageActivities = new HashSet<string>(StringComparer.Ordinal)
    {
        "p.hold.approval.v1", "p.hold.repair.v1", "p.screen.sanctions.v1", "p.screen.fraud.v1", "p.notify.channel.v1", "p.notify.publish-fact.v1",
        "uae.p.enrich.business-date.v1", "uae.p.enrich.participant.v1",
    };

    /// <summary>
    /// Whether the native host answers an activity on a runtime, by its document: it is bound <c>native</c>, it says the runtime implements
    /// it (<c>implementation.native: true</c>), or it is a builtin on core banking (R-08-15), which only a module answers since contract 2.1.0.
    /// </summary>
    public static bool AnsweredByTheHost(JsonObject doc)
    {
        var impl = doc["implementation"] as JsonObject;
        var binding = impl?["binding"]?.ToString();
        if (binding == "native" || string.Equals(impl?["native"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase)) return true;
        return binding == "builtin" && (doc["ports"] as JsonArray ?? []).Any(p => p?.ToString() == CoreBankingPort);
    }

    /// <summary>
    /// The activities (<c>name@release</c>) the distribution's bill of materials carries that the native host answers and nothing serves.
    /// An activity of a scope the manifest says it does not carry (<c>native.notCarried</c>) runs its script-behind, not on the host; one
    /// whose entry names no document is not read. <paramref name="serves"/> says whether a module or the image serves a name and release.
    /// </summary>
    public static IReadOnlyList<string> Unserved(JsonObject manifest, Func<string, byte[]?> file, Func<string, string, bool> serves)
    {
        var notCarried = (manifest["native"]?["notCarried"] as JsonArray ?? []).Select(x => x?.ToString() ?? "").ToHashSet(StringComparer.Ordinal);
        var outp = new List<string>();
        foreach (var a in (manifest["billOfMaterials"]?["artefacts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)a["kind"] != "activity" || (string?)a["path"] is not { Length: > 0 } path) continue;
            var name = (string?)a["name"] ?? "";
            var release = (string?)a["release"] ?? "";
            if (notCarried.Contains((string?)a["scope"] ?? ScopeOf(name))) continue;
            if (file(path) is not { } bytes || Doc(bytes) is not { } doc || !AnsweredByTheHost(doc)) continue;
            if (!ImageActivities.Contains(name) && !serves(name, release)) outp.Add($"{name}@{release}");
        }
        return outp;
    }

    /// <summary>At install: served by the image or listed by a module of the manifest, by name and release or by name.</summary>
    public static IReadOnlyList<string> Unserved(JsonObject manifest, Func<string, byte[]?> file, IReadOnlyList<NativeModulePackage> modules) =>
        Unserved(manifest, file, (name, release) => modules.Any(m => m.Activities.Contains($"{name}@{release}") || m.Activities.Contains(name)));

    /// <summary>The refusal's text: what is not served, and what to do (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3.3).</summary>
    public static string Refusal(IReadOnlyList<string> unserved) =>
        $"{NativeAssemblies.ActivityUnserved}: it carries {string.Join(", ", unserved)}, which the runtime's native host answers, and neither the runtime image nor a native module of the distribution serves {(unserved.Count == 1 ? "it" : "them")}: " +
        "build a new distribution from a studio of the factory release the runtime takes, install and activate it (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3.3)";

    static string ScopeOf(string name)
    {
        var parts = name.Split('.');
        return parts.Length < 2 || parts[0] is "p" or "b" ? parts[0] : $"{parts[0]}.{parts[1]}";
    }

    static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <summary>An activity's document as the studio wrote it (YAML), as JSON; null for one that is not a mapping.</summary>
    static JsonObject? Doc(byte[] bytes)
    {
        try { return ToJson(Yaml.Deserialize<object>(Encoding.UTF8.GetString(bytes))) as JsonObject; }
        catch (YamlDotNet.Core.YamlException) { return null; }
    }

    static JsonNode? ToJson(object? y) => y switch
    {
        null => null,
        IDictionary<object, object> d => new JsonObject(d.Select(kv => KeyValuePair.Create(kv.Key.ToString() ?? "", ToJson(kv.Value)))),
        IList<object> l => new JsonArray(l.Select(ToJson).ToArray()),
        _ => JsonValue.Create(y.ToString()),
    };
}
