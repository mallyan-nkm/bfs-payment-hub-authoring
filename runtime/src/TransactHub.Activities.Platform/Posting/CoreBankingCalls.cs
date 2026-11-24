using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Ports;

namespace TransactHub.Activities.Platform.Posting;

// The image's core banking as a native module calls it (plan P2, R-09-125): the tenant's core-banking binding in force at the engine's
// time (PortBindings.InForce) chooses the adapter, so a bank switches core banking by a binding and nothing else. The adapter's wire
// settings come from the binding. Posting is never retried blindly: a call that may have reached core banking is `unknown`, as the
// source's account-transaction classifier made it (AccTxnReplyClassifier, Indeterminate).
//
// Since native contract 2.0.0 (R-09-264, Issues I-227, I-315) the image keeps no copy of the platform module's logic: p.post.accounting.v1
// and its reading of the accounting template are the platform module's, and the binding's posting window is checked by the gate the
// platform module contributes to its distribution (NativeContributions.BindingGate). Since 2.1.0 (R-09-278, R-09-279, Issue I-382) the image
// keeps no core-banking activity at all: the reservation, its release, the reversal and the check of a posting are the platform module's
// too, on the balance enquiry the contract gives (INativeCoreBankingBinding.Balance). The image names no setting of a posting window: it
// calls core banking only through a gate a module contributed, and with none every call is refused.

/// <summary>
/// The core-banking calls the image makes for a native module (R-09-125): the binding in force, gated by the distribution's module, the
/// accounts and branches it keys, the balance enquiry and the posting with its reply classified.
/// </summary>
public static class CoreBankingCalls
{
    /// <summary>
    /// The binding in force, gated (R-09-264, R-09-279): the gate a module of the distribution contributed on <c>p.port.core-banking.v1</c>
    /// (the platform module's posting window) is asked before core banking is called. With no gate contributed, core banking is not called.
    /// </summary>
    public static (ICoreBanking Cb, PortBinding Binding) Bound(CoreBankingResolver resolver, NativeAttempt a, Func<string, object?>? contributions = null)
    {
        var (cb, b) = resolver.For(a.Tenant, a.Now);
        if (contributions?.Invoke(NativeContributions.BindingGate(CoreBankingResolver.Port)) is not NativeBindingGate gate)
            throw new NativeFailure("PortNotBound", $"No module of the distribution gates {b.Port} for {b.Tenant} (the platform module contributes the gate): core banking is not called ungated", false);
        gate(a, NativeBoundPorts.ToNative(b));
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

    /// <summary>A balance enquiry (R-09-277): what core banking says is available on the account, never a posting.</summary>
    public static NativeBalance Balance(ICoreBanking cb, string branch, string account)
    {
        try
        {
            var bal = cb.Balance(branch, account).GetAwaiter().GetResult();
            return new NativeBalance(bal.Available, bal.Currency, bal.Status);
        }
        catch (PortBindingException e) { throw new NativeFailure("BusinessReject", e.Message, false); }
        catch (TransactHub.Security.SecretUnavailable e) { throw new NativeFailure("SecretUnavailable", e.Message, false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { throw new NativeFailure("TransientError", $"Balance enquiry failed: {e.GetType().Name}", true); }
    }

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
/// contributed (contract 2.0.0, R-09-264), with its balance enquiry (contract 2.1.0, R-09-277).
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
        public NativeBalance Balance(string branch, string account) => CoreBankingCalls.Balance(cb, branch, account);

        public NativePosted Post(NativePostingBatch batch, string primary)
        {
            var (outcome, reason, fact) = CoreBankingCalls.Post(cb, b, new PostingBatch(batch.Reference, batch.ValueDate,
                batch.Legs.Select(l => new PostingLeg(l.Kind, l.Branch, l.Account, l.Amount, l.Currency, l.TxnCode, l.Narration)).ToList()), primary);
            return new NativePosted(outcome, reason, fact);
        }
    }
}
