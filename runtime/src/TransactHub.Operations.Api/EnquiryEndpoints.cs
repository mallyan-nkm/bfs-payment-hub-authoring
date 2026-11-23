using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Auth;
using TransactHub.Operations;
using TransactHub.Operations.Registers;
using TransactHub.Ports;
using TransactHub.Store;

namespace TransactHub.Operations.Api;

/// <summary>
/// The channel's enquiries of core banking (R-09-100, Issue I-184), beside the status enquiries of <see cref="ChannelEndpoints"/>,
/// for a system client with <c>channel-enquiry</c> in the bank. Each asks the bank's binding of <c>p.port.core-banking.v1</c> in
/// force now: the one its installed configuration release bound (kind <c>binding</c> in the store), else the installation's bank
/// folder (<c>Operations:Banks</c> or <c>TH_BANKS_DIR</c>, <c>&lt;bank&gt;/*.yaml</c>). A corporate is named by its id in the bank's
/// corporate register (R-05-62); an account must be one the register holds for it, or nothing is sent to core banking.
/// <list type="table">
/// <item><term>POST /api/channel/accounts/balance</term><description>{ corporateId, account, branch? }: the available balance</description></item>
/// <item><term>GET /api/channel/accounts?corporateId=</term><description>the accounts core banking holds for the corporate's CIF</description></item>
/// <item><term>GET /api/channel/charges/preview?corporateId=&amp;scheme=&amp;messageKind=&amp;records=&amp;amount=&amp;currency=</term><description>a charge preview</description></item>
/// </list>
/// </summary>
public static class EnquiryEndpoints
{
    public const string Port = "p.port.core-banking.v1";
    public const string BindingKind = "binding";
    public sealed record BalanceBody(string? CorporateId, string? Account, string? Branch);

    static IResult Problem(int status, string message, string? code = null) => Results.Json(new { error = message, errorCode = code }, statusCode: status);
    static string? Q(HttpContext http, string k) => http.Request.Query.TryGetValue(k, out var v) && v.ToString().Length > 0 ? v.ToString() : null;

    /// <summary>The one tenant the channel acts in: named (X-TH-Tenant or ?tenant=), or the only one its grant gives it.</summary>
    static (string? tenant, IResult? refused) Tenant(HttpContext http)
    {
        var named = http.Request.Headers["X-TH-Tenant"].ToString() is { Length: > 0 } h ? h : Q(http, "tenant");
        if (named is { Length: > 0 }) return RuntimeAuth.HasRoleFor(http.User, SystemRoles.ChannelEnquiry, named) ? (named, null) : (null, Problem(403, $"{RuntimeAuth.Actor(http.User)} has no {SystemRoles.ChannelEnquiry} role for {named}"));
        var mine = RuntimeAuth.RolesByTenant(http.User).Where(kv => kv.Key != "*" && kv.Value.Contains(SystemRoles.ChannelEnquiry)).Select(kv => kv.Key).ToList();
        return mine.Count == 1 ? (mine[0], null) : (null, Problem(mine.Count == 0 ? 403 : 400, mine.Count == 0 ? $"{RuntimeAuth.Actor(http.User)} has no {SystemRoles.ChannelEnquiry} role" : "Name the tenant (X-TH-Tenant)"));
    }

