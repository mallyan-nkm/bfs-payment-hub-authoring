using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Ports;

namespace TransactHub.Activities.Platform.Posting;

// The platform's posting and balance activities on the runtime (plan P2): each one answers a factory activity of the
// p.port.core-banking.v1 port through the tenant's core-banking binding in force at the engine's time
// (PortBindings.InForce), so a bank switches core banking by a binding and nothing else. The legs come from the
// accounting template's parameters (transaction codes, narration, accounts, charge slabs and VAT); the adapter's wire
// settings come from the binding. Posting is never retried blindly: a call that may have reached core banking is
// `unknown`, as the source's account-transaction classifier made it (AccTxnReplyClassifier, Indeterminate).

/// <summary>The accounting template's values, as the activity's parameters carry them.</summary>
public sealed record AccountingTemplate(
    string Template, string TransactionCode, string ReversalTransactionCode, string ChargeTransactionCode, string VatTransactionCode,
    string Narration, string ChargeNarration, string SettlementAccount, string SuspenseAccount, string ChargeIncomeAccount, string VatAccount,
    IReadOnlyList<ChargeSlab> ChargeSlabs, decimal VatPercent)
{
    /// <summary>
    /// The template from the attempt's parameters. Every parameter the posting needs MUST be present: a missing one is named and
    /// the posting refused, never filled with a code default (the studio sends each declared parameter, at its default or the bank's value).
    /// </summary>
    public static AccountingTemplate From(JsonObject p, string defaultTemplate = "default")
    {
        string S(string k, string? fallback = null)
        {
            var v = NativeContext.Param(p, k);
            if (v.Length > 0) return v;
            return fallback ?? throw new NativeFailure("BusinessReject", $"The accounting template has no value for {k}", false);
        }
        return new AccountingTemplate(
            S("template", defaultTemplate), S("transactionCode"), S("reversalTransactionCode", ""), S("chargeTransactionCode", ""), S("vatTransactionCode", ""),
            S("narration"), S("chargeNarration", ""), S("settlementAccount"), S("suspenseAccount", ""), S("chargeIncomeAccount", ""), S("vatAccount", ""),
            ChargeSlab.Parse(p["chargeSlabs"]), decimal.TryParse(NativeContext.Param(p, "vatPercent"), NumberStyles.Number, CultureInfo.InvariantCulture, out var vat) ? vat : 0m);
    }

    /// <summary>The charge for an amount: the first slab whose upper bound is not below it. No slab, no charge.</summary>
    public decimal ChargeFor(decimal amount) => ChargeSlabs.Where(s => amount <= s.UpTo).OrderBy(s => s.UpTo).FirstOrDefault()?.Charge ?? 0m;
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
/// The core-banking calls the posting activities make, and the image's answer of <see cref="INativeCoreBanking"/> to a native module
/// (R-09-125): the binding in force with its posting window, the accounts and branches it keys, and the posting with its reply classified.
/// </summary>
public static class CoreBankingCalls
{
    public static (ICoreBanking Cb, PortBinding Binding) Bound(CoreBankingResolver resolver, NativeAttempt a)
    {
        var (cb, b) = resolver.For(a.Tenant, a.Now);
        // Posting windows: the hours the bound core banking takes calls, business-local, as settings of the binding.
        var start = b.Setting("postingWindowStart", "");
        var end = b.Setting("postingWindowEnd", "");
        if (start.Length > 0 || end.Length > 0)
        {
            var w = PostingWindow.Evaluate(start, end, NativeContext.Business(a.Now));
            if (w.State == PostingWindowState.ConfigInvalid)
                throw new NativeFailure("BusinessReject", $"The posting window of {b.Port} for {b.Tenant} is not HH:mm to HH:mm", false);
            if (w.State == PostingWindowState.Closed)
                throw new NativeFailure("TransientError", $"Core banking takes postings from {start} to {end}; it is {w.NowLocal:HH:mm}", true);
        }
        return (cb, b);
    }

    /// <summary>The account core banking knows: its id, else its IBAN. A binding may say core banking keys accounts by an IBAN's last digits.</summary>
    public static string AccountOf(JsonNode? account, PortBinding b)
    {
        var id = NativeContext.S(account, "id");
        if (id.Length > 0) return id;
        var iban = NativeContext.S(account, "iban");
        if (iban.Length > 0 && int.TryParse(b.Setting("ibanAccountDigits", ""), out var n) && n > 0 && iban.Length > n) return iban[^n..];
        return iban;
    }

    /// <summary>The source's branch rule: an account number longer than nine digits carries its branch in its first three; otherwise the binding's.</summary>
    public static string BranchFor(string account, PortBinding b) =>
        account.Length > 9 && account.All(char.IsAsciiDigit) ? account[..3] : b.Setting("defaultBranch", b.Setting("uploadBranch", ""));

    public static JsonObject LegsJson(IEnumerable<PostingLeg> legs) => new()
    {
        ["legs"] = new JsonArray(legs.Select(l => (JsonNode?)new JsonObject
        {
            ["kind"] = l.Kind, ["branch"] = l.Branch, ["account"] = l.Account, ["amount"] = l.Amount, ["currency"] = l.Currency, ["txnCode"] = l.TxnCode, ["narration"] = l.Narration,
        }).ToArray()),
    };

    /// <summary>Posts a batch and classifies the reply: posted, rejected by core banking, or unknown when the call may have reached it.</summary>
    public static (string Outcome, string? Reason, JsonObject Fact) Post(ICoreBanking cb, PortBinding b, PostingBatch batch, string primary)
    {
        var fact = LegsJson(batch.Legs);
        fact["adapter"] = b.Adapter;
        fact["bindingFrom"] = b.ValidFrom.ToString("O", CultureInfo.InvariantCulture);
        fact["reference"] = batch.Reference;
        fact["valueDate"] = batch.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        PostingAnswer answer;
        try
        {
            answer = cb.Post(batch).GetAwaiter().GetResult();
        }
        catch (PortBindingException e)
        {
            throw new NativeFailure("BusinessReject", e.Message, false);
        }
        catch (TransactHub.Security.SecretUnavailable e)
        {
            // Read before anything is sent, so nothing reached core banking. The message names the reference, never a value.
            throw new NativeFailure("SecretUnavailable", e.Message, false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            // It may have reached core banking: never posted again automatically (the source's Indeterminate).
            fact["outcome"] = "unknown"; fact["result"] = "unknown";
            fact["error"] = e.GetType().Name;
            return ("unknown", "NARR", fact);
        }
        fact["code"] = answer.Code;
        fact["hostReference"] = answer.HostReference;
        // An adapter that posts line by line (file-bulk) says how each leg came out: a line it failed, after its reposts, is named here.
        if (answer.Lines is { } lines)
            fact["lines"] = new JsonArray(lines.Select(l => (JsonNode?)new JsonObject
            {
                ["line"] = l.Line, ["posted"] = l.Posted, ["code"] = l.Code, ["generation"] = l.Generation,
            }).ToArray());
        var outcome = answer.Posted ? primary : "rejected";
        fact["outcome"] = outcome; fact["result"] = outcome;
        return (outcome, answer.Posted ? null : "NARR", fact);
    }
}

/// <summary>The image's core banking as a native module sees it (native contract 1.1.0, R-09-125).</summary>
public sealed class NativeCoreBanking(CoreBankingResolver resolver) : INativeCoreBanking
{
    public INativeCoreBankingBinding For(NativeAttempt a)
    {
        var (cb, b) = CoreBankingCalls.Bound(resolver, a);
        return new Binding(cb, b);
    }

    sealed class Binding(ICoreBanking cb, PortBinding b) : INativeCoreBankingBinding
    {
        public string Adapter => b.Adapter;
        public string AccountOf(JsonNode? account) => CoreBankingCalls.AccountOf(account, b);
        public string BranchFor(string account) => CoreBankingCalls.BranchFor(account, b);

        public NativePosted Post(NativePostingBatch batch, string primary)
        {
            var (outcome, reason, fact) = CoreBankingCalls.Post(cb, b, new PostingBatch(batch.Reference, batch.ValueDate,
                batch.Legs.Select(l => new PostingLeg(l.Kind, l.Branch, l.Account, l.Amount, l.Currency, l.TxnCode, l.Narration)).ToList()), primary);
            return new NativePosted(outcome, reason, fact);
        }
    }
}

/// <summary>What the posting activities share: the resolver, the accounts, the window gate and the reply classification.</summary>
public abstract class CoreBankingActivity(CoreBankingResolver resolver) : INativeActivity
{
    public abstract string Name { get; }
    public abstract NativeResult Run(NativeAttempt a);

    protected (ICoreBanking Cb, PortBinding Binding) Bound(NativeAttempt a) => CoreBankingCalls.Bound(resolver, a);
    protected static string AccountOf(JsonNode? account, PortBinding b) => CoreBankingCalls.AccountOf(account, b);
    protected static string BranchFor(string account, PortBinding b) => CoreBankingCalls.BranchFor(account, b);
    protected static JsonObject LegsJson(IEnumerable<PostingLeg> legs) => CoreBankingCalls.LegsJson(legs);
    protected static (string Outcome, string? Reason, JsonObject Fact) Post(ICoreBanking cb, PortBinding b, PostingBatch batch, string primary) => CoreBankingCalls.Post(cb, b, batch, primary);

    protected static string Reference(NativeAttempt a, string suffix)
    {
        var id = NativeContext.S(a.View, "header.paymentId");
        if (id.Length == 0) id = NativeContext.S(a.View, "header.endToEndId");
        return suffix.Length == 0 ? id : $"{id}:{suffix}";
    }

    protected static DateOnly ValueDate(NativeAttempt a)
    {
        var d = NativeContext.S(a.View, "header.businessDate");
        return DateOnly.TryParse(d, CultureInfo.InvariantCulture, out var v) ? v : DateOnly.FromDateTime(NativeContext.Business(a.Now));
    }
}

/// <summary><c>p.reserve.funds.v1</c>: a balance enquiry on the debtor account; enough available, in a postable status, reserves it in the hub.</summary>
public sealed class ReserveFunds(CoreBankingResolver resolver) : CoreBankingActivity(resolver)
{
    public override string Name => "p.reserve.funds.v1";

    public override NativeResult Run(NativeAttempt a)
    {
        var (cb, b) = Bound(a);
        var account = AccountOf(NativeContext.Node(a.View, "payment.debtorAccount"), b);
        if (account.Length == 0) throw new NativeFailure("BusinessReject", "The payment names no debtor account to reserve on", false);
        var total = NativeContext.D(a.View, "batch.controlSum");
        var amount = total > 0 ? total : NativeContext.D(a.View, "payment.instructedAmount.value");
        BalanceAnswer bal;
        try { bal = cb.Balance(BranchFor(account, b), account).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { throw new NativeFailure("TransientError", $"Balance enquiry failed: {e.GetType().Name}", true); }
        catch (TransactHub.Security.SecretUnavailable e) { throw new NativeFailure("SecretUnavailable", e.Message, false); }
        // The statuses core banking posts to are a setting of the binding (the source's CoreBanking:PostableAccountStatuses).
        var postable = b.Setting("postableStatuses", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var statusOk = postable.Length == 0 || postable.Contains(bal.Status, StringComparer.OrdinalIgnoreCase);
        var outcome = statusOk && bal.Available >= amount ? "reserved" : "insufficient";
        var fact = new JsonObject
        {
            ["outcome"] = outcome, ["result"] = outcome, ["adapter"] = b.Adapter, ["account"] = account, ["amount"] = amount, ["available"] = bal.Available,
            ["currency"] = bal.Currency, ["status"] = bal.Status,
        };
        return new NativeResult(outcome, outcome == "reserved" ? null : statusOk ? "AM04" : "AC06", new JsonObject { ["facts.reservation"] = fact });
    }
}

/// <summary><c>p.reserve.release.v1</c>: the reservation is the hub's own, so releasing it asks core banking nothing.</summary>
public sealed class ReleaseReservation : INativeActivity
{
    public string Name => "p.reserve.release.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var fact = (NativeContext.Node(a.View, "facts.reservation") as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        fact["released"] = true;
        fact["outcome"] = "released"; fact["result"] = "released";
        return new NativeResult("released", null, new JsonObject { ["facts.reservation"] = fact });
    }
}

/// <summary>
/// <c>p.post.accounting.v1</c>. On an item (a salary line, a payment file's credit) or an inward payment: credit the creditor's
/// account from the settlement account. On an outward payment: debit the debtor's account to the settlement account, with the
/// template's charge and VAT. One batch per attempt, keyed by the payment (and the item), so a retry is the same posting.
/// </summary>
public sealed class PostAccounting(CoreBankingResolver resolver) : CoreBankingActivity(resolver)
{
    public override string Name => "p.post.accounting.v1";

    public override NativeResult Run(NativeAttempt a)
    {
        var (cb, b) = Bound(a);
        var t = AccountingTemplate.From(a.Params);
        // The payment's own currency, never a platform default (R-09-185, Issue I-238).
        var currency = NativeContext.S(a.View, "payment.instructedAmount.currency") is { Length: > 0 } c ? c
            : throw new NativeFailure("BusinessReject", "The payment names no currency to post in", false);
        var settlementBranch = BranchFor(t.SettlementAccount, b);
        var legs = new List<PostingLeg>();
        string reference;
        TransactHub.Model.ChargesInformation? charges = null;
        decimal total;
        var inward = a.Level == "item" || NativeContext.S(a.View, "header.direction") == "inward";
        if (inward)
        {
            var account = AccountOf(NativeContext.Node(a.View, "payment.creditorAccount"), b);
            if (account.Length == 0) throw new NativeFailure("BusinessReject", "The payment names no creditor account to credit", false);
            var amount = NativeContext.D(a.View, "payment.instructedAmount.value");
            reference = Reference(a, a.Level == "item" ? account : "");
            legs.Add(new("debit", settlementBranch, t.SettlementAccount, amount, currency, t.TransactionCode, $"{t.Narration} {reference}".Trim()));
            legs.Add(new("credit", BranchFor(account, b), account, amount, currency, t.TransactionCode, $"{t.Narration} {reference}".Trim()));
            total = amount;
        }
        else
        {
            var reserved = NativeContext.S(a.View, "facts.reservation.account");
            var account = reserved.Length > 0 ? reserved : AccountOf(NativeContext.Node(a.View, "payment.debtorAccount"), b);
            if (account.Length == 0) throw new NativeFailure("BusinessReject", "The payment names no debtor account to debit", false);
            var reservedAmount = NativeContext.D(a.View, "facts.reservation.amount");
            var amount = reservedAmount > 0 ? reservedAmount : NativeContext.D(a.View, "payment.instructedAmount.value");
            reference = Reference(a, "");
            var branch = BranchFor(account, b);
            legs.Add(new("debit", branch, account, amount, currency, t.TransactionCode, $"{t.Narration} {reference}".Trim()));
            legs.Add(new("credit", settlementBranch, t.SettlementAccount, amount, currency, t.TransactionCode, $"{t.Narration} {reference}".Trim()));
            var charge = t.ChargeFor(amount);
            total = amount;
            if (charge > 0)
            {
                if (t.ChargeIncomeAccount.Length == 0 || t.ChargeTransactionCode.Length == 0) throw new NativeFailure("BusinessReject", "A charge applies but the template names no charge account or charge code", false);
                legs.Add(new("debit", branch, account, charge, currency, t.ChargeTransactionCode, t.ChargeNarration));
                legs.Add(new("credit", BranchFor(t.ChargeIncomeAccount, b), t.ChargeIncomeAccount, charge, currency, t.ChargeTransactionCode, t.ChargeNarration));
                var vat = decimal.Round(charge * t.VatPercent / 100m, 2, MidpointRounding.AwayFromZero);
                if (vat > 0)
                {
                    if (t.VatAccount.Length == 0 || t.VatTransactionCode.Length == 0) throw new NativeFailure("BusinessReject", "VAT applies but the template names no VAT account or VAT code", false);
                    legs.Add(new("debit", branch, account, vat, currency, t.VatTransactionCode, t.ChargeNarration));
                    legs.Add(new("credit", BranchFor(t.VatAccount, b), t.VatAccount, vat, currency, t.VatTransactionCode, t.ChargeNarration));
                }
                charges = new(new(charge, currency), "BANK", vat > 0 ? new(t.VatPercent, new(vat, currency)) : null);
                total += charges.TotalChargesAndTax.Amount;
            }
        }
        var (outcome, reason, fact) = Post(cb, b, new PostingBatch(reference, ValueDate(a), legs), "posted");
        fact["template"] = t.Template;
        fact["reversalTransactionCode"] = t.ReversalTransactionCode;
        Statement(fact, legs, total, currency, charges);
        return new NativeResult(outcome, reason, new JsonObject { ["facts.posting"] = fact });
    }

    /// <summary>
    /// The posting as camt.054 reports it (Issue I-100): its entries, each with its side and the account masked to its last four
    /// characters; the amount debited with charges (totalAmount); and the charges with their tax (ChrgsInf). Amounts are decimal
    /// strings (R-02-17). The legs stay as they were, for a reversal.
    /// </summary>
    public static void Statement(JsonObject fact, IEnumerable<PostingLeg> legs, decimal total, string currency, TransactHub.Model.ChargesInformation? charges)
    {
        static string Amt(decimal d) => d.ToString("0.00##", CultureInfo.InvariantCulture);
        static string Mask(string a) => a.Length <= 4 ? new string('*', a.Length) : new string('*', a.Length - 4) + a[^4..];
        fact["entries"] = new JsonArray(legs.Select(l => new TransactHub.Model.PostingEntry(l.Kind == "debit" ? TransactHub.Model.CreditDebit.DBIT : TransactHub.Model.CreditDebit.CRDT,
                new(l.Amount, l.Currency), Mask(l.Account), l.Branch, l.TxnCode))
            .Select(e => (JsonNode?)new JsonObject
            {
                ["creditDebitIndicator"] = e.Indicator.ToString(), ["amount"] = Amt(e.Amount.Amount), ["currency"] = e.Amount.Currency,
                ["account"] = e.MaskedAccount, ["branch"] = e.Branch, ["transactionCode"] = e.TransactionCode,
            }).ToArray());
        fact["totalAmount"] = Amt(total);
        fact["currency"] = currency;
        if (charges is not null)
            fact["charges"] = new JsonObject
            {
                ["amount"] = Amt(charges.Amount.Amount), ["currency"] = charges.Amount.Currency, ["type"] = charges.Type,
                ["tax"] = charges.Tax is null ? null : new JsonObject { ["rate"] = Amt(charges.Tax.Rate), ["amount"] = Amt(charges.Tax.Amount.Amount) },
                ["totalChargesAndTaxAmount"] = Amt(charges.TotalChargesAndTax.Amount),
            };
    }
}

/// <summary><c>p.post.reverse.v1</c>: the posted legs again, each the other way, under the reversal code the posting recorded.</summary>
public sealed class ReversePosting(CoreBankingResolver resolver) : CoreBankingActivity(resolver)
{
    public override string Name => "p.post.reverse.v1";

    public override NativeResult Run(NativeAttempt a)
    {
        var posting = NativeContext.Node(a.View, "facts.posting") as JsonObject;
        var fact = posting?.DeepClone() as JsonObject ?? new JsonObject();
        if (NativeContext.S(posting, "outcome") != "posted")
        {
            // Nothing was posted, so nothing is reversed; a posting of unknown fate goes to an operator, never reversed blind.
            if (NativeContext.S(posting, "outcome") == "unknown") throw new NativeFailure("BusinessReject", "The posting's fate is unknown; an operator decides, it is not reversed blind", false);
            fact["reversal"] = new JsonObject { ["outcome"] = "reversed", ["legs"] = new JsonArray() };
            return new NativeResult("reversed", null, new JsonObject { ["facts.posting"] = fact });
        }
        var (cb, b) = Bound(a);
        var code = NativeContext.S(posting, "reversalTransactionCode");
        if (code.Length == 0) throw new NativeFailure("BusinessReject", "The posting recorded no reversal code", false);
        var legs = (posting!["legs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(l => new PostingLeg(
            NativeContext.S(l, "kind") == "debit" ? "credit" : "debit", NativeContext.S(l, "branch"), NativeContext.S(l, "account"), NativeContext.D(l, "amount"),
            NativeContext.S(l, "currency"), code, $"Reversal {NativeContext.S(posting, "reference")}")).ToList();
        var (outcome, _, reversal) = Post(cb, b, new PostingBatch($"{NativeContext.S(posting, "reference")}:REV", ValueDate(a), legs), "reversed");
        if (outcome != "reversed")
            throw new NativeFailure(outcome == "unknown" ? "TransientError" : "BusinessReject", $"Core banking did not take the reversal ({outcome})", outcome == "unknown");
        fact["reversal"] = reversal;
        return new NativeResult("reversed", null, new JsonObject { ["facts.posting"] = fact });
    }
}

/// <summary><c>p.post.verify.v1</c>: a posting is verified when core banking answered it posted and gave its own reference.</summary>
public sealed class VerifyPosting : INativeActivity
{
    public string Name => "p.post.verify.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var posting = NativeContext.Node(a.View, "facts.posting");
        var ok = NativeContext.S(posting, "outcome") == "posted" && NativeContext.S(posting, "hostReference").Length > 0;
        var outcome = ok ? "verified" : "mismatch";
        return new NativeResult(outcome, ok ? null : "NARR", new JsonObject
        {
            ["facts.postingVerified"] = new JsonObject { ["outcome"] = outcome, ["result"] = outcome, ["hostReference"] = NativeContext.S(posting, "hostReference") },
        });
    }
}

/// <summary>
/// The platform's core-banking activities the image answers (the builtin accounting and reservation services, R-08-15), as the native
/// host registers them. <c>p.post.accounting.v2</c>, a native code-behind, is the platform module's (R-09-126).
/// </summary>
public static class PlatformActivities
{
    public static IReadOnlyList<INativeActivity> CoreBanking(CoreBankingResolver r) =>
        [new ReserveFunds(r), new ReleaseReservation(), new PostAccounting(r), new ReversePosting(r), new VerifyPosting()];
}
