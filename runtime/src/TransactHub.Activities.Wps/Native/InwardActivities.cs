using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Posting;
using TransactHub.Activities.Wps.Inward;
using TransactHub.Activities.Wps.Inward.Dif;
using TransactHub.Activities.Wps.Inward.Rfa;
using TransactHub.Activities.Wps.Inward.Rri;
using TransactHub.Activities.Wps.Rules;

namespace TransactHub.Activities.Wps.Native;

// The WPS inward files on the runtime (plan P2, spec R-08-14 and R-08-16): each class answers one factory activity on the
// studio's canonical context, with the source's own parser or builder underneath (Inward/, ported unchanged and proved by the
// source's unit tests). A file the host renders is kept by the host; the context keeps its name, hash and totals, never its
// lines. No activity here writes a person's name: a PSI's names stay in the file.

/// <summary>What the inward activities share: the inbound file and its name, money and the business instant.</summary>
static class InwardFiles
{
    /// <summary>
    /// The inbound file's text and name: the name from <c>messages.inbound.fileName</c>, else from a leading <c># file: name</c> line, the
    /// fixture metadata of the factory's samples (studio/fixtures/samples/README.md), which is not part of the file and is taken off it.
    /// </summary>
    public static (string Text, string? FileName) Inbound(JsonObject view)
    {
        var inbound = NativeContext.Node(view, "messages.inbound");
        var text = NativeContext.S(inbound, "payload");
        var name = NativeContext.S(inbound, "fileName");
        var nl = text.IndexOf('\n');
        var first = (nl < 0 ? text : text[..nl]).Trim();
        if (first.StartsWith('#') && first.TrimStart('#').TrimStart().StartsWith("file:", StringComparison.Ordinal))
        {
            if (name.Length == 0) name = first.TrimStart('#').Trim()["file:".Length..].Trim();
            text = nl < 0 ? "" : text[(nl + 1)..];
        }
        return (text, name.Length == 0 ? null : name);
    }

