using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Wps.Native;

/// <summary>
/// The scheme's other sends on the WPS port (Issue I-268, R-09-213), each at 1.1.0 as the salary file's send is at 1.1.0: the file its
/// render step kept (<c>messages.rendered</c>, by its SHA-256) is sent on the bank's binding of <c>uae.wps.port.wps.v1</c>, the File
/// API's outbox where the bank binds the port to adapter <c>file-transfer</c>. The file is taken: <c>accepted</c>. No kept file, or a
/// request the port refuses, is <c>rejected</c>; a binding that cannot be reached now is the port's retryable error.
/// </summary>
public sealed class SendKeptFile(INativePorts ports, string name) : INativeActivity
{
    /// <summary>Each send of the port and what it sends, for its refusal when nothing was rendered.</summary>
    public static readonly IReadOnlyDictionary<string, string> Sends = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["uae.wps.exchange.send-receipt-confirmation.v1"] = "receipt confirmation",
        ["uae.wps.exchange.send-disbursement-file.v1"] = "disbursement file",
        ["uae.wps.exchange.send-returns-file.v1"] = "returns file",
        ["uae.wps.exchange.send-disbursement-cancellation.v1"] = "disbursement cancellation request",
        ["uae.wps.exchange.send-refund-request.v1"] = "refund request",
        ["uae.wps.exchange.send-refund-response.v1"] = "refund request response",
    };

    public static IEnumerable<INativeActivity> All(INativePorts ports) => Sends.Keys.Select(n => (INativeActivity)new SendKeptFile(ports, n));

    public string Name => name;

    static NativeResult Rejected(string code, string why) =>
        new("rejected", code, new JsonObject { ["status"] = new JsonObject { ["code"] = "RJCT", ["reasonCode"] = code, ["additionalInformation"] = why } });

    public NativeResult Run(NativeAttempt a)
    {
        var rendered = NativeContext.Node(a.View, "messages.rendered");
        var sha = NativeContext.S(rendered, "sha256");
        var fileName = NativeContext.S(rendered, "fileName");
        if (sha.Length == 0 || fileName.Length == 0) return Rejected("FF01", $"No {Sends.GetValueOrDefault(name, "file")} was rendered to send");
        NativePortAnswer answer;
        try
        {
            answer = ports.Ask(a, SendSalaryFile.Port, SendSalaryFile.Operation, new JsonObject
            {
                ["sha256"] = sha, ["fileName"] = fileName, ["paymentId"] = Ctx.S(a.View, "header.paymentId"), ["scheme"] = "uae.wps", ["leg"] = "scheme-leg",
            });
        }
        catch (NativePortRefused e) { return Rejected("NARR", e.Message); }
        return new NativeResult("accepted", null, new JsonObject
        {
            ["status"] = new JsonObject { ["code"] = "ACCP" },
            ["messages.sent"] = new JsonObject
            {
                ["by"] = Name, ["at"] = a.Now.ToString("O", CultureInfo.InvariantCulture), ["fileName"] = fileName, ["sha256"] = sha, ["via"] = answer.Adapter,
                ["folder"] = answer.Body["folder"]?.DeepClone(),
            },
        });
    }
}
