namespace ClanGuardBot.Services;

/// <summary>Clip times as people read them.</summary>
public static class ClipTime
{
    /// <summary>Seconds as <c>m:ss</c>, rounded to the nearest second (65 → "1:05").</summary>
    public static string Format(double seconds)
    {
        var total = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
        return $"{total / 60}:{total % 60:00}";
    }
}
