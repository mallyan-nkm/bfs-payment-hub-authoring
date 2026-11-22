// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/ExchangeRateBusinessClock.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: The WPS business clock for the exchange-rate refresh (2026-10-09).
// Responsibility: convert an instant to the WPS business time zone, so the request fields (design §5.1,
// D-A5-45) and the expiry rule (§8.1, D-A5-19) read the same calendar.
//
// The same contract as SifUploadRules.BusinessNow: the zone id comes from the WpsBusinessTimeZone
// setting (default Asia/Dubai, as everywhere in the outward WPS flow), and a container without tzdata
// falls back to a fixed UTC+4, which is exactly right for the UAE (no daylight saving). The fallback is
// returned, not swallowed, so the calling activity logs it.
namespace TransactHub.Activities.Wps.Inward;

/// <summary>Wall-clock time and calendar date in the WPS business time zone.</summary>
internal static class ExchangeRateBusinessClock
{
    /// <summary>The configuration key of the WPS business time zone, shared with the outward flow.</summary>
    public const string TimeZoneSettingKey = "WpsBusinessTimeZone";

    /// <summary>The business time zone when <see cref="TimeZoneSettingKey"/> is not set.</summary>
    public const string DefaultTimeZoneId = "Asia/Dubai";

    /// <summary>
    /// <paramref name="instant"/> as wall-clock time in <paramref name="timeZoneId"/>; a fixed UTC+4 when
    /// the time-zone database lacks the id (<c>UsedFallback = true</c>, which the caller logs).
    /// </summary>
    public static (DateTime Local, bool UsedFallback) ToBusinessTime(DateTimeOffset instant, string timeZoneId)
    {
        try
        {
            return (TimeZoneInfo.ConvertTimeFromUtc(instant.UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)), false);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return (instant.UtcDateTime.AddHours(4), true);
        }
    }

    /// <summary>The calendar date of <paramref name="instant"/> in <paramref name="timeZoneId"/>, with the same fallback.</summary>
    public static (DateOnly Today, bool UsedFallback) BusinessDate(DateTimeOffset instant, string timeZoneId)
    {
        var (local, usedFallback) = ToBusinessTime(instant, timeZoneId);
        return (DateOnly.FromDateTime(local), usedFallback);
    }
}
