using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;

namespace ClanGuardBot.Services;

/// <summary>
/// The waitlist rule for an event's "Going" cap, plus the promotion DM.
///
/// ── The rule ──
/// "Going intent" = every RSVP whose Status is Going or Waitlisted, ordered by
/// signup time (<see cref="EventRsvp.UpdatedAt"/>, oldest first). The first
/// <c>MaxParticipants</c> of them are confirmed Going; the rest are Waitlisted.
/// A null cap means unlimited — everyone is Going. Maybe/Decline are never
/// touched (the cap only governs attendance, not interest).
///
/// This single rule covers every transition:
///   • someone clicks Going on a full event  → they sort last, land Waitlisted
///   • a confirmed member drops to Maybe/Decline → oldest waitlister promotes
///   • the cap is lowered below the Going count → newest confirmed demote
///   • the cap is raised or cleared           → waitlisters promote to fill
///
/// <see cref="Rebalance"/> is pure (mutates the in-memory rows; the caller saves
/// and re-renders) and returns the user ids that flipped Waitlisted → Going so
/// the caller can DM them.
/// </summary>
public static class EventWaitlist
{
    /// <summary>
    /// Re-derives Going vs Waitlisted for <paramref name="rsvps"/> under
    /// <paramref name="max"/>. Mutates <see cref="EventRsvp.Status"/> in place and
    /// returns the user ids newly promoted (Waitlisted → Going) this pass.
    /// </summary>
    public static List<ulong> Rebalance(IReadOnlyCollection<EventRsvp> rsvps, int? max)
    {
        var promoted = new List<ulong>();

        var intent = rsvps
            .Where(r => r.Status is EventRsvpStatus.Going or EventRsvpStatus.Waitlisted)
            .OrderBy(r => r.UpdatedAt)
            .ThenBy(r => r.Id)
            .ToList();

        for (var i = 0; i < intent.Count; i++)
        {
            var r        = intent[i];
            var confirmed = max is null || i < max.Value;
            var target    = confirmed ? EventRsvpStatus.Going : EventRsvpStatus.Waitlisted;

            if (r.Status == target) continue;
            if (r.Status == EventRsvpStatus.Waitlisted && target == EventRsvpStatus.Going)
                promoted.Add(r.UserId);
            r.Status = target;
        }

        return promoted;
    }

    /// <summary>
    /// Best-effort DM to a member who was just auto-promoted off the waitlist.
    /// Swallows everything (closed DMs, deleted message) — the embed re-render is
    /// the source of truth; the DM is a courtesy.
    /// </summary>
    public static async Task NotifyPromotedAsync(DiscordSocketClient client, ClanEvent ev, ulong userId)
    {
        try
        {
            IUser? user = client.GetGuild(ev.GuildId)?.GetUser(userId);
            user ??= client.GetUser(userId);
            user ??= await client.Rest.GetUserAsync(userId);
            if (user is null) return;

            var jump  = $"https://discord.com/channels/{ev.GuildId}/{ev.ChannelId}/{ev.MessageId}";
            var embed = new EmbedBuilder()
                .WithColor(new Color(0x57F287))
                .WithTitle("✅ A spot opened up!")
                .WithDescription(
                    $"A waitlist spot freed up — you're now **going** to **{ev.Title}**.\n\n" +
                    $"🕒 {EventTimeParser.Stamp(ev.StartUtc, 'F')} ({EventTimeParser.Stamp(ev.StartUtc, 'R')})\n\n" +
                    $"[Jump to the event]({jump})")
                .Build();

            var dm = await user.CreateDMChannelAsync();
            await dm.SendMessageAsync(embed: embed);
        }
        catch
        {
            /* best-effort — member may have DMs closed */
        }
    }
}