    public static decimal Round2(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    public static string Money(decimal d) => Round2(d).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The business instant of the engine's time (UTC+4), truncated to the second as the source's name policies want it.</summary>
    public static DateTime BusinessSecond(DateTimeOffset now)
    {
        var b = NativeContext.Business(now);
        return new DateTime(b.Ticks - (b.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Unspecified);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A credit the core system refused, or that the inquiry found not posted: the returns table's rule (I-39).</summary>
    public static bool Refused(JsonNode? item) =>
        NativeContext.S(item, "facts.posting.outcome") == "rejected"
        || (NativeContext.S(item, "facts.posting.outcome") == "unknown" && NativeContext.S(item, "facts.postingVerified.outcome") == "mismatch");

    public static NativeResult Unmappable(string by, string code, string reason) =>
        new("unmappable", "FF01", new JsonObject { ["messages.rendered"] = new JsonObject { ["by"] = by, ["error"] = $"{code}: {reason}" } });
}

/// <summary>
/// <c>uae.wps.map.pif-in.v1</c>: the payment information file, one per agent, to the canonical inward batch, with the source's
/// parser (<c>InwardPifParser</c>): EDR, EVP and PCR records, whole-file all-or-nothing, the count and the total reconciled, the
/// 12-character WPS file id from the file name. A rejected file is <c>unmappable</c>; its reason names no field value.
/// </summary>
public sealed class MapPifIn : INativeActivity
{
    public string Name => "uae.wps.map.pif-in.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var (text, fileName) = InwardFiles.Inbound(a.View);
        if (text.Length == 0) return new NativeResult("unmappable", "FF01", new JsonObject());
        var r = InwardPifParser.Parse(Encoding.UTF8.GetBytes(text), fileName ?? "");
        if (!r.IsSuccess) return new NativeResult("unmappable", "FF01", new JsonObject());
        var f = r.File!;
        var currency = f.Header.PaymentCurrency;
        var agent = f.Edrs.Count > 0 ? f.Edrs[0].RoutingCode : "";
        var items = new JsonArray();
        foreach (var e in f.Edrs)
        {
            items.Add(new JsonObject
            {
                ["payment"] = new JsonObject
                {
                    ["creditorAgent"] = new JsonObject { ["clearingMemberId"] = e.RoutingCode },
                    ["creditorAccount"] = new JsonObject { ["iban"] = e.AccountNoRaw },
                    ["instructedAmount"] = new JsonObject { ["value"] = InwardFiles.Round2(e.FixedPay + e.VariablePay), ["currency"] = currency },
                },
                ["ext"] = new JsonObject
                {
                    ["uae"] = new JsonObject
                    {
                        ["wps"] = new JsonObject
                        {
                            ["personId"] = e.EmployeeId, ["payStartDate"] = e.PayStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            ["payEndDate"] = e.PayEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["daysInPeriod"] = e.DaysInPeriod,
                            ["fixedPay"] = e.FixedPay, ["variablePay"] = e.VariablePay, ["leaveDays"] = e.LeaveDays, ["hasVariablePay"] = e.HasVariablePay,
                        },
                    },
                },
            });
        }
        return new NativeResult("mapped", null, new JsonObject
        {
            // Leaf writes, as a script's: the header keeps what the front door set (the payment id, the correlation).
            ["header.fileId"] = f.WpsFileId, ["header.endToEndId"] = f.WpsFileId,
            ["batch"] = new JsonObject
            {
                ["id"] = f.WpsFileId, ["numberOfTransactions"] = f.Header.DeclaredRecordCount, ["controlSum"] = f.Header.DeclaredTotalAmount, ["currency"] = currency,
                ["categoryPurpose"] = "SALA", ["creditorAgent"] = new JsonObject { ["clearingMemberId"] = agent },
                ["debtorAgent"] = new JsonObject { ["clearingMemberId"] = f.Header.EmployerBankRoutingCode },
            },
            ["ext.uae.wps"] = new JsonObject
            {
                ["employerId"] = f.Header.EstablishmentId, ["fileCreationDate"] = f.Header.FileCreationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["fileCreationTime"] = f.Header.FileCreationTime, ["salaryMonth"] = f.Header.SalaryMonth, ["employerReference"] = f.Header.EmployerReference,
                ["fileName"] = fileName, ["evpCount"] = f.EvpCount,
            },
            ["items[]"] = items,
        });
    }
}

/// <summary>
/// <c>uae.wps.map.psi-in.v1</c>: the supplementary file that pairs with a payment file, with the source's parser
/// (<c>InwardPsiParser</c>): one CSI and at least one ESI. The context keeps the pairing (the WPS file id), the employer and the
/// count; the employees' names and labour card numbers stay in the file (personal data).
/// </summary>
public sealed class MapPsiIn : INativeActivity
{
    public string Name => "uae.wps.map.psi-in.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var (text, fileName) = InwardFiles.Inbound(a.View);
        if (text.Length == 0) return new NativeResult("unmappable", "FF01", new JsonObject());
        var r = InwardPsiParser.Parse(Encoding.UTF8.GetBytes(text), fileName ?? "");
        if (!r.IsSuccess) return new NativeResult("unmappable", "FF01", new JsonObject());
        var f = r.File!;
        var fileId = NativeContext.S(a.View, "header.fileId");
        if (fileId.Length > 0 && !string.Equals(fileId, f.WpsFileId, StringComparison.Ordinal))
            return new NativeResult("unmatched", "NARR", new JsonObject());
        return new NativeResult("mapped", null, new JsonObject
        {
            ["ext.uae.wps.supplement"] = new JsonObject { ["wpsFileId"] = f.WpsFileId, ["employerId"] = f.Csi.EmployerUniqueId, ["esiCount"] = f.EsiCount, ["fileName"] = fileName },
        });
    }
}

/// <summary>
/// <c>uae.wps.map.prc-out.v2</c>: the receipt confirmation of a payment file, with the source's builder (<c>PrcFileBuilder</c>,
/// <c>PrcFileNamePolicy</c>): one RDR and one RCR, the sums of the EDRs' fixed and variable pay, named for the agent and the
/// business instant. The host keeps the file.
/// </summary>
public sealed class RenderPrc(Action<string, string, byte[]>? keep = null) : INativeActivity
{
    public string Name => "uae.wps.map.prc-out.v2";

