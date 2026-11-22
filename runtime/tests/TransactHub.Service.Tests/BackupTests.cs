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
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BackupTests(Databases dbs)
{
    const string Wf = "p.wf.demo.v1";
    const string RoleMap = """{"kind":"runtime-role-map","grants":[{"claim":"groups","value":"service-team","roles":["ServiceEngineer"],"tenants":["*"]}]}""";

    static string Banks(string root, string bindingExtra = "")
    {
        var dir = Path.Combine(root, "banks", "example-bank");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "core-banking.yaml"), $"""
            kind: port-bindings
            tenant: example-bank
            bindings:
              - port: p.port.core-banking.v1
                adapter: gateway-rest
                validFrom: '2026-01-01T00:00:00Z'
                settings:
                  baseUrl: https://core.example.test
                  secretRef: env:TH_EXAMPLE_CORE_TOKEN{bindingExtra}
            """);
        return Path.Combine(root, "banks");
    }

    static Dictionary<string, string?> Settings(Signers s, string banks, string? restoreDir = null) => new()
    {
        ["Service:Banks"] = banks, ["Auth:RoleMap"] = RoleMap, ["Service:RestoreDir"] = restoreDir,
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
        var banks = Banks(s.Dir);
        var source = dbs.Fresh();
        await using var a = new ServiceApi(source, s, gh, Settings(s, banks));
        var se = a.Engineer;
        await se.PostAsJsonAsync("/service/api/content/install", new { tag = FakeGitHub.Tag(Wf, "1.0.0"), reason = "install" });
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
        Assert.Equal(2, doc["values"]!.AsArray().Count);

        var verified = await J.Of(await se.PostAsync("/service/api/backup/verify", new ByteArrayContent(zip)));
        Assert.True((bool)verified["verified"]!);

        // The target: an empty runtime with the same package source and trust.
        var restoreDir = Path.Combine(s.Dir, "restored");
        await using var b = new ServiceApi(dbs.Fresh(), s, gh, Settings(s, Banks(Path.Combine(s.Dir, "b")), restoreDir));
        var se2 = b.As("se-9", "*:ServiceEngineer");
        var dry = await J.Of(await se2.PostAsync("/service/api/restore?dryRun=true&reason=DR%20test", new ByteArrayContent(zip)));
        Assert.True((bool)dry["dryRun"]!);
        Assert.Empty(dry["problems"]!.AsArray());
        Assert.Equal("install", (string?)dry["releases"]![0]!["action"]);
        Assert.Equal(1, (int)dry["activations"]!);
        await using (var db = StoreDbContext.Open(b.Store)) Assert.False(db.Releases.Any());

        var done = await se2.PostAsync("/service/api/restore?dryRun=false&reason=DR%20test", new ByteArrayContent(zip));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.True((bool)(await J.Of(done))["restored"]!);
        Assert.Contains("secretRef: 'env:TH_EXAMPLE_CORE_TOKEN'", File.ReadAllText(Path.Combine(restoreDir, "banks", "example-bank", "port-bindings.yaml")));
        Assert.Equal(RoleMap, File.ReadAllText(Path.Combine(restoreDir, "role-map.json")));

        // Round trip: the target's own backup holds the same configuration.
        var again = Doc(await (await se2.PostAsJsonAsync("/service/api/backup", new { reason = "after restore" })).Content.ReadAsByteArrayAsync());
        var (x, y) = (Comparable(doc), Comparable(again));
        x.Remove("bindings"); y.Remove("bindings");
        Assert.Equal(x.ToJsonString(), y.ToJsonString());

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
        await using var a = new ServiceApi(dbs.Fresh(), s, gh, Settings(s, Banks(s.Dir)));
        var se = a.Engineer;
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

        await using var bad = new ServiceApi(dbs.Fresh(), s, gh, Settings(s, Banks(Path.Combine(s.Dir, "bad"), "\n      password: synthetic-literal-value")));
        var e = await bad.Engineer.PostAsJsonAsync("/service/api/backup", new { reason = "nightly" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, e.StatusCode);
        var text = await e.Content.ReadAsStringAsync();
        Assert.Contains("password", text);
        Assert.DoesNotContain("synthetic-literal-value", text);
    }
}
