using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.Services;

/// <summary>
/// Renders the #events RSVP post for a <see cref="ClanEvent"/>: the embed (title,
/// localized time, organizer, description, and the three RSVP rosters) and the
/// Going/Maybe/Decline buttons. Used by EventPublisher to post and by
/// EventRsvpInteractionHandler to re-render on each click, so both stay in sync.
///
/// Times render as Discord &lt;t:unix&gt; markdown — each viewer sees their own
/// zone. RSVP is cosmetic: the rosters here never feed attendance credit.
/// </summary>
public static class EventEmbedBuilder
{
    public const string RsvpPrefix = "evt:rsvp:";

    /// <summary>Custom id for an RSVP button: <c>evt:rsvp:&lt;status&gt;:&lt;clanEventId&gt;</c>.</summary>
    public static string ButtonId(string status, int clanEventId) => $"{RsvpPrefix}{status}:{clanEventId}";

    private static readonly Color Blurple = new(0x5865F2);

    public static Embed BuildEmbed(ClanEvent ev, IReadOnlyCollection<EventRsvp> rsvps)
    {
        var going   = rsvps.Where(r => r.Status == EventRsvpStatus.Going).ToList();
        var maybe   = rsvps.Where(r => r.Status == EventRsvpStatus.Maybe).ToList();
        var decline = rsvps.Where(r => r.Status == EventRsvpStatus.Decline).ToList();

        var eb = new EmbedBuilder()
            .WithTitle($"📅 {ev.Title}")
            .WithColor(ev.Status == ClanEventStatus.Cancelled ? Color.DarkGrey : Blurple)
            .AddField("When", $"{EventTimeParser.Stamp(ev.StartUtc, 'F')}\n{EventTimeParser.Stamp(ev.StartUtc, 'R')}")
            .AddField("Ends", EventTimeParser.Stamp(ev.EndUtc, 't'), inline: true)
            .AddField("Organizer", $"<@{ev.OrganizerId}>", inline: true);

        if (!string.IsNullOrWhiteSpace(ev.Description))
            eb.WithDescription(ev.Description);

        eb.AddField($"✅ Going ({going.Count})",        Names(going),   inline: true);
        eb.AddField($"❔ Maybe ({maybe.Count})",        Names(maybe),   inline: true);
        eb.AddField($"❌ Can't make it ({decline.Count})", Names(decline), inline: true);

        if (ev.Status == ClanEventStatus.Cancelled)
            eb.WithFooter("This event was cancelled.");
        else if (ev.SeriesId.HasValue)
            eb.WithFooter("🔁 Recurring event");

        return eb.Build();
    }

    /// <summary>
    /// The RSVP button row. Returns an empty component set when RSVP is disabled.
    /// Buttons are disabled (locked) once the event has started.
    /// </summary>
    public static MessageComponent BuildComponents(int clanEventId, bool rsvpEnabled, bool locked)
    {
        if (!rsvpEnabled)
            return new ComponentBuilder().Build();

        return new ComponentBuilder()
            .WithButton("Going",         ButtonId("going",   clanEventId), ButtonStyle.Success,   disabled: locked)
            .WithButton("Maybe",         ButtonId("maybe",   clanEventId), ButtonStyle.Secondary, disabled: locked)
            .WithButton("Can't make it", ButtonId("decline", clanEventId), ButtonStyle.Danger,    disabled: locked)
            .Build();
    }

    private static string Names(IReadOnlyCollection<EventRsvp> rsvps)
    {
        if (rsvps.Count == 0) return "—";
        var joined = string.Join(" ", rsvps.Select(r => $"<@{r.UserId}>"));
        // Discord embed field values cap at 1024 chars.
        return joined.Length <= 1024 ? joined : joined[..1000] + " …";
    }
}
