using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TransactHub.Activities.Platform.Posting;
using TransactHub.Ports;

namespace TransactHub.Activities.Native;

/// <summary>A port's adapters per tenant: the binding in force at the engine's time, built once per binding. None in force is refused.</summary>
public sealed class BoundAdapters<T>(PortBindings bindings, string port, Func<PortBinding, T> create) where T : class
{
    readonly ConcurrentDictionary<PortBinding, T> built = new(ReferenceEqualityComparer.Instance);
    public string Port => port;

    public (T Adapter, PortBinding Binding) For(string? tenant, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(tenant)) throw new NativeFailure("NoTenant", $"The attempt names no tenant; {port} is bound per tenant and never defaulted", false);
        try
        {
            var b = bindings.InForce(tenant, port, at);
            return (built.GetOrAdd(b, create), b);
        }
        catch (PortBindingException e) { throw new NativeFailure("PortNotBound", e.Message, false); }
    }
}

/// <summary>
/// The image's file protection (R-12-12, R-12-13; Issue I-117): a file encrypted for a recipient through the bank's binding of
/// <c>p.port.encryption.v1</c>, or decrypted through its binding of <c>p.port.decryption.v1</c>, each file kept by the host. The platform
/// module's <c>p.protect.*</c> activities and the File API's intake both call it.
/// </summary>
public sealed class FileProtectionPort(PortBindings bindings, TransactHub.Security.ISecretResolver? secrets = null)
{
    readonly BoundAdapters<IFileEncryption> encryption = new(bindings, OpenPgpEncryption.Port, b => FileProtectionAdapters.Encryption(b, secrets));
    readonly BoundAdapters<IFileDecryption> decryption = new(bindings, OpenPgpDecryption.Port, b => FileProtectionAdapters.Decryption(b, secrets));

    /// <summary>The plain file a host keeps, encrypted for the recipient and kept as <paramref name="keepAs"/>: its SHA-256, size and the adapter.</summary>
    public (string Sha256, int Bytes, string Adapter) Encrypt(INativeFiles files, string? tenant, DateTimeOffset at, string sha256, string recipientKey, string fileName, string keepAs)
    {
        var plain = files.Get(sha256) ?? throw new NativePortRefused(OpenPgpEncryption.Port, $"The host holds no file {sha256} to encrypt");
        var (enc, b) = encryption.For(tenant, at);
        byte[] cipher;
        try { cipher = enc.Encrypt(plain, recipientKey, fileName).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is PortBindingException or FileProtectionException) { throw new NativePortRefused(OpenPgpEncryption.Port, e.Message); }
        catch (TransactHub.Security.SecretUnavailable e) { throw new NativeFailure("SecretUnavailable", e.Message, false); }
        return (files.Keep("out", "encrypted", keepAs, cipher), cipher.Length, b.Adapter);
    }

    /// <summary>A received file, checked to be an OpenPGP message, kept as received, decrypted and kept as <paramref name="keepAs"/>.</summary>
    public (string Sha256, string CipherSha256, int Bytes, string Adapter) Decrypt(INativeFiles files, string? tenant, DateTimeOffset at, byte[] cipher, string fileName, string keepAs)
    {
        if (!OpenPgp.IsEncrypted(cipher)) throw new NativePortRefused(OpenPgpDecryption.Port, "The inbound file is not an OpenPGP encrypted message");
        var (dec, b) = decryption.For(tenant, at);
        var received = files.Keep("in", "received", fileName, cipher);
        byte[] plain;
        try { plain = dec.Decrypt(cipher).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is PortBindingException or FileProtectionException) { throw new NativePortRefused(OpenPgpDecryption.Port, e.Message); }
        catch (TransactHub.Security.SecretUnavailable e) { throw new NativeFailure("SecretUnavailable", e.Message, false); }
        return (files.Keep("in", "decrypted", keepAs, plain), received, plain.Length, b.Adapter);
    }
}

/// <summary>
/// The bank's ports as a native module asks them (native contract 1.1.0, R-09-125): each by the image's adapter of the binding in force
/// for the attempt's tenant at its time. The ports and operations it answers are the contract's (<see cref="INativePorts"/>): the
/// platform's only. A scheme's port is answered by the scheme's module on <see cref="NativeBoundPorts"/> (contract 2.0.0, R-09-263).
/// </summary>
public sealed class NativePorts(PortBindings bindings, INativeFiles files, RegisterReader? registers = null, TransactHub.Security.ISecretResolver? secrets = null, Func<PortBinding, IFileTransfer>? transferOf = null) : INativePorts
{
    readonly FileProtectionPort protection = new(bindings, secrets);
    readonly BoundAdapters<IPartyRegister> parties = new(bindings, PartyRegisterAdapters.Port, b => PartyRegisterAdapters.Create(b, registers));

