using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>What a report or an export covers: the caller's tenants, then the console's list filters.</summary>
public sealed record ReportFilter(string? Scheme = null, string? Direction = null, string? Status = null, DateTimeOffset? From = null, DateTimeOffset? To = null, string? Kind = null);

/// <summary>
/// The operations console's reports, dashboards and exports (R-05-59, Issue I-68), built only from the read model and the
/// operations views: no scheme has a screen of its own. A report counts payments and their lines and sums their amounts by a
/// dimension of the read model (tenant, scheme, direction, kind, status, outcome, hold, day, distribution) or by a field a
/// view declares as an exact filter. The dashboard adds, for each scheme view, the totals of the list columns it declares as
/// counts or amounts, and the counts by its exact filters. The export is the list as CSV: the read model's columns and the
/// views' list columns, with the values as the read model keeps them, masked as each view's field declares. Every query is
/// bounded to the tenants the caller holds the operator's role for; the caller passes them.
/// </summary>
public sealed class Reports(StoreOptions ops, ViewCatalog views)
{
    public static readonly string[] Dimensions = ["tenant", "scheme", "direction", "kind", "status", "outcome", "hold", "day", "distribution"];
    /// <summary>The most rows a report or an export reads at once; past it the answer says it is cut short.</summary>
    public const int MaxRows = 100_000;

    sealed record Row(string Id, string Kind, string Tenant, string Scheme, string Direction, string? Status, string? LastOutcome, string? Hold, DateTimeOffset AcceptedAt, string ReleaseId, decimal Amount, string Currency, int Count, string FieldsJson);

    async Task<(List<Row> rows, bool cut)> Load(OpsDbContext o, IReadOnlyCollection<string> tenants, ReportFilter f, CancellationToken ct)
    {
        var x = o.Subjects.AsNoTracking().Where(s => tenants.Contains(s.Tenant));
        if (f.Scheme is { } scheme) x = x.Where(s => s.Scheme == scheme);
        if (f.Direction is { } dir) x = x.Where(s => s.Direction == dir);
        if (f.Status is { } status) x = x.Where(s => s.Status == status);
        if (f.Kind is { } kind) x = x.Where(s => s.Kind == kind);
        if (f.From is { } from) x = x.Where(s => s.AcceptedAt >= from);
        if (f.To is { } to) x = x.Where(s => s.AcceptedAt < to);
        var rows = await x.OrderByDescending(s => s.AcceptedAt).ThenBy(s => s.Id).Take(MaxRows + 1)
            .Select(s => new Row(s.Id, s.Kind, s.Tenant, s.Scheme, s.Direction, s.Status, s.LastOutcome, s.Hold, s.AcceptedAt, s.ReleaseId, s.Amount, s.Currency, s.Count, s.FieldsJson))
            .ToListAsync(ct);
        var cut = rows.Count > MaxRows;
        return (cut ? rows.Take(MaxRows).ToList() : rows, cut);
    }

    /// <summary>The fields a report may group by: each view's exact filters, as <c>field:&lt;view&gt;:subject:&lt;id&gt;</c>.</summary>
    public JsonArray Fields() => new(views.All.SelectMany(v => (v.Subject?["filters"] as JsonArray ?? []).OfType<JsonObject>()
        .Where(fl => (string?)fl["match"] is null or "exact")
        .Select(fl => (JsonNode)new JsonObject
        {
            ["key"] = $"field:{v.Name}:subject:{(string)fl["field"]!}", ["view"] = v.Name, ["scheme"] = v.Scheme,
            ["label"] = (string?)fl["label"] ?? Label(v, (string)fl["field"]!),
        })).ToArray());

    static string Label(OperationsView v, string field) =>
        (v.Subject?["fields"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => (string?)x["id"] == field) is { } d ? (string?)d["label"] ?? field : field;

    static JsonArray Amounts(IEnumerable<Row> rows) => new(rows.GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => (JsonNode)new JsonObject { ["currency"] = g.Key, ["amount"] = g.Sum(r => r.Amount), ["payments"] = g.Count() }).ToArray());

