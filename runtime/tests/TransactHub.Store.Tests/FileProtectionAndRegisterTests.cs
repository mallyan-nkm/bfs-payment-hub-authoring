using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Native;
using TransactHub.Activities.Wps.Native;
using TransactHub.Ports;
using TransactHub.Security;
using Xunit;

namespace TransactHub.Store.Tests;

// Issue I-117: the gnupg decryption adapter (the source's key folder, BouncyCastle) and the native code-behinds of the encrypt and
// decrypt activities. Issue I-118: the labour ministry's establishment register on the runtime. Every key is made when the test
// runs; the employer ids and files are synthetic.

public sealed class FileProtectionAndRegisterTests
{
    static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-11-22T00:00:00Z");
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-24T06:00:00Z");
    static readonly byte[] Sif = Encoding.UTF8.GetBytes("SCR,0000001234567,302620122,2026-11-17,0830,102026,1,1500.00,AED,REF-0001\n");

    sealed class MemoryFiles : INativeFiles
    {
        public readonly ConcurrentDictionary<string, (string Direction, string Kind, string Name, byte[] Content)> Kept = new(StringComparer.OrdinalIgnoreCase);
        public string Keep(string direction, string kind, string fileName, byte[] content)
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(content));
            Kept[sha] = (direction, kind, fileName, content);
            return sha;
        }
        public byte[]? Get(string sha256) => Kept.TryGetValue(sha256, out var k) ? k.Content : null;
    }

    sealed record Keys(byte[] Public, byte[] Secret, string Passphrase);

    static Keys NewKeys()
    {
        var pass = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var (pub, sec) = OpenPgp.GenerateKeyPair("Test bank WPS inbound (test)", pass.ToCharArray());
        return new Keys(pub, sec, pass);
    }

    [Fact]
    public async Task The_gnupg_adapter_reads_the_key_folder_by_reference_and_decrypts_with_its_secret_key()
    {
        var keys = NewKeys();
        var folder = Directory.CreateTempSubdirectory("th-gpg-");
        try
        {
            // The folder holds the bank's secret key, a public key and a file that is no key: only the secret key is used.
            await File.WriteAllBytesAsync(Path.Combine(folder.FullName, "inbound.asc"), keys.Secret);
            await File.WriteAllBytesAsync(Path.Combine(folder.FullName, "partner.asc"), NewKeys().Public);
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "README.key"), "not a key");
            var env = new Dictionary<string, string> { ["TH_TEST_GPG_KEYS_PATH"] = folder.FullName, ["TH_TEST_GPG_PASSPHRASE"] = keys.Passphrase };
            var secrets = new SecretResolver([new EnvSecretProvider(k => env.GetValueOrDefault(k))]);
            var dec = FileProtectionAdapters.Decryption(new PortBinding("test-bank", GnuPgDecryption.Port, GnuPgDecryption.Name, From, new Dictionary<string, string>
            {
                ["profile"] = "WPS_SalaryUpload", ["key"] = "TestWpsInbound", ["keyFolderRef"] = "env:TH_TEST_GPG_KEYS_PATH", ["secretRef"] = "env:TH_TEST_GPG_PASSPHRASE",
            }), secrets);
            var cipher = OpenPgp.Encrypt(Sif, OpenPgp.EncryptionKeyOf(keys.Public), "salary.sif", armor: false);
            Assert.Equal(Sif, await dec.Decrypt(cipher));
            // A file encrypted to another key is refused, and so is a folder with no secret key.
            var other = OpenPgp.Encrypt(Sif, OpenPgp.EncryptionKeyOf(NewKeys().Public), "salary.sif", armor: true);
            Assert.Contains("no key of this binding", (await Assert.ThrowsAsync<FileProtectionException>(() => dec.Decrypt(other))).Message);
            File.Delete(Path.Combine(folder.FullName, "inbound.asc"));
            var e = await Assert.ThrowsAsync<FileProtectionException>(() => dec.Decrypt(cipher));
            Assert.Contains("holds no secret key", e.Message);
            Assert.DoesNotContain(folder.FullName, e.Message);
            // The folder is a reference, never a path in the bank's configuration.
            var literal = new PortBinding("test-bank", GnuPgDecryption.Port, GnuPgDecryption.Name, From, new Dictionary<string, string> { ["keyFolderRef"] = folder.FullName, ["secretRef"] = "env:X" });
            await Assert.ThrowsAsync<PortBindingException>(() => FileProtectionAdapters.Decryption(literal, secrets).Decrypt(cipher));
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public void The_banks_gnupg_binding_builds_the_adapter()
    {
        var banks = CoreBankingResolverBanks();
        var b = banks.InForce("dib", GnuPgDecryption.Port, Now);
        Assert.IsType<GnuPgDecryption>(FileProtectionAdapters.Decryption(b));
    }

    static PortBindings CoreBankingResolverBanks()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content-configurations", "banks")))
                return TransactHub.Activities.Platform.Posting.CoreBankingResolver.ReadBanks(Path.Combine(d.FullName, "content-configurations", "banks"));
        throw new DirectoryNotFoundException("content-configurations");
    }

    static (EncryptFile Enc, DecryptFile Dec, MemoryFiles Files, Keys Keys) Native()
    {
        var keys = NewKeys();
        var env = new Dictionary<string, string>
        {
            ["TH_TEST_CB_WPS_PUBLIC_KEY"] = Encoding.UTF8.GetString(keys.Public), ["TH_TEST_SECRET_KEY"] = Encoding.UTF8.GetString(keys.Secret), ["TH_TEST_PASSPHRASE"] = keys.Passphrase,
        };
        var secrets = new SecretResolver([new EnvSecretProvider(k => env.GetValueOrDefault(k))]);
        var bindings = new PortBindings();
        bindings.Add("""
            kind: port-bindings
            tenant: test-bank
            bindings:
              - port: p.port.encryption.v1
                adapter: openpgp
                validFrom: 2026-11-22T00:00:00Z
                settings:
                  keys.cbuae-wps.secretRef: env:TH_TEST_CB_WPS_PUBLIC_KEY
              - port: p.port.decryption.v1
                adapter: openpgp
                validFrom: 2026-11-22T00:00:00Z
                settings:
                  privateKeyRef: env:TH_TEST_SECRET_KEY
                  secretRef: env:TH_TEST_PASSPHRASE
            """);
        var files = new MemoryFiles();
        // The platform module's activities, on the image's ports as the native host gives them (native contract 1.1.0, R-09-125).
        var ports = new NativePorts(bindings, files, secrets: secrets);
        return (new EncryptFile(files, ports), new DecryptFile(files, ports), files, keys);
    }

    [Fact]
    public void The_encrypt_activity_encrypts_the_rendered_file_keeps_what_is_sent_and_refuses_a_key_its_binding_lacks()
    {
        var (enc, _, files, keys) = Native();
        var sha = files.Keep("out", "rendered", "0000001234567261117083000.SIF", Sif);
        var view = new JsonObject { ["messages"] = new JsonObject { ["rendered"] = new JsonObject { ["fileName"] = "0000001234567261117083000.SIF", ["sha256"] = sha.ToUpperInvariant() } } };
        var r = enc.Run(new NativeAttempt(enc.Name, "1.1.0", view, new JsonObject { ["recipientKey"] = "cbuae-wps" }, Now, "test-bank"));
        Assert.Equal("encrypted", r.Outcome);
        var fact = r.Writes["facts.encryption"]!;
        Assert.Equal("0000001234567261117083000.SIF.gpg", (string?)fact["fileName"]);
        var sent = files.Kept[(string)fact["sha256"]!];
        Assert.Equal(("out", "encrypted"), (sent.Direction, sent.Kind));
        Assert.True(OpenPgp.IsEncrypted(sent.Content));
        Assert.Equal(Sif, OpenPgp.Decrypt(sent.Content, OpenPgp.SecretKeysOf(keys.Secret), keys.Passphrase.ToCharArray()));
        Assert.DoesNotContain("EDR", fact.ToJsonString());

        var refused = enc.Run(new NativeAttempt(enc.Name, "1.1.0", view, new JsonObject { ["recipientKey"] = "someone-else" }, Now, "test-bank"));
        Assert.Equal("refused", refused.Outcome);
        Assert.Equal("refused", enc.Run(new NativeAttempt(enc.Name, "1.1.0", new JsonObject(), new JsonObject { ["recipientKey"] = "cbuae-wps" }, Now, "test-bank")).Outcome);
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => enc.Run(new NativeAttempt(enc.Name, "1.1.0", view, new JsonObject { ["recipientKey"] = "cbuae-wps" }, Now, "other-bank"))).Name);
    }

    [Fact]
    public void The_decrypt_activity_gives_the_reader_the_file_as_it_was_and_keeps_both()
    {
        var (_, dec, files, keys) = Native();
        var cipher = OpenPgp.Encrypt(Sif, OpenPgp.EncryptionKeyOf(keys.Public), "salary.SIF", armor: false);
        var view = new JsonObject { ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["type"] = "uae.wps.sif", ["fileName"] = "salary.SIF.gpg", ["contentBase64"] = Convert.ToBase64String(cipher) } } };
        var r = dec.Run(new NativeAttempt(dec.Name, "1.0.0", view, new JsonObject(), Now, "test-bank"));
        Assert.Equal("decrypted", r.Outcome);
        Assert.Equal(Encoding.UTF8.GetString(Sif), (string?)r.Writes["messages.inbound"]!["payload"]);
        Assert.Equal("salary.SIF", (string?)r.Writes["messages.inbound"]!["fileName"]);
        Assert.Null(r.Writes["messages.inbound"]!["contentBase64"]);
        Assert.Contains(files.Kept.Values, k => k.Direction == "in" && k.Kind == "received" && k.Content.SequenceEqual(cipher));
        Assert.Contains(files.Kept.Values, k => k.Direction == "in" && k.Kind == "decrypted" && k.Content.SequenceEqual(Sif));
        var plain = new JsonObject { ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["payload"] = "SCR,plain" } } };
        Assert.Equal("refused", dec.Run(new NativeAttempt(dec.Name, "1.0.0", plain, new JsonObject(), Now, "test-bank")).Outcome);
    }

    sealed class Recording(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r, r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct)));
            return answer(r);
        }
    }

    static NativeAttempt EmployerAttempt(string employer, string tenant = "test-bank", DateTimeOffset? at = null) => new("uae.wps.enrich.employer-status.v1", "1.1.0",
        new JsonObject { ["ext"] = new JsonObject { ["uae"] = new JsonObject { ["wps"] = new JsonObject { ["employerId"] = employer } } } }, new JsonObject(), at ?? Now, tenant, "batch");

    static PortBindings GatewayRegister(string extra = "") => Bound($$"""
        kind: port-bindings
        tenant: test-bank
        bindings:
          - port: uae.wps.port.labour-ministry.v1
            adapter: gateway-rest
            validFrom: 2026-11-22T00:00:00Z
            settings:
              baseUrl: https://labour-register.test.invalid/
              statusPath: /api/establishments/status
              secretRef: env:TH_TEST_LM_TOKEN
              credentialHeader: X-Api-Key
              channel: BANK-CHANNEL
        {{extra}}
        """);

    static PortBindings Bound(string yaml)
    {
        var b = new PortBindings();
        b.Add(yaml);
        return b;
    }

    [Fact]
    public void The_WPS_modules_register_adapter_calls_its_gateway_through_the_images_bound_port_with_the_credential_by_reference()
    {
        // R-09-261, R-09-263 (Issues I-237, I-315): the labour ministry's adapters are the WPS module's; the image gives the binding in
        // force and sends the module's request to the binding's endpoint with its credential, breaker and timeout. The module sees no secret.
        var handler = new Recording(r => r.Headers.GetValues("X-Ref").Single() switch
        {
            "0000001234567" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "status": "SUSPENDED" }""", Encoding.UTF8, "application/json") },
            "0000001234568" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "status": "Active" }""", Encoding.UTF8, "application/json") },
            "0000001234569" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var secrets = new SecretResolver([new EnvSecretProvider(k => k == "TH_TEST_LM_TOKEN" ? "token-for-test" : null)]);
        var ports = new NativeBoundPorts(GatewayRegister(), secrets, _ => new HttpClient(handler), new CircuitBreakers());
        var a = EmployerAttempt("0000001234567");
        var binding = ports.For(a, EmployerRegisterAdapters.Port);
        Assert.Equal(("gateway-rest", "env:TH_TEST_LM_TOKEN"), (binding.Adapter, binding.Settings["secretRef"]));
        var reg = EmployerRegisterAdapters.Create(binding, ports, a);
        Assert.IsType<RestEmployerRegister>(reg);
        Assert.Equal(new EmployerStatusAnswer("0000001234567", true, "suspended"), reg.Status("0000001234567"));
        Assert.Equal(new EmployerStatusAnswer("0000001234568", true, "active"), reg.Status("0000001234568"));
        Assert.Equal(new EmployerStatusAnswer("0000009999999", false, "unknown"), reg.Status("0000009999999"));
        var call = handler.Calls[0];
        Assert.Equal(HttpMethod.Post, call.Request.Method);
        Assert.Equal("https://labour-register.test.invalid/api/establishments/status", call.Request.RequestUri!.ToString());
        Assert.Equal("""{"employerId":"0000001234567"}""", call.Body);
        Assert.Equal("token-for-test", call.Request.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("BANK-CHANNEL", call.Request.Headers.GetValues("X-Channel").Single());
        Assert.Null(call.Request.Headers.Authorization);

        // The activity on the module's adapter: the status written, an unknown employer not-found, a 5xx a retryable error.
        var act = new EnrichEmployerStatus(ports);
        Assert.Equal(("found", "suspended"), (act.Run(a).Outcome, (string?)act.Run(a).Writes["ext.uae.wps.employerStatus"]));
        Assert.Equal("not-found", act.Run(EmployerAttempt("0000009999999")).Outcome);
        var down = Assert.Throws<NativeFailure>(() => act.Run(EmployerAttempt("0000001234569")));
        Assert.Equal(("TransientError", true), (down.Name, down.Retryable));
        // A tenant with no binding is refused by name; a credential the host cannot read is SecretUnavailable, nothing sent.
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => act.Run(EmployerAttempt("0000001234567", "other-bank"))).Name);
        var calls = handler.Calls.Count;
        var noSecret = new NativeBoundPorts(GatewayRegister(), new SecretResolver([new EnvSecretProvider(_ => null)]), _ => new HttpClient(handler), new CircuitBreakers());
        Assert.Equal("SecretUnavailable", Assert.Throws<NativeFailure>(() => new EnrichEmployerStatus(noSecret).Run(a)).Name);
        Assert.Equal(calls, handler.Calls.Count);
    }

    [Fact]
    public void A_module_sends_only_on_the_binding_in_force_below_its_base_url_and_never_sets_the_credential()
    {
        var handler = new Recording(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var secrets = new SecretResolver([new EnvSecretProvider(k => k == "TH_TEST_LM_TOKEN" ? "token-for-test" : null)]);
        var ports = new NativeBoundPorts(GatewayRegister(), secrets, _ => new HttpClient(handler), new CircuitBreakers());
        var a = EmployerAttempt("0000001234567");
        var b = ports.For(a, EmployerRegisterAdapters.Port);
        // A binding a module made up, with a host of its own: refused, nothing sent, the credential never leaves for it.
        var forged = b with { ValidFrom = b.ValidFrom.AddDays(-1), Settings = new Dictionary<string, string>(b.Settings) { ["baseUrl"] = "https://elsewhere.test.invalid/" } };
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => ports.Send(a, forged, new NativeHttpRequest("POST", "/x"))).Name);
        // The settings a module gives are never used: the image's own copy of the binding in force is.
        var moved = b with { Settings = new Dictionary<string, string>(b.Settings) { ["baseUrl"] = "https://elsewhere.test.invalid/" } };
        Assert.Equal(200, ports.Send(a, moved, new NativeHttpRequest("GET", "/ping")).Status);
        Assert.Equal("labour-register.test.invalid", handler.Calls.Single().Request.RequestUri!.Host);
        // A path that names a host, or the credential's header, is refused.
        Assert.Throws<NativePortRefused>(() => ports.Send(a, b, new NativeHttpRequest("GET", "https://elsewhere.test.invalid/x")));
        Assert.Throws<NativePortRefused>(() => ports.Send(a, b, new NativeHttpRequest("GET", "//elsewhere.test.invalid/x")));
        Assert.Throws<NativePortRefused>(() => ports.Send(a, b, new NativeHttpRequest("GET", "/x", Headers: new Dictionary<string, string> { ["X-Api-Key"] = "mine" })));
        Assert.Throws<NativePortRefused>(() => ports.Send(a, b, new NativeHttpRequest("GET", "/x", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer mine" })));
        Assert.Single(handler.Calls);
        // No tenant, no binding.
        Assert.Equal("NoTenant", Assert.Throws<NativeFailure>(() => ports.For(a with { Tenant = null }, EmployerRegisterAdapters.Port)).Name);
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => ports.For(a, "p.port.no-such.v1")).Name);
    }

    [Fact]
    public void An_open_breaker_on_the_register_is_the_retryable_breaker_code_and_sends_nothing()
    {
        var handler = new Recording(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var secrets = new SecretResolver([new EnvSecretProvider(k => k == "TH_TEST_LM_TOKEN" ? "token-for-test" : null)]);
        var ports = new NativeBoundPorts(GatewayRegister(), secrets, _ => new HttpClient(handler), new CircuitBreakers());
        var act = new EnrichEmployerStatus(ports);
        string? last = null;
        for (var i = 0; i < 20 && last != CircuitBreakers.OpenCode; i++) last = Assert.Throws<NativeFailure>(() => act.Run(EmployerAttempt("0000001234567"))).Name;
        Assert.Equal(CircuitBreakers.OpenCode, last);
        var sent = handler.Calls.Count;
        var e = Assert.Throws<NativeFailure>(() => act.Run(EmployerAttempt("0000001234567")));
        Assert.Equal((CircuitBreakers.OpenCode, true), (e.Name, e.Retryable));
        Assert.Equal(sent, handler.Calls.Count);
    }

    [Fact]
    public void The_employer_status_activity_writes_the_status_the_banks_register_gives()
    {
        var bindings = Bound("""
            kind: port-bindings
            tenant: test-bank
            bindings:
              - port: uae.wps.port.labour-ministry.v1
                adapter: simulator
                validFrom: 2026-11-22T00:00:00Z
                settings:
                  suspended: "0000001234567"
                  notRegistered: "0000009999999"
            """);
        // The WPS module's simulator adapter, on the binding the image's bound ports give (R-09-263).
        var act = new EnrichEmployerStatus(new NativeBoundPorts(bindings));
        NativeResult Ask(string employer, string tenant = "test-bank") => act.Run(EmployerAttempt(employer, tenant));
        Assert.Equal(("found", "suspended"), (Ask("0000001234567").Outcome, (string?)Ask("0000001234567").Writes["ext.uae.wps.employerStatus"]));
        Assert.Equal(("found", "active"), (Ask("0000001234568").Outcome, (string?)Ask("0000001234568").Writes["ext.uae.wps.employerStatus"]));
        Assert.Equal(("not-found", "unknown"), (Ask("0000009999999").Outcome, (string?)Ask("0000009999999").Writes["ext.uae.wps.employerStatus"]));
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => Ask("0000001234567", "other-bank")).Name);
        // The image answers no scheme's port: the platform's INativePorts refuses the register's status.
        var refused = Assert.Throws<NativeFailure>(() => new NativePorts(bindings, new MemoryNativeFiles()).Ask(EmployerAttempt("0000001234567"), EmployerRegisterAdapters.Port, "status", new JsonObject { ["employer"] = "0000001234567" }));
        Assert.Equal("PortNotBound", refused.Name);
    }

    [Fact]
    public void The_banks_that_run_WPS_bind_the_register_to_their_gateway_with_a_placeholder_host_and_a_credential_by_reference()
    {
        var banks = CoreBankingResolverBanks();
        foreach (var bank in new[] { "dib", "adib" })
        {
            var b = banks.InForce(bank, EmployerRegisterAdapters.Port, Now);
            Assert.Equal(RestEmployerRegister.Name, b.Adapter);
            Assert.EndsWith(".invalid/", b.Settings["baseUrl"]);
            Assert.True(SecretRef.IsRef(b.Settings["secretRef"]));
        }
    }
}

