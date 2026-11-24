using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Wps.Rules;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Sif;

namespace TransactHub.Activities.Wps.Native;

/// <summary>Reading the studio's canonical context the way every WPS native activity needs it.</summary>
static class Ctx
{
    public static string S(JsonNode? n, string path)
    {
        JsonNode? cur = n;
        foreach (var p in path.Split('.')) cur = cur is JsonObject o ? o[p] : null;
        return cur?.ToString() ?? "";
    }

    public static decimal D(JsonNode? n, string path) =>
        decimal.TryParse(S(n, path), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    public static JsonArray Items(JsonObject view) => view["items"] as JsonArray ?? new JsonArray();

    /// <summary>The UAE business time of an engine time (UTC+4, no daylight saving).</summary>
    public static DateTime Business(DateTimeOffset now) => now.ToUniversalTime().AddHours(4).DateTime;

    public static string Param(JsonObject p, string name) => p[name]?.ToString() ?? "";
}

/// <summary>
/// The CBX salary-file checks of the TransactHub source (CbxFileNameRules, CbxRecordRules, CbxScrRules), run on the
/// canonical context: the file name (the file id with its suffix) and its timestamp, every employee record, a
/// duplicate agent and account, and the control record's bank routing code. The codes and messages are the
/// source's. The bank's values are the activity's parameters, set in the bank's configuration.
/// </summary>
public sealed class ValidateCbxFile : INativeActivity
{
    public string Name => "uae.wps.validate.cbx-file.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var errors = new JsonArray();
        void error(string code, string message) => errors.Add(new JsonObject { ["code"] = code, ["message"] = message });

        var (settings, invalid) = CbxRuleSettings.FromParameters(CbxRuleSettings.ParamNames.All.ToDictionary(k => k, k => (string?)Ctx.Param(a.Params, k)));
        if (settings is null)
            return new NativeResult("reject", "NARR", new JsonObject { ["facts.validation"] = new JsonObject { ["rules"] = "cbx", ["errors"] = new JsonArray(new JsonObject { ["code"] = "CONFIG", ["message"] = $"The bank's value {invalid} is missing or out of range" }) } });

        // Rules 1–7: the file name and its timestamp.
        var fileName = Ctx.S(a.View, "header.fileId") + SifFiles.SifSuffix;
        var (name, failure) = CbxFileNameRules.Check(fileName, settings);
        if (failure is not null) error(failure.ErrorCode, failure.Message);
        else if (CbxFileNameRules.CheckFileNameDate(name!.DateComponent, Ctx.Business(a.Now), TimeSpan.Zero, settings) is { } dateFailure)
            error(dateFailure.ErrorCode, dateFailure.Message);

        // Rules 4/5, 14, 32, 36, 77 on each employee record, and rule 7 across them.
        var freezone = Ctx.Param(a.Params, "FreezoneId");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = Ctx.Items(a.View);
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var agent = Ctx.S(it, "payment.creditorAgent.clearingMemberId");
            var account = Ctx.S(it, "payment.creditorAccount.iban");
            var fields = new[]
            {
                SifRecordTypes.Edr, Ctx.S(it, "ext.uae.wps.personId"), agent, account,
                Ctx.S(it, "ext.uae.wps.payStartDate"), Ctx.S(it, "ext.uae.wps.payEndDate"), Ctx.S(it, "ext.uae.wps.daysInPeriod"),
                Ctx.S(it, "ext.uae.wps.fixedPay"), Ctx.S(it, "ext.uae.wps.variablePay"), Ctx.S(it, "ext.uae.wps.leaveDays"),
            };
            foreach (var m in CbxRecordRules.CheckEdr(fields, i + 1, freezone.Length == 0 ? null : freezone)) error("EDR", m);
            if (!seen.Add($"{agent}|{account}")) error("EDR", CbxRecordRules.DuplicateAgentAccountMessage(i + 1, account, agent));
        }

        // Rule 66: the control record names this bank.
        var scr = new[] { SifRecordTypes.Scr, Ctx.S(a.View, "ext.uae.wps.employerId"), Ctx.S(a.View, "batch.debtorAgent.clearingMemberId") };
        if (CbxScrRules.CheckBankRoutingCode(scr, Ctx.Param(a.Params, CbxScrRules.BankRoutingCodeParam), items.Count + 1) is { } scrFailure)
            error(scrFailure.ErrorCode, scrFailure.Message);

