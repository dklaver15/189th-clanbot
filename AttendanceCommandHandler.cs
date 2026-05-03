using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /attendance slash command, which displays who has been credited
/// for attending clan events in the rolling 24-hour window ending at command
/// invocation time.
///
/// ── Why this exists ──
/// EventAttendanceSnapshotService writes durable EventAttendance rows shortly
/// after each clan event ends. Officers periodically want a quick at-a-glance
/// view of recent attendance — for spot checks, to confirm a recent event was
/// processed at all, or to investigate complaints about missing credit.
/// Without this command, the only way to see attendance was a sqlite3 query
/// inside the running container.
///
/// ── Time window ──
/// Rolling 24 hours ending at command-invocation time. Picked over a "UTC
/// today" window because the 189th plays evening Central time, which means
/// a UTC-day window would drop the evening's events from view at midnight
/// UTC (~7pm Central) just hours after they finished — exactly when an
/// officer is most likely to spot-check attendance for those events.
///
/// Future events on the calendar (e.g. tomorrow's scheduled clan event)
/// are excluded by capping the window's upper bound at "now". /attendance
/// is for "what just happened," not "what's coming up."
///
/// ── Data sources ──
/// Pulls from BOTH CalendarEvents (to know which events happened in window)
/// and EventAttendance (to know who got credit). Two-source approach lets
/// us classify each event into one of four states:
///   • Hasn't started — kept as defensive code only; the window filter
///     normally excludes future events so this state should not occur in
///     practice.
///   • In progress — started, not yet ended; snapshot will run after end.
///   • Snapshot pending — ended, but LastSnapshotAttemptUtc is null.
///   • Snapshotted — list attendees (or "0 qualifying" if the snapshot ran
///     and nobody met the threshold).
///
/// Manual /add-event-credit rows (CalendarEventId == 0) are listed in their
/// own bottom section, keyed by RecordedAt rather than EventStartUtc, since
/// those rows fudge their event times to "now at insert time."
///
/// ── User name rendering ──
/// We can't fully trust Discord's &lt;@id&gt; mention resolution — it sometimes
/// fails to render even for users currently in the guild (observed in
/// ephemeral messages, with stale viewer-side caches, or for users the
/// viewer hasn't interacted with directly). When we have a snapshot-time
/// Username, we render a markdown link to the user's Discord profile URL
/// with the stored name as the link text: always-visible name, clickable
/// when Discord can resolve the user, "(left)" suffix when our local
/// guild cache no longer has a member object. Rows with no stored Username
/// fall back to raw &lt;@id&gt;.
///
/// ── Permissions ──
/// MAJ+ only (hardcoded floor, same defensive pattern as
/// EventCreditCommandHandler's CPT floor — config can't drift below intent).
/// Server Administrators bypass.
/// </summary>
public class AttendanceCommandHandler
{
    private const string CommandName = "attendance";

    /// <summary>
    /// Hardcoded minimum rank to invoke /attendance. Matches the
    /// EventCreditCommandHandler pattern of pinning the floor in code so
    /// BotConfig changes can't accidentally widen access.
    /// </summary>
    private const string MinRankFloor = "MAJ";

    /// <summary>
    /// Sentinel CalendarEventId used by EventCreditCommandHandler to mark
    /// EventAttendance rows that were inserted by /add-event-credit instead
    /// of derived from a real CalendarEvent.
    /// </summary>
    private const int ManualAdjustmentCalendarEventId = 0;

    /// <summary>
    /// Only Clan-source CalendarEvents go through the snapshot pipeline.
    /// CompDiv (comp team scrim) events are excluded, matching
    /// EventAttendanceSnapshotService's filter.
    /// </summary>
    private const string ClanEventSource = "Clan";

    /// <summary>
    /// Discord embed field-value limit. Used to truncate the per-event
    /// attendee list defensively when an event has an unusually large
    /// number of qualifying attendees. The other Discord limits (256 char
    /// field name, 6000 char total embed, 25 fields max) are not currently
    /// at risk for this command's typical output shape.
    /// </summary>
    private const int DiscordFieldValueLimit = 1024;

    private readonly IServiceProvider _services;
    private readonly ILogger<AttendanceCommandHandler> _logger;
    private readonly BotConfig _config;

