using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Ports;

// The core-banking port (plan W6, P5). The platform's activities ask it for a balance and post a batch of
// legs; which system answers, and how, is a port binding in the bank's configuration, never code. The
// source's bank fork read 13 CoreBanking:* settings (mode, variant, service code, host id, txn flag,
// upload branch, auth status, balance flag, paths); here they are the settings of a binding.

public sealed record PostingLeg(string Kind, string Branch, string Account, decimal Amount, string Currency, string TxnCode, string Narration);
public sealed record PostingBatch(string Reference, DateOnly ValueDate, IReadOnlyList<PostingLeg> Legs);
public sealed record BalanceAnswer(string Account, decimal Available, string Currency, string Status);
/// <summary>Core banking's answer to a batch. <paramref name="Lines"/>: how each leg came out, when the adapter posts line by line (file-bulk).</summary>
public sealed record PostingAnswer(string Reference, bool Posted, string Code, string? HostReference, IReadOnlyList<PostingLineResult>? Lines = null);
/// <summary>What core banking knows of a posting by its reference (R-08-19): posted, with its host reference, or not posted.</summary>
public sealed record PostingInquiry(string Reference, bool Known, bool Posted, string? HostReference);

public interface ICoreBanking
{
    /// <summary>The adapter this port is bound to, as the binding names it.</summary>
    string Adapter { get; }
    Task<BalanceAnswer> Balance(string branch, string account, CancellationToken ct = default);
    Task<PostingAnswer> Post(PostingBatch batch, CancellationToken ct = default);
    /// <summary>Asks for the fate of a posting by its reference, as sent (R-08-19): what reconciles a posting whose answer was lost.</summary>
    Task<PostingInquiry> Inquire(string reference, CancellationToken ct = default);
}

/// <summary>Builds the adapter a binding names. A new core-banking system is a new adapter here and a binding in the bank's configuration.</summary>
public static class CoreBankingAdapters
{
    /// <param name="transfer">The tenant's file-transfer port, for an adapter that posts by file (<see cref="FileBulkCoreBanking"/>).</param>
    public static ICoreBanking Create(PortBinding b, HttpClient? http = null, ISecretResolver? secrets = null, Func<IFileTransfer>? transfer = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) => b.Adapter switch
    {
        SimulatedCoreBanking.Name => new SimulatedCoreBanking(b),
        GatewayRestCoreBanking.Name => new GatewayRestCoreBanking(b, http ?? new HttpClient(), secrets),
        FileBulkCoreBanking.Name => FileBulk(b, http ?? new HttpClient(), secrets,
            transfer ?? throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant} needs the tenant's {FileTransferAdapters.Port}"), delay),
        _ => throw new PortBindingException($"No core-banking adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };

    static FileBulkCoreBanking FileBulk(PortBinding b, HttpClient http, ISecretResolver? secrets, Func<IFileTransfer> transfer, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        var t = transfer();
        return b.Setting("upload", RestBulkUploadGateway.Name) switch
        {
            SimulatedBulkUploadGateway.Name => new FileBulkCoreBanking(b, t, new SimulatedBulkUploadGateway(b, t), new SimulatedCoreBanking(b), delay),
            RestBulkUploadGateway.Name => new FileBulkCoreBanking(b, t, new RestBulkUploadGateway(b, http, secrets), new GatewayRestCoreBanking(b, http, secrets), delay),
            var u => throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant} names upload '{u}': write gateway-rest or simulator"),
        };
    }
}

/// <summary>An in-memory core banking for tests and the example bank: every account holds what the binding's `openingBalance` says.</summary>
public sealed class SimulatedCoreBanking(PortBinding b) : ICoreBanking
{
    public const string Name = "simulator";
    public string Adapter => Name;
    public readonly List<PostingBatch> Posted = new();
    public IReadOnlyList<PostingBatch> Snapshot() { lock (Posted) return Posted.ToList(); }

    public Task<BalanceAnswer> Balance(string branch, string account, CancellationToken ct = default) =>
        Task.FromResult(new BalanceAnswer(account, decimal.Parse(b.Setting("openingBalance", "1000000"), System.Globalization.CultureInfo.InvariantCulture), b.Setting("currency", "AED"), b.Setting("accountStatus", "NORM")));

    /// <summary>Posts every batch, except one with a leg on the binding's <c>rejectAccount</c> (a test scenario): that one is declined with code 1.</summary>
    public Task<PostingAnswer> Post(PostingBatch batch, CancellationToken ct = default)
    {
        var reject = b.Setting("rejectAccount", "");
        if (reject.Length > 0 && batch.Legs.Any(l => l.Account == reject)) return Task.FromResult(new PostingAnswer(batch.Reference, false, "1", null));
        lock (Posted)
        {
            // The same reference is the same posting: core banking answers it again, it does not post twice.
            var i = Posted.FindIndex(p => p.Reference == batch.Reference);
            if (i < 0) { Posted.Add(batch); i = Posted.Count - 1; }
            return Task.FromResult(new PostingAnswer(batch.Reference, true, "0", $"SIM-{i + 1:D6}"));
        }
    }

