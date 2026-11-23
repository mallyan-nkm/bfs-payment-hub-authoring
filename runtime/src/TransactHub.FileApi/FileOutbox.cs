using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Ports;
using TransactHub.Store;

namespace TransactHub.FileApi;

// The File API's outbox (R-09-78): every file a workflow sends is written here first, kept once with its signed SHA-256 and linked to
// its payment and leg, and then delivered by the bank's binding of p.port.file-transfer.v1 (the SFTP gateway's `sftp` adapter, a
// `directory`, or the `simulator`). Each delivery is a fact on the file's chain: `file.queued`, then `file.delivered` with the adapter,
// the folder, the name and the attempt, or `file.delivery-failed` with the reason. A resend (the operations console's `resend`, the
// service console's `resend-file`) puts the same kept bytes on the port again, never rendered or signed again, and is a fact
// `file.resent` with who asked; the same action id sends once.

/// <summary>A file a workflow sends: whose, where to (a folder of the binding, naming its partner) and its bytes.</summary>
public sealed record Outbound(string Tenant, string Scheme, string Kind, string Folder, string FileName, byte[] Content, string? PaymentId, string? Leg, string? Activity, string Actor);

/// <summary>One delivery of a kept file.</summary>
public sealed record Delivery(string FileId, string Result, string? Adapter, int Attempt, string? Error);

/// <summary>The binding in force of p.port.file-transfer.v1 for a bank, as an adapter.</summary>
public interface ITransferPorts
{
    (IFileTransfer Transfer, PortBinding Binding) For(string tenant, DateTimeOffset at);
}

/// <summary>The banks' bindings, read once; each adapter built once per binding.</summary>
public sealed class BoundTransfers(PortBindings bindings, TransactHub.Security.ISecretResolver? secrets = null) : ITransferPorts
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<PortBinding, IFileTransfer> built = new(ReferenceEqualityComparer.Instance);
    public PortBindings Bindings => bindings;

    public (IFileTransfer Transfer, PortBinding Binding) For(string tenant, DateTimeOffset at)
    {
        var b = bindings.InForce(tenant, FileTransferAdapters.Port, at);
        return (built.GetOrAdd(b, x => FileTransferAdapters.Create(x, secrets)), b);
    }
}

