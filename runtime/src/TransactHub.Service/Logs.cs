using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TransactHub.Operations;
using TransactHub.Store;
using TransactHub.Telemetry;

namespace TransactHub.Service;

/// <summary>Masking for anything the service console shows or its assistant sends (R-09-35, R-09-42, R-09-44): the telemetry's rules.</summary>
public static class Masked
{
    /// <summary>A JSON value with every personal key's value replaced and every string scrubbed of IBANs, emails and long numbers.</summary>
    public static JsonNode? Json(JsonNode? n, string key = "") => n switch
    {
        null => null,
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Masking.IsPersonalKey(kv.Key) && kv.Value is JsonValue ? JsonValue.Create(Masking.Masked) : Json(kv.Value, kv.Key)))),
        JsonArray a => new JsonArray(a.Select(x => Json(x, key)).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Masking.Scrub(s)),
        _ => n.DeepClone(),
    };

    public static string Text(string? s) => Masking.Scrub(s ?? "");
}

/// <summary>One structured log record of this host, masked when it is kept.</summary>
public sealed record LogRecordView(DateTimeOffset At, string Level, string Category, string Message, int EventId, string? TraceId, string? Exception, JsonObject Properties);

/// <summary>
/// The host's own structured logs, kept masked in a ring buffer the console reads (R-09-42). The runtime keeps no queryable
/// store of other hosts' logs: they go to the installation's log back end over OTLP (Issue I-103).
/// </summary>
public sealed class LogBuffer(int capacity = 5000) : ILoggerProvider
{
    readonly ConcurrentQueue<LogRecordView> q = new();
    public IReadOnlyList<LogRecordView> Snapshot() => q.ToArray();

    public void Add(LogRecordView r)
    {
        q.Enqueue(r);
        while (q.Count > capacity && q.TryDequeue(out _)) { }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    sealed class Logger(LogBuffer b, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel l) => l >= LogLevel.Information;
        public void Log<TState>(LogLevel l, EventId id, TState state, Exception? e, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(l)) return;
            var props = new JsonObject();
            string? template = null;
            if (state is IEnumerable<KeyValuePair<string, object?>> kvs)
                foreach (var (k, v) in kvs)
                {
                    if (k == "{OriginalFormat}") { template = v as string; continue; }
                    var m = Masking.Value(k, v is string or null ? v : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
                    props[k] = m is null ? null : JsonValue.Create(m.ToString());
                }
            // The message is written again from its template and the masked values, as the telemetry's log processor does.
            var msg = template is null ? Masking.Scrub(fmt(state, e))
                : System.Text.RegularExpressions.Regex.Replace(template, @"\{([^{}:,]+)(?:[,:][^{}]*)?\}", m => props.TryGetPropertyValue(m.Groups[1].Value, out var v) ? (string?)v ?? "" : m.Value);
            b.Add(new LogRecordView(DateTimeOffset.UtcNow, l.ToString(), category, msg, id.Id, Activity.Current?.TraceId.ToString(),
                e is null ? null : $"{e.GetType().Name}: {Masking.Scrub(e.Message)}", props));
        }
    }
}

public sealed record LogFilter(string? Code, string? Leg, string? Scheme, string? Tenant, string? Payment, DateTimeOffset? From, DateTimeOffset? To, string? Source, int Take = 200);

/// <summary>The error and event log the console shows: the host's structured logs and the store's facts, masked, filtered.</summary>
public sealed class EventLog(StoreOptions store, StoreOptions ops, LogBuffer logs, ErrorDictionary dictionary)
{
    public async Task<JsonObject> Query(ServiceIdentity who, LogFilter f, CancellationToken ct = default)
    {
        var items = new List<JsonObject>();
        if (f.Source is null or "all" or "facts") items.AddRange(await Facts(who, f, ct));
        if ((f.Source is null or "all" or "logs") && f.Payment is null && f.Leg is null && f.Tenant is null && f.Scheme is null)
            foreach (var r in logs.Snapshot().Where(r => (f.From is null || r.At >= f.From) && (f.To is null || r.At <= f.To)))
            {
                var errors = dictionary.FoundIn(r.Message + " " + r.Exception).ToList();
                if (f.Code is { } c && !errors.Any(e => e.Code == c) && !r.Message.Contains(c)) continue;
                items.Add(new JsonObject
                {
                    ["source"] = "log", ["at"] = r.At.ToString("O"), ["level"] = r.Level, ["category"] = r.Category, ["message"] = r.Message, ["exception"] = r.Exception,
                    ["traceId"] = r.TraceId, ["properties"] = r.Properties.DeepClone(), ["errors"] = Links(errors),
                });
            }
        var sorted = items.OrderByDescending(i => (string?)i["at"], StringComparer.Ordinal).Take(Math.Clamp(f.Take, 1, 1000)).ToList();
        return new JsonObject { ["count"] = sorted.Count, ["items"] = new JsonArray(sorted.Select(x => (JsonNode)x).ToArray()) };
    }

    static JsonArray Links(IEnumerable<ErrorEntry> es) => new(es.Select(e => (JsonNode)new JsonObject { ["code"] = e.Code, ["title"] = e.Title, ["category"] = e.Category, ["runbook"] = e.Link }).ToArray());

