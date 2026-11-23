using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Store;

/// <summary>A distribution's native assemblies are refused (spec R-09-121, R-09-123): a hash, a file or the contract does not hold.</summary>
public sealed class NativeAssembliesRefused(string message) : Exception(message);

/// <summary>One native module of a distribution, checked: its scope, its assembly's name, its files' bytes and the activities it must serve.</summary>
public sealed record NativeModulePackage(string Scope, string Assembly, IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<string> Activities, IReadOnlyDictionary<string, string> Sha256);

/// <summary>
/// The native assemblies a distribution carries (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3, spec R-09-120 to R-09-123),
/// as the manifest's <c>native</c> section lists them: per module its scope, its assembly, the activities (<c>name@release</c>) it
/// serves and each file, kept in the package as base64 text under <c>activities/native/&lt;scope&gt;/</c>, with the SHA-256 of its
/// bytes. The same check runs when the release API installs a distribution and when the native host loads one: every file is
/// there, in the signed bill of materials, with its SHA-256; no other file is under <c>activities/native/</c>; no file is an
/// assembly the runtime image keeps (the contract or the platform); the contract version is one this runtime supports; and the
/// module's assembly was compiled against that contract.
/// </summary>
public static class NativeAssemblies
{
    public const string Folder = "activities/native/";
    public const string Suffix = ".base64";

    /// <summary>The image's assemblies a module references and never carries: the contract and the platform's runtime software.</summary>
    public static readonly IReadOnlySet<string> Shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        NativeContract.AssemblyName, "TransactHub.Activities.Platform", "TransactHub.Ports", "TransactHub.Model", "TransactHub.Store", "TransactHub.Security",
    };

    static string Sha(byte[] b) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Checks the manifest's native section against the files and returns each module, decoded. A manifest without one carries none.</summary>
    public static IReadOnlyList<NativeModulePackage> Check(JsonObject manifest, Func<string, byte[]?> file, IEnumerable<string> paths)
    {
        var native = manifest["native"] as JsonObject;
        var under = paths.Where(p => p.StartsWith(Folder, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (native is null)
        {
            if (under.Count > 0) throw new NativeAssembliesRefused($"{under.First()} is a native assembly the manifest does not list");
            return [];
        }
        var contract = native["contract"]?.ToString();
        var modules = native["modules"] as JsonArray ?? [];
        if (modules.Count > 0 && !NativeContract.Supports(contract)) throw new NativeAssembliesRefused($"It is refused: {NativeContract.Refusal(contract)}");
        var bom = (manifest["billOfMaterials"]?["artefacts"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(a => (string?)a["kind"] == "native-assembly").ToDictionary(a => (string?)a["path"] ?? "", StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var outp = new List<NativeModulePackage>();
        foreach (var m in modules.OfType<JsonObject>())
        {
            var scope = (string?)m["scope"] ?? throw new NativeAssembliesRefused("A native module names no scope");
            var assembly = (string?)m["assembly"] ?? throw new NativeAssembliesRefused($"The native module of {scope} names no assembly");
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var shas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in (m["files"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var path = (string?)f["path"] ?? "";
                var name = (string?)f["name"] ?? "";
                if (!path.StartsWith($"{Folder}{scope}/", StringComparison.Ordinal) || !path.EndsWith(Suffix, StringComparison.Ordinal) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.Contains('/'))
                    throw new NativeAssembliesRefused($"{path} is not where a native assembly of {scope} is kept ({Folder}{scope}/<name>.dll{Suffix})");
                var asmName = name[..^4];
                if (Shared.Contains(asmName)) throw new NativeAssembliesRefused($"{name} is the runtime image's own: a distribution never carries it");
                if (!bom.TryGetValue(path, out var entry)) throw new NativeAssembliesRefused($"{path} is not in the signed bill of materials");
                var text = file(path) ?? throw new NativeAssembliesRefused($"{path} is in the manifest but not in the distribution");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(Encoding.ASCII.GetString(text).Trim()); }
                catch (FormatException) { throw new NativeAssembliesRefused($"{path} is not base64"); }
                var sha = Sha(bytes);
                if (sha != (string?)f["sha256"] || sha != (string?)entry["assemblySha256"])
                    throw new NativeAssembliesRefused($"{name} of {scope} does not match its SHA-256 in the bill of materials");
                files[asmName] = bytes;
                shas[asmName] = sha;
                listed.Add(path);
            }
            if (!files.TryGetValue(assembly, out var main)) throw new NativeAssembliesRefused($"The native module of {scope} does not carry its assembly {assembly}.dll");
            var built = ContractOf(main) ?? throw new NativeAssembliesRefused($"{assembly}.dll does not reference the native contract ({NativeContract.AssemblyName})");
            if (!NativeContract.Supports(built) || !SameMinor(built, contract))
                throw new NativeAssembliesRefused($"{assembly}.dll was built against native contract {built}, its manifest says {contract}: {NativeContract.Refusal(built)}");
            var activities = (m["activities"] as JsonArray ?? []).Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
            outp.Add(new NativeModulePackage(scope, assembly, files, activities, shas));
        }
        foreach (var p in under)
            if (!listed.Contains(p)) throw new NativeAssembliesRefused($"{p} is a native assembly the manifest does not list");
        return outp;
    }

    static bool SameMinor(string built, string? declared) =>
        Version.TryParse(built, out var a) && Version.TryParse(declared, out var b) && a.Major == b.Major && a.Minor == b.Minor;

    /// <summary>The version of the native contract an assembly references (major.minor.build), read from its metadata without loading it.</summary>
    public static string? ContractOf(byte[] assembly)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(assembly));
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            foreach (var h in md.AssemblyReferences)
            {
                var r = md.GetAssemblyReference(h);
                if (md.GetString(r.Name) == NativeContract.AssemblyName) return $"{r.Version.Major}.{r.Version.Minor}.{Math.Max(r.Version.Build, 0)}";
            }
            return null;
        }
        catch (BadImageFormatException) { return null; }
    }

    /// <summary>The files of a stored package (a canonical zip), by path.</summary>
    public static Dictionary<string, byte[]> Unzip(byte[] zip)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var a = new System.IO.Compression.ZipArchive(new MemoryStream(zip), System.IO.Compression.ZipArchiveMode.Read);
        foreach (var e in a.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            using var s = e.Open();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            files[e.FullName] = ms.ToArray();
        }
        return files;
    }
}