    public NativeResult Run(NativeAttempt a)
    {
        var agentId = NativeContext.Param(a.Params, "agentId");
        if (!PrcFileNamePolicy.IsValidAgentId(agentId)) return InwardFiles.Unmappable(Name, "CONFIG", "The bank's agentId is not three letters or digits");
        var pifName = NativeContext.S(a.View, "ext.uae.wps.fileName");
        if (pifName.Length == 0) pifName = NativeContext.S(a.View, "header.fileId");
        if (!PrcFileBuilder.IsPrintable(pifName)) return InwardFiles.Unmappable(Name, "PRC", "The payment file has no printable name");
        var items = NativeContext.Items(a.View);
        var fixedPay = items.Sum(i => NativeContext.D(i, "ext.uae.wps.fixedPay"));
        var variable = items.Sum(i => NativeContext.D(i, "ext.uae.wps.variablePay"));
        var declared = (int)NativeContext.D(a.View, "batch.numberOfTransactions");
        var instant = InwardFiles.BusinessSecond(a.Now);
        var bytes = PrcFileBuilder.BuildBytes(new PrcFileContent(pifName, fixedPay, variable, declared, instant));
        var name = PrcFileNamePolicy.NameFor(agentId, instant);
        var sha = InwardFiles.Sha256(bytes);
        keep?.Invoke(sha, name, bytes);
        var prc = new JsonObject { ["fileName"] = name, ["sha256"] = sha, ["sumFixed"] = fixedPay, ["sumVariable"] = variable, ["declared"] = declared };
        return new NativeResult("mapped", null, new JsonObject
        {
            ["messages.rendered"] = new JsonObject { ["by"] = Name, ["at"] = a.Now.ToString("O", CultureInfo.InvariantCulture), ["fileName"] = name, ["sha256"] = sha },
            ["messages.prc"] = prc,
        });
    }
}

/// <summary>
/// <c>uae.wps.map.dif-out.v2</c>: the disbursement file, with the source's builder (<c>DifLines</c>, <c>DifFileBuilder</c>): one DIR per
/// credit that posted, in item order, and a DCR. Its name is the PRC's with <c>.DIF</c> (the source's rule, <c>InwardPrcAckService.DifFileName</c>).
/// </summary>
public sealed class RenderDif(Action<string, string, byte[]>? keep = null) : INativeActivity
{
    public string Name => "uae.wps.map.dif-out.v2";

    public NativeResult Run(NativeAttempt a)
    {
        var agentId = NativeContext.Param(a.Params, "agentId");
        var prcName = NativeContext.S(a.View, "messages.prc.fileName");
        if (prcName.Length == 0) return InwardFiles.Unmappable(Name, DifErrorCodes.InvalidContent, "No receipt confirmation was rendered, so the DIF has no name");
        var dot = prcName.LastIndexOf('.');
        var difName = (dot > 0 ? prcName[..dot] : prcName) + ".DIF";
        var wpsFileId = NativeContext.S(a.View, "header.fileId");
        var items = NativeContext.Items(a.View);
        var posted = new List<DifCandidate>();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (NativeContext.S(it, "facts.posting.outcome") != "posted") continue;
            posted.Add(new DifCandidate(i + 1, NativeContext.S(it, "ext.uae.wps.personId"), NativeContext.S(it, "payment.creditorAccount.iban"),
                NativeContext.D(it, "ext.uae.wps.fixedPay"), NativeContext.D(it, "ext.uae.wps.variablePay"), i + 1, a.Now.UtcDateTime));
        }
        var invalid = DifFileBuilder.InvalidFields(wpsFileId, agentId, difName, posted);
        if (invalid.Count > 0) return InwardFiles.Unmappable(Name, DifErrorCodes.InvalidContent, "Fields not fit for the file: " + string.Join(", ", invalid));
        var lines = DifLines.Compose(posted, new Dictionary<long, decimal>(), "Asia/Dubai");
        var created = InwardFiles.BusinessSecond(a.Now);
        var total = DifFileBuilder.Total(lines);
        var record = new DifFileRecord(0, a.Tenant ?? "", NativeContext.S(a.View, "header.paymentId"), 0, 0, 1, difName, wpsFileId, agentId, lines.Count,
            items.Count - lines.Count, lines.Sum(l => l.FixedPay), lines.Sum(l => l.VariablePay), 0m, total, created, "Asia/Dubai", a.Now.UtcDateTime,
            DifFileBuilder.LayoutVersion1, "", 0, "", "runtime");
        var built = DifFileBuilder.Build(record, lines);
        keep?.Invoke(built.Sha256, difName, built.Bytes);
        return new NativeResult("mapped", null, new JsonObject
        {
            ["messages.rendered"] = new JsonObject { ["by"] = Name, ["at"] = a.Now.ToString("O", CultureInfo.InvariantCulture), ["fileName"] = difName, ["sha256"] = built.Sha256 },
            ["messages.dif"] = new JsonObject { ["fileName"] = difName, ["sha256"] = built.Sha256, ["lines"] = lines.Count, ["notIncluded"] = items.Count - lines.Count, ["total"] = total },
        });
    }
}

