using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Activities.Native;
using TransactHub.Store;

namespace TransactHub.Operations.Tests;

/// <summary>
/// A hold's expiry counts business days of the bank's business calendar (owner's decision of 23 November 2026, R-09-88, Issue
/// I-127): the runtime's hold reads the reference table of domain business-calendar its configuration installed, records when the
/// hold expires, answers the engine with the calendar so that the engine's timer counts the same days, and the sweep expires it then.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HoldExpiryCalendarTests(Databases dbs)
{
    static readonly DateTimeOffset Friday = DateTimeOffset.Parse("2026-11-27T06:00:00Z");

    static NativeAttempt Attempt(DateTimeOffset now, string calendar = "business") => new("p.hold.approval.v1", "1.1.0",
        new JsonObject { ["header"] = new JsonObject { ["scheme"] = "uae.wps", ["direction"] = "outward", ["kind"] = "payment", ["fileId"] = WpsRun.FileId } },
        new JsonObject { ["expiry"] = "P1D", ["expiryCalendar"] = calendar }, now, WpsRun.Tenant);

    /// <summary>The platform's business calendar, as a configuration release installs it (R-09-75).</summary>
    static async Task InstallCalendar(StoreOptions store)
    {
        var path = Path.Combine(Repo.Root(), "content-configurations", "artefacts", "uae.p", "uae.p.ref.business-calendar.v1", "1.0.0.yaml");
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(await File.ReadAllTextAsync(path));
        var json = JsonNode.Parse(new YamlDotNet.Serialization.SerializerBuilder().JsonCompatible().Build().Serialize(yaml))!;
        await using var db = StoreDbContext.Open(store);
        await new VersionedValues(db).Publish("reference", WpsRun.Tenant, HoldCalendar.Domain, new JsonObject { ["table"] = json, ["configurationRelease"] = "test" }, DateTimeOffset.Parse("2026-10-01T00:00:00Z"), null, "test");
    }

    [Fact]
    public void Whole_days_are_business_days_of_the_calendar_and_the_rest_is_elapsed()
    {
        var cal = new HoldCalendar([0, 6], ["2026-12-01", "2026-12-02", "2026-12-03"], "t");
        Assert.Equal(DateTimeOffset.Parse("2026-11-30T06:00:00Z"), HoldCalendar.ExpiresAt(Friday, "P1D", cal));
        Assert.Equal(DateTimeOffset.Parse("2026-12-04T08:00:00Z"), HoldCalendar.ExpiresAt(DateTimeOffset.Parse("2026-11-30T06:00:00Z"), "P1DT2H", cal));
        Assert.Equal(DateTimeOffset.Parse("2026-11-28T06:00:00Z"), HoldCalendar.ExpiresAt(Friday, "P1D", null));
        // The engines count it the same (studio/engines/shared, Duration.Add on a BusinessCalendar).
        var engines = TransactHub.Engines.Duration.Add(Friday.ToUnixTimeMilliseconds(), "P1D", new TransactHub.Engines.BusinessCalendar([0, 6], []));
        Assert.Equal(DateTimeOffset.Parse("2026-11-30T06:00:00Z").ToUnixTimeMilliseconds(), engines);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_hold_opened_on_a_Friday_expires_on_Monday_and_answers_the_engine_with_the_calendar(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        await InstallCalendar(store);
        var id = await WpsRun.Seed(store, hold: false);
        var clock = new ManualClock(Friday);
        var hold = new StoreHold(store, "p.hold.approval.v1", "approval", "facts.approval", ["approved", "declined"], clock);
        var held = hold.Run(Attempt(Friday));
        Assert.Equal("held", held.Outcome);
        Assert.Equal("business", (string?)held.Held!["expiryCalendar"]);
        Assert.Equal("uae.p.ref.business-calendar.v1", (string?)held.Held["calendar"]!["table"]);
        Assert.Contains("2026-12-02", ((JsonArray)held.Held["calendar"]!["holidays"]!).Select(x => (string?)x));
        await using (var db = StoreDbContext.Open(store))
        {
            var opened = await db.Facts.AsNoTracking().Where(f => f.SubjectId == id && f.Kind == "hold" && f.Code == "opened").SingleAsync();
            Assert.Equal("2026-11-30T06:00:00", ((string?)JsonNode.Parse(opened.DataJson)!["expiresAt"])![..19]);
        }
        var sweep = new HoldExpiry(store, new OutboxSignals(store, clock), clock);
        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1)); // Saturday: a day has elapsed, no business day has
        Assert.Equal(0, await sweep.Sweep());
        clock.Advance(TimeSpan.FromDays(2) - TimeSpan.FromMinutes(2)); // Monday, a minute before
        Assert.Equal(0, await sweep.Sweep());
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await sweep.Sweep());
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_bank_that_counts_elapsed_time_expires_a_day_later_whatever_the_day(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        await InstallCalendar(store);
        await WpsRun.Seed(store, hold: false);
        var clock = new ManualClock(Friday);
        var hold = new StoreHold(store, "p.hold.approval.v1", "approval", "facts.approval", ["approved", "declined"], clock);
        Assert.Null((string?)hold.Run(Attempt(Friday, "elapsed")).Held!["expiryCalendar"]);
        var sweep = new HoldExpiry(store, new OutboxSignals(store, clock), clock);
        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await sweep.Sweep());
    }
}
