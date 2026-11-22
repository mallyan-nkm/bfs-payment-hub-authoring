using TransactHub.Api;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// A bank's configuration values on the runtime (plan P6), on SQL Server and PostgreSQL: each configuration set the studio
// wrote to the branch is imported as effective-dated versions; a later release with a later validFrom changes the value from
// that date with no deployment, and the earlier dates keep the earlier value. Synthetic values only.

[Collection(DatabaseCollection.Name)]
public sealed class BankValuesTests(Databases dbs)
{
    const string Limit = "b.rule.internal-limit.v1#internalLimit";

    static string Set(string release, string values) =>
        $"kind: configuration-set\ntenant: t-bank\nrelease: {release}\nvalues:\n{values}bindings: []\n";

    static string Branch(params (string file, string text)[] files)
    {
        var dir = Directory.CreateTempSubdirectory("th-values-").FullName;
        foreach (var (f, t) in files)
        {
            var p = Path.Combine(dir, "banks", "t-bank", f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, t);
        }
        return dir;
    }

    static async Task<object?> At(StoreDbContext db, string at, string name = Limit) =>
        (await BankValues.InForce(db, "t-bank", DateTimeOffset.Parse(at))).FirstOrDefault(v => (string?)v["parameter"] == name)?["value"]?.ToJsonString().Trim('"');

    [Theory]
    [MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_later_release_changes_the_value_from_its_date_and_the_earlier_dates_keep_theirs(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-22T00:00:00Z"));
        var first = Set("1.0.0", $"  - parameter: {Limit}\n    value: 1200\n    validFrom: '2026-01-01'\n    validTo: '2026-12-31'\n  - parameter: p.table.rail-choice.v1#enabledRails\n    value: [uae.wps]\n    validFrom: '2026-01-01'\n");
        var dir = Branch(("configuration-set.yaml", first));
        Assert.Equal(2, await BankValues.Import(db, clock, Path.Combine(dir, "banks"), "pipeline"));
        // Importing the same branch again stores nothing.
        Assert.Equal(0, await BankValues.Import(db, clock, Path.Combine(dir, "banks"), "pipeline"));
        Assert.Equal("1200", await At(db, "2026-11-17T06:00:00Z"));
        Assert.Equal("[\"uae.wps\"]", await At(db, "2026-11-17T06:00:00Z", "p.table.rail-choice.v1#enabledRails"));
        Assert.Null(await At(db, "2027-01-01T00:00:00Z"));

        var second = Set("1.1.0", $"  - parameter: {Limit}\n    value: 1200\n    validFrom: '2026-01-01'\n    validTo: '2026-11-19'\n  - parameter: {Limit}\n    value: 2000\n    validFrom: '2026-11-20'\n    validTo: '2026-12-31'\n");
        Directory.CreateDirectory(Path.Combine(dir, "banks", "t-bank", "configuration-sets"));
        File.WriteAllText(Path.Combine(dir, "banks", "t-bank", "configuration-sets", "1.1.0.yaml"), second);
        Assert.Equal(2, await BankValues.Import(db, clock, Path.Combine(dir, "banks"), "pipeline"));
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
}
