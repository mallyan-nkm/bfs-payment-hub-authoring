using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using TransactHub.Security;

namespace TransactHub.Ports;

// The `sftp` adapter of p.port.file-transfer.v1 (Issue I-129, R-09-80): the bank's SFTP partners (the Central Bank, the
// labour ministry, core banking's file interface, the corporate channel's pick-up) through SSH.NET, pinned in TransactHub.Ports.csproj.
// The source reached them through its SFTP gateway service (services/platform/SftpGateway), one named profile per endpoint; here
// each partner is a prefix of the binding's settings, so one binding names every partner of the bank:
//
//   <partner>.host, <partner>.port (default 22)     where it is
//   <partner>.hostKeyRef                           the SHA-256 fingerprint(s) of its host key, by reference (`SHA256:<base64>`, as
//                                                  `ssh-keygen -l` prints it; several separated by commas, for a rotation). A
//                                                  partner without one is refused when a session opens: a host key is never
//                                                  trusted on first use.
//   <partner>.secretRef (or secretRef)             the login, by reference: `<user>:<password>`, or `<user>` and a new line and an
//                                                  OpenSSH or PEM private key. With <partner>.user set, the secret is the password
//                                                  or the key alone.
//   sessions.max, sessions.reuse, sessions.idleSeconds, sessions.keepAliveSeconds, sessions.acquireSeconds, sessions.attempts
//                                                  the session settings (the source's SftpWpsCb parameters), for every partner
//
// A folder names its partner by its first segment (`centralBank/sftp_user/wps/DIB/SIF/ToBeDone`); a folder whose first segment is no
// partner is the default partner's (setting `defaultPartner`, else `centralBank`, else the only partner), and its remote path is the
// folder itself from the root. A file is written to `<name>.part` and renamed into place, so the partner never reads half a file.
// Errors name the partner, the host and the reference, never a credential.

/// <summary>A file in a folder, with its size and when it was last written (null when the transport does not say).</summary>
public sealed record TransferFile(string Name, long Size, DateTimeOffset? Modified);

/// <summary>What a transport can say beyond the names: sizes and times, so a poller can leave a file that is still arriving.</summary>
public interface IFileTransferListing
{
    Task<IReadOnlyList<TransferFile>> ListFiles(string folder, CancellationToken ct = default);
}

/// <summary>One SFTP partner of a binding: where it is, its pinned host key and its login, by reference.</summary>
public sealed record SftpPartner(string Name, string Host, int Port, string? HostKeyRef, string? CredentialRef, string? User, IReadOnlyDictionary<string, string> Settings)
{
    /// <summary>A setting of this partner (`<partner>.<key>`), or the fallback.</summary>
    public string? Setting(string key) => Settings.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
}

/// <summary>The session settings a binding carries (`sessions.*`), with the source's defaults where a binding names none.</summary>
public sealed record SftpSessions(int Max, bool Reuse, TimeSpan Idle, TimeSpan KeepAlive, TimeSpan Acquire, int Attempts)
{
    public static SftpSessions Of(PortBinding b)
    {
        int I(string k, int d)
        {
            var v = b.Setting($"sessions.{k}", d.ToString(CultureInfo.InvariantCulture));
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n
                : throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: sessions.{k} is not a whole number above 0");
        }
        var reuse = b.Setting("sessions.reuse", "true");
        if (reuse is not ("true" or "false")) throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: sessions.reuse is true or false");
        return new SftpSessions(I("max", 5), reuse == "true", TimeSpan.FromSeconds(I("idleSeconds", 60)), TimeSpan.FromSeconds(I("keepAliveSeconds", 30)),
            TimeSpan.FromSeconds(I("acquireSeconds", 60)), I("attempts", 3));
    }
}

