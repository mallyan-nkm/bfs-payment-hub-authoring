using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace TransactHub.Operations;

/// <summary>
/// An operations view (artefact kind <c>operations-view</c>, R-05-48): what a pack declares, as data, of how operators
/// see its payments and legs: the fields and where each comes from in the store, their labels and groups, the list's
/// columns and filters, the lines of a bulk, the documents, and the operator actions that apply. The console renders
/// any view the same way; a new scheme gets its console by declaring one.
/// </summary>
public sealed class OperationsView(JsonObject doc)
{
    public JsonObject Doc { get; } = doc;
    public string Name => (string)Doc["name"]!;
    public string Release => (string?)Doc["release"] ?? "1.0.0";
    public string Key => $"{Name}@{Release}";
    /// <summary>The scheme the view is for, or null for the platform's view (scope p).</summary>
    public string? Scheme => (string?)Doc["scheme"];
    public JsonObject? Subject => Doc["subject"] as JsonObject;
    public IEnumerable<JsonObject> Legs => (Doc["legs"] as JsonArray ?? []).OfType<JsonObject>();
}

/// <summary>The views an installation has: read from the published artefacts (content-configurations/artefacts) or a folder.</summary>
public sealed class ViewCatalog
{
    readonly List<OperationsView> views = new();
    public IReadOnlyList<OperationsView> All => views;

    public ViewCatalog(IEnumerable<OperationsView> vs) => views.AddRange(vs);

    /// <summary>Every <c>operations-view</c> document under a folder, newest release of each name.</summary>
    public static ViewCatalog FromFolder(string folder)
    {
        var found = new Dictionary<string, OperationsView>();
        if (Directory.Exists(folder))
            foreach (var f in Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                JsonObject? d;
                try { d = Yaml.ToJson(File.ReadAllText(f)) as JsonObject; } catch { continue; }
                if ((string?)d?["kind"] != "operations-view" || d["name"] is null) continue;
                var v = new OperationsView(d);
                if (!found.TryGetValue(v.Name, out var have) || Compare(v.Release, have.Release) > 0) found[v.Name] = v;
            }
        return new ViewCatalog(found.Values.OrderBy(v => v.Name, StringComparer.Ordinal));
    }

    static int Compare(string a, string b) =>
        a.Split('.').Select(int.Parse).Zip(b.Split('.').Select(int.Parse), (x, y) => x.CompareTo(y)).FirstOrDefault(c => c != 0);

    /// <summary>The platform's view and the scheme's, in that order, for a subject of the scheme.</summary>
    public IEnumerable<OperationsView> For(string scheme) =>
        views.Where(v => v.Scheme is null).Concat(views.Where(v => v.Scheme == scheme));

    public OperationsView? SchemeView(string scheme) => views.FirstOrDefault(v => v.Scheme == scheme);
}

/// <summary>YAML to JSON, as the spec's tools read it (YAML 1.1 scalars kept as strings unless they are numbers or booleans).</summary>
public static class Yaml
{
    public static JsonNode? ToJson(string text)
    {
        var ys = new YamlStream();
        ys.Load(new StringReader(text));
        return ys.Documents.Count == 0 ? null : Node(ys.Documents[0].RootNode);
    }

    static JsonNode? Node(YamlNode n) => n switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value ?? "", Node(kv.Value)))),
        YamlSequenceNode s => new JsonArray(s.Children.Select(Node).ToArray()),
        YamlScalarNode v when v.Style is YamlDotNet.Core.ScalarStyle.Plain => Scalar(v.Value),
        YamlScalarNode v => JsonValue.Create(v.Value),
        _ => null,
    };

    static JsonNode? Scalar(string? s)
    {
        if (s is null or "~" or "null") return null;
        if (s is "true") return JsonValue.Create(true);
        if (s is "false") return JsonValue.Create(false);
        if (Regex.IsMatch(s, @"^-?\d+$") && long.TryParse(s, out var l)) return JsonValue.Create(l);
        if (Regex.IsMatch(s, @"^-?\d+\.\d+$") && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(s);
    }
}

/// <summary>Masking as the source's operations screens did it (piiMask.ts): keep a few characters at each end.</summary>
public static class Mask
{
    public static string? Apply(string? kind, string? value)
    {
        if (value is null || kind is null) return value;
        var v = value.Trim();
        if (v.Length == 0) return v;
        return kind switch
        {
            "account" or "id" => Middle(v, 4, 4),
            "name" => v[..1] + new string('*', Math.Max(3, v.Length - 1)),
            "full" => new string('*', v.Length),
            _ => v,
        };
    }

    static string Middle(string v, int keepStart, int keepEnd) =>
        v.Length <= keepStart + keepEnd ? new string('*', v.Length) : v[..keepStart] + new string('*', Math.Max(4, v.Length - keepStart - keepEnd)) + v[^keepEnd..];
}

