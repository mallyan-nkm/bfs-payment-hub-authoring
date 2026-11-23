using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TransactHub.Activities.Native;
using TransactHub.Ports;
using TransactHub.Store;

namespace TransactHub.FileApi;

// The File API's intake (R-09-82, R-09-83): every file the runtime receives comes in here, whether a channel or a corporate uploads
// it over HTTPS, a laptop drops it in an inbox folder, or the SFTP gateway picks it up from a partner. On arrival it is checked (size,
// type, duplicates by hash and by name), decrypted with the bank's decryption binding when it is encrypted
// (p.protect.decrypt-file.v1, on its native code-behind), kept once in the store with its signed SHA-256 (stored_file, file_content;
// R-09-64), the received bytes and the decrypted ones both, and then handed to the runtime's front door, which starts its payment or
// gives it to the payment it belongs to. Each step is a fact on the file's own chain (subject kind `file`). A refused file is kept
// too, with what refused it, so nothing that arrived is lost; it is never handed on.

/// <summary>A file as it arrived: whose, which message it is, from which channel, and its bytes.</summary>
public sealed record Arrival(string Tenant, string Environment, string Message, string FileName, byte[] Content, string Channel, string Actor,
    IReadOnlyDictionary<string, string>? Correlation = null);

/// <summary>What the intake did with a file.</summary>
public sealed record Received(string Status, string FileId, string Sha256, string? DecryptedFileId, string? Code, string Detail, FrontDoorResult? FrontDoor)
{
    public bool Accepted => Status == "accepted";
}

/// <summary>
/// The checks of one message's files, from the File API's settings (<c>FileApi:Messages:&lt;message&gt;:*</c>), over the defaults
/// (<c>FileApi:MaxBytes</c>): the largest file, the extensions it may have (an encrypted file may add <c>.gpg</c> or <c>.pgp</c>), the
/// pattern its name must match, and whether a name may come twice. The scheme declares none of these yet (Issue I-159): they are the
/// installation's, and a bank may set them per message.
/// </summary>
public sealed record FileRules(long MaxBytes, IReadOnlyList<string> Extensions, Regex? NamePattern, bool UniqueName)
{
    public const long DefaultMaxBytes = 50L * 1024 * 1024;
    public static readonly string[] EncryptedExtensions = [".gpg", ".pgp", ".asc"];

    public static FileRules For(IConfiguration cfg, string message)
    {
        var s = cfg.GetSection($"FileApi:Messages:{message}");
        long L(string? v, long d) => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : d;
        var max = L(s["MaxBytes"], L(cfg["FileApi:MaxBytes"], DefaultMaxBytes));
        var ext = (s["Extensions"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => e.StartsWith('.') ? e : "." + e).ToList();
        var pattern = s["NamePattern"] is { Length: > 0 } p ? new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) : null;
        return new FileRules(max, ext, pattern, (s["UniqueName"] ?? cfg["FileApi:UniqueName"] ?? "true") != "false");
    }

    /// <summary>The name without an encryption extension, as its checks read it.</summary>
    public static string Plain(string name) => EncryptedExtensions.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } e ? name[..^e.Length] : name;

    /// <summary>The first check the file fails, or null.</summary>
    public (string Code, string Why)? Fail(string fileName, byte[] content)
    {
        if (content.Length == 0) return ("file.Empty", "The file is empty");
        if (content.LongLength > MaxBytes) return ("file.TooLarge", $"The file is {content.LongLength} bytes; at most {MaxBytes} are taken");
        var name = Plain(fileName);
        if (Extensions.Count > 0 && !Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            return ("file.WrongType", $"A file of this message ends in {string.Join(" or ", Extensions)}");
        if (NamePattern is not null && !NamePattern.IsMatch(name)) return ("file.BadName", "The file's name does not follow the message's naming rule");
        // A text file of a file scheme is text: a NUL byte means it is not the file it says it is, unless it is encrypted.
        if (!OpenPgp.IsEncrypted(content) && Array.IndexOf(content, (byte)0) >= 0 && Extensions.Count > 0)
            return ("file.WrongType", "The file holds binary content");
        return null;
    }
}

