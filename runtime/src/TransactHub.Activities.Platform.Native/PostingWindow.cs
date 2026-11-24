using System.Globalization;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Platform.Native;

// The posting window gate, written once (R-09-264, Issues I-227, I-315): p.decide.posting-window.v1 evaluates it on the bank's window
// parameters, and PostingGate on a core-banking binding's posting hours, which the module contributes to its distribution so that every
// call on core banking (the platform's reservation, reversal and postings, and a scheme's postings) is gated by it. The image keeps no copy.

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

/// <summary>
/// The posting window of a core-banking binding (plan P2, posting windows): the hours the bound core banking takes calls, business-local,
/// as the binding's settings <c>postingWindowStart</c> and <c>postingWindowEnd</c>. A binding that names neither is always open. Contributed
/// by the platform module under <see cref="NativeContributions.BindingGate"/> of <c>p.port.core-banking.v1</c>; the image asks it before
/// each call on core banking for the distribution (R-09-264).
/// </summary>
public static class PostingGate
{
    public const string Port = "p.port.core-banking.v1";

    public static void Check(NativeAttempt a, NativeBinding b)
    {
        var start = b.Setting("postingWindowStart", "");
        var end = b.Setting("postingWindowEnd", "");
        if (start.Length == 0 && end.Length == 0) return;
        var w = PostingWindow.Evaluate(start, end, NativeContext.Business(a.Now));
        if (w.State == PostingWindowState.ConfigInvalid)
            throw new NativeFailure("BusinessReject", $"The posting window of {b.Port} for {b.Tenant} is not HH:mm to HH:mm", false);
        if (w.State == PostingWindowState.Closed)
            throw new NativeFailure("TransientError", $"Core banking takes postings from {start} to {end}; it is {w.NowLocal:HH:mm}", true);
    }
}