    /// <summary>The core-banking binding in force for the tenant now.</summary>
    public static async Task<PortBinding> Binding(StoreOptions store, string? banksDir, string tenant, DateTimeOffset now, CancellationToken ct)
    {
        await using (var db = StoreDbContext.Open(store))
        {
            var t = StoreTime.Of(now);
            var row = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == BindingKind && v.Tenant == tenant && v.Name == Port && v.EffectiveFrom <= t && v.PublishedAt <= t)
                .OrderByDescending(v => v.EffectiveFrom).ThenByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct);
            if (row is not null && JsonNode.Parse(row.BodyJson)?["binding"] is JsonObject b)
            {
                var settings = (b["settings"] as JsonObject ?? new JsonObject()).ToDictionary(kv => kv.Key, kv => kv.Value is JsonValue v ? v.ToString() : kv.Value?.ToJsonString() ?? "", StringComparer.Ordinal);
                if ((string?)b["secretRef"] is { Length: > 0 } sr) settings["secretRef"] = sr;
                return new PortBinding(tenant, Port, (string)b["adapter"]!, row.EffectiveFrom, settings);
            }
        }
        var dir = banksDir is null ? null : Path.Combine(banksDir, tenant);
        if (dir is null || !Directory.Exists(dir)) throw new PortBindingException($"{Port} is not bound for {tenant}: no configuration release binds it and no bank folder is set");
        return PortBindings.FromDirectory(dir).InForce(tenant, Port, now);
    }

    public static void MapEnquiries(this WebApplication app, StoreOptions store, string? banksDir, Func<PortBinding, ICoreBankingEnquiries>? adapters = null)
    {
        var api = app.MapGroup("/api/channel");
        adapters ??= b => CoreBankingEnquiryAdapters.Create(b);

        async Task<(ICoreBankingEnquiries? core, JsonObject? corporate, string? tenant, IResult? refused)> Prepare(HttpContext http, string? corporateId, RegisterService regs)
        {
            var (tenant, refused) = Tenant(http);
            if (refused is not null) return (null, null, null, refused);
            if (string.IsNullOrWhiteSpace(corporateId)) return (null, null, tenant, Problem(400, "Name the corporate (corporateId)"));
            var corporate = await regs.InForce(tenant!, "corporate", corporateId, DateTimeOffset.UtcNow, http.RequestAborted);
            if (corporate is null) return (null, null, tenant, Problem(404, $"No corporate {corporateId} is registered with the bank; nothing was sent to core banking", "CORPORATE_NOT_FOUND"));
            try { return (adapters(await Binding(store, banksDir, tenant!, DateTimeOffset.UtcNow, http.RequestAborted)), corporate, tenant, null); }
            catch (PortBindingException e) { return (null, null, tenant, Problem(503, e.Message, "CORE_BANKING_NOT_BOUND")); }
        }

        async Task<IResult> Ask(Func<Task<IResult>> f)
        {
            try { return await f(); }
            catch (PortBindingException e) { return Problem(503, e.Message, "CORE_BANKING_NOT_BOUND"); }
            catch (HttpRequestException) { return Problem(503, "Core banking did not answer; ask again later", "CORE_BANKING_UNAVAILABLE"); }
        }

        static List<string> Accounts(JsonObject corporate) => corporate["accounts"] is JsonArray a ? a.Select(x => x is JsonValue v ? v.ToString() : x?.ToJsonString() ?? "").ToList() : [];

        api.MapPost("/accounts/balance", (BalanceBody body, HttpContext http, RegisterService regs) => Ask(async () =>
        {
            var (core, corporate, _, refused) = await Prepare(http, body.CorporateId, regs);
            if (refused is not null) return refused;
            if (string.IsNullOrWhiteSpace(body.Account) || !Accounts(corporate!).Contains(body.Account.Trim(), StringComparer.Ordinal))
                return Problem(404, $"The account is not registered for corporate {body.CorporateId}; nothing was sent to core banking", "ACCOUNT_NOT_REGISTERED");
            var branch = body.Branch ?? (string?)corporate!["branch"] ?? "";
            var b = await core!.Balance(branch, body.Account.Trim(), http.RequestAborted);
            return Results.Ok(new { corporateId = body.CorporateId, account = b.Account, available = b.Available, currency = b.Currency, status = b.Status, adapter = core.Adapter, at = DateTimeOffset.UtcNow });
        }));

        api.MapGet("/accounts", (HttpContext http, RegisterService regs) => Ask(async () =>
        {
            var id = Q(http, "corporateId");
            var (core, corporate, _, refused) = await Prepare(http, id, regs);
            if (refused is not null) return refused;
            if ((string?)corporate!["cif"] is not { Length: > 0 } cif) return Problem(409, $"Corporate {id} has no CIF in the register; core banking is asked by CIF", "CORPORATE_HAS_NO_CIF");
            var held = await core!.Accounts(cif, http.RequestAborted);
            var registered = Accounts(corporate);
            return Results.Ok(new
            {
                corporateId = id, adapter = core.Adapter,
                accounts = held.Select(a => new { a.Account, a.Currency, a.Type, a.Status, a.Branch, registered = registered.Contains(a.Account, StringComparer.Ordinal) }),
            });
        }));

        api.MapGet("/charges/preview", (HttpContext http, RegisterService regs) => Ask(async () =>
        {
            var id = Q(http, "corporateId");
            var (core, corporate, _, refused) = await Prepare(http, id, regs);
            if (refused is not null) return refused;
            var scheme = Q(http, "scheme");
            var kind = Q(http, "messageKind");
            if (scheme is null || kind is null) return Problem(400, "Name the scheme and the message kind (for example uae.wps and uae.wps.sif)");
            if (!int.TryParse(Q(http, "records"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var records) || records < 1) return Problem(400, "records is the number of lines, at least 1");
            var amount = decimal.TryParse(Q(http, "amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out var am) ? am : 0m;
            var q = await core!.Charges((string?)corporate!["cif"] ?? id!, scheme, kind, records, amount, Q(http, "currency") ?? "AED", http.RequestAborted);
            return Results.Ok(new
            {
                corporateId = id, q.Scheme, q.MessageKind, q.Records, q.Amount, q.Currency, charge = q.Charge, vat = q.Vat, total = q.Charge + q.Vat, chargeCurrency = q.ChargeCurrency,
                source = q.Source, preview = true, note = "A preview: the charge is worked out again when the file is processed.",
            });
        }));
    }
}