    public AttendanceCommandHandler(
        IServiceProvider services,
        ILogger<AttendanceCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName)
            return;

        try
        {
            await HandleAttendanceAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", command.Data.Name);
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleAttendanceAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasMinRankFloor(caller))
        {
            await command.FollowupAsync(
                $"❌ You don't have permission to use this command (requires {MinRankFloor}+).",
                ephemeral: true);
            return;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await command.FollowupAsync("Could not determine the guild for this command.", ephemeral: true);
            return;
        }

        var guildId = guild.Id;
        var now = DateTime.UtcNow;

        // Rolling 24-hour window ending at "now". Avoids the surprise of a
        // UTC day-rollover dropping the evening's events from view at midnight
        // UTC (~7pm Central) just hours after they finished. "Last 24 hours"
        // tracks the natural intuition of "what just happened" regardless of
        // when the command is run.
        var windowStart = now.AddHours(-24);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // CalendarEvents that started in the rolling window. Clan-source only —
        // CompDiv events are excluded by EventAttendanceSnapshotService and
        // shouldn't appear here either. The StartUtc <= now bound keeps
        // tomorrow's scheduled events out of the result.
        var windowEvents = await db.CalendarEvents
            .Where(c => c.GuildId == guildId
                     && c.StartUtc >= windowStart
                     && c.StartUtc <= now
                     && c.Source == ClanEventSource)
            .OrderBy(c => c.StartUtc)
            .ToListAsync();

        // EventAttendance rows whose source event started in the window.
        // Pulled by denormalized EventStartUtc so we still see them even if
        // the originating CalendarEvent was deleted (same-day Apollo cleanup).
        // Manual rows (CalendarEventId == 0) are included here but get
        // partitioned out below.
        var windowAttendance = await db.EventAttendances
            .Where(a => a.GuildId == guildId
                     && a.EventStartUtc >= windowStart
                     && a.EventStartUtc <= now)
            .ToListAsync();

        // Manual /add-event-credit rows added during the window. Keyed by
        // RecordedAt rather than EventStartUtc — the manual flow stamps
        // EventStartUtc to "now at insert time" anyway, but RecordedAt is the
        // field that semantically represents "when did the officer add this."
        // Pulled separately because manual rows don't have meaningful
        // EventStartUtc boundaries and shouldn't be grouped with real-event
        // attendance. No upper bound needed: RecordedAt is always set to
        // DateTime.UtcNow at insert and can't be in the future.
        var windowManualCredits = await db.EventAttendances
            .Where(a => a.GuildId == guildId
                     && a.CalendarEventId == ManualAdjustmentCalendarEventId
                     && a.RecordedAt >= windowStart)
            .OrderBy(a => a.RecordedAt)
            .ToListAsync();

        var embed = BuildEmbed(guild, now, windowStart, windowEvents, windowAttendance, windowManualCredits);

        _logger.LogInformation(
            "/attendance invoked by {Caller}: {EventCount} event(s), {AttendanceCount} attendance row(s), {ManualCount} manual credit(s) in window",
            caller.Username, windowEvents.Count, windowAttendance.Count, windowManualCredits.Count);

        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    /// <summary>
    /// Builds the embed with up to three sections: events in the window
    /// (always present), orphaned attendance (rare), and manual credits
    /// (when any were added in the window).
    /// </summary>
    private static Embed BuildEmbed(
        SocketGuild guild,
        DateTime now,
        DateTime windowStart,
        List<CalendarEvent> windowEvents,
        List<EventAttendance> windowAttendance,
        List<EventAttendance> windowManualCredits)
    {
        var builder = new EmbedBuilder()
            .WithTitle("📋 Event Attendance — Last 24 Hours")
            .WithColor(Color.Blue)
            .WithFooter($"Window: {windowStart:yyyy-MM-dd HH:mm} UTC → {now:yyyy-MM-dd HH:mm} UTC");

        // Group real-event attendance by CalendarEventId for fast lookup.
        // Manual rows are skipped here; they get their own section.
        var attendanceByEventId = windowAttendance
            .Where(a => a.CalendarEventId != ManualAdjustmentCalendarEventId)
            .GroupBy(a => a.CalendarEventId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.AttendedMinutes).ToList());