/// <summary>Why a transfer to or from a partner failed: never a credential.</summary>
public sealed class FileTransferException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The <c>sftp</c> adapter: a session pool per partner, the host key checked against its pinned fingerprint before any login.</summary>
public sealed class SftpFileTransfer : IFileTransfer, IFileTransferListing, IDisposable
{
    public const string Name = "sftp";
    public string Adapter => Name;
    public PortBinding Binding { get; }
    public SftpSessions Sessions { get; }
    public IReadOnlyDictionary<string, SftpPartner> Partners { get; }
    public string DefaultPartner { get; }
    readonly ISecretResolver secrets;
    readonly ConcurrentDictionary<string, Pool> pools = new(StringComparer.Ordinal);

    /// <summary>Reads the binding: every partner and the session settings. Nothing connects until a file moves.</summary>
    public SftpFileTransfer(PortBinding b, ISecretResolver? secrets = null)
    {
        Binding = b;
        this.secrets = secrets ?? SecretResolver.FromEnvironment();
        Sessions = SftpSessions.Of(b);
        var partners = new Dictionary<string, SftpPartner>(StringComparer.Ordinal);
        foreach (var (key, host) in b.Settings.Where(kv => kv.Key.EndsWith(".host", StringComparison.Ordinal) && kv.Key.Count(c => c == '.') == 1))
        {
            var p = key[..^".host".Length];
            if (host.Length == 0) throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: {key} is empty");
            var own = b.Settings.Where(kv => kv.Key.StartsWith(p + ".", StringComparison.Ordinal)).ToDictionary(kv => kv.Key[(p.Length + 1)..], kv => kv.Value, StringComparer.Ordinal);
            var port = own.TryGetValue("port", out var ps) ? int.TryParse(ps, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is > 0 and < 65536 ? n
                : throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: {p}.port is not a port") : 22;
            foreach (var r in new[] { "hostKeyRef", "secretRef" })
                if (own.TryGetValue(r, out var v) && v.Length > 0 && !SecretRef.IsRef(v))
                    throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: {p}.{r} must be a reference ({SecretRef.Forms})");
            var cred = own.GetValueOrDefault("secretRef") is { Length: > 0 } c ? c : b.Settings.GetValueOrDefault("secretRef");
            if (cred is { Length: > 0 } && !SecretRef.IsRef(cred)) throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: secretRef must be a reference ({SecretRef.Forms})");
            var hostKey = own.GetValueOrDefault("hostKeyRef") is { Length: > 0 } h ? h : b.Settings.GetValueOrDefault("hostKeyRef");
            partners[p] = new SftpPartner(p, host, port, hostKey is { Length: > 0 } ? hostKey : null, cred is { Length: > 0 } ? cred : null, own.GetValueOrDefault("user"), own);
        }
        if (partners.Count == 0) throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant} names no partner: set <partner>.host");
        Partners = partners;
        DefaultPartner = b.Settings.GetValueOrDefault("defaultPartner") is { Length: > 0 } d
            ? partners.ContainsKey(d) ? d : throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: defaultPartner {d} is not a partner of it")
            : partners.ContainsKey("centralBank") ? "centralBank" : partners.Count == 1 ? partners.Keys.Single()
            : throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant} names {partners.Count} partners and no defaultPartner");
    }

    /// <summary>The partner a folder names and the remote path in it.</summary>
    public (SftpPartner Partner, string Path) Resolve(string folder, string? name = null)
    {
        var rel = FileTransferAdapters.Relative(folder, name ?? "x");
        var parts = rel.Split('/');
        var partner = parts.Length > 1 && Partners.TryGetValue(parts[0], out var p) ? p : Partners[DefaultPartner];
        var rest = partner.Name == parts[0] && parts.Length > 1 ? parts[1..] : parts;
        var path = "/" + string.Join('/', name is null ? rest[..^1] : rest);
        return (partner, path);
    }

    public Task Put(string folder, string name, byte[] content, CancellationToken ct = default)
    {
        var (p, path) = Resolve(folder, name);
        return Use(p, ct, c =>
        {
            var dir = path[..path.LastIndexOf('/')];
            if (dir.Length > 0) EnsureDirectory(c, dir);
            var part = path + ".part";
            using (var s = new MemoryStream(content, writable: false)) c.UploadFile(s, part, canOverride: true);
            if (c.Exists(path)) c.DeleteFile(path);
            c.RenameFile(part, path);
            return 0;
        });
    }

    public Task<byte[]?> Get(string folder, string name, CancellationToken ct = default)
    {
        var (p, path) = Resolve(folder, name);
        return Use<byte[]?>(p, ct, c =>
        {
            if (!c.Exists(path)) return null;
            using var ms = new MemoryStream();
            c.DownloadFile(path, ms);
            return ms.ToArray();
        });
    }

    public Task<bool> Delete(string folder, string name, CancellationToken ct = default)
    {
        var (p, path) = Resolve(folder, name);
        return Use(p, ct, c =>
        {
            if (!c.Exists(path)) return false;
            c.DeleteFile(path);
            return true;
        });
    }

    public async Task<IReadOnlyList<string>> List(string folder, CancellationToken ct = default) => (await ListFiles(folder, ct)).Select(f => f.Name).ToList();

    public Task<IReadOnlyList<TransferFile>> ListFiles(string folder, CancellationToken ct = default)
    {
        var (p, path) = Resolve(folder);
        return Use<IReadOnlyList<TransferFile>>(p, ct, c =>
        {
            if (!c.Exists(path)) return [];
            return c.ListDirectory(path).Where(f => f.IsRegularFile && !f.Name.EndsWith(".part", StringComparison.Ordinal))
                .Select(f => new TransferFile(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero))).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        });
    }

    static void EnsureDirectory(SftpClient c, string dir)
    {
        var at = "";
        foreach (var part in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            at += "/" + part;
            if (!c.Exists(at)) c.CreateDirectory(at);
        }
    }

    /// <summary>
    /// Runs one operation on a session of the partner's pool: at most <c>sessions.max</c> at once, waiting at most <c>sessions.acquireSeconds</c>
    /// for one; a session that failed to connect or dropped is discarded and the operation tried again, up to <c>sessions.attempts</c> times.
    /// </summary>
    async Task<T> Use<T>(SftpPartner p, CancellationToken ct, Func<SftpClient, T> op)
    {
        var pool = pools.GetOrAdd(p.Name, _ => new Pool(Sessions.Max));
        if (!await pool.Slots.WaitAsync(Sessions.Acquire, ct))
            throw new FileTransferException($"No SFTP session to {p.Name} ({p.Host}) within {Sessions.Acquire.TotalSeconds:0} s: all {Sessions.Max} are in use");
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var c = pool.Take(Sessions.Idle);
                try
                {
                    c ??= await Open(p, ct);
                    var r = await Task.Run(() => op(c), ct);
                    if (Sessions.Reuse) pool.Give(c); else c.Dispose();
                    return r;
                }
                catch (Exception e) when (Transient(e) && attempt < Sessions.Attempts)
                {
                    c?.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
                }
                catch (Exception e) when (e is SshException or SocketException or IOException or TimeoutException)
                {
                    c?.Dispose();
                    throw new FileTransferException($"SFTP to {p.Name} ({p.Host}:{p.Port}) failed: {e.Message}", e);
                }
                catch
                {
                    c?.Dispose();
                    throw;
                }
            }
        }
        finally { pool.Slots.Release(); }
    }

    static bool Transient(Exception e) => e is SshConnectionException or SocketException or SshOperationTimeoutException or TimeoutException
        || (e is IOException && e is not FileNotFoundException);

    async Task<SftpClient> Open(SftpPartner p, CancellationToken ct)
    {
        if (p.HostKeyRef is null)
            throw new FileTransferException($"The {Name} binding of {Binding.Port} for {Binding.Tenant} pins no host key for {p.Name}: set {p.Name}.hostKeyRef (a host key is never trusted on first use)");
        if (p.CredentialRef is null)
            throw new FileTransferException($"The {Name} binding of {Binding.Port} for {Binding.Tenant} names no login for {p.Name}: set {p.Name}.secretRef or secretRef");
        var pinned = (await secrets.Resolve(p.HostKeyRef, ct)).Reveal().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Fingerprint).ToHashSet(StringComparer.Ordinal);
        if (pinned.Count == 0) throw new FileTransferException($"{p.HostKeyRef} holds no host key fingerprint for {p.Name}");
        var login = (await secrets.Resolve(p.CredentialRef, ct)).Reveal();
        var (user, auth) = Login(p, login);
        var info = new ConnectionInfo(p.Host, p.Port, user, auth) { Timeout = Sessions.Acquire };
        var c = new SftpClient(info) { KeepAliveInterval = Sessions.KeepAlive, OperationTimeout = TimeSpan.FromMinutes(5) };
        string? seen = null;
        c.HostKeyReceived += (_, e) =>
        {
            seen = "SHA256:" + e.FingerPrintSHA256.TrimEnd('=');
            e.CanTrust = pinned.Contains(seen);
        };
        try { await c.ConnectAsync(ct); }
        catch (SshConnectionException e) when (seen is not null && !pinned.Contains(seen))
        {
            c.Dispose();
            throw new FileTransferException($"The host key of {p.Name} ({p.Host}:{p.Port}) is {seen}, which {p.HostKeyRef} does not pin: nothing was sent", e);
        }
        catch (SshAuthenticationException e)
        {
            c.Dispose();
            throw new FileTransferException($"{p.Name} ({p.Host}:{p.Port}) refused the login {p.CredentialRef} names", e);
        }
        catch
        {
            c.Dispose();
            throw;
        }
        return c;
    }

    /// <summary>A fingerprint as `ssh-keygen -l` prints it, with or without its padding: compared without it.</summary>
    public static string Fingerprint(string s)
    {
        var f = s.Trim();
        if (!f.StartsWith("SHA256:", StringComparison.Ordinal)) throw new FileTransferException("A pinned host key is a SHA-256 fingerprint, written SHA256:<base64>");
        return "SHA256:" + f["SHA256:".Length..].TrimEnd('=');
    }

    static (string User, AuthenticationMethod Auth) Login(SftpPartner p, string secret)
    {
        string user, rest;
        if (p.User is { Length: > 0 } u) (user, rest) = (u, secret);
        else
        {
            var nl = secret.IndexOf('\n');
            var colon = secret.IndexOf(':');
            if (nl > 0 && (colon < 0 || nl < colon)) (user, rest) = (secret[..nl].TrimEnd('\r'), secret[(nl + 1)..]);
            else if (colon > 0) (user, rest) = (secret[..colon], secret[(colon + 1)..]);
            else throw new FileTransferException($"The login {p.CredentialRef} of {p.Name} is not <user>:<password> or <user> and a private key");
        }
        if (rest.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rest.Trim() + "\n"));
            return (user, new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(ms)));
        }
        return (user, new PasswordAuthenticationMethod(user, rest));
    }

    public void Dispose()
    {
        foreach (var pool in pools.Values) pool.Dispose();
        pools.Clear();
    }

    sealed class Pool(int max) : IDisposable
    {
        public readonly SemaphoreSlim Slots = new(max, max);
        readonly ConcurrentQueue<(SftpClient Client, DateTimeOffset At)> idle = new();

        /// <summary>An idle session still connected and used within the idle time; older ones are closed.</summary>
        public SftpClient? Take(TimeSpan idleFor)
        {
            while (idle.TryDequeue(out var x))
            {
                if (x.Client.IsConnected && DateTimeOffset.UtcNow - x.At < idleFor) return x.Client;
                x.Client.Dispose();
            }
            return null;
        }

        public void Give(SftpClient c) => idle.Enqueue((c, DateTimeOffset.UtcNow));

        public void Dispose()
        {
            while (idle.TryDequeue(out var x)) x.Client.Dispose();
        }
    }
}
