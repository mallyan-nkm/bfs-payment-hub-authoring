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
    static byte[] Distribution(ECDsa key, string id, byte[] assembly, string? claimedSha = null, string contract = TransactHub.Activities.Native.NativeContract.Version, string name = Asm, string? stray = null,
        string scope = "uae.wps", string[]? activities = null, IReadOnlyDictionary<string, string>? docs = null, JsonObject? postsWith = null)
    {
        var path = $"activities/native/{scope}/{name}.dll.base64";
        var files = new Dictionary<string, string> { ["binding.yaml"] = "kind: engine-binding\ntarget: temporal\n", [path] = Convert.ToBase64String(assembly) + "\n" };
        // Activity documents the bill of materials carries beside the module's (R-09-291): name@release to its YAML.
        foreach (var (a, yaml) in docs ?? new Dictionary<string, string>()) files[$"activities/{a}.yaml"] = yaml;
        if (stray is not null) files[stray] = Convert.ToBase64String(assembly) + "\n";
        var real = Sha(assembly);
        var artefacts = new JsonArray(new JsonObject { ["path"] = "binding.yaml", ["sha256"] = Sha(Encoding.UTF8.GetBytes(files["binding.yaml"])) },
            new JsonObject { ["scope"] = scope, ["kind"] = "native-assembly", ["name"] = $"{name}.dll", ["path"] = path, ["sha256"] = Sha(Encoding.UTF8.GetBytes(files[path])), ["assemblySha256"] = claimedSha ?? real });
        foreach (var a in activities ?? ["uae.wps.map.ack-in.v1@1.2.0"])
            artefacts.Add(new JsonObject { ["scope"] = scope, ["kind"] = "activity", ["name"] = a.Split('@')[0], ["release"] = a.Split('@')[1] });
        foreach (var (a, _) in docs ?? new Dictionary<string, string>())
            artefacts.Add(new JsonObject { ["scope"] = a.Split('.')[0], ["kind"] = "activity", ["name"] = a.Split('@')[0], ["release"] = a.Split('@')[1], ["path"] = $"activities/{a}.yaml", ["sha256"] = Sha(Encoding.UTF8.GetBytes(files[$"activities/{a}.yaml"])) });
        var bom = new JsonObject { ["packs"] = new JsonArray(), ["artefacts"] = artefacts };
        var pc = new JsonObject { ["format"] = "th-parameter-contract/1", ["parameters"] = new JsonArray() };
        var native = new JsonObject
        {
            ["contract"] = contract,
            ["modules"] = new JsonArray(new JsonObject
            {
                ["scope"] = scope, ["assembly"] = name, ["activities"] = new JsonArray((activities ?? ["uae.wps.map.ack-in.v1@1.2.0"]).Select(a => (JsonNode)a).ToArray()),
                ["files"] = new JsonArray(new JsonObject { ["path"] = path, ["name"] = $"{name}.dll", ["sha256"] = claimedSha ?? real }),
            }),
        };
        if (postsWith is not null) native["postsWith"] = postsWith;
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

        // Built against a contract this runtime does not support, or claiming one its assembly was not built against: its own code (R-09-262).
        var (future, futureBody) = await Post(c, Distribution(s.Key, "2026.11.23-3", Wps, contract: "3.0.0"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, future);
        Assert.Contains("native contract 3.0.0", futureBody);
        Assert.Contains(NativeAssemblies.ContractUnsupported, futureBody);
        var (claims, claimsBody) = await Post(c, Distribution(s.Key, "2026.11.23-4", Wps, contract: "1.1.0"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, claims);
        Assert.Contains(NativeAssemblies.ContractUnsupported, claimsBody);
        // A module built against contract 1.x, as every module before 2.0.0 was: refused at install, to be rebuilt (Issue I-224 superseded).
        var (old, oldBody) = await Post(c, Distribution(s.Key, "2026.11.23-9", BuiltAgainstContract1(), contract: "1.1.0"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, old);
        Assert.Contains(NativeAssemblies.ContractUnsupported, oldBody);
        Assert.Contains($"rebuild every module against {TransactHub.Activities.Native.NativeContract.Version}", oldBody);
        var (oldClaims, oldClaimsBody) = await Post(c, Distribution(s.Key, "2026.11.23-10", BuiltAgainstContract1()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, oldClaims);
        Assert.Contains($"was built against native contract 1.1.0, its manifest says {TransactHub.Activities.Native.NativeContract.Version}", oldClaimsBody);

        // Contract 2.1.0 is a minor (R-09-276): a module built against 2.0 is installed, under a 2.0 manifest or a 2.1 one; a module built
        // against 2.1 under a manifest that names 2.0, and a later minor than the runtime's, are refused.
        var wps20 = TransactHub.Tests.Support.ContractBuilds.BuiltAgainst(Wps, "2.0.0");
        var (two0, two0Body) = await Post(c, Distribution(s.Key, "2026.11.24-41", wps20, contract: "2.0.0"));
        Assert.True(two0 == HttpStatusCode.Created, two0Body);
        var (two0on21, two0on21Body) = await Post(c, Distribution(s.Key, "2026.11.24-42", wps20, contract: "2.1.0"));
        Assert.True(two0on21 == HttpStatusCode.Created, two0on21Body);
        var (under, underBody) = await Post(c, Distribution(s.Key, "2026.11.24-43", Wps, contract: "2.0.0"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, under);
        Assert.Contains($"was built against native contract {TransactHub.Activities.Native.NativeContract.Version}, its manifest says 2.0.0", underBody);
        var (later, laterBody) = await Post(c, Distribution(s.Key, "2026.11.24-44", Wps, contract: "2.3.0"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, later);
        Assert.Contains(NativeAssemblies.ContractUnsupported, laterBody);

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

    /// <summary>The reservation as factory 1.22.0 published it: a builtin of the reservation service on core banking, which the image answered.</summary>
    const string ReservationBefore21 = "kind: activity\nname: p.reserve.funds.v1\nrelease: 1.0.0\ntype: reserve\nports:\n  - p.port.core-banking.v1\nimplementation:\n  binding: builtin\n  service: reservation\n";
    /// <summary>The reservation since factory 1.23.0: the platform module answers it (R-09-282).</summary>
    const string Reservation = "kind: activity\nname: p.reserve.funds.v1\nrelease: 1.1.0\ntype: reserve\nports:\n  - p.port.core-banking.v1\nimplementation:\n  binding: builtin\n  service: reservation\n  native: true\n";
    /// <summary>A hold: a builtin the image answers itself.</summary>
    const string Hold = "kind: activity\nname: p.hold.approval.v1\nrelease: 1.1.0\ntype: hold\nports:\n  - p.port.operations-queue.v1\nimplementation:\n  binding: builtin\n  service: operations\n";
    /// <summary>A rule-service builtin: the worker answers it, never the native host.</summary>
    const string Limits = "kind: activity\nname: p.validate.limits.v1\nrelease: 1.0.0\ntype: validate\nimplementation:\n  binding: builtin\n  service: rule-service\n";

    /// <summary>
    /// R-09-291 (Issue I-384), on SQL Server and PostgreSQL: the release API refuses at install a distribution whose bill of materials
    /// carries an activity the runtime's native host answers (here the reservation as factory 1.22.0 published it, a builtin on core
    /// banking) that neither the image nor a module of it serves, with its own code; and the native host's check refuses it again as it
    /// loads one that was stored before (the served activities read from the module as loaded). The same distribution with the reservation
    /// the platform module lists is installed, and the image's own activities and the worker's builtins are no gap.
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_distribution_whose_native_activity_nothing_serves_is_refused_at_install_and_at_load(StoreProvider p)
    {
        using var s = new TestSigners();
        var options = dbs.Fresh(p);
        await using var host = new ApiHost(options, s);
        var c = host.Client();
        var before = new Dictionary<string, string> { ["p.reserve.funds.v1@1.0.0"] = ReservationBefore21, ["p.hold.approval.v1@1.1.0"] = Hold, ["p.validate.limits.v1@1.0.0"] = Limits };
        var zip = Distribution(s.Key, "2026.11.24-61", Wps, docs: before);
        var (refused, refusedBody) = await Post(c, zip);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused);
        Assert.Contains(NativeAssemblies.ActivityUnserved, refusedBody);
        Assert.Contains("p.reserve.funds.v1@1.0.0", refusedBody);
        Assert.DoesNotContain("p.hold.approval.v1", refusedBody);
        Assert.DoesNotContain("p.validate.limits.v1", refusedBody);
        Assert.Contains("section 3.3", refusedBody);
        // The refusal names the rebuild: the factory release and the native contract (R-09-308, Issue I-399).
        Assert.Contains($"rebuild the distribution once from a studio of factory {NativeServed.RebuildFactory} or later, with its module builds made against native contract {TransactHub.Activities.Native.NativeContract.Version}", refusedBody);

        // Rebuilt at factory 1.23.0: the reservation says native: true and the platform module serves it. Installed.
        var platform = File.ReadAllBytes(typeof(TransactHub.Activities.Platform.Native.PlatformNativeModule).Assembly.Location);
        var (ok, okBody) = await Post(c, Distribution(s.Key, "2026.11.24-62", platform, scope: "p", name: "TransactHub.Activities.Platform.Native",
            activities: ["p.reserve.funds.v1@1.1.0"], docs: new Dictionary<string, string> { ["p.hold.approval.v1@1.1.0"] = Hold, ["p.validate.limits.v1@1.0.0"] = Limits }));
        Assert.True(ok == HttpStatusCode.Created, okBody);

        // Stored before the release API checked it (installed by an earlier runtime): the native host's check refuses it as it loads it.
        var files = NativeAssemblies.Unzip(zip);
        var manifest = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(files["manifest.yaml"]))!;
        await using (var db = StoreDbContext.Open(options))
            await new DistributionRegistry(db).Store(Bank, "2026.11.24-63", zip, manifest, Sha(zip)[7..], "test", "installed before R-09-291");
        await using (var db = StoreDbContext.Open(options))
        {
            var row = await new ReleaseRegistry(db).Get(DistributionRegistry.Scope(Bank), "2026.11.24-63") ?? throw new Xunit.Sdk.XunitException("not stored");
            var stored = NativeAssemblies.Unzip(row.Package);
            var kept = (JsonObject)JsonNode.Parse(row.ManifestJson)!;
            var module = Assert.Single(NativeAssemblies.Check(kept, f => stored.TryGetValue(f, out var x) ? x : null, stored.Keys));
            var ctx = new ModuleContext(module.Files);
            try
            {
                var asm = ctx.LoadFromAssemblyName(new AssemblyName(module.Assembly));
                var entry = (TransactHub.Activities.Native.INativeModule)Activator.CreateInstance(asm.GetCustomAttribute<TransactHub.Activities.Native.NativeModuleAttribute>()!.Module)!;
                var serves = entry.Activities(new Services(new TransactHub.Activities.Platform.Posting.CoreBankingResolver(new TransactHub.Ports.PortBindings()))).Select(a => a.Name).ToHashSet();
                var unserved = NativeServed.Unserved(kept, f => stored.TryGetValue(f, out var x) ? x : null, (n, _) => serves.Contains(n));
                Assert.Equal(["p.reserve.funds.v1@1.0.0"], unserved);
                Assert.StartsWith(NativeAssemblies.ActivityUnserved, NativeServed.Refusal(unserved));
            }
            finally { ctx.Unload(); }
        }
    }

    /// <summary>An imported scheme's posting that posts with the platform's (R-09-311, Issue I-400): a builtin the runtime answers.</summary>
    const string SchemePosting = "kind: activity\nname: uae.xps.post.payment.v1\nrelease: 1.0.0\ntype: post\nports:\n  - p.port.core-banking.v1\nimplementation:\n  binding: builtin\n  service: accounting\n  native: true\n  postsWith: p.post.accounting.v1\n";

    /// <summary>
    /// R-09-311 (Issue I-400), on SQL Server and PostgreSQL: a scheme's posting that posts with the platform's is served by the platform
    /// module, so the release API installs a distribution that carries it with the platform module, and refuses one without it
    /// (`native.ActivityUnserved`, naming the posting), or one whose manifest has it post with an activity the platform module does not list.
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_schemes_posting_that_posts_with_the_platforms_needs_the_platform_module(StoreProvider p)
    {
        using var s = new TestSigners();
        await using var host = new ApiHost(dbs.Fresh(p), s);
        var c = host.Client();
        var docs = new Dictionary<string, string> { ["uae.xps.post.payment.v1@1.0.0"] = SchemePosting };
        var platform = File.ReadAllBytes(typeof(TransactHub.Activities.Platform.Native.PlatformNativeModule).Assembly.Location);
        JsonObject To(string target) => new() { ["uae.xps.post.payment.v1@1.0.0"] = target };
        var (ok, okBody) = await Post(c, Distribution(s.Key, "2026.11.25-71", platform, scope: "p", name: "TransactHub.Activities.Platform.Native",
            activities: ["p.post.accounting.v1@1.3.0"], docs: docs, postsWith: To("p.post.accounting.v1")));
        Assert.True(ok == HttpStatusCode.Created, okBody);
        // Without the platform module (only WPS's): nothing answers the posting, and core banking is not reached any other way.
        var (none, noneBody) = await Post(c, Distribution(s.Key, "2026.11.25-72", Wps, docs: docs, postsWith: To("p.post.accounting.v1")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none);
        Assert.Contains(NativeAssemblies.ActivityUnserved, noneBody);
        Assert.Contains("uae.xps.post.payment.v1@1.0.0", noneBody);
        // Posting with an activity the platform module does not list is not taken: the posting is then served by nothing.
        var (other, otherBody) = await Post(c, Distribution(s.Key, "2026.11.25-73", platform, scope: "p", name: "TransactHub.Activities.Platform.Native",
            activities: ["p.post.accounting.v1@1.3.0"], docs: docs, postsWith: To("p.post.verify.v1")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other);
        Assert.Contains("uae.xps.post.payment.v1@1.0.0", otherBody);
    }

    /// <summary>R-09-291: what the native host answers on a runtime, read from an activity's document.</summary>
    [Fact]
    public void The_host_answers_a_native_activity_and_a_builtin_on_core_banking()
    {
        JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;
        Assert.True(NativeServed.AnsweredByTheHost(Doc("""{ "implementation": { "binding": "native" } }""")));
        Assert.True(NativeServed.AnsweredByTheHost(Doc("""{ "implementation": { "binding": "script", "native": "true" } }""")));
        Assert.True(NativeServed.AnsweredByTheHost(Doc("""{ "ports": ["p.port.core-banking.v1"], "implementation": { "binding": "builtin", "service": "accounting" } }""")));
        Assert.False(NativeServed.AnsweredByTheHost(Doc("""{ "implementation": { "binding": "builtin", "service": "rule-service" } }""")));
        Assert.False(NativeServed.AnsweredByTheHost(Doc("""{ "ports": ["p.port.payment-record.v1"], "implementation": { "binding": "builtin", "service": "payment-record" } }""")));
        Assert.False(NativeServed.AnsweredByTheHost(Doc("""{ "implementation": { "binding": "script" } }""")));
    }

    /// <summary>One distribution's load context, as the native host makes it: the module from its bytes, the contract from the image's.</summary>
    sealed class ModuleContext(IReadOnlyDictionary<string, byte[]> carried) : System.Runtime.Loader.AssemblyLoadContext("th-test-platform-module", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName n) =>
            n.Name is { } name && !NativeAssemblies.Shared.Contains(name) && carried.TryGetValue(name, out var bytes) ? LoadFromStream(new MemoryStream(bytes)) : null;
    }

    /// <summary>What the image gives a distribution's modules: its core banking, gated by what they contribute.</summary>
    sealed class Services(TransactHub.Activities.Platform.Posting.CoreBankingResolver resolver) : TransactHub.Activities.Native.INativeServices
    {
        readonly Dictionary<string, object> contributed = new(StringComparer.Ordinal);
        public string? Distribution => $"dist/{Bank}@2026.11.24-45";
        public void KeepRendered(string sha256, string fileName, byte[] content) { }
        public void Contribute(string key, object value) => contributed[key] = value;
        public object? Contribution(string key) => contributed.GetValueOrDefault(key);
        public TransactHub.Activities.Native.INativeCoreBanking CoreBanking => new TransactHub.Activities.Platform.Posting.NativeCoreBanking(resolver, Contribution);
        public TransactHub.Activities.Native.INativeFiles Files { get; } = new TransactHub.Activities.Native.MemoryNativeFiles();
        public TransactHub.Activities.Native.INativePorts Ports => new TransactHub.Activities.Native.NativePorts(new TransactHub.Ports.PortBindings(), Files);
        public TransactHub.Activities.Native.INativeBoundPorts BoundPorts => new TransactHub.Activities.Native.NativeBoundPorts(new TransactHub.Ports.PortBindings());
    }

    /// <summary>
    /// R-09-278 (Issue I-382), on SQL Server and PostgreSQL: the platform module as this repository builds it is installed by the release
    /// API, read back from the store, checked again and loaded in a context of its own, as the native host loads it; its reservation, its
    /// posting, the check of the posting, the reversal and the release run on the image's core banking (the simulator binding), gated by
    /// the posting window gate the module contributes. The image answers none of them.
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_platform_modules_reservation_posting_and_reversal_run_from_the_build_the_store_keeps(StoreProvider p)
    {
        using var s = new TestSigners();
        var options = dbs.Fresh(p);
        await using var host = new ApiHost(options, s);
        var platform = File.ReadAllBytes(typeof(TransactHub.Activities.Platform.Native.PlatformNativeModule).Assembly.Location);
        string[] served = ["p.reserve.funds.v1@1.1.0", "p.reserve.release.v1@1.1.0", "p.post.accounting.v1@1.3.0", "p.post.verify.v1@1.1.0", "p.post.reverse.v1@1.1.0"];
        var (st, body) = await Post(host.Client(), Distribution(s.Key, "2026.11.24-45", platform, scope: "p", name: "TransactHub.Activities.Platform.Native", activities: served));
        Assert.True(st == HttpStatusCode.Created, body);

        await using var db = StoreDbContext.Open(options);
        var row = await new ReleaseRegistry(db).Get(DistributionRegistry.Scope(Bank), "2026.11.24-45") ?? throw new Xunit.Sdk.XunitException("not stored");
        var files = NativeAssemblies.Unzip(row.Package);
        var module = Assert.Single(NativeAssemblies.Check((JsonObject)JsonNode.Parse(row.ManifestJson)!, f => files.TryGetValue(f, out var x) ? x : null, files.Keys));
        Assert.Equal(served, module.Activities);
        var ctx = new ModuleContext(module.Files);
        try
        {
            var asm = ctx.LoadFromAssemblyName(new AssemblyName(module.Assembly));
            Assert.NotSame(typeof(TransactHub.Activities.Platform.Native.PlatformNativeModule).Assembly, asm);
            var entry = (TransactHub.Activities.Native.INativeModule)Activator.CreateInstance(asm.GetCustomAttribute<TransactHub.Activities.Native.NativeModuleAttribute>()!.Module)!;
            var bindings = new TransactHub.Ports.PortBindings();
            bindings.Add($"kind: port-bindings\ntenant: {Bank}\nbindings:\n  - port: p.port.core-banking.v1\n    adapter: simulator\n    validFrom: 2026-01-01T00:00:00Z\n    settings:\n      openingBalance: \"5000\"\n      currency: AED\n      postingWindowStart: \"07:00\"\n      postingWindowEnd: \"22:00\"\n");
            var resolver = new TransactHub.Activities.Platform.Posting.CoreBankingResolver(bindings);
            var table = entry.Activities(new Services(resolver)).ToDictionary(a => a.Name);
            foreach (var a in served) Assert.Contains(a.Split('@')[0], table.Keys);

            var now = DateTimeOffset.Parse("2026-11-24T06:00:00Z");   // 10:00 in the UAE, inside the binding's window
            var view = (JsonObject)JsonNode.Parse("""
                { "header": { "paymentId": "P-DB-1", "direction": "outward", "businessDate": "2026-11-24" },
                  "payment": { "debtorAccount": { "id": "0010000000001" }, "instructedAmount": { "value": 3500, "currency": "AED" } }, "batch": { "controlSum": 3500 } }
                """)!;
            var template = (JsonObject)JsonNode.Parse("""
                { "template": "default", "transactionCode": "PAY", "reversalTransactionCode": "REV", "narration": "Payment", "settlementAccount": "9990000000001", "chargeSlabs": [], "vatPercent": 0 }
                """)!;
            TransactHub.Activities.Native.NativeResult Run(string activity, JsonObject? p = null, DateTimeOffset? at = null) =>
                table[activity].Run(new(activity, served.First(x => x.StartsWith(activity + "@")).Split('@')[1], view, p ?? new JsonObject(), at ?? now, Bank));
            void Take(TransactHub.Activities.Native.NativeResult r)
            {
                var facts = view["facts"] as JsonObject ?? (JsonObject)(view["facts"] = new JsonObject());
                foreach (var (k, v) in r.Writes) facts[k["facts.".Length..]] = v?.DeepClone();
            }

            var reserved = Run("p.reserve.funds.v1");
            Assert.Equal("reserved", reserved.Outcome);
            Assert.Equal(5000m, (decimal)reserved.Writes["facts.reservation"]!["available"]!);
            Take(reserved);
            var posted = Run("p.post.accounting.v1", template);
            Assert.Equal("posted", posted.Outcome);
            Take(posted);
            Assert.Equal("verified", Run("p.post.verify.v1").Outcome);
            var reversed = Run("p.post.reverse.v1");
            Assert.Equal("reversed", reversed.Outcome);
            Take(reversed);
            Assert.Equal("released", Run("p.reserve.release.v1").Outcome);
            var batches = resolver.Built.Select(x => x.CoreBanking).OfType<TransactHub.Ports.SimulatedCoreBanking>().Single().Snapshot();
            Assert.Equal(["P-DB-1", "P-DB-1:REV"], batches.Select(b => b.Reference));
            Assert.Equal(["credit", "debit"], batches[1].Legs.Select(l => l.Kind));
            Assert.All(batches[1].Legs, l => Assert.Equal("REV", l.TxnCode));
            // The window is the module's gate: outside it the reservation is a retryable error and core banking is not asked.
            var closed = Assert.Throws<TransactHub.Activities.Native.NativeFailure>(() => Run("p.reserve.funds.v1", at: DateTimeOffset.Parse("2026-11-24T20:00:00Z")));
            Assert.Equal(("TransientError", true), (closed.Name, closed.Retryable));
        }
        finally { ctx.Unload(); }
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

    /// <summary>
    /// A module assembly as a build against native contract 1.1.0 leaves it (its reference to the contract at 1.1.0.0), written with
    /// System.Reflection.Metadata for the test and never loaded.
    /// </summary>
    static byte[] BuiltAgainstContract1()
    {
        var mb = new System.Reflection.Metadata.Ecma335.MetadataBuilder();
        mb.AddModule(0, mb.GetOrAddString($"{Asm}.dll"), mb.GetOrAddGuid(new Guid("6f1c2a0e-3b7d-4c55-9a51-0c1d2e3f4a5b")), default, default);
        mb.AddAssembly(mb.GetOrAddString(Asm), new Version(1, 0, 0, 0), default, default, 0, System.Reflection.AssemblyHashAlgorithm.Sha1);
        var contract = mb.AddAssemblyReference(mb.GetOrAddString("TransactHub.Activities.Contract"), new Version(1, 1, 0, 0), default, default, 0, default);
        mb.AddTypeReference(contract, mb.GetOrAddString("TransactHub.Activities.Native"), mb.GetOrAddString("INativeModule"));
        mb.AddTypeDefinition(default, default, mb.GetOrAddString("<Module>"), default,
            System.Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle(1), System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle(1));
        var pe = new System.Reflection.PortableExecutable.ManagedPEBuilder(System.Reflection.PortableExecutable.PEHeaderBuilder.CreateLibraryHeader(),
            new System.Reflection.Metadata.Ecma335.MetadataRootBuilder(mb), new System.Reflection.Metadata.BlobBuilder());
        var blob = new System.Reflection.Metadata.BlobBuilder();
        pe.Serialize(blob);
        return blob.ToArray();
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