    public NativePortAnswer Ask(NativeAttempt a, string port, string operation, JsonObject request)
    {
        string S(string k) => (string?)request[k] ?? "";
        switch ($"{port} {operation}")
        {
            case $"{PartyRegisterAdapters.Port} lookup":
            {
                var (r, b) = parties.For(a.Tenant, a.Now);
                if (S("value").Length == 0) return new(b.Adapter, new JsonObject { ["found"] = false });
                RegisteredParty? party;
                try { party = r.Lookup(a.Tenant!, S("kind"), S("field"), S("value"), a.Now).GetAwaiter().GetResult(); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or TimeoutException) { throw new NativeFailure("TransientError", $"The party register did not answer: {e.GetType().Name}", true); }
                return new(b.Adapter, party is null ? new JsonObject { ["found"] = false } : new JsonObject { ["found"] = true, ["key"] = party.Key, ["record"] = party.Record.DeepClone() });
            }
            case $"{OpenPgpEncryption.Port} encrypt":
            {
                var (sha, bytes, adapter) = protection.Encrypt(files, a.Tenant, a.Now, S("sha256"), S("recipientKey"), S("fileName"), S("keepAs"));
                return new(adapter, new JsonObject { ["sha256"] = sha, ["bytes"] = bytes });
            }
            case $"{OpenPgpDecryption.Port} decrypt":
            {
                byte[] cipher;
                try { cipher = Convert.FromBase64String(S("contentBase64")); }
                catch (FormatException) { throw new NativePortRefused(port, "The inbound file's content is not Base64"); }
                var (sha, received, bytes, adapter) = protection.Decrypt(files, a.Tenant, a.Now, cipher, S("fileName"), S("keepAs"));
                return new(adapter, new JsonObject { ["sha256"] = sha, ["cipherSha256"] = received, ["bytes"] = bytes });
            }
            default:
            {
                // A port the bank binds to the File API (adapter file-transfer, R-09-160): the file the host kept is sent through the
                // bank's file-transfer binding, which on a runtime host is the File API's outbox (R-09-138).
                if (!string.IsNullOrEmpty(a.Tenant) && Bound(a.Tenant, port, a.Now) is { Adapter: PortFileTransfer.Name })
                    return SendFile(a, port, operation, request);
                throw new NativeFailure("PortNotBound", $"The runtime image answers no {operation} of {port} for a native module", false);
            }
        }
    }

    PortBinding? Bound(string tenant, string port, DateTimeOffset at)
    {
        try { return bindings.InForce(tenant, port, at); }
        catch (PortBindingException) { return null; }
    }

    /// <summary>A file the host kept (<c>sha256</c>), sent as <c>fileName</c> on a port bound to <c>file-transfer</c>, for the request's payment.</summary>
    NativePortAnswer SendFile(NativeAttempt a, string port, string operation, JsonObject request)
    {
        string S(string k) => (string?)request[k] ?? "";
        var content = files.Get(S("sha256")) ?? throw new NativePortRefused(port, $"The host holds no file {S("sha256")} to send");
        var name = S("fileName").Length > 0 ? S("fileName") : throw new NativePortRefused(port, "The request names no fileName");
        var file = new PortFile(name, content, S("paymentId") is { Length: > 0 } p ? p : null, S("leg") is { Length: > 0 } l ? l : null,
            S("scheme") is { Length: > 0 } sc ? sc : string.Join('.', port.Split('.').Take(2)), a.Activity,
            // the scheme message the send says the file is (R-09-234, Issue I-310): its kind, and its folder where the binding names one
            S("message") is { Length: > 0 } msg ? msg : null);
        var sent = NativeBoundPorts.PutFile(bindings, a, port, operation, file, transferOf, secrets);
        return new(PortFileTransfer.Name, new JsonObject { ["sent"] = true, ["via"] = sent.Via, ["folder"] = sent.Folder, ["fileName"] = sent.FileName, ["kind"] = sent.Kind, ["bytes"] = sent.Bytes });
    }
}

/// <summary>
/// What the runtime image gives a distribution's native modules (R-09-122, R-09-125, R-09-261): the host's files, the bank's core banking
/// and the platform's other ports through their bindings in force, and the bound ports a module builds its own adapters on. It hands out
/// no type of the image (contract 2.0.0 removed <c>Service&lt;T&gt;</c>, Issue I-226). Contributions are the distribution table's, so this one
/// keeps none; <paramref name="contributions"/> reads them, for the gate a module contributed on a binding (R-09-264).
/// </summary>
public sealed class NativeHostServices(string? distribution, Action<string, string, byte[]> keep, INativeFiles files, PortBindings bindings, CoreBankingResolver coreBanking,
    RegisterReader? registers = null, TransactHub.Security.ISecretResolver? secrets = null, Func<string, object?>? contributions = null, Func<PortBinding, IFileTransfer>? transferOf = null) : INativeServices
{
    public string? Distribution => distribution;
    public void KeepRendered(string sha256, string fileName, byte[] content) => keep(sha256, fileName, content);
    public void Contribute(string key, object value) => throw new InvalidOperationException("A module contributes through its distribution's table");
    public object? Contribution(string key) => contributions?.Invoke(key);
    public INativeCoreBanking CoreBanking { get; } = new NativeCoreBanking(coreBanking, contributions);
    public INativePorts Ports { get; } = new NativePorts(bindings, files, registers, secrets, transferOf);
    public INativeBoundPorts BoundPorts { get; } = new NativeBoundPorts(bindings, secrets, transferOf: transferOf);
    public INativeFiles Files => files;
}

/// <summary>The host's files in memory, by SHA-256: for a test, or a caller that keeps what it is given itself.</summary>
public sealed class MemoryNativeFiles : INativeFiles
{
    public readonly ConcurrentDictionary<string, (string Direction, string Kind, string Name, byte[] Content)> All = new(StringComparer.OrdinalIgnoreCase);

    public string Keep(string direction, string kind, string fileName, byte[] content)
    {
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        All[sha] = (direction, kind, fileName, content);
        return sha;
    }

    public byte[]? Get(string sha256) => All.TryGetValue(sha256, out var f) ? f.Content : null;
}