    async Task<List<JsonObject>> Facts(ServiceIdentity who, LogFilter f, CancellationToken ct)
    {
        await using var o = OpsDbContext.Open(ops);
        var subjects = o.Subjects.AsNoTracking().AsQueryable();
        if (f.Tenant is { } t) subjects = subjects.Where(s => s.Tenant == t);
        if (f.Scheme is { } sc) subjects = subjects.Where(s => s.Scheme == sc);
        if (f.Payment is { } p) subjects = subjects.Where(s => s.Id == p || s.BulkId == p);
        var subj = (await subjects.Select(s => new { s.Id, s.Tenant, s.Scheme }).Take(5000).ToListAsync(ct)).Where(s => who.Sees(s.Tenant)).ToDictionary(s => s.Id);
        HashSet<string>? instances = null;
        if (f.Leg is { } leg)
            instances = (await o.Legs.AsNoTracking().Where(l => l.Leg == leg).Select(l => l.SubjectId + "|" + l.Instance).ToListAsync(ct)).ToHashSet();
        await using var db = StoreDbContext.Open(store);
        var ids = subj.Keys.ToList();
        var q = db.Facts.AsNoTracking().Where(x => ids.Contains(x.SubjectId) || (f.Payment == null && f.Tenant == null && f.Scheme == null && f.Leg == null && (x.SubjectKind == ServiceAudit.Kind || x.SubjectKind == "release")));
        if (f.From is { } from) q = q.Where(x => x.At >= StoreTime.Of(from));
        if (f.To is { } to) q = q.Where(x => x.At <= StoreTime.Of(to));
        var rows = await q.OrderByDescending(x => x.Id).Take(2000).ToListAsync(ct);
        var result = new List<JsonObject>();
        foreach (var r in rows)
        {
            var data = JsonNode.Parse(r.DataJson) as JsonObject ?? new JsonObject();
            if (instances is not null && !instances.Contains(r.SubjectId + "|" + (string?)data["instance"])) continue;
            var scheme = subj.TryGetValue(r.SubjectId, out var s) ? s.Scheme : null;
            var codes = new[] { (string?)data["reasonCode"], (string?)data["error"], (string?)data["code"], r.Kind == "step" ? null : r.Code }.OfType<string>().Distinct().ToList();
            var errors = codes.Select(c => dictionary.Find(c, scheme)).OfType<ErrorEntry>().ToList();
            if (f.Code is { } c && !codes.Contains(c)) continue;
            var isError = errors.Count > 0 || r.Code is "refused" or "reject" or "rejected" or "failed" or "expired" || (string?)data["result"] == "refused";
            result.Add(new JsonObject
            {
                ["source"] = "fact", ["at"] = r.At.ToString("O"), ["level"] = isError ? "Error" : "Information", ["subject"] = r.SubjectId, ["subjectKind"] = r.SubjectKind,
                ["tenant"] = s?.Tenant, ["scheme"] = scheme, ["seq"] = r.Seq, ["kind"] = r.Kind, ["code"] = r.Code, ["actor"] = r.Actor,
                ["step"] = (string?)data["step"], ["activity"] = (string?)data["activity"], ["instance"] = (string?)data["instance"],
                ["data"] = Masked.Json(data), ["errors"] = Links(errors),
                ["payment"] = s is null ? null : $"#/payments/{Uri.EscapeDataString(r.SubjectId)}",
                ["trace"] = (string?)data["traceId"],
            });
        }
        return result;
    }

    /// <summary>A payment's facts, masked, for its trace view and the assistant.</summary>
    public async Task<JsonObject?> Trace(ServiceIdentity who, string id, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var s = await o.Subjects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || !who.Sees(s.Tenant)) return null;
        var legs = await o.Legs.AsNoTracking().Where(l => l.SubjectId == id).Select(l => new { l.Instance, l.Leg, l.LastStep, l.LastOutcome }).ToListAsync(ct);
        var r = await Query(who, new LogFilter(null, null, null, null, id, null, null, "facts", 500), ct);
        return new JsonObject
        {
            ["payment"] = new JsonObject { ["id"] = s.Id, ["kind"] = s.Kind, ["tenant"] = s.Tenant, ["scheme"] = s.Scheme, ["direction"] = s.Direction, ["status"] = s.Status, ["lastStep"] = s.LastStep, ["lastOutcome"] = s.LastOutcome, ["hold"] = s.Hold, ["holdReason"] = Masked.Text(s.HoldReason), ["releaseId"] = s.ReleaseId, ["acceptedAt"] = s.AcceptedAt.ToString("O") },
            ["legs"] = new JsonArray(legs.Select(l => (JsonNode)new JsonObject { ["instance"] = l.Instance, ["leg"] = l.Leg, ["lastStep"] = l.LastStep, ["lastOutcome"] = l.LastOutcome }).ToArray()),
            ["facts"] = r["items"]!.DeepClone(),
            ["operationsConsole"] = $"#/payments/{Uri.EscapeDataString(id)}",
        };
    }
}
