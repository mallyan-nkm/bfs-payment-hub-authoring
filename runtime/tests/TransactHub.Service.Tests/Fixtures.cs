using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using TransactHub.Api;
using TransactHub.Model;
using TransactHub.Operations;
using TransactHub.Security;
using TransactHub.Service;
using TransactHub.Service.Api;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>PostgreSQL 16, one container for the run; each test gets a database of its own.</summary>
public sealed class Databases : IAsyncLifetime
{
    readonly PostgreSqlContainer pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public Task InitializeAsync() => pg.StartAsync();
    public Task DisposeAsync() => pg.DisposeAsync().AsTask();

    public StoreOptions Fresh() => new(StoreProvider.PostgreSql, new Npgsql.NpgsqlConnectionStringBuilder(pg.GetConnectionString()) { Database = $"th_svc_{Guid.NewGuid():N}"[..24] }.ConnectionString);
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<Databases> { public const string Name = "service-databases"; }

public static class Repo
{
    public static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "spec")) && Directory.Exists(Path.Combine(d.FullName, "runtime"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}

/// <summary>A signing key of the test's own, and the trusted-signers file the runtime reads (never beyond the test's folder).</summary>
public sealed class Signers : IDisposable
{
    public readonly string Dir = Directory.CreateTempSubdirectory("th-svc-").FullName;
    public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public readonly ECDsa BackupKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string KeyId => ReleaseSignature.KeyIdOf(Key);
    public string TrustFile => Path.Combine(Dir, "trusted-signers.json");
    public string BackupKeyFile => Path.Combine(Dir, "backup-key.pem");

    public Signers()
    {
        Trust([Key, BackupKey]);
        File.WriteAllText(BackupKeyFile, BackupKey.ExportPkcs8PrivateKeyPem());
    }

    public void Trust(IEnumerable<ECDsa> keys) => File.WriteAllText(TrustFile, new JsonObject
    {
        ["kind"] = "trusted-signers",
        ["keys"] = new JsonArray(keys.Select(k => (JsonNode)new JsonObject { ["keyId"] = ReleaseSignature.KeyIdOf(k), ["publicKey"] = k.ExportSubjectPublicKeyInfoPem() }).ToArray()),
        ["revoked"] = new JsonArray(),
    }.ToJsonString());

    public static Dictionary<string, byte[]> Sign(Dictionary<string, string> files, ECDsa key, string releaseId)
    {
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var parts = releaseId.Split('@');
        var wf = parts[0].Split('/');
        var (st, sig) = ReleaseSignature.Sign(bytes, new ReleaseClaims(releaseId, wf[0], parts[1], wf[1], null, "CS-TEST-001"), key, DateTimeOffset.UtcNow);
        bytes[ReleaseSignature.StatementFile] = st;
        bytes[ReleaseSignature.SignatureFile] = sig;
        return bytes;
    }

    public void Dispose()
    {
        Key.Dispose();
        BackupKey.Dispose();
        Directory.Delete(Dir, recursive: true);
    }
}

/// <summary>A GitHub repository as the Git Data API shows it, in memory: the four endpoints the package source reads.</summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    const string Root = "content-configurations";
    readonly Dictionary<string, Dictionary<string, byte[]>> commits = new();
    readonly Dictionary<string, (string Commit, string Message)> tagObjects = new();
    readonly Dictionary<string, string> tagRefs = new(StringComparer.Ordinal);
    readonly Dictionary<string, byte[]> blobs = new();
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    public void Publish(string tag, string folder, Dictionary<string, byte[]> files)
    {
        var all = files.ToDictionary(f => $"{folder}/{f.Key}", f => f.Value);
        var commit = Sha(Encoding.UTF8.GetBytes(string.Join("|", all.Keys.Order()) + tag));
        commits[commit] = all;
        foreach (var v in all.Values) blobs[Sha(v)] = v;
        var tagSha = Sha(Encoding.UTF8.GetBytes("tag " + tag));
        tagObjects[tagSha] = (commit, $"Release {tag}");
        tagRefs[tag] = tagSha;
    }

    public static string Folder(string workflow, string release, string target = "temporal") => $"{Root}/releases/p/{workflow}/{release}/{target}";
    public static string Tag(string workflow, string release, string target = "temporal") => $"release/{workflow}/{release}/{target}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        await Task.Yield();
        var path = r.RequestUri!.AbsolutePath;
        const string repo = "/repos/bank/content/";
        if (!path.StartsWith(repo)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var rest = Uri.UnescapeDataString(path[repo.Length..]);
        JsonNode? body = null;
        foreach (var prefix in new[] { "release", "dist", "config" })
            if (rest == $"git/matching-refs/tags/{prefix}")
                body = new JsonArray(tagRefs.Where(t => t.Key.StartsWith(prefix)).OrderBy(t => t.Key).Select(t => (JsonNode)new JsonObject { ["ref"] = $"refs/tags/{t.Key}", ["object"] = new JsonObject { ["sha"] = t.Value, ["type"] = "tag" } }).ToArray());
        if (rest.StartsWith("git/tags/") && tagObjects.TryGetValue(rest[9..], out var to))
            body = new JsonObject { ["message"] = to.Message, ["object"] = new JsonObject { ["sha"] = to.Commit, ["type"] = "commit" } };
        else if (rest.StartsWith("git/trees/") && commits.TryGetValue(rest[10..].Split('?')[0], out var files))
            body = new JsonObject { ["truncated"] = false, ["tree"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["type"] = "blob", ["sha"] = Sha(f.Value) }).ToArray()) };
        else if (rest.StartsWith("git/blobs/") && blobs.TryGetValue(rest[10..], out var blob))
            body = new JsonObject { ["encoding"] = "base64", ["content"] = Convert.ToBase64String(blob) };
        return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}

