using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Platform.Native;

// The platform's reservation, its release, the reversal and the check of a posting (plan P2; R-08-15), the platform module's since native
// contract 2.1.0 (R-09-278, Issue I-382; they were the image's builtin reservation and accounting services). Each one that calls core banking
// does it through the image's binding in force for the attempt (INativeServices.CoreBanking), which asks the posting window gate this
// module contributes first (PostingGate, R-09-264). The image keeps the core-banking adapters and no core-banking activity.

/// <summary>
/// <c>p.reserve.funds.v1</c>: a balance enquiry on the debtor account (contract 2.1.0, <see cref="INativeCoreBankingBinding.Balance"/>);
/// enough available, in a status the binding posts to (<c>postableStatuses</c>, the source's CoreBanking:PostableAccountStatuses), reserves
/// it in the hub. The amount is the batch's control sum, else the payment's. It posts nothing, so a retry asks again.
/// </summary>
public sealed class ReserveFunds(INativeCoreBanking coreBanking) : INativeActivity
{
    public string Name => "p.reserve.funds.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var b = coreBanking.For(a);
        var account = b.AccountOf(NativeContext.Node(a.View, "payment.debtorAccount"));
        if (account.Length == 0) throw new NativeFailure("BusinessReject", "The payment names no debtor account to reserve on", false);
        var total = NativeContext.D(a.View, "batch.controlSum");
        var amount = total > 0 ? total : NativeContext.D(a.View, "payment.instructedAmount.value");
        var bal = b.Balance(b.BranchFor(account), account);
        var postable = b.Binding.List("postableStatuses");
        var statusOk = postable.Count == 0 || postable.Contains(bal.Status, StringComparer.OrdinalIgnoreCase);
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
/// <c>p.post.reverse.v1</c>: the posted legs again, each the other way, under the reversal code the posting recorded, keyed by the posting's
/// reference with <c>:REV</c>, so a retry is the same reversal. Nothing posted, nothing reversed; a posting of unknown fate goes to an
/// operator and is never reversed blind.
/// </summary>
public sealed class ReversePosting(INativeCoreBanking coreBanking) : INativeActivity
{
    public string Name => "p.post.reverse.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var posting = NativeContext.Node(a.View, "facts.posting") as JsonObject;
        var fact = posting?.DeepClone() as JsonObject ?? new JsonObject();
        if (NativeContext.S(posting, "outcome") != "posted")
        {
            if (NativeContext.S(posting, "outcome") == "unknown") throw new NativeFailure("BusinessReject", "The posting's fate is unknown; an operator decides, it is not reversed blind", false);
            fact["reversal"] = new JsonObject { ["outcome"] = "reversed", ["legs"] = new JsonArray() };
            return new NativeResult("reversed", null, new JsonObject { ["facts.posting"] = fact });
        }
        var b = coreBanking.For(a);
        var code = NativeContext.S(posting, "reversalTransactionCode");
        if (code.Length == 0) throw new NativeFailure("BusinessReject", "The posting recorded no reversal code", false);
        var reference = NativeContext.S(posting, "reference");
        var legs = (posting!["legs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(l => new NativePostingLeg(
            NativeContext.S(l, "kind") == "debit" ? "credit" : "debit", NativeContext.S(l, "branch"), NativeContext.S(l, "account"), NativeContext.D(l, "amount"),
            NativeContext.S(l, "currency"), code, $"Reversal {reference}")).ToList();
        var reversal = b.Post(new NativePostingBatch($"{reference}:REV", PostingTemplate.ValueDate(a), legs), "reversed");
        if (reversal.Outcome != "reversed")
            throw new NativeFailure(reversal.Outcome == "unknown" ? "TransientError" : "BusinessReject", $"Core banking did not take the reversal ({reversal.Outcome})", reversal.Outcome == "unknown");
        fact["reversal"] = reversal.Fact;
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
