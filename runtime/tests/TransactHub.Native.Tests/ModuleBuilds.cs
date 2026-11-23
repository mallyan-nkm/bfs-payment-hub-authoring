using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TransactHub.Native.Tests;

/// <summary>
/// Two builds of a native code-behind, as two releases would make them: a copy of the module's project (WPS's
/// <c>src/TransactHub.Activities.Wps</c>, or the platform's <c>src/TransactHub.Activities.Platform.Native</c>) that writes a marker of its
/// build into what one activity answers, built (Release, deterministic) into a folder of the test's own. The bytes differ, and so do
/// their SHA-256.
/// </summary>
public static class ModuleBuilds
{
    public const string Assembly = "TransactHub.Activities.Wps";
    public const string PlatformAssembly = "TransactHub.Activities.Platform.Native";
    public const string AckIn = "uae.wps.map.ack-in.v1";
    public const string AckInRelease = "1.2.0";
    public const string PostingWindow = "p.decide.posting-window.v1";
    public const string PostingWindowRelease = "1.0.0";
    static readonly object gate = new();
    static readonly Dictionary<string, byte[]> built = new();

    public static string RuntimeDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TransactHub.Runtime.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("runtime/");
    }

    /// <summary>The WPS module built with <paramref name="marker"/> in its ACK mapping (<c>status.additionalInformation</c>).</summary>
    public static byte[] Wps(string marker) => Build(Assembly, marker, "Native/InwardActivities.cs",
        "new JsonObject { [\"code\"] = \"ACCP\" }", $"new JsonObject {{ [\"code\"] = \"ACCP\", [\"additionalInformation\"] = \"{marker}\" }}");

    /// <summary>The platform module built with <paramref name="marker"/> in its posting window decision (<c>facts.postingWindow.build</c>).</summary>
    public static byte[] Platform(string marker) => Build(PlatformAssembly, marker, "PlatformNativeModule.cs",
        "if (w.State == PostingWindowState.ConfigInvalid) fact[\"invalid\"] = true;", $"fact[\"build\"] = \"{marker}\"; if (w.State == PostingWindowState.ConfigInvalid) fact[\"invalid\"] = true;");

    static byte[] Build(string assembly, string marker, string file, string from, string to)
    {
        lock (gate)
        {
            if (built.TryGetValue($"{assembly}|{marker}", out var b)) return b;
            var src = Path.Combine(RuntimeDir(), "src", assembly);
            var work = Path.Combine(Path.GetTempPath(), $"th-native-module-{marker}-{Guid.NewGuid():N}");
            try
            {
                var patched = false;
                foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(src, f);
                    if (rel.StartsWith("bin") || rel.StartsWith("obj")) continue;
                    var to2 = Path.Combine(work, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(to2)!);
                    var text = File.ReadAllText(f);
                    // The copy's project references point at the runtime's own projects.
                    if (f.EndsWith(".csproj")) text = text.Replace(@"Include=""..\", $@"Include=""{Path.Combine(RuntimeDir(), "src")}{Path.DirectorySeparatorChar}").Replace('\\', Path.DirectorySeparatorChar);
                    if (rel.Replace('\\', '/') == file) { Assert.Contains(from, text); text = text.Replace(from, to); patched = true; }
                    File.WriteAllText(to2, text);
                }
                Assert.True(patched, $"{file} of {assembly} was not marked");
                var outDir = Path.Combine(work, "out");
                var p = Process.Start(new ProcessStartInfo("dotnet", ["build", Path.Combine(work, $"{assembly}.csproj"), "-c", "Release", "--nologo", "-v", "q", $"-p:OutDir={outDir}{Path.DirectorySeparatorChar}", "-p:Deterministic=true", "-p:ContinuousIntegrationBuild=true", "-nodeReuse:false", "-p:UseSharedCompilation=false"])
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, Environment = { ["MSBUILDDISABLENODEREUSE"] = "1" } })!;
                var err = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd() + err.Result;
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, output);
                return built[$"{assembly}|{marker}"] = File.ReadAllBytes(Path.Combine(outDir, $"{assembly}.dll"));
            }
            finally { try { Directory.Delete(work, true); } catch (IOException) { } }
        }
    }

    /// <summary>
    /// An assembly that names a native module and references the platform's runtime software (<c>TransactHub.Ports</c>) as well as the
    /// contract: what R-09-125 refuses. Emitted here, saved as a file, never loaded.
    /// </summary>
    public static byte[] ReachesIntoTheImage(string name = Assembly)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(name) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var mb = ab.DefineDynamicModule(name);
        var tb = mb.DefineType($"{name}.Reaches", TypeAttributes.Public | TypeAttributes.Class);
        tb.DefineField("bindings", typeof(TransactHub.Ports.PortBindings), FieldAttributes.Public);
        tb.DefineField("module", typeof(TransactHub.Activities.Native.INativeModule), FieldAttributes.Public);
        tb.CreateType();
        var ms = new MemoryStream();
        ab.Save(ms);
        return ms.ToArray();
    }

    public static string Sha(byte[] b) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>One module of a distribution: its scope, assembly, build, the bytes carried (another build to tamper) and its activities.</summary>
    public sealed record Module(string Scope, string Assembly, byte[] Build, string[] Activities, byte[]? Carried = null);

    /// <summary>A distribution with the WPS module only, as the studio builds it, reduced to what the native host reads.</summary>
    public static (JsonObject Manifest, byte[] Package) Distribution(string bank, string id, byte[] module, string contract = "1.1.0", byte[]? carried = null, string ackRelease = AckInRelease) =>
        Distribution(bank, id, contract, new Module("uae.wps", Assembly, module, [$"{AckIn}@{ackRelease}"], carried));

    /// <summary>A distribution as the studio builds it, reduced to what the native host reads: its manifest and its package.</summary>
    public static (JsonObject Manifest, byte[] Package) Distribution(string bank, string id, string contract, params Module[] modules)
    {
        var files = new Dictionary<string, byte[]> { ["manifest.yaml"] = Encoding.UTF8.GetBytes($"kind: distribution\nid: {id}\nbank: {bank}\n") };
        var artefacts = new JsonArray();
        var listed = new JsonArray();
        foreach (var m in modules)
        {
            var path = $"activities/native/{m.Scope}/{m.Assembly}.dll.base64";
            files[path] = Encoding.ASCII.GetBytes(Convert.ToBase64String(m.Carried ?? m.Build) + "\n");
            foreach (var a in m.Activities)
                artefacts.Add(new JsonObject { ["scope"] = m.Scope, ["kind"] = "activity", ["name"] = a.Split('@')[0], ["release"] = a.Split('@')[1] });
            artefacts.Add(new JsonObject { ["scope"] = m.Scope, ["kind"] = "native-assembly", ["name"] = $"{m.Assembly}.dll", ["path"] = path, ["sha256"] = Sha(files[path]), ["assemblySha256"] = Sha(m.Build) });
            listed.Add(new JsonObject
            {
                ["scope"] = m.Scope, ["assembly"] = m.Assembly, ["activities"] = new JsonArray(m.Activities.Select(x => (JsonNode)x).ToArray()),
                ["files"] = new JsonArray(new JsonObject { ["path"] = path, ["name"] = $"{m.Assembly}.dll", ["sha256"] = Sha(m.Build) }),
            });
        }
        var manifest = new JsonObject
        {
            ["kind"] = "distribution", ["id"] = id, ["bank"] = bank, ["engine"] = "temporal", ["longestWait"] = "PT0S",
            ["billOfMaterials"] = new JsonObject { ["artefacts"] = artefacts },
            ["native"] = new JsonObject { ["contract"] = contract, ["modules"] = listed },
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