/// <summary>
/// <c>uae.wps.map.rfa-out.v1</c>: the returns file, with the source's builder (<c>RfaLines</c>, <c>RfaFileBuilder</c>, <c>RfaFileNamePolicy</c>):
/// one ARI per refused credit, each round2(fixed + variable) half away from zero, the total the sum of the lines. The returns decision's
/// count and amount MUST agree with the file, or the file is not rendered.
/// </summary>
public sealed class RenderRfa(Action<string, string, byte[]>? keep = null) : INativeActivity
{
    public string Name => "uae.wps.map.rfa-out.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var head = NativeContext.Param(a.Params, "headRoutingCode");
        if (!InwardRfaParameters.IsValidHeadRoutingCode(head)) return InwardFiles.Unmappable(Name, "CONFIG", "The bank's headRoutingCode is not 4 to 20 letters or digits");
        var items = NativeContext.Items(a.View);
        var candidates = new List<RfaCandidate>();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (!InwardFiles.Refused(it)) continue;
            var reason = NativeContext.S(it, "facts.posting.reasonCode");
            candidates.Add(new RfaCandidate(i + 1, NativeContext.S(it, "ext.uae.wps.personId"), NativeContext.D(it, "ext.uae.wps.fixedPay"), NativeContext.D(it, "ext.uae.wps.variablePay"),
                i + 1, reason.Length > 0 ? new RfaReason(reason, RfaReasonSources.PifEdrReturnReasons, i + 1) : RfaReason.Missing));
        }
        var wpsFileId = NativeContext.S(a.View, "header.fileId");
        var establishment = NativeContext.S(a.View, "ext.uae.wps.employerId");
        var invalid = RfaFileBuilder.InvalidFields(wpsFileId, establishment, candidates);
        if (candidates.Count == 0 || invalid.Count > 0)
            return InwardFiles.Unmappable(Name, RfaErrorCodes.InvalidInput, candidates.Count == 0 ? "No credit to return" : "Fields not fit for the file: " + string.Join(", ", invalid));
        var lines = RfaLines.Compose(candidates);
        var total = lines.Sum(l => l.AmountReturned);
        var decided = NativeContext.D(a.View, "facts.returns.amount");
        var decidedCount = (int)NativeContext.D(a.View, "facts.returns.count");
        if (decided != total || decidedCount != lines.Count)
            return InwardFiles.Unmappable(Name, RfaErrorCodes.InvalidInput, $"The returns decision ({decidedCount}, {decided}) does not match the file ({lines.Count}, {total})");
        var instant = InwardFiles.BusinessSecond(a.Now);
        var name = RfaFileNamePolicy.NameFor(head, instant);
        var record = new RfaFileRecord(0, a.Tenant ?? "", NativeContext.S(a.View, "header.paymentId"), 1, name, head, wpsFileId, establishment, lines.Count, 0, total,
            DateOnly.FromDateTime(instant), a.Now.UtcDateTime, RfaFileBuilder.LayoutVersion1, "", 0, null, "runtime");
        var built = RfaFileBuilder.Build(record, lines);
        keep?.Invoke(built.Sha256, name, built.Bytes);
        return new NativeResult("mapped", null, new JsonObject
        {
            ["messages.rendered"] = new JsonObject { ["by"] = Name, ["at"] = a.Now.ToString("O", CultureInfo.InvariantCulture), ["fileName"] = name, ["sha256"] = built.Sha256 },
            ["messages.rfa"] = new JsonObject { ["fileName"] = name, ["sha256"] = built.Sha256, ["lines"] = lines.Count, ["total"] = total },
        });
    }
}

/// <summary>
/// <c>uae.wps.map.ack-in.v1</c>: an acknowledgement of a file this bank sent. When the message carries the acknowledgement file, it is read
/// with the source's reader (<c>WpsAckNakFile</c>): ACCEPTED is an ACK, anything else a NAK, with the detail errors' codes. When it carries
/// only the scheme's status (a simulator's reply), the status decides.
/// </summary>
public sealed class MapAckIn : INativeActivity
{
    public string Name => "uae.wps.map.ack-in.v1";

