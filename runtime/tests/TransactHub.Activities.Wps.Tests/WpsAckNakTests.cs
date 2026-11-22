using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Native;
using TransactHub.Activities.Wps.Rules;

namespace TransactHub.Activities.Wps.Tests;

// R-02-19 (Issue I-100): a NAK file's per-line errors (DER records) are read into status.transactions, pain.002 TxInfAndSts,
// one per line: rejected, the ISO reason NARR, the scheme's error number beside it and the scheme's description. The file
// names are invented; no value is a person's.
public sealed class WpsAckNakTests
{
    static NativeResult Read(string payload) => new MapAckIn().Run(new NativeAttempt("uae.wps.map.ack-in.v1", "1.2.0",
        new JsonObject { ["messages"] = new JsonObject { ["lastReceived"] = new JsonObject { ["payload"] = payload, ["fileName"] = "0000001234567261117083000N26000000001.NAK" } } },
        new JsonObject(), DateTimeOffset.Parse("2026-11-24T08:00:00Z"), "example-bank"));

    [Fact]
    public void A_NAK_lists_each_rejected_line_with_its_scheme_code_and_description()
    {
        var nak = WpsAckNakFile.Build("0000001234567261117083000.SIF", false,
            [new WpsAckDetailError(2, "E012", "Invalid IBAN"), new WpsAckDetailError(3, "E031", "Pay period out of range")]);
        var r = Read(nak);
        Assert.Equal("nak", r.Outcome);
        Assert.Equal("E012", r.ReasonCode);
        var status = (JsonObject)r.Writes["status"]!;
        Assert.Equal("RJCT", (string?)status["code"]);
        Assert.Equal("E012", (string?)status["schemeCode"]);
        var lines = ((JsonArray)status["transactions"]!).OfType<JsonObject>().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal([2, 3], lines.Select(l => (int)l["lineNo"]!));
        Assert.All(lines, l => Assert.Equal("RJCT", (string?)l["code"]));
        Assert.All(lines, l => Assert.Equal("NARR", (string?)l["reasonCode"]));
        Assert.Equal(["E012", "E031"], lines.Select(l => (string?)l["schemeCode"]));
        Assert.Equal("Pay period out of range", (string?)lines[1]["additionalInformation"]);
    }

    [Fact]
    public void An_ACK_and_a_status_without_a_file_name_no_line()
    {
        var ack = Read(WpsAckNakFile.Build("0000001234567261117083000.SIF", true));
        Assert.Equal("ack", ack.Outcome);
        Assert.Null(ack.Writes["status"]!["transactions"]);
        var status = new MapAckIn().Run(new NativeAttempt("uae.wps.map.ack-in.v1", "1.2.0",
            new JsonObject { ["messages"] = new JsonObject { ["lastReceived"] = new JsonObject { ["status"] = "rejected", ["reasonCode"] = "AM10" } } },
            new JsonObject(), DateTimeOffset.Parse("2026-11-24T08:00:00Z"), "example-bank"));
        Assert.Equal("nak", status.Outcome);
        Assert.Null(status.Writes["status"]!["transactions"]);
    }
}