        var validation = new JsonObject { ["rules"] = "cbx", ["errors"] = errors };
        return errors.Count == 0
            ? new NativeResult("pass", null, new JsonObject { ["facts.validation"] = validation })
            : new NativeResult("reject", "NARR", new JsonObject { ["facts.validation"] = validation });
    }
}

/// <summary>
/// The Central Bank SIF of the TransactHub source (CbxSifWriter), written from the canonical context. The file is
/// kept by the host; the context keeps a reference: its name, hash and totals.
/// </summary>
public sealed class RenderCbSif(Action<string, string, byte[]>? keep = null, string name = RenderCbSif.CbName) : INativeActivity
{
    public const string CbName = "uae.wps.map.cb-sif-out.v1";
    /// <summary>The factory's render step of the outward salary file, whose 1.1.0 is this code-behind (R-09-162, Issue I-192).</summary>
    public const string FactoryName = "uae.wps.map.sif-out.v1";
    public string Name => name;

    public NativeResult Run(NativeAttempt a)
    {
        var employees = new List<CbxSifEmployee>();
        var items = Ctx.Items(a.View);
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            employees.Add(new CbxSifEmployee(i + 1, i + 1, Ctx.S(it, "ext.uae.wps.personId"), Ctx.S(it, "payment.creditorAgent.clearingMemberId"), Ctx.S(it, "payment.creditorAccount.iban"),
                Date(Ctx.S(it, "ext.uae.wps.payStartDate")), Date(Ctx.S(it, "ext.uae.wps.payEndDate")), (int)Ctx.D(it, "ext.uae.wps.daysInPeriod"),
                Ctx.D(it, "ext.uae.wps.fixedPay"), Ctx.D(it, "ext.uae.wps.variablePay"), Ctx.D(it, "ext.uae.wps.leaveDays")));
        }
        // The source's SCR carries the salary month as MMYYYY.
        // Studio parameters are camelCase (bankRoutingCode); the source's name is accepted too.
        // The factory's render has no parameter: the employer's bank is the batch's debtor agent, as the channel's file named it.
        var routing = Ctx.Param(a.Params, "bankRoutingCode") is { Length: > 0 } r ? r : Ctx.Param(a.Params, CbxScrRules.BankRoutingCodeParam) is { Length: > 0 } src ? src
            : name == FactoryName ? Ctx.S(a.View, "batch.debtorAgent.clearingMemberId") : "";
        var control = new CbxSifControl(Ctx.S(a.View, "ext.uae.wps.employerId"), routing, Ctx.Business(a.Now),
            Ctx.S(a.View, "ext.uae.wps.salaryMonth"), Ctx.S(a.View, "batch.currency") is { Length: > 0 } c ? c : "AED", Ctx.S(a.View, "ext.uae.wps.employerReference"));
        var (doc, failure) = CbxSifWriter.Build(employees, [], control);
        if (doc is null)
            return new NativeResult("unmappable", "FF01", new JsonObject { ["messages.rendered"] = new JsonObject { ["by"] = Name, ["error"] = $"{failure!.ErrorCode}: {failure.Message}" } });
        // The file's id: the header's where the step reads it, else the batch's (the factory's render reads batch, items and ext).
        var fileId = Ctx.S(a.View, "header.fileId") is { Length: > 0 } h ? h : Ctx.S(a.View, "batch.id");
        var fileName = CbxSifWriter.FileNameFor(fileId + SifFiles.SifSuffix);
        keep?.Invoke(doc.Sha256, fileName, CbxSifWriter.Encode(doc.Content));
        return new NativeResult("mapped", null, new JsonObject
        {
            ["messages.rendered"] = new JsonObject
            {
                ["by"] = Name, ["at"] = a.Now.ToString("O", CultureInfo.InvariantCulture), ["fileName"] = fileName, ["sha256"] = doc.Sha256,
                ["edr"] = doc.EdrCount, ["evp"] = doc.EvpCount, ["total"] = doc.TotalAmount,
            },
        });
    }

    static DateOnly Date(string s) => DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : default;
}

