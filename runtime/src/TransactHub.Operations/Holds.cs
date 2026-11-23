using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Activities.Native;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The platform's holds answered by the runtime (R-08-15, R-05-50, R-05-56): when a bench or a deployment gives
/// <c>p.port.operations-queue.v1</c> to the runtime, <c>p.hold.approval.v1</c> and <c>p.hold.repair.v1</c> run here. The hold
/// is opened as a fact on the payment's chain (kind <c>hold</c>, code <c>opened</c>, with its expiry) with an exception item,
/// and the attempt answers at once that it is held. The engine then awaits the operator's decision (<c>p.ops.decision</c>)
/// until the hold's expiry: the console records the decision on the same chain (<c>decided</c>) and puts it on the outbox
/// for the engine's host, which delivers it (Issue I-98). A hold that is asked again while it is open (an attempt retried before
/// its answer was recorded) finds its own open hold and is held again; a closed hold is not reopened.
/// </summary>
public sealed class StoreHold(StoreOptions store, string activity, string kind, string factPath, string[] decisions, TimeProvider? clock = null) : INativeActivity
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public string Name => activity;

    public static INativeActivity[] For(StoreOptions store) =>
    [
        new StoreHold(store, "p.hold.approval.v1", "approval", "facts.approval", ["approved", "declined"]),
        new StoreHold(store, "p.hold.repair.v1", "repair", "facts.repair", ["released", "rejected"]),
    ];

    public NativeResult Run(NativeAttempt a) => RunAsync(a).GetAwaiter().GetResult();

    async Task<NativeResult> RunAsync(NativeAttempt a)
    {
        var tenant = a.Tenant ?? throw new NativeFailure("TenantMissing", $"{activity} needs the tenant it runs for", false);
        var header = a.View["header"] as JsonObject ?? new JsonObject();
        var (subjectId, subjectKind) = await SubjectOf(tenant, header) ?? throw new NativeFailure("NotInStore", $"{activity}: the payment is not in the store", false);
        string holdId;
        var expiry = (string?)a.Params["expiry"];
        // Counted on the bank's business calendar when the hold says so (owner's decision of 23 November 2026, R-09-88, Issue I-127).
        var business = (string?)a.Params["expiryCalendar"] == "business";
        HoldCalendar? calendar = null;
        string? expiresAt = null;
        await using (var db = StoreDbContext.Open(store))
        {
            if (business) calendar = await HoldCalendar.Of(db, tenant, clock.GetUtcNow());
            if (expiry is not null) expiresAt = StoreTime.Of(HoldCalendar.ExpiresAt(clock.GetUtcNow(), expiry, calendar)).ToString("O");
            var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "hold").OrderBy(f => f.Seq).ToListAsync();
            var closed = facts.Where(f => f.Code != "opened").Select(f => (string?)JsonNode.Parse(f.DataJson)?["hold"]).ToHashSet();
            var open = facts.LastOrDefault(f => f.Code == "opened" && (string?)JsonNode.Parse(f.DataJson)?["activity"] == activity && !closed.Contains((string?)JsonNode.Parse(f.DataJson)?["hold"]));
            if (open is not null) holdId = (string)JsonNode.Parse(open.DataJson)!["hold"]!;
            else
            {
                holdId = Guid.NewGuid().ToString("N");
                var reason = await ReasonOf(db, subjectId);
                db.ExceptionItems.Add(new ExceptionItemRow
                {
                    Id = holdId, Tenant = tenant, SubjectId = subjectId, Code = $"hold.{kind}", Text = reason,
                    DataJson = CanonicalJson.Of(new JsonObject { ["activity"] = activity, ["expiry"] = expiry, ["expiresAt"] = expiresAt }), RaisedAt = StoreTime.Of(clock.GetUtcNow()),
                });
                await db.SaveChangesAsync();
                await new FactLog(db, clock).Append(subjectId, subjectKind, new NewFact("hold", "opened",
                    new JsonObject { ["hold"] = holdId, ["kind"] = kind, ["activity"] = activity, ["reason"] = reason, ["expiry"] = expiry, ["expiryCalendar"] = business ? "business" : null, ["expiresAt"] = expiresAt, ["calendar"] = calendar?.Table }, Actor: "runtime"));
            }
        }
        // Held: the engine awaits the decision until the hold's expiry (R-05-56). No connection is held while it waits.
        return new NativeResult("held", null, new JsonObject()) { Held = new JsonObject { ["hold"] = holdId, ["expiry"] = expiry, ["expiryCalendar"] = business ? "business" : null, ["calendar"] = calendar?.ToJson() } };
    }

    /// <summary>The payment or bulk the hold is for: by the header's file id (a bulk's message id) or end-to-end id, the newest.</summary>
    async Task<(string, string)?> SubjectOf(string tenant, JsonObject header)
    {
        await using var db = StoreDbContext.Open(store);
        var scheme = (string?)header["scheme"] ?? "";
        var fileId = Short((string?)header["fileId"] ?? (string?)header["endToEndId"]);
        if (fileId is not null)
        {
            var b = await db.Bulks.AsNoTracking().Where(x => x.Tenant == tenant && x.Scheme == scheme && x.MessageId == fileId).OrderByDescending(x => x.AcceptedAt).Select(x => x.Id).FirstOrDefaultAsync();
            if (b is not null) return (b, "bulk");
        }
        var e2e = Short((string?)header["endToEndId"]);
        if (e2e is null) return null;
        var p = await db.Payments.AsNoTracking().Where(x => x.Tenant == tenant && x.Scheme == scheme && x.EndToEndId == e2e && x.BulkId == null).OrderByDescending(x => x.AcceptedAt).Select(x => x.Id).FirstOrDefaultAsync();
        return p is null ? null : (p, "payment");
    }

    static string? Short(string? s) => s is null ? null : s.Length <= 35 ? s : s[..35];

    /// <summary>Why the payment is held: the last step before the hold that did not take its primary outcome, with its reason code.</summary>
    static async Task<string?> ReasonOf(StoreDbContext db, string subjectId)
    {
        var steps = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "step").OrderByDescending(f => f.Seq).Take(20).ToListAsync();
        foreach (var s in steps)
        {
            var d = JsonNode.Parse(s.DataJson) as JsonObject;
            var rc = (string?)d?["reasonCode"];
            if (rc is not null || s.Code is "reject" or "repair" or "hold" or "hit" or "rejected" or "unknown" or "unverified")
                return $"{rc ?? s.Code} at {(string?)d?["step"]} ({(string?)d?["activity"]})";
        }
        return null;
    }
}

