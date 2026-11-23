using System.Text.Json.Nodes;
using TransactHub.Api;
using TransactHub.Ports;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// The bank's cut-offs and its own calendar on the runtime, from its installed configuration release, as of the right date, on SQL
/// Server and PostgreSQL (R-12-28, R-09-102, Issues I-186 and I-191). The configuration release carries the values the studio read
/// from the bank's reference tables (one per edition, R-11-51), which p.decide.cut-off.v1 reads on the workers; and the tables
/// themselves, which the calendar port's reference adapter reads with the bank's closed days. The example bank's tables, synthetic.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CutOffConfigurationTests(Databases dbs)
{
    const string Bank = "cutoff-bank";

    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content-configurations"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    static JsonObject Table(string file)
    {
        var path = Path.Combine(Root(), "content-configurations", file);
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(File.ReadAllText(path));
        return (JsonObject)JsonNode.Parse(new YamlDotNet.Serialization.SerializerBuilder().JsonCompatible().Build().Serialize(yaml))!;
    }

    // The configuration release's set as the studio builds it (buildConfigurationRelease): each table edition a value of its own.
    const string Set = """
        kind: configuration-set
        tenant: cutoff-bank
        release: 1.9.0
        values:
          - parameter: p.decide.cut-off.v1#cutOffs
            validFrom: '2026-10-01'
            value:
              - { scheme: uae.wps, direction: outward, cutOff: '14:00', timeZone: Asia/Dubai, afterCutOff: next-business-day }
          - parameter: p.decide.cut-off.v1#cutOffs
            validFrom: '2027-01-01'
            value:
              - { scheme: uae.wps, direction: outward, cutOff: '13:30', timeZone: Asia/Dubai, afterCutOff: next-business-day }
              - { scheme: uae.wps, direction: inward, cutOff: '16:00', timeZone: Asia/Dubai, afterCutOff: refuse }
          - parameter: p.decide.cut-off.v1#earlyClosings
            validFrom: '2026-10-01'
            value:
              - { day: '2026-12-31', closesAt: '12:00' }
        bindings: []
        """;

    [Theory]
    [MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_cut_offs_in_force_on_a_day_are_the_edition_of_that_day(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-23T00:00:00Z"));
        var tenant = $"{Bank}-{p}".ToLowerInvariant();
        Assert.Equal(3, await BankValues.ImportText(db, clock, Set.Replace("tenant: cutoff-bank", $"tenant: {tenant}"), "pipeline"));
        async Task<JsonArray?> CutOffs(string at) => (await BankValues.InForce(db, tenant, DateTimeOffset.Parse(at))).FirstOrDefault(v => (string?)v["parameter"] == "p.decide.cut-off.v1#cutOffs")?["value"] as JsonArray;
        Assert.Equal("14:00", (string?)(await CutOffs("2026-11-24T11:00:00Z"))![0]!["cutOff"]);
        Assert.Single((await CutOffs("2026-12-31T23:00:00Z"))!);
        var later = (await CutOffs("2027-01-04T06:00:00Z"))!;
        Assert.Equal(["13:30", "16:00"], later.Select(r => (string?)r!["cutOff"]));
        Assert.Null(await CutOffs("2026-09-30T06:00:00Z"));
    }

    [Theory]
    [MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_calendar_port_reads_the_market_calendar_and_the_banks_closed_days_from_the_installed_release(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-23T00:00:00Z"));
        var tenant = $"{Bank}-cal-{p}".ToLowerInvariant();
        var market = Table("artefacts/uae.p/uae.p.ref.business-calendar.v1/1.0.0.yaml");
        var own = Table("banks/example-bank/reference/b.ref.bank-calendar.v1.yaml");
        var cutOffs = Table("banks/example-bank/reference/b.ref.cut-offs.v1.yaml");
        // A later edition of the bank's own calendar closes the bank on Wednesday 25 November 2026 (synthetic).
        ((JsonArray)own["editions"]!).Add(new JsonObject { ["validFrom"] = "2026-11-20", ["rows"] = new JsonArray(
            new JsonObject { ["day"] = "2026-11-25", ["kind"] = "closed", ["name"] = "A synthetic closing day" },
            new JsonObject { ["day"] = "2026-12-31", ["kind"] = "half-day", ["closesAt"] = "12:00", ["name"] = "Year end" }) });
        Assert.Equal(3, await Lookups.Import(db, clock, tenant, "1.9.0", [market, own, cutOffs], "pipeline"));
        // The cut-offs table is served by date (GET /lookups/cut-offs).
        var served = await Lookups.At(db, tenant, "cut-offs", DateTimeOffset.Parse("2026-11-24T06:00:00Z"));
        Assert.Equal("14:00", (string?)served!["rows"]![0]!["cutOff"]);
        ReferenceReader reader = async (t, domain, at, ct) => await ReferenceTables.RowsAt(db, t, domain, at, ct);
        var cal = CalendarAdapters.Create(new PortBinding(tenant, CalendarAdapters.Port, ReferenceCalendar.Name, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), new Dictionary<string, string>()), reader);
        // Wednesday is the bank's closed day: a file of that day counts for Thursday; before the edition it was a business day.
        Assert.Equal(new BusinessDay("2026-11-25", false, "2026-11-26", "uae.p.ref.business-calendar.v1+b.ref.bank-calendar.v1"), await cal.Of(DateTimeOffset.Parse("2026-11-25T08:00:00Z")));
        Assert.True((await cal.Of(DateTimeOffset.Parse("2026-11-24T08:00:00Z"))).IsBusinessDay);
        // A half day is a business day; the market's holidays still count.
        Assert.True((await cal.Of(DateTimeOffset.Parse("2026-12-31T08:00:00Z"))).IsBusinessDay);
        Assert.Equal("2026-12-04", (await cal.Of(DateTimeOffset.Parse("2026-12-02T08:00:00Z"))).BusinessDate);
    }
}