    /// <summary>A posting is known when this core banking holds a batch of that reference.</summary>
    public Task<PostingInquiry> Inquire(string reference, CancellationToken ct = default)
    {
        lock (Posted)
        {
            var i = Posted.FindIndex(p => p.Reference == reference);
            return Task.FromResult(i < 0 ? new PostingInquiry(reference, true, false, null) : new PostingInquiry(reference, true, true, $"SIM-{i + 1:D6}"));
        }
    }
}

/// <summary>
/// A core banking behind an integration gateway that speaks REST/JSON with header-borne routing (the shape
/// the source's bank fork used): function id, external host id, channel, txn flag. Every value that was a
/// bank constant is a setting of the binding.
/// </summary>
public sealed class GatewayRestCoreBanking(PortBinding b, HttpClient http, ISecretResolver? secrets = null) : ICoreBanking
{
    public const string Name = "gateway-rest";
    public string Adapter => Name;
    readonly ISecretResolver secrets = secrets ?? SecretResolver.FromEnvironment();
    internal HttpClient Http => http;
    Uri Url(string path) => new(new Uri(b.Required("baseUrl")), b.Required(path));

    internal async Task<HttpRequestMessage> Request(string pathSetting, string functionSetting, string reference, JsonObject body, CancellationToken ct)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, Url(pathSetting)) { Content = JsonContent.Create(body) };
        m.Headers.Add("X-Function-Id", b.Required(functionSetting));
        m.Headers.Add("X-External-Host-Id", b.Required("externalHostId"));
        m.Headers.Add("X-Channel", b.Setting("channel", "WPS"));
        m.Headers.Add("X-Txn-Type", "RQST");
        m.Headers.Add("X-Txn-Flag", b.Required("txnFlag"));
        m.Headers.Add("X-Ref", reference);
        // The gateway's credential, by reference only (R-06-22): `secretRef` names where the installation keeps it
        // (env:NAME, file:/path, vault:mount/path#field). A binding that names one and cannot have it fails the call.
        if (b.Setting("secretRef", "") is { Length: > 0 } r)
            m.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (await secrets.Resolve(r, ct)).Reveal());
        return m;
    }

    public async Task<BalanceAnswer> Balance(string branch, string account, CancellationToken ct = default)
    {
        var body = new JsonObject { ["serviceCode"] = b.Required("serviceCode"), ["branch"] = branch, ["account"] = account };
        using var res = await GuardedHttp.Send(http, b, await Request("balancePath", "balanceFunctionId", $"{b.Setting("referencePrefix", "")}{account}", body, ct), ct, ct);
        res.EnsureSuccessStatusCode();
        var j = (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        return new BalanceAnswer(account, (decimal)j["available"]!, (string?)j["currency"] ?? "AED", (string?)j["status"] ?? "");
    }

    public async Task<PostingAnswer> Post(PostingBatch batch, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["serviceCode"] = b.Required("serviceCode"),
            ["uploadBranch"] = b.Required("uploadBranch"),
            ["balanceFlag"] = b.Required("balanceFlag"),
            ["authStatus"] = b.Required("authStatus"),
            ["reference"] = b.Setting("referencePrefix", "") + batch.Reference,
            ["valueDate"] = batch.ValueDate.ToString("yyyyMMdd"),
            ["entries"] = new JsonArray(batch.Legs.Select(l => (JsonNode?)new JsonObject
            {
                ["kind"] = l.Kind, ["branch"] = l.Branch, ["account"] = l.Account, ["amount"] = l.Amount, ["currency"] = l.Currency, ["txnCode"] = l.TxnCode, ["narration"] = l.Narration,
            }).ToArray()),
        };
        using var res = await GuardedHttp.Send(http, b, await Request("postingPath", "postingFunctionId", batch.Reference, body, ct), ct, ct);
        res.EnsureSuccessStatusCode();
        var j = (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        var code = (string?)j["code"] ?? "";
        return new PostingAnswer(batch.Reference, code == b.Setting("successCode", "0"), code, (string?)j["hostReference"]);
    }

    /// <summary>
    /// The gateway's inquiry by reference (settings <c>inquiryPath</c>, <c>inquiryFunctionId</c>): it answers <c>posted</c> with the
    /// host reference, or <c>notFound</c>. A gateway that does not know the reference answers 404, read as not posted.
    /// </summary>
    public async Task<PostingInquiry> Inquire(string reference, CancellationToken ct = default)
    {
        var body = new JsonObject { ["serviceCode"] = b.Required("serviceCode"), ["reference"] = b.Setting("referencePrefix", "") + reference };
        using var res = await GuardedHttp.Send(http, b, await Request("inquiryPath", "inquiryFunctionId", reference, body, ct), ct, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return new PostingInquiry(reference, true, false, null);
        res.EnsureSuccessStatusCode();
        var j = (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        var status = (string?)j["status"] ?? "";
        return new PostingInquiry(reference, status is "posted" or "notFound", status == "posted", (string?)j["hostReference"]);
    }
}