/// <summary>The decryption binding of each bank, through the native code-behind of <c>p.protect.decrypt-file.v1</c>.</summary>
public sealed class Decryptor(PortBindings bindings, TransactHub.Security.ISecretResolver? secrets = null)
{
    sealed class Kept : INativeFiles
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        public string Keep(string direction, string kind, string fileName, byte[] content)
        {
            var sha = FileStore.Sha256Hex(content);
            Files[sha] = content;
            return sha;
        }
        public byte[]? Get(string sha256) => Files.GetValueOrDefault(sha256);
    }

    /// <summary>Whether the bank binds <c>p.port.decryption.v1</c> at this moment.</summary>
    public bool Binds(string tenant, DateTimeOffset at)
    {
        try { bindings.InForce(tenant, OpenPgpDecryption.Port, at); return true; }
        catch (PortBindingException) { return false; }
    }

    /// <summary>Decrypts a received file: the plain bytes and the file name without its encryption extension, or what refused it.</summary>
    public (byte[]? Plain, string Name, string? Refused) Decrypt(string tenant, string fileName, byte[] cipher, DateTimeOffset at)
    {
        var files = new Kept();
        var activity = new DecryptFile(files, new BoundAdapters<IFileDecryption>(bindings, OpenPgpDecryption.Port, b => FileProtectionAdapters.Decryption(b, secrets)));
        var view = new JsonObject { ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["fileName"] = fileName, ["contentBase64"] = Convert.ToBase64String(cipher) } } };
        NativeResult r;
        try { r = activity.Run(new NativeAttempt(activity.Name, null, view, new JsonObject(), at, tenant)); }
        catch (NativeFailure e) { return (null, fileName, e.Message); }
        if (r.Outcome != "decrypted") return (null, fileName, (string?)r.Writes["facts.decryption"]?["error"] ?? r.Outcome);
        var sha = (string)r.Writes["facts.decryption"]!["sha256"]!;
        var name = (string?)r.Writes["facts.decryption"]!["fileName"] is { Length: > 0 } n ? n : FileRules.Plain(fileName);
        return (files.Get(sha), name, null);
    }
}

