using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Posting;

namespace TransactHub.Activities.Wps.Native;

/// <summary>
/// <c>uae.wps.post.return.v1</c> on the runtime (plan P2, RFA): once WPS accepts the agent's returns file, the returned amount
/// moves from the suspense account, where refused credits wait, back to the settlement account, through the bank's core-banking
/// binding. The source posted the same two legs for an accepted RFA (Activity 28, the RFA acknowledgement reversal posting).
/// The amount is the returns decision's <c>amount</c>; a decision without one is refused, never guessed.
/// </summary>
public sealed class PostReturn(CoreBankingResolver resolver) : CoreBankingActivity(resolver)
{
    public override string Name => "uae.wps.post.return.v1";

    public override NativeResult Run(NativeAttempt a)
    {
        var t = AccountingTemplate.From(a.Params, "wps-return");
        if (t.SuspenseAccount.Length == 0) throw new NativeFailure("BusinessReject", "The return's template has no value for suspenseAccount", false);
        var amount = NativeContext.D(a.View, "facts.returns.amount");
        if (amount <= 0) throw new NativeFailure("BusinessReject", "The returns decision carries no amount to post", false);
        var (cb, b) = Bound(a);
        var currency = NativeContext.S(a.View, "batch.currency") is { Length: > 0 } c ? c : "AED";
        var reference = Reference(a, "return");
        var narration = $"{t.Narration} {reference}".Trim();
        var batch = new Ports.PostingBatch(reference, ValueDate(a), [
            new("debit", BranchFor(t.SuspenseAccount, b), t.SuspenseAccount, amount, currency, t.TransactionCode, narration),
            new("credit", BranchFor(t.SettlementAccount, b), t.SettlementAccount, amount, currency, t.TransactionCode, narration),
        ]);
        var (outcome, reason, fact) = Post(cb, b, batch, "posted");
        fact["template"] = t.Template;
        fact["reversalTransactionCode"] = t.ReversalTransactionCode;
        return new NativeResult(outcome, reason, new JsonObject { ["facts.posting"] = fact });
    }
}