    public NativeResult Run(NativeAttempt a)
    {
        // The acknowledgement the await took (messages.lastReceived), else the message that started the run.
        var inbound = NativeContext.Node(a.View, "messages.lastReceived") ?? NativeContext.Node(a.View, "messages.inbound");
        var payload = NativeContext.S(inbound, "payload");
        if (payload.TrimStart().StartsWith(WpsAckNakFile.RecordTypeHeader + ",", StringComparison.OrdinalIgnoreCase))
        {
            var doc = WpsAckNakFile.Parse(payload, NativeContext.S(inbound, "fileName") is { Length: > 0 } n ? n : null);
            if (doc.Status is null) return new NativeResult("nak", "FF01", new JsonObject { ["status"] = new JsonObject { ["code"] = "RJCT", ["reasonCode"] = "FF01" } });
            var reason = doc.DetailErrors.Select(e => e.ErrorNumber).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            return doc.IsAccepted
                ? new NativeResult("ack", null, new JsonObject { ["status"] = new JsonObject { ["code"] = "ACCP" } })
                : new NativeResult("nak", reason ?? "NARR", new JsonObject { ["status"] = new JsonObject { ["code"] = "RJCT", ["reasonCode"] = reason ?? "NARR" } });
        }
        var status = NativeContext.S(inbound, "status");
        var code = NativeContext.S(inbound, "reasonCode");
        var nak = status is "rejected" or "nak" or "refused";
        return nak
            ? new NativeResult("nak", code.Length > 0 ? code : "NARR", new JsonObject { ["status"] = new JsonObject { ["code"] = "RJCT", ["reasonCode"] = code.Length > 0 ? code : "NARR" } })
            : new NativeResult("ack", null, new JsonObject { ["status"] = new JsonObject { ["code"] = "ACCP" } });
    }
}

/// <summary>
/// <c>uae.wps.map.rri-in.v1</c>: a refund request instruction from WPS, with the source's parser (<c>InwardRriParser</c>): one DRC and its DRR
/// lines, the file name naming the establishment and the WPS file. Each line becomes an item: the employee's account, the amount to refund
/// and the reason. The bank's three-character file-name agent code is the activity's parameter.
/// </summary>
public sealed class MapRriIn : INativeActivity
{
    public string Name => "uae.wps.map.rri-in.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var agent = NativeContext.Param(a.Params, "fileNameAgentCode");
        if (agent.Length != InwardRriParameters.FileNameAgentCodeLength) return new NativeResult("unmappable", "FF01", new JsonObject());
        var (text, fileName) = InwardFiles.Inbound(a.View);
        if (text.Length == 0) return new NativeResult("unmappable", "FF01", new JsonObject());
        var r = InwardRriParser.Parse(Encoding.UTF8.GetBytes(text), fileName, agent);
        if (!r.IsSuccess) return new NativeResult("unmappable", "FF01", new JsonObject());
        var f = r.File!;
        var items = new JsonArray();
        foreach (var d in f.Drrs)
        {
            items.Add(new JsonObject
            {
                ["payment"] = new JsonObject
                {
                    ["debtorAccount"] = new JsonObject { ["iban"] = d.AccountNoRaw },
                    ["instructedAmount"] = new JsonObject { ["value"] = d.RefundAmount, ["currency"] = "AED" },
                },
                ["ext"] = new JsonObject { ["uae"] = new JsonObject { ["wps"] = new JsonObject { ["personId"] = d.PersonId, ["refundReason"] = d.ReasonCode, ["payrollWpsFileReference"] = d.PayrollWpsFileReference } } },
            });
        }
        return new NativeResult("mapped", null, new JsonObject
        {
            ["header.fileId"] = f.WpsFileReference, ["header.endToEndId"] = f.WpsFileReference,
            ["batch"] = new JsonObject { ["id"] = f.WpsFileReference, ["numberOfTransactions"] = f.Drc.DeclaredRecordCount, ["controlSum"] = f.Drc.DeclaredTotalAmount, ["currency"] = "AED" },
            ["ext.uae.wps"] = new JsonObject { ["establishmentReference"] = f.EstablishmentReference, ["fileName"] = fileName },
            ["items[]"] = items,
        });
    }
}

/// <summary>
/// <c>uae.wps.map.rtc-in.v2</c>: a return to the employer's bank, to the canonical inward return. The RTC layout is the factory's
/// provisional one (the source parses no RTC lines: its <c>ParseRtcRecords</c> takes only a name, a count and a total): one RTH line
/// (employer id, employer account, the employer's bank routing code, count, total, currency) and one RTD line per returned employee. The
/// payment-level creditor account is the employer's, so the leg's posting credits it (plan P2, RTC); the debtor account is the return file.
/// </summary>
public sealed class MapRtcIn : INativeActivity
{
    public string Name => "uae.wps.map.rtc-in.v2";

