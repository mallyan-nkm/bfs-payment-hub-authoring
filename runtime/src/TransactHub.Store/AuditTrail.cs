using System.Text.Json.Nodes;

namespace TransactHub.Store;

/// <summary>
/// A console's audit page asks its trail by person, action and date (R-05-75, R-05-76, Issue I-202). A person matches the fact's
/// actor or the name its data records (<c>by</c>), in part and in any case; an action matches the fact's kind, its code or the
/// system action it names (<c>name</c>, <c>action</c> of a service action); the dates are inclusive days, UTC.
/// </summary>
public sealed record AuditFilter(string? Person = null, string? Action = null, DateOnly? From = null, DateOnly? To = null)
{
    public static AuditFilter Of(Func<string, string?> query) => new(
        Blank(query("person")), Blank(query("action")), Day(query("from")), Day(query("to")));

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static DateOnly? Day(string? s) => DateOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    public bool Matches(FactRow f)
    {
        var day = DateOnly.FromDateTime(f.At.UtcDateTime);
        if (From is { } from && day < from) return false;
        if (To is { } to && day > to) return false;
        if (Person is null && Action is null) return true;
        JsonObject? data = null;
        try { data = JsonNode.Parse(f.DataJson) as JsonObject; } catch (System.Text.Json.JsonException) { }
        bool Has(string? text, string part) => text is not null && text.Contains(part, StringComparison.OrdinalIgnoreCase);
        string? S(string k) => data?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (Person is { } p && !Has(f.Actor, p) && !Has(S("by"), p)) return false;
        if (Action is { } a && !string.Equals(f.Kind, a, StringComparison.OrdinalIgnoreCase) && !string.Equals(f.Code, a, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(S("name"), a, StringComparison.OrdinalIgnoreCase) && !string.Equals(S("action"), a, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
