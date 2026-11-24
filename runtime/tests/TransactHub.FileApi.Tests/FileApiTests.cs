using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using TransactHub.FileApi;
using TransactHub.Ports;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.FileApi.Tests;

/// <summary>PostgreSQL 16 for the run; each test a database of its own.</summary>
public sealed class Store : IAsyncLifetime
{
    readonly PostgreSqlContainer pg = new PostgreSqlBuilder("postgres:16-alpine").WithName($"th-fileapi-pg-{Guid.NewGuid():N}"[..30]).Build();
    public Task InitializeAsync() => pg.StartAsync();
    public Task DisposeAsync() => pg.DisposeAsync().AsTask();

    public async Task<StoreOptions> Fresh()
    {
        var o = new StoreOptions(StoreProvider.PostgreSql, new Npgsql.NpgsqlConnectionStringBuilder(pg.GetConnectionString()) { Database = $"th_{Guid.NewGuid():N}"[..20] }.ConnectionString);
        await using var db = StoreDbContext.Open(o);
        await db.Database.MigrateAsync();
        return o;
    }
}

/// <summary>The intake map, as a worker answers it: mapped, with the file's id from its name and a bulk of the file.</summary>
sealed class FakeIntake : IIntakeRunner
{
    public readonly List<(string Map, JsonObject Message)> Calls = new();
    public string Outcome = "mapped";
    public Task<(string Outcome, JsonObject Writes)> Map(string distribution, string intakeMap, JsonObject context, JsonObject message, DateTimeOffset now, CancellationToken ct = default)
    {
        Calls.Add((intakeMap, message));
        // The ACK's correlation map, as the native host answers it (R-09-212): the WPS module's own code-behind.
        if (intakeMap == "uae.wps.map.ack-correlation.v1")
        {
            var view = new JsonObject { ["messages"] = new JsonObject { ["inbound"] = message.DeepClone() } };
            var r = new TransactHub.Activities.Wps.Native.MapAckCorrelation().Run(new TransactHub.Activities.Native.NativeAttempt(intakeMap, "1.0.0", view, new JsonObject(), now));
            return Task.FromResult((r.Outcome, r.Writes));
        }
        // A test report's map (R-09-210): "<reference>;<amount>" per line, read into report lines.
        if (intakeMap == "t.map.report-in.v1")
        {
            var lines = new JsonArray();
            foreach (var l in ((string)message["payload"]!).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var c = l.Split(';');
                lines.Add(new JsonObject { ["reference"] = c[0], ["amount"] = c.Length > 1 ? c[1] : null, ["currency"] = "AED" });
            }
            return Task.FromResult(("mapped", new JsonObject { ["messages.report"] = new JsonObject { ["lines"] = lines } }));
        }
        var stem = RuntimeFrontDoor.Stem((string)message["fileName"]!);
        return Task.FromResult((Outcome, new JsonObject { ["header"] = new JsonObject { ["fileId"] = stem, ["kind"] = "not-the-route" }, ["batch"] = new JsonObject { ["count"] = 2 } }));
    }
}

/// <summary>The engine host: what it was asked to start and to deliver.</summary>
sealed class FakeEngines : IEngineHost
{
    public readonly List<(string Payment, string Distribution, StartRoute Route, JsonObject Context)> Started = new();
    public readonly List<(string Payment, string Distribution, JsonObject Message)> Delivered = new();
    public Task Start(string paymentId, string distribution, StartRoute route, JsonObject context, CancellationToken ct = default)
    {
        Started.Add((paymentId, distribution, route, context));
        return Task.CompletedTask;
    }
    public Task<string> Deliver(string paymentId, string distribution, JsonObject message, CancellationToken ct = default)
    {
        Delivered.Add((paymentId, distribution, message));
        return Task.FromResult("delivered");
    }
}

