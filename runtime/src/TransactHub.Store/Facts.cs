using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

/// <summary>The time the store records: UTC, to the microsecond, so it reads back the same on both databases.</summary>
public static class StoreTime
{
    public static DateTimeOffset Of(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return new DateTimeOffset(u.Ticks - u.Ticks % 10, TimeSpan.Zero);
    }
}

/// <summary>JSON written the same way every time, for hashing: object keys sorted, no whitespace.</summary>
public static class CanonicalJson
{
    public static string Of(JsonNode? n)
    {
        var sb = new StringBuilder();
        Write(n, sb);
        return sb.ToString();
    }

    static void Write(JsonNode? n, StringBuilder sb)
    {
        switch (n)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(k)).Append(':');
                    Write(v, sb);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(a[i], sb); }
                sb.Append(']');
                break;
            default: sb.Append(n.ToJsonString()); break;
        }
    }
}

public sealed record NewFact(string Kind, string? Code = null, JsonObject? Data = null, string? ReleaseId = null, string Actor = "runtime");

/// <summary>
/// Facts about a subject, numbered and chained: each fact's hash covers the one before it, so a changed
/// or missing fact is found by <see cref="Verify"/>. Writers that race for the same number are kept apart
/// by the unique index on (subject, seq); the loser reads the new head and tries again.
/// </summary>
public sealed class FactLog(StoreDbContext db, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string HashOf(FactRow f) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f',
            f.PrevHash, f.SubjectId, f.SubjectKind, f.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            f.Kind, f.Code ?? "", f.DataJson, f.At.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            f.ReleaseId ?? "", f.Actor))));

    public async Task<FactRow> Append(string subjectId, string subjectKind, NewFact fact, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var head = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId)
                .OrderByDescending(f => f.Seq).Select(f => new { f.Seq, f.Hash }).FirstOrDefaultAsync(ct);
            var row = new FactRow
            {
                SubjectId = subjectId,
                SubjectKind = subjectKind,
                Seq = (head?.Seq ?? 0) + 1,
                Kind = fact.Kind,
                Code = fact.Code,
                DataJson = CanonicalJson.Of(fact.Data ?? new JsonObject()),
                At = StoreTime.Of(clock.GetUtcNow()),
                ReleaseId = fact.ReleaseId,
                Actor = fact.Actor,
                PrevHash = head?.Hash ?? Genesis,
            };
            row.Hash = HashOf(row);
            db.Facts.Add(row);
            // The transactional outbox (plan W2): every fact is announced in the same save, so a reader that
            // follows the outbox (the operations read model) misses none and sees none that was not written.
            var note = Outbox.Of(row);
            db.Outbox.Add(note);
            try
            {
                await db.SaveChangesAsync(ct);
                return row;
            }
            catch (DbUpdateException) when (attempt < 50)
            {
                db.Entry(row).State = EntityState.Detached;
                db.Entry(note).State = EntityState.Detached;
                if (!await db.Facts.AnyAsync(f => f.SubjectId == subjectId && f.Seq == row.Seq, ct)) throw;
                await Task.Delay(Random.Shared.Next(1, 5 + attempt * 2), ct);
            }
        }
    }

    public Task<List<FactRow>> Of(string subjectId, CancellationToken ct = default) =>
        db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId).OrderBy(f => f.Seq).ToListAsync(ct);

    /// <summary>The last fact of a kind (the current status is the last "status" fact).</summary>
    public Task<FactRow?> Last(string subjectId, string kind, CancellationToken ct = default) =>
        db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == kind).OrderByDescending(f => f.Seq).FirstOrDefaultAsync(ct);

    /// <summary>The current status of each subject that has one, as a projection over the facts.</summary>
    public async Task<Dictionary<string, string?>> CurrentStatuses(IReadOnlyCollection<string> subjectIds, CancellationToken ct = default)
    {
        var rows = await db.Facts.AsNoTracking()
            .Where(f => subjectIds.Contains(f.SubjectId) && f.Kind == "status")
            .GroupBy(f => f.SubjectId)
            .Select(g => g.OrderByDescending(f => f.Seq).Select(f => new { f.SubjectId, f.Code }).First())
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.SubjectId, r => r.Code);
    }

    /// <summary>Checks the chain of one subject: numbering without gaps, each hash right, each link right.</summary>
    public async Task<ChainCheck> Verify(string subjectId, CancellationToken ct = default)
    {
        var prev = Genesis;
        var n = 0;
        foreach (var f in await Of(subjectId, ct))
        {
            n++;
            if (f.Seq != n) return new ChainCheck(false, n, $"fact {f.Seq} where {n} was expected");
            if (f.PrevHash != prev) return new ChainCheck(false, n, $"fact {f.Seq} does not link to the one before it");
            if (HashOf(f) != f.Hash) return new ChainCheck(false, n, $"fact {f.Seq} was changed after it was written");
            prev = f.Hash;
        }
        return new ChainCheck(true, n, null);
    }
}

public sealed record ChainCheck(bool Ok, int Facts, string? Problem);

/// <summary>
/// What the outbox says of each fact and message: a reference, never the data (a fact's data can hold a payload).
/// A reader reads the fact itself from the store by subject and number.
/// </summary>
public static class Outbox
{
    public const string FactTopic = "fact";
    public const string MessageTopic = "message";

    public static OutboxRow Of(FactRow f) => new()
    {
        Topic = FactTopic, Key = f.SubjectId, At = f.At,
        PayloadJson = CanonicalJson.Of(new JsonObject { ["subject"] = f.SubjectId, ["subjectKind"] = f.SubjectKind, ["seq"] = f.Seq, ["kind"] = f.Kind, ["code"] = f.Code }),
    };

    public static OutboxRow Of(MessageRow m) => new()
    {
        Topic = MessageTopic, Key = m.Id, At = m.At,
        PayloadJson = CanonicalJson.Of(new JsonObject { ["message"] = m.Id, ["subject"] = m.SubjectId, ["kind"] = m.Kind }),
    };
}
