using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.Services;

/// <summary>
/// Builds the "reminder scheduled" status card posted to a reminder's target
/// channel at creation — the reminder equivalent of an event's RSVP post, so
/// members can see a reminder exists and when it will fire. This card is
/// informational and NEVER pings (post it with <see cref="AllowedMentions.None"/>);
/// the actual ping happens later when the reminder fires.
///
/// The card is edited to show the next run after each recurring fire, flipped to
/// a cancelled state on cancel, and deleted when a one-off fires.
/// </summary>
public static class ReminderCard
{
    private static readonly Color Scheduled = new(0x57F287); // Discord green
    private static readonly Color Cancelled = new(0x99AAB5); // muted grey

    public static Embed Build(ClanReminder r, bool cancelled)
    {
        var eb = new EmbedBuilder()
            .WithColor(cancelled ? Cancelled : Scheduled)
            .WithTitle(cancelled
                ? "🚫 Reminder cancelled"
                : (r.Frequency is null ? "🔔 Reminder scheduled" : "🔁 Recurring reminder scheduled"))
            .WithFooter($"Scheduled by {r.CreatorName}")
            .WithCurrentTimestamp();

        var desc = $"**{Truncate(r.Title, 240)}**";
        if (!string.IsNullOrWhiteSpace(r.Description))
            desc += $"\n{Truncate(r.Description, 600)}";
        eb.WithDescription(desc);

        if (!cancelled)
        {
            var label = r.Frequency is null ? "Posts" : "Next post";
            eb.AddField(label,
                $"{EventTimeParser.Stamp(r.NextFireUtc, 'F')} ({EventTimeParser.Stamp(r.NextFireUtc, 'R')})",
                inline: false);
        }

        if (r.Frequency is not null)
            eb.AddField("Repeats", DescribeRecurrence(r), inline: true);

        eb.AddField("Tagging", DescribePings(r), inline: true);

        // Show the link both as the clickable card title and an explicit field.
        if (!string.IsNullOrWhiteSpace(r.Url) && Uri.TryCreate(r.Url, UriKind.Absolute, out _))
        {
            if (!cancelled) eb.WithUrl(r.Url);
            eb.AddField("Link", r.Url, inline: false);
        }

        // Full image (not a thumbnail) so the banner actually shows, like the
        // real reminder embed. The bytes are attached to the card message.
        if (!string.IsNullOrWhiteSpace(r.ImageFileName))
            eb.WithImageUrl($"attachment://{r.ImageFileName}");

        return eb.Build();
    }

    private static string DescribeRecurrence(ClanReminder r)
    {
        if (r.Frequency is null) return "Does not repeat";
        var bound = r.MaxOccurrences is int n ? $", {n} times"
                  : r.UntilUtc is DateTime u ? $", until {EventTimeParser.Stamp(u, 'd')}"
                  : ", ongoing";
        return $"{ClanReminderFrequency.Label(r.Frequency.Value)}{bound}";
    }

    private static string DescribePings(ClanReminder r)
    {
        var parts = new List<string>();
        parts.AddRange(Split(r.PingRoleIdsCsv).Select(id => $"<@&{id}>"));
        parts.AddRange(Split(r.PingUserIdsCsv).Select(id => $"<@{id}>"));
        if (r.PingHere) parts.Add("@here");
        if (r.PingEveryone) parts.Add("@everyone");
        return parts.Count == 0 ? "No ping" : string.Join(" ", parts);
    }

    private static IEnumerable<ulong> Split(string csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => ulong.TryParse(p, out var n) ? n : 0UL)
            .Where(n => n != 0);

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..(max - 1)] + "…");
}
