using System.Text.Json.Nodes;
using TransactHub.FileApi;
using TransactHub.Store;

namespace TransactHub.FileApi.Tests;

/// <summary>
/// The country's default currency at the front door (R-02-24; owner's decision of 24 November 2026): the parameter a country
/// cluster marks <c>role: default-currency</c>, AED for the UAE, which a bank of the cluster may set for itself, given to a payment
/// that names none after its intake map. A cluster that declares none gives none, so the canonical checks refuse the payment.
/// Synthetic values only.
/// </summary>
public sealed class DefaultCurrencyTests(Store pg) : IClassFixture<Store>
{
    static readonly JsonObject Cluster = (JsonObject)JsonNode.Parse("""
        { "kind": "activity", "name": "uae.p.enrich.default-currency.v1", "release": "1.0.0",
          "parameters": { "currency": { "type": "code", "default": "AED", "bounds": { "pattern": "^[A-Z]{3}$" }, "role": "default-currency" } } }
        """)!;

    static JsonObject File_() => (JsonObject)JsonNode.Parse("""{ "header": { "scheme": "uae.wps" }, "batch": { "id": "F-1" }, "items": [ { "payment": { "instructedAmount": { "value": "1.00" } } } ] }""")!;

    [Fact]
    public async Task A_bank_of_the_cluster_gets_its_default_or_its_own_and_a_cluster_with_none_gives_none()
    {
        var o = await pg.Fresh();
        var dispatch = Dispatch.Of([Cluster]);
        var now = DateTimeOffset.UtcNow;
        await using var db = StoreDbContext.Open(o);
        // A UAE bank that sets nothing: the cluster's AED.
        Assert.Equal("AED", await DefaultCurrency.Of(db, dispatch, "bank-a", "uae.wps", now));
        var ctx = File_();
        Assert.True(DefaultCurrency.Apply(ctx, "AED"));
        Assert.Equal("AED", (string?)ctx["batch"]!["currency"]);
        // A file that names its own keeps it.
        var named = File_();
        named["batch"]!["currency"] = "EUR";
        Assert.False(DefaultCurrency.Apply(named, "AED"));
        Assert.Equal("EUR", (string?)named["batch"]!["currency"]);
        // A bank that sets its own value of the cluster's parameter keeps its own.
        await new VersionedValues(db).Publish(ConfigurationValues.Kind, "bank-b", "uae.p.enrich.default-currency.v1#currency",
            new JsonObject { ["value"] = "USD", ["configurationSet"] = "1.0.0" }, now.AddDays(-1), null, "test");
        Assert.Equal("USD", await DefaultCurrency.Of(db, dispatch, "bank-b", "uae.wps", now));
        // A scheme of a cluster that declares no default, and a bank with no value: none, so the payment keeps no currency.
        Assert.Null(await DefaultCurrency.Of(db, dispatch, "bank-a", "xx.test", now));
        var bare = File_();
        Assert.False(DefaultCurrency.Apply(bare, null));
        Assert.False(DefaultCurrency.Names(bare));
        // The platform names none: a scheme in no cluster gives none.
        Assert.Null(DefaultCurrency.ClusterOf("p"));
    }
}
