using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The payments whose file is kept and undelivered (R-05-103, Issue I-337): the File API's outbox records each delivery of a payment's
/// sent file on the payment's chain (R-09-236), and a file whose latest delivery fact is a failed attempt (<c>failing</c>, the outbox
/// still tries) or the end of its attempts (<c>undelivered</c>) is listed here, with its error's code, until a delivery or a resend
/// puts it on the partner's folder. The send step of such a payment said the file was taken; only the delivery says it left. It reads
/// the read model only; the resend is the payment's own action (<c>resend</c>, R-05-58).
/// </summary>
public sealed class UndeliveredFiles(StoreOptions ops)
{
    public const string Failing = "failing", Undelivered = "undelivered";

    public sealed record Row(string Payment, string Tenant, string Scheme, string File, string? Kind, string? FileName, string State, int Attempts,
        string? Code, string? Why, string? Adapter, DateTimeOffset At, string? Status);

    /// <summary>Every kept file of the tenants' payments that is not delivered, the latest first.</summary>
    public async Task<List<Row>> List(IReadOnlyCollection<string> tenants, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        string[] kinds = [Projector.DeliveredKind, Projector.DeliveryFailedKind, Projector.UndeliveredKind];
        var facts = await (from t in o.Timeline.AsNoTracking()
                           join s in o.Subjects.AsNoTracking() on t.SubjectId equals s.Id
                           where kinds.Contains(t.Kind) && tenants.Contains(s.Tenant)
                           orderby t.Seq
                           select new { t, s }).Take(20000).ToListAsync(ct);
        var rows = new List<Row>();
        foreach (var g in facts.Select(x => (x.t, x.s, d: JsonNode.Parse(x.t.DataJson) as JsonObject ?? new JsonObject()))
                     .GroupBy(x => (x.s.Id, File: (string?)x.d["file"] ?? "")))
        {
            var last = g.Last();
            if (last.t.Kind == Projector.DeliveredKind) continue;
            var attempts = g.Count(x => x.t.Kind is Projector.DeliveredKind or Projector.DeliveryFailedKind);
            rows.Add(new Row(last.s.Id, last.s.Tenant, last.s.Scheme, g.Key.File, (string?)last.d["kind"], (string?)last.d["fileName"],
                last.t.Kind == Projector.UndeliveredKind ? Undelivered : Failing, attempts,
                last.t.Kind == Projector.UndeliveredKind ? (string?)last.d["lastCode"] ?? last.t.Code : last.t.Code,
                (string?)last.d["why"], (string?)last.d["adapter"], last.t.At, last.s.Status));
        }
        return rows.OrderByDescending(r => r.At).ToList();
    }

    /// <summary>A row as the console shows it, with the link to the payment, where its resend is offered.</summary>
    public static object Show(Row r) => new
    {
        payment = r.Payment, tenant = r.Tenant, scheme = r.Scheme, file = r.File, kind = r.Kind, fileName = r.FileName, state = r.State,
        attempts = r.Attempts, code = r.Code, why = r.Why, adapter = r.Adapter, at = r.At, status = r.Status, link = $"/payments/{Uri.EscapeDataString(r.Payment)}",
    };
}
