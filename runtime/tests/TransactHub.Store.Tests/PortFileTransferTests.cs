using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Native;
using TransactHub.Ports;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// The File API as a port that sends data out (Issue I-192, R-09-160, R-09-162): a port a bank binds to adapter <c>file-transfer</c>
/// sends its file on the bank's binding of <c>p.port.file-transfer.v1</c>, at the folder the port's binding names, as the payment's
/// file. The salary file's render (<c>uae.wps.map.sif-out.v1</c> 1.1.0) and send (<c>uae.wps.exchange.send-salary-file.v2</c> 2.0.0)
/// run natively: the SIF is written by the source's writer, kept by the host and put on the Central Bank's folder. A bank that binds the
/// WPS port to anything else, or binds no Central Bank folder, is refused, and nothing is sent. Synthetic values only.
/// </summary>
public sealed class PortFileTransferTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    static string RuntimeRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TransactHub.Runtime.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("runtime root");
    }

    static JsonObject View() => (JsonObject)JsonNode.Parse("""
    {
      "header": { "paymentId": "uae-wps-0001", "fileId": "0000001234567261117083000" },
      "batch": { "id": "0000001234567261117083000", "currency": "AED", "debtorAgent": { "clearingMemberId": "302620122" } },
      "items": [ { "payment": { "creditorAgent": { "clearingMemberId": "203320101" }, "creditorAccount": { "iban": "AE070331234567890123456" } },
                   "ext": { "uae": { "wps": { "personId": "00001234567890", "payStartDate": "2026-10-01", "payEndDate": "2026-10-31", "daysInPeriod": 31, "fixedPay": 1200, "variablePay": 300, "leaveDays": 0 } } } } ],
      "ext": { "uae": { "wps": { "employerId": "0000001234567", "salaryMonth": "102026", "employerReference": "REF-0001" } } }
    }
    """)!;

    static PortBindings Bindings(string wpsAdapter = PortFileTransfer.Name, string sifFolder = "centralBank.sifFolder", string perMessage = "")
    {
        var b = new PortBindings();
        b.Add($"""
            kind: port-bindings
            tenant: test-bank
            bindings:
              - port: p.port.file-transfer.v1
                adapter: simulator
                validFrom: 2026-11-23T00:00:00Z
                settings:
                  centralBank.sifFolder: upload/cb/wps/sif
                  centralBank.inbox.ack.folder: upload/cb/wps/ack
                  centralBank.inbox.ack.message: uae.wps.ack
                  centralBank.prcFolder: upload/cb/wps/prc
                  centralBank.difFolder: upload/cb/wps/dif
                  centralBank.rfaFolder: upload/cb/wps/rfa
                  centralBank.dcrFolder: upload/cb/wps/dcr
                  centralBank.rfrFolder: upload/cb/wps/rfr
                  centralBank.rrrFolder: upload/cb/wps/rrr
              - port: uae.wps.port.wps.v1
                adapter: {wpsAdapter}
                validFrom: 2026-11-23T00:00:00Z
                settings:
                  submit-file.folder: {sifFolder}
                  submit-file.kind: uae.wps.sif-out
            {perMessage}
            """);
        return b;
    }

    /// <summary>A transfer that records whose each file is, as the File API's outbox does from <see cref="FileTransferOwner"/>.</summary>
    sealed class Recording : IFileTransfer
    {
        public readonly SimulatedFileTransfer Files = new();
        public readonly List<(string Path, FileTransferOwner? Owner)> Puts = new();
        public string Adapter => "simulator";
        public Task Put(string folder, string name, byte[] content, CancellationToken ct = default)
        {
            Puts.Add((FileTransferAdapters.Relative(folder, name), FileTransferOwner.Current));
            return Files.Put(folder, name, content, ct);
        }
        public Task<byte[]?> Get(string folder, string name, CancellationToken ct = default) => Files.Get(folder, name, ct);
        public Task<bool> Delete(string folder, string name, CancellationToken ct = default) => Files.Delete(folder, name, ct);
        public Task<IReadOnlyList<string>> List(string folder, CancellationToken ct = default) => Files.List(folder, ct);
    }

    [Fact]
    public void The_salary_file_is_rendered_kept_and_sent_through_the_banks_file_transfer_to_the_central_banks_folder()
    {
        var files = new MemoryNativeFiles();
        var transfer = new Recording();
        var ports = new NativePorts(Bindings(), files, transferOf: _ => transfer);
        var render = new RenderCbSif((sha, name, content) => files.Keep("out", "rendered", name, content), RenderCbSif.FactoryName);
        Assert.Equal("uae.wps.map.sif-out.v1", render.Name);
        // The factory's render has no parameter: the employer's bank is the batch's debtor agent; its file id the batch's.
        var view = View();
        view.Remove("header");
        var r = render.Run(new NativeAttempt(render.Name, "1.1.0", view, new JsonObject(), Now, "test-bank", "batch"));
        Assert.Equal("mapped", r.Outcome);
        var rendered = r.Writes["messages.rendered"]!.AsObject();
        Assert.Equal("0000001234567261117083000.SIF", (string?)rendered["fileName"]);

        var send = new SendSalaryFile(ports);
        var ctx = View();
        ctx["messages"] = new JsonObject { ["rendered"] = new JsonObject { ["fileName"] = (string?)rendered["fileName"], ["sha256"] = ((string)rendered["sha256"]!).ToLowerInvariant() } };
        var s = send.Run(new NativeAttempt(send.Name, "2.0.0", ctx, new JsonObject(), Now, "test-bank"));
        Assert.Equal("accepted", s.Outcome);
        Assert.Equal("ACCP", (string?)s.Writes["status"]!["code"]);
        var (path, owner) = Assert.Single(transfer.Puts);
        Assert.Equal("centralBank/upload/cb/wps/sif/0000001234567261117083000.SIF", path);
        // The file is kept as the scheme's message the send sends (R-09-234, Issue I-310), not as the binding's submit-file.kind.
        Assert.Equal(("uae-wps-0001", "scheme-leg", "uae.wps", "uae.wps.sif-submission", "uae.wps.exchange.send-salary-file.v2"), (owner!.PaymentId, owner.Leg, owner.Scheme, owner.Kind, owner.Activity));
        Assert.Equal("uae.wps.sif-submission", (string?)s.Writes["messages.sent"]!["message"]);
        var sent = transfer.Files.Snapshot()[path];
        Assert.StartsWith("EDR,00001234567890,203320101,", System.Text.Encoding.UTF8.GetString(sent));
        Assert.Contains("SCR,0000001234567,302620122,", System.Text.Encoding.UTF8.GetString(sent));
        Assert.Equal("centralBank/upload/cb/wps/sif", (string?)s.Writes["messages.sent"]!["folder"]);
    }

    /// <summary>
    /// R-09-213, Issue I-268: the scheme's other sends on the WPS port have native code-behinds as the salary file's does: the receipt
    /// confirmation the native render kept goes out on the bank's binding of the port; nothing rendered is refused, nothing sent.
    /// </summary>
    [Fact]
    public void The_schemes_other_sends_put_the_kept_file_on_the_banks_binding_of_the_port()
    {
        var files = new MemoryNativeFiles();
        var transfer = new Recording();
        var ports = new NativePorts(Bindings(), files, transferOf: _ => transfer);
        var sha = files.Keep("out", "rendered", "PRC000261118103000.PRC", "RDR,synthetic\r\n"u8.ToArray());
        Assert.Equal(12, SendKeptFile.Sends.Count); // the v1 lines for the distributions that pin them, and their v2 lines (R-09-232)
        var send = SendKeptFile.All(ports).Single(s => s.Name == "uae.wps.exchange.send-receipt-confirmation.v1");
        var ctx = View();
        ctx["messages"] = new JsonObject { ["rendered"] = new JsonObject { ["fileName"] = "PRC000261118103000.PRC", ["sha256"] = sha } };
        var s = send.Run(new NativeAttempt(send.Name, "1.1.0", ctx, new JsonObject(), Now, "test-bank"));
        Assert.Equal("accepted", s.Outcome);
        var (path, owner) = Assert.Single(transfer.Puts);
        Assert.Equal("centralBank/upload/cb/wps/sif/PRC000261118103000.PRC", path);
        Assert.Equal("uae.wps.exchange.send-receipt-confirmation.v1", owner!.Activity);
        Assert.Equal("uae.wps.prc", owner.Kind);
        var none = View();
        none["messages"] = new JsonObject();
        var r = send.Run(new NativeAttempt(send.Name, "1.1.0", none, new JsonObject(), Now, "test-bank"));
        Assert.Equal(("rejected", "FF01"), (r.Outcome, r.ReasonCode));
        Assert.Single(transfer.Puts);
    }

    [Fact]
    public void A_file_the_host_does_not_hold_or_a_folder_the_bank_does_not_bind_is_refused_and_nothing_is_sent()
    {
        var transfer = new Recording();
        var files = new MemoryNativeFiles();
        var sha = files.Keep("out", "rendered", "0000001234567261117083000.SIF", "EDR,synthetic\r\n"u8.ToArray());
        var send = new SendSalaryFile(new NativePorts(Bindings(sifFolder: "centralBank.missingFolder"), files, transferOf: _ => transfer));
        NativeResult Send(SendSalaryFile s, string hash)
        {
            var ctx = View();
            ctx["messages"] = new JsonObject { ["rendered"] = new JsonObject { ["fileName"] = "0000001234567261117083000.SIF", ["sha256"] = hash } };
            return s.Run(new NativeAttempt(s.Name, "2.0.0", ctx, new JsonObject(), Now, "test-bank"));
        }
        Assert.Equal("rejected", Send(send, sha).Outcome); // the send's folder names a setting the file-transfer binding lacks
        var unbound = new SendSalaryFile(new NativePorts(Bindings(), files, transferOf: _ => transfer));
        Assert.Equal("rejected", Send(unbound, "0000").Outcome);
        Assert.Contains("missingFolder", Assert.Throws<PortBindingException>(() => PortFileTransfer.FolderOf(Bindings(sifFolder: "centralBank.missingFolder").InForce("test-bank", "uae.wps.port.wps.v1", Now), Bindings().InForce("test-bank", FileTransferAdapters.Port, Now), "submit-file")).Message);
        // A bank that binds the WPS port to another adapter is not answered by the image for a module.
        var other = new SendSalaryFile(new NativePorts(Bindings(wpsAdapter: "gateway-rest"), files, transferOf: _ => transfer));
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => Send(other, sha)).Name);
        Assert.Empty(transfer.Puts);
        // A folder of the file-transfer binding may be named as it is.
        var named = new PortBindings();
        named.Add("""
            kind: port-bindings
            tenant: test-bank
            bindings:
              - port: uae.wps.port.wps.v1
                adapter: file-transfer
                validFrom: 2026-11-23T00:00:00Z
                settings: { submit-file.folder: centralBank/outbound/sif }
            """);
        Assert.Equal("centralBank/outbound/sif", PortFileTransfer.FolderOf(named.InForce("test-bank", "uae.wps.port.wps.v1", Now), Bindings().InForce("test-bank", FileTransferAdapters.Port, Now), "submit-file"));
    }

    /// <summary>
    /// R-09-182 (Issue I-266): where the bank's binding of <c>p.port.encryption.v1</c> encrypts the salary file (the openpgp adapter, a
    /// key made by the test), the send v2 puts the encrypted file on the Central Bank's folder, never the plain one the render kept: the
    /// bytes that reach the destination are an OpenPGP message that the recipient's key opens to the rendered file. The send v1, which
    /// reads no encryption fact, would have sent the plain file. An encryption that names a file the host does not hold, or one the
    /// service refused, sends nothing.
    /// </summary>
    [Fact]
    public void The_send_v2_puts_the_encrypted_salary_file_on_the_central_banks_folder_where_the_bank_encrypts()
    {
        var pass = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var (pub, sec) = OpenPgp.GenerateKeyPair("Central Bank WPS (test)", pass.ToCharArray());
        var env = new Dictionary<string, string> { ["TH_TEST_CB_PUBLIC"] = System.Text.Encoding.UTF8.GetString(pub), ["TH_TEST_CB_SECRET"] = System.Text.Encoding.UTF8.GetString(sec), ["TH_TEST_CB_PASS"] = pass };
        var secrets = new TransactHub.Security.SecretResolver([new TransactHub.Security.EnvSecretProvider(k => env.GetValueOrDefault(k))]);
        var bindings = Bindings();
        bindings.Add("""
            kind: port-bindings
            tenant: test-bank
            bindings:
              - port: p.port.encryption.v1
                adapter: openpgp
                validFrom: 2026-11-23T00:00:00Z
                settings:
                  keys.cbuae-wps.secretRef: env:TH_TEST_CB_PUBLIC
                  armor: "false"
            """);
        var files = new MemoryNativeFiles();
        var transfer = new Recording();
        var ports = new NativePorts(bindings, files, secrets: secrets, transferOf: _ => transfer);
        var render = new RenderCbSif((sha, name, content) => files.Keep("out", "rendered", name, content), RenderCbSif.FactoryName);
        var view = View();
        view.Remove("header");
        var rendered = render.Run(new NativeAttempt(render.Name, "1.1.0", view, new JsonObject(), Now, "test-bank", "batch")).Writes["messages.rendered"]!.AsObject();
        var plain = files.Get(((string)rendered["sha256"]!).ToLowerInvariant())!;

        // The encryption step: the rendered file encrypted for the Central Bank's key, kept by the host.
        var encrypt = new TransactHub.Activities.Platform.Native.EncryptFile(files, ports);
        var ctx = View();
        ctx["messages"] = new JsonObject { ["rendered"] = new JsonObject { ["fileName"] = (string?)rendered["fileName"], ["sha256"] = ((string)rendered["sha256"]!).ToLowerInvariant() } };
        var e = encrypt.Run(new NativeAttempt(encrypt.Name, "1.1.0", ctx, new JsonObject { ["recipientKey"] = "cbuae-wps" }, Now, "test-bank"));
        Assert.Equal("encrypted", e.Outcome);
        ctx["facts"] = new JsonObject { ["encryption"] = e.Writes["facts.encryption"]!.DeepClone() };

        var send = new SendSalaryFile(ports);
        Assert.Equal("uae.wps.exchange.send-salary-file.v2", send.Name);
        var s = send.Run(new NativeAttempt(send.Name, "2.0.0", ctx, new JsonObject(), Now, "test-bank"));
        Assert.Equal("accepted", s.Outcome);
        Assert.True((bool)s.Writes["messages.sent"]!["encrypted"]!);
        var (path, owner) = Assert.Single(transfer.Puts);
        Assert.Equal("centralBank/upload/cb/wps/sif/0000001234567261117083000.SIF.gpg", path);
        Assert.Equal("uae.wps.exchange.send-salary-file.v2", owner!.Activity);
        var sent = transfer.Files.Snapshot()[path];
        // At the destination: an OpenPGP message, not the plain file, which the recipient's key opens to the rendered file.
        Assert.True(OpenPgp.IsEncrypted(sent));
        Assert.False(OpenPgp.IsEncrypted(plain));
        Assert.DoesNotContain("EDR,", System.Text.Encoding.Latin1.GetString(sent));
        var dec = FileProtectionAdapters.Decryption(new PortBinding("test-bank", OpenPgpDecryption.Port, OpenPgpDecryption.Name, Now, new Dictionary<string, string> { ["privateKeyRef"] = "env:TH_TEST_CB_SECRET", ["secretRef"] = "env:TH_TEST_CB_PASS" }), secrets);
        Assert.Equal(plain, dec.Decrypt(sent).GetAwaiter().GetResult());

        // An encryption that names a file the host does not hold, or one the service refused, sends nothing more.
        var before = transfer.Puts.Count;
        var missing = (JsonObject)ctx.DeepClone();
        missing["facts"]!["encryption"]!["sha256"] = new string('0', 64);
        Assert.Equal("rejected", send.Run(new NativeAttempt(send.Name, "2.0.0", missing, new JsonObject(), Now, "test-bank")).Outcome);
        var refused = (JsonObject)ctx.DeepClone();
        refused["facts"] = new JsonObject { ["encryption"] = new JsonObject { ["outcome"] = "refused" } };
        Assert.Equal("rejected", send.Run(new NativeAttempt(send.Name, "2.0.0", refused, new JsonObject(), Now, "test-bank")).Outcome);
        Assert.Equal(before, transfer.Puts.Count);
        // A simulated encryption that names no file (a test distribution): the rendered file goes out, as on the laptop.
        var simulated = (JsonObject)ctx.DeepClone();
        simulated["facts"] = new JsonObject { ["encryption"] = new JsonObject { ["outcome"] = "encrypted", ["result"] = "encrypted" } };
        Assert.False((bool)send.Run(new NativeAttempt(send.Name, "2.0.0", simulated, new JsonObject(), Now, "test-bank")).Writes["messages.sent"]!["encrypted"]!);
    }

    /// <summary>
    /// R-09-232 (Issue I-293), R-09-234 (Issue I-310), R-09-235 (Issue I-313): each of the scheme's other sends v2 sends the encrypted file
    /// the encryption step names, never the plain one, as the salary file's does; it is kept as the scheme message whose <c>sentBy</c>
    /// names the send, and goes to that message's folder where the bank's binding names one (<c>submit-file.&lt;message&gt;.folder</c>),
    /// else to the operation's folder. The v1 lines read no encryption fact. A refused encryption sends nothing.
    /// </summary>
    [Fact]
    public void Every_send_v2_puts_the_encrypted_file_on_its_messages_folder_where_the_bank_encrypts()
    {
        var folders = string.Join("", new[] { "prc", "dif", "rfa", "dcr", "rfr", "rrr" }.Select(m => $"      submit-file.uae.wps.{m}.folder: centralBank.{m}Folder\n"));
        var files = new MemoryNativeFiles();
        var transfer = new Recording();
        var ports = new NativePorts(Bindings(perMessage: folders), files, transferOf: _ => transfer);
        var v2 = SendKeptFile.All(ports).Where(a => a.Name.EndsWith(".v2", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, v2.Count);
        foreach (var send in v2)
        {
            var message = SendKeptFile.Sends[send.Name].Message;
            var plain = files.Keep("out", "rendered", $"F-{message}.TXT", System.Text.Encoding.UTF8.GetBytes($"plain,{message}\r\n"));
            var cipher = files.Keep("out", "encrypted", $"F-{message}.TXT.gpg", System.Text.Encoding.UTF8.GetBytes($"cipher,{message}"));
            var ctx = View();
            ctx["messages"] = new JsonObject { ["rendered"] = new JsonObject { ["fileName"] = $"F-{message}.TXT", ["sha256"] = plain } };
            ctx["facts"] = new JsonObject { ["encryption"] = new JsonObject { ["outcome"] = "encrypted", ["sha256"] = cipher, ["fileName"] = $"F-{message}.TXT.gpg", ["plainSha256"] = plain } };
            var before = transfer.Puts.Count;
            var s = send.Run(new NativeAttempt(send.Name, "2.0.0", ctx, new JsonObject(), Now, "test-bank"));
            Assert.Equal("accepted", s.Outcome);
            Assert.True((bool)s.Writes["messages.sent"]!["encrypted"]!);
            var (path, owner) = transfer.Puts[before];
            Assert.Equal($"centralBank/upload/cb/wps/{message[(message.LastIndexOf('.') + 1)..]}/F-{message}.TXT.gpg", path);
            Assert.Equal((message, send.Name), (owner!.Kind, owner.Activity));
            Assert.Equal($"cipher,{message}", System.Text.Encoding.UTF8.GetString(transfer.Files.Snapshot()[path]));
            // The same send v1 reads no encryption fact, and sends the plain file (the bug of Issue I-293).
            var v1 = SendKeptFile.All(ports).Single(a => a.Name == send.Name.Replace(".v2", ".v1"));
            Assert.Equal("accepted", v1.Run(new NativeAttempt(v1.Name, "1.1.0", ctx, new JsonObject(), Now, "test-bank")).Outcome);
            Assert.Equal($"plain,{message}\r\n", System.Text.Encoding.UTF8.GetString(transfer.Files.Snapshot()[$"centralBank/upload/cb/wps/{message[(message.LastIndexOf('.') + 1)..]}/F-{message}.TXT"]));
            // A refused encryption sends nothing.
            var refused = (JsonObject)ctx.DeepClone();
            refused["facts"] = new JsonObject { ["encryption"] = new JsonObject { ["outcome"] = "refused" } };
            var count = transfer.Puts.Count;
            Assert.Equal("rejected", send.Run(new NativeAttempt(send.Name, "2.0.0", refused, new JsonObject(), Now, "test-bank")).Outcome);
            Assert.Equal(count, transfer.Puts.Count);
        }
        // A binding that names no folder for a message sends it to the operation's folder.
        var plainBinding = new NativePorts(Bindings(), files, transferOf: _ => transfer);
        var prc = SendKeptFile.All(plainBinding).Single(a => a.Name == "uae.wps.exchange.send-receipt-confirmation.v2");
        var c = View();
        var sha = files.Keep("out", "rendered", "PRC1.PRC", "RDR,synthetic\r\n"u8.ToArray());
        c["messages"] = new JsonObject { ["rendered"] = new JsonObject { ["fileName"] = "PRC1.PRC", ["sha256"] = sha } };
        Assert.Equal("accepted", prc.Run(new NativeAttempt(prc.Name, "2.0.0", c, new JsonObject(), Now, "test-bank")).Outcome);
        Assert.Equal("centralBank/upload/cb/wps/sif/PRC1.PRC", transfer.Puts[^1].Path);
    }

    /// <summary>
    /// R-09-233, R-09-234 (Issues I-293, I-310): every message the scheme sends names a send of the WPS module that says it is that
    /// message, and the salary file's send v1, which the factory retired, is not in the module.
    /// </summary>
    [Fact]
    public void The_WPS_module_carries_no_send_the_factory_retired_and_each_send_is_its_schemes_message()
    {
        var scheme = File.ReadAllLines(Path.Combine(RuntimeRoot(), "..", "spec", "factory", "uae.wps", "scheme.yaml"));
        var sentBy = new Dictionary<string, string>();
        string? id = null;
        foreach (var line in scheme)
        {
            var t = line.Trim();
            if (t.StartsWith("- id: ", StringComparison.Ordinal)) id = t[6..];
            else if (t.StartsWith("sentBy: ", StringComparison.Ordinal) && id is not null) sentBy[t[8..]] = id;
        }
        Assert.Equal(7, sentBy.Count);
        foreach (var (send, message) in sentBy)
            Assert.Equal(message, send == SendSalaryFile.V2 ? SendSalaryFile.Message : SendKeptFile.Sends[send].Message);
        var module = Path.Combine(RuntimeRoot(), "src", "TransactHub.Activities.Wps", "Native");
        foreach (var f in Directory.GetFiles(module, "*.cs"))
            Assert.DoesNotContain("\"uae.wps.exchange.send-salary-file.v1\"", File.ReadAllText(f));
    }

    [Fact]
    public void Adibs_laptop_release_binds_the_wps_port_to_the_file_api_and_its_ack_inbox_drops_the_central_banks_file_id()
    {
        var b = new PortBindings();
        b.Add(File.ReadAllText(Path.Combine(RuntimeRoot(), "..", "deploy", "laptop", "banks", "adib", "file-transfer.yaml")));
        var wps = b.InForce("adib", "uae.wps.port.wps.v1", Now);
        Assert.Equal(PortFileTransfer.Name, wps.Adapter);
        Assert.Equal("centralBank/upload/cb/wps/sif", PortFileTransfer.FolderOf(wps, b.InForce("adib", FileTransferAdapters.Port, Now), "submit-file"));
        Assert.Equal("centralBank/upload/cb/wps/sif", PortFileTransfer.FolderOf(wps, b.InForce("adib", FileTransferAdapters.Port, Now), "submit-file", SendSalaryFile.Message));
        // The binding names no kind: the file is kept as the scheme's message (R-09-234, Issue I-310).
        Assert.DoesNotContain("submit-file.kind", wps.Settings.Keys);
        Assert.Equal(SendSalaryFile.Message, PortFileTransfer.KindOf(wps, "uae.wps.port.wps.v1", "submit-file", SendSalaryFile.Message));
        // The ACK is correlated by the scheme's correlation map (R-09-212, Issue I-267): the binding trims no stem.
        Assert.DoesNotContain(b.InForce("adib", FileTransferAdapters.Port, Now).Settings.Keys, k => k.EndsWith(".stemDrop"));
    }
}
