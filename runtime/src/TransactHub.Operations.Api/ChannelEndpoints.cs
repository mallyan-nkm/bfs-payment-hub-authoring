using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Auth;
using TransactHub.Operations;
using TransactHub.Store;

namespace TransactHub.Operations.Api;

/// <summary>
/// The channel enquiry APIs (R-09-74): one per need, scheme-neutral, for the bank's channels as system clients of its identity
/// provider (role <c>channel-enquiry</c>, per tenant; a channel sees its own bank only). Each answers in the shape of the ISO
/// 20022 payment status report (pain.002: the group status, the payment information and each transaction's status) from the
/// operations read model, with the scheme's own status as the scheme's operations view words it (the legs' status fields, masked
/// as the view declares) and a three-value channel status (PENDING, ACCEPTED, REJECTED), as the source's WPS inquiry gave it.
/// <list type="table">
/// <item><term>GET /api/channel/payments/{transactionId}/status</term><description>the source's /api/payment/{id}/status and /api/wps/inquiry/{id}</description></item>
/// <item><term>GET /api/channel/batches/{batchId}/status</term><description>the source's /api/batch/{batchId}/status</description></item>
/// <item><term>GET /api/channel/payments?reference=&amp;from=&amp;to=&amp;status=&amp;scheme=</term><description>the source's SIF upload search</description></item>
/// </list>
/// </summary>
public static class ChannelEndpoints
{
    static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    static string? Q(HttpContext http, string k) => http.Request.Query.TryGetValue(k, out var v) && v.ToString().Length > 0 ? v.ToString() : null;

    /// <summary>The tenants the channel may see: those its grant gives it the role in; for <c>*</c>, every tenant of the read model.</summary>
    static async Task<List<string>> Tenants(HttpContext http, OpsDbContext o)
    {
        var byTenant = RuntimeAuth.RolesByTenant(http.User).Where(kv => kv.Value.Contains(SystemRoles.ChannelEnquiry)).Select(kv => kv.Key).ToList();
        if (byTenant.Contains("*")) return await o.Subjects.AsNoTracking().Select(s => s.Tenant).Distinct().ToListAsync();
        return byTenant;
    }

    /// <summary>The channel's three words, from the ISO status and how the payment ended (the source's WpsInquiryEndpoints.MapStatus).</summary>
    public static string ChannelStatus(OpsSubject s)
    {
        var end = (string?)(JsonNode.Parse(s.FieldsJson) as JsonObject)?["endOutcome"];
        if (end is "settled" || s.Status is "ACSC") return "ACCEPTED";
        if (end is "rejected" or "returned" || s.Status is "RJCT") return "REJECTED";
        return "PENDING";
    }

    static JsonObject SchemeStatus(OpsSubject s, IEnumerable<OpsLeg> legs, ViewCatalog views)
    {
        var view = views.SchemeView(s.Scheme);
        var fields = new JsonObject();
        foreach (var leg in legs)
        {
            var decl = view?.Legs.FirstOrDefault(l => (string?)l["leg"] == leg.Leg);
            var values = JsonNode.Parse(leg.FieldsJson) as JsonObject ?? new JsonObject();
            foreach (var f in (decl?["fields"] as JsonArray ?? []).OfType<JsonObject>())
                if ((string?)f["format"] == "status" && values[(string)f["id"]!] is { } v)
                    fields[(string)f["id"]!] = new JsonObject { ["label"] = (string?)f["label"], ["value"] = v.DeepClone() };
        }
        return new JsonObject { ["scope"] = s.Scheme, ["view"] = view?.Key, ["status"] = ChannelStatus(s), ["rawStatus"] = s.LastOutcome, ["fields"] = fields };
    }

    /// <summary>A payment or a bulk as pain.002 words it, with its lines' statuses and the scheme's.</summary>
    public static JsonObject Report(OpsSubject s, IEnumerable<OpsLeg> legs, IEnumerable<OpsLine> lines, ViewCatalog views, DateTimeOffset now)
    {
        var group = s.Status ?? "RCVD";
        var tx = new JsonArray();
        foreach (var l in lines)
        {
            var facts = JsonNode.Parse(l.FactsJson) as JsonObject;
            var worst = (string?)facts?["validation"]?["worst"];
            var rejected = worst is not null && worst != "pass";
            tx.Add(new JsonObject
            {
                ["originalTransactionId"] = l.Id, ["lineNo"] = l.LineNo, ["amount"] = l.Amount, ["currency"] = l.Currency,
                ["transactionStatus"] = rejected ? "RJCT" : group,
                ["statusReasonInformation"] = rejected ? new JsonArray(new JsonObject { ["additionalInformation"] = worst }) : new JsonArray(),
            });
        }
        if (s.Kind == "payment") tx.Add(new JsonObject { ["originalTransactionId"] = s.Id, ["originalEndToEndId"] = s.EndToEndId, ["amount"] = s.Amount, ["currency"] = s.Currency, ["transactionStatus"] = group, ["statusReasonInformation"] = new JsonArray() });
        return new JsonObject
        {
            ["transactionId"] = s.Id, ["kind"] = s.Kind,
            ["groupHeader"] = new JsonObject { ["messageId"] = $"STS-{s.Id}", ["creationDateTime"] = now.ToString("O", CultureInfo.InvariantCulture) },
            ["originalGroupInformationAndStatus"] = new JsonObject
            {
                ["originalMessageId"] = s.MessageId ?? s.Id, ["originalMessageNameId"] = s.Kind == "bulk" ? "pain.001" : "pacs.008",
                ["originalNumberOfTransactions"] = s.Count, ["originalControlSum"] = s.Amount, ["groupStatus"] = group,
            },
            ["originalPaymentInformationAndStatus"] = new JsonArray(new JsonObject
            {
                ["originalPaymentInformationId"] = s.BulkId ?? s.Id, ["paymentInformationStatus"] = group, ["transactionInformationAndStatus"] = tx,
            }),
            ["scheme"] = SchemeStatus(s, legs, views),
            ["fileName"] = s.FileName, ["acceptedAt"] = s.AcceptedAt, ["updatedAt"] = s.UpdatedAt,
        };
    }

