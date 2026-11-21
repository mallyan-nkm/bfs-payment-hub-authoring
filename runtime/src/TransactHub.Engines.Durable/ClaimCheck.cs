using System.Text.Json.Nodes;
using TransactHub.Engines;
using TransactHub.Model;
using TransactHub.Store;

namespace TransactHub.Engines.Durable;

/// <summary>
/// Where the payload of a run lives while the engine holds only references (R-09-10): the parties,
/// accounts and amounts, the items of a batch, the scheme's fields. Staged writes become part of the
/// payload only when the engine commits them, once.
/// </summary>
public interface IClaimCheck
{
    Dictionary<string, JsonNode?> RuleTraces { get; }
    Task Create(string r, JsonObject payload, AcceptInfo? accept = null);
    JsonObject Full(JsonNode? state, string r);
    JsonObject FullForItem(JsonNode? state, string r, int index);
    Task<string> Stage(string r, List<(string path, JsonNode? value)> writes);
    Task Commit(string token);
    Task Fork(string from, string to);
    Task Copy(string from, string to, IEnumerable<string> paths);
    int Count(string r, string path);
}

/// <summary>Who a run's payment belongs to, for accepting it into the store.</summary>
public sealed record AcceptInfo(string Tenant, string Scheme, Direction Direction, string ReleaseId);

/// <summary>The claim check of a kit run: in memory, as on every engine's harness.</summary>
public sealed class MemoryClaimCheck : IClaimCheck
{
    readonly ClaimCheckStore s = new();
    public Dictionary<string, JsonNode?> RuleTraces => s.RuleTraces;
    public Task Create(string r, JsonObject payload, AcceptInfo? accept = null) { s.Create(r, payload); return Task.CompletedTask; }
    public JsonObject Full(JsonNode? state, string r) => s.Full(state, r);
    public JsonObject FullForItem(JsonNode? state, string r, int index) => s.FullForItem(state, r, index);
    public Task<string> Stage(string r, List<(string path, JsonNode? value)> writes) => Task.FromResult(s.Stage(r, writes));
    public Task Commit(string token) { s.Commit(token); return Task.CompletedTask; }
    public Task Fork(string from, string to) { s.Fork(from, to); return Task.CompletedTask; }
    public Task Copy(string from, string to, IEnumerable<string> paths) { s.Copy(from, to, paths); return Task.CompletedTask; }
    public int Count(string r, string path) => s.Count(r, path);
}

/// <summary>
/// The claim check of a deployment (plan W2): every change to a payload is a fact in the store, written
/// before it is acknowledged, insert-only and chained per payload: created, a write staged, a staged
/// write committed, forked, copied. The payment itself is accepted into the ISO 20022 tables when its
/// run starts. Reads are served from a cache that <see cref="Load"/> rebuilds from the facts, so a host
/// that restarts goes on from what the store holds.
/// </summary>
public sealed class DurableClaimCheck(StoreOptions options, TimeProvider? clock = null) : IClaimCheck
{
    readonly ClaimCheckStore cache = new();
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<string, (string r, List<(string path, JsonNode? value)> writes)> staged = new();
    readonly HashSet<string> committed = new();
    int seq;
    public Dictionary<string, JsonNode?> RuleTraces => cache.RuleTraces;
    public const string Kind = "payload";
    /// <summary>What was accepted into the store, by payload reference.</summary>
    public readonly Dictionary<string, (string subjectId, string kind, int payments)> Accepted = new();

    async Task Fact(string r, string code, JsonObject data)
    {
        await using var db = StoreDbContext.Open(options);
        await new FactLog(db, clock).Append(r, Kind, new NewFact("payload", code, data, Actor: "claim-check"));
    }

    public async Task Create(string r, JsonObject payload, AcceptInfo? accept = null)
    {
        await gate.WaitAsync();
        try
        {
            await Fact(r, "create", new JsonObject { ["payload"] = J.Clone(payload) });
            cache.Create(r, (JsonObject)J.Clone(payload)!);
            if (accept is not null) await AcceptInto(r, payload, accept);
        }
        finally { gate.Release(); }
    }