public sealed class FileApiTests(Store pg) : IClassFixture<Store>
{
    const string Bank = "test-bank", Env = "test";
    static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>A distribution of the bank holding the content branch's documents the front door reads, stored and active.</summary>
    /// <param name="scheme">A scheme manifest of a later release than the branch's, carried beside it (the repository's next release, or a test's).</param>
    static async Task<string> ActiveDistribution(StoreOptions o, string? scheme = null)
    {
        var art = Path.Combine(SftpTests.Repo(), "content-configurations", "artefacts");
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var dir in new[] { "uae.wps/scheme", "p/p.table.workflow-select.v1", "uae.wps/uae.wps.wf.agent-payment-in.v2", "uae.wps/uae.wps.wf.salary-file-out.v1", "uae.wps/uae.wps.wf.inward-refuse.v1" })
                foreach (var f in Directory.EnumerateFiles(Path.Combine(art, dir), "*.yaml"))
                {
                    var e = z.CreateEntry($"engine/p.wf.inward.v2/{dir}/{Path.GetFileName(f)}");
                    await using var s = e.Open();
                    await s.WriteAsync(await File.ReadAllBytesAsync(f));
                }
            if (scheme is not null)
            {
                await using var s = z.CreateEntry("engine/p.wf.inward.v2/uae.wps/scheme/next.yaml").Open();
                await s.WriteAsync(Encoding.UTF8.GetBytes(scheme));
            }
        }
        var bytes = ms.ToArray();
        await using var db = StoreDbContext.Open(o);
        var reg = new DistributionRegistry(db);
        // The front door caches a distribution's dispatch by its id, as a stored distribution never changes: a test scheme is a distribution of its own.
        var id = scheme is null ? "2026.11.23-1" : $"2026.11.24-{Random.Shared.Next(2, 1_000_000)}";
        await reg.Store(Bank, id, bytes, new JsonObject { ["bank"] = Bank }, FileStore.Sha256Hex(bytes), "test", "test");
        await reg.Activate(DistributionRegistry.IdOf(Bank, id), Env, "test", "test");
        return DistributionRegistry.IdOf(Bank, id);
    }

    static IConfiguration Config(Dictionary<string, string?>? more = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["FileApi:MaxBytes"] = "4096",
        ["FileApi:Messages:uae.wps.pif:Extensions"] = "PIF",
        ["FileApi:Messages:uae.wps.psi:Extensions"] = "PSI",
    }.Concat(more ?? new())).Build();

    sealed record Rig(StoreOptions Store, FileIntake Intake, FakeIntake Map, FakeEngines Engines, string Distribution, PortBindings Bindings);

    /// <summary>The repository's uae.wps scheme manifest (spec/factory), its next release, with what a test adds to it.</summary>
    static string NextScheme(Func<string, string>? change = null)
    {
        var text = File.ReadAllText(Path.Combine(SftpTests.Repo(), "spec", "factory", "uae.wps", "scheme.yaml"));
        return change is null ? text : change(text);
    }

    async Task<Rig> Make(PortBindings? bindings = null, Dictionary<string, string>? secrets = null, Dictionary<string, string?>? config = null, string? scheme = null)
    {
        var o = await pg.Fresh();
        var dist = await ActiveDistribution(o, scheme);
        var map = new FakeIntake();
        var engines = new FakeEngines();
        bindings ??= new PortBindings();
        var resolver = new SecretResolver([new EnvSecretProvider(k => secrets?.GetValueOrDefault(k))]);
        var fd = new RuntimeFrontDoor(o, map, engines);
        return new Rig(o, new FileIntake(o, Key, Config(config), new Decryptor(bindings, resolver), fd), map, engines, dist, bindings);
    }

    static byte[] Pif(string tag) => Encoding.UTF8.GetBytes($"# synthetic payment file {tag}\nSCR,TEST\n");

    [Fact]
    public async Task A_scheme_report_is_reconciled_on_arrival_and_its_breaks_recorded_not_routed()
    {
        // R-09-92: the installation names the report's message (Issue I-164); the report is kept, matched against the store, and
        // each break is a fact for the operations console's queue. Nothing is started and nothing is handed to an engine.
        var r = await Make(config: new() { ["FileApi:Reports:uae.wps.scheme-report:Scheme"] = "uae.wps" });
        var report = Encoding.UTF8.GetBytes("reference,amount,currency,status\n0000NOPAYMENT-1,10.00,AED,settled\n");
        var got = await r.Intake.Receive(A("uae.wps.scheme-report", "report-20261123.csv", report));
        Assert.True(got.Accepted, got.Detail);
        Assert.Contains("reconciled: unmatched, 1 break(s)", got.Detail);
        Assert.Empty(r.Engines.Started);
        await using var db = StoreDbContext.Open(r.Store);
        var fileFacts = (await db.Facts.AsNoTracking().Where(f => f.SubjectId == got.FileId).OrderBy(f => f.Seq).ToListAsync()).Select(f => $"{f.Kind}:{f.Code}").ToList();
        Assert.Equal(["file.received:", "file.reconciled:unmatched"], fileFacts);
        var brk = await db.Facts.AsNoTracking().SingleAsync(f => f.SubjectKind == SchemeReconciliation.SubjectKind && f.Code == "break");
        Assert.Equal("not-in-store", (string?)JsonNode.Parse(brk.DataJson)!["kind"]);
        Assert.Equal(got.FileId, (string?)JsonNode.Parse(brk.DataJson)!["file"]);
        // The same report again is a duplicate, refused like any file.
        Assert.Equal("file.Duplicate", (await r.Intake.Receive(A("uae.wps.scheme-report", "report-20261123-again.csv", report))).Code);
    }
    static Arrival A(string message, string name, byte[] content, Dictionary<string, string>? correlation = null) => new(Bank, Env, message, name, content, "api", "channel-client", correlation);

    [Fact]
    public async Task A_payment_file_is_kept_once_signed_and_starts_its_payment_on_the_active_distribution()
    {
        var r = await Make();
        var got = await r.Intake.Receive(A("uae.wps.pif", "000000000001.PIF", Pif("a")));
        Assert.True(got.Accepted, got.Detail);
        var (payment, dist, route, ctx) = Assert.Single(r.Engines.Started);
        Assert.Equal(r.Distribution, dist);
        Assert.Equal("p.wf.inward.v2", route.Workflow);
        Assert.Equal("uae.wps.wf.agent-payment-in.v2", route.Legs["scheme-leg"]);
        Assert.Equal("uae.wps.wf.inward-refuse.v1", route.Legs["scheme-refuse"]);
        Assert.Equal("uae.wps.map.pif-in.v1", Assert.Single(r.Map.Calls).Map);
        // The dispatch table's route stands after the intake map (R-05-42); the payment's id is the one the file is kept under.
        Assert.Equal("payment", (string?)ctx["header"]!["kind"]);
        Assert.Equal("inward", (string?)ctx["header"]!["direction"]);
        Assert.Equal(payment, (string?)ctx["header"]!["paymentId"]);
        Assert.Equal(2, (int?)ctx["batch"]!["count"]);
        await using var db = StoreDbContext.Open(r.Store);
        var file = Assert.Single(await db.StoredFiles.AsNoTracking().ToListAsync());
        Assert.Equal((payment, "in", "uae.wps.pif", FileStore.Sha256Hex(Pif("a"))), (file.SubjectId, file.Direction, file.Kind, file.ContentHash));
        Assert.True(FileSignature.Verify(file, Key));
        Assert.Equal(dist, await new DistributionRegistry(db).PinOf(payment));
        var facts = await new FactLog(db).Of(file.Id);
        Assert.Equal(["file.received", "file.handed"], facts.Select(f => f.Kind));
        Assert.True((await new FactLog(db).Verify(file.Id)).Ok);
    }

    [Fact]
    public async Task A_supplement_is_correlated_to_the_running_payment_by_its_key_and_by_its_file_name()
    {
        var r = await Make();
        Assert.True((await r.Intake.Receive(A("uae.wps.pif", "000000000002.PIF", Pif("b")))).Accepted);
        var payment = r.Engines.Started.Single().Payment;
        // By the stem of its name, the file it pairs with.
        var psi = await r.Intake.Receive(A("uae.wps.psi", "000000000002.PSI", Encoding.UTF8.GetBytes("# synthetic supplement\n")));
        Assert.True(psi.Accepted, psi.Detail);
        var (to, dist, msg) = Assert.Single(r.Engines.Delivered);
        Assert.Equal((payment, r.Distribution, "uae.wps.psi"), (to, dist, (string?)msg["type"]));
        Assert.Equal($"conv-{payment}", (string?)msg["conversation"]);
        // By a key the sender names (the payment file's id the intake map read).
        var ack = await r.Intake.Receive(A("uae.wps.ack", "ACK-OF-SOMETHING.ACK", Encoding.UTF8.GetBytes("ack\n"), new() { ["fileId"] = "000000000002" }));
        Assert.True(ack.Accepted, ack.Detail);
        Assert.Equal(payment, r.Engines.Delivered[1].Payment);
        // A message for no payment is refused and kept as refused, never handed on.
        var none = await r.Intake.Receive(A("uae.wps.psi", "999999999999.PSI", Encoding.UTF8.GetBytes("# another\n")));
        Assert.Equal(("refused", "frontdoor.Unmatched"), (none.Status, none.Code));
        Assert.Equal(2, r.Engines.Delivered.Count);
        await using var db = StoreDbContext.Open(r.Store);
        var files = await new FileStore(db, Key).Of(Bank, payment);
        Assert.Equal(["uae.wps.pif", "uae.wps.psi", "uae.wps.ack"], files.Select(f => f.Kind));
        Assert.Contains(await db.Facts.AsNoTracking().Where(f => f.SubjectId == payment).Select(f => f.Kind).ToListAsync(), k => k == RuntimeFrontDoor.DeliveredKind);
    }

    [Fact]
    public async Task Duplicates_by_hash_and_by_name_oversized_and_wrong_type_files_are_refused_and_kept_as_refused()
    {
        var r = await Make();
        Assert.True((await r.Intake.Receive(A("uae.wps.pif", "000000000003.PIF", Pif("c")))).Accepted);
        var again = await r.Intake.Receive(A("uae.wps.pif", "000000000003-copy.PIF", Pif("c")));
        Assert.Equal(("refused", "file.Duplicate"), (again.Status, again.Code));
        var name = await r.Intake.Receive(A("uae.wps.pif", "000000000003.PIF", Pif("d")));
        Assert.Equal("file.DuplicateName", name.Code);
        Assert.Equal("file.TooLarge", (await r.Intake.Receive(A("uae.wps.pif", "000000000004.PIF", new byte[5000]))).Code);
        Assert.Equal("file.WrongType", (await r.Intake.Receive(A("uae.wps.pif", "000000000005.TXT", Pif("e")))).Code);
        Assert.Equal("file.Empty", (await r.Intake.Receive(A("uae.wps.pif", "000000000006.PIF", []))).Code);
        Assert.Equal("frontdoor.UnknownMessage", (await r.Intake.Receive(A("uae.wps.nothing", "x.PIF", Pif("f")))).Code);
        Assert.Equal("file.NotReceived", (await r.Intake.Receive(A("uae.wps.prc", "x.PRC", Pif("g")))).Code);
        Assert.Single(r.Engines.Started);
        await using var db = StoreDbContext.Open(r.Store);
        Assert.Equal(7, await db.StoredFiles.CountAsync(f => f.Kind.StartsWith("refused:")));
        Assert.Equal(7, await db.Facts.CountAsync(f => f.Kind == "file.refused"));
    }

    [Fact]
    public async Task A_paused_intake_and_a_map_that_does_not_map_start_nothing()
    {
        var r = await Make();
        await using (var db = StoreDbContext.Open(r.Store)) await IntakeSwitch.Set(db, TimeProvider.System, Bank, "uae.wps", true, "test", "test");
        Assert.Equal("ops.IntakePaused", (await r.Intake.Receive(A("uae.wps.pif", "000000000007.PIF", Pif("h")))).Code);
        await using (var db = StoreDbContext.Open(r.Store)) await IntakeSwitch.Set(db, TimeProvider.System, Bank, "uae.wps", false, "test", "test");
        r.Map.Outcome = "unmappable";
        Assert.Equal("frontdoor.IntakeRefused", (await r.Intake.Receive(A("uae.wps.pif", "000000000008.PIF", Pif("i")))).Code);
        Assert.Empty(r.Engines.Started);
        // The payment was pinned before the map refused it: its pin ends abandoned with the refusal, so it keeps no distribution open
        // (Issue I-206, R-09-164). The paused intake refused before any pin.
        await using (var db = StoreDbContext.Open(r.Store))
        {
            var reg = new DistributionRegistry(db);
            var pins = await db.Facts.AsNoTracking().Where(f => f.Kind == DistributionRegistry.PinKind).Select(f => f.SubjectId).ToListAsync();
            var pinned = Assert.Single(pins);
            var end = await db.Facts.AsNoTracking().SingleAsync(f => f.SubjectId == pinned && f.Kind == DistributionRegistry.EndKind);
            Assert.Equal(DistributionRegistry.Abandoned, end.Code);
            Assert.Equal("frontdoor.IntakeRefused", (string?)JsonNode.Parse(end.DataJson)!["code"]);
            Assert.Contains("answered unmappable", (string?)JsonNode.Parse(end.DataJson)!["reason"]);
            Assert.Equal(0, (await reg.OpenPayments()).GetValueOrDefault(r.Distribution));
            Assert.Empty(await reg.Unstarted(TimeSpan.Zero));
        }
    }

    /// <summary>An engine host that refuses every start, as one with no host for the distribution does.</summary>
    sealed class RefusingEngines : IEngineHost
    {
        public Task Start(string paymentId, string distribution, StartRoute route, JsonObject context, CancellationToken ct = default) =>
            throw new FrontDoorRefused("frontdoor.EngineRefused", "The engine host answered 409: no host serves the distribution (synthetic)");
        public Task<string> Deliver(string paymentId, string distribution, JsonObject message, CancellationToken ct = default) => Task.FromResult("delivered");
    }

    [Fact]
    public async Task A_payment_whose_start_the_engine_host_refuses_ends_abandoned_and_its_distribution_can_be_removed()
    {
        // Issue I-206, R-09-164: pinned, mapped, its keys recorded, and then the engine host refused it. Before, the pin stayed open
        // and its distribution could never be removed after it drained.
        var o = await pg.Fresh();
        var dist = await ActiveDistribution(o);
        var intake = new FileIntake(o, Key, Config(), new Decryptor(new PortBindings(), new SecretResolver([new EnvSecretProvider(_ => null)])), new RuntimeFrontDoor(o, new FakeIntake(), new RefusingEngines()));
        var got = await intake.Receive(A("uae.wps.pif", "000000000021.PIF", Pif("refused-start")));
        Assert.Equal("frontdoor.EngineRefused", got.Code);
        await using var db = StoreDbContext.Open(o);
        var reg = new DistributionRegistry(db);
        var paymentId = await db.Facts.AsNoTracking().Where(f => f.Kind == DistributionRegistry.PinKind).Select(f => f.SubjectId).SingleAsync();
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == paymentId).OrderBy(f => f.Seq).Select(f => f.Kind + ":" + f.Code).ToListAsync();
        Assert.Equal("payment.end:abandoned", facts[^1]);
        Assert.Contains(facts, f => f.StartsWith("frontdoor.started:"));
        Assert.Equal(0, (await reg.OpenPayments()).GetValueOrDefault(dist));
        await reg.Drain(dist, Env, "test", "test");
        await reg.Remove(dist, Env, "test", "test");
        Assert.Equal("removed", await reg.StateIn(dist, Env));
    }

    [Fact]
    public async Task An_encrypted_file_is_decrypted_with_the_banks_key_and_both_are_kept()
    {
        var pass = "synthetic-passphrase".ToCharArray();
        var (pub, sec) = OpenPgp.GenerateKeyPair("test-bank-files", pass);
        var bindings = new PortBindings();
        bindings.Add($"""
            kind: port-bindings
            tenant: {Bank}
            bindings:
              - port: p.port.decryption.v1
                adapter: openpgp
                validFrom: 2026-01-01T00:00:00Z
                settings: {"{"} privateKeyRef: env:BANK_KEY, secretRef: env:BANK_PASS {"}"}
            """);
        var r = await Make(bindings, new() { ["BANK_KEY"] = Encoding.UTF8.GetString(sec), ["BANK_PASS"] = new string(pass) });
        var plain = Pif("secret");
        var cipher = OpenPgp.Encrypt(plain, OpenPgp.EncryptionKeyOf(pub), "000000000009.PIF", armor: false);
        var got = await r.Intake.Receive(A("uae.wps.pif", "000000000009.PIF.gpg", cipher));
        Assert.True(got.Accepted, got.Detail);
        Assert.NotNull(got.DecryptedFileId);
        Assert.Equal("000000000009.PIF", (string?)r.Map.Calls.Single().Message["fileName"]);
        Assert.Equal(Encoding.UTF8.GetString(plain), (string?)r.Map.Calls.Single().Message["payload"]);
        await using var db = StoreDbContext.Open(r.Store);
        var kinds = await db.StoredFiles.AsNoTracking().OrderBy(f => f.At).Select(f => new { f.Kind, f.FileName, f.ContentHash }).ToListAsync();
        Assert.Equal([("uae.wps.pif", "000000000009.PIF.gpg", FileStore.Sha256Hex(cipher)), ("uae.wps.pif:decrypted", "000000000009.PIF", FileStore.Sha256Hex(plain))], kinds.Select(k => (k.Kind, k.FileName, k.ContentHash)));
        // Encrypted to a key the bank does not hold: refused, nothing starts.
        var (other, _) = OpenPgp.GenerateKeyPair("someone-else", pass);
        var wrong = await r.Intake.Receive(A("uae.wps.pif", "000000000010.PIF.gpg", OpenPgp.Encrypt(Pif("x"), OpenPgp.EncryptionKeyOf(other), "000000000010.PIF", false)));
        Assert.Equal("file.NotDecrypted", wrong.Code);
        Assert.Single(r.Engines.Started);
    }

    /// <summary>
    /// R-09-162, R-09-212, Issues I-267 and I-160 (2): a payment's outward file names the payment by its stem, and an answer the partner
    /// names after it, with a file id of its own (an ACK: the SIF's stem and twelve characters), is correlated to the payment by the
    /// scheme's correlation map (uae.wps 1.5.0, <c>uae.wps.map.ack-correlation.v1</c>). A binding's <c>stemDrop</c> is no longer read.
    /// </summary>
    [Fact]
    public async Task An_answer_named_after_the_file_a_payment_sent_reaches_that_payment()
    {
        var r = await Make(scheme: NextScheme());
        Assert.True((await r.Intake.Receive(A("uae.wps.pif", "000000000031.PIF", Pif("ack")))).Accepted);
        var payment = r.Engines.Started.Single().Payment;
        var outbox = new FileOutbox(r.Store, Key, new BoundTransfers(new PortBindings()));
        await outbox.Queue(new Outbound(Bank, "uae.wps", "uae.wps.sif-out", "centralBank/upload/cb/wps/sif", "0000001234567261117083100.SIF", "EDR,synthetic\r\n"u8.ToArray(), payment, "scheme-leg", "uae.wps.exchange.send-salary-file.v1", "native-host"));
        var bindings = new PortBindings();
        bindings.Add($"""
            kind: port-bindings
            tenant: {Bank}
            bindings:
              - port: p.port.file-transfer.v1
                adapter: sftp
                validFrom: 2026-11-23T00:00:00Z
                settings:
                  centralBank.host: sftp
                  centralBank.inbox.ack.folder: upload/cb/wps/ack
                  centralBank.inbox.ack.message: uae.wps.ack
            """);
        var box = Assert.Single(Inbox.Of(bindings.InForce(Bank, "p.port.file-transfer.v1", DateTimeOffset.UtcNow)));
        Assert.Equal("centralBank/upload/cb/wps/ack", box.Folder);
        // Named after the SIF, with the Central Bank's file id: the map reads the SIF's stem from the AHR record, else from the name.
        const string ackName = "0000001234567261117083100S26000000001.ACK";
        var ack = await r.Intake.Receive(new Arrival(Bank, Env, "uae.wps.ack", ackName, "AHR,ACCEPTED,0000001234567261117083100.SIF\nATR,ACCEPTED,2\n"u8.ToArray(), "inbox", "inbox:centralBank/upload/cb/wps/ack"));
        Assert.True(ack.Accepted, ack.Detail);
        Assert.Contains(r.Map.Calls, c => c.Map == "uae.wps.map.ack-correlation.v1");
        var (to, _, msg) = Assert.Single(r.Engines.Delivered);
        Assert.Equal((payment, "uae.wps.ack", ackName), (to, (string?)msg["type"], (string?)msg["fileName"]));
        Assert.StartsWith("AHR,ACCEPTED", (string?)msg["payload"]);
        // An AHR that names no file: the name split from the back still names the SIF.
        var byName = await r.Intake.Receive(new Arrival(Bank, Env, "uae.wps.ack", "0000001234567261117083100S26000000002.NAK", "ATR,REJECTED,1\n"u8.ToArray(), "inbox", "inbox:test"));
        Assert.True(byName.Accepted, byName.Detail);
        Assert.Equal(payment, r.Engines.Delivered[^1].Payment);
        // A name too short to carry the Central Bank's file id, and no AHR: the map reads no key, and the file is refused, not guessed.
        var none = await r.Intake.Receive(new Arrival(Bank, Env, "uae.wps.ack", "SHORT.ACK", "ATR,ACCEPTED,0\n"u8.ToArray(), "inbox", "inbox:test"));
        Assert.Equal("frontdoor.Uncorrelated", none.Code);
        // An answer for a SIF no payment sent names no payment: refused.
        var other = await r.Intake.Receive(new Arrival(Bank, Env, "uae.wps.ack", "0000009999999261117083100S26000000003.ACK", "AHR,ACCEPTED,0000009999999261117083100.SIF\n"u8.ToArray(), "inbox", "inbox:test"));
        Assert.Equal("frontdoor.Unmatched", other.Code);
    }

    /// <summary>
    /// R-09-209, Issue I-159: the File API reads a message's file rules from the scheme in the active distribution (its name pattern,
    /// extensions, largest size, whether a name may come twice, whether it is encrypted); a rule the scheme does not declare is the
    /// installation's. uae.wps declares none (provisional), so a test scheme of its next release declares them for the PIF.
    /// </summary>
    [Fact]
    public async Task A_messages_file_rules_are_the_schemes_in_the_active_distribution()
    {
        string Declare(string block) => NextScheme(t => t.Replace("  - id: uae.wps.pif\n    title: Payment information file, one per agent\n    direction: in\n    confirmed: true\n",
            $"  - id: uae.wps.pif\n    title: Payment information file, one per agent\n    direction: in\n    confirmed: true\n    file: {block}\n"));
        var r = await Make(scheme: Declare("{ namePattern: '[0-9]{12}\\.PIF', extensions: [.PIF], maxBytes: 64, uniqueName: false }"));
        Assert.Equal("file.BadName", (await r.Intake.Receive(A("uae.wps.pif", "12345.PIF", Pif("a")))).Code);
        Assert.Equal("file.TooLarge", (await r.Intake.Receive(A("uae.wps.pif", "000000000041.PIF", Encoding.UTF8.GetBytes(new string('x', 65))))).Code);
        Assert.Equal("file.WrongType", (await r.Intake.Receive(A("uae.wps.pif", "000000000041.TXT", Pif("b")))).Code);
        // Names need not be unique for this message: the same name with other bytes is taken twice.
        Assert.True((await r.Intake.Receive(A("uae.wps.pif", "000000000041.PIF", Pif("c")))).Accepted);
        Assert.True((await r.Intake.Receive(A("uae.wps.pif", "000000000041.PIF", Pif("d")))).Accepted);
        // Encrypted: true refuses a file in the clear before anything else is done with it.
        var enc = await Make(scheme: Declare("{ encrypted: true }"));
        Assert.Equal("file.NotEncrypted", (await enc.Intake.Receive(A("uae.wps.pif", "000000000042.PIF", Pif("e")))).Code);
        Assert.Empty(enc.Engines.Started);
        // The scheme declares no rule here: the installation's (FileApi:Messages:uae.wps.pif:Extensions = PIF, names unique) stand.
        var plain = await Make();
        Assert.True((await plain.Intake.Receive(A("uae.wps.pif", "000000000043.PIF", Pif("f")))).Accepted);
        Assert.Equal("file.DuplicateName", (await plain.Intake.Receive(A("uae.wps.pif", "000000000043.PIF", Pif("g")))).Code);
    }

    /// <summary>
    /// R-09-210, Issue I-164: a message the scheme gives a reconcile disposition is a report: its map reads the scheme's layout into
    /// lines, it is reconciled on arrival, not routed, and a payment of its period it does not name is a break. WPS declares no report
    /// (the Central Bank gives no layout), so a test message of a test scheme release declares one.
    /// </summary>
    [Fact]
    public async Task A_report_the_scheme_declares_is_read_by_its_map_and_reconciled_over_its_period()
    {
        var scheme = NextScheme(t => t.Replace("legs:\n", "  - id: uae.wps.test-report\n    title: A test report\n    direction: in\n    confirmed: false\n    disposition:\n      reconcile: { activity: p.reconcile.scheme-report.v1, map: t.map.report-in.v1, period: P1D }\nlegs:\n"));
        var r = await Make(scheme: scheme);
        var got = await r.Intake.Receive(A("uae.wps.test-report", "report-1.txt", "0000NOPAYMENT-1;10.00\n"u8.ToArray()));
        Assert.True(got.Accepted, got.Detail);
        Assert.Contains("reconciled: unmatched, 1 break(s)", got.Detail);
        Assert.Contains(r.Map.Calls, c => c.Map == "t.map.report-in.v1");
        Assert.Empty(r.Engines.Started);
        Assert.Empty(r.Engines.Delivered);
        await using var db = StoreDbContext.Open(r.Store);
        var run = await db.Facts.AsNoTracking().SingleAsync(f => f.SubjectKind == SchemeReconciliation.SubjectKind && f.Code == "run");
        Assert.Equal(("p.reconcile.scheme-report.v1", "P1D"), ((string?)JsonNode.Parse(run.DataJson)!["activity"], (string?)JsonNode.Parse(run.DataJson)!["period"]));
    }

    [Fact]
    public async Task The_outbox_delivers_on_the_banks_binding_records_each_delivery_and_resends_the_kept_bytes()
    {
        var o = await pg.Fresh();
        var root = Directory.CreateTempSubdirectory("th-fileapi-out-").FullName;
        try
        {
            var bindings = new PortBindings();
            bindings.Add($"kind: port-bindings\ntenant: {Bank}\nbindings:\n  - port: p.port.file-transfer.v1\n    adapter: directory\n    validFrom: 2026-01-01T00:00:00Z\n    settings: {{ rootRef: env:OUT_ROOT }}\n");
            var ports = new BoundTransfers(bindings, new SecretResolver([new EnvSecretProvider(k => k == "OUT_ROOT" ? root : null)]));
            var outbox = new FileOutbox(o, Key, ports);
            var bytes = Encoding.UTF8.GetBytes("synthetic receipt confirmation\n");
            var row = await outbox.Queue(new Outbound(Bank, "uae.wps", "uae.wps.prc", "centralBank/prc", "000000000001.PRC.gpg", bytes, "P-1", "leg-1", "uae.wps.exchange.send-receipt-confirmation.v1", "native-host"));
            Assert.Equal(("out", "P-1", "leg-1"), (row.Direction, row.SubjectId, row.Leg));
            Assert.Equal([row.Id], await outbox.Pending(5));
            var d = await outbox.Deliver(row.Id);
            Assert.Equal(("delivered", "directory", 1), (d.Result, d.Adapter, d.Attempt));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "centralBank", "prc", "000000000001.PRC.gpg")));
            Assert.Empty(await outbox.Pending(5));
            Assert.Equal("delivered before", (await outbox.Deliver(row.Id)).Result);
            // A resend: the same bytes again, once per action.
            File.Delete(Path.Combine(root, "centralBank", "prc", "000000000001.PRC.gpg"));
            Assert.Equal("resent", (await outbox.Resend(row.Id, "operator-1", "action-1")).Result);
            Assert.Equal("already resent by this action", (await outbox.Resend(row.Id, "operator-1", "action-1")).Result);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "centralBank", "prc", "000000000001.PRC.gpg")));
            var history = await outbox.History(row.Id);
            Assert.Equal(["file.queued", "file.delivered", "file.resent", "file.delivered"], history.Select(h => (string)h!["kind"]!));
            // Queued twice for the same payment and leg: one file, one queue.
            Assert.Equal(row.Id, (await outbox.Queue(new Outbound(Bank, "uae.wps", "uae.wps.prc", "centralBank/prc", "000000000001.PRC.gpg", bytes, "P-1", "leg-1", null, "native-host"))).Id);
            // No binding: the delivery fails, as a fact, and stays pending.
            var other = new FileOutbox(o, Key, new BoundTransfers(new PortBindings()));
            var r2 = await other.Queue(new Outbound(Bank, "uae.wps", "uae.wps.dif", "centralBank/dif", "000000000001.DIF", bytes, "P-1", "leg-1", null, "native-host"));
            Assert.Equal("failed", (await other.Deliver(r2.Id)).Result);
            Assert.Equal([r2.Id], await other.Pending(5));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_laptop_inbox_folder_is_polled_and_each_file_taken_once()
    {
        var root = Directory.CreateTempSubdirectory("th-fileapi-in-").FullName;
        try
        {
            var bindings = new PortBindings();
            bindings.Add($"kind: port-bindings\ntenant: {Bank}\nbindings:\n  - port: p.port.file-transfer.v1\n    adapter: directory\n    validFrom: 2026-01-01T00:00:00Z\n    settings: {{ rootRef: env:IN_ROOT, inbox.folder: inbox, inbox.message: uae.wps.pif, inbox.doneFolder: done, inbox.errorFolder: error }}\n");
            var r = await Make(bindings);
            var ports = new BoundTransfers(bindings, new SecretResolver([new EnvSecretProvider(k => k == "IN_ROOT" ? root : null)]));
            var boxes = Inbox.Of(bindings.InForce(Bank, FileTransferAdapters.Port, DateTimeOffset.UtcNow)).ToList();
            var poller = new InboxPoller(ports, r.Intake, Env, () => boxes);
            Directory.CreateDirectory(Path.Combine(root, "inbox"));
            await File.WriteAllBytesAsync(Path.Combine(root, "inbox", "000000000011.PIF"), Pif("in"));
            await File.WriteAllBytesAsync(Path.Combine(root, "inbox", "000000000012.TXT"), Pif("bad"));
            var taken = await poller.PollOnce();
            Assert.Equal(["accepted", "refused"], taken.Select(t => t.Status));
            Assert.True(File.Exists(Path.Combine(root, "inbox", "done", "000000000011.PIF")));
            Assert.True(File.Exists(Path.Combine(root, "inbox", "error", "000000000012.TXT")));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "inbox")));
            Assert.Empty(await poller.PollOnce());
            Assert.Single(r.Engines.Started);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>R-09-104: one runtime serves one bank; its poller never reads another bank's inbox, from a binding or a setting.</summary>
    [Fact]
    public async Task The_inbox_poller_reads_the_inboxes_of_the_runtimes_bank_only()
    {
        var root = Directory.CreateTempSubdirectory("th-fileapi-bank-").FullName;
        try
        {
            const string other = "other-bank";
            var bindings = new PortBindings();
            foreach (var t in new[] { Bank, other })
                bindings.Add($"kind: port-bindings\ntenant: {t}\nbindings:\n  - port: p.port.file-transfer.v1\n    adapter: directory\n    validFrom: 2026-01-01T00:00:00Z\n    settings: {{ rootRef: env:IN_ROOT, inbox.folder: {t}/inbox, inbox.message: uae.wps.pif, inbox.doneFolder: done }}\n");
            var r = await Make(bindings);
            var ports = new BoundTransfers(bindings, new SecretResolver([new EnvSecretProvider(k => k == "IN_ROOT" ? root : null)]));
            var boxes = new[] { Bank, other }.SelectMany(t => Inbox.Of(bindings.InForce(t, FileTransferAdapters.Port, DateTimeOffset.UtcNow)))
                .Append(new Inbox(other, $"{other}/extra", "uae.wps.pif", null, null, TimeSpan.Zero)).ToList();
            Assert.Equal(3, boxes.Count);
            foreach (var f in new[] { $"{Bank}/inbox", $"{other}/inbox", $"{other}/extra" }) Directory.CreateDirectory(Path.Combine(root, f));
            await File.WriteAllBytesAsync(Path.Combine(root, Bank, "inbox", "000000000021.PIF"), Pif("mine"));
            await File.WriteAllBytesAsync(Path.Combine(root, other, "inbox", "000000000022.PIF"), Pif("theirs"));
            await File.WriteAllBytesAsync(Path.Combine(root, other, "extra", "000000000023.PIF"), Pif("theirs too"));
            var poller = new InboxPoller(ports, r.Intake, Env, () => boxes, bank: Bank);
            Assert.Equal([Bank], poller.Inboxes().Select(b => b.Tenant).Distinct());
            var taken = await poller.PollOnce();
            Assert.Equal(["accepted"], taken.Select(t => t.Status));
            Assert.Single(r.Engines.Started);
            // The other bank's files are where they were: neither taken nor moved.
            Assert.True(File.Exists(Path.Combine(root, other, "inbox", "000000000022.PIF")));
            Assert.True(File.Exists(Path.Combine(root, other, "extra", "000000000023.PIF")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task The_HTTP_upload_takes_a_system_clients_file_and_refuses_a_caller_without_the_role()
    {
        var o = await pg.Fresh();
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        var jwks = new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA", ["kid"] = "t1", ["use"] = "sig", ["alg"] = "RS256", ["n"] = Base64UrlEncoder.Encode(p.Modulus), ["e"] = Base64UrlEncoder.Encode(p.Exponent) }) }.ToJsonString();
        const string iss = "https://idp.bank.example.test/realms/runtime", aud = "transacthub-runtime";
        string Token(string role) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = iss, Audience = aud, Expires = DateTime.UtcNow.AddMinutes(10), Claims = new Dictionary<string, object> { ["sub"] = "channel", ["azp"] = "channel", ["roles"] = new[] { role } },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "t1" }, SecurityAlgorithms.RsaSha256),
        });
        var roleMap = $$"""
            { "kind": "runtime-role-map", "grants": [
              { "claim": "roles", "value": "Channel", "roles": ["file-sender", "file-reader"], "tenants": ["{{Bank}}"] },
              { "claim": "roles", "value": "Reader", "roles": ["file-reader"], "tenants": ["{{Bank}}"] } ] }
            """;
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Store:Provider", "PostgreSql").UseSetting("Store:Connection", o.ConnectionString)
            .UseSetting("FileApi:Environment", Env).UseSetting("FileApi:Worker", "off").UseSetting("Auth:Oidc:Issuer", iss).UseSetting("Auth:Oidc:Audience", aud)
            .UseSetting("Auth:Oidc:SigningKeys", jwks).UseSetting("Auth:RoleMap", roleMap));
        var http = app.CreateClient();
        var body = new JsonObject { ["tenant"] = Bank, ["message"] = "uae.wps.sif", ["fileName"] = "000000000001.SIF", ["contentBase64"] = Convert.ToBase64String(Pif("sif")) };
        HttpRequestMessage Req(string role) => new(HttpMethod.Post, "/files") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Token(role)) }, Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/files", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Req("Reader"))).StatusCode);
        var ok = await http.SendAsync(Req("Channel"));
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var file = (string)JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["file"]!;
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(Req("Channel"))).StatusCode);
        var get = new HttpRequestMessage(HttpMethod.Get, $"/files/{file}/content") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Token("Reader")) } };
        Assert.Equal(Pif("sif"), await (await http.SendAsync(get)).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_consoles_resend_goes_through_the_File_API_outbox_and_is_delivered_on_the_banks_binding()
    {
        var o = await pg.Fresh();
        var root = Directory.CreateTempSubdirectory("th-fileapi-cb-").FullName;
        var root2 = Directory.CreateTempSubdirectory("th-fileapi-cb2-").FullName;
        // The bank's file-transfer binding, as its configuration release put in force leaves it in the store (R-09-112).
        async Task Bind(string release, string rootRef)
        {
            await using var db = StoreDbContext.Open(o);
            await new VersionedValues(db).Publish(ConfigurationBindings.Kind, Bank, "p.port.file-transfer.v1", new JsonObject
            {
                ["binding"] = new JsonObject { ["port"] = "p.port.file-transfer.v1", ["adapter"] = "directory", ["validFrom"] = "2026-01-01", ["settings"] = new JsonObject { ["rootRef"] = rootRef } },
                ["configurationRelease"] = release,
            }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, "bank.checker");
        }
        try
        {
            await Bind("1.0.0", "env:TH_TEST_CB_ROOT");
            using var rsa = RSA.Create(2048);
            var p = rsa.ExportParameters(false);
            var jwks = new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA", ["kid"] = "t1", ["use"] = "sig", ["alg"] = "RS256", ["n"] = Base64UrlEncoder.Encode(p.Modulus), ["e"] = Base64UrlEncoder.Encode(p.Exponent) }) }.ToJsonString();
            const string iss = "https://idp.bank.example.test/realms/runtime", aud = "transacthub-runtime";
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = iss, Audience = aud, Expires = DateTime.UtcNow.AddMinutes(10), Claims = new Dictionary<string, object> { ["sub"] = "ops-host", ["roles"] = new[] { "Ops" } },
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "t1" }, SecurityAlgorithms.RsaSha256),
            });
            var roleMap = $$"""{ "kind": "runtime-role-map", "grants": [ { "claim": "roles", "value": "Ops", "roles": ["file-outbox", "file-resender", "file-reader"], "tenants": ["{{Bank}}"] } ] }""";
            await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Store:Provider", "PostgreSql").UseSetting("Store:Connection", o.ConnectionString)
                .UseSetting("FileApi:Environment", Env).UseSetting("FileApi:Worker", "off").UseSetting("Auth:Oidc:Issuer", iss).UseSetting("Auth:Oidc:Audience", aud)
                .UseSetting("Auth:Oidc:SigningKeys", jwks).UseSetting("Auth:RoleMap", roleMap).UseSetting("TH_BANK", Bank).UseSetting("TH_TEST_CB_ROOT", root).UseSetting("TH_TEST_CB_ROOT2", root2));
            var http = app.CreateClient();
            var port = new TransactHub.Operations.FileApiTransferPort(http.BaseAddress!, "centralBank/sif", _ => Task.FromResult(token), http);
            var bytes = Encoding.UTF8.GetBytes("synthetic salary file, encrypted\n");
            Assert.Equal("sent", await port.Put(Bank, "P-9", "m-1", "000000000009.SIF.gpg", bytes));
            var at = Path.Combine(root, "centralBank", "sif", "000000000009.SIF.gpg");
            Assert.Equal(bytes, await File.ReadAllBytesAsync(at));
            File.Delete(at);
            // The same file again: the File API sends its kept bytes again, as a resend.
            Assert.Equal("sent", await port.Put(Bank, "P-9", "m-1", "000000000009.SIF.gpg", bytes));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(at));
            // A workflow's send on a host whose files go through the File API: the port's put is the File API's outbox.
            var tokens = new SecretResolver([new EnvSecretProvider(k => k == "TOKEN" ? token : null)]);
            var outbox = new OutboxFileTransfer("directory", Bank, http.BaseAddress!, "env:TOKEN", tokens, http);
            using (FileTransferOwner.Use(new FileTransferOwner("P-10", "leg-10", "p", "p.post.file-bulk", "p.post.accounting.v1")))
                await outbox.Put("coreBanking/in", "BULK-000001.csv", Encoding.UTF8.GetBytes("synthetic,bulk\n"));
            Assert.True(File.Exists(Path.Combine(root, "coreBanking", "in", "BULK-000001.csv")));
            // A read, a listing and a removal go through the File API as well (R-09-138): the host opens no folder of the bank's.
            Assert.Equal(Encoding.UTF8.GetBytes("synthetic,bulk\n"), await outbox.Get("coreBanking/in", "BULK-000001.csv"));
            Assert.Null(await outbox.Get("coreBanking/in", "BULK-000404.csv"));
            Assert.Equal(["BULK-000001.csv"], await outbox.List("coreBanking/in"));
            Assert.True(await outbox.Delete("coreBanking/in", "BULK-000001.csv"));
            Assert.False(File.Exists(Path.Combine(root, "coreBanking", "in", "BULK-000001.csv")));
            Assert.Empty(await outbox.List("coreBanking/in"));
            // A host's port is the File API by default: with no File API and no development setting, a folder is refused.
            var folderBinding = new PortBinding(Bank, FileTransferAdapters.Port, "directory", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new Dictionary<string, string> { ["rootRef"] = "env:TH_TEST_CB_ROOT" });
            Assert.IsType<OutboxFileTransfer>(FileTransferAdapters.Create(folderBinding, tokens, k => k switch { "TH_FILE_API_URL" => http.BaseAddress!.ToString(), "TH_FILE_API_TOKEN_REF" => "env:TOKEN", _ => null }));
            Assert.Contains("only through the File API", Assert.Throws<PortBindingException>(() => FileTransferAdapters.Create(folderBinding, tokens, _ => null)).Message);
            Assert.IsType<DirectoryFileTransfer>(FileTransferAdapters.Create(folderBinding, tokens, k => k == FileTransferAdapters.DirectSetting ? "dev" : null));
            // So is a console's resend: the File API's outbox, or a folder of its own only on a development host (R-09-138).
            Assert.IsType<TransactHub.Operations.FileApiTransferPort>(TransactHub.Operations.FileApiPort.ForResend(k => k switch { "Operations:FileApi:Url" => "http://file-api.test/", "Operations:FileApi:Folder" => "centralBank/sif", "Operations:FileApi:TokenRef" => "env:TOKEN", "Operations:FileTransfer:Folder" => root, _ => null }, "Operations"));
            Assert.Contains("only through the File API", Assert.Throws<InvalidOperationException>(() => TransactHub.Operations.FileApiPort.ForResend(k => k == "Operations:FileTransfer:Folder" ? root : null, "Operations")).Message);
            Assert.IsType<TransactHub.Operations.FolderFileTransfer>(TransactHub.Operations.FileApiPort.ForResend(k => k switch { "Operations:FileTransfer:Folder" => root, "TH_FILES_DIRECT" => "dev", _ => null }, "Operations"));
            Assert.Null(TransactHub.Operations.FileApiPort.ForResend(_ => null, "Operations"));
            await using var db = StoreDbContext.Open(o);
            Assert.Equal(("P-10", "leg-10", "p.post.file-bulk"), (await db.StoredFiles.AsNoTracking().SingleAsync(x => x.SubjectId == "P-10")) is var b10 ? (b10.SubjectId, b10.Leg, b10.Kind) : default);
            var f = Assert.Single(await db.StoredFiles.AsNoTracking().Where(x => x.SubjectId == "P-9").ToListAsync());
            Assert.Equal(("P-9", "out"), (f.SubjectId, f.Direction));
            Assert.Equal(["file.queued", "file.delivered", "file.resent", "file.delivered"], await db.Facts.AsNoTracking().Where(x => x.SubjectId == f.Id).OrderBy(x => x.Seq).Select(x => x.Kind).ToListAsync());

            // A later configuration release binds another folder: the next delivery uses it, with no restart of the File API (R-09-112).
            await Bind("1.1.0", "env:TH_TEST_CB_ROOT2");
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            Assert.Equal("sent", await port.Put(Bank, "P-11", "m-11", "000000000011.SIF.gpg", bytes));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root2, "centralBank", "sif", "000000000011.SIF.gpg")));
            Assert.False(File.Exists(Path.Combine(root, "centralBank", "sif", "000000000011.SIF.gpg")));

            // A folder of bindings is never read: a File API given one does not start (R-09-113).
            await using var folder = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Store:Provider", "PostgreSql").UseSetting("Store:Connection", o.ConnectionString)
                .UseSetting("FileApi:Environment", Env).UseSetting("FileApi:Worker", "off").UseSetting("Auth:Oidc:Issuer", iss).UseSetting("Auth:Oidc:Audience", aud)
                .UseSetting("Auth:Oidc:SigningKeys", jwks).UseSetting("Auth:RoleMap", roleMap).UseSetting("TH_BANKS_DIR", Path.GetTempPath()));
            Assert.Contains("TH_BANKS_DIR names a folder of port bindings", Assert.ThrowsAny<Exception>(() => folder.CreateClient()).ToString());
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(root2, true);
        }
    }
}
