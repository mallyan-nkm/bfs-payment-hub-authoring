using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Platform.Native;

/// <summary>
/// The accounting template's values, as the activity's parameters carry them. Every parameter the posting needs MUST be present: a
/// missing one is named and the posting refused, never filled with a code default (the studio sends each declared parameter, at its
/// default or the bank's value).
/// </summary>
public sealed record PostingTemplate(string Template, string TransactionCode, string ReversalTransactionCode, string ChargeTransactionCode, string VatTransactionCode,
    string Narration, string ChargeNarration, string SettlementAccount, string SuspenseAccount, string ChargeIncomeAccount, string VatAccount)
{
    public static PostingTemplate From(JsonObject p, string defaultTemplate = "default")
    {
        string S(string k, string? fallback = null)
        {
            var v = NativeContext.Param(p, k);
            if (v.Length > 0) return v;
            return fallback ?? throw new NativeFailure("BusinessReject", $"The accounting template has no value for {k}", false);
        }
        return new PostingTemplate(S("template", defaultTemplate), S("transactionCode"), S("reversalTransactionCode", ""), S("chargeTransactionCode", ""), S("vatTransactionCode", ""),
            S("narration"), S("chargeNarration", ""), S("settlementAccount"), S("suspenseAccount", ""), S("chargeIncomeAccount", ""), S("vatAccount", ""));
    }

    /// <summary>The posting's reference: the payment's id (else its end-to-end id), with a suffix.</summary>
    public static string Reference(NativeAttempt a, string suffix)
    {
        var id = NativeContext.S(a.View, "header.paymentId");
        if (id.Length == 0) id = NativeContext.S(a.View, "header.endToEndId");
        return suffix.Length == 0 ? id : $"{id}:{suffix}";
    }

    /// <summary>The value date: the payment's business date, else the business day of the engine's time.</summary>
    public static DateOnly ValueDate(NativeAttempt a)
    {
        var d = NativeContext.S(a.View, "header.businessDate");
        return DateOnly.TryParse(d, CultureInfo.InvariantCulture, out var v) ? v : DateOnly.FromDateTime(NativeContext.Business(a.Now));
    }
}

/// <summary>
/// <c>p.post.accounting.v2</c> (R-12-23, R-12-25; Issue I-136): an order posted from what the platform's own steps found, as its
/// script-behind (studio/scripts/p.post.accounting.v2.ts) posts it. Each line, in line order, posts its converted amount
/// (<c>facts.fx.amount</c> in <c>facts.fx.to</c>, else its instructed amount) between the line's account and the settlement
/// account (a line with a debtor account is debited, one with only a creditor account credited), then its charge and its VAT
/// (<c>facts.charges</c>), each debited from the line's account. The template has no slabs: nothing is priced here. One batch for
/// the order, keyed by the order, so a retry is the same posting. Amounts are exact, numbers as the script writes them. Core banking
/// is the image's, through the binding in force (native contract 1.1.0).
/// </summary>
public sealed class PostAccountingV2(INativeCoreBanking coreBanking) : INativeActivity
{
    public string Name => "p.post.accounting.v2";

    static string Mask(string a) => a.Length <= 4 ? new string('*', a.Length) : new string('*', a.Length - 4) + a[^4..];