public sealed class FileOutbox(StoreOptions store, System.Security.Cryptography.ECDsa signingKey, ITransferPorts ports, TimeProvider? clock = null)
{
    public const string QueuedKind = "file.queued", DeliveredKind = "file.delivered", FailedKind = "file.delivery-failed", ResentKind = "file.resent";
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>Keeps the file and queues it for delivery; the same file for the same payment and leg is queued once.</summary>
    public async Task<StoredFileRow> Queue(Outbound o, CancellationToken ct = default)
    {
        FileTransferAdapters.Relative(o.Folder, o.FileName);
        await using var db = StoreDbContext.Open(store);
        var row = await new FileStore(db, signingKey, clock).Keep(new FileToKeep(o.Tenant, o.Scheme, "out", o.Kind, o.FileName, o.PaymentId, o.Leg, o.Activity ?? "file-api:outbox"), o.Content, ct);
        if (!await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == row.Id && f.Kind == QueuedKind, ct))
            await new FactLog(db, clock).Append(row.Id, FileIntake.FileKind, new NewFact(QueuedKind, null, new JsonObject
            {
                ["folder"] = o.Folder, ["fileName"] = o.FileName, ["payment"] = o.PaymentId, ["leg"] = o.Leg, ["sha256"] = row.ContentHash,
            }, Actor: o.Actor), ct);
        return row;
    }

    /// <summary>Delivers a queued file on the bank's binding in force now; a file delivered already is not sent again.</summary>
    public Task<Delivery> Deliver(string fileId, CancellationToken ct = default) => Send(fileId, again: false, actor: "file-api", actionId: null, ct);

    /// <summary>Sends a kept file's bytes again (a resend). An action id given twice sends once.</summary>
    public Task<Delivery> Resend(string fileId, string actor, string actionId, CancellationToken ct = default) => Send(fileId, again: true, actor, actionId, ct);

    async Task<Delivery> Send(string fileId, bool again, string actor, string? actionId, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var kept = (await new FileStore(db, signingKey, clock).Read(fileId, ct)) ?? throw new KeyNotFoundException($"No file {fileId} is kept");
        var (file, content) = kept;
        var facts = await new FactLog(db, clock).Of(fileId, ct);
        var queued = facts.FirstOrDefault(f => f.Kind == QueuedKind) ?? throw new InvalidOperationException($"{fileId} was never queued to send: only an outbox file is delivered");
        var q = JsonNode.Parse(queued.DataJson) as JsonObject ?? new JsonObject();
        if (actionId is not null && facts.Any(f => f.Kind == ResentKind && (string?)JsonNode.Parse(f.DataJson)?["action"] == actionId))
            return new Delivery(fileId, "already resent by this action", null, facts.Count(f => f.Kind == DeliveredKind), null);
        if (!again && facts.Any(f => f.Kind == DeliveredKind)) return new Delivery(fileId, "delivered before", null, facts.Count(f => f.Kind == DeliveredKind), null);
        var attempt = facts.Count(f => f.Kind is DeliveredKind or FailedKind) + 1;
        var log = new FactLog(db, clock);
        if (again) await log.Append(fileId, FileIntake.FileKind, new NewFact(ResentKind, null, new JsonObject { ["action"] = actionId, ["by"] = actor, ["sha256"] = file.ContentHash }, Actor: actor), ct);
        IFileTransfer t;
        PortBinding b;
        try { (t, b) = ports.For(file.Tenant, clock.GetUtcNow()); }
        catch (PortBindingException e)
        {
            await log.Append(fileId, FileIntake.FileKind, new NewFact(FailedKind, "file.NotBound", new JsonObject { ["attempt"] = attempt, ["why"] = e.Message }, Actor: actor), ct);
            return new Delivery(fileId, "failed", null, attempt, e.Message);
        }
        var folder = (string?)q["folder"] ?? "";
        var name = (string?)q["fileName"] ?? file.FileName;
        try
        {
            await t.Put(folder, name, content, ct);
            await log.Append(fileId, FileIntake.FileKind, new NewFact(DeliveredKind, b.Adapter, new JsonObject
            {
                ["adapter"] = b.Adapter, ["folder"] = folder, ["fileName"] = name, ["attempt"] = attempt, ["sha256"] = file.ContentHash, ["resend"] = again,
            }, Actor: actor), ct);
            return new Delivery(fileId, again ? "resent" : "delivered", b.Adapter, attempt, null);
        }
        catch (Exception e) when (e is FileTransferException or PortBindingException or IOException or TransactHub.Security.SecretUnavailable)
        {
            await log.Append(fileId, FileIntake.FileKind, new NewFact(FailedKind, b.Adapter, new JsonObject { ["adapter"] = b.Adapter, ["attempt"] = attempt, ["why"] = e.Message }, Actor: actor), ct);
            return new Delivery(fileId, "failed", b.Adapter, attempt, e.Message);
        }
    }

    /// <summary>The outbox files not yet delivered, oldest first, with fewer failed attempts than the limit.</summary>
    public async Task<List<string>> Pending(int maxAttempts, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var queued = await db.Facts.AsNoTracking().Where(f => f.SubjectKind == FileIntake.FileKind && f.Kind == QueuedKind).OrderBy(f => f.Id).Select(f => f.SubjectId).ToListAsync(ct);
        if (queued.Count == 0) return [];
        var done = await db.Facts.AsNoTracking().Where(f => queued.Contains(f.SubjectId) && (f.Kind == DeliveredKind || f.Kind == FailedKind)).Select(f => new { f.SubjectId, f.Kind }).ToListAsync(ct);
        return queued.Where(id => !done.Any(d => d.SubjectId == id && d.Kind == DeliveredKind) && done.Count(d => d.SubjectId == id && d.Kind == FailedKind) < maxAttempts).ToList();
    }

    /// <summary>A file's deliveries and resends, for the consoles.</summary>
    public async Task<JsonArray> History(string fileId, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var facts = await new FactLog(db, clock).Of(fileId, ct);
        return new JsonArray(facts.Select(f => (JsonNode)new JsonObject { ["kind"] = f.Kind, ["code"] = f.Code, ["at"] = f.At.ToString("O"), ["actor"] = f.Actor, ["data"] = JsonNode.Parse(f.DataJson) }).ToArray());
    }
}