/// <summary>What a view's sources read, for one payment or bulk: store rows by name, steps, holds, documents and lines.</summary>
public sealed class SourceContext
{
    public JsonObject Subject { get; init; } = new();
    public JsonObject? Bulk { get; init; }
    public JsonObject? Payment { get; init; }
    /// <summary>SupplementaryData of the bulk or the payment, keyed by scope ("uae.wps").</summary>
    public JsonObject Ext { get; init; } = new();
    /// <summary>Step facts in order: { instance, step, activity, outcome, reasonCode, at, attempt, header, status, writes (nested) }.</summary>
    public List<JsonObject> Steps { get; init; } = new();
    public JsonObject? Hold { get; init; }
    public List<JsonObject> Documents { get; init; } = new();
    /// <summary>
    /// The deliveries of the payment's sent files as the File API's outbox recorded them on its chain (R-05-103, R-09-236), in order:
    /// { kind, outcome (delivered, failed, undelivered), at, attempt, adapter, code, fileName, folder, file }.
    /// </summary>
    public List<JsonObject> Deliveries { get; init; } = new();
    /// <summary>Each line: { lineNo, endToEndId, amount, currency, creditorAccount, creditorAgent, ext, facts }.</summary>
    public List<JsonObject> Lines { get; init; } = new();
    /// <summary>The instance a leg's sources are limited to; null for the payment as a whole.</summary>
    public string? Instance { get; init; }
}

/// <summary>
/// The source language of a view's fields (R-05-48). A source is one of:
/// <c>subject.x</c>, <c>bulk.x</c>, <c>payment.x</c>, <c>ext.&lt;scope&gt;.&lt;field&gt;</c>, <c>header.x</c>,
/// <c>step:&lt;step path&gt;.&lt;outcome|reasonCode|at|attempt|activity|status.x|writes.x&gt;</c>,
/// <c>hold.x</c>, <c>message:&lt;kind&gt;.&lt;fileName|at|size|count|direction&gt;</c>,
/// <c>delivery:&lt;message&gt;.&lt;outcome|at|attempts|adapter|code|fileName|folder&gt;</c> (R-05-103),
/// <c>lines.count</c>, <c>lines.count(&lt;line source&gt; = | != &lt;value or source&gt;)</c>, <c>lines.sum(&lt;line source&gt;)</c>;
/// and for a line: <c>line.x</c>, <c>line.ext.&lt;scope&gt;.&lt;field&gt;</c>, <c>line.facts.x</c>.
/// </summary>
public static class Sources
{
    public static readonly string[] Roots = ["subject", "bulk", "payment", "ext", "header", "step", "hold", "message", "delivery", "lines", "line"];
    static readonly Regex StepRx = new(@"^step:(?<path>[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*?)\.(?<attr>outcome|reasonCode|at|attempt|activity|instance|status(\..+)?|writes\..+)$");
    static readonly Regex MessageRx = new(@"^message:(?<kind>[a-z0-9.\-]+?)\.(?<attr>fileName|at|size|count|direction)$");
    static readonly Regex DeliveryRx = new(@"^delivery:(?<kind>[a-z0-9.\-]+?)\.(?<attr>outcome|at|attempts|adapter|code|fileName|folder)$");
    static readonly Regex CountRx = new(@"^lines\.count\((?<left>[^=!]+?)\s*(?<op>!=|=)\s*(?<right>.+)\)$");
    static readonly Regex SumRx = new(@"^lines\.sum\((?<src>.+)\)$");

    public static (string? step, string? attr) StepOf(string source)
    {
        var m = StepRx.Match(source);
        return m.Success ? (m.Groups["path"].Value, m.Groups["attr"].Value) : (null, null);
    }

    public static string? MessageKindOf(string source)
    {
        var m = MessageRx.Match(source);
        return m.Success ? m.Groups["kind"].Value : null;
    }

