using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The file-transfer port as the runtime reaches it to send a kept file again (R-05-58). A put is idempotent by the file's id:
/// the same file put twice reaches the receiver once.
/// </summary>
public interface IFileTransferPort
{
    /// <summary>Puts the file; answers what happened (<c>sent</c>, or <c>already there</c> when the receiver has these bytes).</summary>
    Task<string> Put(string fileId, string fileName, byte[] content, CancellationToken ct = default);

    /// <summary>Puts a payment's file, whose tenant and payment the port is told (the File API keeps the file under them).</summary>
    Task<string> Put(string tenant, string subjectId, string fileId, string fileName, byte[] content, CancellationToken ct = default) => Put(fileId, fileName, content, ct);
}

/// <summary>
/// The file-transfer port through the File API (R-09-84, Issue I-129): the file goes to its outbox for the payment, and the File API
/// delivers it on the bank's binding of p.port.file-transfer.v1 (the SFTP gateway, a directory), recording each delivery; a file it
/// delivered before is sent again by its resend. Settings: the File API's URL, the folder of the binding to put the file in, and the
/// host's client-credentials token by reference.
/// </summary>
public sealed class FileApiTransferPort(Uri fileApi, string folder, Func<CancellationToken, Task<string>> token, HttpClient? http = null) : IFileTransferPort
{
    readonly HttpClient http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

    public Task<string> Put(string fileId, string fileName, byte[] content, CancellationToken ct = default) =>
        throw new InvalidOperationException("The File API keeps a file under its tenant and payment: name them");

    async Task<JsonObject> Post(string path, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(fileApi, path)) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await token(ct));
        using var res = await http.SendAsync(req, ct);
        var o = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject ?? new JsonObject();
        if ((int)res.StatusCode >= 400 && (int)res.StatusCode != 502) throw new ActionRefused(502, $"The File API answered {(int)res.StatusCode}: {(string?)o["error"]}", OpsCodes.NoFileKept);
        return o;
    }

    public async Task<string> Put(string tenant, string subjectId, string fileId, string fileName, byte[] content, CancellationToken ct = default)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? fileId : fileName);
        var q = await Post("outbox", new JsonObject
        {
            ["tenant"] = tenant, ["scheme"] = "p", ["kind"] = "resend", ["folder"] = folder, ["fileName"] = name,
            ["contentBase64"] = Convert.ToBase64String(content), ["paymentId"] = subjectId, ["activity"] = "resend-file",
        }, ct);
        var delivery = (string?)q["delivery"];
        if (delivery != "delivered before") return delivery == "delivered" ? "sent" : delivery ?? "queued";
        var r = await Post($"files/{Uri.EscapeDataString((string)q["file"]!)}/resend", new JsonObject { ["actionId"] = $"{fileId}:{Guid.NewGuid():N}" }, ct);
        return (string?)r["result"] == "resent" ? "sent" : (string?)r["result"] ?? "failed";
    }
}

/// <summary>
/// A file-transfer port that is a folder the scheme's gateway collects from (a mounted share or the outbox folder of an SFTP
/// gateway): the file is written under its own name, atomically; a file of that name with the same bytes is left as it is.
/// </summary>
public sealed class FolderFileTransfer(string folder) : IFileTransferPort
{
    public async Task<string> Put(string fileId, string fileName, byte[] content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? fileId : fileName);
        var target = Path.Combine(folder, name);
        if (File.Exists(target) && SHA256.HashData(await File.ReadAllBytesAsync(target, ct)).SequenceEqual(SHA256.HashData(content))) return "already there";
        var part = target + ".part";
        await File.WriteAllBytesAsync(part, content, ct);
        File.Move(part, target, overwrite: true);
        return "sent";
    }
}

/// <summary>The File API port of a host, when its settings name one: <c>&lt;section&gt;:FileApi:Url</c>, <c>:Folder</c>, <c>:TokenRef</c>.</summary>
public static class FileApiPort
{
    public static IFileTransferPort? FromConfiguration(Func<string, string?> cfg, string section)
    {
        if (cfg($"{section}:FileApi:Url") is not { Length: > 0 } url) return null;
        var folder = cfg($"{section}:FileApi:Folder") ?? throw new InvalidOperationException($"Set {section}:FileApi:Folder: the folder of the bank's file-transfer binding a resend goes to");
        var tokenRef = cfg($"{section}:FileApi:TokenRef") ?? throw new InvalidOperationException($"Set {section}:FileApi:TokenRef: this host's token for the File API, by reference");
        var secrets = TransactHub.Security.SecretResolver.FromEnvironment(cfg);
        return new FileApiTransferPort(new Uri(url.EndsWith('/') ? url : url + "/"), folder, async ct => (await secrets.Resolve(tokenRef, ct)).Reveal());
    }
}