    public async Task<JsonObject> Summary(IReadOnlyCollection<string> tenants, ReportFilter f, string by, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var (rows, cut) = await Load(o, tenants, f, ct);
        Func<Row, string?> key;
        if (by.StartsWith("field:", StringComparison.Ordinal))
        {
            if (!Fields().Any(x => (string?)x!["key"] == by)) throw new ArgumentException($"{by} is no exact filter a view declares");
            var field = by[6..];
            var ids = rows.Select(r => r.Id).ToList();
            var values = (await o.FieldValues.AsNoTracking().Where(v => v.Field == field && ids.Contains(v.SubjectId)).Select(v => new { v.SubjectId, v.Text }).ToListAsync(ct))
                .GroupBy(v => v.SubjectId).ToDictionary(g => g.Key, g => g.First().Text);
            key = r => values.GetValueOrDefault(r.Id);
        }
        else key = by switch
        {
            "tenant" => r => r.Tenant, "scheme" => r => r.Scheme, "direction" => r => r.Direction, "kind" => r => r.Kind,
            "status" => r => r.Status, "outcome" => r => r.LastOutcome, "hold" => r => r.Hold ?? "none",
            "day" => r => r.AcceptedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "distribution" => r => r.ReleaseId,
            _ => throw new ArgumentException($"A report groups by one of {string.Join(", ", Dimensions)} or a view's field, not {by}"),
        };
        var groups = rows.GroupBy(r => key(r) ?? "(none)").OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (JsonNode)new JsonObject { ["key"] = g.Key, ["payments"] = g.Count(), ["lines"] = g.Sum(r => r.Count), ["amounts"] = Amounts(g) }).ToArray();
        return new JsonObject { ["by"] = by, ["payments"] = rows.Count, ["lines"] = rows.Sum(r => r.Count), ["amounts"] = Amounts(rows), ["groups"] = new JsonArray(groups), ["cut"] = cut };
    }

    /// <summary>The dashboard: the totals, by scheme, status and day, and for each scheme view its declared measures and exact filters.</summary>
    public async Task<JsonObject> Dashboard(IReadOnlyCollection<string> tenants, ReportFilter f, CancellationToken ct = default)
    {
        var bySchemes = await Summary(tenants, f, "scheme", ct);
        var schemes = new JsonArray();
        await using var o = OpsDbContext.Open(ops);
        foreach (var v in views.All.Where(v => v.Scheme is not null && (f.Scheme is null || v.Scheme == f.Scheme)))
        {
            var (rows, _) = await Load(o, tenants, f with { Scheme = v.Scheme }, ct);
            if (rows.Count == 0) continue;
            var decl = (v.Subject?["fields"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(x => (string)x["id"]!, x => x);
            var columns = (v.Subject?["columns"] as JsonArray ?? []).Select(c => (string)c!).Where(decl.ContainsKey).ToList();
            var parsed = rows.Select(r => JsonNode.Parse(r.FieldsJson) as JsonObject ?? new JsonObject()).ToList();
            var measures = new JsonArray(columns.Where(c => (string?)decl[c]["format"] is "count" or "amount").Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c, ["label"] = (string?)decl[c]["label"] ?? c, ["format"] = (string?)decl[c]["format"],
                ["total"] = parsed.Sum(p => p[c] is JsonValue jv && decimal.TryParse(jv.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m),
            }).ToArray());
            var dims = new JsonArray();
            foreach (var fl in (v.Subject?["filters"] as JsonArray ?? []).OfType<JsonObject>().Where(x => (string?)x["match"] is null or "exact"))
            {
                var id = (string)fl["field"]!;
                // A masked field is grouped by its masked value: a report never shows more than the list.
                var counts = parsed.GroupBy(p => p[id]?.ToString() ?? "(none)").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(20)
                    .Select(g => (JsonNode)new JsonObject { ["value"] = g.Key, ["payments"] = g.Count() }).ToArray();
                dims.Add(new JsonObject { ["id"] = id, ["label"] = (string?)fl["label"] ?? Label(v, id), ["values"] = new JsonArray(counts) });
            }
            schemes.Add(new JsonObject { ["scheme"] = v.Scheme, ["view"] = v.Key, ["title"] = (string?)v.Doc["title"] ?? v.Name, ["payments"] = rows.Count, ["measures"] = measures, ["dimensions"] = dims });
        }
        return new JsonObject
        {
            ["payments"] = bySchemes["payments"]!.DeepClone(), ["lines"] = bySchemes["lines"]!.DeepClone(), ["amounts"] = bySchemes["amounts"]!.DeepClone(),
            ["byScheme"] = bySchemes["groups"]!.DeepClone(),
            ["byStatus"] = (await Summary(tenants, f, "status", ct))["groups"]!.DeepClone(),
            ["byDay"] = (await Summary(tenants, f, "day", ct))["groups"]!.DeepClone(),
            ["holds"] = (await Summary(tenants, f, "hold", ct))["groups"]!.DeepClone(),
            ["schemes"] = schemes, ["cut"] = bySchemes["cut"]!.DeepClone(),
        };
    }

    /// <summary>
    /// The list as CSV (RFC 4180): the read model's columns, then the platform view's list columns, then, for a scheme, its
    /// view's. Values are the read model's, which keeps a personal field masked as its view declares; nothing is read from the
    /// store's payloads. A cell that a spreadsheet would read as a formula starts with an apostrophe.
    /// </summary>
    public async Task<(string csv, int rows, bool cut)> Csv(IReadOnlyCollection<string> tenants, ReportFilter f, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var (rows, cut) = await Load(o, tenants, f, ct);
        var platform = views.All.FirstOrDefault(v => v.Scheme is null);
        var scheme = f.Scheme is { } s ? views.SchemeView(s) : null;
        var extra = new List<(string id, string label)>();
        foreach (var v in new[] { platform, scheme }.OfType<OperationsView>())
        {
            var decl = (v.Subject?["fields"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(x => (string)x["id"]!, x => x);
            foreach (var c in (v.Subject?["columns"] as JsonArray ?? []).Select(c => (string)c!))
                if (decl.TryGetValue(c, out var d) && !extra.Any(e => e.id == c)) extra.Add((c, (string?)d["label"] ?? c));
        }
        var sb = new StringBuilder();
        string[] head = ["Id", "Kind", "Tenant", "Scheme", "Direction", "Status", "Last outcome", "Hold", "Accepted at", "Distribution", "Amount", "Currency", "Lines"];
        sb.Append(string.Join(',', head.Concat(extra.Select(e => e.label)).Select(Cell))).Append("\r\n");
        foreach (var r in rows)
        {
            var fields = JsonNode.Parse(r.FieldsJson) as JsonObject ?? new JsonObject();
            var cells = new[] { r.Id, r.Kind, r.Tenant, r.Scheme, r.Direction, r.Status, r.LastOutcome, r.Hold, r.AcceptedAt.ToString("O", CultureInfo.InvariantCulture), r.ReleaseId,
                    r.Amount.ToString(CultureInfo.InvariantCulture), r.Currency, r.Count.ToString(CultureInfo.InvariantCulture) }
                .Concat(extra.Select(e => fields[e.id] is JsonValue jv ? jv.ToString() : fields[e.id]?.ToJsonString()));
            sb.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");
        }
        return (sb.ToString(), rows.Count, cut);
    }

    static string Cell(string? v)
    {
        v ??= "";
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0]) && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) v = "'" + v;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
