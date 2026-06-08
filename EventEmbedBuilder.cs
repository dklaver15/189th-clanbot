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

    /// <summary>Prefix for the on-post Edit/Cancel buttons — must match EventManagementHandler.Prefix.</summary>
    private const string MgmtPrefix = "evtmgmt:";

    /// <summary>Custom id for an RSVP button: <c>evt:rsvp:&lt;status&gt;:&lt;clanEventId&gt;</c>.</summary>
    public static string ButtonId(string status, int clanEventId) => $"{RsvpPrefix}{status}:{clanEventId}";

    private static readonly Color Blurple = new(0x5865F2);

    // Invisible Braille-blank (U+2800) run. Unlike normal spaces it has real
    // width and isn't collapsed, so a line of these pushes the embed out to
    // Discord's max width on desktop (a square banner image otherwise lets the
    // embed shrink narrower than Apollo's wide flag). On mobile it wraps as
    // harmless blank space. Bump/cut the count to taste.
    private static readonly string WidthSpacer = new('\u2800', 40);

    public static Embed BuildEmbed(ClanEvent ev, IReadOnlyCollection<EventRsvp> rsvps, string? imageFileName = null)
    {
        var going   = rsvps.Where(r => r.Status == EventRsvpStatus.Going).ToList();
        var maybe   = rsvps.Where(r => r.Status == EventRsvpStatus.Maybe).ToList();
        var decline = rsvps.Where(r => r.Status == EventRsvpStatus.Decline).ToList();

        var eb = new EmbedBuilder()
            .WithTitle($"📅 {ev.Title}")
            .WithColor(ev.Status == ClanEventStatus.Cancelled ? Color.DarkGrey : Blurple)
            .AddField("When", $"{EventTimeParser.Stamp(ev.StartUtc, 'F')}\n{EventTimeParser.Stamp(ev.StartUtc, 'R')}")
            .AddField("Ends", EventTimeParser.Stamp(ev.EndUtc, 't'), inline: true)
            .AddField("Host", $"<@{ev.HostId ?? ev.OrganizerId}>", inline: true);

        if (!string.IsNullOrWhiteSpace(ev.Description))
            eb.WithDescription(ev.Description);

        // Full-width blank line between the Ends/Organizer row and the RSVP
        // rosters: adds visual separation, forces the three rosters onto a fresh
        // row as clean side-by-side columns, and (via WidthSpacer) widens the
        // embed toward Discord's max on desktop.
        eb.AddField("\u200b", WidthSpacer, inline: false);

        eb.AddField($"✅ Going ({going.Count})",        Names(going),   inline: true);
        eb.AddField($"❔ Maybe ({maybe.Count})",        Names(maybe),   inline: true);
        eb.AddField($"❌ Declined ({decline.Count})", Names(decline), inline: true);

        // Footer: "Created by <name>" (the creator — officers often create on
        // someone else's behalf), plus a recurring/cancelled note. A footer is
        // plain text, so this is the stored display name, not a mention.
        var footer = new List<string>();
        if (!string.IsNullOrWhiteSpace(ev.OrganizerName))
            footer.Add($"Created by {ev.OrganizerName}");
        if (ev.Status == ClanEventStatus.Cancelled)
            footer.Add("This event was cancelled");
        else if (ev.SeriesId.HasValue)
            footer.Add("🔁 Recurring");
        if (footer.Count > 0)
            eb.WithFooter(string.Join(" • ", footer));

        // References the file attached to this same message (see EventImage).
        if (!string.IsNullOrWhiteSpace(imageFileName))
            eb.WithImageUrl($"attachment://{imageFileName}");

        return eb.Build();
    }

    /// <summary>
    /// The button rows: emoji-only RSVP buttons (Apollo-style) on the first row,
    /// then Edit/Cancel on a second row. RSVP buttons are omitted when RSVP is
    /// disabled and are locked once the event has started; Edit/Cancel always
    /// show (permission is checked on click) so the post is self-managing.
    /// </summary>
    public static MessageComponent BuildComponents(int clanEventId, bool rsvpEnabled, bool locked)
    {
        var cb = new ComponentBuilder();

        if (rsvpEnabled)
        {
            cb.WithButton(null, ButtonId("going",   clanEventId), ButtonStyle.Secondary, emote: new Emoji("✅"), disabled: locked)
              .WithButton(null, ButtonId("maybe",   clanEventId), ButtonStyle.Secondary, emote: new Emoji("❔"), disabled: locked)
              .WithButton(null, ButtonId("decline", clanEventId), ButtonStyle.Secondary, emote: new Emoji("❌"), disabled: locked);
        }

        var mgmtRow = rsvpEnabled ? 1 : 0;
        cb.WithButton("Edit",     $"{MgmtPrefix}pedit:{clanEventId}",   ButtonStyle.Primary,   row: mgmtRow)
          .WithButton("Set Host", $"{MgmtPrefix}sethost:{clanEventId}", ButtonStyle.Success,   row: mgmtRow)
          .WithButton("Cancel",   $"{MgmtPrefix}pcancel:{clanEventId}", ButtonStyle.Danger,    row: mgmtRow);

        return cb.Build();
    }

    private static string Names(IReadOnlyCollection<EventRsvp> rsvps)
    {
        if (rsvps.Count == 0) return "—";

        // Apollo-style: each mention on its own line inside a blockquote (the
        // "> " prefix draws the vertical bar). Consecutive quoted lines merge
        // into one continuous quote. Truncate at a line boundary so we never cut
        // a mention in half, and stay under the 1024-char embed-field cap.
        var mentions = rsvps.Select(r => $"<@{r.UserId}>").ToList();
        var lines = new List<string>();
        var len = 0;
        var shown = 0;
        foreach (var m in mentions)
        {
            var add = m.Length + 3; // "> " prefix + newline
            if (len + add > 980) break;
            lines.Add($"> {m}");
            len += add;
            shown++;
        }
        if (shown < mentions.Count)
            lines.Add($"> …and {mentions.Count - shown} more");

        return string.Join("\n", lines);
    }
}