/// <summary>
/// Resend a file (R-05-58, the troubleshooting action <c>resend-file</c>): the same bytes the payment's exchange sent, as the store
/// kept them (<c>PaymentStore.KeepMessage</c>: never rendered or signed again), put on the file-transfer port. Each resend is a
/// fact on the payment's chain (kind <c>message</c>, code <c>resent</c>) with the file's id and hash; an action carried out twice
/// sends once.
/// </summary>
public sealed class FileResend(StoreOptions store, IFileTransferPort port, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<JsonObject> Resend(string subjectId, string subjectKind, string? fileId, string actor, string actionId, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var q = db.Messages.AsNoTracking().Where(m => m.SubjectId == subjectId && m.Direction == "Outward");
        var file = fileId is null ? await q.OrderByDescending(m => m.At).FirstOrDefaultAsync(ct) : await q.FirstOrDefaultAsync(m => m.Id == fileId, ct);
        if (file is null) throw new ActionRefused(409, fileId is null ? $"{subjectId} has no file kept to send again" : $"{subjectId} has no file {fileId}", OpsCodes.NoFileKept);
        var done = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "message" && f.Code == "resent").Select(f => f.DataJson).ToListAsync(ct);
        if (done.Any(d => (string?)JsonNode.Parse(d)?["action"] == actionId))
            return new JsonObject { ["file"] = file.Id, ["result"] = "already resent by this action" };
        var tenant = await db.Bulks.AsNoTracking().Where(b => b.Id == subjectId).Select(b => b.Tenant).FirstOrDefaultAsync(ct)
            ?? await db.Payments.AsNoTracking().Where(p => p.Id == subjectId).Select(p => p.Tenant).FirstOrDefaultAsync(ct) ?? "";
        var result = await port.Put(tenant, subjectId, file.Id, file.FileName ?? file.Id, file.Content, ct);
        await new FactLog(db, clock).Append(subjectId, subjectKind, new NewFact("message", "resent",
            new JsonObject { ["file"] = file.Id, ["fileName"] = file.FileName, ["kind"] = file.Kind, ["sha256"] = file.ContentHash, ["result"] = result, ["action"] = actionId }, Actor: actor), ct);
        return new JsonObject { ["file"] = file.Id, ["fileName"] = file.FileName, ["sha256"] = file.ContentHash, ["result"] = result };
    }
}

/// <summary>
/// A hold's expiry on the runtime's side (R-05-56): an open hold whose expiry has passed since it opened, or one a service
/// engineer expires, is recorded <c>expired</c> on the payment's chain and its decision <c>expired</c> goes to the engine, which
/// ends the hold expired (its own timer does the same at the same time on the engine's clock).
/// </summary>
public sealed class HoldExpiry(StoreOptions store, IEngineSignals signals, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>Expires every open hold whose expiry has passed. Answers how many.</summary>
    public async Task<int> Sweep(CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var holds = await db.Facts.AsNoTracking().Where(f => f.Kind == "hold").OrderBy(f => f.Seq).ToListAsync(ct);
        var closed = holds.Where(f => f.Code != "opened").Select(f => (f.SubjectId, (string?)JsonNode.Parse(f.DataJson)?["hold"])).ToHashSet();
        var n = 0;
        foreach (var f in holds.Where(f => f.Code == "opened"))
        {
            var d = JsonNode.Parse(f.DataJson) as JsonObject;
            var hold = (string?)d?["hold"];
            if (hold is null || closed.Contains((f.SubjectId, hold))) continue;
            // The instant the hold recorded when it opened, counted on the bank's business calendar (R-09-88); else its elapsed expiry.
            if ((string?)d!["expiresAt"] is string ea && DateTimeOffset.TryParse(ea, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var expiresAt))
            {
                if (expiresAt > clock.GetUtcNow()) continue;
            }
            else
            {
                if ((string?)d["expiry"] is not string e) continue;
                TimeSpan expiry;
                try { expiry = XmlConvert.ToTimeSpan(e); } catch (FormatException) { continue; }
                if (f.At + expiry > clock.GetUtcNow()) continue;
            }
            await Expire(f.SubjectId, f.SubjectKind, hold, "runtime", null, "its expiry passed", ct);
            n++;
        }
        return n;
    }

    /// <summary>Ends an open hold as expired: the fact, and the decision on its way to the engine.</summary>
    public async Task<JsonObject> Expire(string subjectId, string subjectKind, string hold, string actor, string? actionId, string why, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var tenant = await db.Bulks.AsNoTracking().Where(b => b.Id == subjectId).Select(b => b.Tenant).FirstOrDefaultAsync(ct)
            ?? await db.Payments.AsNoTracking().Where(p => p.Id == subjectId).Select(p => p.Tenant).FirstOrDefaultAsync(ct) ?? "";
        await new FactLog(db, clock).Append(subjectId, subjectKind, new NewFact("hold", "expired", new JsonObject { ["hold"] = hold, ["by"] = actor, ["why"] = why, ["action"] = actionId }, Actor: actor), ct);
        // A hold that reached its expiry undecided, for the alert rules (R-09-72); an operator's expire-hold is a decision.
        if (actionId is null) RuntimeGauges.HoldsExpired.Add(1, new KeyValuePair<string, object?>("th.tenant", tenant));
        await signals.Send(tenant, subjectId, null, "decide", actionId ?? Guid.NewGuid().ToString("N"), ct, new JsonObject { ["hold"] = hold, ["decision"] = "expired", ["by"] = actor });
        return new JsonObject { ["hold"] = hold, ["decision"] = "expired" };
    }
}
