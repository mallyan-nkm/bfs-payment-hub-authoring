using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Ports;

// The channel's enquiries of core banking (R-09-100, Issue I-184): a balance, a customer's accounts and a charge preview, asked of
// the bank's binding of p.port.core-banking.v1. The port's contract in the factory names reserve, release, post, verify and
// reverse only; these three are the runtime's, on the same binding and its settings, until the port's next release names them.

public sealed record AccountSummary(string Account, string Currency, string Type, string Status, string? Branch);
public sealed record ChargeQuote(string Scheme, string MessageKind, int Records, decimal Amount, string Currency, decimal Charge, decimal Vat, string ChargeCurrency, string Source);

public interface ICoreBankingEnquiries
{
    string Adapter { get; }
    Task<BalanceAnswer> Balance(string branch, string account, CancellationToken ct = default);
    /// <summary>The accounts core banking holds for a customer (its CIF).</summary>
    Task<IReadOnlyList<AccountSummary>> Accounts(string customer, CancellationToken ct = default);
    /// <summary>What core banking would charge a customer for a file of a scheme: a preview, priced again when the file is processed.</summary>
    Task<ChargeQuote> Charges(string customer, string scheme, string messageKind, int records, decimal amount, string currency, CancellationToken ct = default);
}

public static class CoreBankingEnquiryAdapters
{
    public static ICoreBankingEnquiries Create(PortBinding b, HttpClient? http = null, ISecretResolver? secrets = null) => b.Adapter switch
    {
        SimulatedCoreBanking.Name => new SimulatedCoreBankingEnquiries(b),
        // A file-bulk binding posts by file and asks its balances of the gateway (FileBulkCoreBanking); its enquiries go there too.
        GatewayRestCoreBanking.Name or FileBulkCoreBanking.Name => new GatewayRestCoreBankingEnquiries(b, new GatewayRestCoreBanking(b, http ?? new HttpClient(), secrets)),
        _ => throw new PortBindingException($"No core-banking adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };
}

/// <summary>
/// The simulator's enquiries, from the binding's settings only: <c>accounts.&lt;customer&gt;</c> (a comma list of accounts),
/// <c>openingBalance</c>, <c>currency</c>, and the charge it quotes (<c>chargeFixed</c>, <c>chargePerRecord</c>, <c>vatRate</c>; each 0 unless set).
/// </summary>
public sealed class SimulatedCoreBankingEnquiries(PortBinding b) : ICoreBankingEnquiries
{
    readonly SimulatedCoreBanking core = new(b);
    public string Adapter => SimulatedCoreBanking.Name;

    static decimal D(string s) => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);

    public Task<BalanceAnswer> Balance(string branch, string account, CancellationToken ct = default) => core.Balance(branch, account, ct);

    public Task<IReadOnlyList<AccountSummary>> Accounts(string customer, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AccountSummary>>(b.Setting($"accounts.{customer}", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => new AccountSummary(a, b.Setting("currency", "AED"), b.Setting("accountType", "current"), b.Setting("accountStatus", "NORM"), b.Setting("branch", "") is { Length: > 0 } br ? br : null)).ToList());

    public Task<ChargeQuote> Charges(string customer, string scheme, string messageKind, int records, decimal amount, string currency, CancellationToken ct = default)
    {
        var charge = Math.Round(D(b.Setting("chargeFixed", "0")) + D(b.Setting("chargePerRecord", "0")) * records, 2, MidpointRounding.AwayFromZero);
        var vat = Math.Round(charge * D(b.Setting("vatRate", "0")), 2, MidpointRounding.AwayFromZero);
        return Task.FromResult(new ChargeQuote(scheme, messageKind, records, amount, currency, charge, vat, b.Setting("currency", "AED"), "simulator"));
    }
}

/// <summary>
/// The gateway's enquiries, on the binding's own routing headers: <c>accountsPath</c> and <c>accountsFunctionId</c>, <c>chargesPath</c>
/// and <c>chargesFunctionId</c>. A binding that names neither answers that it is not bound, never a made-up value.
/// </summary>
public sealed class GatewayRestCoreBankingEnquiries(PortBinding b, GatewayRestCoreBanking core) : ICoreBankingEnquiries
{
    public string Adapter => b.Adapter;

    public Task<BalanceAnswer> Balance(string branch, string account, CancellationToken ct = default) => core.Balance(branch, account, ct);

    public async Task<IReadOnlyList<AccountSummary>> Accounts(string customer, CancellationToken ct = default)
    {
        var body = new JsonObject { ["serviceCode"] = b.Required("serviceCode"), ["customer"] = customer };
        using var res = await GuardedHttp.Send(core.Http, b, await core.Request("accountsPath", "accountsFunctionId", $"{b.Setting("referencePrefix", "")}{customer}", body, ct), ct, ct);
        res.EnsureSuccessStatusCode();
        var j = (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        return (j["accounts"] as JsonArray ?? []).OfType<JsonObject>().Select(a => new AccountSummary((string)a["account"]!, (string?)a["currency"] ?? "", (string?)a["type"] ?? "", (string?)a["status"] ?? "", (string?)a["branch"])).ToList();
    }

    public async Task<ChargeQuote> Charges(string customer, string scheme, string messageKind, int records, decimal amount, string currency, CancellationToken ct = default)
    {
        var body = new JsonObject { ["serviceCode"] = b.Required("serviceCode"), ["customer"] = customer, ["scheme"] = scheme, ["messageKind"] = messageKind, ["records"] = records, ["amount"] = amount, ["currency"] = currency };
        using var res = await GuardedHttp.Send(core.Http, b, await core.Request("chargesPath", "chargesFunctionId", $"{b.Setting("referencePrefix", "")}{customer}", body, ct), ct, ct);
        res.EnsureSuccessStatusCode();
        var j = (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        return new ChargeQuote(scheme, messageKind, records, amount, currency, (decimal)j["charge"]!, (decimal?)j["vat"] ?? 0m, (string?)j["currency"] ?? currency, b.Adapter);
    }
}
