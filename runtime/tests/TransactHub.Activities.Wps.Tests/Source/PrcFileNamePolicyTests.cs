// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardPrc/PrcFileNamePolicyTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
using Xunit;
using System.Text.RegularExpressions;
using FluentAssertions;
using TransactHub.Activities.Wps.Inward;

namespace TransactHub.Activities.Wps.Tests.Source;

public class PrcFileNamePolicyTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("05")]
    [InlineData("0510")]
    [InlineData("05-")]
    [InlineData(" 51")]
    [InlineData("")]
    [InlineData(null)]
    public void Cfg02_InvalidAgentIds_AreRejected(string? agentId) =>
        PrcFileNamePolicy.IsValidAgentId(agentId).Should().BeFalse();

    [Theory]
    [InlineData("051")]
    [InlineData("ARD")]
    public void Cfg02_ValidAgentIds_AreAccepted(string agentId) =>
        PrcFileNamePolicy.IsValidAgentId(agentId).Should().BeTrue();

    [Fact]
    public void Cfg05_BusinessNow_UsesZone_WithUtc4Fallback()
    {
        var clock = new FixedClock(new DateTimeOffset(2025, 8, 15, 6, 54, 30, TimeSpan.Zero));

        PrcFileNamePolicy.BusinessNow(clock, null).Should().Be(new DateTime(2025, 8, 15, 10, 54, 30));
        PrcFileNamePolicy.BusinessNow(clock, "UTC").Should().Be(new DateTime(2025, 8, 15, 6, 54, 30));
        PrcFileNamePolicy.BusinessNow(clock, "No/Such_Zone").Should().Be(new DateTime(2025, 8, 15, 10, 54, 30));
    }

    [Fact]
    public void Nam01_NameFor_FormatsAgentAndInstant() =>
        PrcFileNamePolicy.NameFor("051", new DateTime(2025, 1, 15, 9, 30, 15)).Should().Be("051250115093015.PRC");

    [Fact]
    public void Nam02_TickingClock_NameAndInstantAgree()
    {
        var clock = new FixedClock(new DateTimeOffset(2025, 8, 15, 6, 54, 30, 900, TimeSpan.Zero));
        var instant = PrcFileNamePolicy.BusinessNow(clock, null);
        clock.Now = clock.Now.AddMilliseconds(300);

        PrcFileNamePolicy.NameFor("ARD", instant).Should().Be("ARD250815105430.PRC");
    }

    [Fact]
    public void Nam02_DelayToNextSecond_ReachesALaterSecond()
    {
        var clock = new FixedClock(new DateTimeOffset(2025, 8, 15, 6, 54, 30, 250, TimeSpan.Zero));
        var before = PrcFileNamePolicy.BusinessNow(clock, null);

        clock.Now += PrcFileNamePolicy.DelayToNextSecond(clock);

        PrcFileNamePolicy.BusinessNow(clock, null).Should().Be(before.AddSeconds(1));
    }

    [Fact]
    public void Nam06_EveryName_MatchesThePattern()
    {
        var pattern = new Regex(@"^[A-Za-z0-9]{3}[0-9]{12}\.PRC$");
        var start = new DateTime(2025, 1, 1, 0, 0, 0);
        for (var i = 0; i < 2000; i++)
        {
            PrcFileNamePolicy.NameFor("a1Z", start.AddSeconds(i * 86_413L)).Should().MatchRegex(pattern.ToString());
        }
    }

    [Fact]
    public void NameFor_InvalidAgent_Throws()
    {
        var act = () => PrcFileNamePolicy.NameFor("05", DateTime.UtcNow);
        act.Should().Throw<ArgumentException>();
    }
}
