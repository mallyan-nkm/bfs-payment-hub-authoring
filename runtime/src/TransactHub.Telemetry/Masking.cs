using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace TransactHub.Telemetry;

/// <summary>
/// What keeps personal data out of telemetry (R-09-35). Two rules, applied to every span attribute, span status,
/// log attribute, log message and exception before any exporter sees them:
/// a key that names personal data (a name, an account, an IBAN, an email, a phone, an identity number) has its
/// value replaced; and every string value has IBANs, emails and runs of eight or more digits masked, keeping
/// the last four characters so an operator can still tell two apart. The hosts write ids and outcomes only;
/// this is the guard behind that rule, and <c>TelemetryMaskingTests</c> proves it on a WPS run.
/// </summary>
public static partial class Masking
{
    public const string Masked = "[masked]";

    [GeneratedRegex(@"\b[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\b")]
    private static partial Regex Iban();

    // Bounded parts, as RFC 5321 bounds them, so a long text is masked in linear time (review 2026-11, ReDoS).
    [GeneratedRegex(@"[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,253}\.[A-Za-z]{2,24}")]
    private static partial Regex Email();

    // A run of digits that stands alone, spaces allowed: an account, card, employee or identity number, a phone. Dates keep their dashes.
    [GeneratedRegex(@"(?<![0-9A-Za-z])\+?[0-9][0-9 ]{6,}[0-9](?![0-9A-Za-z])")]
    private static partial Regex LongDigits();

    static readonly string[] PersonalWords = ["name", "iban", "account", "acct", "email", "phone", "mobile", "msisdn", "passport", "emirates", "address", "birth", "cif", "nationalid", "employee", "person", "party"];
    // Keys of the OpenTelemetry semantic conventions and of the engines that hold no personal data, though a word above is in them.
    static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "db.name", "db.namespace", "db.system.name", "db.operation.name", "service.name", "messaging.destination.name", "rpc.method", "rpc.service",
        "code.function.name", "exception.type", "error.type", "th.party.kind",
    };

    /// <summary>Whether a key names personal data, whatever its value.</summary>
    public static bool IsPersonalKey(string key)
    {
        if (Known.Contains(key)) return false;
        var last = key[(key.LastIndexOf('.') + 1)..].Replace("_", "").Replace("-", "").ToLowerInvariant();
        return PersonalWords.Any(last.Contains);
    }

    static string Tail(string s)
    {
        var digits = new string(s.Where(char.IsLetterOrDigit).ToArray());
        return "****" + (digits.Length > 4 ? digits[^4..] : "");
    }

    /// <summary>A string with every IBAN, email and long number in it masked.</summary>
    public static string Scrub(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = Iban().Replace(s, m => Tail(m.Value));
        s = Email().Replace(s, "[masked-email]");
        s = LongDigits().Replace(s, m => m.Value.Count(char.IsDigit) >= 8 ? Tail(m.Value) : m.Value);
        return s;
    }

    /// <summary>The value an exporter may see for a key.</summary>
    public static object? Value(string key, object? v) => v switch
    {
        null => null,
        _ when IsPersonalKey(key) => Masked,
        string s => Scrub(s),
        string[] a => a.Select(Scrub).ToArray(),
        _ => v,
    };
}

/// <summary>Masks every attribute and the status of each span as it ends, before the exporter's processor.</summary>
public sealed class MaskingSpanProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity a)
    {
        foreach (var (k, v) in a.TagObjects.ToList())
        {
            var m = Masking.Value(k, v);
            if (!Equals(m, v)) a.SetTag(k, m);
        }
        if (a.StatusDescription is { Length: > 0 } d)
        {
            var m = Masking.Scrub(d);
            if (m != d) a.SetStatus(a.Status, m);
        }
        // An event (an exception's message and stack among them) cannot be changed once added, and Activity
        // has no way to remove one: the events are taken off and added back masked.
        var events = a.Events.ToList();
        if (events.Count == 0 || !events.Any(NeedsMask)) return;
        if (EventsField is null)
        {
            // This runtime keeps its events elsewhere: mark the span, and TelemetryMaskingTests fails on it.
            a.SetTag("th.events.unmasked", events.Count);
            return;
        }
        EventsField.SetValue(a, null);
        foreach (var e in events)
            a.AddEvent(new ActivityEvent(e.Name, e.Timestamp, new ActivityTagsCollection(e.Tags.Select(t => new KeyValuePair<string, object?>(t.Key, Masking.Value(t.Key, t.Value))))));
    }

    static readonly System.Reflection.FieldInfo? EventsField = typeof(Activity).GetField("_events", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    static bool NeedsMask(ActivityEvent e) => e.Tags.Any(t => t.Value is string s ? !Equals(Masking.Value(t.Key, s), s) : Masking.IsPersonalKey(t.Key));
}

/// <summary>Masks the attributes, message and exception of each log record before the exporter's processor.</summary>
public sealed partial class MaskingLogProcessor : BaseProcessor<LogRecord>
{
    [GeneratedRegex(@"\{([^{}:,]+)(?:[,:][^{}]*)?\}")]
    private static partial Regex Hole();

    public override void OnEnd(LogRecord r)
    {
        string? template = null;
        if (r.Attributes is { } attrs)
        {
            var masked = attrs.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Key == "{OriginalFormat}" ? kv.Value : Masking.Value(kv.Key, kv.Value))).ToList();
            r.Attributes = masked;
            template = masked.FirstOrDefault(kv => kv.Key == "{OriginalFormat}").Value as string;
            // The message is written again from its template and the masked values: a name has no pattern to find it by.
            if (template is not null && r.FormattedMessage is not null)
            {
                var values = masked.Where(kv => kv.Key != "{OriginalFormat}").GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);
                r.FormattedMessage = Hole().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" : m.Value);
            }
        }
        if (template is null && r.FormattedMessage is { } fm) r.FormattedMessage = Masking.Scrub(fm);
        if (r.Body is { } b) r.Body = Masking.Scrub(b);
        if (r.Exception is { } e) r.Exception = new MaskedException(e.GetType().FullName ?? "Exception", Masking.Scrub(e.Message));
    }
}

/// <summary>An exception as a log record carries it after masking: its type and a masked message, no stack data.</summary>
public sealed class MaskedException(string type, string message) : Exception($"{type}: {message}")
{
    public string OriginalType { get; } = type;
}
