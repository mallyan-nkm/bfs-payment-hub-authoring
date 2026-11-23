using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Store.Tests;

// A distribution's native assemblies at install (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3, spec R-09-121 and R-09-123):
// the release API takes a distribution only when every native assembly it carries is the one the signed bill of materials names
// by SHA-256, none is the runtime image's own, none is unlisted, it was built against a native contract this runtime supports, and
// it references the contract and no other assembly of TransactHub (R-09-125). The assembly here is the WPS code-behind as this
// repository builds it.

[Collection(DatabaseCollection.Name)]
public sealed class NativeInstallTests(Databases dbs)
{
    const string Bank = "example-bank";
    const string Asm = "TransactHub.Activities.Wps";
    static string Sha(byte[] b) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(b));
    static byte[] Wps => File.ReadAllBytes(typeof(TransactHub.Activities.Wps.Native.WpsNativeModule).Assembly.Location);

    /// <summary>A distribution with a native module, signed as the studio signs it; each argument breaks one thing.</summary>
    static byte[] Distribution(ECDsa key, string id, byte[] assembly, string? claimedSha = null, string contract = "1.1.0", string name = Asm, string? stray = null)
    {
        var path = $"activities/native/uae.wps/{name}.dll.base64";
        var files = new Dictionary<string, string> { ["binding.yaml"] = "kind: engine-binding\ntarget: temporal\n", [path] = Convert.ToBase64String(assembly) + "\n" };
        if (stray is not null) files[stray] = Convert.ToBase64String(assembly) + "\n";
        var real = Sha(assembly);
        var artefacts = new JsonArray(new JsonObject { ["path"] = "binding.yaml", ["sha256"] = Sha(Encoding.UTF8.GetBytes(files["binding.yaml"])) },
            new JsonObject { ["scope"] = "uae.wps", ["kind"] = "native-assembly", ["name"] = $"{name}.dll", ["path"] = path, ["sha256"] = Sha(Encoding.UTF8.GetBytes(files[path])), ["assemblySha256"] = claimedSha ?? real });
        var bom = new JsonObject { ["packs"] = new JsonArray(), ["artefacts"] = artefacts };
        var pc = new JsonObject { ["format"] = "th-parameter-contract/1", ["parameters"] = new JsonArray() };
        var native = new JsonObject
        {
            ["contract"] = contract,
            ["modules"] = new JsonArray(new JsonObject
            {
                ["scope"] = "uae.wps", ["assembly"] = name, ["activities"] = new JsonArray("uae.wps.map.ack-in.v1@1.2.0"),
                ["files"] = new JsonArray(new JsonObject { ["path"] = path, ["name"] = $"{name}.dll", ["sha256"] = claimedSha ?? real }),
            }),
        };
        var manifest = new JsonObject { ["kind"] = "distribution", ["id"] = id, ["bank"] = Bank, ["engine"] = "temporal", ["longestWait"] = "PT0S", ["billOfMaterials"] = bom.DeepClone(), ["parameterContract"] = pc.DeepClone(), ["native"] = native };
        files["manifest.yaml"] = manifest.ToJsonString();
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var (st, sig) = ReleaseSignature.Sign(bytes, new ReleaseClaims($"dist/{Bank}@{id}", "", id, "temporal", Bank, "CS-TEST-NATIVE", "distribution", new JsonObject { ["billOfMaterials"] = bom, ["parameterContract"] = pc }), key, DateTimeOffset.UtcNow);
        bytes[ReleaseSignature.StatementFile] = st;
        bytes[ReleaseSignature.SignatureFile] = sig;
        return TestSigners.Zip(bytes);
    }

    static async Task<(HttpStatusCode, string)> Post(HttpClient c, byte[] zip)
    {
        var r = await c.PostAsync("/distributions", new ByteArrayContent(zip));
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_native_assembly_is_installed_only_when_its_hash_and_contract_hold(StoreProvider p)
    {
        using var s = new TestSigners();
        await using var host = new ApiHost(dbs.Fresh(p), s);
        var c = host.Client();
        // Signed, and the bytes are the ones the bill of materials names: installed.
        var (ok, okBody) = await Post(c, Distribution(s.Key, "2026.11.23-1", Wps));
        Assert.True(ok == HttpStatusCode.Created, okBody);

        // Another build behind the same signed hash (the file swapped, its text hash kept right): refused at install.
        var other = Wps.ToArray();
        other[^1] ^= 0x5a;
        var (swapped, swappedBody) = await Post(c, Distribution(s.Key, "2026.11.23-2", other, claimedSha: Sha(Wps)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, swapped);
        Assert.Contains("does not match its SHA-256 in the bill of materials", swappedBody);

        // Built against a contract this runtime does not support, or claiming one its assembly was not built against.
        var (future, futureBody) = await Post(c, Distribution(s.Key, "2026.11.23-3", Wps, contract: "2.0.0"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, future);
        Assert.Contains("native contract 2.0.0", futureBody);
        var (claims, claimsBody) = await Post(c, Distribution(s.Key, "2026.11.23-4", Wps, contract: "1.0.0"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, claims);
        Assert.Contains("native contract 1.1.0, its manifest says 1.0.0", claimsBody);

        // An assembly that does not reference the contract, one the image keeps, and one the manifest does not list.
        var (bare, bareBody) = await Post(c, Distribution(s.Key, "2026.11.23-5", File.ReadAllBytes(typeof(Xunit.FactAttribute).Assembly.Location)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bare);
        Assert.Contains("does not reference the native contract", bareBody);
        var (shared, sharedBody) = await Post(c, Distribution(s.Key, "2026.11.23-6", Wps, name: "TransactHub.Activities.Contract"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, shared);
        Assert.Contains("the runtime image's own", sharedBody);
        var (stray, strayBody) = await Post(c, Distribution(s.Key, "2026.11.23-7", Wps, stray: "activities/native/uae.wps/Other.dll.base64"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stray);
        Assert.Contains("the manifest does not list", strayBody);

        // A module that references the image's runtime software beyond the contract (R-09-125): refused, with its own code.
        var (reaching, reachingBody) = await Post(c, Distribution(s.Key, "2026.11.23-8", ReachesIntoTheImage()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reaching);
        Assert.Contains("references TransactHub.Ports, which is the runtime image's", reachingBody);
        Assert.Contains(NativeAssemblies.ImageReference, reachingBody);
    }

    [Fact]
    public void The_WPS_and_platform_modules_reference_the_contract_and_no_other_assembly_of_TransactHub()
    {
        foreach (var module in new[] { Wps, File.ReadAllBytes(typeof(TransactHub.Activities.Platform.Native.PlatformNativeModule).Assembly.Location) })
        {
            var refs = NativeAssemblies.References(module);
            Assert.Contains(TransactHub.Activities.Native.NativeContract.AssemblyName, refs);
            Assert.DoesNotContain(refs, r => r.StartsWith("TransactHub.") && r != TransactHub.Activities.Native.NativeContract.AssemblyName);
        }
        Assert.Contains("TransactHub.Ports", NativeAssemblies.References(ReachesIntoTheImage()));
    }

    /// <summary>A module assembly that references the ports beside the contract, emitted for the test and never loaded.</summary>
    static byte[] ReachesIntoTheImage()
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(Asm) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var tb = ab.DefineDynamicModule(Asm).DefineType($"{Asm}.Reaches", TypeAttributes.Public | TypeAttributes.Class);
        tb.DefineField("bindings", typeof(TransactHub.Ports.PortBindings), FieldAttributes.Public);
        tb.DefineField("module", typeof(TransactHub.Activities.Native.INativeModule), FieldAttributes.Public);
        tb.CreateType();
        var ms = new MemoryStream();
        ab.Save(ms);
        return ms.ToArray();
    }
}
