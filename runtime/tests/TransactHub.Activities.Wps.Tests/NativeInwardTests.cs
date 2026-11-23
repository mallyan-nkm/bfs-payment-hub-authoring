using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Inward;
using TransactHub.Activities.Wps.Inward.Dif;
using TransactHub.Activities.Wps.Native;
using TransactHub.Activities.Wps.Rules;

namespace TransactHub.Activities.Wps.Tests;

/// <summary>
/// The WPS inward native activities on the studio's canonical context (plan P2, R-08-16): the payment file mapped in by the source's
/// parser, its receipt confirmation, disbursement and returns files rendered by the source's builders and read back by them, an
/// acknowledgement read by the source's reader, the supplementary file, the refund request instruction, the factory's return file
/// and the posting window. Every value is synthetic: the factory's sample files (studio/fixtures/samples).
/// </summary>
public class NativeInwardTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 18, 6, 30, 0, TimeSpan.Zero); // 10:30 business time
    static readonly string Samples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../studio/fixtures/samples"));

    static JsonObject Inbound(string text) => new() { ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["payload"] = text } } };

    static NativeResult Run(INativeActivity a, JsonObject view, JsonObject? p = null) => a.Run(new NativeAttempt(a.Name, null, view, p ?? new JsonObject(), Now, "example-bank"));

    /// <summary>The context after the intake map, as the leg sees it: the writes merged in.</summary>
    static JsonObject Mapped()
    {
        var r = Run(new MapPifIn(), Inbound(File.ReadAllText(Path.Combine(Samples, "wps-pif-ssm.pif"))));
        r.Outcome.Should().Be("mapped");
        var ctx = new JsonObject { ["header"] = new JsonObject { ["fileId"] = r.Writes["header.fileId"]!.DeepClone(), ["endToEndId"] = r.Writes["header.endToEndId"]!.DeepClone() }, ["batch"] = r.Writes["batch"]!.DeepClone(), ["items"] = r.Writes["items[]"]!.DeepClone() };
        ctx["ext"] = new JsonObject { ["uae"] = new JsonObject { ["wps"] = r.Writes["ext.uae.wps"]!.DeepClone() } };
        ctx["header"]!["paymentId"] = "PAY-1";
        return ctx;
    }

    [Fact]
    public void The_payment_file_in_the_source_layout_maps_to_the_canonical_batch()
    {
        var ctx = Mapped();
        ctx["header"]!["fileId"]!.ToString().Should().Be("060000000001", "the last 12 characters of the file name's base");
        ctx["batch"]!["numberOfTransactions"]!.GetValue<int>().Should().Be(2);
        ctx["batch"]!["controlSum"]!.GetValue<decimal>().Should().Be(3500.00m);
        ctx["batch"]!["creditorAgent"]!["clearingMemberId"]!.ToString().Should().Be("302620122");
        var items = (JsonArray)ctx["items"]!;
        items[0]!["payment"]!["instructedAmount"]!["value"]!.GetValue<decimal>().Should().Be(1500.00m);
        items[0]!["ext"]!["uae"]!["wps"]!["hasVariablePay"]!.GetValue<bool>().Should().BeTrue("an EVP names the employee");
        items[1]!["ext"]!["uae"]!["wps"]!["hasVariablePay"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void A_payment_file_whose_total_does_not_reconcile_is_unmappable_and_the_factory_layout_is_not_the_source_one()
    {
        var bad = File.ReadAllText(Path.Combine(Samples, "wps-pif-ssm.pif")).Replace(",2,3500.00,AED,", ",2,3500.01,AED,");
        Run(new MapPifIn(), Inbound(bad)).Outcome.Should().Be("unmappable");
        Run(new MapPifIn(), Inbound(File.ReadAllText(Path.Combine(Samples, "wps-pif-valid.pif")))).Outcome.Should().Be("unmappable",
            "the native code-behind is the source's parser, which reads EDR, EVP and PCR only (Issue I-84)");
    }

    [Fact]
    public void The_receipt_confirmation_and_the_disbursement_file_are_the_source_builders_files()
    {
        var kept = new Dictionary<string, (string Name, byte[] Bytes)>();
        var ctx = Mapped();
        var prc = Run(new RenderPrc((sha, n, b) => kept[sha] = (n, b)), ctx, new JsonObject { ["agentId"] = "026" });
        prc.Outcome.Should().Be("mapped");
        var prcName = prc.Writes["messages.prc"]!["fileName"]!.ToString();
        prcName.Should().StartWith("026");
        var prcText = Encoding.ASCII.GetString(kept[prc.Writes["messages.prc"]!["sha256"]!.ToString()].Bytes);
        prcText.Should().Be("RDR,302620122261118060000000001.PIF,3200.00,300.00,3500.00,2, , , ,EWPMS\r\n" +
                            "RCR, ,3200.00,300.00,3500.00,1,2026-11-18,1030, ,EWPMS\r\n");
        // The first credit posted, the second refused: the DIF carries the first only.
        var items = (JsonArray)ctx["items"]!;
        items[0]!["facts"] = new JsonObject { ["posting"] = new JsonObject { ["outcome"] = "posted" } };
        items[1]!["facts"] = new JsonObject { ["posting"] = new JsonObject { ["outcome"] = "rejected", ["reasonCode"] = "AC04" } };
        ctx["messages"] = new JsonObject { ["prc"] = prc.Writes["messages.prc"]!.DeepClone() };
        var dif = Run(new RenderDif((sha, n, b) => kept[sha] = (n, b)), ctx, new JsonObject { ["agentId"] = "026" });
        dif.Outcome.Should().Be("mapped");
        dif.Writes["messages.dif"]!["fileName"]!.ToString().Should().Be(prcName[..prcName.LastIndexOf('.')] + ".DIF");
        dif.Writes["messages.dif"]!["lines"]!.GetValue<int>().Should().Be(1);
        dif.Writes["messages.dif"]!["total"]!.GetValue<decimal>().Should().Be(1500.00m);
        var difText = Encoding.ASCII.GetString(kept[dif.Writes["messages.dif"]!["sha256"]!.ToString()].Bytes);
        difText.Should().StartWith("DIR,060000000001,00001234567890,AE570330000000000000002,2026-11-18,1500.00,0.00,,,1\r\nDCR,026,2026-11-18,1030,1,1500.00,1500.00,0.00,EWPMS,EWPMS\r\n");
    }

    [Fact]
    public void The_returns_file_carries_the_refused_credits_at_the_returns_decision_s_amount()
    {
        var kept = new Dictionary<string, byte[]>();
        var ctx = Mapped();
        var items = (JsonArray)ctx["items"]!;
        items[0]!["facts"] = new JsonObject { ["posting"] = new JsonObject { ["outcome"] = "rejected", ["reasonCode"] = "AC04" } };
        items[1]!["facts"] = new JsonObject { ["posting"] = new JsonObject { ["outcome"] = "unknown" }, ["postingVerified"] = new JsonObject { ["outcome"] = "verified" } };
        ctx["facts"] = new JsonObject { ["returns"] = new JsonObject { ["returns"] = "returns", ["count"] = 1, ["amount"] = 1500.00m } };
        var rfa = Run(new RenderRfa((sha, _, b) => kept[sha] = b), ctx, new JsonObject { ["headRoutingCode"] = "302620122" });
        rfa.Outcome.Should().Be("mapped");
        rfa.Writes["messages.rfa"]!["fileName"]!.ToString().Should().Be("026261118103000.RFA");
        Encoding.ASCII.GetString(kept[rfa.Writes["messages.rfa"]!["sha256"]!.ToString()]).Should().StartWith("ARI,060000000001,0000001234567,00001234567890,1500.00,AC04,");
        // A decision that disagrees with the file is refused, never sent.
        ctx["facts"]!["returns"]!["amount"] = 1500.01m;
        Run(new RenderRfa(), ctx, new JsonObject { ["headRoutingCode"] = "302620122" }).Outcome.Should().Be("unmappable");
    }

    [Fact]
    public void An_acknowledgement_file_is_read_by_the_source_s_reader_and_a_simulator_s_status_by_itself()
    {
        var ack = WpsAckNakFile.Build("026261118103000.RFA", accepted: true);
        Run(new MapAckIn(), Inbound(ack)).Outcome.Should().Be("ack");
        var nak = WpsAckNakFile.Build("026261118103000.RFA", accepted: false, [new WpsAckDetailError(2, "E104", "Synthetic error")]);
        var r = Run(new MapAckIn(), Inbound(nak));
        r.Outcome.Should().Be("nak");
        r.ReasonCode.Should().Be("E104");
        Run(new MapAckIn(), new JsonObject { ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["status"] = "rejected", ["reasonCode"] = "FF01" } } }).Outcome.Should().Be("nak");
        Run(new MapAckIn(), new JsonObject { ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["status"] = "accepted" } } }).Outcome.Should().Be("ack");
    }

    [Fact]
    public void The_supplementary_file_pairs_with_its_payment_file_and_keeps_no_name()
    {
        const string psi = "# file: 041041DOM784000000000001060000000001.PSI\n" +
                           "ESI,DOM205590910784000000000002,EMPLOYEE ONE,90000001, , , , ,EWPMS\n" +
                           "CSI,DOM784000000000001,EMPLOYER ONE, , , , , ,EWPMS\n";
        var view = Inbound(psi);
        view["header"] = new JsonObject { ["fileId"] = "060000000001" };
        var r = Run(new MapPsiIn(), view);
        r.Outcome.Should().Be("mapped");
        r.Writes["ext.uae.wps.supplement"]!["esiCount"]!.GetValue<int>().Should().Be(1);
        r.Writes.ToJsonString().Should().NotContain("EMPLOYEE ONE", "names are personal data and stay in the file");
        view["header"]!["fileId"] = "060000000002";
        Run(new MapPsiIn(), view).Outcome.Should().Be("unmatched");
    }

    [Fact]
    public void The_refund_request_instruction_maps_each_line_to_an_item()
    {
        const string rri = "# file: 0270270000000009899609000042725.RRI\n" +
                           "DRR,0000000009899,90000000000003,100.00,005,12334567890987654322121212121222.PAF,109000005248609000042662,AE000190000000000000005, ,EWPMS\n" +
                           "DRR,0000000009899,90000000000001,500.00,003,12334567890987654322121212121222.PAF,109000005248609000042662,AE000190000000000000004, ,EWPMS\n" +
                           "DRC,,12345678901,600.00,2, , , , ,EWPMS\n";
        var r = Run(new MapRriIn(), Inbound(rri), new JsonObject { ["fileNameAgentCode"] = "027" });
        r.Outcome.Should().Be("mapped");
        ((JsonArray)r.Writes["items[]"]!).Count.Should().Be(2);
        r.Writes["batch"]!["controlSum"]!.GetValue<decimal>().Should().Be(600.00m);
        Run(new MapRriIn(), Inbound(rri), new JsonObject { ["fileNameAgentCode"] = "407" }).Outcome.Should().Be("unmappable", "the file is for another agent");
    }

    [Fact]
    public void The_return_file_names_the_employer_s_account_at_payment_level()
    {
        var r = Run(new MapRtcIn(), Inbound(File.ReadAllText(Path.Combine(Samples, "wps-rtc-valid.rtc"))));
        r.Outcome.Should().Be("mapped");
        r.Writes["payment"]!["creditorAccount"]!["iban"]!.ToString().Should().Be("AE570330000000000000099");
        r.Writes["payment"]!["instructedAmount"]!["value"]!.GetValue<decimal>().Should().Be(3500.00m);
        r.Writes["batch"]!["creditorAgent"]!["clearingMemberId"]!.ToString().Should().Be("302620122");
    }

    [Theory]
    [InlineData("08:00", "14:00", "open")]   // 10:30 business time
    [InlineData("15:00", "18:00", "closed")]
    [InlineData("22:00", "11:00", "open")]   // overnight, its early part
    [InlineData("bad", "14:00", "closed")]   // an invalid window fails closed
    public void The_posting_window_is_the_source_s_call_hours_gate(string start, string end, string expected)
    {
        // The platform module's since R-09-126: the posting window is a platform activity, carried as the platform's module.
        Run(new TransactHub.Activities.Platform.Native.DecidePostingWindow(), new JsonObject(), new JsonObject { ["windowStart"] = start, ["windowEnd"] = end }).Outcome.Should().Be(expected);
    }
}
