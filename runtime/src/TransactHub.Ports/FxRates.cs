using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Ports;

/// <summary>One exchange rate the bank's rates service gave: one unit of <c>From</c> is <c>Rate</c> units of <c>To</c>, as of <c>AsOf</c>.</summary>
public sealed record FxQuote(string From, string To, decimal Rate, DateTimeOffset AsOf);

/// <summary>The bank's exchange-rate service (<c>p.port.fx-rates.v1</c>, operation <c>rates</c>; R-09-177).</summary>
public interface IFxRates
{
    string Adapter { get; }
    Task<IReadOnlyList<FxQuote>> Rates(CancellationToken ct = default);
}

/// <summary>
/// The adapters of the rates port (R-09-177; owner's decision of 23 November 2026, docs/OWNER-ACTIONS.md A2, Issue I-189): the
/// <c>simulator</c>, for test environments only, and <c>gateway-rest</c>, the bank's rates service behind a REST interface, called
/// through its circuit breaker like every production adapter (R-09-155). A quote that is not a currency pair of three capital
/// letters, a rate that is not above zero, or a time that is not an instant is refused: the feed never publishes a guess.
/// </summary>
public static class FxRateAdapters
{
    public const string Port = "p.port.fx-rates.v1";

    public static IFxRates Create(PortBinding b, HttpClient? http = null, ISecretResolver? secrets = null, TimeProvider? clock = null) => b.Adapter switch
    {
        SimulatedFxRates.Name => new SimulatedFxRates(b, clock ?? TimeProvider.System),
        RestFxRates.Name => new RestFxRates(b, http ?? new HttpClient(), secrets ?? SecretResolver.FromEnvironment()),
        _ => throw new PortBindingException($"No rates adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };

    static readonly System.Text.RegularExpressions.Regex Ccy = new("^[A-Z]{3}$");

    /// <summary>A quote read from a service's answer, checked; a malformed one is refused with the reason.</summary>
    public static FxQuote Quote(string port, JsonNode? q)
    {
        var from = (string?)q?["from"] ?? "";
        var to = (string?)q?["to"] ?? "";
        if (!Ccy.IsMatch(from) || !Ccy.IsMatch(to) || from == to) throw new FxRatesRefused($"{port}: '{from}/{to}' is not a pair of two currencies");
        var rate = q?["rate"] is JsonValue v && (v.TryGetValue<decimal>(out var d) || (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d))) ? d : 0m;
        if (rate <= 0m) throw new FxRatesRefused($"{port}: the rate of {from}/{to} is not above zero");
        if ((string?)q?["asOf"] is not { } at || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var asOf))
            throw new FxRatesRefused($"{port}: the rate of {from}/{to} has no asOf instant");
        return new FxQuote(from, to, rate, asOf.ToUniversalTime());
    }
}

/// <summary>A rates service's answer the feed will not publish.</summary>
public sealed class FxRatesRefused(string message) : Exception(message);

/// <summary>
/// The simulator (test environments only): setting <c>rates</c>, pairs as <c>AED/USD=0.2723;AED/EUR=0.25</c> (synthetic); every
/// quote is as of now, or <c>ageMinutes</c> earlier (default 0) to stand for a feed that has stopped; <c>answer: rejected</c> makes
/// the service fail, as one that does not answer.
/// </summary>
public sealed class SimulatedFxRates(PortBinding b, TimeProvider clock) : IFxRates
{
    public const string Name = "simulator";
    public string Adapter => Name;

    public Task<IReadOnlyList<FxQuote>> Rates(CancellationToken ct = default)
    {
        if (b.Setting("answer", "accepted") == "rejected") throw new HttpRequestException($"{b.Port}: the simulator does not answer");
        var at = clock.GetUtcNow().AddMinutes(-int.Parse(b.Setting("ageMinutes", "0"), CultureInfo.InvariantCulture));
        var list = new List<FxQuote>();
        foreach (var pair in b.Setting("rates", "AED/USD=0.2723").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (ccy, rate) = (pair.Split('=')[0], pair.Split('=').ElementAtOrDefault(1) ?? "");
            list.Add(FxRateAdapters.Quote(b.Port, new JsonObject { ["from"] = ccy.Split('/')[0], ["to"] = ccy.Split('/').ElementAtOrDefault(1), ["rate"] = rate, ["asOf"] = at.ToString("O") }));
        }
        return Task.FromResult<IReadOnlyList<FxQuote>>(list);
    }
}

/// <summary>
/// The bank's rates service behind a REST interface: <c>GET {baseUrl}{ratesPath}</c> (default <c>/rates</c>), answering
/// <c>{"rates": [{"from", "to", "rate", "asOf"}]}</c>. The credential by <c>secretRef</c> as a bearer token, or in the header
/// <c>credentialHeader</c> names. 5xx, 429 and timeouts are transient; the circuit breaker stops calling a failing service a while.
/// </summary>
public sealed class RestFxRates(PortBinding b, HttpClient http, ISecretResolver secrets) : IFxRates
{
    public const string Name = "gateway-rest";
    public string Adapter => Name;

    public async Task<IReadOnlyList<FxQuote>> Rates(CancellationToken ct = default)
    {
        using var res = await Rest.Send(http, secrets, b, HttpMethod.Get, "ratesPath", "/rates", null, ct);
        var j = await Rest.Json(res, ct);
        return (j["rates"] as JsonArray ?? throw new FxRatesRefused($"{b.Port}: the answer holds no rates")).Select(q => FxRateAdapters.Quote(b.Port, q)).ToList();
    }
}
