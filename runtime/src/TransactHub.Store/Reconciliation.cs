using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

/// <summary>One line of a scheme's report: the payment it names (its end-to-end id), its amount, currency and the scheme's status.</summary>
public sealed record ReportLine(int LineNo, string Reference, decimal? Amount, string? Currency, string? Status);

/// <summary>
/// A scheme's report file, as the runtime reads it until the scheme declares its layout (Issue I-164): CSV with a header row naming
/// at least <c>reference</c> (the end-to-end id), and any of <c>amount</c>, <c>currency</c> and <c>status</c>; or JSON
/// <c>{ "lines": [ { reference, amount, currency, status } ] }</c>. Amounts are exact decimals.
/// </summary>
public static class SchemeReport
{
    public static List<ReportLine> Parse(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content).TrimStart('﻿').Trim();
        if (text.StartsWith('{'))
        {
            var j = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("The report is not a JSON object");
            return (j["lines"] as JsonArray ?? []).OfType<JsonObject>().Select((l, i) => new ReportLine(i + 1, (string?)l["reference"] ?? throw new FormatException($"Line {i + 1} names no reference"),
                l["amount"] is JsonNode a ? decimal.Parse(a.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture) : null, (string?)l["currency"], (string?)l["status"])).ToList();
        }
        var rows = text.Split('\n').Select(r => r.TrimEnd('\r')).Where(r => r.Length > 0 && !r.StartsWith('#')).ToList();
        if (rows.Count == 0) return [];
        var head = rows[0].Split(',').Select(h => h.Trim().ToLowerInvariant()).ToList();
        var at = (string c) => head.IndexOf(c);
        if (at("reference") < 0) throw new FormatException("The report's header names no reference column");
        var outp = new List<ReportLine>();
        for (var i = 1; i < rows.Count; i++)
        {
            var c = rows[i].Split(',').Select(x => x.Trim()).ToArray();
            string? Col(string n) => at(n) is var k && k >= 0 && k < c.Length && c[k].Length > 0 ? c[k] : null;
            outp.Add(new ReportLine(i, Col("reference") ?? throw new FormatException($"Line {i} names no reference"),
                Col("amount") is string amt ? decimal.Parse(amt, NumberStyles.Number, CultureInfo.InvariantCulture) : null, Col("currency"), Col("status")));
        }
        return outp;
    }
}

/// <summary>
/// The scheme report's reconciliation on the runtime (R-09-92): the behaviour of <c>p.reconcile.scheme-report.v1</c>, run when the
/// scheme's report file arrives through the File API. Each line is matched against the store's payments of the bank and scheme by
/// its end-to-end id; a line the store has no payment for, an amount or a currency that differs, or a status the store's last
/// status does not agree with is a break. The run and each break are facts on the run's own chain (subject <c>recon-…</c>, kind
/// <c>reconciliation</c>), linked to the report file and, where one is found, to the payment; nothing of the payment changes.
/// </summary>
public sealed class SchemeReconciliation(StoreDbContext db, TimeProvider? clock = null)
{
    public const string SubjectKind = "reconciliation";
    public const string Activity = "p.reconcile.scheme-report.v1";
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>The scheme's word for a status, as the store's ISO statuses take it.</summary>
    public static string? Iso(string? s) => (s ?? "").Trim().ToUpperInvariant() switch
    {
        "" => null,
        "SETTLED" or "ACCEPTED" or "PAID" or "CREDITED" or "ACSC" or "ACCC" => "ACSC",
        "REJECTED" or "RETURNED" or "FAILED" or "RJCT" => "RJCT",
        "PENDING" or "PDNG" or "ACSP" => "PDNG",
        var x => x,
    };

    /// <summary>A report in the runtime's own layout (<see cref="SchemeReport"/>), as an installation names one the scheme does not.</summary>
    public async Task<JsonObject> Run(string tenant, string scheme, string fileId, byte[] content, string actor, CancellationToken ct = default)
    {
        List<ReportLine>? lines = null;
        string? why = null;
        try { lines = SchemeReport.Parse(content); }
        catch (FormatException e) { why = e.Message; }
        return await Run(tenant, scheme, fileId, lines, why, null, Activity, actor, ct);
    }

