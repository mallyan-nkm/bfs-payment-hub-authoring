using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Native;
using TransactHub.Activities.Wps.Sif;
using Xunit;

namespace TransactHub.Store.Tests;

// The native code-behind the runtime's native host serves (R-08-14): the source's writer and CBX checks
// on the studio's canonical context.
public sealed class NativeActivityTests
{
    static JsonObject View() => (JsonObject)JsonNode.Parse("""
    {
      "header": { "fileId": "0000001234567261117083000" },
      "batch": { "currency": "AED", "debtorAgent": { "clearingMemberId": "302620122" } },
      "items": [ { "payment": { "creditorAgent": { "clearingMemberId": "203320101" }, "creditorAccount": { "iban": "AE070331234567890123456" } },
                   "ext": { "uae": { "wps": { "personId": "00001234567890", "payStartDate": "2026-10-01", "payEndDate": "2026-10-31", "daysInPeriod": 31, "fixedPay": 1200, "variablePay": 300, "leaveDays": 0 } } } } ],
      "ext": { "uae": { "wps": { "employerId": "0000001234567", "salaryMonth": "102026", "employerReference": "REF-0001" } } }
    }
    """)!;
    static readonly DateTimeOffset Now = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_writer_renders_the_central_bank_file_and_keeps_it()
    {
        var kept = new Dictionary<string, (string, byte[])>();
        var r = new RenderCbSif((sha, name, content) => kept[sha] = (name, content)).Run(new("uae.wps.map.cb-sif-out.v1", "1.0.0", View(), new JsonObject { ["bankRoutingCode"] = "302620122" }, Now));
        Assert.Equal("mapped", r.Outcome);
        var rendered = r.Writes["messages.rendered"]!;
        Assert.Equal("0000001234567261117083000.SIF", (string)rendered["fileName"]!);
        var (name, content) = kept[(string)rendered["sha256"]!];
        Assert.Equal("EDR,00001234567890,203320101,AE070331234567890123456,2026-10-01,2026-10-31,31,1200.00,300.00,0\r\nSCR,0000001234567,302620122,2026-11-17,1000,102026,1,1500.00,AED,REF-0001\r\n", CbxSifWriter.Encode(System.Text.Encoding.UTF8.GetString(content)) is var b ? System.Text.Encoding.UTF8.GetString(b) : "");
        Assert.Equal(1500m, (decimal)rendered["total"]!);
    }

    [Fact]
    public void Without_the_bank_s_routing_code_the_writer_refuses_the_file()
    {
        var r = new RenderCbSif().Run(new("uae.wps.map.cb-sif-out.v1", "1.0.0", View(), new JsonObject(), Now));
        Assert.Equal("unmappable", r.Outcome);
        Assert.Contains("BankRoutingCode is missing", (string)r.Writes["messages.rendered"]!["error"]!);
    }

    [Fact]
    public void The_cbx_checks_pass_this_bank_s_file_and_name_what_is_wrong_with_another()
    {
        var p = new JsonObject { ["CbxMolIdLength"] = "13", ["CbxFileDateMaxYearsBack"] = "1", ["CbxScrDebitAccountRequired"] = "true", ["CbxScrDebitAccountRegisteredCheck"] = "true", ["CbxAccountMatchLength"] = "15", ["BankRoutingCode"] = "302620122" };
        Assert.Equal("pass", new ValidateCbxFile().Run(new("uae.wps.validate.cbx-file.v1", "1.0.0", View(), p, Now)).Outcome);
        var other = View();
        other["batch"]!["debtorAgent"]!["clearingMemberId"] = "302620999";
        other["items"]![0]!["payment"]!["creditorAccount"]!["iban"] = "AE070321234567890123456";
        var r = new ValidateCbxFile().Run(new("uae.wps.validate.cbx-file.v1", "1.0.0", other, p, Now));
        Assert.Equal("reject", r.Outcome);
        var messages = string.Join("\n", r.Writes["facts.validation"]!["errors"]!.AsArray().Select(e => (string)e!["message"]!));
        Assert.Contains("Invalid Employee Account No with AgentRouting code in Line No :1", messages);
        Assert.Contains("Bank Code of the Employer 302620999 is invalid", messages);
        p.Remove("CbxMolIdLength");
        Assert.Equal("CONFIG", (string)new ValidateCbxFile().Run(new("uae.wps.validate.cbx-file.v1", "1.0.0", View(), p, Now)).Writes["facts.validation"]!["errors"]![0]!["code"]!);
    }
}
