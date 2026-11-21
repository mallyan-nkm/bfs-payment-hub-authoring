using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Model;
using TransactHub.Store;

namespace TransactHub.Activities.Wps.Sif;

/// <summary>
/// The Central Bank SIF of a salary file, generated from the bulk as the store holds it (plan W1, W2): the ISO
/// 20022 payment model, with the WPS fields in SupplementaryData["uae.wps"], instead of the source's wps.Sif_Edr,
/// Sif_Evp and Sif_Scr tables. The writer is the source's own (ported, golden-tested). The generated file is kept
/// in the store as a message, with its hash, linked to the bulk.
/// </summary>
public static class CentralBankSif
{
    public const string Scope = "uae.wps";
    public const string MessageKind = "uae.wps.sif-out";

    public sealed record Generated(CbxSifDocument Document, string FileName, string MessageId);

    public static async Task<(Generated? Sif, CbxSifFailure? Failure)> Generate(StoreDbContext db, string tenant, string bulkId, string bankRoutingCode, string receivedFileName, DateTime createdAt, TimeProvider? clock = null, CancellationToken ct = default)
    {
        var store = new PaymentStore(db, clock);
        var bulk = await store.ReadBulk(bulkId, ct) ?? throw new InvalidOperationException($"No bulk {bulkId}");
        var (doc, failure) = Build(bulk, bankRoutingCode, createdAt);
        if (doc is null) return (null, failure);
        var name = CbxSifWriter.FileNameFor(receivedFileName);
        var (messageId, _) = await store.KeepMessage(tenant, Scope, Direction.Outward, MessageKind, name, CbxSifWriter.Encode(doc.Content), bulkId, ct);
        await new FactLog(db, clock).Append(bulkId, "bulk", new NewFact("message", "sif-generated", new JsonObject { ["message"] = messageId, ["sha256"] = doc.Sha256, ["edr"] = doc.EdrCount, ["evp"] = doc.EvpCount }), ct);
        return (new Generated(doc, name, messageId), null);
    }

    /// <summary>The bulk to the writer's records: one EDR per transaction, in line order; EVPs from the transaction's variable-pay breakdown.</summary>
    public static (CbxSifDocument? Document, CbxSifFailure? Failure) Build(PaymentBulk bulk, string bankRoutingCode, DateTime createdAt)
    {
        var employees = new List<CbxSifEmployee>();
        var pays = new List<CbxSifVariablePay>();
        var line = 0;
        foreach (var t in bulk.Transactions)
        {
            line++;
            var w = t.SupplementaryData.TryGetValue(Scope, out var x) ? x : new JsonObject();
            var person = S(w, "personId");
            var agent = t.CreditorAgent?.MemberId ?? "";
            employees.Add(new CbxSifEmployee(line, line, person, agent, t.CreditorAccount.Display,
                D(w, "payStartDate"), D(w, "payEndDate"), (int)N(w, "daysInPeriod"), N(w, "fixedPay"), N(w, "variablePay"), N(w, "leaveDays")));
            if (w["variablePay"] is not null && w["evp"] is JsonObject e)
                pays.Add(new CbxSifVariablePay(line, person, S(e, "routingCode") is { Length: > 0 } rc ? rc : agent,
                    N(e, "housing"), N(e, "conveyance"), N(e, "medical"), N(e, "annualPassage"), N(e, "overtime"), N(e, "other"), N(e, "leaveEncashment")));
        }
        var b = bulk.SupplementaryData.TryGetValue(Scope, out var bw) ? bw : new JsonObject();
        var establishment = S(b, "employerId") is { Length: > 0 } eid ? eid : bulk.Debtor.OtherIds.FirstOrDefault(i => i.SchemeName == "MOL")?.Id ?? "";
        var control = new CbxSifControl(establishment, bankRoutingCode, createdAt, S(b, "salaryMonth"), bulk.DebtorAccount.Currency ?? "AED", S(b, "employerReference"));
        return CbxSifWriter.Build(employees, pays, control);
    }

    static string S(JsonObject o, string k) => o[k]?.ToString() ?? "";
    static decimal N(JsonObject o, string k) => o[k] is JsonValue v && decimal.TryParse(v.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    static DateOnly D(JsonObject o, string k) => DateOnly.TryParse(o[k]?.ToString(), CultureInfo.InvariantCulture, out var d) ? d : default;
}
