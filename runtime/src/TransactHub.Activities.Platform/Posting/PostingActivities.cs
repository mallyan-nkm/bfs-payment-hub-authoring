using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Ports;

namespace TransactHub.Activities.Platform.Posting;

// The platform's balance, reservation and reversal activities on the runtime (plan P2): each one answers a factory activity of the
// p.port.core-banking.v1 port through the tenant's core-banking binding in force at the engine's time
// (PortBindings.InForce), so a bank switches core banking by a binding and nothing else. The adapter's wire settings come from the
// binding. Posting is never retried blindly: a call that may have reached core banking is `unknown`, as the source's
// account-transaction classifier made it (AccTxnReplyClassifier, Indeterminate).
//
// Since native contract 2.0.0 (R-09-264, Issues I-227, I-315) the image keeps no copy of the platform module's logic: p.post.accounting.v1
// and its reading of the accounting template are the platform module's, and the binding's posting window is checked by the gate the
// platform module contributes to its distribution (NativeContributions.BindingGate), which every call here on a core-banking binding asks.

/// <summary>
/// The core-banking calls the posting activities make, and the image's answer of <see cref="INativeCoreBanking"/> to a native module
/// (R-09-125): the binding in force with its posting window, the accounts and branches it keys, and the posting with its reply classified.
/// </summary>
public static class CoreBankingCalls
{
    /// <summary>The settings of a core-banking binding that name a posting window, which only a gate a module contributed reads.</summary>
    public static readonly IReadOnlyList<string> GateSettings = ["postingWindowStart", "postingWindowEnd"];

    /// <summary>
    /// The binding in force, gated (R-09-264): the gate a module of the distribution contributed on <c>p.port.core-banking.v1</c> (the
    /// platform module's posting window) is asked before core banking is called. A binding that names a posting window when no gate is
    /// contributed is refused, never called ungated.
    /// </summary>
    public static (ICoreBanking Cb, PortBinding Binding) Bound(CoreBankingResolver resolver, NativeAttempt a, Func<string, object?>? contributions = null)
    {
        var (cb, b) = resolver.For(a.Tenant, a.Now);
        if (contributions?.Invoke(NativeContributions.BindingGate(CoreBankingResolver.Port)) is NativeBindingGate gate) gate(a, NativeBoundPorts.ToNative(b));
        else if (GateSettings.Any(k => b.Setting(k, "").Length > 0))
            throw new NativeFailure("PortNotBound", $"The {b.Adapter} binding of {b.Port} for {b.Tenant} names a posting window and no module of the distribution gates it (the platform module contributes the gate): core banking is not called ungated", false);
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

/// <summary>
/// The image's core banking as a native module sees it (native contract 1.1.0, R-09-125), gated by what the distribution's modules
/// contributed (contract 2.0.0, R-09-264).
/// </summary>
public sealed class NativeCoreBanking(CoreBankingResolver resolver, Func<string, object?>? contributions = null) : INativeCoreBanking
{
    public INativeCoreBankingBinding For(NativeAttempt a)
    {
        var (cb, b) = CoreBankingCalls.Bound(resolver, a, contributions);
        return new BoundCoreBanking(cb, b);
    }

    sealed class BoundCoreBanking(ICoreBanking cb, PortBinding b) : INativeCoreBankingBinding
    {
        public string Adapter => b.Adapter;
        public NativeBinding Binding { get; } = NativeBoundPorts.ToNative(b);
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

/// <summary>What the posting activities share: the resolver, the accounts, the distribution's gate and the reply classification.</summary>
public abstract class CoreBankingActivity(CoreBankingResolver resolver, Func<string, object?>? contributions) : INativeActivity
{
    public abstract string Name { get; }
    public abstract NativeResult Run(NativeAttempt a);

    protected (ICoreBanking Cb, PortBinding Binding) Bound(NativeAttempt a) => CoreBankingCalls.Bound(resolver, a, contributions);
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
public sealed class ReserveFunds(CoreBankingResolver resolver, Func<string, object?>? contributions = null) : CoreBankingActivity(resolver, contributions)
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

/// <summary><c>p.post.reverse.v1</c>: the posted legs again, each the other way, under the reversal code the posting recorded.</summary>
public sealed class ReversePosting(CoreBankingResolver resolver, Func<string, object?>? contributions = null) : CoreBankingActivity(resolver, contributions)
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
/// The platform's core-banking activities the image answers (the builtin reservation service and the reversal and verification of a
/// posting, R-08-15), as the native host registers them for a distribution, gated by what its modules contributed. The posting itself,
/// <c>p.post.accounting.v1</c> and <c>p.post.accounting.v2</c>, is the platform module's (R-09-126, R-09-264).
/// </summary>
public static class PlatformActivities
{
    public static IReadOnlyList<INativeActivity> CoreBanking(CoreBankingResolver r, Func<string, object?>? contributions = null) =>
        [new ReserveFunds(r, contributions), new ReleaseReservation(), new ReversePosting(r, contributions), new VerifyPosting()];
}
