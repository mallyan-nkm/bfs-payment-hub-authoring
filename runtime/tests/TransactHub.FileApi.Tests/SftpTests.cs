using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TransactHub.FileApi;
using TransactHub.Ports;
using TransactHub.Security;

namespace TransactHub.FileApi.Tests;

/// <summary>
/// An SFTP server in a container (atmoz/sftp, pinned by digest), with a login key made at test time (RSA, never written beyond the
/// container) and a password user. Its host keys are read out of band from the container, as a bank reads its partner's, and pinned.
/// </summary>
public sealed class SftpServer : IAsyncLifetime
{
    public const string User = "th", KeyUser = "thkey", Password = "th-test-only";
    public readonly RSA LoginKey = RSA.Create(2048);
    readonly IContainer container;
    public string Fingerprints { get; private set; } = "";
    public string Host => container.Hostname;
    public int Port => container.GetMappedPublicPort(22);

    public SftpServer()
    {
        container = new ContainerBuilder("atmoz/sftp:alpine@sha256:c5fb9e4beb8b7054a4c0b65c5e11df47cc20da95ad6002e88aecff62bab06372")
            .WithName($"th-fileapi-sftp-{Guid.NewGuid():N}"[..32])
            .WithCommand($"{User}:{Password}:1001::upload", $"{KeyUser}::1002::upload")
            .WithResourceMapping(Encoding.ASCII.GetBytes(OpenSshPublicKey(LoginKey) + "\n"), $"/home/{KeyUser}/.ssh/keys/test.pub")
            .WithPortBinding(22, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server listening on"))
            .Build();
    }

    /// <summary>An RSA public key as an OpenSSH authorized_keys line.</summary>
    public static string OpenSshPublicKey(RSA rsa)
    {
        var p = rsa.ExportParameters(false);
        using var ms = new MemoryStream();
        void Str(byte[] b) { var l = BitConverter.GetBytes(b.Length); if (BitConverter.IsLittleEndian) Array.Reverse(l); ms.Write(l); ms.Write(b); }
        byte[] Mpint(byte[] b) => b.Length > 0 && (b[0] & 0x80) != 0 ? [0, .. b] : b;
        Str(Encoding.ASCII.GetBytes("ssh-rsa"));
        Str(Mpint(p.Exponent!));
        Str(Mpint(p.Modulus!));
        return "ssh-rsa " + Convert.ToBase64String(ms.ToArray()) + " th-test";
    }

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        var prints = new List<string>();
        foreach (var k in new[] { "ed25519", "rsa", "ecdsa" })
        {
            var r = await container.ExecAsync(["ssh-keygen", "-lf", $"/etc/ssh/ssh_host_{k}_key.pub"]);
            if (r.ExitCode == 0) prints.Add(r.Stdout.Split(' ')[1]);
        }
        Fingerprints = string.Join(',', prints);
        Assert.NotEmpty(prints);
    }

    public async Task DisposeAsync()
    {
        await container.DisposeAsync();
        LoginKey.Dispose();
    }
}

public sealed class SftpTests(SftpServer server) : IClassFixture<SftpServer>
{
    static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-11-01T00:00:00Z");

    static SecretResolver Secrets(Dictionary<string, string> values) => new([new EnvSecretProvider(k => values.GetValueOrDefault(k))]);

