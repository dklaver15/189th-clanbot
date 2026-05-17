using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

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
/// Pulls from THREE tables: CalendarEvents (to know which events happened
/// in window), EventAttendance (to know who got events-VC credit), and
/// MeetingAttendance (to know who got meetings-VC credit). Per clan rule
/// "meeting attendance counts as event attendance" the two attendance
/// streams are merged in-process and rendered uniformly per event — a
/// member who qualified via either pipeline shows up the same way, and
/// a member who somehow qualified via both is deduped to a single bullet
/// (max minutes). Two-source-plus-meetings approach lets us classify each
/// event into one of four states:
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
/// those rows fudge their event times to "now at insert time." Manual rows
/// only exist on the EventAttendance side — there is no parallel manual
/// flow for meetings.
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
    /// number of qualifying attendees.
    /// </summary>
    private const int DiscordFieldValueLimit = 1024;

    /// <summary>
    /// Discord's hard cap on the combined length of an embed's title,
    /// description, footer text, author name, and all field name+value
    /// pairs. Exceeding this returns 400 from the API and the followup
    /// fails entirely — observable to officers as the generic
    /// "Something went wrong" fallback. /attendance can hit this on a
    /// heavy event day (many events × many attendees + orphan + manual
    /// sections).
    /// </summary>
    private const int DiscordEmbedTotalLimit = 6000;

    /// <summary>
    /// Discord's hard cap on field count per embed. /attendance produces
    /// one field per event plus continuation fields from chunked attendee
    /// lists, so this can also be reached on a heavy day independently
    /// of the total-char limit.
    /// </summary>
    private const int DiscordEmbedMaxFields = 25;

    /// <summary>
    /// Headroom held back from <see cref="DiscordEmbedTotalLimit"/> and
    /// from <see cref="DiscordEmbedMaxFields"/> so that, when we have to
    /// stop adding content, the final "output truncated" notice can
    /// always fit. Without the reserve, hitting the cap mid-section
    /// would leave the embed silently incomplete with no signal to the
    /// reader.
    /// </summary>
    private const int TruncationSummaryReserve = 200;

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
        var windowEventAttendance = await db.EventAttendances
            .Where(a => a.GuildId == guildId
                     && a.EventStartUtc >= windowStart
                     && a.EventStartUtc <= now)
            .ToListAsync();

        // Meeting attendance rows for the same window. Per the clan rule
        // "meeting attendance counts as event attendance", these are merged
        // into the same per-event listing rather than rendered separately.
        // Promoted to EventAttendance shape so the rest of the embed builder
        // (which keys off EventAttendance fields) doesn't need a parallel
        // code path. The promotion is purely for in-memory display — nothing
        // is written back. Manual-credit sentinel handling is unaffected:
        // MeetingAttendance has no concept of CalendarEventId=0, so these
        // rows never collide with the manual section below.
        var windowMeetingAttendance = await db.MeetingAttendances
            .Where(a => a.GuildId == guildId
                     && a.EventStartUtc >= windowStart
                     && a.EventStartUtc <= now)
            .Select(m => new EventAttendance
            {
                GuildId         = m.GuildId,
                UserId          = m.UserId,
                CalendarEventId = m.CalendarEventId,
                Username        = m.Username,
                EventStartUtc   = m.EventStartUtc,
                EventEndUtc     = m.EventEndUtc,
                AttendedMinutes = m.AttendedMinutes,
                RecordedAt      = m.RecordedAt
            })
            .ToListAsync();

        // Combine, then dedupe by (CalendarEventId, UserId) keeping the
        // higher AttendedMinutes. The rare collision case — a member who
        // qualified in BOTH the events-VC pipeline AND the meetings-VC
        // pipeline for the same Apollo event — would otherwise render as
        // two bullet lines for the same person. Dedupe avoids that
        // confusion. Max-minutes is a display choice (the minutes value
        // isn't used for promotion math anywhere downstream of /attendance);
        // it shows the larger of the two qualifying sessions so the
        // officer reading the embed sees "this person's heaviest VC
        // presence at this event."
        var windowAttendance = windowEventAttendance
            .Concat(windowMeetingAttendance)
            .GroupBy(a => (a.CalendarEventId, a.UserId))
            .Select(g => g.OrderByDescending(a => a.AttendedMinutes).First())
            .ToList();

        // Manual /add-event-credit rows added during the window. Keyed by
        // RecordedAt rather than EventStartUtc — the manual flow stamps
        // EventStartUtc to "now at insert time" anyway, but RecordedAt is the
        // field that semantically represents "when did the officer add this."
        // Pulled separately because manual rows don't have meaningful
        // EventStartUtc boundaries and shouldn't be grouped with real-event
        // attendance. No upper bound needed: RecordedAt is always set to
        // DateTime.UtcNow at insert and can't be in the future. Sourced
        // from EventAttendance only — manual-credit doesn't have a meeting
        // counterpart by design.
        var windowManualCredits = await db.EventAttendances
            .Where(a => a.GuildId == guildId
                     && a.CalendarEventId == ManualAdjustmentCalendarEventId
                     && a.RecordedAt >= windowStart)
            .OrderBy(a => a.RecordedAt)
            .ToListAsync();

        var embed = BuildEmbed(guild, now, windowStart, windowEvents, windowAttendance, windowManualCredits);

        _logger.LogInformation(
            "/attendance invoked by {Caller}: {EventCount} event(s), {EventRowCount} event-attendance row(s), {MeetingRowCount} meeting-attendance row(s), {ManualCount} manual credit(s) in window",
            caller.Username, windowEvents.Count, windowEventAttendance.Count, windowMeetingAttendance.Count, windowManualCredits.Count);

        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    /// <summary>
    /// Builds the embed with up to three sections: events in the window
    /// (always present), orphaned attendance (rare), and manual credits
    /// (when any were added in the window). Long attendee lists are split
    /// across multiple embed fields; see AddChunkedField.
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

        var truncated = false;

        // ── Section 1: Events in window ──
        if (windowEvents.Count == 0)
        {
            // Trivially small; well under any cap.
            builder.AddField("Events", "_No clan events in the last 24 hours._");
        }
        else
        {
            foreach (var ev in windowEvents)
            {
                var fieldName = TruncateFieldName(
                    $"📅 {ev.Title} — <t:{((DateTimeOffset)ev.StartUtc).ToUnixTimeSeconds()}:t>");
                var (header, attendeeLines) = BuildEventContent(guild, ev, attendanceByEventId, now);

                bool added;
                if (attendeeLines is null)
                {
                    // Non-attendee state (in progress, snapshot pending, no
                    // qualifiers, etc.) — short single-field message.
                    added = TryAddField(builder, fieldName, header);
                }
                else
                {
                    // Attendees present — may need chunking across fields if
                    // the list is long enough to exceed Discord's 1024-char
                    // field-value limit.
                    added = AddChunkedField(builder, fieldName, header, attendeeLines);
                }

                if (!added)
                {
                    truncated = true;
                    break;
                }
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
                .Select(a => $"• {FormatUser(guild, a.UserId, a.Username)} — **{a.AttendedMinutes}** min (event id {a.CalendarEventId}, calendar entry no longer present)")
                .ToList();
            if (!AddChunkedField(builder, "⚠️ Orphaned attendance", header: string.Empty, lines: orphanedLines))
                truncated = true;
        }

        // ── Section 3: Manual credits added in window ──
        if (windowManualCredits.Count > 0)
        {
            var manualLines = windowManualCredits
                .Select(a => $"• {FormatUser(guild, a.UserId, a.Username)} — added <t:{((DateTimeOffset)a.RecordedAt).ToUnixTimeSeconds()}:t>")
                .ToList();
            if (!AddChunkedField(
                    builder,
                    $"✏️ Manual credits added in window ({windowManualCredits.Count})",
                    header: string.Empty,
                    lines: manualLines))
                truncated = true;
        }

        // ── Truncation notice ──
        // We always reserve room for this final field via TruncationSummaryReserve,
        // so a direct AddField is safe even when truncation occurred.
        if (truncated)
        {
            builder.AddField(
                "⚠️ Output truncated",
                "_Embed size limit reached — some entries were omitted. Use sqlite to view the full list._");
        }

        return builder.Build();
    }

    /// <summary>
    /// Adds an arbitrary number of bullet lines to the embed, splitting them
    /// across multiple fields when the total content would exceed Discord's
    /// 1024-char field-value limit. The first field gets <paramref name="primaryName"/>;
    /// continuation fields are named "↳ continued" so it's visually clear
    /// they're part of the same group. An optional <paramref name="header"/>
    /// (e.g. "✅ 24 attendee(s) credited") goes into the first field above
    /// the lines; continuation fields contain only lines.
    ///
    /// Per-line length is assumed to be well under the field limit (no single
    /// attendee or manual-credit line gets remotely close to 1024 chars).
    /// We don't defensively split a single oversized line — if that ever
    /// happens it means a username/title got pathologically long and the
    /// upstream truncation is the right place to fix it.
    ///
    /// Returns true if every line was added; false if the embed-level
    /// total-size or field-count budget ran out before all lines fit.
    /// Caller is expected to surface the partial-add as truncation in the
    /// final embed.
    /// </summary>
    private static bool AddChunkedField(
        EmbedBuilder builder,
        string primaryName,
        string header,
        List<string> lines)
    {
        const string ContinuationName = "↳ continued";

        var current = new StringBuilder();
        if (!string.IsNullOrEmpty(header))
            current.Append(header);

        var isFirstField = true;

        foreach (var line in lines)
        {
            // Compute the size we'd land at if we appended this line. We
            // include the leading newline only when the buffer already has
            // content (so an empty header doesn't introduce a leading blank
            // line).
            var prefix = current.Length > 0 ? "\n" : string.Empty;
            var projected = current.Length + prefix.Length + line.Length;

            if (projected > DiscordFieldValueLimit)
            {
                // Flush the current chunk and start a fresh one with this
                // line at the top. Bail if the embed-level budget is full.
                if (!TryAddField(builder, isFirstField ? primaryName : ContinuationName, current.ToString()))
                    return false;
                isFirstField = false;
                current.Clear();
                current.Append(line);
            }
            else
            {
                current.Append(prefix).Append(line);
            }
        }

        // Flush any remaining content. The empty-current case (no header,
        // no lines) doesn't happen given the callers in BuildEmbed always
        // gate on a non-empty list, but the guard is cheap.
        if (current.Length > 0)
        {
            if (!TryAddField(builder, isFirstField ? primaryName : ContinuationName, current.ToString()))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Adds a field to the embed only if doing so leaves enough headroom
    /// (<see cref="TruncationSummaryReserve"/> chars and 1 field slot) for
    /// the final "output truncated" notice. Returns false when the embed
    /// is too full to accept this field; the caller treats that as a
    /// truncation signal and stops adding more from its section.
    /// </summary>
    private static bool TryAddField(EmbedBuilder builder, string name, string value)
    {
        var addedLength = name.Length + value.Length;
        if (builder.Length + addedLength > DiscordEmbedTotalLimit - TruncationSummaryReserve)
            return false;
        if (builder.Fields.Count >= DiscordEmbedMaxFields - 1)
            return false;

        builder.AddField(name, value);
        return true;
    }

    /// <summary>
    /// Resolves what should be rendered for a single event, picking the
    /// right message for the event's current state. Returns a tuple of
    /// (header, attendeeLines) where attendeeLines is non-null only for
    /// the "snapshotted with attendees" branch — that's the only state
    /// that needs the chunked-field rendering path. All other states fit
    /// in a single embed field by definition.
    ///
    /// State machine:
    ///   StartUtc &gt; now              → "hasn't started" (defensive; the
    ///                                   query filter normally excludes it)
    ///   StartUtc &lt;= now &lt; EndUtc    → "in progress"
    ///   EndUtc &lt;= now AND attendees → header + lines (chunked by caller)
    ///   EndUtc &lt;= now AND no rows AND either snapshot column is null  → "pending"
    ///   EndUtc &lt;= now AND no rows AND both snapshot columns stamped   → "0 qualifying"
    ///
    /// Both attempt columns participate in the pending/complete decision
    /// because attendance now flows from two services (event-side and
    /// meeting-side). If only one side has stamped its column, the other
    /// is still pending and we don't want to declare the event "complete
    /// with zero attendees" — that would mislabel an event where the
    /// other pipeline is still ~5 minutes from running.
    ///
    /// We check for attendance rows BEFORE the snapshot columns so a
    /// freshly migrated DB (where the attempt columns may be null on
    /// rows that DO already have attendance from before the columns
    /// existed) still renders correctly.
    /// </summary>
    private static (string Header, List<string>? AttendeeLines) BuildEventContent(
        SocketGuild guild,
        CalendarEvent ev,
        Dictionary<int, List<EventAttendance>> attendanceByEventId,
        DateTime now)
    {
        if (ev.StartUtc > now)
        {
            return ("⏳ _Event hasn't started yet — no attendance to report._", null);
        }

        if (now < ev.EndUtc)
        {
            return ($"🔴 _Event is in progress (ends <t:{((DateTimeOffset)ev.EndUtc).ToUnixTimeSeconds()}:R>). Attendance will be snapshotted after it ends._", null);
        }

        // Event has ended.
        if (attendanceByEventId.TryGetValue(ev.Id, out var attendees) && attendees.Count > 0)
        {
            var lines = attendees
                .Select(a => $"• {FormatUser(guild, a.UserId, a.Username)} — **{a.AttendedMinutes}** min")
                .ToList();
            var header = $"✅ **{attendees.Count}** attendee(s) credited";
            return (header, lines);
        }

        // Pending if EITHER side hasn't attempted a snapshot yet. The
        // meeting-side service is feature-gated (MeetingVoiceChannelId == 0
        // means no-op), so on a deploy where meetings are disabled
        // LastMeetingSnapshotAttemptUtc will remain null forever and this
        // would falsely report "pending" indefinitely. To stay correct in
        // that case we treat the meeting column as "complete" if the event
        // ended long enough ago that the meeting-side service would have
        // had time to run if it were active.
        var meetingProbablyDone = ev.LastMeetingSnapshotAttemptUtc.HasValue
            || ev.EndUtc < now.AddMinutes(-30);

        if (!ev.LastSnapshotAttemptUtc.HasValue || !meetingProbablyDone)
        {
            return ($"⏱️ _Event ended <t:{((DateTimeOffset)ev.EndUtc).ToUnixTimeSeconds()}:R>. Snapshot pending — runs every 5 minutes._", null);
        }

        // Pick the later of the two attempt timestamps as the "last
        // attempt" surface so the embed reflects the most recent
        // bookkeeping moment.
        var lastAttempt = ev.LastSnapshotAttemptUtc.Value;
        if (ev.LastMeetingSnapshotAttemptUtc is { } meetingAttempt && meetingAttempt > lastAttempt)
        {
            lastAttempt = meetingAttempt;
        }
        return ($"✅ _Snapshot complete — no qualifying attendees._\nLast attempt: <t:{((DateTimeOffset)lastAttempt).ToUnixTimeSeconds()}:R>", null);
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