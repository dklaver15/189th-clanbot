namespace ClanGuardBot.Services;

/// <summary>
/// When the three "season starts soon" reminders go out for a Scheduled season.
///
/// ── The rule ──
///   1. One week out: the latest evening (EveningHour, local) that is at least 7 days before the start.
///   2. One day out:  the latest evening that is at least 1 day (24h) before the start.
///   3. Six hours out: exactly 6 hours before the start.
///
/// "At least N days before" is what places a midnight start correctly: a season
/// opening at 00:00 Wednesday is thought of as starting Tuesday night, so its
/// one-day reminder lands Monday evening rather than Tuesday evening, five hours
/// out and right on top of the six-hour reminder. It also guarantees the three
/// reminders are always in order and never collide, whatever time a season opens.
///
/// Evenings are computed in the configured time zone, so they follow daylight
/// saving. The evening hour is never inside a DST transition (those happen at
/// 2 AM), so every evening maps to exactly one UTC instant.
/// </summary>
public static class XpSeasonReminders
{
    public const int Count = 3;

    /// <summary>The three reminder instants in UTC, earliest first.</summary>
    public static DateTime[] TimesUtc(DateTime startUtc, TimeZoneInfo zone, int eveningHour)
    {
        var start = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        return
        [
            LatestEveningAtOrBefore(start.AddDays(-7), zone, eveningHour),
            LatestEveningAtOrBefore(start.AddDays(-1), zone, eveningHour),
            start.AddHours(-6),
        ];
    }

    private static DateTime LatestEveningAtOrBefore(DateTime limitUtc, TimeZoneInfo zone, int eveningHour)
    {
        var limitLocal = TimeZoneInfo.ConvertTimeFromUtc(limitUtc, zone);
        var evening = limitLocal.Date.AddHours(eveningHour);
        if (evening > limitLocal) evening = evening.AddDays(-1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(evening, DateTimeKind.Unspecified), zone);
    }
}
