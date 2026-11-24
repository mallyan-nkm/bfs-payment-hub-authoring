using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Wps.Native;

/// <summary>
/// The scheme's other sends on the WPS port. Their v1 lines (1.1.0, Issue I-268, R-09-213) send the file their render step kept
/// (<c>messages.rendered</c>, by its SHA-256) on the bank's binding of <c>uae.wps.port.wps.v1</c>, the File API's outbox where the bank
/// binds the port to adapter <c>file-transfer</c>; they stay for the distributions that pin them. Their v2 lines (2.0.0, R-09-232,
/// Issue I-293) read the encryption step's fact too and send the encrypted file where the bank's binding encrypted it, as the salary
/// file's v2 does (R-09-182). Every send names the scheme message it sends (the message whose <c>disposition.sentBy</c> names it,
/// R-09-234, Issue I-310), so the File API keeps the file as that message and puts it in the message's folder where the bank's
/// binding names one (R-09-235, Issue I-313). The file is taken: <c>accepted</c>. No kept file, or a request the port refuses, is
/// <c>rejected</c>; a binding that cannot be reached now is the port's retryable error.
/// </summary>
public sealed class SendKeptFile(INativePorts ports, string name) : INativeActivity
{
    /// <summary>Each send of the port: what it sends, for its refusal when nothing was rendered, and the scheme message it is.</summary>
    public static readonly IReadOnlyDictionary<string, (string What, string Message)> Sends = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["uae.wps.exchange.send-receipt-confirmation.v1"] = ("receipt confirmation", "uae.wps.prc"),
        ["uae.wps.exchange.send-disbursement-file.v1"] = ("disbursement file", "uae.wps.dif"),
        ["uae.wps.exchange.send-returns-file.v1"] = ("returns file", "uae.wps.rfa"),
        ["uae.wps.exchange.send-disbursement-cancellation.v1"] = ("disbursement cancellation request", "uae.wps.dcr"),
        ["uae.wps.exchange.send-refund-request.v1"] = ("refund request", "uae.wps.rfr"),
        ["uae.wps.exchange.send-refund-response.v1"] = ("refund request response", "uae.wps.rrr"),
        ["uae.wps.exchange.send-receipt-confirmation.v2"] = ("receipt confirmation", "uae.wps.prc"),
        ["uae.wps.exchange.send-disbursement-file.v2"] = ("disbursement file", "uae.wps.dif"),
        ["uae.wps.exchange.send-returns-file.v2"] = ("returns file", "uae.wps.rfa"),
        ["uae.wps.exchange.send-disbursement-cancellation.v2"] = ("disbursement cancellation request", "uae.wps.dcr"),
        ["uae.wps.exchange.send-refund-request.v2"] = ("refund request", "uae.wps.rfr"),
        ["uae.wps.exchange.send-refund-response.v2"] = ("refund request response", "uae.wps.rrr"),
    };

    public static IEnumerable<INativeActivity> All(INativePorts ports) => Sends.Keys.Select(n => (INativeActivity)new SendKeptFile(ports, n));

    public string Name => name;

    public NativeResult Run(NativeAttempt a)
    {
        var (what, message) = Sends.TryGetValue(name, out var s) ? s : ("file", "");
        return SchemeSend.Run(ports, a, name, what, message, readsEncryption: name.EndsWith(".v2", StringComparison.Ordinal));
    }
}

/// <summary>One send of a file the host kept on the WPS port, shared by every send of the scheme (R-09-182, R-09-232).</summary>
public static class SchemeSend
{
    static NativeResult Rejected(string code, string why) =>
        new("rejected", code, new JsonObject { ["status"] = new JsonObject { ["code"] = "RJCT", ["reasonCode"] = code, ["additionalInformation"] = why } });

    /// <summary>
    /// Sends the file the render kept, or, where <paramref name="readsEncryption"/> and the encryption step's fact names the file it
    /// encrypted, that file: an encryption the service refused, or of another file than the one rendered, sends nothing.
    /// <paramref name="message"/> is the scheme message the file is (the File API's kind of it, R-09-234).
    /// </summary>
    public static NativeResult Run(INativePorts ports, NativeAttempt a, string name, string what, string message, bool readsEncryption)
    {
        var rendered = NativeContext.Node(a.View, "messages.rendered");
        var sha = NativeContext.S(rendered, "sha256");
        var fileName = NativeContext.S(rendered, "fileName");
        if (sha.Length == 0 || fileName.Length == 0) return Rejected("FF01", $"No {what} was rendered to send");
        var encrypted = false;
        if (readsEncryption && NativeContext.Node(a.View, "facts.encryption") is JsonObject enc)
        {
            var outcome = NativeContext.S(enc, "outcome");
            if (outcome == "refused") return Rejected("NARR", "The encryption service refused the file: it is not sent");
            var encSha = NativeContext.S(enc, "sha256");
            if (outcome == "encrypted" && encSha.Length > 0)
            {
                // The plain file the encryption read must be the one rendered: an encryption of another file is not this file's.
                if (NativeContext.S(enc, "plainSha256") is { Length: > 0 } plain && !string.Equals(plain, sha, StringComparison.OrdinalIgnoreCase))
                    return Rejected("NARR", $"The encrypted file is not the rendered {what}'s: nothing is sent");
                sha = encSha;
                fileName = NativeContext.S(enc, "fileName") is { Length: > 0 } n ? n : fileName + ".gpg";
                encrypted = true;
            }
        }
        NativePortAnswer answer;
        try
        {
            var request = new JsonObject
            {
                ["sha256"] = sha, ["fileName"] = fileName, ["paymentId"] = Ctx.S(a.View, "header.paymentId"), ["scheme"] = "uae.wps", ["leg"] = "scheme-leg",
            };
            if (message.Length > 0) request["message"] = message;
            answer = ports.Ask(a, SendSalaryFile.Port, SendSalaryFile.Operation, request);
        }
        catch (NativePortRefused e) { return Rejected("NARR", e.Message); }
        var sent = new JsonObject
        {
            ["by"] = name, ["at"] = a.Now.ToString("O", CultureInfo.InvariantCulture), ["fileName"] = fileName, ["sha256"] = sha, ["via"] = answer.Adapter,
            ["folder"] = answer.Body["folder"]?.DeepClone(),
        };
        if (readsEncryption) sent["encrypted"] = encrypted;
        if (message.Length > 0) sent["message"] = message;
        return new NativeResult("accepted", null, new JsonObject { ["status"] = new JsonObject { ["code"] = "ACCP" }, ["messages.sent"] = sent });
    }
}