    public static void MapChannel(this WebApplication app, StoreOptions opsStore)
    {
        var api = app.MapGroup("/api/channel");

        async Task<IResult> One(HttpContext http, string id, string? kind)
        {
            await using var o = OpsDbContext.Open(opsStore);
            var tenants = await Tenants(http, o);
            if (tenants.Count == 0) return Problem(403, $"{RuntimeAuth.Actor(http.User)} has no {SystemRoles.ChannelEnquiry} role");
            var s = await o.Subjects.AsNoTracking().Where(x => tenants.Contains(x.Tenant) && (kind == null || x.Kind == kind)
                && (x.Id == id || x.EndToEndId == id || x.MessageId == id)).OrderByDescending(x => x.AcceptedAt).FirstOrDefaultAsync();
            if (s is null) return Results.Json(new { transactionId = id, errorCode = "TRANSACTION_NOT_FOUND", errorDescription = $"No {(kind ?? "payment")} '{id}' was found." }, statusCode: 404);
            var legs = await o.Legs.AsNoTracking().Where(l => l.SubjectId == s.Id).OrderBy(l => l.Instance).ToListAsync();
            var lines = await o.Lines.AsNoTracking().Where(l => l.BulkId == s.Id).OrderBy(l => l.LineNo).Take(5000).ToListAsync();
            return Results.Ok(Report(s, legs, lines, http.RequestServices.GetRequiredService<ViewCatalog>(), DateTimeOffset.UtcNow));
        }

        api.MapGet("/payments/{transactionId}/status", (string transactionId, HttpContext http) => One(http, transactionId, null));
        api.MapGet("/batches/{batchId}/status", (string batchId, HttpContext http) => One(http, batchId, "bulk"));

        api.MapGet("/payments", async (HttpContext http) =>
        {
            await using var o = OpsDbContext.Open(opsStore);
            var tenants = await Tenants(http, o);
            if (tenants.Count == 0) return Problem(403, $"{RuntimeAuth.Actor(http.User)} has no {SystemRoles.ChannelEnquiry} role");
            var x = o.Subjects.AsNoTracking().Where(s => tenants.Contains(s.Tenant));
            if (Q(http, "reference") is { } r) x = x.Where(s => s.Id.Contains(r) || (s.MessageId != null && s.MessageId.Contains(r)) || (s.EndToEndId != null && s.EndToEndId.Contains(r)) || (s.FileName != null && s.FileName.Contains(r)));
            if (Q(http, "scheme") is { } sc) x = x.Where(s => s.Scheme == sc);
            if (Q(http, "from") is { } f && DateTimeOffset.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fd)) x = x.Where(s => s.AcceptedAt >= fd);
            if (Q(http, "to") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var td)) { var end = t.Length == 10 ? td.AddDays(1) : td; x = x.Where(s => s.AcceptedAt < end); }
            var status = Q(http, "status");
            // An ISO status filters in the database; a channel word (PENDING, ACCEPTED, REJECTED) after.
            if (status is { Length: 4 } iso) x = x.Where(s => s.Status == iso);
            var rows = await x.OrderByDescending(s => s.AcceptedAt).ThenBy(s => s.Id).Take(2000).ToListAsync();
            if (status is "PENDING" or "ACCEPTED" or "REJECTED") rows = rows.Where(s => ChannelStatus(s) == status).ToList();
            var page = Math.Max(1, int.TryParse(Q(http, "page"), out var p) ? p : 1);
            var size = Math.Clamp(int.TryParse(Q(http, "pageSize"), out var ps) ? ps : 25, 1, 200);
            return Results.Ok(new
            {
                total = rows.Count, page, pageSize = size,
                rows = rows.Skip((page - 1) * size).Take(size).Select(s => new
                {
                    transactionId = s.Id, s.Kind, s.Scheme, s.MessageId, s.EndToEndId, s.FileName, s.Amount, s.Currency, s.Count,
                    groupStatus = s.Status ?? "RCVD", status = ChannelStatus(s), s.AcceptedAt, s.UpdatedAt,
                }),
            });
        });
    }
}
