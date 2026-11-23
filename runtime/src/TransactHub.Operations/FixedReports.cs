using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The fixed reports a view declares (R-05-67, Issue I-144): the WPS view's SIF summary, SIF detail and upload history are its
/// declared reports, not screens of the scheme's own. Each is a CSV of declared fields with the values the read model keeps, so a
/// personal field is masked as its view declares (and reads <c>[erased]</c> once erased); one row per payment of the view's scheme,
/// per line, or per kept document of the kind the report names. The caller's tenants and the list's filters bound it, as the
/// export's (R-05-59); every export of one is an audit fact (R-05-65), which the API records.
/// </summary>
public sealed class FixedReports(StoreOptions ops, ViewCatalog views)
{
    public sealed record Column(string Key, string Label);

    public JsonArray List() => new(views.All.SelectMany(v => (v.Subject?["reports"] as JsonArray ?? []).OfType<JsonObject>().Select(r => (JsonNode)new JsonObject
    {
        ["view"] = v.Name, ["release"] = v.Release, ["scheme"] = v.Scheme, ["id"] = (string?)r["id"], ["title"] = (string?)r["title"], ["rows"] = (string?)r["rows"],
        ["document"] = (string?)r["document"], ["oldScreen"] = (string?)r["oldScreen"],
        ["columns"] = new JsonArray(Columns(v, r).Select(c => (JsonNode)new JsonObject { ["key"] = c.Key, ["label"] = c.Label }).ToArray()),
    })).ToArray());

    static readonly Dictionary<string, string> DocLabels = new() { ["fileName"] = "File name", ["at"] = "Received at", ["size"] = "Size (bytes)", ["source"] = "Kept as", ["direction"] = "Direction", ["signed"] = "Signed" };

    static List<Column> Columns(OperationsView v, JsonObject r)
    {
        var fields = (v.Subject?["fields"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(f => (string)f["id"]!, f => (string?)f["label"]);
        var lines = (Sources.Path(v.Subject, "lines.fields") as JsonArray ?? []).OfType<JsonObject>().ToDictionary(f => (string)f["id"]!, f => (string?)f["label"]);
        return (r["columns"] as JsonArray ?? []).Select(c => (string)c!).Select(c =>
            c.StartsWith("line.", StringComparison.Ordinal) ? new Column(c, lines.GetValueOrDefault(c[5..]) ?? c)
            : c.StartsWith("document.", StringComparison.Ordinal) ? new Column(c, DocLabels.GetValueOrDefault(c[9..]) ?? c)
            : new Column(c, fields.GetValueOrDefault(c) ?? c)).ToList();
    }

    public (OperationsView view, JsonObject report)? Find(string view, string id)
    {
        var v = views.All.FirstOrDefault(x => x.Name == view);
        var r = (v?.Subject?["reports"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => (string?)x["id"] == id);
        return v is null || r is null ? null : (v, r);
    }

    public async Task<(string csv, int rows, bool cut)> Csv(OperationsView v, JsonObject report, IReadOnlyCollection<string> tenants, ReportFilter f, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var x = o.Subjects.AsNoTracking().Where(s => tenants.Contains(s.Tenant));
        if (v.Scheme is { } scheme) x = x.Where(s => s.Scheme == scheme);
        if (f.Direction is { } dir) x = x.Where(s => s.Direction == dir);
        if (f.Status is { } status) x = x.Where(s => s.Status == status);
        if (f.Kind is { } kind) x = x.Where(s => s.Kind == kind);
        if (f.From is { } from) x = x.Where(s => s.AcceptedAt >= from);
        if (f.To is { } to) x = x.Where(s => s.AcceptedAt < to);
        var cols = Columns(v, report);
        var rowsKind = (string?)report["rows"] ?? "subject";
        var docKind = (string?)report["document"];
        if (rowsKind == "documents") { var dk = docKind; x = x.Where(s => o.Documents.Any(d => d.SubjectId == s.Id && d.Kind == dk)); }
        var subjects = await x.OrderBy(s => s.AcceptedAt).ThenBy(s => s.Id).Take(Reports.MaxRows + 1).ToListAsync(ct);
        var cut = subjects.Count > Reports.MaxRows;
        if (cut) subjects = subjects.Take(Reports.MaxRows).ToList();
        var sb = new StringBuilder();
        sb.Append(string.Join(',', cols.Select(c => Reports.Cell(c.Label)))).Append("\r\n");
        var n = 0;
        static string? Text(JsonNode? n) => n switch { null => null, JsonValue jv => jv.ToString(), _ => n.ToJsonString() };
        void Line(JsonObject subj, JsonObject? line, OpsDocument? doc)
        {
            sb.Append(string.Join(',', cols.Select(c => Reports.Cell(
                c.Key.StartsWith("line.", StringComparison.Ordinal) ? Text(line?[c.Key[5..]])
                : c.Key.StartsWith("document.", StringComparison.Ordinal) ? DocValue(doc, c.Key[9..])
                : Text(subj[c.Key]))))).Append("\r\n");
            n++;
        }
        foreach (var chunk in subjects.Chunk(500))
        {
            var ids = chunk.Select(s => s.Id).ToList();
            var lines = rowsKind == "lines" ? (await o.Lines.AsNoTracking().Where(l => ids.Contains(l.BulkId)).OrderBy(l => l.LineNo).ToListAsync(ct)).GroupBy(l => l.BulkId).ToDictionary(g => g.Key, g => g.ToList()) : null;
            var docs = rowsKind == "documents" ? (await o.Documents.AsNoTracking().Where(d => d.SubjectId != null && ids.Contains(d.SubjectId) && d.Kind == docKind).OrderBy(d => d.At).ToListAsync(ct)).GroupBy(d => d.SubjectId!).ToDictionary(g => g.Key, g => g.ToList()) : null;
            foreach (var s in chunk)
            {
                var subj = JsonNode.Parse(s.FieldsJson) as JsonObject ?? new JsonObject();
                if (lines is not null) foreach (var l in lines.GetValueOrDefault(s.Id) ?? []) Line(subj, JsonNode.Parse(l.FieldsJson) as JsonObject, null);
                else if (docs is not null) foreach (var d in docs.GetValueOrDefault(s.Id) ?? []) Line(subj, null, d);
                else Line(subj, null, null);
                if (n > Reports.MaxRows) { cut = true; break; }
            }
            if (n > Reports.MaxRows) break;
        }
        return (sb.ToString(), n, cut);
    }

    static string? DocValue(OpsDocument? d, string attr) => d is null ? null : attr switch
    {
        "fileName" => d.FileName, "at" => d.At.ToString("O", CultureInfo.InvariantCulture), "size" => d.Size.ToString(CultureInfo.InvariantCulture),
        "source" => d.Source, "direction" => d.Direction, "signed" => d.SigningKeyId is null ? "no" : "yes", _ => null,
    };
}
