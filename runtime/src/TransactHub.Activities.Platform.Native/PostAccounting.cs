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
    string Narration, string ChargeNarration, string SettlementAccount, string SuspenseAccount, string ChargeIncomeAccount, string VatAccount,
    IReadOnlyList<ChargeSlab> ChargeSlabs, decimal VatPercent)
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
            S("narration"), S("chargeNarration", ""), S("settlementAccount"), S("suspenseAccount", ""), S("chargeIncomeAccount", ""), S("vatAccount", ""),
            ChargeSlab.Parse(p["chargeSlabs"]), decimal.TryParse(NativeContext.Param(p, "vatPercent"), NumberStyles.Number, CultureInfo.InvariantCulture, out var vat) ? vat : 0m);
    }

    /// <summary>The charge for an amount (<c>p.post.accounting.v1</c>): the first slab whose upper bound is not below it. No slab, no charge.</summary>
    public decimal ChargeFor(decimal amount) => ChargeSlabs.Where(s => amount <= s.UpTo).OrderBy(s => s.UpTo).FirstOrDefault()?.Charge ?? 0m;

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

/// <summary>One charge slab: up to (and including) an amount, a fixed charge.</summary>
public sealed record ChargeSlab(decimal UpTo, decimal Charge)
{
    public static IReadOnlyList<ChargeSlab> Parse(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0) n = JsonNode.Parse(text);
        if (n is not JsonArray a) return [];
        return a.OfType<JsonObject>().Select(o => new ChargeSlab(NativeContext.D(o, "upTo"), NativeContext.D(o, "charge"))).ToList();
    }
}

