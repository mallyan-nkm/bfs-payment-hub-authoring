using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Api;

/// <summary>A released version a package source offers: its tag, the release it names, and where its package is.</summary>
/// <param name="Kind">What the tag offers: a workflow package (<c>release/…</c>), or a bank's distribution (<c>dist/&lt;bank&gt;/&lt;id&gt;</c>,
/// docs/runtime/PACKAGING-AND-DEPLOYMENT.md), or a configuration release (<c>config/&lt;bank&gt;/&lt;release&gt;</c>).</param>
public sealed record PublishedVersion(string Tag, string Workflow, string Release, string Target, string Commit, string Folder, string? Message, string Kind = ReleaseSignature.WorkflowPackage)
{
    /// <summary>The release id the runtime stores it as, and the package's signature must name.</summary>
    public string ReleaseId => Kind switch { ReleaseSignature.Distribution => $"dist/{Workflow}@{Release}", ReleaseSignature.ConfigurationRelease => $"config/{Workflow}@{Release}", _ => $"{Workflow}/{Target}@{Release}" };
}

/// <summary>
/// Where an installation takes the signed workflow packages the studio released (spec R-09-30). A source only lists and
/// downloads: what it gives goes through the same verification as any other package (its signature against
/// TH_TRUSTED_SIGNERS, every file against the signed statement) before anything is stored. A source is read-only.
/// </summary>
public interface IPackageSource
{
    Task<IReadOnlyList<PublishedVersion>> List(CancellationToken ct = default);
    /// <summary>The package's files as the source holds them, signature included.</summary>
    Task<Dictionary<string, byte[]>> Download(PublishedVersion version, CancellationToken ct = default);
}

/// <summary>
/// The studio's content repository on GitHub as a package source. Each released version is an annotated tag
/// <c>release/&lt;workflow&gt;/&lt;release&gt;/&lt;target&gt;</c> on the commit that published it, and its package is the folder
/// <c>&lt;root&gt;/releases/&lt;scope&gt;/&lt;workflow&gt;/&lt;release&gt;/&lt;target&gt;/</c> of that commit, read through the Git Data API.
/// The token is read through the secret interface (a fine-grained read-only token or a GitHub App installation token);
/// without one the repository must be public.
/// Configuration: <c>PackageSource:GitHub:Repository</c> (owner/repo), <c>:Api</c> (default https://api.github.com),
/// <c>:Root</c> (default content-configurations), <c>:TokenRef</c> (a secret reference).
/// </summary>
public sealed class GitHubPackageSource(HttpClient http, Uri api, string owner, string repo, string root, string? tokenRef, ISecretResolver secrets) : IPackageSource
{
    public const string TagPrefix = "release/";

    public static GitHubPackageSource? FromConfiguration(IConfiguration cfg, ISecretResolver secrets, HttpClient? http = null)
    {
        var full = cfg["PackageSource:GitHub:Repository"];
        if (string.IsNullOrEmpty(full)) return null;
        var parts = full.Split('/');
        if (parts.Length != 2) throw new InvalidOperationException("PackageSource:GitHub:Repository is owner/repo");
        var tokenRef = cfg["PackageSource:GitHub:TokenRef"];
        if (tokenRef is { Length: > 0 } && !SecretRef.IsRef(tokenRef)) throw new InvalidOperationException("PackageSource:GitHub:TokenRef must be a secret reference (env:, file:, vault:), never the token");
        var api = new Uri((cfg["PackageSource:GitHub:Api"] ?? "https://api.github.com").TrimEnd('/') + "/");
        return new GitHubPackageSource(http ?? new HttpClient(), api, parts[0], parts[1], cfg["PackageSource:GitHub:Root"] ?? "content-configurations", tokenRef, secrets);
    }

    async Task<JsonNode?> Get(string path, CancellationToken ct, bool allow404 = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(api, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/{path}"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("transacthub-runtime", "1"));
        if (tokenRef is { Length: > 0 }) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await secrets.Resolve(tokenRef, ct)).Reveal());
        using var res = await http.SendAsync(req, ct);
        if (allow404 && res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new PackageRejected($"GitHub answered {(int)res.StatusCode} to GET {path.Split('?')[0]}");
        return await res.Content.ReadFromJsonAsync<JsonNode>(ct);
    }

    static string ScopeOf(string name)
    {
        var p = name.Split('.');
        return p[0] is "p" or "b" ? p[0] : $"{p[0]}.{p[1]}";
    }

    public async Task<IReadOnlyList<PublishedVersion>> List(CancellationToken ct = default)
    {
        var refs = new JsonArray();
        foreach (var prefix in new[] { "release", "dist", "config" })
            foreach (var r in await Get($"git/matching-refs/tags/{prefix}", ct, allow404: true) as JsonArray ?? []) refs.Add(r?.DeepClone());
        var list = new List<PublishedVersion>();
        foreach (var r in refs)
        {
            var tag = ((string?)r?["ref"] ?? "").Replace("refs/tags/", "");
            var parts = tag.Split('/');
            // release/<workflow>/<release>/<target>, or dist/<bank>/<id>: any other tag is not a release the studio made.
            var dist = parts.Length == 3 && parts[0] == "dist";
            var config = parts.Length == 3 && parts[0] == "config";
            if (!dist && !config && (parts.Length != 4 || parts[0] != "release")) continue;
            var sha = (string?)r?["object"]?["sha"] ?? "";
            string commit = sha;
            string? message = null;
            if ((string?)r?["object"]?["type"] == "tag")
            {
                var t = await Get($"git/tags/{sha}", ct);
                commit = (string?)t?["object"]?["sha"] ?? "";
                message = (string?)t?["message"];
            }
            list.Add(config
                ? new PublishedVersion(tag, parts[1], parts[2], "", commit, $"{root}/releases/configuration/{parts[1]}/{parts[2]}", message, ReleaseSignature.ConfigurationRelease)
                : dist
                ? new PublishedVersion(tag, parts[1], parts[2], "", commit, $"{root}/releases/distributions/{parts[1]}/{parts[2]}", message, ReleaseSignature.Distribution)
                : new PublishedVersion(tag, parts[1], parts[2], parts[3], commit, $"{root}/releases/{ScopeOf(parts[1])}/{parts[1]}/{parts[2]}/{parts[3]}", message));
        }
        return list.OrderBy(v => v.Tag, StringComparer.Ordinal).ToList();
    }

    public async Task<Dictionary<string, byte[]>> Download(PublishedVersion v, CancellationToken ct = default)
    {
        var tree = await Get($"git/trees/{v.Commit}?recursive=1", ct) ?? throw new PackageRejected($"No commit {v.Commit} for {v.Tag}");
        if (tree["truncated"]?.GetValue<bool>() == true) throw new PackageRejected($"The tree of {v.Tag} is too large for one read");
        var prefix = v.Folder + "/";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var e in tree["tree"] as JsonArray ?? [])
        {
            var path = (string?)e?["path"] ?? "";
            if ((string?)e?["type"] != "blob" || !path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rel = path[prefix.Length..];
            if (rel == "package.sha256") continue;
            var b = await Get($"git/blobs/{(string?)e?["sha"]}", ct);
            var content = (string?)b?["content"] ?? "";
            files[rel] = (string?)b?["encoding"] == "base64" ? Convert.FromBase64String(content.Replace("\n", "")) : System.Text.Encoding.UTF8.GetBytes(content);
        }
        if (files.Count == 0) throw new PackageRejected($"{v.Tag} names {v.Folder}, which holds no package at {v.Commit[..Math.Min(12, v.Commit.Length)]}");
        return files;
    }
}