    PortBinding Binding(Dictionary<string, string>? more = null)
    {
        var s = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Three partners, as a bank has: the Central Bank, the labour ministry and core banking's file interface.
            ["centralBank.host"] = server.Host, ["centralBank.port"] = server.Port.ToString(), ["centralBank.hostKeyRef"] = "env:CB_HOSTKEY", ["centralBank.secretRef"] = "env:CB_LOGIN",
            ["ministry.host"] = server.Host, ["ministry.port"] = server.Port.ToString(), ["ministry.hostKeyRef"] = "env:CB_HOSTKEY", ["ministry.secretRef"] = "env:KEY_LOGIN",
            ["coreBanking.host"] = server.Host, ["coreBanking.port"] = server.Port.ToString(), ["coreBanking.hostKeyRef"] = "env:CB_HOSTKEY", ["coreBanking.user"] = SftpServer.User, ["coreBanking.secretRef"] = "env:PASSWORD_ONLY",
            ["sessions.max"] = "2", ["sessions.reuse"] = "true", ["sessions.idleSeconds"] = "60", ["sessions.keepAliveSeconds"] = "30", ["sessions.acquireSeconds"] = "20", ["sessions.attempts"] = "2",
        };
        foreach (var (k, v) in more ?? new()) s[k] = v;
        return new PortBinding("test-bank", FileTransferAdapters.Port, "sftp", From, s);
    }

    Dictionary<string, string> Values(string? fingerprints = null) => new()
    {
        ["CB_HOSTKEY"] = fingerprints ?? server.Fingerprints,
        ["CB_LOGIN"] = $"{SftpServer.User}:{SftpServer.Password}",
        ["KEY_LOGIN"] = $"{SftpServer.KeyUser}\n{server.LoginKey.ExportRSAPrivateKeyPem()}",
        ["PASSWORD_ONLY"] = SftpServer.Password,
    };

    [Fact]
    public async Task Puts_lists_reads_and_removes_a_file_at_each_partner_with_a_password_and_with_a_key()
    {
        using var t = new SftpFileTransfer(Binding(), Secrets(Values()));
        Assert.Equal(["centralBank", "coreBanking", "ministry"], t.Partners.Keys.Order(StringComparer.Ordinal));
        foreach (var partner in new[] { "centralBank", "ministry", "coreBanking" })
        {
            var folder = $"{partner}/upload/{partner}-out";
            var bytes = Encoding.UTF8.GetBytes($"synthetic file for {partner}\n");
            await t.Put(folder, "000000000001.SIF", bytes);
            Assert.Equal(["000000000001.SIF"], await t.List(folder));
            var listed = Assert.Single(await t.ListFiles(folder));
            Assert.Equal(bytes.Length, listed.Size);
            Assert.NotNull(listed.Modified);
            Assert.Equal(bytes, await t.Get(folder, "000000000001.SIF"));
            // A second put replaces the file whole: the partner never sees half a file or a .part.
            await t.Put(folder, "000000000001.SIF", [.. bytes, .. bytes]);
            Assert.Equal(bytes.Length * 2, (await t.Get(folder, "000000000001.SIF"))!.Length);
            Assert.True(await t.Delete(folder, "000000000001.SIF"));
            Assert.Empty(await t.List(folder));
            Assert.Null(await t.Get(folder, "000000000001.SIF"));
        }
    }

    [Fact]
    public async Task A_host_key_the_binding_does_not_pin_is_refused_before_any_login()
    {
        using var t = new SftpFileTransfer(Binding(), Secrets(Values("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")));
        var e = await Assert.ThrowsAsync<FileTransferException>(() => t.Put("centralBank/upload", "x.txt", [1]));
        Assert.Contains("does not pin", e.Message);
        Assert.DoesNotContain(SftpServer.Password, e.Message);
    }

    [Fact]
    public async Task A_partner_without_a_pinned_host_key_is_refused_and_a_wrong_login_names_its_reference_only()
    {
        var unpinned = Binding(new() { ["centralBank.hostKeyRef"] = "" });
        using (var t = new SftpFileTransfer(new PortBinding(unpinned.Tenant, unpinned.Port, unpinned.Adapter, From, unpinned.Settings.Where(kv => kv.Key != "centralBank.hostKeyRef").ToDictionary()), Secrets(Values())))
        {
            var e = await Assert.ThrowsAsync<FileTransferException>(() => t.List("centralBank/upload"));
            Assert.Contains("never trusted on first use", e.Message);
        }
        var v = Values();
        v["CB_LOGIN"] = $"{SftpServer.User}:not-the-password";
        using var w = new SftpFileTransfer(Binding(), Secrets(v));
        var x = await Assert.ThrowsAsync<FileTransferException>(() => w.List("centralBank/upload"));
        Assert.Contains("env:CB_LOGIN", x.Message);
        Assert.DoesNotContain("not-the-password", x.Message);
    }

    [Fact]
    public async Task Sessions_are_reused_and_at_most_sessions_max_run_at_once()
    {
        using var t = new SftpFileTransfer(Binding(), Secrets(Values()));
        Assert.Equal(new SftpSessions(2, true, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), 2), t.Sessions);
        var puts = Enumerable.Range(1, 8).Select(i => t.Put("centralBank/upload/many", $"f{i}.txt", Encoding.UTF8.GetBytes($"{i}"))).ToList();
        await Task.WhenAll(puts);
        Assert.Equal(8, (await t.List("centralBank/upload/many")).Count);
    }

    [Fact]
    public async Task The_File_API_polls_a_partner_inbox_over_sftp_and_delivers_its_outbox_there()
    {
        // The inbox a binding names (<partner>.folder, .message, .doneFolder, .errorFolder), on the same adapter as the outbox.
        var b = Binding(new()
        {
            ["centralBank.folder"] = "/upload/inbox", ["centralBank.message"] = "uae.wps.ack", ["centralBank.doneFolder"] = "Done", ["centralBank.errorFolder"] = "/upload/error",
            ["ministry.inbox.status.folder"] = "/upload/mol", ["ministry.inbox.status.message"] = "uae.wps.mol-status",
        });
        var boxes = Inbox.Of(b).ToList();
        Assert.Equal(2, boxes.Count);
        var cb = boxes.Single(x => x.Message == "uae.wps.ack");
        Assert.Equal(("centralBank/upload/inbox", "centralBank/upload/inbox/Done", "centralBank/upload/error"), (cb.Folder, cb.DoneFolder, cb.ErrorFolder));
        using var t = new SftpFileTransfer(b, Secrets(Values()));
        await t.Put("centralBank/upload/inbox", "000000000001.ACK", Encoding.UTF8.GetBytes("ack\n"));
        Assert.Equal(["000000000001.ACK"], await t.List(cb.Folder));
    }

    [Fact]
    public void The_banks_sftp_bindings_load_with_their_partners_and_session_settings()
    {
        var repo = Repo();
        foreach (var bank in new[] { "dib", "adib" })
        {
            var all = new PortBindings();
            all.Add(File.ReadAllText(Path.Combine(repo, "content-configurations", "banks", bank, "file-transfer.yaml")));
            var binding = all.InForce(bank, FileTransferAdapters.Port, DateTimeOffset.Parse("2026-12-01T00:00:00Z"));
            var t = Assert.IsType<SftpFileTransfer>(FileTransferAdapters.Create(binding, Secrets(new())));
            Assert.Equal(["centralBank", "pickup"], t.Partners.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("centralBank", t.DefaultPartner);
            Assert.All(t.Partners.Values, p => Assert.Equal(22, p.Port));
            // The login is the gateway's, by reference; neither bank pins a host key yet (Issue I-157), so no session would open.
            Assert.All(t.Partners.Values, p => Assert.StartsWith("env:", p.CredentialRef));
            Assert.All(t.Partners.Values, p => Assert.Null(p.HostKeyRef));
            if (bank == "dib") Assert.Equal(new SftpSessions(5, true, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), 3), t.Sessions);
            Assert.Equal(("centralBank", "/sftp_user/wps/x"), (t.Resolve("centralBank/sftp_user/wps", "x").Partner.Name, t.Resolve("centralBank/sftp_user/wps", "x").Path));
            Assert.Equal("pickup", t.Resolve("pickup/in", "f").Partner.Name);
            Assert.Equal(("centralBank", "/wps/out/f"), (t.Resolve("wps/out", "f").Partner.Name, t.Resolve("wps/out", "f").Path));
        }
    }

    [Fact]
    public void A_binding_with_no_partner_or_a_setting_that_is_not_a_reference_is_refused()
    {
        Assert.Throws<PortBindingException>(() => new SftpFileTransfer(new PortBinding("t", FileTransferAdapters.Port, "sftp", From, new Dictionary<string, string>())));
        Assert.Throws<PortBindingException>(() => new SftpFileTransfer(new PortBinding("t", FileTransferAdapters.Port, "sftp", From, new Dictionary<string, string> { ["a.host"] = "h", ["a.secretRef"] = "plain-password" })));
        Assert.Throws<PortBindingException>(() => new SftpFileTransfer(new PortBinding("t", FileTransferAdapters.Port, "sftp", From, new Dictionary<string, string> { ["a.host"] = "h", ["sessions.max"] = "0" })));
        Assert.Throws<PortBindingException>(() => new SftpFileTransfer(new PortBinding("t", FileTransferAdapters.Port, "sftp", From, new Dictionary<string, string> { ["a.host"] = "h", ["b.host"] = "h" })));
    }

    internal static string Repo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content-configurations")) && Directory.Exists(Path.Combine(d.FullName, "runtime"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
