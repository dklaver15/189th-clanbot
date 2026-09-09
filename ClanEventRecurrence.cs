using ClanGuardBot.Models;

namespace ClanGuardBot.Services;

/// <summary>
/// Computes the concrete occurrences of a <see cref="ClanEventSeries"/>, used by
/// EventPublisher (initial fill) and the recurrence scheduler (rolling top-up).
///
/// ── DST-safe ──
/// Occurrence starts are anchored in the organizer's LOCAL wall-clock
/// (<see cref="ClanEventSeries.FirstStartLocal"/> advanced by the frequency),
/// then converted to UTC fresh for each one. A fixed UTC delta would drift an
/// hour across the spring/fall transition; this keeps every occurrence at the
/// same wall-clock time (e.g. 8 PM Central) year-round. A local time that falls
/// in a spring-forward gap is nudged forward into the valid range.
/// </summary>
public static class ClanEventRecurrence
{
    /// <summary>
    /// Yields (StartUtc, EndUtc) for occurrences whose start lands in
    /// (<paramref name="fromUtc"/>, <paramref name="toUtc"/>], honoring the
    /// series' UntilUtc / MaxOccurrences bounds and capping at
    /// <paramref name="maxToYield"/> results per call.
    /// </summary>
    public static IEnumerable<(DateTime StartUtc, DateTime EndUtc)> Occurrences(
        ClanEventSeries series, DateTime fromUtc, DateTime toUtc, int maxToYield)
    {
        // Custom (specific-dates) series have their occurrences materialized up
        // front and must never be rule-generated — otherwise Advance's default
        // arm would treat them as daily. Yield nothing so the scheduler and the
        // publisher's horizon-fill both leave them untouched.
        if (series.Frequency == ClanEventFrequency.Custom)
            yield break;

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(series.TimeZoneId, out var tz) || tz is null)
            tz = TimeZoneInfo.Utc;

        var duration = TimeSpan.FromMinutes(series.DurationMinutes);
        var yielded = 0;

        for (var n = 0; ; n++)
        {
            if (series.MaxOccurrences is int max && n >= max)
                yield break;

            var local   = Advance(series.FirstStartLocal, series.Frequency, n);
            var startUtc = LocalToUtc(local, tz);

            if (series.UntilUtc is DateTime until && startUtc > until)
                yield break;
            if (startUtc > toUtc)
                yield break;                 // occurrences only move forward — nothing more in range
            if (startUtc <= fromUtc)
                continue;                    // already before the window; keep scanning forward

            yield return (startUtc, startUtc + duration);

            if (++yielded >= maxToYield)
                yield break;
        }
    }

    private static DateTime Advance(DateTime localAnchor, ClanEventFrequency freq, int n) => freq switch
    {
        ClanEventFrequency.SixHourly => localAnchor.AddHours(6 * n),
        ClanEventFrequency.Daily    => localAnchor.AddDays(n),
        ClanEventFrequency.Weekly   => localAnchor.AddDays(7 * n),
        ClanEventFrequency.Biweekly => localAnchor.AddDays(14 * n),
        ClanEventFrequency.Monthly  => localAnchor.AddMonths(n),
        _                           => localAnchor.AddDays(n),
    };

    private static DateTime LocalToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
            local = local.AddHours(1);       // spring-forward gap → shift into a valid wall-clock time
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }
}
