using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Ports;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// Where the operations console's alerts go (R-05-80, Issue I-182): the platform's notification port <c>p.port.notification.v1</c>,
/// bound by the bank in its configuration release like any port (R-09-150): the binding in force for the tenant at the moment of
/// the alert, read live. A tenant with no binding in force has its alerts written to the console's log, ids only (the <c>log</c>
/// adapter), so an alert is never lost for want of a binding.
/// </summary>
public sealed class OperatorAlerts(PortBindings? bindings, Action<string> log, HttpClient? http = null, ISecretResolver? secrets = null)
{
    readonly ConcurrentDictionary<PortBinding, INotification> adapters = new(ReferenceEqualityComparer.Instance);
    readonly LogNotification fallback = new(log);

    /// <summary>The adapter of the tenant's binding in force at <paramref name="at"/>, else the log.</summary>
    public INotification For(string tenant, DateTimeOffset at)
    {
        if (bindings is null) return fallback;
        PortBinding b;
        try { b = bindings.InForce(tenant, NotificationAdapters.Port, at); }
        catch (PortBindingException) { return fallback; }
        return adapters.GetOrAdd(b, x => NotificationAdapters.Create(x, log, http, secrets));
    }
}

/// <summary>
/// Turnaround limits per payment (R-05-68, R-05-81): the bank's <c>tatLimit</c> in force on the day a payment was accepted, the
/// scheme's where the bank set one, else the platform's (P0D, the default, sets no limit). The limit is counted from the
/// acceptance on the bank's business calendar, as a hold's expiry is (R-09-88, R-09-102): each whole day of it is a business day of
/// the reference table of domain <c>business-calendar</c> in force on the day of acceptance, the bank's own closing days (domain
/// <c>bank-calendar</c>) included, and its time part is elapsed time. A payment is in breach when it is open past its deadline, or
/// ended after it. The breach list is worked out from the read model; each breach is alerted once, as a fact on the console's chain
/// (kind <c>tat</c>, code <c>breached</c>) and through the notification port (R-05-80).
/// </summary>
public sealed class Turnaround(StoreOptions store, StoreOptions ops, OperationsDials dials, ConsoleRecord record, OperatorAlerts alerts, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<List<JsonObject>> Breaches(IReadOnlyCollection<string>? tenants, string? scheme = null, bool openOnly = false, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var x = o.Subjects.AsNoTracking().AsQueryable();
        if (tenants is not null) x = x.Where(s => tenants.Contains(s.Tenant));
        if (scheme is not null) x = x.Where(s => s.Scheme == scheme);
        var rows = await x.OrderByDescending(s => s.AcceptedAt).Take(Reports.MaxRows)
            .Select(s => new { s.Id, s.Kind, s.Tenant, s.Scheme, s.Direction, s.AcceptedAt, s.Status, s.FieldsJson, s.FileName }).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var limits = new Dictionary<string, OperationsDials.Dial>();
        var calendars = new Dictionary<string, HoldCalendar>();
        var out_ = new List<JsonObject>();
        await using var db = StoreDbContext.Open(store);
        foreach (var s in rows)
        {
            var day = $"{s.Tenant}|{s.AcceptedAt.UtcDateTime:yyyy-MM-dd}";
            var key = $"{day}|{s.Scheme}";
            if (!limits.TryGetValue(key, out var limit)) limits[key] = limit = await dials.For(s.Tenant, s.Scheme, OperationsDials.TatLimit, s.AcceptedAt, ct);
            if (limit.Value <= TimeSpan.Zero) continue;
            var f = JsonNode.Parse(s.FieldsJson) as JsonObject;
            DateTimeOffset? ended = (string?)f?["endedAt"] is { } e && DateTimeOffset.TryParse(e, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
            if (openOnly && ended is not null) continue;
            if (!calendars.TryGetValue(day, out var calendar)) calendars[day] = calendar = await HoldCalendar.Of(db, s.Tenant, s.AcceptedAt, ct);
            var deadline = HoldCalendar.ExpiresAt(s.AcceptedAt, OperationsDials.Iso(limit.Value), calendar);
            var until = ended ?? now;
            if (until <= deadline) continue;
            var elapsed = until - s.AcceptedAt;
            out_.Add(new JsonObject
            {
                ["id"] = s.Id, ["kind"] = s.Kind, ["tenant"] = s.Tenant, ["scheme"] = s.Scheme, ["direction"] = s.Direction, ["fileName"] = s.FileName, ["status"] = s.Status,
                ["acceptedAt"] = s.AcceptedAt.ToString("O"), ["endedAt"] = ended?.ToString("O"), ["open"] = ended is null,
                ["limit"] = OperationsDials.Iso(limit.Value), ["parameter"] = limit.Parameter, ["source"] = limit.Source,
                ["calendar"] = "business", ["calendarTable"] = calendar.Table, ["deadline"] = deadline.ToString("O"),
                ["elapsed"] = OperationsDials.Iso(TimeSpan.FromSeconds(Math.Floor(elapsed.TotalSeconds))),
                ["over"] = OperationsDials.Iso(TimeSpan.FromSeconds(Math.Floor((until - deadline).TotalSeconds))),
            });
        }
        return out_;
    }

    /// <summary>Alerts each breach not alerted before: a fact, then the notification port. Returns how many it alerted.</summary>
    public async Task<int> Sweep(CancellationToken ct = default)
    {
        var alerted = (await record.All(ConsoleRecord.TatKind, ct)).Where(f => f.Code == "breached").Select(f => (string?)JsonNode.Parse(f.DataJson)?["subject"]).ToHashSet();
        var n = 0;
        foreach (var b in await Breaches(null, null, false, ct))
        {
            var id = (string)b["id"]!;
            if (alerted.Contains(id)) continue;
            var tenant = (string)b["tenant"]!;
            var port = alerts.For(tenant, clock.GetUtcNow());
            var data = new JsonObject
            {
                ["subject"] = id, ["tenant"] = tenant, ["scheme"] = b["scheme"]?.DeepClone(), ["limit"] = b["limit"]?.DeepClone(), ["elapsed"] = b["elapsed"]?.DeepClone(),
                ["deadline"] = b["deadline"]?.DeepClone(), ["open"] = b["open"]?.DeepClone(), ["parameter"] = b["parameter"]?.DeepClone(),
                ["port"] = NotificationAdapters.Port, ["adapter"] = port.Adapter,
            };
            await record.Note(ConsoleRecord.TatKind, "breached", data, "runtime:operations", ct);
            try
            {
                await port.Alert(new OperatorAlert("tat.breached", tenant, id, (string?)b["scheme"],
                    $"Turnaround limit {(string?)b["limit"]} (business days) passed: {(string?)b["elapsed"]} since acceptance", (JsonObject)data.DeepClone()), ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // A port that fails is recorded and not tried again: the breach stays on the list and on the console's chain.
                await record.Note(ConsoleRecord.TatKind, "alert-failed", new JsonObject { ["subject"] = id, ["port"] = NotificationAdapters.Port, ["adapter"] = port.Adapter, ["why"] = e.GetType().Name }, "runtime:operations", ct);
            }
            alerted.Add(id);
            n++;
        }
        return n;
    }
}
