using System.Globalization;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Native;
using TransactHub.Activities.Platform.Posting;
using TransactHub.Ports;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The native code-behinds of the order's FX, charges and posting (R-12-25, R-12-23; Issue I-136) give what their script-behinds
/// give: every factory test of each (P-FX, P-CHG, P-POST, which the studio runs on the scripts) is run here on the native code, with
/// the same context, the same parameters (the activity's defaults, and the example bank's values by date for a test that names
/// it), and the same expected outcome and writes, compared as exact decimals. The posting goes to core banking through the example
/// bank's simulator binding; a test whose simulator refuses runs on a simulator binding that refuses the line's account.
/// </summary>
public sealed class PlatformNativeTests
{
    static readonly string Spec = Path.Combine(Repo.Root(), "spec");

    static JsonObject Doc(string path) => (JsonObject)TransactHub.Operations.Yaml.ToJson(File.ReadAllText(path))!;

    public static TheoryData<string> Cases()
    {
        var d = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Repo.Root(), "spec", "factory", "p", "tests"), "*.yaml").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(f);
            if (id.StartsWith("P-FX-", StringComparison.Ordinal) || id.StartsWith("P-CHG-", StringComparison.Ordinal) || id.StartsWith("P-POST-", StringComparison.Ordinal)) d.Add(id);
        }
        return d;
    }

    /// <summary>The parameters the studio sends: each declared one at its default, or the bank's value in force on the test's date.</summary>
    static JsonObject Params(JsonObject act, string? tenant, string date)
    {
        var p = new JsonObject();
        foreach (var (k, decl) in act["parameters"] as JsonObject ?? []) p[k] = decl?["default"]?.DeepClone();
        if (tenant != "example-bank") return p;
        var cs = Doc(Path.Combine(Spec, "examples", "bank", "configuration-set.yaml"));
        var name = (string)act["name"]!;
        foreach (var v in (cs["values"] as JsonArray ?? []).OfType<JsonObject>()
                     .Where(v => ((string?)v["parameter"])?.StartsWith(name + "#", StringComparison.Ordinal) == true && string.CompareOrdinal((string?)v["validFrom"] ?? "", date) <= 0)
                     .OrderBy(v => (string?)v["validFrom"], StringComparer.Ordinal))
            p[((string)v["parameter"]!)[(name.Length + 1)..]] = v["value"]?.DeepClone();
        return p;
    }

    static INativeActivity Native(string name, JsonArray? bench)
    {
        if (name == "p.enrich.fx-rate.v1") return new EnrichFxRate();
        if (name == "p.enrich.charges.v1") return new EnrichCharges();
        var refuse = bench?.OfType<JsonObject>().Any(b => (string?)b["port"] == CoreBankingResolver.Port && (string?)b["scenario"] == "reject") == true;
        var bindings = new PortBindings();
        bindings.Add($"""
            kind: port-bindings
            tenant: example-bank
            bindings:
              - port: p.port.core-banking.v1
                adapter: simulator
                validFrom: 2026-01-01T00:00:00Z
                settings:
                  openingBalance: "1000000"
                  currency: AED
            {(refuse ? "      rejectAccount: AE070331234567890123456\n" : "")}
            """, "parity");
        // The platform module's posting on the image's core banking, as the native host gives it (native contract 1.1.0, R-09-125), gated by
        // the posting window gate the module contributes to its distribution (R-09-264, R-09-279).
        return new PostAccountingV2(new NativeCoreBanking(new CoreBankingResolver(bindings), k => k == NativeContributions.BindingGate(PostingGate.Port) ? (NativeBindingGate)PostingGate.Check : null));
    }

    /// <summary>A dotted path into the writes as the context would hold them: "facts.posting.entries.0.amount".</summary>
    static JsonNode? At(JsonObject writes, string path)
    {
        foreach (var (k, v) in writes)
        {
            if (path == k) return v;
            if (!path.StartsWith(k + ".", StringComparison.Ordinal)) continue;
            JsonNode? cur = v;
            foreach (var part in path[(k.Length + 1)..].Split('.'))
                cur = cur is JsonArray a && int.TryParse(part, out var i) ? (i < a.Count ? a[i] : null) : cur is JsonObject o ? o[part] : null;
            return cur;
        }
        return null;
    }

    static bool Same(JsonNode? want, JsonNode? got) =>
        Money.Read(want) is { } w && want is JsonValue wv && !wv.TryGetValue<string>(out _) ? Money.Read(got) == w : want?.ToJsonString() == got?.ToJsonString();

    [Theory, MemberData(nameof(Cases))]
    public void The_native_code_behind_gives_what_the_script_gives(string id)
    {
        var test = Doc(Path.Combine(Spec, "factory", "p", "tests", $"{id}.yaml"));
        var target = (string)test["target"]!;
        var act = Doc(Path.Combine(Spec, "factory", "p", "activities", $"{target}.yaml"));
        var date = (string?)test["evaluationDate"] ?? "2026-11-17";
        var view = (JsonObject)(test["given"]!["context"]!.DeepClone());
        var attempt = new NativeAttempt(target, (string?)act["release"], view, Params(act, (string?)test["tenant"], date),
            DateTimeOffset.Parse($"{date}T06:00:00Z", CultureInfo.InvariantCulture), (string?)test["tenant"] ?? "example-bank", (string?)act["level"]);
        var r = Native(target, test["bench"] as JsonArray).Run(attempt);
        var expect = (JsonObject)test["expect"]!;
        Assert.Equal((string?)expect["outcome"], r.Outcome);
        foreach (var (path, want) in expect["writes"] as JsonObject ?? [])
        {
            var got = At(r.Writes, path);
            Assert.True(Same(want, got), $"{id} {path}: the script gives {want?.ToJsonString()}, the native code-behind {got?.ToJsonString() ?? "nothing"}");
        }
    }

    [Fact]
    public void The_posting_posts_each_lines_converted_amount_and_its_charge_and_vat_as_entries_of_their_own()
    {
        // The refund order of e2e-refund-order: 100.00 and 500.00, priced 5.10 + 0.26 and 5.50 + 0.28 (the example bank's slab).
        var view = (JsonObject)JsonNode.Parse("""
        { "header": { "paymentId": "R-9", "kind": "refund", "direction": "inward" },
          "items": [
            { "payment": { "debtorAccount": { "iban": "AE070331234567890123456" }, "instructedAmount": { "value": 100, "currency": "AED" } },
              "facts": { "fx": { "to": "AED", "rate": 1, "amount": 100 }, "charges": { "amount": 5.1, "tax": { "rate": 5, "amount": 0.26 } } } },
            { "payment": { "debtorAccount": { "iban": "AE970336543210987654321" }, "instructedAmount": { "value": 500, "currency": "AED" } },
              "facts": { "fx": { "to": "AED", "rate": 1, "amount": 500 }, "charges": { "amount": 5.5, "tax": { "rate": 5, "amount": 0.28 } } } } ] }
        """)!;
        var act = Doc(Path.Combine(Spec, "factory", "p", "activities", "p.post.accounting.v2.yaml"));
        var r = Native("p.post.accounting.v2", null).Run(new NativeAttempt("p.post.accounting.v2", "2.0.0", view, Params(act, "example-bank", "2026-11-17"),
            DateTimeOffset.Parse("2026-11-17T06:00:00Z", CultureInfo.InvariantCulture), "example-bank", "batch"));
        Assert.Equal("posted", r.Outcome);
        var f = (JsonObject)r.Writes["facts.posting"]!;
        Assert.Equal(600m, Money.Read(f["amount"]));
        Assert.Equal(611.14m, Money.Read(f["totalAmount"]));
        Assert.Equal(10.6m, Money.Read(f["charges"]!["amount"]));
        Assert.Equal(0.54m, Money.Read(f["charges"]!["tax"]!["amount"]));
        Assert.Equal(["DBIT 100 PAY", "CRDT 100 PAY", "DBIT 5.1 CHG", "CRDT 5.1 CHG", "DBIT 0.26 VAT", "CRDT 0.26 VAT", "DBIT 500 PAY", "CRDT 500 PAY", "DBIT 5.5 CHG", "CRDT 5.5 CHG", "DBIT 0.28 VAT", "CRDT 0.28 VAT"],
            (f["entries"] as JsonArray)!.Select(e => $"{e!["creditDebitIndicator"]} {Money.Read(e["amount"])!.Value.ToString(CultureInfo.InvariantCulture)} {e["transactionCode"]}"));
        // The legs keep the accounts for a reversal; the entries show the last four characters only.
        Assert.Equal("*******************3456", (string?)f["entries"]![0]!["account"]);
        Assert.Equal("AE070331234567890123456", (string?)f["legs"]![0]!["account"]);
        Assert.False(string.IsNullOrEmpty((string?)f["hostReference"]));
    }
}