    public NativeResult Run(NativeAttempt a)
    {
        var b = coreBanking.For(a);
        var t = PostingTemplate.From(a.Params);
        var (_, lines) = Money.Lines(a.View, v => new JsonObject
        {
            ["payment"] = v["payment"]?.DeepClone() ?? new JsonObject(),
            ["facts"] = new JsonObject { ["fx"] = NativeContext.Node(v, "facts.fx")?.DeepClone(), ["charges"] = NativeContext.Node(v, "facts.charges")?.DeepClone() },
        });
        var reference = PostingTemplate.Reference(a, "");
        var narration = $"{t.Narration} {reference}".Trim();
        var legs = new List<NativePostingLeg>();
        var detail = new JsonArray();
        var customerTotal = 0m;
        var currencies = new List<string>();
        decimal amounts = 0m, chargeTotal = 0m, vatTotal = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var fx = NativeContext.Node(line, "facts.fx");
            var ch = NativeContext.Node(line, "facts.charges");
            var amount = Money.Read(NativeContext.Node(fx, "amount")) ?? Money.Read(NativeContext.Node(line, "payment.instructedAmount.value")) ?? 0m;
            var currency = new[] { NativeContext.S(fx, "to"), NativeContext.S(fx, "currency"), NativeContext.S(line, "payment.instructedAmount.currency"), "AED" }.First(c => c.Length > 0);
            var debit = NativeContext.Node(line, "payment.debtorAccount") is not null;
            var account = b.AccountOf(NativeContext.Node(line, debit ? "payment.debtorAccount" : "payment.creditorAccount"));
            if (account.Length == 0) return new NativeResult("rejected", "AC01", new JsonObject());
            var branch = b.BranchFor(account);
            var settlementBranch = b.BranchFor(t.SettlementAccount);
            legs.Add(new(debit ? "debit" : "credit", branch, account, amount, currency, t.TransactionCode, narration));
            legs.Add(new(debit ? "credit" : "debit", settlementBranch, t.SettlementAccount, amount, currency, t.TransactionCode, narration));
            var charge = Money.Read(NativeContext.Node(ch, "amount")) ?? 0m;
            var vat = Money.Read(NativeContext.Node(ch, "tax.amount")) ?? 0m;
            if (charge > 0m)
            {
                if (t.ChargeIncomeAccount.Length == 0 || t.ChargeTransactionCode.Length == 0) throw new NativeFailure("BusinessReject", "A charge applies but the template names no charge account or charge code", false);
                legs.Add(new("debit", branch, account, charge, currency, t.ChargeTransactionCode, t.ChargeNarration));
                legs.Add(new("credit", b.BranchFor(t.ChargeIncomeAccount), t.ChargeIncomeAccount, charge, currency, t.ChargeTransactionCode, t.ChargeNarration));
            }
            if (vat > 0m)
            {
                if (t.VatAccount.Length == 0 || t.VatTransactionCode.Length == 0) throw new NativeFailure("BusinessReject", "VAT applies but the template names no VAT account or VAT code", false);
                legs.Add(new("debit", branch, account, vat, currency, t.VatTransactionCode, t.ChargeNarration));
                legs.Add(new("credit", b.BranchFor(t.VatAccount), t.VatAccount, vat, currency, t.VatTransactionCode, t.ChargeNarration));
            }
            customerTotal += debit ? amount + charge + vat : amount - (charge + vat);
            amounts += amount; chargeTotal += charge; vatTotal += vat;
            if (!currencies.Contains(currency)) currencies.Add(currency);
            detail.Add(new JsonObject
            {
                ["line"] = i, ["side"] = debit ? "debit" : "credit", ["amount"] = Money.Json(amount), ["currency"] = currency,
                ["charge"] = Money.Json(charge), ["vat"] = Money.Json(vat),
            });
        }
        var posted = b.Post(new NativePostingBatch(reference, PostingTemplate.ValueDate(a), legs), "posted");
        var fact = posted.Fact;
        fact["template"] = t.Template;
        fact["reversalTransactionCode"] = t.ReversalTransactionCode;
        fact["entries"] = new JsonArray(legs.Select(l => (JsonNode?)new JsonObject
        {
            ["creditDebitIndicator"] = l.Kind == "debit" ? "DBIT" : "CRDT", ["amount"] = Money.Json(l.Amount), ["currency"] = l.Currency,
            ["account"] = Mask(l.Account), ["branch"] = l.Branch, ["transactionCode"] = l.TxnCode,
        }).ToArray());
        fact["entryCount"] = legs.Count;
        fact["lines"] = detail;
        fact["amount"] = Money.Json(amounts);
        fact["totalAmount"] = Money.Json(customerTotal);
        if (currencies.Count == 1) fact["currency"] = currencies[0];
        if (chargeTotal + vatTotal > 0m)
        {
            var rate = lines.Select(l => Money.Read(NativeContext.Node(l, "facts.charges.tax.rate"))).FirstOrDefault(r => r is not null)
                ?? Money.Read(NativeContext.Node(a.View, "facts.charges.tax.rate")) ?? 0m;
            var charges = new JsonObject { ["amount"] = Money.Json(chargeTotal) };
            if (currencies.Count == 1) charges["currency"] = currencies[0];
            charges["type"] = "BANK";
            charges["tax"] = new JsonObject { ["rate"] = Money.Json(rate), ["amount"] = Money.Json(vatTotal) };
            charges["totalChargesAndTaxAmount"] = Money.Json(chargeTotal + vatTotal);
            fact["charges"] = charges;
        }
        return new NativeResult(posted.Outcome, posted.ReasonCode, new JsonObject { ["facts.posting"] = fact });
    }
}
