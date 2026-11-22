using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Security;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// The release API (plan W4), over HTTP, on SQL Server and on PostgreSQL: a signed package is verified, stored once,
// checked against its manifest, activated by environment; nothing is updated or deleted. Every call carries a service
// token; who may do what is ApiAuthTests.

[Collection(DatabaseCollection.Name)]
public sealed class ReleaseApiTests(Databases dbs)
{
    const string Scope = "p.wf.corporate.v1/temporal";

    /// <summary>A package as the studio exports it: files, and a manifest that lists each with its hash.</summary>
    static Dictionary<string, string> PackageFiles(string release = "1.0.0", bool simulators = false, string workflowBody = "kind: workflow\nname: p.wf.corporate.v1\n")
    {
        var files = new Dictionary<string, string>
        {
            [$"workflows/p.wf.corporate.v1@{release}.yaml"] = workflowBody + $"release: {release}\n",
            ["activities/p.validate.canonical.v1@1.0.0.yaml"] = "kind: activity\nname: p.validate.canonical.v1\nrelease: 1.0.0\n",
        };
        var sha = (string s) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
        var listed = string.Join("", files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"  - path: {f.Key}\n    hash: {sha(f.Value)}\n"));
        files["manifest.yaml"] = $"kind: export\ntarget: temporal\ncontainsSimulators: {(simulators ? "true" : "false")}\npromotable: {(simulators ? "false" : "true")}\nflows:\n  - name: b.hook.add-cost-centre.v1\n    kind: hook\n    release: 1.0.0\n  - name: p.wf.corporate.v1\n    kind: workflow\n    wfKind: platform\n    release: {release}\n  - name: uae.wps.wf.salary-file-out.v1\n    kind: workflow\n    wfKind: leg\n    release: 1.1.0\nfiles:\n{listed}";
        files["README.md"] = "# Export package\n";
        return files;
    }

    static byte[] Zip(TestSigners s, Dictionary<string, string> files, string release = "1.0.0", bool reverse = false) =>
        TestSigners.Zip(TestSigners.Sign(files, s.Key, $"{Scope}@{release}"), reverse);

    static Task<HttpResponseMessage> Post(HttpClient c, byte[] zip) => c.PostAsync("/releases", new ByteArrayContent(zip));

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_package_is_stored_once_whatever_zip_carries_it_and_other_content_is_refused(StoreProvider p)
    {
        using var s = new TestSigners();
        var o = dbs.Fresh(p);
        await using var api = new ApiHost(o, s);
        var c = api.Client();
        var first = await Post(c, Zip(s, PackageFiles()));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = (await first.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(Scope, (string)body["scope"]!);
        Assert.Equal("1.0.0", (string)body["release"]!);
        Assert.Equal(s.KeyId, (string)body["signedBy"]!);

        // The same files in another zip (another order, other times), signed again: the same release, nothing stored.
        var again = await Post(c, Zip(s, PackageFiles(), reverse: true));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((string)body["hash"]!, (string)(await again.Content.ReadFromJsonAsync<JsonObject>())!["hash"]!);

        // Other content at the same release: refused.
        var changed = await Post(c, Zip(s, PackageFiles(workflowBody: "kind: workflow\nname: p.wf.corporate.v1\ntitle: changed\n")));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains("new release", await changed.Content.ReadAsStringAsync());

        // The actor is the caller's verified name; the verification is a fact on the release's chain.
        var got = await c.GetFromJsonAsync<JsonObject>($"/releases?scope={Scope}&release=1.0.0");
        Assert.Equal("pipeline", (string)got!["storedBy"]!);
        var v = Assert.Single(got["verifications"]!.AsArray())!;
        Assert.Equal("upload", (string)v["purpose"]!);
        Assert.Equal(s.KeyId, (string)v["detail"]!["keyId"]!);
        Assert.Equal("CS-TEST-001", (string)v["detail"]!["changeSet"]!);
        await using var db = StoreDbContext.Open(o);
        Assert.True((await new FactLog(db).Verify($"{Scope}@1.0.0")).Ok);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_package_that_does_not_match_its_manifest_or_its_signature_is_refused(StoreProvider p)
    {
        using var s = new TestSigners();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var api = new ApiHost(dbs.Fresh(p), s);
        var c = api.Client();
        var tampered = PackageFiles();
        tampered["activities/p.validate.canonical.v1@1.0.0.yaml"] += "# edited after export\n";
        var r = await Post(c, Zip(s, tampered));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("does not match its hash", await r.Content.ReadAsStringAsync());

        var extra = PackageFiles();
        extra["scripts/smuggled.js"] = "x";
        Assert.Contains("not in the manifest", await (await Post(c, Zip(s, extra))).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(c, Encoding.UTF8.GetBytes("not a zip"))).StatusCode);

        // A file changed after signing, though the manifest was put right with it: the signature refuses it.
        var signed = TestSigners.Sign(PackageFiles(), s.Key, $"{Scope}@1.0.0");
        var after = PackageFiles(workflowBody: "kind: workflow\nname: p.wf.corporate.v1\ntitle: after signing\n");
        foreach (var k in after.Keys) signed[k] = Encoding.UTF8.GetBytes(after[k]);
        var resigned = await Post(c, TestSigners.Zip(signed));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resigned.StatusCode);
        Assert.Contains("does not match its hash in the signed statement", await resigned.Content.ReadAsStringAsync());

        // Unsigned, signed by a key the runtime does not trust, signed for another release: refused, nothing stored.
        var unsigned = await Post(c, TestSigners.Zip(PackageFiles().ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value))));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsigned.StatusCode);
        Assert.Contains("is not signed", await unsigned.Content.ReadAsStringAsync());
        var wrongKey = await Post(c, TestSigners.Zip(TestSigners.Sign(PackageFiles(), stranger, $"{Scope}@1.0.0")));
        Assert.Contains("does not trust", await wrongKey.Content.ReadAsStringAsync());
        var otherRelease = await Post(c, TestSigners.Zip(TestSigners.Sign(PackageFiles(), s.Key, $"{Scope}@9.9.9")));
        Assert.Contains("is for p.wf.corporate.v1/temporal@9.9.9", await otherRelease.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/releases?scope={Scope}&release=1.0.0")).StatusCode);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Activation_picks_what_new_payments_take_and_a_test_package_never_reaches_production(StoreProvider p)
    {
        using var s = new TestSigners();
        await using var api = new ApiHost(dbs.Fresh(p), s);
        var c = api.Client();
        Assert.Equal(HttpStatusCode.Created, (await Post(c, Zip(s, PackageFiles("1.0.0")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(c, Zip(s, PackageFiles("1.1.0", simulators: true), "1.1.0"))).StatusCode);
        var t0 = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        object act(string env, string rel, DateTimeOffset at) => new { environment = env, scope = Scope, release = rel, effectiveFrom = at };

        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/activations", act("prod", "1.0.0", t0))).StatusCode);
        var refused = await c.PostAsJsonAsync("/activations", act("prod", "1.1.0", t0.AddDays(1)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("not promotable", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/activations", act("test", "1.1.0", t0))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/activations", act("prod", "9.9.9", t0))).StatusCode);

        var active = await c.GetFromJsonAsync<JsonObject>($"/activations/active?environment=prod&scope={Scope}&at={Uri.EscapeDataString(t0.AddDays(5).ToString("O"))}");
        Assert.Equal("1.0.0", (string)active!["release"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/activations/active?environment=prod&scope={Scope}&at={Uri.EscapeDataString(t0.AddDays(-1).ToString("O"))}")).StatusCode);
        // Each activation verified the stored package again, and says so on the release's chain.
        var got = await c.GetFromJsonAsync<JsonObject>($"/releases?scope={Scope}&release=1.0.0");
        Assert.Equal(["upload", "activation:prod"], got!["verifications"]!.AsArray().Select(v => (string)v!["purpose"]!));
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_key_revoked_after_a_release_was_stored_stops_its_activation_and_a_rotated_key_takes_over(StoreProvider p)
    {
        using var s = new TestSigners();
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var api = new ApiHost(dbs.Fresh(p), s);
        var c = api.Client();
        Assert.Equal(HttpStatusCode.Created, (await Post(c, Zip(s, PackageFiles("1.0.0")))).StatusCode);
        // Rotation: the new key is trusted beside the old one, with no restart; then the old key is revoked.
        s.Trust([s.Key, next]);
        Assert.Equal(HttpStatusCode.Created, (await Post(c, TestSigners.Zip(TestSigners.Sign(PackageFiles("1.1.0"), next, $"{Scope}@1.1.0")))).StatusCode);
        s.Trust([next], revoked: [s.Key]);
        var refused = await c.PostAsJsonAsync("/activations", new { environment = "prod", scope = Scope, release = "1.0.0" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("was revoked", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/activations", new { environment = "prod", scope = Scope, release = "1.1.0" })).StatusCode);
        Assert.Contains("was revoked", await (await Post(c, Zip(s, PackageFiles("1.2.0"), "1.2.0"))).Content.ReadAsStringAsync());
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Nothing_is_updated_or_deleted(StoreProvider p)
    {
        using var s = new TestSigners();
        await using var api = new ApiHost(dbs.Fresh(p), s);
        var c = api.Client();
        await Post(c, Zip(s, PackageFiles()));
        foreach (var client in new[] { c, api.Client(null) })
            foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
                foreach (var path in new[] { "/releases", "/releases/p.wf.corporate.v1/temporal/1.0.0", "/activations", "/activations/1" })
                {
                    var r = await client.SendAsync(new HttpRequestMessage(method, path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
                    Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
                }
    }

    static string Branch(TestSigners s, Func<Dictionary<string, byte[]>, Dictionary<string, byte[]>>? change = null)
    {
        // The layout the studio writes (R-09-20): releases/<scope>/<name>/<release>/<target>/, signed, and activations.
        var branch = Directory.CreateTempSubdirectory("th-branch-").FullName;
        var dir = Path.Combine(branch, "releases", "p", "p.wf.corporate.v1", "1.0.0", "temporal");
        var files = TestSigners.Sign(PackageFiles(), s.Key, $"{Scope}@1.0.0");
        foreach (var (k, v) in change?.Invoke(files) ?? files)
        {
            var f = Path.Combine(dir, k);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, v);
        }
        File.WriteAllText(Path.Combine(dir, "package.sha256"), "sha256:ignored\n");
        var acts = Path.Combine(branch, "environments", "uat", "activations");
        Directory.CreateDirectory(acts);
        File.WriteAllText(Path.Combine(acts, "CS-2026-11-21-001.yaml"), "kind: activation\nenvironment: uat\nchangeSet: CS-2026-11-21-001\neffectiveFrom: 2026-11-21T08:00:00.000Z\nreleases:\n  - name: p.wf.corporate.v1\n    release: 1.0.0\n  - name: p.validate.canonical.v1\n    release: 1.0.0\n");
        return branch;
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_branch_the_studio_exports_to_imports_and_a_second_import_changes_nothing(StoreProvider p)
    {
        using var s = new TestSigners();
        var branch = Branch(s);
        try
        {
            await using var api = new ApiHost(dbs.Fresh(p), s);
            var c = api.Client();
            var first = (await (await c.PostAsJsonAsync("/import-branch", new { path = branch })).Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Empty(first["problems"]!.AsArray());
            Assert.Equal(201, (int)first["stored"]![0]!["status"]!);
            Assert.Single(first["activated"]!.AsArray());
            var second = (await (await c.PostAsJsonAsync("/import-branch", new { path = branch })).Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal(200, (int)second["stored"]![0]!["status"]!);
            Assert.Empty(second["activated"]!.AsArray());
            var active = await c.GetFromJsonAsync<JsonObject>($"/activations/active?environment=uat&scope={Scope}&at=2026-11-22T00:00:00Z");
            Assert.Equal("1.0.0", (string)active!["release"]!);
            var got = await c.GetFromJsonAsync<JsonObject>($"/releases?scope={Scope}&release=1.0.0");
            Assert.Equal(["import-branch", "activation:uat"], got!["verifications"]!.AsArray().Select(v => (string)v!["purpose"]!));
        }
        finally { Directory.Delete(branch, recursive: true); }
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_branch_package_without_its_signature_or_with_a_file_changed_is_a_problem_and_is_not_stored(StoreProvider p)
    {
        using var s = new TestSigners();
        var unsigned = Branch(s, f => f.Where(x => !ReleaseSignature.IsSignatureFile(x.Key)).ToDictionary());
        var tampered = Branch(s, f => { f["README.md"] = "# Export package, edited on the branch\n"u8.ToArray(); return f; });
        try
        {
            await using var api = new ApiHost(dbs.Fresh(p), s);
            var c = api.Client();
            foreach (var (branch, why) in new[] { (unsigned, "is not signed"), (tampered, "README.md does not match its hash in the signed statement") })
            {
                var r = (await (await c.PostAsJsonAsync("/import-branch", new { path = branch })).Content.ReadFromJsonAsync<JsonObject>())!;
                Assert.Contains(why, (string)Assert.Single(r["problems"]!.AsArray())!);
                Assert.Empty(r["stored"]!.AsArray());
                Assert.Empty(r["activated"]!.AsArray());
            }
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/releases?scope={Scope}&release=1.0.0")).StatusCode);
        }
        finally { Directory.Delete(unsigned, recursive: true); Directory.Delete(tampered, recursive: true); }
    }
}
