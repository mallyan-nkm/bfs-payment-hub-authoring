using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace TransactHub.Api;

/// <summary>
/// A studio export package (spec Part 9): its files, its manifest, and the checks the runtime makes
/// before it stores one. The stored form is a canonical zip (entries sorted, fixed times), so the same
/// files always hash the same, however the package reached the runtime.
/// </summary>
public sealed class Package
{
    public required IReadOnlyDictionary<string, byte[]> Files { get; init; }
    public required JsonObject Manifest { get; init; }

    /// <summary>The workflow the package was exported for, at its release, and the engine it targets.</summary>
    public string Workflow => (string?)Root["name"] ?? throw new PackageRejected("The manifest's root flow has no name");
    public string Release => (string?)Root["release"] ?? throw new PackageRejected("The manifest's root flow has no release");

    /// <summary>
    /// The flow the package was exported for. The manifest lists every flow it carries in name order (the
    /// legs, segments, subflows, handlers and bank hooks it needs too). The root is the one workflow that is
    /// none of those; a package exported for a leg alone (a leg is a release of its own) has that leg as root.
    /// No root, or two at the same tier, is refused.
    /// </summary>
    JsonObject Root
    {
        get
        {
            var flows = (Manifest["flows"] as JsonArray ?? throw new PackageRejected("The manifest names no flow")).OfType<JsonObject>().Where(f => (string?)f["kind"] == "workflow").ToList();
            string[] parts = ["leg", "segment", "subflow", "handler"];
            foreach (var tier in new Func<JsonObject, bool>[] { f => !parts.Contains((string?)f["wfKind"]), f => (string?)f["wfKind"] == "leg" })
            {
                var roots = flows.Where(tier).ToList();
                if (roots.Count == 1) return roots[0];
                if (roots.Count > 1) throw new PackageRejected($"The manifest has {roots.Count} root workflows; a package carries one: {string.Join(", ", roots.Select(r => (string?)r["name"]))}");
            }
            throw new PackageRejected("The manifest has no root workflow (only segments, subflows, handlers or hooks)");
        }
    }
    public string Target => (string?)Manifest["target"] ?? throw new PackageRejected("The manifest names no target");
    public bool Promotable => Manifest["promotable"]?.GetValue<bool>() == true && Manifest["containsSimulators"]?.GetValue<bool>() != true;
    /// <summary>The registry's scope for this package: one line per workflow and engine.</summary>
    public string Scope => $"{Workflow}/{Target}";

    public static Package FromZip(byte[] zip)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var a = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            foreach (var e in a.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                if (e.FullName.Contains("..") || e.FullName.StartsWith('/')) throw new PackageRejected($"Unsafe path in the package: {e.FullName}");
                using var s = e.Open();
                using var m = new MemoryStream();
                s.CopyTo(m);
                files[e.FullName] = m.ToArray();
            }
        }
        catch (InvalidDataException ex) { throw new PackageRejected($"Not a zip: {ex.Message}"); }
        return FromFiles(files);
    }

    public static Package FromDirectory(string dir)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(dir, f).Replace('\\', '/')] = File.ReadAllBytes(f);
        // The branch keeps the package hash beside the package; it is not part of the package.
        files.Remove("package.sha256");
        return FromFiles(files);
    }

    static Package FromFiles(Dictionary<string, byte[]> files)
    {
        if (!files.TryGetValue("manifest.yaml", out var m)) throw new PackageRejected("The package has no manifest.yaml");
        var yaml = new DeserializerBuilder().Build().Deserialize<object>(Encoding.UTF8.GetString(m));
        var manifest = ToJson(yaml) as JsonObject ?? throw new PackageRejected("manifest.yaml is not a mapping");
        var p = new Package { Files = files, Manifest = manifest };
        p.Verify();
        return p;
    }

    /// <summary>Every file the manifest lists is there with the hash it lists, and nothing else is but the manifest and README.</summary>
    void Verify()
    {
        _ = Workflow; _ = Release; _ = Target;
        var listed = Manifest["files"] as JsonArray ?? throw new PackageRejected("The manifest lists no files");
        var seen = new HashSet<string>(StringComparer.Ordinal) { "manifest.yaml", "README.md" };
        foreach (var f in listed)
        {
            var path = (string?)f?["path"] ?? throw new PackageRejected("A manifest file entry has no path");
            var hash = (string?)f?["hash"] ?? "";
            if (!Files.TryGetValue(path, out var content)) throw new PackageRejected($"{path} is in the manifest but not in the package");
            var actual = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
            if (actual != hash) throw new PackageRejected($"{path} does not match its hash in the manifest");
            seen.Add(path);
        }
        var extra = Files.Keys.Where(k => !seen.Contains(k)).ToList();
        if (extra.Count > 0) throw new PackageRejected($"Files not in the manifest: {string.Join(", ", extra.Take(5))}");
    }

    /// <summary>The canonical zip: entries in ordinal order, all at one fixed time, stored the same way.</summary>
    public byte[] CanonicalZip()
    {
        var ms = new MemoryStream();
        using (var a = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in Files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var e = a.CreateEntry(path, CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(content);
            }
        }
        return ms.ToArray();
    }

    static JsonNode? ToJson(object? y) => y switch
    {
        null => null,
        IDictionary<object, object> d => new JsonObject(d.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key.ToString()!, ToJson(kv.Value)))),
        IList<object> l => new JsonArray(l.Select(ToJson).ToArray()),
        string s when s is "true" or "false" => JsonValue.Create(s == "true"),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(y.ToString()),
    };
}

public sealed class PackageRejected(string message) : Exception(message);