/// <summary>
/// <c>uae.wps.exchange.send-salary-file.v1</c> 1.1.0 on the runtime (R-09-162, Issue I-192): the salary file the render step kept
/// (<c>messages.rendered</c>, by its SHA-256) is sent on the bank's binding of the WPS port. A bank that binds the port to the File API
/// (adapter <c>file-transfer</c>) sends it through the File API's outbox to the Central Bank's folder, where it is kept, delivered,
/// audited and resent on an operator's action; the acknowledgement comes back on the File API's inbox and is awaited by the next step.
/// The file is taken: <c>accepted</c>. A request the port refuses is <c>rejected</c>; a binding that cannot be reached now is a
/// retryable <c>TransientError</c>.
/// <para>
/// <c>uae.wps.exchange.send-salary-file.v2</c> (2.0.0, R-09-182, Issue I-266) reads the encryption step's fact too: where the bank's
/// binding of <c>p.port.encryption.v1</c> encrypted the file (<c>facts.encryption</c> names the encrypted file's name and SHA-256, which
/// the host kept), that file is sent, never the plain one; an encryption that names a file the host does not hold refuses the send,
/// and nothing goes out. Where the encryption names no file (a test distribution's simulator), the rendered file is sent, as v1 does.
/// </para>
/// </summary>
public sealed class SendSalaryFile(INativePorts ports, string name = SendSalaryFile.V1) : INativeActivity
{
    public const string Port = "uae.wps.port.wps.v1";
    public const string Operation = "submit-file";
    public const string V1 = "uae.wps.exchange.send-salary-file.v1", V2 = "uae.wps.exchange.send-salary-file.v2";
    public string Name => name;

    static NativeResult Rejected(string code, string why) =>
        new("rejected", code, new JsonObject { ["status"] = new JsonObject { ["code"] = "RJCT", ["reasonCode"] = code, ["additionalInformation"] = why } });

    public NativeResult Run(NativeAttempt a)
    {
        var rendered = NativeContext.Node(a.View, "messages.rendered");
        var sha = NativeContext.S(rendered, "sha256");
        var fileName = NativeContext.S(rendered, "fileName");
        if (sha.Length == 0 || fileName.Length == 0) return Rejected("FF01", "No salary file was rendered to send");
        var encrypted = false;
        if (name == V2 && NativeContext.Node(a.View, "facts.encryption") is JsonObject enc)
        {
            var outcome = NativeContext.S(enc, "outcome");
            if (outcome == "refused") return Rejected("NARR", "The encryption service refused the file: it is not sent");
            var encSha = NativeContext.S(enc, "sha256");
            if (outcome == "encrypted" && encSha.Length > 0)
            {
                // The plain file the encryption read must be the one rendered: an encryption of another file is not this file's.
                if (NativeContext.S(enc, "plainSha256") is { Length: > 0 } plain && !string.Equals(plain, sha, StringComparison.OrdinalIgnoreCase))
                    return Rejected("NARR", "The encrypted file is not the rendered salary file's: nothing is sent");
                sha = encSha;
                fileName = NativeContext.S(enc, "fileName") is { Length: > 0 } n ? n : fileName + ".gpg";
                encrypted = true;
            }
        }
        NativePortAnswer answer;
        try
        {
            answer = ports.Ask(a, Port, Operation, new JsonObject
            {
                ["sha256"] = sha, ["fileName"] = fileName, ["paymentId"] = Ctx.S(a.View, "header.paymentId"), ["scheme"] = "uae.wps", ["leg"] = "scheme-leg",
            });
        }
        catch (NativePortRefused e)
        {
            return new NativeResult("rejected", "NARR", new JsonObject { ["status"] = new JsonObject { ["code"] = "RJCT", ["reasonCode"] = "NARR", ["additionalInformation"] = e.Message } });
        }
        return new NativeResult("accepted", null, new JsonObject
        {
            ["status"] = new JsonObject { ["code"] = "ACCP" },
            ["messages.sent"] = new JsonObject
            {
                ["by"] = Name, ["at"] = a.Now.ToString("O", CultureInfo.InvariantCulture), ["fileName"] = fileName, ["sha256"] = sha, ["via"] = answer.Adapter,
                ["folder"] = answer.Body["folder"]?.DeepClone(), ["encrypted"] = encrypted,
            },
        });
    }
}
