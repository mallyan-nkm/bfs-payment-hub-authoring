using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// The release API (plan W4), over HTTP, on SQL Server and on PostgreSQL: a package is stored once,
// checked against its manifest, activated by environment; nothing is updated or deleted.

[Collection(DatabaseCollection.Name)]
public sealed class ReleaseApiTests(Databases dbs)
{
    sealed class Api(StoreOptions o) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseSetting("Store:Provider", o.Provider.ToString());
            b.UseSetting("Store:Connection", o.ConnectionString);
        }
    }

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

    static byte[] Zip(Dictionary<string, string> files, bool reverse = false)
    {
        var ms = new MemoryStream();
        using (var a = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (k, v) in reverse ? files.OrderByDescending(f => f.Key) : files.OrderBy(f => f.Key))
            {
                var e = a.CreateEntry(k);
                e.LastWriteTime = reverse ? DateTimeOffset.Now : new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(Encoding.UTF8.GetBytes(v));
            }
        return ms.ToArray();
    }

    static Task<HttpResponseMessage> Post(HttpClient c, byte[] zip, string actor = "pipeline") =>
        c.PostAsync($"/releases?actor={actor}", new ByteArrayContent(zip));

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_package_is_stored_once_whatever_zip_carries_it_and_other_content_is_refused(StoreProvider p)
    {
        await using var api = new Api(dbs.Fresh(p));
        var c = api.CreateClient();
        var first = await Post(c, Zip(PackageFiles()));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = (await first.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("p.wf.corporate.v1/temporal", (string)body["scope"]!);
        Assert.Equal("1.0.0", (string)body["release"]!);

        // The same files in another zip (another order, other times): the same release, nothing stored.
        var again = await Post(c, Zip(PackageFiles(), reverse: true));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((string)body["hash"]!, (string)(await again.Content.ReadFromJsonAsync<JsonObject>())!["hash"]!);

        // Other content at the same release: refused.
        var changed = await Post(c, Zip(PackageFiles(workflowBody: "kind: workflow\nname: p.wf.corporate.v1\ntitle: changed\n")));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Contains("new release", await changed.Content.ReadAsStringAsync());

        var got = await c.GetFromJsonAsync<JsonObject>("/releases?scope=p.wf.corporate.v1/temporal&release=1.0.0");
        Assert.Equal("pipeline", (string)got!["storedBy"]!);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_package_that_does_not_match_its_manifest_is_refused(StoreProvider p)
    {
        await using var api = new Api(dbs.Fresh(p));
        var c = api.CreateClient();
        var tampered = PackageFiles();
        tampered["activities/p.validate.canonical.v1@1.0.0.yaml"] += "# edited after export\n";
        var r = await Post(c, Zip(tampered));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("does not match its hash", await r.Content.ReadAsStringAsync());

        var extra = PackageFiles();
        extra["scripts/smuggled.js"] = "x";
        Assert.Contains("not in the manifest", await (await Post(c, Zip(extra))).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(c, Encoding.UTF8.GetBytes("not a zip"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/releases", new ByteArrayContent(Zip(PackageFiles())))).StatusCode);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Activation_picks_what_new_payments_take_and_a_test_package_never_reaches_production(StoreProvider p)
    {
        await using var api = new Api(dbs.Fresh(p));
        var c = api.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await Post(c, Zip(PackageFiles("1.0.0")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(c, Zip(PackageFiles("1.1.0", simulators: true)))).StatusCode);
        var t0 = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        object act(string env, string rel, DateTimeOffset at) => new { environment = env, scope = "p.wf.corporate.v1/temporal", release = rel, effectiveFrom = at, actor = "ops" };

        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/activations", act("prod", "1.0.0", t0))).StatusCode);
        var refused = await c.PostAsJsonAsync("/activations", act("prod", "1.1.0", t0.AddDays(1)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("not promotable", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/activations", act("test", "1.1.0", t0))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/activations", act("prod", "9.9.9", t0))).StatusCode);

        var active = await c.GetFromJsonAsync<JsonObject>($"/activations/active?environment=prod&scope=p.wf.corporate.v1/temporal&at={Uri.EscapeDataString(t0.AddDays(5).ToString("O"))}");
        Assert.Equal("1.0.0", (string)active!["release"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/activations/active?environment=prod&scope=p.wf.corporate.v1/temporal&at={Uri.EscapeDataString(t0.AddDays(-1).ToString("O"))}")).StatusCode);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Nothing_is_updated_or_deleted(StoreProvider p)
    {
        await using var api = new Api(dbs.Fresh(p));
        var c = api.CreateClient();
        await Post(c, Zip(PackageFiles()));
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            foreach (var path in new[] { "/releases", "/releases/p.wf.corporate.v1/temporal/1.0.0", "/activations", "/activations/1" })
            {
                var r = await c.SendAsync(new HttpRequestMessage(method, path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
                Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
            }
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_branch_the_studio_exports_to_imports_and_a_second_import_changes_nothing(StoreProvider p)
    {
        // The layout the studio writes (R-09-20): releases/<scope>/<name>/<release>/<target>/ and activations.
        var branch = Directory.CreateTempSubdirectory("th-branch-").FullName;
        var dir = Path.Combine(branch, "releases", "p", "p.wf.corporate.v1", "1.0.0", "temporal");
        foreach (var (k, v) in PackageFiles())
        {
            var f = Path.Combine(dir, k);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, v);
        }
        File.WriteAllText(Path.Combine(dir, "package.sha256"), "sha256:ignored\n");
        var acts = Path.Combine(branch, "environments", "uat", "activations");
        Directory.CreateDirectory(acts);
        File.WriteAllText(Path.Combine(acts, "CS-2026-11-21-001.yaml"), "kind: activation\nenvironment: uat\nchangeSet: CS-2026-11-21-001\neffectiveFrom: 2026-11-21T08:00:00.000Z\nreleases:\n  - name: p.wf.corporate.v1\n    release: 1.0.0\n  - name: p.validate.canonical.v1\n    release: 1.0.0\n");

        await using var api = new Api(dbs.Fresh(p));
        var c = api.CreateClient();
        var first = (await (await c.PostAsJsonAsync("/import-branch", new { path = branch, actor = "pipeline" })).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Empty(first["problems"]!.AsArray());
        Assert.Equal(201, (int)first["stored"]![0]!["status"]!);
        Assert.Single(first["activated"]!.AsArray());
        var second = (await (await c.PostAsJsonAsync("/import-branch", new { path = branch, actor = "pipeline" })).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(200, (int)second["stored"]![0]!["status"]!);
        Assert.Empty(second["activated"]!.AsArray());
        var active = await c.GetFromJsonAsync<JsonObject>("/activations/active?environment=uat&scope=p.wf.corporate.v1/temporal&at=2026-11-22T00:00:00Z");
        Assert.Equal("1.0.0", (string)active!["release"]!);
    }
}
