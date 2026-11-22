// The TransactHub source's own unit test (666f1ee): tests/PaymentHub.UnitTests/Activities/InwardRfa/RfaFileNamePolicyTests.cs.
// Unchanged but for the namespaces: it proves the runtime's port behaves as the source does (plan P2, golden).
// Purpose: the RFA name of WPS inward Activity 25 (design Inward_PAF_RFA_Generation v1.0 §6.2; A-5, A-8; task T2): AC-NAM-01.
// Responsibility: RfaFileNamePolicy (HeadRoutingCode characters 1–3 + yyMMddHHmmss + .RFA), with PrcFileNamePolicy.BusinessNow as the instant.
using FluentAssertions;
using TransactHub.Activities.Wps.Inward;
using TransactHub.Activities.Wps.Inward.Rfa;
using Xunit;

namespace TransactHub.Activities.Wps.Tests.Source;

public class RfaFileNamePolicyTests
{
    // AC-NAM-01: R = 740810000 and business instant 2026-07-14 09:15:12 → 408260714091512.RFA (19 characters, base 15).
    [Fact]
    public void AcNam01_RoutingPrefixPlusBusinessInstant()
    {
        var name = RfaFileNamePolicy.NameFor("740810000", new DateTime(2026, 7, 14, 9, 15, 12));

        name.Should().Be("408260714091512.RFA");
        name.Should().HaveLength(RfaFileNamePolicy.NameLength).And.HaveLength(19).And.EndWith(".RFA");
        Path.GetFileNameWithoutExtension(name).Should().HaveLength(RfaFileNamePolicy.BaseNameLength).And.HaveLength(15);
        RfaFileNamePolicy.RoutingPrefix("740810000").Should().Be("408");
    }

    // §6.2: one business instant for the name, truncated to the second (PrcFileNamePolicy.BusinessNow, reused); 05:15:12.987 UTC is 09:15:12 in Dubai.
    [Fact]
    public void NameFrom_PrcFileNamePolicyBusinessNow()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 14, 5, 15, 12, 987, TimeSpan.Zero));

        RfaFileNamePolicy.NameFor("740810000", PrcFileNamePolicy.BusinessNow(clock, "Asia/Dubai")).Should().Be("408260714091512.RFA");
    }

    // E-RG10: a code shorter than 4 characters (or not letters/digits) has no characters 1–3 to take.
    [Theory]
    [InlineData("740")]
    [InlineData("")]
    [InlineData("74 08")]
    [InlineData("7408,1")]
    public void InvalidHeadRoutingCode_Throws(string code)
    {
        var act = () => RfaFileNamePolicy.NameFor(code, new DateTime(2026, 7, 14, 9, 15, 12));
        act.Should().Throw<ArgumentException>();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
