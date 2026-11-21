using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Wps.Rules;
using TransactHub.Activities.Wps.Sif;

namespace TransactHub.Activities.Wps.Native;

/// <summary>One attempt of a native activity: the activity's view of the context, its parameter values and the engine's time.</summary>
public sealed record NativeAttempt(string Activity, string? Release, JsonObject View, JsonObject Params, DateTimeOffset Now);

/// <summary>What a native activity answers: an outcome of its contract, an ISO reason code, and its writes (path to value).</summary>
public sealed record NativeResult(string Outcome, string? ReasonCode, JsonObject Writes);

/// <summary>A native code-behind (spec Part 3, binding <c>native</c>): the platform's own code for a hot-path or regulated block.</summary>
public interface INativeActivity
{
    /// <summary>The activity line it implements, e.g. <c>uae.wps.validate.cbx-file.v1</c>.</summary>
    string Name { get; }
    NativeResult Run(NativeAttempt a);
}

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
public sealed class RenderCbSif(Action<string, string, byte[]>? keep = null) : INativeActivity
{
    public string Name => "uae.wps.map.cb-sif-out.v1";

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
        var routing = Ctx.Param(a.Params, "bankRoutingCode") is { Length: > 0 } r ? r : Ctx.Param(a.Params, CbxScrRules.BankRoutingCodeParam);
        var control = new CbxSifControl(Ctx.S(a.View, "ext.uae.wps.employerId"), routing, Ctx.Business(a.Now),
            Ctx.S(a.View, "ext.uae.wps.salaryMonth"), Ctx.S(a.View, "batch.currency") is { Length: > 0 } c ? c : "AED", Ctx.S(a.View, "ext.uae.wps.employerReference"));
        var (doc, failure) = CbxSifWriter.Build(employees, [], control);
        if (doc is null)
            return new NativeResult("unmappable", "FF01", new JsonObject { ["messages.rendered"] = new JsonObject { ["by"] = Name, ["error"] = $"{failure!.ErrorCode}: {failure.Message}" } });
        var fileName = CbxSifWriter.FileNameFor(Ctx.S(a.View, "header.fileId") + SifFiles.SifSuffix);
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
