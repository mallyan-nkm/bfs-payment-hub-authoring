using Microsoft.EntityFrameworkCore;
using TransactHub.Activities.Wps;
using TransactHub.Activities.Wps.Rules;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// A bank's values for the WPS rules come from its configuration in the store, as of the payment's date
// (plan W6). On SQL Server and on PostgreSQL.

[Collection(DatabaseCollection.Name)]
public sealed class WpsRuleValuesTests(Databases dbs)
{
    static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly Dictionary<string, string> Example = new()
    {
        ["CbxMolIdLength"] = "13", ["CbxFileDateMaxYearsBack"] = "1", ["CbxScrDebitAccountRequired"] = "true",
        ["CbxScrDebitAccountRegisteredCheck"] = "true", ["CbxAccountMatchLength"] = "15", ["BankRoutingCode"] = "302620122",
    };

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_values_in_force_drive_the_rules_and_a_later_change_reaches_only_later_payments(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var clock = new TestClock(T0);
        var store = new VersionedValues(db, clock);
        await WpsRuleValues.Publish(store, "example-bank", Example, T0, "example.checker");

        var (v, bad) = await WpsRuleValues.Load(store, "example-bank", T0.AddDays(1));
        Assert.Null(bad);
        Assert.Equal(13, v!.Settings.MolIdLength);
        Assert.Equal("302620122", v.BankRoutingCode);
        var scr = new[] { "SCR", "0000001234567", "302620122", "2026-11-17", "0830", "102026", "1", "1500.00", "AED", "000000000000001" };
        Assert.Null(CbxScrRules.CheckBankRoutingCode(scr, v.BankRoutingCode, 3));

        // The bank changes its routing code from the 10th; a payment of the 5th still reads the old one.
        clock.Set(T0.AddDays(2));
        await WpsRuleValues.Publish(store, "example-bank", new Dictionary<string, string> { ["BankRoutingCode"] = "302620999" }, T0.AddDays(9), "example.checker");
        Assert.Equal("302620122", (await WpsRuleValues.Load(store, "example-bank", T0.AddDays(4))).Values!.BankRoutingCode);
        var later = (await WpsRuleValues.Load(store, "example-bank", T0.AddDays(9))).Values!;
        Assert.Equal("302620999", later.BankRoutingCode);
        Assert.Equal("007", CbxScrRules.CheckBankRoutingCode(scr, later.BankRoutingCode, 3)!.ErrorCode);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_missing_or_bad_value_is_named_and_nothing_is_defaulted(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var store = new VersionedValues(db, new TestClock(T0));
        Assert.Equal("CbxMolIdLength", (await WpsRuleValues.Load(store, "other-bank", T0)).Invalid);
        var noCode = new Dictionary<string, string>(Example);
        noCode.Remove("BankRoutingCode");
        await WpsRuleValues.Publish(store, "example-bank", noCode, T0, "example.checker");
        Assert.Equal("BankRoutingCode", (await WpsRuleValues.Load(store, "example-bank", T0)).Invalid);
        await WpsRuleValues.Publish(store, "example-bank", new Dictionary<string, string> { ["BankRoutingCode"] = "30262012X" }, T0, "example.checker");
        Assert.Equal("BankRoutingCode", (await WpsRuleValues.Load(store, "example-bank", T0)).Invalid);
        await Assert.ThrowsAsync<ArgumentException>(() => WpsRuleValues.Publish(store, "example-bank", new Dictionary<string, string> { ["DIBRoutingCode"] = "1" }, T0, "x"));
    }

    [Fact]
    public void The_example_bank_configuration_in_content_configurations_is_complete()
    {
        var root = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(root); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content-configurations"))) { root = d.FullName; break; }
        var text = File.ReadAllText(Path.Combine(root, "content-configurations", "banks", "example-bank", "uae.wps.salary-file-rules.yaml"));
        foreach (var p in WpsRuleValues.Parameters) Assert.Contains($"  {p}: ", text);
    }
}
