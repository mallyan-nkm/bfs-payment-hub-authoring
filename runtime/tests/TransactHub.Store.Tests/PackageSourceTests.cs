using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TransactHub.Api;
using TransactHub.Security;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// The studio's content repository on GitHub as a read-only source of signed releases (spec R-09-30): the runtime lists
// the release tags, downloads a version's folder through the Git Data API with a read-only token read by reference, and
// stores it only through the same verification as an upload. GitHub here is a fake of the four endpoints the source
// reads, in memory, with the layout the studio publishes (releases/<scope>/<workflow>/<release>/<target>/ and an
// annotated tag release/<workflow>/<release>/<target> on the commit).

[Collection(DatabaseCollection.Name)]
public sealed class PackageSourceTests(Databases dbs)
{
    const string Root = "content-configurations";

    /// <summary>A GitHub repository as the Git Data API shows it: commits of files, annotated tags, blobs by sha.</summary>
    sealed class FakeGitHub(string token) : HttpMessageHandler
    {
        readonly Dictionary<string, Dictionary<string, byte[]>> commits = new();
        readonly Dictionary<string, (string Commit, string Message)> tagObjects = new();
        readonly Dictionary<string, string> tagRefs = new(StringComparer.Ordinal);
        readonly Dictionary<string, byte[]> blobs = new();
        public readonly List<string> Requests = new();
        static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

