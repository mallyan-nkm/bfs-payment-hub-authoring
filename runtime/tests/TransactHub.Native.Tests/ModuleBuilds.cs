using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TransactHub.Native.Tests;

/// <summary>
/// Two builds of the WPS native code-behind, as two scheme patch releases would make them: a copy of
/// <c>src/TransactHub.Activities.Wps</c> whose ACK mapping writes a marker of its build into <c>status.additionalInformation</c>,
/// built (Release, deterministic) into a folder of the test's own. The bytes differ, and so do their SHA-256.
/// </summary>
public static class ModuleBuilds
{
    public const string Assembly = "TransactHub.Activities.Wps";
    public const string AckIn = "uae.wps.map.ack-in.v1";
    public const string AckInRelease = "1.2.0";
    static readonly object gate = new();
    static readonly Dictionary<string, byte[]> built = new();

    public static string RuntimeDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TransactHub.Runtime.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("runtime/");
    }

    /// <summary>The WPS module built with <paramref name="marker"/> in its ACK mapping.</summary>
    public static byte[] Wps(string marker)
    {
        lock (gate)
        {
            if (built.TryGetValue(marker, out var b)) return b;
            var src = Path.Combine(RuntimeDir(), "src", "TransactHub.Activities.Wps");
            var work = Path.Combine(Path.GetTempPath(), $"th-native-module-{marker}-{Guid.NewGuid():N}");
            try
            {
                foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(src, f);
                    if (rel.StartsWith("bin") || rel.StartsWith("obj")) continue;
                    var to = Path.Combine(work, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    var text = File.ReadAllText(f);
                    // The copy's project references point at the runtime's own projects.
                    if (f.EndsWith(".csproj")) text = text.Replace(@"Include=""..\", $@"Include=""{Path.Combine(RuntimeDir(), "src")}{Path.DirectorySeparatorChar}").Replace('\\', Path.DirectorySeparatorChar);
                    if (Path.GetFileName(f) == "InwardActivities.cs") text = text.Replace("new JsonObject { [\"code\"] = \"ACCP\" }", $"new JsonObject {{ [\"code\"] = \"ACCP\", [\"additionalInformation\"] = \"{marker}\" }}");
                    File.WriteAllText(to, text);
                }
                var outDir = Path.Combine(work, "out");
                var p = Process.Start(new ProcessStartInfo("dotnet", ["build", Path.Combine(work, $"{Assembly}.csproj"), "-c", "Release", "--nologo", "-v", "q", $"-p:OutDir={outDir}{Path.DirectorySeparatorChar}", "-p:Deterministic=true", "-p:ContinuousIntegrationBuild=true", "-nodeReuse:false", "-p:UseSharedCompilation=false"])
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, Environment = { ["MSBUILDDISABLENODEREUSE"] = "1" } })!;
                var err = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd() + err.Result;
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, output);
                return built[marker] = File.ReadAllBytes(Path.Combine(outDir, $"{Assembly}.dll"));
            }
            finally { try { Directory.Delete(work, true); } catch (IOException) { } }
        }
    }

    public static string Sha(byte[] b) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>A distribution as the studio builds it, reduced to what the native host reads: its manifest and its package.</summary>
    public static (JsonObject Manifest, byte[] Package) Distribution(string bank, string id, byte[] module, string contract = "1.0.0", byte[]? carried = null, string ackRelease = AckInRelease)
    {
        var path = $"activities/native/uae.wps/{Assembly}.dll.base64";
        var text = Convert.ToBase64String(carried ?? module) + "\n";
        var files = new Dictionary<string, byte[]> { [path] = Encoding.ASCII.GetBytes(text), ["manifest.yaml"] = Encoding.UTF8.GetBytes($"kind: distribution\nid: {id}\nbank: {bank}\n") };
        var manifest = new JsonObject
        {
            ["kind"] = "distribution", ["id"] = id, ["bank"] = bank, ["engine"] = "temporal", ["longestWait"] = "PT0S",
            ["billOfMaterials"] = new JsonObject
            {
                ["artefacts"] = new JsonArray(
                    new JsonObject { ["scope"] = "uae.wps", ["kind"] = "activity", ["name"] = AckIn, ["release"] = ackRelease },
                    new JsonObject { ["scope"] = "p", ["kind"] = "activity", ["name"] = "p.decide.posting-window.v1", ["release"] = "1.0.0" },
                    new JsonObject { ["scope"] = "uae.wps", ["kind"] = "native-assembly", ["name"] = $"{Assembly}.dll", ["path"] = path, ["sha256"] = Sha(files[path]), ["assemblySha256"] = Sha(module) }),
            },
            ["native"] = new JsonObject
            {
                ["contract"] = contract, ["platform"] = "runtime-image",
                ["modules"] = new JsonArray(new JsonObject
                {
                    ["scope"] = "uae.wps", ["assembly"] = Assembly, ["activities"] = new JsonArray($"{AckIn}@{ackRelease}"),
                    ["files"] = new JsonArray(new JsonObject { ["path"] = path, ["name"] = $"{Assembly}.dll", ["sha256"] = Sha(module) }),
                }),
            },
        };
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (n, c) in files)
            {
                using var s = z.CreateEntry(n).Open();
                s.Write(c);
            }
        return (manifest, ms.ToArray());
    }
}