/// <summary>A minimal studio package: one workflow, an activity with a parameter, and the platform's error dictionary.</summary>
public static class Packages
{
    public static Dictionary<string, string> Files(string workflow, string release, string limit = "250000", bool promotable = true)
    {
        var wf = $"workflows/{workflow}@{release}.yaml";
        var act = "activities/p.validate.limits.v1@1.0.0.yaml";
        var errors = "errors/p.errors.platform.v1@1.1.0.yaml";
        var files = new Dictionary<string, string>
        {
            [wf] = $"kind: workflow\nname: {workflow}\nrelease: {release}\n",
            [act] = $"kind: activity\nname: p.validate.limits.v1\nrelease: 1.0.0\nparameters:\n  maxAmount:\n    type: amount\n    default: {limit}\n",
            [errors] = File.ReadAllText(Path.Combine(Repo.Root(), "spec", "factory", "p", "errors", "p.errors.platform.v1.yaml")),
        };
        string H(string s) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
        files["manifest.yaml"] = $"kind: export\ntarget: temporal\npromotable: {(promotable ? "true" : "false")}\nflows:\n  - name: {workflow}\n    kind: workflow\n    wfKind: platform\n    release: {release}\n    hash: {H(files[wf])}\nfiles:\n"
            + string.Concat(new[] { act, errors, wf }.Select(p => $"  - path: {p}\n    hash: {H(files[p])}\n"));
        return files;
    }

    public static string Id(string workflow, string release) => $"{workflow}/temporal@{release}";

    public static void Publish(FakeGitHub gh, ECDsa key, string workflow, string release, string limit = "250000")
        => gh.Publish(FakeGitHub.Tag(workflow, release), FakeGitHub.Folder(workflow, release), Signers.Sign(Files(workflow, release, limit), key, Id(workflow, release)));
}

/// <summary>A payment file of synthetic values only, accepted into the store with its parties, and pinned to a release.</summary>
public static class Seed
{
    public const string Tenant = "example-bank";
    public const string PartyName = "Example Trading LLC";
    public const string CreditorName = "Synthetic Person One";
    public const string Iban = "AE070331234567890123456";
    public const string PersonId = "00001234567890";
    public const string EmployerId = "0000001234567";