        public void Publish(string tag, string folder, Dictionary<string, byte[]> files)
        {
            var all = files.ToDictionary(f => $"{folder}/{f.Key}", f => f.Value);
            all[$"{Root}/README.md"] = "# content\n"u8.ToArray();
            var commit = Sha(Encoding.UTF8.GetBytes(string.Join("|", all.Keys.Order()) + tag));
            commits[commit] = all;
            foreach (var v in all.Values) blobs[Sha(v)] = v;
            var tagSha = Sha(Encoding.UTF8.GetBytes("tag " + tag));
            tagObjects[tagSha] = (commit, $"Release {tag}");
            tagRefs[tag] = tagSha;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            await Task.Yield();
            var path = r.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (r.Headers.Authorization?.ToString() != $"Bearer {token}") return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { message = "Bad credentials" }) };
            const string repo = "/repos/bank/content/";
            if (!path.StartsWith(repo)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var rest = Uri.UnescapeDataString(path[repo.Length..]);
            JsonNode? body = null;
            if (rest == "git/matching-refs/tags/release")
                body = new JsonArray(tagRefs.Where(t => t.Key.StartsWith("release")).OrderBy(t => t.Key).Select(t => (JsonNode)new JsonObject { ["ref"] = $"refs/tags/{t.Key}", ["object"] = new JsonObject { ["sha"] = t.Value, ["type"] = "tag" } }).ToArray());
            else if (rest.StartsWith("git/tags/") && tagObjects.TryGetValue(rest[9..], out var to))
                body = new JsonObject { ["message"] = to.Message, ["object"] = new JsonObject { ["sha"] = to.Commit, ["type"] = "commit" } };
            else if (rest.StartsWith("git/trees/") && commits.TryGetValue(rest[10..], out var files))
                body = new JsonObject { ["truncated"] = false, ["tree"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["type"] = "blob", ["sha"] = Sha(f.Value) }).ToArray()) };
            else if (rest.StartsWith("git/blobs/") && blobs.TryGetValue(rest[10..], out var blob))
                body = new JsonObject { ["encoding"] = "base64", ["content"] = Convert.ToBase64String(blob) };
            return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }

    static Dictionary<string, string> Files(string release, string body = "kind: workflow\n")
    {
        var wf = $"workflows/p.wf.corporate.v1@{release}.yaml";
        return new()
        {
            [wf] = body,
            ["manifest.yaml"] = $"kind: export\ntarget: temporal\npromotable: true\nflows:\n  - name: p.wf.corporate.v1\n    kind: workflow\n    wfKind: platform\n    release: {release}\nfiles:\n  - path: {wf}\n    hash: sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}\n",
        };
    }

    static string Folder(string release) => $"{Root}/releases/p/p.wf.corporate.v1/{release}/temporal";
    static string Tag(string release) => $"release/p.wf.corporate.v1/{release}/temporal";

    ApiHost Host(StoreProvider p, TestSigners s, FakeGitHub gh, string token) => new(dbs.Fresh(p), s, b => b.ConfigureServices(services =>
        services.AddSingleton<IPackageSource>(new GitHubPackageSource(new HttpClient(gh), new Uri("https://api.github.test/"), "bank", "content", Root, "env:TH_TEST_GH_READ_TOKEN",
            SecretResolver.FromEnvironment(k => k == "TH_TEST_GH_READ_TOKEN" ? token : null)))));

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_signed_version_on_GitHub_is_listed_verified_and_stored_once_and_anything_else_is_refused(StoreProvider p)
    {
        using var s = new TestSigners();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = "synthetic-" + Guid.NewGuid().ToString("N");
        var gh = new FakeGitHub(token);
        gh.Publish(Tag("1.0.0"), Folder("1.0.0"), TestSigners.Sign(Files("1.0.0"), s.Key, "p.wf.corporate.v1/temporal@1.0.0"));
        gh.Publish(Tag("1.0.1"), Folder("1.0.1"), Files("1.0.1").ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value)));
        var tampered = TestSigners.Sign(Files("1.0.2"), s.Key, "p.wf.corporate.v1/temporal@1.0.2");
        tampered["workflows/p.wf.corporate.v1@1.0.2.yaml"] = "kind: workflow\n# changed on GitHub\n"u8.ToArray();
        gh.Publish(Tag("1.0.2"), Folder("1.0.2"), tampered);
        gh.Publish(Tag("1.0.3"), Folder("1.0.3"), TestSigners.Sign(Files("1.0.3"), stranger, "p.wf.corporate.v1/temporal@1.0.3"));
        // A tag that names another release than the package in its folder.
        gh.Publish(Tag("1.0.4"), Folder("1.0.4"), TestSigners.Sign(Files("1.0.0"), s.Key, "p.wf.corporate.v1/temporal@1.0.0"));

        await using var api = Host(p, s, gh, token);
        var c = api.Client();
        var versions = (await c.GetFromJsonAsync<JsonArray>("/sources/versions"))!;
        Assert.Equal(["1.0.0", "1.0.1", "1.0.2", "1.0.3", "1.0.4"], versions.Select(v => (string)v!["release"]!));
        Assert.All(versions, v => Assert.False((bool)v!["stored"]!));

        var install = await c.PostAsJsonAsync("/sources/install", new { tag = Tag("1.0.0") });
        Assert.Equal(HttpStatusCode.Created, install.StatusCode);
        Assert.Equal(s.KeyId, (string)(await install.Content.ReadFromJsonAsync<JsonObject>())!["signedBy"]!);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/sources/install", new { tag = Tag("1.0.0") })).StatusCode);
        var got = await c.GetFromJsonAsync<JsonObject>("/releases?scope=p.wf.corporate.v1/temporal&release=1.0.0");
        Assert.Equal($"package-source:{Tag("1.0.0")}", (string)Assert.Single(got!["verifications"]!.AsArray())!["purpose"]!);
        Assert.True((bool)(await c.GetFromJsonAsync<JsonArray>("/sources/versions"))![0]!["stored"]!);

        foreach (var (release, why) in new[] { ("1.0.1", "is not signed"), ("1.0.2", "does not match its hash"), ("1.0.3", "does not trust"), ("1.0.4", "names p.wf.corporate.v1/temporal@1.0.4, but its package is p.wf.corporate.v1/temporal@1.0.0") })
        {
            var r = await c.PostAsJsonAsync("/sources/install", new { tag = Tag(release) });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Contains(why, await r.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/releases?scope=p.wf.corporate.v1/temporal&release={release}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/sources/install", new { tag = "release/p.wf.corporate.v1/9.9.9/temporal" })).StatusCode);
        // Reading versions is a reader's; installing one is an importer's.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client("reader").PostAsJsonAsync("/sources/install", new { tag = Tag("1.0.0") })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client(null).GetAsync("/sources/versions")).StatusCode);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_token_GitHub_refuses_is_a_502_that_shows_no_token_and_no_source_is_a_404(StoreProvider p)
    {
        using var s = new TestSigners();
        var gh = new FakeGitHub("synthetic-the-right-token-" + Guid.NewGuid().ToString("N"));
        var wrong = "synthetic-wrong-" + Guid.NewGuid().ToString("N");
        await using (var api = Host(p, s, gh, wrong))
        {
            var r = await api.Client().GetAsync("/sources/versions");
            Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
            var text = await r.Content.ReadAsStringAsync();
            Assert.Contains("GitHub answered 401", text);
            Assert.DoesNotContain(wrong, text);
        }
        await using var none = new ApiHost(dbs.Fresh(p), s);
        Assert.Equal(HttpStatusCode.NotFound, (await none.Client().GetAsync("/sources/versions")).StatusCode);
    }
}
