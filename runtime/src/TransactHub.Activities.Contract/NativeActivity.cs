using System.Globalization;
using System.Text.Json.Nodes;

namespace TransactHub.Activities.Native;

/// <summary>
/// One attempt of a native activity: the activity's view of the context, its parameter values, the engine's time, the
/// tenant it runs for and, for a step inside a loop, its level (<c>item</c>). A tenant is always given when the activity
/// needs one; it is never defaulted.
/// </summary>
public sealed record NativeAttempt(string Activity, string? Release, JsonObject View, JsonObject Params, DateTimeOffset Now, string? Tenant = null, string? Level = null);

/// <summary>What a native activity answers: an outcome of its contract, an ISO reason code, and its writes (path to value).</summary>
public sealed record NativeResult(string Outcome, string? ReasonCode, JsonObject Writes)
{
    /// <summary>A hold the runtime opened (R-05-56): the hold's id and expiry; the engine awaits the operator's decision.</summary>
    public JsonObject? Held { get; init; }
}

/// <summary>A native code-behind (spec Part 3, binding <c>native</c>; R-08-15 for a port the runtime answers).</summary>
public interface INativeActivity
{
    /// <summary>The activity line it implements, e.g. <c>uae.wps.validate.cbx-file.v1</c>.</summary>
    string Name { get; }
    NativeResult Run(NativeAttempt a);
}

/// <summary>An attempt that ends in an error of the activity's contract, not an outcome: retryable or not, as its contract says.</summary>
public sealed class NativeFailure(string name, string message, bool retryable) : Exception(message)
{
    public string Name { get; } = name;
    public bool Retryable { get; } = retryable;
}

/// <summary>Reading the studio's canonical context the way the native activities need it.</summary>
public static class NativeContext
{
    public static string S(JsonNode? n, string path)
    {
        JsonNode? cur = n;
        foreach (var p in path.Split('.')) cur = cur is JsonObject o ? o[p] : null;
        return cur?.ToString() ?? "";
    }

    public static JsonNode? Node(JsonNode? n, string path)
    {
        JsonNode? cur = n;
        foreach (var p in path.Split('.')) cur = cur is JsonObject o ? o[p] : null;
        return cur;
    }

    public static decimal D(JsonNode? n, string path) =>
        decimal.TryParse(S(n, path), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    public static JsonArray Items(JsonObject view) => view["items"] as JsonArray ?? new JsonArray();

    /// <summary>The UAE business time of an engine time (UTC+4, no daylight saving).</summary>
    public static DateTime Business(DateTimeOffset now) => now.ToUniversalTime().AddHours(4).DateTime;

    public static string Param(JsonObject p, string name) => p[name]?.ToString() ?? "";
}