    public NativeResult Run(NativeAttempt a)
    {
        var (text, fileName) = InwardFiles.Inbound(a.View);
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var head = lines.Where(l => l.StartsWith("RTH,", StringComparison.Ordinal)).ToList();
        if (head.Count != 1) return new NativeResult("unmappable", "FF01", new JsonObject());
        var h = head[0].Split(',');
        if (h.Length != 7 || !int.TryParse(h[4], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || !decimal.TryParse(h[5], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var total))
            return new NativeResult("unmappable", "FF01", new JsonObject());
        var items = new JsonArray();
        var sum = 0m;
        foreach (var l in lines.Where(l => l.StartsWith("RTD,", StringComparison.Ordinal)))
        {
            var d = l.Split(',');
            if (d.Length != 5 || !decimal.TryParse(d[3], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return new NativeResult("unmappable", "FF01", new JsonObject());
            sum += amount;
            items.Add(new JsonObject
            {
                ["payment"] = new JsonObject { ["creditorAccount"] = new JsonObject { ["iban"] = d[2] }, ["instructedAmount"] = new JsonObject { ["value"] = amount, ["currency"] = h[6] } },
                ["ext"] = new JsonObject { ["uae"] = new JsonObject { ["wps"] = new JsonObject { ["personId"] = d[1], ["returnReason"] = d[4] } } },
            });
        }
        if (items.Count != count || sum != total) return new NativeResult("unmappable", "FF01", new JsonObject());
        var fileId = (fileName ?? $"RTC-{h[1]}").Replace(".rtc", "", StringComparison.OrdinalIgnoreCase);
        return new NativeResult("mapped", null, new JsonObject
        {
            ["header.fileId"] = fileId, ["header.endToEndId"] = fileId, ["header.direction"] = "inward",
            ["batch"] = new JsonObject { ["id"] = fileId, ["numberOfTransactions"] = count, ["controlSum"] = total, ["currency"] = h[6], ["categoryPurpose"] = "SALA", ["creditorAgent"] = new JsonObject { ["clearingMemberId"] = h[3] } },
            ["payment"] = new JsonObject
            {
                ["creditorAccount"] = new JsonObject { ["iban"] = h[2] },
                ["creditorAgent"] = new JsonObject { ["clearingMemberId"] = h[3] },
                ["debtorAccount"] = new JsonObject { ["other"] = fileId },
                ["instructedAmount"] = new JsonObject { ["value"] = total, ["currency"] = h[6] },
            },
            ["ext.uae.wps"] = new JsonObject { ["employerId"] = h[1], ["fileName"] = fileName },
            ["items[]"] = items,
            ["status"] = new JsonObject { ["code"] = "RCVD" },
        });
    }
}

/// <summary>
/// <c>p.decide.posting-window.v1</c>: whether core banking takes postings now, by the bank's window (<c>windowStart</c>, <c>windowEnd</c>,
/// HH:mm business-local), with the source's call-hours gate (<c>CbCallWindowGate</c>, here <c>PostingWindow</c>). An invalid window fails
/// closed. The composite <c>p.exchange.await-posting-window.v1</c> waits and asks again while it is closed (the schedule-gated await).
/// </summary>
public sealed class DecidePostingWindow : INativeActivity
{
    public string Name => "p.decide.posting-window.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var w = PostingWindow.Evaluate(NativeContext.Param(a.Params, "windowStart"), NativeContext.Param(a.Params, "windowEnd"), NativeContext.Business(a.Now));
        var state = w.State == PostingWindowState.Open ? "open" : "closed";
        var fact = new JsonObject { ["outcome"] = state, ["result"] = state, ["windowId"] = w.WindowId, ["at"] = w.NowLocal.ToString("HH:mm", CultureInfo.InvariantCulture) };
        if (w.State == PostingWindowState.ConfigInvalid) fact["invalid"] = true;
        return new NativeResult(state, null, new JsonObject { ["facts.postingWindow"] = fact });
    }
}

/// <summary>The WPS inward activities, as the native host registers them.</summary>
public static class WpsInwardActivities
{
    public static IReadOnlyList<INativeActivity> All(Action<string, string, byte[]>? keep = null) =>
        [new MapPifIn(), new MapPsiIn(), new RenderPrc(keep), new RenderDif(keep), new RenderRfa(keep), new MapAckIn(), new MapRriIn(), new MapRtcIn(), new DecidePostingWindow()];
}
