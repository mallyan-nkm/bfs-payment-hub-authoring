using TransactHub.Api;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// A bank's configuration values on the runtime (plan P6), on SQL Server and PostgreSQL: the configuration set of each
// configuration release put in force is stored as effective-dated versions (R-09-111); a later release with a later validFrom
// changes the value from that date with no deployment, and the earlier dates keep the earlier value. Values never come from a
// folder (R-09-110): the release API's branch import takes none, whatever the branch holds. Synthetic values only.

[Collection(DatabaseCollection.Name)]
public sealed class BankValuesTests(Databases dbs)
{
    const string Limit = "b.rule.internal-limit.v1#internalLimit";

    static string Set(string release, string values) =>
        $"kind: configuration-set\ntenant: t-bank\nrelease: {release}\nvalues:\n{values}bindings: []\n";

    static async Task<object?> At(StoreDbContext db, string at, string name = Limit) =>
        (await BankValues.InForce(db, "t-bank", DateTimeOffset.Parse(at))).FirstOrDefault(v => (string?)v["parameter"] == name)?["value"]?.ToJsonString().Trim('"');

    [Theory]
    [MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_later_release_changes_the_value_from_its_date_and_the_earlier_dates_keep_theirs(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-22T00:00:00Z"));
        var first = Set("1.0.0", $"  - parameter: {Limit}\n    value: 1200\n    validFrom: '2026-01-01'\n    validTo: '2026-12-31'\n  - parameter: p.table.rail-choice.v1#enabledRails\n    value: [uae.wps]\n    validFrom: '2026-01-01'\n");
        Assert.Equal(2, await BankValues.ImportText(db, clock, first, "pipeline"));
        // The same set again stores nothing.
        Assert.Equal(0, await BankValues.ImportText(db, clock, first, "pipeline"));
        Assert.Equal("1200", await At(db, "2026-11-17T06:00:00Z"));
        Assert.Equal("[\"uae.wps\"]", await At(db, "2026-11-17T06:00:00Z", "p.table.rail-choice.v1#enabledRails"));
        Assert.Null(await At(db, "2027-01-01T00:00:00Z"));

        var second = Set("1.1.0", $"  - parameter: {Limit}\n    value: 1200\n    validFrom: '2026-01-01'\n    validTo: '2026-11-19'\n  - parameter: {Limit}\n    value: 2000\n    validFrom: '2026-11-20'\n    validTo: '2026-12-31'\n");
        Assert.Equal(2, await BankValues.ImportText(db, clock, second, "pipeline"));
        Assert.Equal("2000", await At(db, "2026-11-24T06:00:00Z"));
        Assert.Equal("2000", await At(db, "2026-11-20T00:00:00Z"));
        Assert.Equal("1200", await At(db, "2026-11-19T23:59:59Z"));
        // The latest release sets the parameter's periods: the earlier release's open-ended 2026 period no longer counts.
        Assert.Equal("1200", await At(db, "2026-11-17T06:00:00Z"));
        // Nothing was changed in place: every version is still there.
        Assert.Equal(4, db.VersionedValues.Count(v => v.Tenant == "t-bank"));
        // A parameter the latest release does not set keeps the release that last set it.
        Assert.Equal("[\"uae.wps\"]", await At(db, "2026-11-24T06:00:00Z", "p.table.rail-choice.v1#enabledRails"));
    }

    [Fact]
    public void Values_have_no_way_in_from_a_folder()
    {
        // R-09-110: the folder import is gone; the only way in is a configuration release's set, put in force by DistributionService.
        Assert.Null(typeof(BankValues).GetMethod("Import"));
    }
}
