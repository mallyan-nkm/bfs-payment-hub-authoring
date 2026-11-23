using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Security;
using YamlDotNet.Serialization;

namespace TransactHub.Api;

/// <summary>
/// A bank's signed distribution or configuration release (docs/runtime/PACKAGING-AND-DEPLOYMENT.md sections 3 and 4, spec
/// R-09-48 to R-09-51): a folder with <c>manifest.yaml</c>, the files it lists and the statement and signature of R-09-23 with
/// kind <c>distribution</c> or <c>configuration</c>. The statement names it <c>dist/&lt;bank&gt;@&lt;id&gt;</c> or
/// <c>config/&lt;bank&gt;@&lt;release&gt;</c>, for tenant <c>&lt;bank&gt;</c>, and carries its bill of materials (and a distribution's
/// parameter contract) as signed fields, which must be the manifest's. Every artefact of the bill of materials that the
/// bundle carries is checked against its SHA-256. The stored form is a canonical zip, hashed without the signature.
/// </summary>
public sealed class Bundle
{
    public required IReadOnlyDictionary<string, byte[]> Files { get; init; }
    public required JsonObject Manifest { get; init; }
    public required string Kind { get; init; }

    public string Bank => (string?)Manifest["bank"] ?? throw new PackageRejected("The manifest names no bank");
    /// <summary>The distribution's id (YYYY.MM.DD-n) or the configuration release's release.</summary>
    public string Release => (string?)Manifest[Kind == ReleaseSignature.Distribution ? "id" : "release"] ?? throw new PackageRejected($"The manifest names no {(Kind == ReleaseSignature.Distribution ? "id" : "release")}");
    public string ReleaseId => Kind == ReleaseSignature.Distribution ? $"dist/{Bank}@{Release}" : $"config/{Bank}@{Release}";
    public string? Engine => (string?)Manifest["engine"];
    public string ContentHash => Convert.ToHexStringLower(SHA256.HashData(CanonicalZip(withSignature: false)));

    public static Bundle FromFiles(Dictionary<string, byte[]> files, string kind)
    {
        files.Remove("package.sha256");
        if (!files.TryGetValue("manifest.yaml", out var m)) throw new PackageRejected("The bundle has no manifest.yaml");
        var yaml = new DeserializerBuilder().Build().Deserialize<object>(Encoding.UTF8.GetString(m));
        var manifest = BundleYaml.ToJsonNode(yaml) as JsonObject ?? throw new PackageRejected("manifest.yaml is not a mapping");
        var want = kind == ReleaseSignature.Distribution ? "distribution" : "configuration-release";
        if ((string?)manifest["kind"] != want) throw new PackageRejected($"manifest.yaml is a {(string?)manifest["kind"] ?? "document of no kind"}, not a {want}");
        var b = new Bundle { Files = files, Manifest = manifest, Kind = kind };
        _ = b.Bank; _ = b.Release;
        b.CheckBillOfMaterials();
        // The native assemblies (R-09-121): each the one its SHA-256 in the signed bill of materials names, built against a native
        // contract this runtime supports; a mismatch refuses the whole distribution at install.
        if (kind == ReleaseSignature.Distribution)
        {
            try { TransactHub.Store.NativeAssemblies.Check(manifest, p => files.TryGetValue(p, out var c) ? c : null, files.Keys); }
            catch (TransactHub.Store.NativeAssembliesRefused e) { throw new PackageRejected($"Its native assemblies are refused: {e.Message}"); }
        }
        return b;
    }

    public static Bundle FromZip(byte[] zip, string kind)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            // Read within the limits whatever the headers claim: a zip bomb is refused (R-09-59).
            foreach (var (name, bytes) in SafeZip.Read(zip))
            {
                if (name.Contains("..") || name.StartsWith('/') || name.Contains('\\')) throw new PackageRejected($"Unsafe path in the bundle: {name}");
                files[name] = bytes;
            }
        }
        catch (InvalidDataException ex) { throw new PackageRejected($"Not a zip: {ex.Message}"); }
        return FromFiles(files, kind);
    }

    /// <summary>Every artefact of the bill of materials that the bundle carries is there with its SHA-256.</summary>
    void CheckBillOfMaterials()
    {
        foreach (var a in Manifest["billOfMaterials"]?["artefacts"] as JsonArray ?? [])
        {
            var path = (string?)a?["path"];
            if (path is null) continue;
            if (!Files.TryGetValue(path, out var content)) throw new PackageRejected($"{path} is in the bill of materials but not in the bundle");
            if ("sha256:" + Convert.ToHexStringLower(SHA256.HashData(content)) != (string?)a?["sha256"]) throw new PackageRejected($"{path} does not match its SHA-256 in the bill of materials");
        }
    }

    /// <summary>Verifies the signature (R-09-24) and that the signed bill of materials and parameter contract are the manifest's.</summary>
    public Verification Verify(TrustedSigners trust)
    {
        var v = ReleaseSignature.Verify(Files, new ReleaseExpectation(ReleaseId, Bank, Kind), trust);
        var st = JsonNode.Parse(Files[ReleaseSignature.StatementFile]) as JsonObject;
        foreach (var field in Kind == ReleaseSignature.Distribution ? new[] { "billOfMaterials", "parameterContract" } : ["billOfMaterials"])
            if (TransactHub.Store.CanonicalJson.Of(st?[field]) != TransactHub.Store.CanonicalJson.Of(Manifest[field]))
                throw new SignatureRejected($"The signed {field} is not the manifest's");
        return v;
    }

    public byte[] CanonicalZip(bool withSignature = true)
    {
        var ms = new MemoryStream();
        using (var a = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in Files.Where(f => withSignature || !ReleaseSignature.IsSignatureFile(f.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var e = a.CreateEntry(path, CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(content);
            }
        }
        return ms.ToArray();
    }

    /// <summary>A file of the bundle read as YAML, as JSON.</summary>
    public JsonNode? Yaml(string path) => Files.TryGetValue(path, out var b) ? BundleYaml.ToJsonNode(new DeserializerBuilder().Build().Deserialize<object>(Encoding.UTF8.GetString(b))) : null;
}

public static class BundleYaml
{
    /// <summary>YAML as YamlDotNet reads it, as JSON: scalars that read as numbers or booleans become those.</summary>
    public static JsonNode? ToJsonNode(object? y) => y switch
    {
        null => null,
        IDictionary<object, object> d => new JsonObject(d.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key.ToString()!, ToJsonNode(kv.Value)))),
        IList<object> l => new JsonArray(l.Select(ToJsonNode).ToArray()),
        string s when s is "true" or "false" => JsonValue.Create(s == "true"),
        string s when decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && !s.StartsWith('+') && !(s.Length > 1 && s[0] == '0' && s[1] != '.') => JsonValue.Create(n),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(y.ToString()),
    };
}
