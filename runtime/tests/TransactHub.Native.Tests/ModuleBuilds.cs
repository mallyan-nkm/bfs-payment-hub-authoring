using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
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

    /// <summary>
    /// The WPS module built with <paramref name="marker"/> in its ACK mapping (<c>status.additionalInformation</c>), at the assembly
    /// version <paramref name="version"/> when one is given (a scheme release's build of its module).
    /// </summary>
    public static byte[] Wps(string marker, string? version = null) => Build(Assembly, marker, "Native/InwardActivities.cs",
        "new JsonObject { [\"code\"] = \"ACCP\" }", $"new JsonObject {{ [\"code\"] = \"ACCP\", [\"additionalInformation\"] = \"{marker}\" }}", version);

    /// <summary>The platform module built with <paramref name="marker"/> in its posting window decision (<c>facts.postingWindow.build</c>).</summary>
    public static byte[] Platform(string marker) => Build(PlatformAssembly, marker, "PlatformNativeModule.cs",
        "if (w.State == PostingWindowState.ConfigInvalid) fact[\"invalid\"] = true;", $"fact[\"build\"] = \"{marker}\"; if (w.State == PostingWindowState.ConfigInvalid) fact[\"invalid\"] = true;");

    static byte[] Build(string assembly, string marker, string file, string from, string to, string? version = null)
    {
        lock (gate)
        {
            if (built.TryGetValue($"{assembly}|{marker}|{version}", out var b)) return b;
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
                var p = Process.Start(new ProcessStartInfo("dotnet", ["build", Path.Combine(work, $"{assembly}.csproj"), "-c", "Release", "--nologo", "-v", "q", $"-p:OutDir={outDir}{Path.DirectorySeparatorChar}", "-p:Deterministic=true", "-p:ContinuousIntegrationBuild=true", "-nodeReuse:false", "-p:UseSharedCompilation=false", .. (version is null ? Array.Empty<string>() : [$"-p:Version={version}"])])
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, Environment = { ["MSBUILDDISABLENODEREUSE"] = "1" } })!;
                var err = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd() + err.Result;
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, output);
                return built[$"{assembly}|{marker}|{version}"] = File.ReadAllBytes(Path.Combine(outDir, $"{assembly}.dll"));
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

    /// <summary>
    /// A module assembly as a build against native contract <paramref name="contract"/> (1.1.0 by default) leaves it: an assembly named
    /// <paramref name="name"/> whose reference to <c>TransactHub.Activities.Contract</c> carries that version, and that names the contract's
    /// <c>INativeModule</c>. Written with System.Reflection.Metadata (no compiler, no 1.x contract kept), saved as bytes and never loaded:
    /// what every module built before contract 2.0.0 is (R-09-262).
    /// </summary>
    public static byte[] BuiltAgainst(string contract = "1.1.0", string name = Assembly)
    {
        var mb = new MetadataBuilder();
        mb.AddModule(0, mb.GetOrAddString($"{name}.dll"), mb.GetOrAddGuid(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(name + contract))[..16])), default, default);
        mb.AddAssembly(mb.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.Sha1);
        var runtime = mb.AddAssemblyReference(mb.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0), default, mb.GetOrAddBlob(new byte[] { 0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a }), 0, default);
        var v = Version.Parse(contract);
        var c = mb.AddAssemblyReference(mb.GetOrAddString("TransactHub.Activities.Contract"), new Version(v.Major, v.Minor, Math.Max(v.Build, 0), 0), default, default, 0, default);
        mb.AddTypeReference(runtime, mb.GetOrAddString("System"), mb.GetOrAddString("Object"));
        mb.AddTypeReference(c, mb.GetOrAddString("TransactHub.Activities.Native"), mb.GetOrAddString("INativeModule"));
        mb.AddTypeDefinition(default, default, mb.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var pe = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(mb), new BlobBuilder());
        var blob = new BlobBuilder();
        pe.Serialize(blob);
        return blob.ToArray();
    }

    public static string Sha(byte[] b) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>One module of a distribution: its scope, assembly, build, the bytes carried (another build to tamper) and its activities.</summary>
    public sealed record Module(string Scope, string Assembly, byte[] Build, string[] Activities, byte[]? Carried = null);

    /// <summary>A distribution with the WPS module only, as the studio builds it, reduced to what the native host reads.</summary>
    public static (JsonObject Manifest, byte[] Package) Distribution(string bank, string id, byte[] module, string contract = TransactHub.Activities.Native.NativeContract.Version, byte[]? carried = null, string ackRelease = AckInRelease) =>
        Distribution(bank, id, contract, new Module("uae.wps", Assembly, module, [$"{AckIn}@{ackRelease}"], carried));

    /// <summary>A distribution as the studio builds it, reduced to what the native host reads: its manifest and its package.</summary>
    public static (JsonObject Manifest, byte[] Package) Distribution(string bank, string id, string contract, params Module[] modules) =>
        Distribution(bank, id, contract, new Dictionary<string, string>(), modules);

    /// <summary>The same, with activity documents (<c>name@release</c> to its YAML) in the bill of materials beside the modules' (R-09-291).</summary>
    public static (JsonObject Manifest, byte[] Package) Distribution(string bank, string id, string contract, IReadOnlyDictionary<string, string> docs, params Module[] modules)
    {
        var files = new Dictionary<string, byte[]> { ["manifest.yaml"] = Encoding.UTF8.GetBytes($"kind: distribution\nid: {id}\nbank: {bank}\n") };
        var artefacts = new JsonArray();
        foreach (var (a, yaml) in docs)
        {
            var path = $"activities/{a}.yaml";
            files[path] = Encoding.UTF8.GetBytes(yaml);
            artefacts.Add(new JsonObject { ["scope"] = a.Split('.')[0], ["kind"] = "activity", ["name"] = a.Split('@')[0], ["release"] = a.Split('@')[1], ["path"] = path, ["sha256"] = Sha(files[path]) });
        }
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