// Issue I-97 part 2 (R-09-64): every file a host receives or sends is kept in the store, with its signed hash, on both databases.
[Collection(DatabaseCollection.Name)]
public sealed class StoredFileTests(Databases dbs)
{
    static readonly DateTimeOffset T0 = new(2026, 11, 24, 6, 0, 0, TimeSpan.Zero);

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_kept_file_reads_back_with_its_signed_hash_and_its_bytes_are_kept_once(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var files = new FileStore(db, key, new TestClock(T0));
        var content = Encoding.UTF8.GetBytes("SCR,synthetic\n");
        var sent = await files.Keep(new FileToKeep("example-bank", "uae.wps", "out", "uae.wps.map.sif-out.v1", "0000001234567261124100000.SIF", "bulk-1", "root/leg3", "uae.wps.map.sif-out.v1"), content);
        var again = await files.Keep(new FileToKeep("example-bank", "uae.wps", "out", "uae.wps.map.sif-out.v1", "0000001234567261124100000.SIF", "bulk-1", "root/leg3", "uae.wps.map.sif-out.v1"), content);
        Assert.Equal(sent.Id, again.Id);
        var other = await files.Keep(new FileToKeep("example-bank", "uae.wps", "in", "received", "copy.SIF", "bulk-2"), content);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.StoredFiles.CountAsync());
        Assert.Equal(1, await db.FileContents.CountAsync());
        var (row, bytes) = (await files.Read(sent.Id))!.Value;
        Assert.Equal(content, bytes);
        Assert.Equal((14L, "out", "root/leg3", FileStore.Sha256Hex(content)), (row.Size, row.Direction, row.Leg, row.ContentHash));
        Assert.True(FileSignature.Verify(row, key));
        // A changed field, or another key, does not verify.
        Assert.False(FileSignature.Verify(new StoredFileRow { Id = row.Id, Tenant = row.Tenant, Scheme = row.Scheme, Direction = "in", Kind = row.Kind, FileName = row.FileName, Size = row.Size, ContentHash = row.ContentHash, Signature = row.Signature, SigningKeyId = row.SigningKeyId, SubjectId = row.SubjectId, Leg = row.Leg, At = row.At }, key));
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(FileSignature.Verify(row, otherKey));
        Assert.Equal([other.Id], (await files.Of("example-bank", "bulk-2")).Select(f => f.Id));
        // Insert-only: a kept file is never changed.
        var tracked = await db.StoredFiles.FirstAsync(f => f.Id == sent.Id);
        tracked.FileName = "changed";
        await Assert.ThrowsAsync<InsertOnlyViolation>(() => db.SaveChangesAsync());
        Assert.Contains(await db.Outbox.AsNoTracking().Where(o => o.Topic == FileStore.FileTopic).Select(o => o.Key).ToListAsync(), k => k == sent.Id);
    }
}
