using System.Text.Json.Nodes;

namespace TransactHub.Model;

/// <summary>
/// What a workflow carries (plan W1): which payment, under which release, as of which date, and the few
/// facts its flow branches on. Parties, accounts and amounts are not here: activities read them from the
/// store by id, so engine history holds no personal data.
/// </summary>
public sealed record PaymentContext
{
    public required string Tenant { get; init; }
    /// <summary>The payment or bulk this run is about.</summary>
    public required string SubjectId { get; init; }
    public required SubjectKind SubjectKind { get; init; }
    /// <summary>The scheme scope, e.g. "uae.wps".</summary>
    public required string Scheme { get; init; }
    public required Direction Direction { get; init; }
    /// <summary>The release this payment was accepted under; it finishes on it.</summary>
    public required string ReleaseId { get; init; }
    /// <summary>The date rules are evaluated as of, unless a rule says "as of the step".</summary>
    public required DateTimeOffset AsOf { get; init; }
    /// <summary>Branch facts, declared by the package. Only scalar values; the context guard refuses others.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Facts { get; init; } = new Dictionary<string, JsonNode?>();

    public PaymentContext With(string fact, JsonNode? value)
    {
        ContextGuard.CheckFact(fact, value);
        var next = new Dictionary<string, JsonNode?>(Facts) { [fact] = value?.DeepClone() };
        return this with { Facts = next };
    }
}

public enum SubjectKind { Payment, Bulk }
public enum Direction { Outward, Inward }

/// <summary>
/// Keeps personal data out of the context: facts are scalars with short values, and no fact key names a
/// party field. It is a guard, not a classifier: the store is where personal data lives.
/// </summary>
public static class ContextGuard
{
    static readonly string[] Forbidden = ["name", "email", "iban", "account", "cif", "address", "phone", "mobile", "passport", "emirates"];
    public const int MaxValueLength = 64;

    public static void CheckFact(string key, JsonNode? value)
    {
        var k = key.ToLowerInvariant();
        foreach (var f in Forbidden)
            if (k.Contains(f)) throw new ArgumentException($"Context fact '{key}' names personal data; read it from the store by the payment id.");
        if (value is JsonObject or JsonArray) throw new ArgumentException($"Context fact '{key}' must be a scalar.");
        if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > MaxValueLength)
            throw new ArgumentException($"Context fact '{key}' is longer than {MaxValueLength} characters.");
    }
}
