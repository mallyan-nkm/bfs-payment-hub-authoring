using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Posting;
using TransactHub.Activities.Wps.Native;
using TransactHub.Ports;
using Xunit;

namespace TransactHub.Store.Tests;

// WPS posting and balance on the runtime go through the bank's core-banking port (plan P2): the binding in force for the
// tenant at the engine's time (PortBindings.InForce) chooses the adapter. The example bank is on the simulator until
// 2026-12-01 and on the gateway from then; the bank folder's binding is the gateway. The gateway is reached through a
// recording handler, so the wire is checked without a network. Accounts and amounts are synthetic.

public sealed class CoreBankingPostingTests
{
    static string Banks()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content-configurations")) && Directory.Exists(Path.Combine(d.FullName, "runtime")))
                return Path.Combine(d.FullName, "content-configurations", "banks");
        throw new DirectoryNotFoundException("repository root");
    }

    static readonly DateTimeOffset OnSimulator = DateTimeOffset.Parse("2026-11-24T06:00:00Z");   // 10:00 in the UAE
    static readonly DateTimeOffset OnGateway = DateTimeOffset.Parse("2026-12-15T06:00:00Z");

    /// <summary>The accounting template's values as the studio sends them: the factory's defaults, synthetic accounts.</summary>
    static JsonObject Template(Action<JsonObject>? change = null)
    {
        var p = new JsonObject
        {
            ["template"] = "default", ["transactionCode"] = "PAY", ["reversalTransactionCode"] = "REV", ["chargeTransactionCode"] = "CHG", ["vatTransactionCode"] = "VAT",
            ["narration"] = "Payment", ["chargeNarration"] = "Bank charge", ["settlementAccount"] = "9990000000001", ["suspenseAccount"] = "9990000000002",
            ["chargeIncomeAccount"] = "9990000000003", ["vatAccount"] = "9990000000004", ["chargeSlabs"] = new JsonArray(), ["vatPercent"] = 5,
        };
        change?.Invoke(p);
        return p;
    }

    /// <summary>One credit of a payment file, as the item-level view gives it.</summary>
    static JsonObject Credit(string iban = "AE070331234567890123456", decimal amount = 1500m) => (JsonObject)JsonNode.Parse($$"""
    { "header": { "paymentId": "P-1", "direction": "inward", "businessDate": "2026-11-24" },
      "payment": { "creditorAccount": { "iban": "{{iban}}" }, "instructedAmount": { "value": {{amount}}, "currency": "AED" } } }
    """)!;

    /// <summary>An outward payment with its debtor account, as the bulk-level view gives it.</summary>
    static JsonObject Outward(decimal total = 3500m) => (JsonObject)JsonNode.Parse($$"""
    { "header": { "paymentId": "P-2", "direction": "outward", "businessDate": "2026-11-24" },
      "payment": { "debtorAccount": { "id": "0010000000001" }, "instructedAmount": { "value": {{total}}, "currency": "AED" } },
      "batch": { "controlSum": {{total}} } }
    """)!;

    // The bank folder's gateway credential, by reference (env:TH_DIB_CORE_BANKING_TOKEN): a synthetic value made for the run.
    static readonly string GatewayToken = "synthetic-" + Guid.NewGuid().ToString("N");
    static readonly TransactHub.Security.SecretResolver Secrets = TransactHub.Security.SecretResolver.FromEnvironment(k => k == "TH_DIB_CORE_BANKING_TOKEN" ? GatewayToken : null);
    static CoreBankingResolver Resolver(Recorder? rec = null) => new(CoreBankingResolver.ReadBanks(Banks()), _ => new HttpClient(rec ?? new Recorder()), Secrets);
    static NativeAttempt At(string activity, JsonObject view, JsonObject p, DateTimeOffset now, string? tenant = "example-bank", string? level = null) =>
        new(activity, "1.0.0", view, p, now, tenant, level);

    [Fact]
    public void A_payment_file_credit_posts_through_the_simulator_while_the_example_bank_is_bound_to_it()
    {
        var r = Resolver();
        var res = ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", Credit(), Template(), OnSimulator, level: "item"));
        Assert.Equal("posted", res.Outcome);
        var fact = res.Writes["facts.posting"]!;
        Assert.Equal("simulator", (string)fact["adapter"]!);
        Assert.StartsWith("SIM-", (string)fact["hostReference"]!);
        var batch = r.Built.Select(x => x.CoreBanking).OfType<SimulatedCoreBanking>().Single().Snapshot().Single();
        Assert.Equal("P-1:AE070331234567890123456", batch.Reference);
        Assert.Equal(new DateOnly(2026, 11, 24), batch.ValueDate);
        Assert.Collection(batch.Legs,
            d => Assert.Equal(("debit", "9990000000001", 1500m, "PAY"), (d.Kind, d.Account, d.Amount, d.TxnCode)),
            c => Assert.Equal(("credit", "AE070331234567890123456", 1500m, "PAY"), (c.Kind, c.Account, c.Amount, c.TxnCode)));
        // A retry is the same posting: the same reference, posted once.
        ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", Credit(), Template(), OnSimulator, level: "item"));
        Assert.Single(r.Built.Select(x => x.CoreBanking).OfType<SimulatedCoreBanking>().Single().Snapshot());
    }

    [Fact]
    public async Task From_the_switch_the_same_credit_goes_to_the_gateway_with_the_binding_s_settings_on_the_wire()
    {
        var rec = new Recorder();
        var res = ModuleGate.PostAccounting(Resolver(rec)).Run(At("p.post.accounting.v1", Credit(), Template(), OnGateway, level: "item"));
        Assert.Equal("posted", res.Outcome);
        Assert.Equal("gateway-rest", (string)res.Writes["facts.posting"]!["adapter"]!);
        Assert.Equal("H-1", (string)res.Writes["facts.posting"]!["hostReference"]!);
        var (req, body) = rec.Requests.Single();
        Assert.Equal("https://core-banking.example.invalid/api/v1/postings", req.RequestUri!.ToString());
        Assert.Equal("0332", req.Headers.GetValues("X-Function-Id").Single());
        var j = (JsonObject)JsonNode.Parse(body)!;
        Assert.Equal("EXP-1:AE070331234567890123456", (string?)j["reference"]);
        Assert.Equal("20261124", (string?)j["valueDate"]);
        var entries = j["entries"]!.AsArray();
        Assert.Equal(["debit", "credit"], entries.Select(e => (string)e!["kind"]!));
        Assert.All(entries, e => Assert.Equal("PAY", (string)e!["txnCode"]!));
        // The settlement account is the template's; its branch comes from the account number (the source's rule).
        Assert.Equal("999", (string)entries[0]!["branch"]!);
        // The IBAN carries no branch, so the binding's upload branch is used.
        Assert.Equal("001", (string)entries[1]!["branch"]!);
        await Task.CompletedTask;
    }

    [Fact]
    public void The_bank_folder_s_gateway_answers_the_balance_and_the_posting_for_its_own_tenant()
    {
        var rec = new Recorder { Available = 5000m };
        var r = Resolver(rec);
        var reserve = new ReserveFunds(r, ModuleGate.Contributions).Run(At("p.reserve.funds.v1", Outward(3500m), new JsonObject(), OnSimulator, tenant: "dib"));
        Assert.Equal("reserved", reserve.Outcome);
        var (balReq, balBody) = rec.Requests[0];
        Assert.Equal("https://core-banking.dib.invalid/api/v1/accounts/balance-enquiry", balReq.RequestUri!.ToString());
        Assert.Equal("0301", balReq.Headers.GetValues("X-Function-Id").Single());
        Assert.Equal("001", (string?)JsonNode.Parse(balBody)!["branch"]);

        var view = Outward(3500m);
        view["facts"] = new JsonObject { ["reservation"] = reserve.Writes["facts.reservation"]!.DeepClone() };
        var post = ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", view, Template(), OnSimulator, tenant: "dib"));
        Assert.Equal("posted", post.Outcome);
        var j = (JsonObject)JsonNode.Parse(rec.Requests[1].body)!;
        Assert.Equal("IBSSYSTEM", (string?)j["serviceCode"]);
        Assert.Equal("DIBP-2", (string?)j["reference"]);
        Assert.Equal(["debit", "credit"], j["entries"]!.AsArray().Select(e => (string)e!["kind"]!));
        Assert.Equal("0010000000001", (string)j["entries"]![0]!["account"]!);

        // Not enough available: the reservation is refused, with the balance it saw.
        var short_ = new ReserveFunds(Resolver(new Recorder { Available = 100m }), ModuleGate.Contributions).Run(At("p.reserve.funds.v1", Outward(3500m), new JsonObject(), OnSimulator, tenant: "dib"));
        Assert.Equal("insufficient", short_.Outcome);
        Assert.Equal("AM04", short_.ReasonCode);
        Assert.Equal(100m, (decimal)short_.Writes["facts.reservation"]!["available"]!);
    }

    [Fact]
    public void A_declined_posting_is_rejected_and_one_that_may_have_reached_core_banking_is_unknown_never_retried_blind()
    {
        var declined = ModuleGate.PostAccounting(Resolver(new Recorder { PostingCode = "E-104" })).Run(At("p.post.accounting.v1", Credit(), Template(), OnGateway, level: "item"));
        Assert.Equal("rejected", declined.Outcome);
        Assert.Equal("E-104", (string)declined.Writes["facts.posting"]!["code"]!);

        var lost = ModuleGate.PostAccounting(Resolver(new Recorder { Throw = true })).Run(At("p.post.accounting.v1", Credit(), Template(), OnGateway, level: "item"));
        Assert.Equal("unknown", lost.Outcome);
        Assert.Equal("HttpRequestException", (string)lost.Writes["facts.posting"]!["error"]!);

        // An unknown posting is not reversed blind; an operator decides.
        var view = new JsonObject { ["header"] = new JsonObject { ["paymentId"] = "P-1" }, ["facts"] = new JsonObject { ["posting"] = lost.Writes["facts.posting"]!.DeepClone() } };
        var e = Assert.Throws<NativeFailure>(() => new ReversePosting(Resolver(), ModuleGate.Contributions).Run(At("p.post.reverse.v1", view, new JsonObject(), OnGateway)));
        Assert.Equal("BusinessReject", e.Name);
    }

    [Fact]
    public void A_posting_is_reversed_leg_by_leg_under_the_reversal_code_and_verified_by_the_host_s_reference()
    {
        var r = Resolver();
        var post = ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", Credit(), Template(), OnSimulator, level: "item"));
        var view = new JsonObject { ["header"] = new JsonObject { ["paymentId"] = "P-1" }, ["facts"] = new JsonObject { ["posting"] = post.Writes["facts.posting"]!.DeepClone() } };
        Assert.Equal("verified", new VerifyPosting().Run(At("p.post.verify.v1", view, new JsonObject(), OnSimulator)).Outcome);
        var rev = new ReversePosting(r, ModuleGate.Contributions).Run(At("p.post.reverse.v1", view, new JsonObject(), OnSimulator));
        Assert.Equal("reversed", rev.Outcome);
        var batches = r.Built.Select(x => x.CoreBanking).OfType<SimulatedCoreBanking>().Single().Snapshot();
        Assert.Equal("P-1:AE070331234567890123456:REV", batches[1].Reference);
        Assert.Collection(batches[1].Legs,
            l => Assert.Equal(("credit", "9990000000001", "REV"), (l.Kind, l.Account, l.TxnCode)),
            l => Assert.Equal(("debit", "AE070331234567890123456", "REV"), (l.Kind, l.Account, l.TxnCode)));
        var nothing = new JsonObject { ["facts"] = new JsonObject { ["posting"] = new JsonObject { ["outcome"] = "rejected" } } };
        Assert.Equal("mismatch", new VerifyPosting().Run(At("p.post.verify.v1", nothing, new JsonObject(), OnSimulator)).Outcome);
    }

    [Fact]
    public void An_outward_posting_carries_the_template_s_charge_and_its_VAT()
    {
        var r = Resolver();
        var slabs = new JsonArray(new JsonObject { ["upTo"] = 1000, ["charge"] = 5 }, new JsonObject { ["upTo"] = 100000, ["charge"] = 10 });
        var res = ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", Outward(3500m), Template(p => p["chargeSlabs"] = slabs), OnSimulator));
        Assert.Equal("posted", res.Outcome);
        var legs = r.Built.Select(x => x.CoreBanking).OfType<SimulatedCoreBanking>().Single().Snapshot().Single().Legs;
        Assert.Equal(6, legs.Count);
        Assert.Equal(("debit", "0010000000001", 10m, "CHG"), (legs[2].Kind, legs[2].Account, legs[2].Amount, legs[2].TxnCode));
        Assert.Equal(("credit", "9990000000003", 10m), (legs[3].Kind, legs[3].Account, legs[3].Amount));
        Assert.Equal(("debit", 0.50m, "VAT"), (legs[4].Kind, legs[4].Amount, legs[4].TxnCode));
        Assert.Equal(("credit", "9990000000004"), (legs[5].Kind, legs[5].Account));
        // The posting fact as camt.054 reports it (Issue I-100): the entries with masked accounts, the total debited with the
        // charge and VAT, and the charges with their tax. Amounts are decimal strings (R-02-17).
        var fact = (JsonObject)res.Writes["facts.posting"]!;
        Assert.Equal("3510.50", (string?)fact["totalAmount"]);
        Assert.Equal("10.00", (string?)fact["charges"]!["amount"]);
        Assert.Equal("0.50", (string?)fact["charges"]!["tax"]!["amount"]);
        Assert.Equal("5.00", (string?)fact["charges"]!["tax"]!["rate"]);
        Assert.Equal("10.50", (string?)fact["charges"]!["totalChargesAndTaxAmount"]);
        var entries = ((JsonArray)fact["entries"]!).OfType<JsonObject>().ToList();
        Assert.Equal(6, entries.Count);
        Assert.Equal(["DBIT", "CRDT", "DBIT", "CRDT", "DBIT", "CRDT"], entries.Select(e => (string?)e["creditDebitIndicator"]));
        Assert.Equal("*********0001", (string?)entries[0]["account"]);
        Assert.DoesNotContain(entries, e => ((string?)e["account"])!.Contains("0010000000001"));
        // No slab, no charge.
        Assert.Equal(0m, TransactHub.Activities.Platform.Native.PostingTemplate.From(Template()).ChargeFor(3500m));
    }

    [Fact]
    public void Outside_the_posting_window_core_banking_is_not_called_and_the_attempt_is_retried_later()
    {
        var bindings = new PortBindings();
        bindings.Add("kind: port-bindings\ntenant: t\nbindings:\n  - port: p.port.core-banking.v1\n    adapter: simulator\n    validFrom: 2026-01-01T00:00:00Z\n    settings:\n      postingWindowStart: \"07:00\"\n      postingWindowEnd: \"22:00\"\n");
        var r = new CoreBankingResolver(bindings);
        // 06:00Z is 10:00 in the UAE: open.
        Assert.Equal("posted", ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", Credit(), Template(), OnSimulator, tenant: "t", level: "item")).Outcome);
        // 20:00Z is 00:00 in the UAE: closed, a retryable error, nothing posted.
        var e = Assert.Throws<NativeFailure>(() => ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", Credit("AE070331234567890123999"), Template(), DateTimeOffset.Parse("2026-11-24T20:00:00Z"), tenant: "t", level: "item")));
        Assert.Equal(("TransientError", true), (e.Name, e.Retryable));
        Assert.Single(r.Built.Select(x => x.CoreBanking).OfType<SimulatedCoreBanking>().Single().Snapshot());
    }

    [Theory]
    [InlineData("07:00", "22:00", "2026-11-24 07:00", "Open", "20261124T0700")]
    [InlineData("07:00", "22:00", "2026-11-24 22:00", "Open", "20261124T0700")]
    [InlineData("07:00", "22:00", "2026-11-24 22:01", "Closed", null)]
    [InlineData("23:00", "06:00", "2026-11-24 23:15", "Open", "20261124T2300")]
    [InlineData("23:00", "06:00", "2026-11-25 05:59", "Open", "20261124T2300")]
    [InlineData("23:00", "06:00", "2026-11-25 12:00", "Closed", null)]
    [InlineData("13.45", "22:00", "2026-11-24 14:00", "ConfigInvalid", null)]
    [InlineData("9:5", "22:00", "2026-11-24 14:00", "ConfigInvalid", null)]
    [InlineData("07:00", "25:00", "2026-11-24 14:00", "ConfigInvalid", null)]
    public void The_posting_window_is_the_source_s_call_hours_gate(string start, string end, string now, string state, string? id)
    {
        var w = TransactHub.Activities.Platform.Native.PostingWindow.Evaluate(start, end, DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(state, w.State.ToString());
        Assert.Equal(id, w.WindowId);
    }

    [Fact]
    public void No_tenant_no_binding_and_a_missing_template_value_are_refused_by_name_not_defaulted()
    {
        var r = Resolver();
        Assert.Equal("NoTenant", Assert.Throws<NativeFailure>(() => ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", Credit(), Template(), OnSimulator, tenant: null, level: "item"))).Name);
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", Credit(), Template(), OnSimulator, tenant: "no-such-bank", level: "item"))).Name);
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", Credit(), Template(), DateTimeOffset.Parse("2026-11-20T00:00:00Z"), tenant: "dib", level: "item"))).Name);
        var missing = Assert.Throws<NativeFailure>(() => ModuleGate.PostAccounting(r).Run(At("p.post.accounting.v1", Credit(), Template(p => p.Remove("settlementAccount")), OnSimulator, level: "item")));
        Assert.Contains("settlementAccount", missing.Message);
    }

    [Fact]
    public void An_accepted_returns_file_moves_the_returned_amount_from_suspense_to_settlement()
    {
        var r = Resolver();
        var view = (JsonObject)JsonNode.Parse("""{ "header": { "paymentId": "P-3", "businessDate": "2026-11-24" }, "batch": { "currency": "AED" }, "facts": { "returns": { "returns": "yes", "count": 1, "amount": 500 } } }""")!;
        var res = new PostReturn(new NativeCoreBanking(r, ModuleGate.Contributions)).Run(At("uae.wps.post.return.v1", view, Template(p => p["template"] = "wps-return"), OnSimulator));
        Assert.Equal("posted", res.Outcome);
        var batch = r.Built.Select(x => x.CoreBanking).OfType<SimulatedCoreBanking>().Single().Snapshot().Single();
        Assert.Equal("P-3:return", batch.Reference);
        Assert.Equal([("debit", "9990000000002", 500m), ("credit", "9990000000001", 500m)], batch.Legs.Select(l => (l.Kind, l.Account, l.Amount)));
        view["facts"]!["returns"]!.AsObject().Remove("amount");
        Assert.Equal("BusinessReject", Assert.Throws<NativeFailure>(() => new PostReturn(new NativeCoreBanking(r, ModuleGate.Contributions)).Run(At("uae.wps.post.return.v1", view, Template(), OnSimulator))).Name);
    }

    [Fact]
    public void The_gateway_s_credential_travels_by_reference_only()
    {
        var rec = new Recorder();
        var b = PortBindings.FromDirectory(Path.Combine(Banks(), "dib")).InForce("dib", CoreBankingResolver.Port, OnSimulator);
        Assert.Equal("env:TH_DIB_CORE_BANKING_TOKEN", b.Settings["secretRef"]);
        var name = "TH_DIB_CORE_BANKING_TOKEN";
        var before = Environment.GetEnvironmentVariable(name);
        try
        {
            // Not set: the call fails before it is sent, and says which reference, never a value (R-06-22).
            Environment.SetEnvironmentVariable(name, null);
            var missing = Assert.Throws<TransactHub.Security.SecretUnavailable>(() => CoreBankingAdapters.Create(b, new HttpClient(rec)).Balance("001", "0010000000001").GetAwaiter().GetResult());
            Assert.Contains("env:TH_DIB_CORE_BANKING_TOKEN", missing.Message);
            Assert.Empty(rec.Requests);
            var token = "synthetic-" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(name, token);
            CoreBankingAdapters.Create(b, new HttpClient(rec)).Balance("001", "0010000000001").GetAwaiter().GetResult();
            Assert.Equal($"Bearer {token}", rec.Requests[0].req.Headers.Authorization!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, before);
        }
    }

    /// <summary>A core banking behind a gateway, as far as the adapter can tell: it records each request and answers by function id.</summary>
    sealed class Recorder : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage req, string body)> Requests = new();
        public decimal Available { get; init; } = 2500.50m;
        public string PostingCode { get; init; } = "0";
        public bool Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (Throw) throw new HttpRequestException("the connection was reset after the request was sent");
            var posting = request.Headers.GetValues("X-Function-Id").Single() == "0332";
            var answer = posting
                ? $"{{\"code\":\"{PostingCode}\",\"hostReference\":\"H-1\"}}"
                : $"{{\"available\":{Available.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"currency\":\"AED\",\"status\":\"NORM\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
