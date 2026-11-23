using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// Retention, legal holds and erasure (R-05-70, Issue I-180). The store is insert-only, at the database too (R-09-81), so nothing is
/// deleted: an erasure is a tombstone fact (<c>erasure</c>, code <c>erased</c>) on the payment's own chain, with who asked, who
/// consented and why, and no value. From it the read model shows every field that declares a mask as <c>[erased]</c> (a rebuild
/// gives the same), the console refuses the payment's kept documents and any unmasking, and the fixed reports and the export read
/// the read model only. What this does not do is recorded in Issue I-180: the store's own rows (the payment, its lines, its kept
/// files) still hold the values, readable by whoever reads the database.
/// <para>
/// An erasure is asked for by a supervisor and carried out only when a bank approver, another person, consents (R-09-77); it is
/// refused while the payment is open, already erased, or under a legal hold, both when asked and again at the consent. A payment
/// is due for erasure when it ended longer ago than the bank's retention period (<c>retentionPeriod</c>, R-05-66); a period of P0D,
/// the default, makes nothing due. A legal hold is placed at once by a supervisor or a bank approver; releasing one, which lets an
/// erasure through, waits for a bank approver's consent.
/// </para>
/// </summary>
public sealed class Erasure(StoreOptions store, StoreOptions ops, OperationsDials dials, ConsoleRecord record, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public const string FactKind = "erasure";

    public sealed record Hold(string Id, string Subject, string Tenant, string Why, string PlacedBy, DateTimeOffset PlacedAt);

    async Task<OpsSubject> Subject(string id, CancellationToken ct)
    {
        await using var o = OpsDbContext.Open(ops);
        return await o.Subjects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ActionRefused(404, $"No payment or bulk {id}", OpsCodes.NotFound);
    }

    public async Task<bool> IsErased(string subjectId, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        return await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == subjectId && f.Kind == FactKind, ct);
    }

    static DateTimeOffset? EndOf(OpsSubject s) =>
        (string?)(JsonNode.Parse(s.FieldsJson) as JsonObject)?["endedAt"] is { } e && DateTimeOffset.TryParse(e, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>The legal holds in force, of one payment or of all.</summary>
    public async Task<List<Hold>> Holds(string? subjectId = null, CancellationToken ct = default)
    {
        var placed = new Dictionary<string, Hold>();
        foreach (var f in await record.All(ConsoleRecord.LegalHoldKind, ct))
        {
            var d = JsonNode.Parse(f.DataJson)!.AsObject();
            var id = (string)d["hold"]!;
            if (f.Code == "placed") placed[id] = new Hold(id, (string)d["subject"]!, (string?)d["tenant"] ?? "", (string?)d["why"] ?? "", f.Actor, f.At);
            else if (f.Code == "released") placed.Remove(id);
        }
        return placed.Values.Where(h => subjectId is null || h.Subject == subjectId).OrderBy(h => h.PlacedAt).ToList();
    }

    /// <summary>A legal hold, placed at once: it only keeps (an erasure is refused while it holds).</summary>
    public async Task<Hold> PlaceHold(OperatorIdentity who, string subjectId, string? why, CancellationToken ct = default)
    {
        var s = await Subject(subjectId, ct);
        if (!who.Has(s.Tenant, OperatorRoles.Supervisor) && !who.Has(s.Tenant, OperatorRoles.BankApprover)) throw new ActionRefused(403, "A legal hold is placed by a supervisor or a bank approver", OpsCodes.Forbidden);
        if (string.IsNullOrWhiteSpace(why)) throw new ActionRefused(400, "A legal hold needs a reason: the matter it is for", OpsCodes.RemarksRequired);
        var id = Guid.NewGuid().ToString("N")[..16];
        var f = await record.Note(ConsoleRecord.LegalHoldKind, "placed", new JsonObject { ["hold"] = id, ["subject"] = subjectId, ["tenant"] = s.Tenant, ["why"] = why, ["by"] = who.DisplayName }, who.Actor, ct);
        return new Hold(id, subjectId, s.Tenant, why, who.Actor, f.At);
    }

    /// <summary>Asks to release a legal hold; it waits for a bank approver's consent.</summary>
    public async Task<JsonObject> AskRelease(OperatorIdentity who, string holdId, string? why, CancellationToken ct = default)
    {
        var h = (await Holds(null, ct)).FirstOrDefault(x => x.Id == holdId) ?? throw new ActionRefused(404, $"No legal hold {holdId} in force", OpsCodes.NotFound);
        if (!who.Has(h.Tenant, OperatorRoles.Supervisor) && !who.Has(h.Tenant, OperatorRoles.BankApprover)) throw new ActionRefused(403, "Releasing a legal hold is asked for by a supervisor or a bank approver", OpsCodes.Forbidden);
        return await record.Request(who, ConsoleRecord.ReleaseLegalHold, why, ct, new JsonObject { ["hold"] = holdId, ["subject"] = h.Subject, ["tenant"] = h.Tenant });
    }

    /// <summary>On the bank approver's consent: the hold's release, a fact.</summary>
    public async Task<JsonObject> Release(JsonObject details, string requestedBy, string approver, CancellationToken ct = default)
    {
        var id = (string)details["hold"]!;
        if (!(await Holds(null, ct)).Any(x => x.Id == id)) throw new ActionRefused(409, $"Legal hold {id} is no longer in force", OpsCodes.AlreadyDecided);
        await record.Note(ConsoleRecord.LegalHoldKind, "released", new JsonObject { ["hold"] = id, ["subject"] = details["subject"]?.DeepClone(), ["requestedBy"] = requestedBy }, approver, ct);
        return new JsonObject { ["released"] = id };
    }

    async Task Erasable(OpsSubject s, CancellationToken ct)
    {
        if (await IsErased(s.Id, ct)) throw new ActionRefused(409, $"{s.Id} is already erased", OpsCodes.AlreadyDecided);
        if (EndOf(s) is null) throw new ActionRefused(409, $"{s.Id} has not ended; only an ended payment is erased", OpsCodes.ActionNotOffered);
        if ((await Holds(s.Id, ct)).FirstOrDefault() is { } h) throw new ActionRefused(409, $"{s.Id} is under legal hold {h.Id} ({h.Why}); it is not erased while the hold is in force", OpsCodes.ActionNotOffered);
    }

    /// <summary>A supervisor asks to erase an ended payment's personal data; it waits for a bank approver's consent.</summary>
    public async Task<JsonObject> AskErase(OperatorIdentity who, string subjectId, string? why, CancellationToken ct = default)
    {
        var s = await Subject(subjectId, ct);
        if (!who.Has(s.Tenant, OperatorRoles.Supervisor)) throw new ActionRefused(403, "An erasure is asked for by a supervisor", OpsCodes.Forbidden);
        await Erasable(s, ct);
        var due = await DueAt(s, ct);
        return await record.Request(who, ConsoleRecord.Erase, why, ct, new JsonObject
        {
            ["subject"] = subjectId, ["kind"] = s.Kind, ["tenant"] = s.Tenant, ["scheme"] = s.Scheme,
            ["basis"] = due is { } d && d <= clock.GetUtcNow() ? "retention" : "request", ["dueAt"] = due?.ToString("O"),
        });
    }

    /// <summary>On the bank approver's consent: the checks again, then the tombstone on the payment's chain.</summary>
    public async Task<JsonObject> Erase(JsonObject details, string requestedBy, string approver, string actionId, CancellationToken ct = default)
    {
        var s = await Subject((string)details["subject"]!, ct);
        await Erasable(s, ct);
        await using var db = StoreDbContext.Open(store);
        var f = await new FactLog(db, clock).Append(s.Id, s.Kind, new NewFact(FactKind, "erased", new JsonObject
        {
            ["action"] = actionId, ["requestedBy"] = requestedBy, ["basis"] = details["basis"]?.DeepClone(), ["dueAt"] = details["dueAt"]?.DeepClone(),
        }, Actor: approver), ct);
        return new JsonObject { ["erased"] = s.Id, ["fact"] = f.Seq };
    }

    /// <summary>When a payment is due for erasure: its end plus the bank's retention period in force now; null when it has not ended or the period is P0D.</summary>
    async Task<DateTimeOffset?> DueAt(OpsSubject s, CancellationToken ct)
    {
        if (EndOf(s) is not { } end) return null;
        var period = await dials.For(s.Tenant, s.Scheme, OperationsDials.RetentionPeriod, clock.GetUtcNow(), ct);
        return period.Value <= TimeSpan.Zero ? null : end + period.Value;
    }

    /// <summary>The payments due for erasure: ended longer ago than the retention period, not erased, not under a legal hold.</summary>
    public async Task<JsonObject> Due(IReadOnlyCollection<string> tenants, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var subjects = await o.Subjects.AsNoTracking().Where(x => tenants.Contains(x.Tenant)).OrderBy(x => x.AcceptedAt).Take(Reports.MaxRows).ToListAsync(ct);
        var held = (await Holds(null, ct)).Select(h => h.Subject).ToHashSet();
        await using var db = StoreDbContext.Open(store);
        var erased = (await db.Facts.AsNoTracking().Where(f => f.Kind == FactKind).Select(f => f.SubjectId).ToListAsync(ct)).ToHashSet();
        var now = clock.GetUtcNow();
        var rows = new JsonArray();
        var periods = new JsonObject();
        foreach (var s in subjects)
        {
            if (erased.Contains(s.Id)) continue;
            var due = await DueAt(s, ct);
            if (due is not { } d || d > now) continue;
            rows.Add(new JsonObject
            {
                ["id"] = s.Id, ["kind"] = s.Kind, ["tenant"] = s.Tenant, ["scheme"] = s.Scheme, ["acceptedAt"] = s.AcceptedAt.ToString("O"),
                ["endedAt"] = EndOf(s)?.ToString("O"), ["dueAt"] = d.ToString("O"), ["legalHold"] = held.Contains(s.Id),
            });
        }
        foreach (var t in tenants)
            foreach (var sc in subjects.Where(x => x.Tenant == t).Select(x => x.Scheme).Distinct())
            {
                var p = await dials.For(t, sc, OperationsDials.RetentionPeriod, now, ct);
                periods[$"{t}/{sc}"] = new JsonObject { ["parameter"] = p.Parameter, ["value"] = OperationsDials.Iso(p.Value), ["source"] = p.Source };
            }
        return new JsonObject { ["at"] = now.ToString("O"), ["periods"] = periods, ["total"] = rows.Count, ["rows"] = rows };
    }
}