    public static async Task<string> Bulk(StoreOptions o, string releaseId, bool hold = true, string tenant = Tenant)
    {
        await using var db = StoreDbContext.Open(o);
        var store = new PaymentStore(db);
        var fileId = $"00000012345672611{Random.Shared.Next(10000000, 99999999)}";
        var bulk = new PaymentBulk
        {
            MessageId = fileId, PaymentInformationId = fileId, Debtor = new Party { Name = PartyName, IsOrganisation = true },
            DebtorAccount = new Account { Iban = "AE070331234567890123999" }, DebtorAgent = new Agent { MemberId = "302620122" },
            SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = new() { ["employerId"] = EmployerId, ["salaryMonth"] = "102026" } },
            Transactions =
            [
                new CreditTransfer
                {
                    PaymentId = new PaymentIdentification { EndToEndId = $"{fileId}-1" }, InstructedAmount = Money.Aed(1500m), Creditor = new Party { Name = CreditorName },
                    CreditorAccount = new Account { Iban = Iban }, CreditorAgent = new Agent { MemberId = "203320101" },
                    SupplementaryData = new Dictionary<string, JsonObject> { ["uae.wps"] = new() { ["personId"] = PersonId, ["fixedPay"] = 1200m } },
                },
            ],
        };
        var accepted = await store.AcceptBulk(tenant, "uae.wps", Direction.Outward, bulk, releaseId);
        var log = new FactLog(db);
        var header = new JsonObject { ["scheme"] = "uae.wps", ["direction"] = "outward", ["kind"] = "payment", ["fileId"] = fileId };
        await log.Append(accepted.BulkId, "bulk", new NewFact("step", "pass", new JsonObject { ["instance"] = "root", ["step"] = "validate", ["activity"] = "p.validate.canonical.v1", ["outcome"] = "pass", ["header"] = header.DeepClone() }, Actor: "engine"));
        await log.Append(accepted.BulkId, "bulk", new NewFact("step", "reject", new JsonObject
        {
            ["instance"] = "root/hook2", ["step"] = "internal-limit", ["activity"] = "b.validate.internal-limit.v1", ["outcome"] = "reject", ["reasonCode"] = "AM02",
            ["message"] = $"Amount above the limit for {PartyName} on {Iban}", ["header"] = header.DeepClone(),
        }, Actor: "engine"));
        await log.Append(accepted.BulkId, "bulk", new NewFact("step", "failed", new JsonObject { ["instance"] = "root/leg3", ["step"] = "submit.send", ["activity"] = "uae.wps.exchange.send-salary-file.v1", ["outcome"] = "failed", ["error"] = "TransientError", ["header"] = header.DeepClone() }, Actor: "engine"));
        if (hold)
            await log.Append(accepted.BulkId, "bulk", new NewFact("hold", "opened", new JsonObject { ["hold"] = $"hold-{Guid.NewGuid():N}"[..20], ["kind"] = "approval", ["activity"] = "p.hold.approval.v1", ["reason"] = "AM02 at internal-limit", ["expiry"] = "P1D" }, Actor: "runtime"));
        return accepted.BulkId;
    }
}

/// <summary>The service console's API on a fresh database, with the fake GitHub as its package source.</summary>
public sealed class ServiceApi(StoreOptions store, Signers signers, FakeGitHub gh, Dictionary<string, string?>? extra = null, IAssistantModel? model = null, HttpMessageHandler? flowable = null, string environment = "Development", HttpMessageHandler? logs = null)
    : WebApplicationFactory<ServiceHost>
{
    public StoreOptions Store => store;

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment(environment);
        var settings = new Dictionary<string, string?>
        {
            ["Store:Provider"] = store.Provider.ToString(), ["Store:Connection"] = store.ConnectionString, ["TH_TRUSTED_SIGNERS"] = $"file:{signers.TrustFile}",
            ["TH_SECRET_FILE_ROOTS"] = signers.Dir, ["TH_AUTH_DEV"] = "1", ["Service:Environment"] = "production",
            ["PackageSource:GitHub:Repository"] = "bank/content", ["PackageSource:GitHub:Api"] = "https://api.github.test/",
            ["Service:Errors"] = Path.Combine(Repo.Root(), "content-configurations", "artefacts"), ["Service:Docs"] = Path.Combine(Repo.Root(), "docs", "north-star"),
            ["Service:Console"] = Path.Combine(Repo.Root(), "runtime", "ops-console", "dist-absent"), ["Service:Health:EverySeconds"] = "3600",
            ["TH_BACKUP_SIGNING_KEY"] = $"file:{signers.BackupKeyFile}", ["Service:Project"] = "true", ["Operations:PollMilliseconds"] = "100",
        };
        foreach (var (k, v) in extra ?? new()) settings[k] = v;
        foreach (var (k, v) in settings) b.UseSetting(k, v);
        b.ConfigureServices(s =>
        {
            s.AddSingleton<IPackageSource>(new GitHubPackageSource(new HttpClient(gh), new Uri("https://api.github.test/"), "bank", "content", "content-configurations", null, SecretResolver.FromEnvironment(_ => null)));
            if (model is not null) s.AddSingleton(new AssistantModel(model));
            if (flowable is not null) s.AddSingleton(new FlowableHandler(flowable));
            if (logs is not null) s.AddSingleton(new LogSourceHandler(logs));
        });
    }

    public HttpClient As(string sub, params string[] roles)
    {
        var c = CreateClient();
        if (roles.Length > 0 || sub.Length > 0)
            c.DefaultRequestHeaders.Add(ServiceAuth.DevHeader, new JsonObject { ["sub"] = sub, ["name"] = sub, ["roles"] = new JsonArray(roles.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString());
        return c;
    }

    public HttpClient Engineer => As("se-1", "*:ServiceEngineer");
    public HttpClient Approver => As("sa-1", "*:ServiceApprover");

    public async Task CatchUp()
    {
        await Services.GetRequiredService<Projector>().CatchUp();
    }
}

public static class J
{
    public static async Task<JsonNode> Of(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync()) ?? new JsonObject();
}
