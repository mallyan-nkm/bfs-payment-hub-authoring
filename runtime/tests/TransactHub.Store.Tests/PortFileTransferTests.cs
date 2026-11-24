using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Native;
using TransactHub.Ports;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// The File API as a port that sends data out (Issue I-192, R-09-160, R-09-162): a port a bank binds to adapter <c>file-transfer</c>
/// sends its file on the bank's binding of <c>p.port.file-transfer.v1</c>, at the folder the port's binding names, as the payment's
/// file. The salary file's render (<c>uae.wps.map.sif-out.v1</c> 1.1.0) and send (<c>uae.wps.exchange.send-salary-file.v1</c> 1.1.0)
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

    static PortBindings Bindings(string wpsAdapter = PortFileTransfer.Name, string sifFolder = "centralBank.sifFolder")
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
              - port: uae.wps.port.wps.v1
                adapter: {wpsAdapter}
                validFrom: 2026-11-23T00:00:00Z
                settings:
                  submit-file.folder: {sifFolder}
                  submit-file.kind: uae.wps.sif-out
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
        var s = send.Run(new NativeAttempt(send.Name, "1.1.0", ctx, new JsonObject(), Now, "test-bank"));
        Assert.Equal("accepted", s.Outcome);
        Assert.Equal("ACCP", (string?)s.Writes["status"]!["code"]);
        var (path, owner) = Assert.Single(transfer.Puts);
        Assert.Equal("centralBank/upload/cb/wps/sif/0000001234567261117083000.SIF", path);
        Assert.Equal(("uae-wps-0001", "scheme-leg", "uae.wps", "uae.wps.sif-out", "uae.wps.exchange.send-salary-file.v1"), (owner!.PaymentId, owner.Leg, owner.Scheme, owner.Kind, owner.Activity));
        var sent = transfer.Files.Snapshot()[path];
        Assert.StartsWith("EDR,00001234567890,203320101,", System.Text.Encoding.UTF8.GetString(sent));
        Assert.Contains("SCR,0000001234567,302620122,", System.Text.Encoding.UTF8.GetString(sent));
        Assert.Equal("centralBank/upload/cb/wps/sif", (string?)s.Writes["messages.sent"]!["folder"]);
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
            return s.Run(new NativeAttempt(s.Name, "1.1.0", ctx, new JsonObject(), Now, "test-bank"));
        }
        Assert.Equal("rejected", Send(send, sha).Outcome);
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

    [Fact]
    public void Adibs_laptop_release_binds_the_wps_port_to_the_file_api_and_its_ack_inbox_drops_the_central_banks_file_id()
    {
        var b = new PortBindings();
        b.Add(File.ReadAllText(Path.Combine(RuntimeRoot(), "..", "deploy", "laptop", "banks", "adib", "file-transfer.yaml")));
        var wps = b.InForce("adib", "uae.wps.port.wps.v1", Now);
        Assert.Equal(PortFileTransfer.Name, wps.Adapter);
        Assert.Equal("centralBank/upload/cb/wps/sif", PortFileTransfer.FolderOf(wps, b.InForce("adib", FileTransferAdapters.Port, Now), "submit-file"));
        // The ACK is correlated by the scheme's correlation map (R-09-212, Issue I-267): the binding trims no stem.
        Assert.DoesNotContain(b.InForce("adib", FileTransferAdapters.Port, Now).Settings.Keys, k => k.EndsWith(".stemDrop"));
    }
}