        // ── Section 1: Events in window ──
        if (windowEvents.Count == 0)
        {
            builder.AddField("Events", "_No clan events in the last 24 hours._");
        }
        else
        {
            foreach (var ev in windowEvents)
            {
                var fieldName = TruncateFieldName(
                    $"📅 {ev.Title} — <t:{((DateTimeOffset)ev.StartUtc).ToUnixTimeSeconds()}:t>");
                var fieldValue = BuildEventFieldValue(guild, ev, attendanceByEventId, now);
                builder.AddField(fieldName, fieldValue);
            }
        }

        // ── Section 2: Orphaned attendance ──
        // Attendance rows that point to a CalendarEventId we don't have a
        // CalendarEvent row for any more (Apollo message cleaned up after the
        // snapshot ran). Edge case for in-window cleanup; usually empty.
        var windowEventIds = windowEvents.Select(e => e.Id).ToHashSet();
        var orphaned = attendanceByEventId
            .Where(kvp => !windowEventIds.Contains(kvp.Key))
            .SelectMany(kvp => kvp.Value)
            .OrderByDescending(a => a.AttendedMinutes)
            .ToList();

        if (orphaned.Count > 0)
        {
            var orphanedLines = orphaned
                .Select(a => $"• {FormatUser(guild, a.UserId, a.Username)} — **{a.AttendedMinutes}** min (event id {a.CalendarEventId}, calendar entry no longer present)");
            builder.AddField(
                "⚠️ Orphaned attendance",
                TruncateFieldValue(string.Join("\n", orphanedLines)));
        }

        // ── Section 3: Manual credits added in window ──
        if (windowManualCredits.Count > 0)
        {
            var manualLines = windowManualCredits
                .Select(a => $"• {FormatUser(guild, a.UserId, a.Username)} — added <t:{((DateTimeOffset)a.RecordedAt).ToUnixTimeSeconds()}:t>");
            builder.AddField(
                $"✏️ Manual credits added in window ({windowManualCredits.Count})",
                TruncateFieldValue(string.Join("\n", manualLines)));
        }