/// <summary>
/// The bank's business calendar for a hold's expiry (owner's decision of 23 November 2026, R-09-88, Issue I-127): the reference
/// table of domain <c>business-calendar</c> its configuration release installed (R-09-75), the edition in force on the day the hold
/// opens; its weekday rows are the weekend and its dated rows the holidays. Without a table, Saturday and Sunday. A whole day of the
/// expiry counts one business day and lands on one; the time part is elapsed time, as the studio's addDuration counts it.
/// </summary>
public sealed record HoldCalendar(int[] Weekend, HashSet<string> Holidays, string? Table)
{
    public const string Domain = "business-calendar";
    public static readonly HoldCalendar Default = new([0, 6], [], null);
    static readonly string[] Days = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    public static async Task<HoldCalendar> Of(StoreDbContext db, string bank, DateTimeOffset at, CancellationToken ct = default)
    {
        var row = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == "reference" && v.Tenant == bank && v.Name == Domain).OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct);
        if (row is null) return Default;
        var t = JsonNode.Parse(row.BodyJson)?["table"];
        var day = at.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var edition = (t?["editions"] as JsonArray ?? []).OfType<JsonObject>().Where(e => string.CompareOrdinal((string?)e["validFrom"], day) <= 0)
            .OrderBy(e => (string?)e["validFrom"], StringComparer.Ordinal).LastOrDefault();
        if (edition is null) return Default;
        var weekend = new List<int>();
        var holidays = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in edition["rows"] as JsonArray ?? [])
        {
            var d = (string?)r?["day"] ?? "";
            var w = Array.IndexOf(Days, d);
            if (w >= 0) weekend.Add(w);
            else if (System.Text.RegularExpressions.Regex.IsMatch(d, @"^\d{4}-\d{2}-\d{2}$")) holidays.Add(d);
        }
        return new HoldCalendar(weekend.Order().ToArray(), holidays, (string?)t?["name"]);
    }

    public bool IsBusinessDay(DateTimeOffset t) =>
        !Weekend.Contains((int)t.UtcDateTime.DayOfWeek) && !Holidays.Contains(t.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>When a hold opened at <paramref name="opened"/> expires; without a calendar, in elapsed time.</summary>
    public static DateTimeOffset ExpiresAt(DateTimeOffset opened, string expiry, HoldCalendar? calendar)
    {
        var span = System.Xml.XmlConvert.ToTimeSpan(expiry);
        if (calendar is null) return opened + span;
        var t = opened;
        for (var i = 0; i < span.Days; i++)
        {
            t = t.AddDays(1);
            while (!calendar.IsBusinessDay(t)) t = t.AddDays(1);
        }
        return t + (span - TimeSpan.FromDays(span.Days));
    }

    public JsonObject ToJson() => new()
    {
        ["weekend"] = new JsonArray(Weekend.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
        ["holidays"] = new JsonArray(Holidays.Order(StringComparer.Ordinal).Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
        ["table"] = Table,
    };
}