/// <summary>The File API's intake: check, decrypt, keep once, hand to the front door.</summary>
public sealed class FileIntake(StoreOptions store, System.Security.Cryptography.ECDsa signingKey, IConfiguration cfg, Decryptor decryptor, RuntimeFrontDoor? frontDoor, TimeProvider? clock = null)
{
    public const string FileKind = "file";
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<Received> Receive(Arrival a, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var rules = FileRules.For(cfg, a.Message);
        var sha = FileStore.Sha256Hex(a.Content);
        var scheme = WorkerIntake.ScopeOf(a.Message);

        // 1. What the file is, by the dispatch table of the bank's active distribution: a file no scheme names is refused.
        MessageEntry? entry = null;
        string? refusedCode = null, refused = null;
        if (frontDoor is not null)
        {
            try
            {
                entry = await frontDoor.Classify(a.Tenant, a.Environment, a.Message, ct);
                if (entry.Direction != "in") (refusedCode, refused) = ("file.NotReceived", $"{a.Message} is a message the bank sends, not one it receives");
            }
            catch (FrontDoorRefused e) { (refusedCode, refused) = (e.Code, e.Message); }
        }
        // 2. The checks of its message: size, type, name.
        if (refused is null && rules.Fail(a.FileName, a.Content) is { } f) (refusedCode, refused) = f;
        // 3. Duplicates: the same bytes, or the same name where the message's names are unique, kept before for the bank.
        await using var db = StoreDbContext.Open(store);
        if (refused is null)
        {
            var plainName = FileRules.Plain(a.FileName);
            var earlier = await db.StoredFiles.AsNoTracking()
                .Where(x => x.Tenant == a.Tenant && x.Direction == "in" && x.Kind == a.Message && (x.ContentHash == sha || (rules.UniqueName && (x.FileName == a.FileName || x.FileName == plainName))))
                .OrderBy(x => x.At).FirstOrDefaultAsync(ct);
            if (earlier is not null)
                (refusedCode, refused) = earlier.ContentHash == sha
                    ? ("file.Duplicate", $"The same file was received before, as {earlier.Id}")
                    : ("file.DuplicateName", $"A file of this name was received before, as {earlier.Id}");
        }

        // 4. Decrypted, when it is encrypted, with the bank's own key (p.protect.decrypt-file.v1).
        byte[]? plain = null;
        var plainFileName = a.FileName;
        if (refused is null && OpenPgp.IsEncrypted(a.Content))
        {
            if (!decryptor.Binds(a.Tenant, now)) (refusedCode, refused) = ("file.NoDecryption", $"The file is encrypted and {OpenPgpDecryption.Port} is not bound for {a.Tenant}");
            else
            {
                var d = decryptor.Decrypt(a.Tenant, a.FileName, a.Content, now);
                if (d.Plain is null) (refusedCode, refused) = ("file.NotDecrypted", d.Refused ?? "The file could not be decrypted");
                else (plain, plainFileName) = (d.Plain, d.Name);
            }
            if (refused is null && rules.Fail(plainFileName, plain!) is { } g) (refusedCode, refused) = g;
        }

        // 5. Whose it is: a start names a new payment; a message for a running payment is correlated before it is kept.
        string? paymentId = null;
        if (refused is null && frontDoor is not null && entry is not null)
        {
            try
            {
                if (entry.Kind == "start") paymentId = RuntimeFrontDoor.NewPaymentId(entry.Scheme);
                else if (entry.Kind == "deliver")
                {
                    var keys = new Dictionary<string, string>(a.Correlation ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                    keys.TryAdd("fileStem", RuntimeFrontDoor.Stem(plainFileName));
                    paymentId = await frontDoor.Correlate(a.Tenant, keys, ct);
                    if (paymentId is null) (refusedCode, refused) = ("frontdoor.Unmatched", $"No payment the front door started matches this {a.Message}");
                }
                else (refusedCode, refused) = ("frontdoor.NotRouted", $"{a.Message} neither starts a payment nor answers one");
            }
            catch (FrontDoorRefused e) { (refusedCode, refused) = (e.Code, e.Message); }
        }

        // 6. Kept once, as received; the decrypted file beside it. A refused file is kept under its own kind, never handed on.
        var files = new FileStore(db, signingKey, clock);
        var kind = refused is null ? a.Message : $"refused:{a.Message}";
        var row = await files.Keep(new FileToKeep(a.Tenant, scheme, "in", kind, a.FileName, paymentId, null, $"file-api:{a.Channel}"), a.Content, ct);
        StoredFileRow? dec = plain is null ? null
            : await files.Keep(new FileToKeep(a.Tenant, scheme, "in", $"{a.Message}:decrypted", plainFileName, paymentId, null, "p.protect.decrypt-file.v1"), plain, ct);
        var log = new FactLog(db, clock);
        await log.Append(row.Id, FileKind, new NewFact(refused is null ? "file.received" : "file.refused", refusedCode, new JsonObject
        {
            ["message"] = a.Message, ["channel"] = a.Channel, ["fileName"] = a.FileName, ["sha256"] = sha, ["size"] = a.Content.LongLength,
            ["payment"] = paymentId, ["decrypted"] = dec?.Id, ["why"] = refused,
        }, Actor: a.Actor), ct);
        if (refused is not null) return new Received("refused", row.Id, sha, dec?.Id, refusedCode, refused, null);
        if (frontDoor is null || entry is null) return new Received("accepted", row.Id, sha, dec?.Id, null, "kept; no front door is configured", null);

        // 7. The front door: the payment starts, or the message reaches the payment it belongs to.
        var given = new FrontDoorFile(a.Tenant, a.Environment, a.Message, plainFileName, plain ?? a.Content, dec?.Id ?? row.Id, dec?.ContentHash ?? sha, a.Correlation);
        try
        {
            var r = entry.Kind == "start" ? await frontDoor.Start(given, paymentId!, ct) : await frontDoor.Deliver(given, paymentId!, ct);
            await log.Append(row.Id, FileKind, new NewFact("file.handed", r.Disposition, new JsonObject { ["payment"] = r.PaymentId, ["distribution"] = r.Distribution, ["workflow"] = r.Workflow, ["detail"] = r.Detail }, Actor: "front-door"), ct);
            return new Received("accepted", row.Id, sha, dec?.Id, null, r.Detail, r);
        }
        catch (FrontDoorRefused e)
        {
            await log.Append(row.Id, FileKind, new NewFact("file.not-handed", e.Code, new JsonObject { ["payment"] = paymentId, ["why"] = e.Message }, Actor: "front-door"), ct);
            return new Received("refused", row.Id, sha, dec?.Id, e.Code, e.Message, null);
        }
    }
}
