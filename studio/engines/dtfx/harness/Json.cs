using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DtfxHarness;

/// <summary>Helpers for the DSL documents and the context, held as JSON nodes.</summary>
public static class J
{
    public static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static JsonNode? Parse(string? s) => string.IsNullOrEmpty(s) ? null : JsonNode.Parse(s);
    public static string Str(JsonNode? n) => n is null ? "null" : n.ToJsonString(Opts);
    public static JsonNode? Clone(JsonNode? n) => n?.DeepClone();
    public static JsonObject Obj(JsonNode? n) => n as JsonObject ?? new JsonObject();

    public static string? S(JsonNode? n, string key)
    {
        if (n is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is null) return null;
        return v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
    }

    public static bool Has(JsonNode? n, string key) => n is JsonObject o && o.ContainsKey(key);
    public static JsonNode? P(JsonNode? n, string key) => n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;
    public static JsonArray A(JsonNode? n, string key) => P(n, key) as JsonArray ?? new JsonArray();

    public static double? Num(JsonNode? n, string key) => D(P(n, key));

    /// <summary>A number, whether parsed from text or created in process from an int, long or double.</summary>
    public static double? D(JsonNode? v)
    {
        if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.Number) return null;
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<int>(out var i)) return i;
        return double.Parse(jv.ToJsonString(), CultureInfo.InvariantCulture);
    }

    public static bool Truthy(JsonNode? v)
    {
        if (v is null) return false;
        return v.GetValueKind() switch
        {
            JsonValueKind.False => false,
            JsonValueKind.Null => false,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => D(v) != 0,
            _ => true,
        };
    }

    /// <summary>The value as a string, as JavaScript's String() would give it for the kit's values.</summary>
    public static string? AsKey(JsonNode? v)
    {
        if (v is null) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Null => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => D(v)!.Value.ToString(CultureInfo.InvariantCulture),
            _ => v.ToJsonString(),
        };
    }

    static string[] Segs(string path) => Regex.Replace(path, @"^items\[\]", "items").Split('.');

    public static JsonNode? Get(JsonNode? obj, string path)
    {
        var cur = obj;
        foreach (var s in Segs(path))
        {
            if (cur is null) return null;
            if (cur is JsonObject o) cur = o.TryGetPropertyValue(s, out var v) ? v : null;
            else if (cur is JsonArray a && int.TryParse(s, out var i)) cur = i >= 0 && i < a.Count ? a[i] : null;
            else return null;
        }
        return cur;
    }

    public static void Set(JsonObject obj, string path, JsonNode? value)
    {
        var parts = Segs(path);
        JsonObject cur = obj;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var (name, index) = SplitIndex(parts[i]);
            if (index is int ix)
            {
                if (cur[name] is not JsonArray arr) { arr = new JsonArray(); cur[name] = arr; }
                while (arr.Count <= ix) arr.Add(new JsonObject());
                if (arr[ix] is not JsonObject next) { next = new JsonObject(); arr[ix] = next; }
                cur = next;
            }
            else
            {
                if (cur[name] is not JsonObject next) { next = new JsonObject(); cur[name] = next; }
                cur = next;
            }
        }
        var (last, lix) = SplitIndex(parts[^1]);
        if (lix is int l)
        {
            if (cur[last] is not JsonArray arr) { arr = new JsonArray(); cur[last] = arr; }
            while (arr.Count <= l) arr.Add(null);
            arr[l] = value?.Parent is null ? value : value.DeepClone();
        }
        else cur[last] = value?.Parent is null ? value : value.DeepClone();
    }

    static (string, int?) SplitIndex(string seg)
    {
        var m = Regex.Match(seg, @"^(.*)\[(\d+)\]$");
        return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value)) : (seg, null);
    }

    public static bool Covers(string root, string path)
    {
        var r = root.Replace("[]", "");
        var p = path.Replace("[]", "");
        return p == r || p.StartsWith(r + ".");
    }

    public static bool CoveredByAny(IEnumerable<string> roots, string path) => roots.Any(r => Covers(r, path));

    /// <summary>The paths where two contexts differ, as the studio's diffPaths gives them.</summary>
    public static List<string> DiffPaths(JsonNode? a, JsonNode? b, string prefix = "")
    {
        var outp = new List<string>();
        var keys = new List<string>();
        if (a is JsonObject ao) keys.AddRange(ao.Select(kv => kv.Key));
        if (b is JsonObject bo) foreach (var kv in bo) if (!keys.Contains(kv.Key)) keys.Add(kv.Key);
        foreach (var k in keys)
        {
            var p = prefix.Length > 0 ? $"{prefix}.{k}" : k;
            var x = P(a, k);
            var y = P(b, k);
            if (Str(x) == Str(y)) continue;
            if (x is JsonObject && y is JsonObject) outp.AddRange(DiffPaths(x, y, p));
            else outp.Add(p);
        }
        return outp;
    }

    public static List<string> Strings(JsonNode? arr)
    {
        var l = new List<string>();
        if (arr is JsonArray a) foreach (var x in a) if (x is not null) l.Add(AsKey(x)!);
        return l;
    }

    public static JsonArray Arr(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
}

/// <summary>ISO 8601 durations and the business calendar, as the studio's duration.ts has them.</summary>
public static class Duration
{
    static readonly Regex Re = new(@"^P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?$");

    public record D(double Years, double Months, double Weeks, double Days, double Hours, double Minutes, double Seconds);

    public static bool IsDuration(string? s) => s is not null && s != "P" && Re.IsMatch(s);

    public static D Parse(string s)
    {
        var m = Re.Match(s);
        if (!m.Success || s == "P" || s.EndsWith("T")) throw new FormatException($"Not an ISO 8601 duration: {s}");
        double n(int i) => m.Groups[i].Success ? double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        return new D(n(1), n(2), n(3), n(4), n(5), n(6), n(7));
    }

    public static long Ms(string s)
    {
        var d = Parse(s);
        if (d.Years != 0 || d.Months != 0) throw new FormatException($"Duration {s} has calendar parts");
        return (long)(((((d.Weeks * 7 + d.Days) * 24 + d.Hours) * 60 + d.Minutes) * 60 + d.Seconds) * 1000);
    }

    static readonly int[] Weekend = { 0, 6 };

    static bool IsBusinessDay(long t) => !Weekend.Contains((int)DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime.DayOfWeek);

    public static long Add(long t, string s, bool business = false)
    {
        var d = Parse(s);
        var outp = t;
        if (d.Years != 0 || d.Months != 0)
            outp = new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(outp).UtcDateTime.AddYears((int)d.Years).AddMonths((int)d.Months)).ToUnixTimeMilliseconds();
        var days = (long)(d.Weeks * 7 + d.Days);
        if (business)
        {
            for (long i = 0; i < days; i++)
            {
                outp += 86400000;
                while (!IsBusinessDay(outp)) outp += 86400000;
            }
        }
        else outp += days * 86400000;
        outp += (long)(((d.Hours * 60 + d.Minutes) * 60 + d.Seconds) * 1000);
        return outp;
    }

    public static DateTime Utc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    public static long MsOf(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    public static string Today(long ms) => Utc(ms).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Iso(long ms) => Utc(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
