// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/PrcFileNamePolicy.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: Names an inward PRC file and supplies the business instant it is named from (design v1.0 §6.2).
// Responsibility: <AgentId:3 alphanumeric><yyMMddHHmmss in WpsBusinessTimeZone>.PRC; one truncated-to-the-second instant for
// the name and the RCR line (I-G7, A-14); the wait to the next second after a name clash.
using System.Globalization;
using System.Text.RegularExpressions;

namespace TransactHub.Activities.Wps.Inward;

public static partial class PrcFileNamePolicy
{
    /// <summary>The zone used when <c>WpsBusinessTimeZone</c> is unset (A-9).</summary>
    public const string DefaultTimeZoneId = "Asia/Dubai";

    private static readonly TimeSpan FallbackUtcOffset = TimeSpan.FromHours(4);

    [GeneratedRegex("^[A-Za-z0-9]{3}$")]
    private static partial Regex AgentIdPattern();

    /// <summary>True for exactly three ASCII letters or digits (A-6).</summary>
    public static bool IsValidAgentId(string? agentId) => agentId is not null && AgentIdPattern().IsMatch(agentId);

    /// <summary>
    /// The current business instant truncated to the second. An unset zone means <see cref="DefaultTimeZoneId"/>; a zone the
    /// host does not know falls back to a fixed UTC+4.
    /// </summary>
    public static DateTime BusinessNow(TimeProvider clock, string? timeZoneId)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var utc = clock.GetUtcNow().UtcDateTime;
        var business = ConvertToBusiness(utc, timeZoneId);
        return new DateTime(business.Ticks - (business.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Unspecified);
    }

    /// <summary>The PRC file name for the agent and the business instant. The agent must satisfy <see cref="IsValidAgentId"/>.</summary>
    public static string NameFor(string agentId, DateTime businessInstant)
    {
        if (!IsValidAgentId(agentId))
        {
            throw new ArgumentException("The PRC agent id must be exactly three letters or digits.", nameof(agentId));
        }

        return agentId + businessInstant.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + ".PRC";
    }

    /// <summary>How long to wait so the next <see cref="BusinessNow"/> lands in a later second (at least one millisecond).</summary>
    public static TimeSpan DelayToNextSecond(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var ticksIntoSecond = clock.GetUtcNow().UtcDateTime.Ticks % TimeSpan.TicksPerSecond;
        var remaining = TimeSpan.FromTicks(TimeSpan.TicksPerSecond - ticksIntoSecond);
        return remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining;
    }

    private static DateTime ConvertToBusiness(DateTime utc, string? timeZoneId)
    {
        var id = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZoneId : timeZoneId.Trim();
        try
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(id));
        }
        catch (TimeZoneNotFoundException)
        {
            return utc + FallbackUtcOffset;
        }
        catch (InvalidTimeZoneException)
        {
            return utc + FallbackUtcOffset;
        }
    }
}