/// <summary>
/// <c>p.post.accounting.v1</c>, the builtin accounting service (R-08-15), as the platform module answers it since native contract 2.0.0
/// (R-09-264, Issue I-227; it was the image's). On an item (a salary line, a payment file's credit) or an inward payment: credit the
/// creditor's account from the settlement account. On an outward payment: debit the debtor's account to the settlement account, with
/// the template's charge (its slabs) and VAT (its per cent). One batch per attempt, keyed by the payment (and the item), so a retry is
/// the same posting. Core banking is the image's, through the binding in force, gated by the posting window (<see cref="PostingGate"/>).
/// </summary>
public sealed class PostAccountingV1(INativeCoreBanking coreBanking) : INativeActivity
{
    public string Name => "p.post.accounting.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var b = coreBanking.For(a);
        var t = PostingTemplate.From(a.Params);
        // The payment's own currency, never a platform default (R-09-185, Issue I-238).
        var currency = NativeContext.S(a.View, "payment.instructedAmount.currency") is { Length: > 0 } c ? c
            : throw new NativeFailure("BusinessReject", "The payment names no currency to post in", false);
        var settlementBranch = b.BranchFor(t.SettlementAccount);
        var legs = new List<NativePostingLeg>();
        string reference;
        (decimal Charge, decimal Vat)? charges = null;
        decimal total;
        var inward = a.Level == "item" || NativeContext.S(a.View, "header.direction") == "inward";
        if (inward)
        {
            var account = b.AccountOf(NativeContext.Node(a.View, "payment.creditorAccount"));
            if (account.Length == 0) throw new NativeFailure("BusinessReject", "The payment names no creditor account to credit", false);
            var amount = NativeContext.D(a.View, "payment.instructedAmount.value");
            reference = PostingTemplate.Reference(a, a.Level == "item" ? account : "");
            legs.Add(new("debit", settlementBranch, t.SettlementAccount, amount, currency, t.TransactionCode, $"{t.Narration} {reference}".Trim()));
            legs.Add(new("credit", b.BranchFor(account), account, amount, currency, t.TransactionCode, $"{t.Narration} {reference}".Trim()));
            total = amount;
        }
        else
        {
            var reserved = NativeContext.S(a.View, "facts.reservation.account");
            var account = reserved.Length > 0 ? reserved : b.AccountOf(NativeContext.Node(a.View, "payment.debtorAccount"));
            if (account.Length == 0) throw new NativeFailure("BusinessReject", "The payment names no debtor account to debit", false);
            var reservedAmount = NativeContext.D(a.View, "facts.reservation.amount");
            var amount = reservedAmount > 0 ? reservedAmount : NativeContext.D(a.View, "payment.instructedAmount.value");
            reference = PostingTemplate.Reference(a, "");
            var branch = b.BranchFor(account);
            legs.Add(new("debit", branch, account, amount, currency, t.TransactionCode, $"{t.Narration} {reference}".Trim()));
            legs.Add(new("credit", settlementBranch, t.SettlementAccount, amount, currency, t.TransactionCode, $"{t.Narration} {reference}".Trim()));
            var charge = t.ChargeFor(amount);
            total = amount;
            if (charge > 0)
            {
                if (t.ChargeIncomeAccount.Length == 0 || t.ChargeTransactionCode.Length == 0) throw new NativeFailure("BusinessReject", "A charge applies but the template names no charge account or charge code", false);
                legs.Add(new("debit", branch, account, charge, currency, t.ChargeTransactionCode, t.ChargeNarration));
                legs.Add(new("credit", b.BranchFor(t.ChargeIncomeAccount), t.ChargeIncomeAccount, charge, currency, t.ChargeTransactionCode, t.ChargeNarration));
                var vat = decimal.Round(charge * t.VatPercent / 100m, 2, MidpointRounding.AwayFromZero);
                if (vat > 0)
                {
                    if (t.VatAccount.Length == 0 || t.VatTransactionCode.Length == 0) throw new NativeFailure("BusinessReject", "VAT applies but the template names no VAT account or VAT code", false);
                    legs.Add(new("debit", branch, account, vat, currency, t.VatTransactionCode, t.ChargeNarration));
                    legs.Add(new("credit", b.BranchFor(t.VatAccount), t.VatAccount, vat, currency, t.VatTransactionCode, t.ChargeNarration));
                }
                charges = (charge, vat);
                total += charge + vat;
            }
        }
        var posted = b.Post(new NativePostingBatch(reference, PostingTemplate.ValueDate(a), legs), "posted");
        var fact = posted.Fact;
        fact["template"] = t.Template;
        fact["reversalTransactionCode"] = t.ReversalTransactionCode;
        Statement(fact, legs, total, currency, charges, t.VatPercent);
        return new NativeResult(posted.Outcome, posted.ReasonCode, new JsonObject { ["facts.posting"] = fact });
    }

    /// <summary>
    /// The posting as camt.054 reports it (Issue I-100): its entries, each with its side and the account masked to its last four
    /// characters; the amount debited with charges (totalAmount); and the charges with their tax (ChrgsInf). Amounts are decimal
    /// strings (R-02-17). The legs stay as they were, for a reversal.
    /// </summary>
    public static void Statement(JsonObject fact, IEnumerable<NativePostingLeg> legs, decimal total, string currency, (decimal Charge, decimal Vat)? charges, decimal vatPercent)
    {
        static string Amt(decimal d) => d.ToString("0.00##", CultureInfo.InvariantCulture);
        fact["entries"] = new JsonArray(legs.Select(l => (JsonNode?)new JsonObject
        {
            ["creditDebitIndicator"] = l.Kind == "debit" ? "DBIT" : "CRDT", ["amount"] = Amt(l.Amount), ["currency"] = l.Currency,
            ["account"] = Mask(l.Account), ["branch"] = l.Branch, ["transactionCode"] = l.TxnCode,
        }).ToArray());
        fact["totalAmount"] = Amt(total);
        fact["currency"] = currency;
        if (charges is { } ch)
            fact["charges"] = new JsonObject
            {
                ["amount"] = Amt(ch.Charge), ["currency"] = currency, ["type"] = "BANK",
                ["tax"] = ch.Vat > 0 ? new JsonObject { ["rate"] = Amt(vatPercent), ["amount"] = Amt(ch.Vat) } : null,
                ["totalChargesAndTaxAmount"] = Amt(ch.Charge + ch.Vat),
            };
    }

    static string Mask(string a) => a.Length <= 4 ? new string('*', a.Length) : new string('*', a.Length - 4) + a[^4..];
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
            // The converted currency, else the line's own: never a platform default (R-09-185, Issue I-238).
            var currency = new[] { NativeContext.S(fx, "to"), NativeContext.S(fx, "currency"), NativeContext.S(line, "payment.instructedAmount.currency") }.FirstOrDefault(c => c.Length > 0)
                ?? throw new NativeFailure("BusinessReject", $"Line {i + 1} names no currency to post in", false);
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