    public static JsonNode? Eval(string source, SourceContext c, JsonObject? line = null)
    {
        source = source.Trim();
        if (source.StartsWith("line.", StringComparison.Ordinal)) return line is null ? null : Path(line, source[5..]);
        if (source.StartsWith("step:", StringComparison.Ordinal))
        {
            var (step, attr) = StepOf(source);
            if (step is null) return null;
            var hit = c.Steps.LastOrDefault(s => (c.Instance is null || (string?)s["instance"] == c.Instance) && (string?)s["step"] == step);
            return hit is null ? null : attr == "attempt" ? JsonValue.Create(c.Steps.Count(s => (string?)s["step"] == step && (c.Instance is null || (string?)s["instance"] == c.Instance))) : Path(hit, attr!);
        }
        if (source.StartsWith("message:", StringComparison.Ordinal))
        {
            var m = MessageRx.Match(source);
            if (!m.Success) return null;
            var docs = c.Documents.Where(d => (string?)d["kind"] == m.Groups["kind"].Value).ToList();
            if (m.Groups["attr"].Value == "count") return JsonValue.Create(docs.Count);
            return docs.Count == 0 ? null : Path(docs[^1], m.Groups["attr"].Value);
        }
        if (source.StartsWith("delivery:", StringComparison.Ordinal))
        {
            var m = DeliveryRx.Match(source);
            return m.Success ? Delivery(c.Deliveries.Where(d => (string?)d["kind"] == m.Groups["kind"].Value).ToList(), m.Groups["attr"].Value) : null;
        }
        if (source == "lines.count") return JsonValue.Create(c.Lines.Count);
        if (CountRx.Match(source) is { Success: true } cm)
        {
            var n = c.Lines.Count(l =>
            {
                var left = Text(Eval(cm.Groups["left"].Value, c, l));
                var r = cm.Groups["right"].Value.Trim();
                var right = Roots.Any(x => r.StartsWith(x + ".", StringComparison.Ordinal) || r.StartsWith(x + ":", StringComparison.Ordinal)) ? Text(Eval(r, c, l)) : r.Trim('\'', '"');
                return cm.Groups["op"].Value == "=" ? left == right : left != right;
            });
            return JsonValue.Create(n);
        }
        if (SumRx.Match(source) is { Success: true } sm)
            return JsonValue.Create(c.Lines.Sum(l => Number(Eval(sm.Groups["src"].Value, c, l)) ?? 0m));
        var dot = source.IndexOf('.');
        if (dot < 0) return null;
        var root = source[..dot];
        var rest = source[(dot + 1)..];
        return root switch
        {
            "subject" => Path(c.Subject, rest),
            "bulk" => c.Bulk is null ? null : Path(c.Bulk, rest),
            "payment" => c.Payment is null ? null : Path(c.Payment, rest),
            "ext" => Path(c.Ext, rest),
            "header" => c.Steps.LastOrDefault(s => c.Instance is null || (string?)s["instance"] == c.Instance)?["header"] is JsonObject h ? Path(h, rest) : null,
            "hold" => c.Hold is null ? null : Path(c.Hold, rest),
            _ => null,
        };
    }

    /// <summary>
    /// The delivery of a message's file (R-05-103): the outcome of its latest fact (<c>delivered</c>, <c>failed</c> while the outbox
    /// still tries, <c>undelivered</c> once its attempts ended); <c>at</c> when it was last delivered, else nothing; <c>attempts</c> the
    /// attempts made; <c>code</c> the error of a delivery not made. Nothing is said of a file the outbox has not yet tried.
    /// </summary>
    static JsonNode? Delivery(List<JsonObject> ds, string attr)
    {
        if (ds.Count == 0) return null;
        var last = ds[^1];
        var delivered = ds.LastOrDefault(d => (string?)d["outcome"] == "delivered");
        return attr switch
        {
            "outcome" => JsonValue.Create((string?)last["outcome"]),
            "at" => delivered?["at"]?.DeepClone(),
            "attempts" => JsonValue.Create(ds.Count(d => (string?)d["outcome"] is "delivered" or "failed")),
            "code" => (string?)last["outcome"] == "delivered" ? null : last["code"]?.DeepClone(),
            _ => last[attr]?.DeepClone(),
        };
    }

    /// <summary>A dotted path into an object; a key may itself hold dots (SupplementaryData's "uae.wps"), longest first.</summary>
    public static JsonNode? Path(JsonNode? n, string path)
    {
        if (path.Length == 0) return n;
        if (n is not JsonObject o) return null;
        var parts = path.Split('.');
        for (var take = parts.Length; take >= 1; take--)
        {
            var key = string.Join('.', parts.Take(take));
            if (o.TryGetPropertyValue(key, out var v)) return take == parts.Length ? v : Path(v, string.Join('.', parts.Skip(take)));
        }
        return null;
    }

    public static string? Text(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => n.ToJsonString(),
    };

    public static decimal? Number(JsonNode? n) =>
        n is JsonValue v && (v.TryGetValue<decimal>(out var d) || (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d))) ? d : null;
}

/// <summary>A view's fields evaluated for a subject (or a leg, or a line), masked as declared.</summary>
public static class ViewEval
{
    /// <summary>The value an erased field reads (R-05-70): every field that declares a mask, the personal ones, once erased.</summary>
    public const string Erased = "[erased]";

    public static JsonObject Fields(JsonArray? fields, SourceContext c, JsonObject? line = null, bool erased = false)
    {
        var o = new JsonObject();
        foreach (var f in (fields ?? []).OfType<JsonObject>())
        {
            var id = (string)f["id"]!;
            if (erased && f["mask"] is not null) { o[id] = Erased; continue; }
            var v = Sources.Eval((string)f["source"]!, c, line);
            if (v is JsonObject or JsonArray) { o[id] = v.DeepClone(); continue; }
            var mask = (string?)f["mask"];
            o[id] = mask is null ? v?.DeepClone() : JsonValue.Create(Mask.Apply(mask, Sources.Text(v)));
        }
        return o;
    }
}
