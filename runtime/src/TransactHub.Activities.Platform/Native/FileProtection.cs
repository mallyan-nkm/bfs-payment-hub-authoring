using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Ports;

namespace TransactHub.Activities.Native;

/// <summary>
/// The files a native host keeps (Issue I-97): what an activity renders, encrypts, receives or decrypts, by SHA-256. The host says where
/// (its memory, and the store when it has one) and whose (the attempt's tenant, payment and leg).
/// </summary>
public interface INativeFiles
{
    /// <summary>Keeps a file; <paramref name="direction"/> is "in" (received) or "out" (sent). Returns its SHA-256.</summary>
    string Keep(string direction, string kind, string fileName, byte[] content);
    /// <summary>A file kept earlier, by its SHA-256; null when the host holds none.</summary>
    byte[]? Get(string sha256);
}

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
/// <c>p.protect.encrypt-file.v1</c> on the runtime (R-03-24, R-12-13; Issue I-117): the file the step before rendered
/// (<c>messages.rendered</c>, kept by the host) encrypted for the recipient the bank names (<c>recipientKey</c>) through its binding of
/// <c>p.port.encryption.v1</c> (OpenPGP). The encrypted file is kept as the one that is sent; the context keeps its name, hash and size,
/// never its bytes. A key id the binding does not name, or a key that is not a public key, refuses the file: it is not sent.
/// </summary>
public sealed class EncryptFile(INativeFiles files, BoundAdapters<IFileEncryption> encryption) : INativeActivity
{
    public string Name => "p.protect.encrypt-file.v1";
    public const string EncryptedSuffix = ".gpg";

    static NativeResult Refused(string key, string why) => new("refused", "NARR", new JsonObject
    {
        ["facts.encryption"] = new JsonObject { ["outcome"] = "refused", ["result"] = "refused", ["recipientKey"] = key, ["error"] = why },
    });

    public NativeResult Run(NativeAttempt a)
    {
        var key = NativeContext.Param(a.Params, "recipientKey");
        var rendered = NativeContext.Node(a.View, "messages.rendered");
        var sha = NativeContext.S(rendered, "sha256");
        var name = NativeContext.S(rendered, "fileName");
        if (sha.Length == 0) return Refused(key, "No file was rendered to encrypt");
        var plain = files.Get(sha);
        if (plain is null) return Refused(key, $"The host holds no file {sha} to encrypt");
        var (enc, b) = encryption.For(a.Tenant, a.Now);
        byte[] cipher;
        try { cipher = enc.Encrypt(plain, key, name).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is PortBindingException or FileProtectionException) { return Refused(key, e.Message); }
        catch (TransactHub.Security.SecretUnavailable e) { throw new NativeFailure("SecretUnavailable", e.Message, false); }
        var sent = name + EncryptedSuffix;
        var cipherSha = files.Keep("out", "encrypted", sent, cipher);
        return new NativeResult("encrypted", null, new JsonObject
        {
            ["facts.encryption"] = new JsonObject
            {
                ["outcome"] = "encrypted", ["result"] = "encrypted", ["adapter"] = b.Adapter, ["recipientKey"] = key,
                ["fileName"] = sent, ["sha256"] = cipherSha, ["bytes"] = cipher.Length, ["plainSha256"] = sha,
            },
        });
    }
}

/// <summary>
/// <c>p.protect.decrypt-file.v1</c> on the runtime (R-12-12; Issue I-117): a file the bank received encrypted to its own key, decrypted
/// through its binding of <c>p.port.decryption.v1</c> (<c>openpgp</c>, or <c>gnupg</c> with the source's key folder) before it is read.
/// The inbound message carries the file as <c>contentBase64</c> (binary) or as armoured text in <c>payload</c>. Both the file as received
/// and the file as decrypted are kept; the context's inbound message becomes the decrypted text.
/// </summary>
public sealed class DecryptFile(INativeFiles files, BoundAdapters<IFileDecryption> decryption) : INativeActivity
{
    public string Name => "p.protect.decrypt-file.v1";

    static NativeResult Refused(string why) => new("refused", "NARR", new JsonObject
    {
        ["facts.decryption"] = new JsonObject { ["outcome"] = "refused", ["result"] = "refused", ["error"] = why },
    });

    public NativeResult Run(NativeAttempt a)
    {
        var inbound = NativeContext.Node(a.View, "messages.inbound") as JsonObject;
        var name = NativeContext.S(inbound, "fileName");
        var b64 = NativeContext.S(inbound, "contentBase64");
        byte[] cipher;
        try { cipher = b64.Length > 0 ? Convert.FromBase64String(b64) : Encoding.UTF8.GetBytes(NativeContext.S(inbound, "payload")); }
        catch (FormatException) { return Refused("The inbound file's content is not Base64"); }
        if (cipher.Length == 0) return Refused("There is no inbound file to decrypt");
        if (!OpenPgp.IsEncrypted(cipher)) return Refused("The inbound file is not an OpenPGP encrypted message");
        var (dec, b) = decryption.For(a.Tenant, a.Now);
        var received = files.Keep("in", "received", name.Length > 0 ? name : "inbound", cipher);
        byte[] plain;
        try { plain = dec.Decrypt(cipher).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is PortBindingException or FileProtectionException) { return Refused(e.Message); }
        catch (TransactHub.Security.SecretUnavailable e) { throw new NativeFailure("SecretUnavailable", e.Message, false); }
        var plainName = name.EndsWith(EncryptFile.EncryptedSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^EncryptFile.EncryptedSuffix.Length] : name;
        var plainSha = files.Keep("in", "decrypted", plainName.Length > 0 ? plainName : "inbound", plain);
        var msg = inbound?.DeepClone() as JsonObject ?? new JsonObject();
        msg.Remove("contentBase64");
        msg["payload"] = Encoding.UTF8.GetString(plain);
        if (plainName.Length > 0) msg["fileName"] = plainName;
        return new NativeResult("decrypted", null, new JsonObject
        {
            ["messages.inbound"] = msg,
            ["facts.decryption"] = new JsonObject
            {
                ["outcome"] = "decrypted", ["result"] = "decrypted", ["adapter"] = b.Adapter, ["fileName"] = plainName,
                ["sha256"] = plainSha, ["cipherSha256"] = received, ["bytes"] = plain.Length,
            },
        });
    }
}

/// <summary>The file activities, as the native host registers them, on the banks' bindings.</summary>
public static class FileProtectionActivities
{
    public static IReadOnlyList<INativeActivity> All(PortBindings bindings, INativeFiles files, TransactHub.Security.ISecretResolver? secrets = null) =>
    [
        new EncryptFile(files, new BoundAdapters<IFileEncryption>(bindings, OpenPgpEncryption.Port, b => FileProtectionAdapters.Encryption(b, secrets))),
        new DecryptFile(files, new BoundAdapters<IFileDecryption>(bindings, OpenPgpDecryption.Port, b => FileProtectionAdapters.Decryption(b, secrets))),
    ];
}