    async Task AcceptInto(string r, JsonObject payload, AcceptInfo a)
    {
        await using var db = StoreDbContext.Open(options);
        var store = new PaymentStore(db, clock);
        if (CanonicalMapper.ToBulk(payload) is { } bulk)
        {
            var b = await store.AcceptBulk(a.Tenant, a.Scheme, a.Direction, bulk, a.ReleaseId);
            Accepted[r] = (b.BulkId, "bulk", b.PaymentIds.Count);
            await new FactLog(db, clock).Append(r, Kind, new NewFact("payload", "accepted", new JsonObject { ["bulk"] = b.BulkId, ["payments"] = b.PaymentIds.Count }, a.ReleaseId, "claim-check"));
        }
        else if (CanonicalMapper.ToPayment(payload, null, 0) is { } t)
        {
            var id = await store.AcceptPayment(a.Tenant, a.Scheme, a.Direction, t, a.ReleaseId);
            Accepted[r] = (id, "payment", 1);
            await new FactLog(db, clock).Append(r, Kind, new NewFact("payload", "accepted", new JsonObject { ["payment"] = id }, a.ReleaseId, "claim-check"));
        }
    }

    public JsonObject Full(JsonNode? state, string r) => cache.Full(state, r);
    public JsonObject FullForItem(JsonNode? state, string r, int index) => cache.FullForItem(state, r, index);
    public int Count(string r, string path) => cache.Count(r, path);

    public async Task<string> Stage(string r, List<(string path, JsonNode? value)> writes)
    {
        await gate.WaitAsync();
        try
        {
            var token = $"{r}!{++seq}";
            await Fact(r, "staged", new JsonObject { ["token"] = token, ["writes"] = Writes(writes) });
            staged[token] = (r, writes);
            return token;
        }
        finally { gate.Release(); }
    }

    public async Task Commit(string token)
    {
        await gate.WaitAsync();
        try
        {
            // A commit that arrives twice (the engine retried its claim-check call) changes nothing.
            if (committed.Contains(token) || !staged.TryGetValue(token, out var s)) return;
            await Fact(s.r, "commit", new JsonObject { ["token"] = token });
            committed.Add(token);
            cache.Commit(cache.Stage(s.r, s.writes));
        }
        finally { gate.Release(); }
    }

    public async Task Fork(string from, string to)
    {
        await gate.WaitAsync();
        try
        {
            await Fact(to, "fork", new JsonObject { ["from"] = from, ["payload"] = cache.Payload(from) });
            cache.Fork(from, to);
        }
        finally { gate.Release(); }
    }

    public async Task Copy(string from, string to, IEnumerable<string> paths)
    {
        var list = paths.ToList();
        await gate.WaitAsync();
        try
        {
            var src = cache.Payload(from);
            var values = new JsonObject();
            foreach (var p in list) values[p] = J.Clone(J.Get(src, p));
            await Fact(to, "copy", new JsonObject { ["from"] = from, ["values"] = values });
            cache.Copy(from, to, list);
        }
        finally { gate.Release(); }
    }

    static JsonArray Writes(List<(string path, JsonNode? value)> writes) =>
        new(writes.Select(w => (JsonNode?)new JsonObject { ["path"] = w.path, ["value"] = J.Clone(w.value) }).ToArray());

    /// <summary>
    /// The payload of a reference as the store holds it: its facts replayed in order. A write that was
    /// staged and never committed is not in it.
    /// </summary>
    public static async Task<JsonObject> Load(StoreOptions options, string r)
    {
        await using var db = StoreDbContext.Open(options);
        var p = new JsonObject();
        var pending = new Dictionary<string, JsonArray>();
        foreach (var f in await new FactLog(db).Of(r))
        {
            var d = (JsonObject)JsonNode.Parse(f.DataJson)!;
            switch (f.Code)
            {
                case "create":
                case "fork":
                    p = (JsonObject)J.Clone(d["payload"])!;
                    break;
                case "staged":
                    pending[(string)d["token"]!] = (JsonArray)J.Clone(d["writes"])!;
                    break;
                case "commit":
                    if (pending.Remove((string)d["token"]!, out var ws))
                        foreach (var w in ws) J.Set(p, (string)w!["path"]!, J.Clone(w["value"]));
                    break;
                case "copy":
                    foreach (var (path, v) in (JsonObject)d["values"]!) if (v is not null) J.Set(p, path, J.Clone(v));
                    break;
            }
        }
        return p;
    }
}

/// <summary>
/// The studio's canonical context (batch, items[].payment, payment, ext.&lt;scheme&gt;) to the ISO 20022
/// model, for accepting a payment into the store. A scheme's fields go into SupplementaryData under the
/// scheme's scope; nothing is dropped silently: a field the model has no place for stays in the payload.
/// </summary>
public static class CanonicalMapper
{
    public static PaymentBulk? ToBulk(JsonObject payload)
    {
        if (payload["batch"] is not JsonObject batch || payload["items"] is not JsonArray items) return null;
        var id = (string?)batch["id"] ?? Guid.NewGuid().ToString("N");
        var txs = new List<CreditTransfer>();
        for (var i = 0; i < items.Count; i++)
            if (ToPayment(items[i] as JsonObject ?? new JsonObject(), id, i + 1) is { } t) txs.Add(t);
        return new PaymentBulk
        {
            MessageId = Short(id), PaymentInformationId = Short(id),
            RequestedExecutionDate = Date(batch["requestedExecutionDate"]),
            CategoryPurposeCode = (string?)batch["categoryPurpose"],
            Debtor = Party(batch["debtor"]) ?? new Party(),
            DebtorAccount = Account(batch["debtorAccount"]) ?? new Account(),
            DebtorAgent = Agent(batch["debtorAgent"]),
            Transactions = txs,
            SupplementaryData = Supplementary(payload["ext"]),
        };
    }

