using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Platform.Native;

/// <summary>
/// <c>p.protect.encrypt-file.v1</c> on the runtime (R-03-24, R-12-13; Issue I-117): the file the step before rendered
/// (<c>messages.rendered</c>, kept by the host) encrypted for the recipient the bank names (<c>recipientKey</c>) through its binding of
/// <c>p.port.encryption.v1</c> (OpenPGP, the image's adapter). The encrypted file is kept as the one that is sent; the context keeps its
/// name, hash and size, never its bytes. A key id the binding does not name, or a key that is not a public key, refuses the file: it is
/// not sent.
/// </summary>
public sealed class EncryptFile(INativeFiles files, INativePorts ports) : INativeActivity
{
    public const string Port = "p.port.encryption.v1";
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
        if (files.Get(sha) is null) return Refused(key, $"The host holds no file {sha} to encrypt");
        var sent = name + EncryptedSuffix;
        NativePortAnswer answer;
        try { answer = ports.Ask(a, Port, "encrypt", new JsonObject { ["sha256"] = sha, ["recipientKey"] = key, ["fileName"] = name, ["keepAs"] = sent }); }
        catch (NativePortRefused e) { return Refused(key, e.Message); }
        return new NativeResult("encrypted", null, new JsonObject
        {
            ["facts.encryption"] = new JsonObject
            {
                ["outcome"] = "encrypted", ["result"] = "encrypted", ["adapter"] = answer.Adapter, ["recipientKey"] = key,
                ["fileName"] = sent, ["sha256"] = (string?)answer.Body["sha256"], ["bytes"] = answer.Body["bytes"]?.DeepClone(), ["plainSha256"] = sha,
            },
        });
    }
}

/// <summary>
/// <c>p.protect.decrypt-file.v1</c> on the runtime (R-12-12; Issue I-117): a file the bank received encrypted to its own key, decrypted
/// through its binding of <c>p.port.decryption.v1</c> (<c>openpgp</c>, or <c>gnupg</c> with the source's key folder; the image's adapters)
/// before it is read. The inbound message carries the file as <c>contentBase64</c> (binary) or as armoured text in <c>payload</c>. Both the
/// file as received and the file as decrypted are kept; the context's inbound message becomes the decrypted text.
/// </summary>
public sealed class DecryptFile(INativeFiles files, INativePorts ports) : INativeActivity
{
    public const string Port = "p.port.decryption.v1";
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
        var plainName = name.EndsWith(EncryptFile.EncryptedSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^EncryptFile.EncryptedSuffix.Length] : name;
        NativePortAnswer answer;
        // The image checks the file is an OpenPGP message, keeps it as received, decrypts it and keeps the plain file.
        try { answer = ports.Ask(a, Port, "decrypt", new JsonObject { ["contentBase64"] = Convert.ToBase64String(cipher), ["fileName"] = name.Length > 0 ? name : "inbound", ["keepAs"] = plainName.Length > 0 ? plainName : "inbound" }); }
        catch (NativePortRefused e) { return Refused(e.Message); }
        var plainSha = (string?)answer.Body["sha256"] ?? "";
        var plain = files.Get(plainSha) ?? throw new NativeFailure("Failure", $"The host kept no decrypted file {plainSha}", false);
        var msg = inbound?.DeepClone() as JsonObject ?? new JsonObject();
        msg.Remove("contentBase64");
        msg["payload"] = Encoding.UTF8.GetString(plain);
        if (plainName.Length > 0) msg["fileName"] = plainName;
        return new NativeResult("decrypted", null, new JsonObject
        {
            ["messages.inbound"] = msg,
            ["facts.decryption"] = new JsonObject
            {
                ["outcome"] = "decrypted", ["result"] = "decrypted", ["adapter"] = answer.Adapter, ["fileName"] = plainName,
                ["sha256"] = plainSha, ["cipherSha256"] = (string?)answer.Body["cipherSha256"], ["bytes"] = plain.Length,
            },
        });
    }
}