        return builder.Build();
    }

    /// <summary>
    /// Renders the body of one event's field, picking the right message for
    /// the event's current state. The state machine is:
    ///
    ///   StartUtc &gt; now              → "hasn't started"
    ///   StartUtc &lt;= now &lt; EndUtc    → "in progress"
    ///   EndUtc &lt;= now AND attendees → list them
    ///   EndUtc &lt;= now AND no rows AND LastSnapshotAttemptUtc null → "pending"
    ///   EndUtc &lt;= now AND no rows AND attempted                  → "0 qualifying"
    ///
    /// We check for attendance rows BEFORE LastSnapshotAttemptUtc so a freshly
    /// migrated DB (where LastSnapshotAttemptUtc may be null on rows that DO
    /// already have attendance from before the column existed) still renders
    /// correctly.
    /// </summary>
    private static string BuildEventFieldValue(
        SocketGuild guild,
        CalendarEvent ev,
        Dictionary<int, List<EventAttendance>> attendanceByEventId,
        DateTime now)
    {
        if (ev.StartUtc > now)
        {
            return "⏳ _Event hasn't started yet — no attendance to report._";
        }

        if (now < ev.EndUtc)
        {
            return $"🔴 _Event is in progress (ends <t:{((DateTimeOffset)ev.EndUtc).ToUnixTimeSeconds()}:R>). Attendance will be snapshotted after it ends._";
        }

        // Event has ended.
        if (attendanceByEventId.TryGetValue(ev.Id, out var attendees) && attendees.Count > 0)
        {
            var lines = attendees
                .Select(a => $"• {FormatUser(guild, a.UserId, a.Username)} — **{a.AttendedMinutes}** min")
                .ToList();
            var header = $"✅ **{attendees.Count}** attendee(s) credited";
            return TruncateFieldValue(header + "\n" + string.Join("\n", lines));
        }

        if (!ev.LastSnapshotAttemptUtc.HasValue)
        {
            return $"⏱️ _Event ended <t:{((DateTimeOffset)ev.EndUtc).ToUnixTimeSeconds()}:R>. Snapshot pending — runs every 5 minutes._";
        }

        var attemptedAt = ev.LastSnapshotAttemptUtc.Value;
        return $"✅ _Snapshot complete — no qualifying attendees._\nLast attempt: <t:{((DateTimeOffset)attemptedAt).ToUnixTimeSeconds()}:R>";
    }

    /// <summary>
    /// Renders a user reference for the embed.
    ///
    /// ── Why this isn't just &lt;@id&gt; ──
    /// Discord's user-mention rendering is unreliable for our use case. In
    /// ephemeral messages, and for users whose IDs the viewer's client
    /// hasn't cached, &lt;@id&gt; can render as the literal string
    /// "&lt;@123456&gt;" plus a "you don't have access to this link" error
    /// when clicked. This was observed for several brand-new members on
    /// 2026-05-02 even though guild.GetUser returned non-null — the bot's
    /// view of guild membership and Discord's view of mention resolution
    /// don't always agree.
    ///
    /// Instead we render a markdown link to the Discord profile URL with
    /// the user's display name as the link text. The link text always
    /// displays correctly regardless of cache state, and the URL opens
    /// the user's profile when Discord can resolve them.
    ///
    /// ── Where the display name comes from ──
    /// For users still in the guild, we pull SocketGuildUser.DisplayName
    /// at render time. That gives us the nicest rendering — server
    /// nicknames with rank prefixes ("SGT.Cobra", "MAJ.xAP3RONINx", etc.)
    /// instead of the lowercase Discord @handle. The stored Username
    /// column is the fallback for users who've since left the guild;
    /// it's also what the migration backfill populated for pre-existing
    /// rows, so historical rows for current members render with live
    /// names automatically rather than the @handles in the column.
    ///
    /// Pre-migration legacy rows where the backfill found nothing fall
    /// back to raw &lt;@id&gt; mention syntax — same behaviour as before
    /// the column existed.
    /// </summary>
    private static string FormatUser(SocketGuild guild, ulong userId, string snapshotUsername)
    {
        var member = guild.GetUser(userId);

        // Current member → live DisplayName, no "(left)" marker. Picks up
        // server nicknames (rank prefixes etc.) which is the nicest rendering.
        if (member is not null)
        {
            var displayName = EscapeForLinkText(member.DisplayName);
            return $"[**{displayName}**](https://discord.com/users/{userId})";
        }

        // Not in guild but we have a stored snapshot-time name → render that
        // with the "(left)" marker.
        if (!string.IsNullOrEmpty(snapshotUsername))
        {
            var stored = EscapeForLinkText(snapshotUsername);
            return $"[**{stored}**](https://discord.com/users/{userId}) _(left)_";
        }

        // Pre-migration legacy row that backfill couldn't resolve — fall back
        // to mention syntax. Discord may render it as raw ID; that's the
        // same behaviour we had before this column existed.
        return $"<@{userId}>";
    }

    /// <summary>
    /// Strips characters from a name that would break embedded markdown link
    /// syntax. Discord display names rarely contain these — the field is
    /// here mainly to keep the rendering robust against an officer setting
    /// an unusual nickname.
    ///
    /// Note: we deliberately do NOT escape underscores. Inside link text
    /// (the [...] portion of a markdown link), Discord doesn't treat
    /// underscores in the middle of a word as italic markers — escaping
    /// them would render the backslash literally, which is what produced
    /// the "cobrain\_action" rendering observed on the first deploy of
    /// this method.
    /// </summary>
    private static string EscapeForLinkText(string value)
    {
        return value
            .Replace("[", string.Empty)
            .Replace("]", string.Empty);
    }

    private static string TruncateFieldValue(string value)
    {
        if (value.Length <= DiscordFieldValueLimit)
            return value;

        const string suffix = "\n_…(truncated; query DB for full list)_";
        return value.Substring(0, DiscordFieldValueLimit - suffix.Length) + suffix;
    }

    private static string TruncateFieldName(string name)
    {
        const int discordFieldNameLimit = 256;
        return name.Length <= discordFieldNameLimit
            ? name
            : name.Substring(0, discordFieldNameLimit - 1) + "…";
    }

    /// <summary>
    /// MAJ+ rank gate using the same role-list-index approach as
    /// EventCreditCommandHandler. Server Administrator bypasses the rank
    /// check entirely.
    /// </summary>
    private bool HasMinRankFloor(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex = rankRoles.FindIndex(r => r.Equals(MinRankFloor, StringComparison.OrdinalIgnoreCase));
        if (minIndex < 0) return false;

        return user.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r => r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }
}