    public static CreditTransfer? ToPayment(JsonObject holder, string? batchId, int line)
    {
        if (holder["payment"] is not JsonObject p) return null;
        var amount = p["instructedAmount"] as JsonObject;
        var e2e = (string?)p["endToEndId"] ?? (batchId is null ? Guid.NewGuid().ToString("N") : $"{batchId}-{line}");
        return new CreditTransfer
        {
            PaymentId = new PaymentIdentification { EndToEndId = Short(e2e), InstructionId = (string?)p["instructionId"], TransactionId = (string?)p["transactionId"], Uetr = (string?)p["uetr"] },
            InstructedAmount = new Money(amount?["value"]?.GetValue<decimal>() ?? 0m, (string?)amount?["currency"] ?? "AED"),
            Debtor = Party(p["debtor"]), DebtorAccount = Account(p["debtorAccount"]), DebtorAgent = Agent(p["debtorAgent"]),
            Creditor = Party(p["creditor"]) ?? new Party(),
            CreditorAccount = Account(p["creditorAccount"]) ?? new Account(),
            CreditorAgent = Agent(p["creditorAgent"]),
            PurposeCode = (string?)p["purpose"],
            RemittanceUnstructured = p["remittance"] is JsonArray ra ? ra.Select(x => (string)x!).ToList() : p["remittance"] is JsonValue rv ? [(string)rv!] : [],
            SupplementaryData = Supplementary(holder["ext"]),
        };
    }

    static string Short(string s) => s.Length <= 35 ? s : s[..35];
    static DateOnly? Date(JsonNode? n) => n is JsonValue v && DateOnly.TryParse((string?)v, out var d) ? d : null;

    static Party? Party(JsonNode? n) => n is not JsonObject o ? null : new Party
    {
        Name = (string?)o["name"],
        AnyBic = (string?)o["bic"],
        Lei = (string?)o["lei"],
        PostalAddress = o["postalAddress"] is JsonObject a ? new PostalAddress
        {
            StreetName = (string?)a["streetName"], BuildingNumber = (string?)a["buildingNumber"], PostCode = (string?)a["postCode"],
            TownName = (string?)a["townName"], CountrySubDivision = (string?)a["countrySubDivision"], Country = (string?)a["country"],
        } : null,
    };

    static Account? Account(JsonNode? n) => n is not JsonObject o ? null : new Account
    {
        Iban = (string?)o["iban"], Other = o["other"] is JsonValue v ? new GenericId((string)v!) : null, Currency = (string?)o["currency"], Name = (string?)o["name"],
    };

    static Agent? Agent(JsonNode? n) => n is not JsonObject o ? null : new Agent
    {
        Bicfi = (string?)o["bic"] ?? (string?)o["bicfi"],
        ClearingSystem = o["clearingMemberId"] is null ? null : (string?)o["clearingSystem"] ?? "AEUAE",
        MemberId = (string?)o["clearingMemberId"],
        Name = (string?)o["name"],
    };

    /// <summary>
    /// ext.uae.wps.* becomes SupplementaryData["uae.wps"]; ext.p.* and ext.b.* become ["p"] and ["b"]
    /// (a scope is p, b or cluster.scheme). A scalar directly under a cluster stays under the cluster.
    /// </summary>
    static IReadOnlyDictionary<string, JsonObject> Supplementary(JsonNode? ext)
    {
        var d = new Dictionary<string, JsonObject>();
        if (ext is not JsonObject e) return d;
        foreach (var (k, v) in e)
        {
            if (v is not JsonObject o) continue;
            if (k is "p" or "b") { d[k] = (JsonObject)J.Clone(o)!; continue; }
            foreach (var (s, w) in o)
            {
                if (w is JsonObject scheme) d[$"{k}.{s}"] = (JsonObject)J.Clone(scheme)!;
                else (d.TryGetValue(k, out var c) ? c : d[k] = new JsonObject())[s] = J.Clone(w);
            }
        }
        return d;
    }
}