    /// <summary>
    /// A report's lines as its scheme's map read them (R-09-210, Issue I-164), or why they could not be read. With a period, each
    /// payment of the bank and scheme accepted within it before now that no line names is a break too (<c>not-in-report</c>).
    /// </summary>
    public async Task<JsonObject> Run(string tenant, string scheme, string fileId, List<ReportLine>? lines, string? unreadable, TimeSpan? period, string activity, string actor, CancellationToken ct = default)
    {
        var run = $"recon-{fileId}"[..Math.Min(64, $"recon-{fileId}".Length)];
        var log = new FactLog(db, clock);
        if (lines is null)
        {
            await log.Append(run, SubjectKind, new NewFact("reconciliation", "unreadable", new JsonObject { ["file"] = fileId, ["why"] = unreadable, ["activity"] = activity }, Actor: actor), ct);
            return new JsonObject { ["run"] = run, ["outcome"] = "unreadable", ["why"] = unreadable };
        }
        var refs = lines.Select(l => l.Reference.Length <= 35 ? l.Reference : l.Reference[..35]).Distinct().ToList();
        var payments = await db.Payments.AsNoTracking().Where(p => p.Tenant == tenant && p.Scheme == scheme && refs.Contains(p.EndToEndId)).ToListAsync(ct);
        var missing = new List<PaymentRow>();
        if (period is TimeSpan within)
        {
            var until = clock.GetUtcNow();
            var since = until - within;
            missing = (await db.Payments.AsNoTracking().Where(p => p.Tenant == tenant && p.Scheme == scheme && p.AcceptedAt >= since && p.AcceptedAt <= until && !refs.Contains(p.EndToEndId)).ToListAsync(ct))
                .OrderBy(p => p.AcceptedAt).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        }
        var ids = payments.Select(p => p.Id).Concat(payments.Where(p => p.BulkId != null).Select(p => p.BulkId!)).Distinct().ToList();
        var statuses = (await db.Facts.AsNoTracking().Where(f => ids.Contains(f.SubjectId) && f.Kind == "status").OrderBy(f => f.Seq).Select(f => new { f.SubjectId, f.Code }).ToListAsync(ct))
            .GroupBy(f => f.SubjectId).ToDictionary(g => g.Key, g => g.Last().Code);
        await log.Append(run, SubjectKind, new NewFact("reconciliation", "run", new JsonObject
        {
            ["file"] = fileId, ["tenant"] = tenant, ["scheme"] = scheme, ["lines"] = lines.Count, ["activity"] = activity,
            ["period"] = period is TimeSpan pd ? System.Xml.XmlConvert.ToString(pd) : null,
        }, Actor: actor), ct);
        var breaks = new JsonArray();
        var matched = 0;
        foreach (var l in lines)
        {
            var p = payments.Where(x => x.EndToEndId == l.Reference || (l.Reference.Length > 35 && x.EndToEndId == l.Reference[..35])).OrderByDescending(x => x.AcceptedAt).FirstOrDefault();
            string? kind = null;
            JsonNode? expected = null, actual = null;
            if (p is null) kind = "not-in-store";
            else if (l.Amount is decimal a && a != p.Amount) (kind, expected, actual) = ("amount", p.Amount.ToString(CultureInfo.InvariantCulture), a.ToString(CultureInfo.InvariantCulture));
            else if (l.Currency is string c && !string.Equals(c, p.Currency, StringComparison.OrdinalIgnoreCase)) (kind, expected, actual) = ("currency", p.Currency, c);
            else if (Iso(l.Status) is string s && (statuses.GetValueOrDefault(p.Id) ?? (p.BulkId is null ? null : statuses.GetValueOrDefault(p.BulkId))) is string have && have != s)
                (kind, expected, actual) = ("status", have, s);
            if (kind is null) { matched++; continue; }
            var id = $"brk-{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{run}|{l.LineNo}")))[..20]}";
            var b = new JsonObject
            {
                ["break"] = id, ["kind"] = kind, ["line"] = l.LineNo, ["reference"] = l.Reference, ["payment"] = p?.Id, ["expected"] = expected, ["reported"] = actual,
                ["reportedStatus"] = Iso(l.Status), ["file"] = fileId, ["tenant"] = tenant, ["scheme"] = scheme,
            };
            await log.Append(run, SubjectKind, new NewFact("reconciliation", "break", b, Actor: actor), ct);
            breaks.Add(b.DeepClone());
        }
        // A payment of the report's period the report does not name (R-09-210): the scheme has no record of what the store holds.
        foreach (var p in missing)
        {
            var id = $"brk-{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{run}|missing|{p.Id}")))[..20]}";
            var b = new JsonObject
            {
                ["break"] = id, ["kind"] = "not-in-report", ["reference"] = p.EndToEndId, ["payment"] = p.Id, ["file"] = fileId, ["tenant"] = tenant, ["scheme"] = scheme,
            };
            await log.Append(run, SubjectKind, new NewFact("reconciliation", "break", b, Actor: actor), ct);
            breaks.Add(b.DeepClone());
        }
        var outcome = breaks.Count == 0 ? "matched" : "unmatched";
        await log.Append(run, SubjectKind, new NewFact("reconciliation", outcome, new JsonObject { ["file"] = fileId, ["matched"] = matched, ["breaks"] = breaks.Count }, Actor: actor), ct);
        return new JsonObject { ["run"] = run, ["outcome"] = outcome, ["lines"] = lines.Count, ["matched"] = matched, ["breaks"] = breaks };
    }
}
