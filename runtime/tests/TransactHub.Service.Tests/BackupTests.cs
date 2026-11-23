using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// The configuration backup (R-09-41): signed and verifiable, holding the releases' ids, the activations, the configuration
/// values, the bindings with secretRef only, the role map and the trusted signers; a dry run and a restore into an empty
/// runtime give the same configuration back; a changed backup, a literal secret and a target that is not empty are refused.
/// The bindings are those of the bank's configuration release in force, from the store, and the backup holds the runtime's own
/// bank only (R-09-112, R-09-116): a laptop's backup restores into its own runtime.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BackupTests(Databases dbs)
{
    const string Wf = "p.wf.demo.v1";
    const string RoleMap = """{"kind":"runtime-role-map","grants":[{"claim":"groups","value":"service-team","roles":["ServiceEngineer"],"tenants":["*"]}]}""";

    /// <summary>The bank's core-banking binding, as a configuration release put in force leaves it in the store (R-09-112).</summary>
    static async Task Bind(StoreOptions o, JsonObject? extra = null, string bank = "example-bank")
    {
        var settings = new JsonObject { ["baseUrl"] = "https://core.example.test", ["secretRef"] = "env:TH_EXAMPLE_CORE_TOKEN" };
        foreach (var (k, v) in extra ?? []) settings[k] = v?.DeepClone();
        await using var db = StoreDbContext.Open(o);
        await new VersionedValues(db).Publish(ConfigurationBindings.Kind, bank, "p.port.core-banking.v1", new JsonObject
        {
            ["binding"] = new JsonObject { ["port"] = "p.port.core-banking.v1", ["adapter"] = "gateway-rest", ["validFrom"] = "2026-01-01", ["settings"] = settings },
            ["configurationRelease"] = "1.0.0",
        }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, "bank.checker");
    }

    static Dictionary<string, string?> Settings(Signers s, string? restoreDir = null, string environment = "production") => new()
    {
        ["Auth:RoleMap"] = RoleMap, ["Service:RestoreDir"] = restoreDir, ["Service:Environment"] = environment,
    };

    static JsonObject Doc(byte[] zip)
    {
        using var a = new ZipArchive(new MemoryStream(zip));
        using var r = new StreamReader(a.GetEntry("backup.json")!.Open());
        return (JsonObject)JsonNode.Parse(r.ReadToEnd())!;
    }

    static JsonObject Comparable(JsonObject d)
    {
        var c = (JsonObject)d.DeepClone();
        c.Remove("createdAt");
        return c;
    }

    [Fact]
    public async Task A_signed_backup_restores_into_an_empty_runtime_with_a_dry_run_first_and_gives_the_same_configuration_back()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        Packages.Publish(gh, s.Key, Wf, "1.0.0");
        var source = dbs.Fresh();
        await using var a = new ServiceApi(source, s, gh, Settings(s));
        var se = a.Engineer;
        await a.Consented(se, "/service/api/content/install", new { tag = FakeGitHub.Tag(Wf, "1.0.0"), reason = "install" });
        await Bind(source);
        await using (var db = StoreDbContext.Open(source))
        {
            await new ReleaseRegistry(db).Activate("production", $"{Wf}/temporal", "1.0.0", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "pipeline");
            var vv = new VersionedValues(db);
            await vv.Publish("config", "example-bank", "p.validate.limits.v1#maxAmount", new JsonObject { ["value"] = 250000, ["configurationSet"] = "1.0.0" }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, "bank.checker");
            await vv.Publish("config", "example-bank", "p.validate.limits.v1#maxAmount", new JsonObject { ["value"] = 300000, ["configurationSet"] = "1.1.0" }, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), null, "bank.checker");
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await se.PostAsJsonAsync("/service/api/backup", new { })).StatusCode);
        var export = await se.PostAsJsonAsync("/service/api/backup", new { reason = "before upgrade" });
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var zip = await export.Content.ReadAsByteArrayAsync();
        var doc = Doc(zip);
        Assert.Equal(Packages.Id(Wf, "1.0.0"), (string?)doc["releases"]![0]!["id"]);
        Assert.Equal("env:TH_EXAMPLE_CORE_TOKEN", (string?)doc["bindings"]![0]!["settings"]!["secretRef"]);
        Assert.Equal("runtime-role-map", (string?)doc["roleMap"]!["kind"]);
        Assert.Equal(2, doc["trustedSigners"]!["keys"]!.AsArray().Count);
        // The two values and the binding, each an effective-dated version of the store.
        Assert.Equal(3, doc["values"]!.AsArray().Count);

        var verified = await J.Of(await se.PostAsync("/service/api/backup/verify", new ByteArrayContent(zip)));
        Assert.True((bool)verified["verified"]!);

        // The target: an empty runtime with the same package source and trust.
        var restoreDir = Path.Combine(s.Dir, "restored");
        await using var b = new ServiceApi(dbs.Fresh(), s, gh, Settings(s, restoreDir));
        var se2 = b.As("se-9", "*:ServiceEngineer");
        var dry = await J.Of(await se2.PostAsync("/service/api/restore?dryRun=true&reason=DR%20test", new ByteArrayContent(zip)));
        Assert.True((bool)dry["dryRun"]!);
        Assert.Empty(dry["problems"]!.AsArray());
        Assert.Equal("install", (string?)dry["releases"]![0]!["action"]);
        Assert.Equal(1, (int)dry["activations"]!);
        await using (var db = StoreDbContext.Open(b.Store)) Assert.False(db.Releases.Any());

        // A real restore waits for a bank approver's consent like every change of the system (R-09-77, Issue I-122).
        var asked = await se2.PostAsync("/service/api/restore?dryRun=false&reason=DR%20test", new ByteArrayContent(zip));
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        await using (var db = StoreDbContext.Open(b.Store)) Assert.False(db.Releases.Any());
        var done = await b.Consented(se2, "/service/api/restore?dryRun=false&reason=DR%20test", content: new ByteArrayContent(zip));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.True((bool)(await J.Of(done))["restored"]!);
        // The binding is a value of the store again: in force in the target, read as every host reads it; no folder is written.
        Assert.Equal("env:TH_EXAMPLE_CORE_TOKEN", Assert.Single(ConfigurationBindings.Load(b.Store, "example-bank")).Settings["secretRef"]);
        Assert.False(Directory.Exists(Path.Combine(restoreDir, "banks")));
        Assert.Equal(RoleMap, File.ReadAllText(Path.Combine(restoreDir, "role-map.json")));

        // Round trip: the target's own backup holds the same configuration, its bindings too.
        var again = Doc(await (await se2.PostAsJsonAsync("/service/api/backup", new { reason = "after restore" })).Content.ReadAsByteArrayAsync());
        Assert.Equal(Comparable(doc).ToJsonString(), Comparable(again).ToJsonString());

        // The source is not empty and not a test environment: a restore into it is refused.
        var refused = await se.PostAsync("/service/api/restore?dryRun=false&reason=oops", new ByteArrayContent(zip));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("service.RestoreRefused", (string?)(await J.Of(refused))["code"]);
    }

    [Fact]
    public async Task A_changed_backup_and_a_secret_written_as_a_value_are_refused()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        var o = dbs.Fresh();
        await using var a = new ServiceApi(o, s, gh, Settings(s));
        var se = a.Engineer;
        await Bind(o);
        var zip = await (await se.PostAsJsonAsync("/service/api/backup", new { reason = "nightly" })).Content.ReadAsByteArrayAsync();
        var ms = new MemoryStream();
        ms.Write(zip);
        using (var z = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            z.GetEntry("backup.json")!.Delete();
            using var w = new StreamWriter(z.CreateEntry("backup.json").Open(), Encoding.UTF8);
            w.Write(Doc(zip).ToJsonString().Replace("service-team", "everyone"));
        }
        var r = await se.PostAsync("/service/api/backup/verify", new ByteArrayContent(ms.ToArray()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var j = await J.Of(r);
        Assert.Equal("service.BackupInvalid", (string?)j["code"]);
        Assert.Contains("does not match its hash", (string?)j["error"]);

        var badStore = dbs.Fresh();
        await using var bad = new ServiceApi(badStore, s, gh, Settings(s));
        var badEngineer = bad.Engineer;
        await Bind(badStore, new JsonObject { ["password"] = "synthetic-literal-value" });
        var e = await badEngineer.PostAsJsonAsync("/service/api/backup", new { reason = "nightly" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, e.StatusCode);
        var text = await e.Content.ReadAsStringAsync();
        Assert.Contains("password", text);
        Assert.DoesNotContain("synthetic-literal-value", text);
    }

    /// <summary>
    /// A laptop's backup restores into its own runtime (R-09-116): the backup takes only the runtime's bank, never a folder of every
    /// bank's bindings (a service console given one does not start, R-09-113), so nothing in it is another bank's; a row of
    /// another tenant an older runtime left in the store stays out of it.
    /// </summary>
    [Fact]
    public async Task A_laptop_backup_holds_only_its_own_bank_and_restores_into_its_own_runtime()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        Packages.Publish(gh, s.Key, Wf, "1.0.0");
        // A service console given the laptop's folder of every bank's bindings does not start.
        await using (var folder = new ServiceApi(dbs.Fresh(), s, gh, new() { ["Service:Banks"] = Path.Combine(Repo.Root(), "content-configurations", "banks"), ["Auth:RoleMap"] = RoleMap }))
            Assert.Contains("reads its bank's bindings only from the configuration release in force", Assert.ThrowsAny<Exception>(() => folder.CreateClient()).ToString());

        var o = dbs.Fresh();
        await using var laptop = new ServiceApi(o, s, gh, Settings(s, environment: "test"));
        var se = laptop.Engineer;
        await laptop.Consented(se, "/service/api/content/install", new { tag = FakeGitHub.Tag(Wf, "1.0.0"), reason = "install (synthetic)" });
        await Bind(o);
        await Bind(o, bank: "dib");
        await using (var db = StoreDbContext.Open(o))
            await new VersionedValues(db).Publish("config", "dib", "p.validate.limits.v1#maxAmount", new JsonObject { ["value"] = 1, ["configurationSet"] = "1.0.0" }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, "older runtime");
        var zip = await (await se.PostAsJsonAsync("/service/api/backup", new { reason = "laptop backup (synthetic)" })).Content.ReadAsByteArrayAsync();
        var doc = Doc(zip);
        Assert.All(doc["values"]!.AsArray(), v => Assert.Equal("example-bank", (string?)v!["tenant"]));
        Assert.Equal("example-bank", (string?)Assert.Single(doc["bindings"]!.AsArray())!["tenant"]);

        var dry = await J.Of(await se.PostAsync("/service/api/restore?dryRun=true&reason=laptop%20restore", new ByteArrayContent(zip)));
        Assert.Empty(dry["problems"]!.AsArray());
        Assert.True((bool)dry["target"]!["test"]!);
        var done = await laptop.Consented(se, "/service/api/restore?dryRun=false&reason=laptop%20restore", content: new ByteArrayContent(zip));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.True((bool)(await J.Of(done))["restored"]!);
    }
}
