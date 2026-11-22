using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

// The files of a deployment (Issue I-97 part 2, R-09-62): every file a host receives or sends is kept in the store, insert-only. The
// row says what it is (name, size, direction, kind), whose it is (tenant, payment or bulk, leg) and holds the SHA-256 of its bytes,
// signed by the host that kept it; the bytes are kept once per hash in `file_content`. The operations console lists a payment's
// files in its documents tab and serves their bytes to the payment's operators; anyone with the host's public key can check that a
// file is the one that was kept.

/// <summary>A file to keep: what it is and whose.</summary>
public sealed record FileToKeep(string Tenant, string Scheme, string Direction, string Kind, string FileName, string? SubjectId, string? Leg = null, string? Activity = null);

/// <summary>
/// The statement a host signs for a file: a fixed text of its hash, size, name, direction, kind, tenant, subject, leg and time, one per
/// line. ECDSA P-256 with SHA-256, DER, in Base64: <c>openssl dgst -sha256 -verify host.pem -signature sig.der statement.txt</c> checks one.
/// </summary>
public static class FileSignature
{
    public const string Format = "th-stored-file/1";

    public static byte[] Statement(StoredFileRow f) => Encoding.UTF8.GetBytes(string.Join('\n',
        Format, f.ContentHash, f.Size.ToString(CultureInfo.InvariantCulture), f.FileName, f.Direction, f.Kind, f.Tenant, f.SubjectId ?? "", f.Leg ?? "",
        f.At.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture)) + "\n");

    public static string KeyIdOf(ECDsa key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..32];

    public static string Sign(StoredFileRow f, ECDsa key) => Convert.ToBase64String(key.SignData(Statement(f), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    public static bool Verify(StoredFileRow f, ECDsa publicKey) =>
        f.SigningKeyId == KeyIdOf(publicKey) && publicKey.VerifyData(Statement(f), Convert.FromBase64String(f.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
}

/// <summary>Keeps and reads the files of a deployment. Insert-only: a file kept twice for the same payment, leg and direction is one row.</summary>
public sealed class FileStore(StoreDbContext db, ECDsa signingKey, TimeProvider? clock = null)
{
    public const string FileTopic = "file";
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public static string Sha256Hex(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>Keeps a file and its bytes; returns the row (the earlier one when this file was kept before for the same owner).</summary>
    public async Task<StoredFileRow> Keep(FileToKeep f, byte[] content, CancellationToken ct = default)
    {
        if (f.Direction is not ("in" or "out")) throw new ArgumentException($"A file's direction is in or out, not {f.Direction}", nameof(f));
        var hash = Sha256Hex(content);
        var earlier = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Tenant == f.Tenant && x.ContentHash == hash && x.SubjectId == f.SubjectId
            && x.Leg == f.Leg && x.Direction == f.Direction && x.FileName == f.FileName, ct);
        if (earlier is not null) return earlier;
        var row = new StoredFileRow
        {
            Id = Guid.NewGuid().ToString("N"), Tenant = f.Tenant, Scheme = f.Scheme, Direction = f.Direction, Kind = f.Kind, FileName = f.FileName,
            Size = content.LongLength, ContentHash = hash, SubjectId = f.SubjectId, Leg = f.Leg, Activity = f.Activity, At = StoreTime.Of(clock.GetUtcNow()),
            SigningKeyId = FileSignature.KeyIdOf(signingKey),
        };
        row.Signature = FileSignature.Sign(row, signingKey);
        if (!await db.FileContents.AsNoTracking().AnyAsync(x => x.ContentHash == hash, ct))
            db.FileContents.Add(new FileContentRow { ContentHash = hash, Content = content.ToArray(), At = row.At });
        db.StoredFiles.Add(row);
        db.Outbox.Add(new OutboxRow
        {
            Topic = FileTopic, Key = row.Id, At = row.At,
            PayloadJson = CanonicalJson.Of(new JsonObject { ["file"] = row.Id, ["subject"] = row.SubjectId, ["kind"] = row.Kind, ["direction"] = row.Direction }),
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Another writer kept the same bytes first: keep the row without them.
            db.ChangeTracker.Clear();
            if (!await db.FileContents.AsNoTracking().AnyAsync(x => x.ContentHash == hash, ct)) throw;
            db.StoredFiles.Add(row);
            db.Outbox.Add(new OutboxRow { Topic = FileTopic, Key = row.Id, At = row.At, PayloadJson = CanonicalJson.Of(new JsonObject { ["file"] = row.Id, ["subject"] = row.SubjectId, ["kind"] = row.Kind, ["direction"] = row.Direction }) });
            await db.SaveChangesAsync(ct);
        }
        return row;
    }

    /// <summary>A kept file's bytes, checked against its hash; null when there is no such file.</summary>
    public async Task<(StoredFileRow File, byte[] Content)?> Read(string id, CancellationToken ct = default)
    {
        var f = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return null;
        var c = await db.FileContents.AsNoTracking().FirstOrDefaultAsync(x => x.ContentHash == f.ContentHash, ct)
            ?? throw new InvalidOperationException($"The bytes of file {id} are not in the store");
        if (Sha256Hex(c.Content) != f.ContentHash) throw new InvalidOperationException($"The bytes of file {id} do not match its hash");
        return (f, c.Content);
    }

    public Task<List<StoredFileRow>> Of(string tenant, string subjectId, CancellationToken ct = default) =>
        db.StoredFiles.AsNoTracking().Where(x => x.Tenant == tenant && x.SubjectId == subjectId).OrderBy(x => x.At).ToListAsync(ct);
}
