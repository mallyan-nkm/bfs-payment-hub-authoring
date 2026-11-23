using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>An alert the operations console raises, by ids only: never a personal value.</summary>
public sealed record OpsAlert(string Kind, string Tenant, string SubjectId, string? Scheme, string Title, JsonObject Data);

/// <summary>
/// The operations console's notification port (R-05-68, Issue I-182): where its alerts go. The factory declares no notification
/// port for operators, so the console has its own, bound by its settings: <c>simulator</c> (kept in memory, for tests),
/// <c>webhook</c> (a POST of the alert as JSON to <c>Operations:Notify:Url</c>) and <c>log</c> (the default: a warning with ids only).
/// </summary>
public interface IOpsNotifier
{
    string Adapter { get; }
    Task Notify(OpsAlert alert, CancellationToken ct = default);
}

public sealed class SimulatedNotifier : IOpsNotifier
{
    public const string Name = "simulator";
    public string Adapter => Name;
    readonly List<OpsAlert> sent = new();
    public IReadOnlyList<OpsAlert> Sent { get { lock (sent) return sent.ToList(); } }
    public Task Notify(OpsAlert alert, CancellationToken ct = default) { lock (sent) sent.Add(alert); return Task.CompletedTask; }
}

public sealed class WebhookNotifier(Uri url, HttpClient? http = null) : IOpsNotifier
{
    public const string Name = "webhook";
    readonly HttpClient http = http ?? new HttpClient();
    public string Adapter => Name;
    public async Task Notify(OpsAlert alert, CancellationToken ct = default)
    {
        using var r = await http.PostAsJsonAsync(url, new JsonObject
        {
            ["kind"] = alert.Kind, ["tenant"] = alert.Tenant, ["subject"] = alert.SubjectId, ["scheme"] = alert.Scheme, ["title"] = alert.Title, ["data"] = alert.Data.DeepClone(),
        }, ct);
        r.EnsureSuccessStatusCode();
    }
}

public sealed class LogNotifier(Action<string> log) : IOpsNotifier
{
    public const string Name = "log";
    public string Adapter => Name;
    public Task Notify(OpsAlert alert, CancellationToken ct = default) { log($"{alert.Kind} {alert.SubjectId} ({alert.Tenant}, {alert.Scheme}): {alert.Title}"); return Task.CompletedTask; }
}

public static class OpsNotifiers
{
    /// <summary>The adapter the settings name (<c>Operations:Notify:Adapter</c>, <c>Operations:Notify:Url</c>).</summary>
    public static IOpsNotifier From(Func<string, string?> setting, Action<string> log) => (setting("Operations:Notify:Adapter") ?? LogNotifier.Name) switch
    {
        SimulatedNotifier.Name => new SimulatedNotifier(),
        WebhookNotifier.Name => new WebhookNotifier(new Uri(setting("Operations:Notify:Url") ?? throw new InvalidOperationException("Operations:Notify:Adapter=webhook needs Operations:Notify:Url"))),
        LogNotifier.Name => new LogNotifier(log),
        var a => throw new InvalidOperationException($"No notification adapter {a}: write simulator, webhook or log"),
    };
}

/// <summary>
/// Turnaround limits per payment (R-05-68): the bank's <c>tatLimit</c> in force on the day a payment was accepted, the scheme's
/// where the bank set one, else the platform's (P0D, the default, sets no limit). A payment is in breach when it is open longer
/// than its limit after its acceptance, or ended later than that. The breach list is worked out from the read model; each breach
/// is alerted once, as a fact on the console's chain (kind <c>tat</c>, code <c>breached</c>) and through the notification port.
/// </summary>
public sealed class Turnaround(StoreOptions ops, OperationsDials dials, ConsoleRecord record, IOpsNotifier notifier, TimeProvider? clock = null)
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
        var out_ = new List<JsonObject>();
        foreach (var s in rows)
        {
            var key = $"{s.Tenant}|{s.Scheme}|{s.AcceptedAt.UtcDateTime:yyyy-MM-dd}";
            if (!limits.TryGetValue(key, out var limit)) limits[key] = limit = await dials.For(s.Tenant, s.Scheme, OperationsDials.TatLimit, s.AcceptedAt, ct);
            if (limit.Value <= TimeSpan.Zero) continue;
            var f = JsonNode.Parse(s.FieldsJson) as JsonObject;
            DateTimeOffset? ended = (string?)f?["endedAt"] is { } e && DateTimeOffset.TryParse(e, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
            if (openOnly && ended is not null) continue;
            var elapsed = (ended ?? now) - s.AcceptedAt;
            if (elapsed <= limit.Value) continue;
            out_.Add(new JsonObject
            {
                ["id"] = s.Id, ["kind"] = s.Kind, ["tenant"] = s.Tenant, ["scheme"] = s.Scheme, ["direction"] = s.Direction, ["fileName"] = s.FileName, ["status"] = s.Status,
                ["acceptedAt"] = s.AcceptedAt.ToString("O"), ["endedAt"] = ended?.ToString("O"), ["open"] = ended is null,
                ["limit"] = OperationsDials.Iso(limit.Value), ["parameter"] = limit.Parameter, ["source"] = limit.Source,
                ["elapsed"] = OperationsDials.Iso(TimeSpan.FromSeconds(Math.Floor(elapsed.TotalSeconds))),
                ["over"] = OperationsDials.Iso(TimeSpan.FromSeconds(Math.Floor((elapsed - limit.Value).TotalSeconds))),
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
            var data = new JsonObject { ["subject"] = id, ["tenant"] = b["tenant"]?.DeepClone(), ["scheme"] = b["scheme"]?.DeepClone(), ["limit"] = b["limit"]?.DeepClone(), ["elapsed"] = b["elapsed"]?.DeepClone(), ["open"] = b["open"]?.DeepClone(), ["parameter"] = b["parameter"]?.DeepClone(), ["adapter"] = notifier.Adapter };
            await record.Note(ConsoleRecord.TatKind, "breached", data, "runtime:operations", ct);
            try { await notifier.Notify(new OpsAlert("tat.breached", (string)b["tenant"]!, id, (string?)b["scheme"], $"Turnaround limit {(string?)b["limit"]} passed: {(string?)b["elapsed"]} since acceptance", (JsonObject)data.DeepClone()), ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                await record.Note(ConsoleRecord.TatKind, "alert-failed", new JsonObject { ["subject"] = id, ["adapter"] = notifier.Adapter, ["why"] = e.GetType().Name }, "runtime:operations", ct);
            }
            alerted.Add(id);
            n++;
        }
        return n;
    }
}
