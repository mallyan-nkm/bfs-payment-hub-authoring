using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Wps.Native;

/// <summary>
/// <c>uae.wps.post.return.v1</c> on the runtime (plan P2, RFA): once WPS accepts the agent's returns file, the returned amount
/// moves from the suspense account, where refused credits wait, back to the settlement account, through the bank's core-banking
/// binding. The source posted the same two legs for an accepted RFA (Activity 28, the RFA acknowledgement reversal posting).
/// The amount is the returns decision's <c>amount</c>; a decision without one is refused, never guessed. Core banking is the
/// image's, through the native contract (R-09-125).
/// </summary>
public sealed class PostReturn(INativeCoreBanking coreBanking) : INativeActivity
{
    public string Name => "uae.wps.post.return.v1";

    /// <summary>A template value the return needs: present, or the posting is refused by name (never a code default).</summary>
    static string Value(JsonObject p, string k, string? fallback = null) =>
        NativeContext.Param(p, k) is { Length: > 0 } v ? v : fallback ?? throw new NativeFailure("BusinessReject", $"The accounting template has no value for {k}", false);

    public NativeResult Run(NativeAttempt a)
    {
        var template = Value(a.Params, "template", "wps-return");
        var transactionCode = Value(a.Params, "transactionCode");
        var reversalTransactionCode = Value(a.Params, "reversalTransactionCode", "");
        var narrationText = Value(a.Params, "narration");
        var settlement = Value(a.Params, "settlementAccount");
        var suspense = Value(a.Params, "suspenseAccount", "");
        if (suspense.Length == 0) throw new NativeFailure("BusinessReject", "The return's template has no value for suspenseAccount", false);
        var amount = NativeContext.D(a.View, "facts.returns.amount");
        if (amount <= 0) throw new NativeFailure("BusinessReject", "The returns decision carries no amount to post", false);
        var b = coreBanking.For(a);
        var currency = NativeContext.S(a.View, "batch.currency") is { Length: > 0 } c ? c : "AED";
        var id = NativeContext.S(a.View, "header.paymentId") is { Length: > 0 } pid ? pid : NativeContext.S(a.View, "header.endToEndId");
        var reference = $"{id}:return";
        var narration = $"{narrationText} {reference}".Trim();
        var valueDate = DateOnly.TryParse(NativeContext.S(a.View, "header.businessDate"), CultureInfo.InvariantCulture, out var vd) ? vd : DateOnly.FromDateTime(NativeContext.Business(a.Now));
        var posted = b.Post(new NativePostingBatch(reference, valueDate, [
            new("debit", b.BranchFor(suspense), suspense, amount, currency, transactionCode, narration),
            new("credit", b.BranchFor(settlement), settlement, amount, currency, transactionCode, narration),
        ]), "posted");
        var fact = posted.Fact;
        fact["template"] = template;
        fact["reversalTransactionCode"] = reversalTransactionCode;
        return new NativeResult(posted.Outcome, posted.ReasonCode, new JsonObject { ["facts.posting"] = fact });
    }
}
