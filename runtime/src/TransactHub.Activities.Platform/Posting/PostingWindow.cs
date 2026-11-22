using System.Globalization;

namespace TransactHub.Activities.Platform.Posting;

/// <summary>The gate's three results.</summary>
public enum PostingWindowState
{
    Open,
    Closed,
    ConfigInvalid,
}

/// <summary>One evaluation of the gate. When open: the window's id, <c>{openDate:yyyyMMdd}T{start:HHmm}</c>, and when it opened (business-local).</summary>
public sealed record PostingWindowResult(PostingWindowState State, string? WindowId, DateTime? OpenedAtLocal, DateTime NowLocal);

/// <summary>
/// The hours in which core banking takes postings (plan P2, posting windows), ported from the source's call-hours gate
/// (<c>CbCallWindowGate</c>, design Inward_PAF_CB_Bulk_Posting_Dispatch v1.0 section 8). A pure function over a start and an end
/// (<c>HH:mm</c>, business-local) and a business-local now: open, closed, or an invalid configuration, which fails closed. Bounds are
/// inclusive; an end before the start is an overnight window, whose late and early parts share one window id. No clock of its own.
/// </summary>
public static class PostingWindow
{
    public const string TimeFormat = "HH:mm";

    /// <summary>Strictly <c>HH:mm</c>: missing, blank, <c>13.45</c>, <c>25:00</c> and <c>9:5</c> all fail. Surrounding white space is ignored.</summary>
    public static bool TryParse(string? value, out TimeOnly time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(value) && TimeOnly.TryParseExact(value.Trim(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    public static PostingWindowResult Evaluate(string? start, string? end, DateTime nowLocal)
    {
        var now = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, nowLocal.Minute, 0, nowLocal.Kind);
        if (!TryParse(start, out var s) || !TryParse(end, out var e)) return new(PostingWindowState.ConfigInvalid, null, null, now);
        var time = TimeOnly.FromDateTime(now);
        var today = DateOnly.FromDateTime(now);
        DateOnly? openDate = s <= e
            ? (time >= s && time <= e ? today : null)   // a same-day window
            : time >= s ? today                          // overnight, the late part: it opened today
            : time <= e ? today.AddDays(-1)              // overnight, the early part: it opened yesterday
            : null;
        if (openDate is not { } date) return new(PostingWindowState.Closed, null, null, now);
        return new(PostingWindowState.Open, WindowIdFor(date, s), date.ToDateTime(s, now.Kind), now);
    }

    public static string WindowIdFor(DateOnly openDate, TimeOnly start) =>
        openDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "T" + start.ToString("HHmm", CultureInfo.InvariantCulture);
}
