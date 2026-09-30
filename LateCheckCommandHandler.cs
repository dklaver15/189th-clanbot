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
/// Handles the /late-check slash command. Answers "how often did this member
/// show up LATE to events THEY organized or hosted?" — the punctuality
/// counterpart to /attendance.
///
/// ── What counts ──
///   • "Their events"  = ClanEvents where OrganizerId == target OR HostId ==
///                       target that have already started. Duplicate-series
///                       rows are collapsed to one slot per StartUtc, and
///                       cancelled slots are KEPT (not dropped) because the
///                       series churn marks real, attended events as cancelled
///                       — they're just flagged "(cancelled)" in the output.
///   • "Showed up"     = the target has a VoiceSession in the EVENTS category
///                       (matched by EventsCategoryId; pre-migration rows with
///                       a null CategoryId fall back to EventsVoiceChannelId)
///                       overlapping the scan window
///                       [StartUtc - buffer, max(EndUtc + buffer,
///                       StartUtc + MaxLateHours)].
///   • "Late"          = their EARLIEST qualifying join is more than the grace
///                       period (default 15 min, overridable via the grace
///                       option) after StartUtc. The window reaches MaxLateHours
///                       past start so an hours-late arrival is counted as late
///                       rather than a no-show.
///   • "No-show"       = no qualifying voice session at all — reported
///                       separately (with dates), NOT counted as late.
///
/// The target may be given as the user picker OR a raw numeric user_id, the
/// latter so the command works on members who have already left the guild.
///
/// The window/category/buffer logic mirrors EventAttendanceSnapshotService so
/// the "showed up" determination matches what the attendance snapshotter would
/// credit. SyncWithHandlers: EventAttendanceSnapshotService.SnapshotEventAsync.
///
/// ── Permissions ──
/// MAJ+ rank gate (same floor as /attendance) — this is a leadership
/// accountability tool, not a self-service lookup. Server Administrator
/// bypasses. Ephemeral reply.
/// </summary>
public class LateCheckCommandHandler
{
    private const string MinRankFloor = "MAJ";
    private const int DefaultGraceMinutes = 15;
    private const int MaxListedEvents = 15;

    /// <summary>
    /// How many hours past an event's start we still scan for the member's
    /// first qualifying voice join. Without this, anyone arriving after
    /// EndUtc + buffer fell outside the window and was misclassified as a
    /// no-show instead of "very late". 4h comfortably covers a member who
    /// rolls in hours into their own event.
    /// </summary>
    private const int MaxLateHours = 4;

    private readonly ILogger<LateCheckCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly IServiceProvider _services;

    public LateCheckCommandHandler(
        ILogger<LateCheckCommandHandler> logger,
        IOptions<BotConfig> config,
        IServiceProvider services)
    {
        _logger = logger;
        _config = config.Value;
        _services = services;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName("late-check")
            .WithDescription("How often a member showed up late to their own events (MAJ+)")
            .AddOption("user", ApplicationCommandOptionType.User,
                "The member whose own events to check", isRequired: false)
            .AddOption("user_id", ApplicationCommandOptionType.String,
                "Raw numeric ID instead of the picker — use for members who already left",
                isRequired: false)
            .AddOption("grace", ApplicationCommandOptionType.Integer,
                $"Minutes after start that still count as on time (default {DefaultGraceMinutes})",
                isRequired: false)
            .Build();

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "late-check") return;

        try
        {
            await HandleAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /late-check for {User}", command.User.Username);
            if (command.HasResponded)
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            else
                await command.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
        }
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.User is not SocketGuildUser caller)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        if (!HasMinRankFloor(caller))
        {
            await command.FollowupAsync(
                $"❌ You don't have permission to use this command (requires {MinRankFloor}+).",
                ephemeral: true);
            return;
        }

        if (command.GuildId is not ulong guildId)
        {
            await command.FollowupAsync("Could not determine the guild for this command.", ephemeral: true);
            return;
        }

        var targetUser = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;
        var rawId = command.Data.Options.FirstOrDefault(o => o.Name == "user_id")?.Value as string;

        // Resolve the target from EITHER the picker or a raw ID. The raw-ID path
        // is what makes this usable on members who have already left the server
        // (the user picker can't select someone who isn't in the guild).
        ulong targetId;
        if (targetUser is not null)
        {
            targetId = targetUser.Id;
        }
        else if (string.IsNullOrWhiteSpace(rawId) || !ulong.TryParse(rawId.Trim(), out targetId))
        {
            await command.FollowupAsync(
                "⚠️ Select a user, or pass a numeric `user_id` (use this for members who already left).",
                ephemeral: true);
            return;
        }

        var grace = DefaultGraceMinutes;
        if (command.Data.Options.FirstOrDefault(o => o.Name == "grace")?.Value is long g)
            grace = (int)Math.Clamp(g, 0, 240);

        var displayName = await ResolveDisplayNameAsync(guildId, targetId, targetUser);

        var result = await ComputeAsync(guildId, targetId, grace);

        if (result.TotalOwnEvents == 0)
        {
            await command.FollowupAsync(
                $"**{displayName}** hasn't organized or hosted any (non-cancelled) events in the last {_config.GetActivityRetentionDays()} days.",
                ephemeral: true);
            return;
        }

        var pct = result.ShowedUp > 0
            ? $" ({100.0 * result.TimesLate / result.ShowedUp:0}% of the times they showed)"
            : "";

        var embed = new EmbedBuilder()
            .WithAuthor(displayName, targetUser?.GetDisplayAvatarUrl())
            .WithTitle("⏰ Late-to-own-events report")
            .WithColor(result.TimesLate > 0 || result.NoShows > 0 ? Color.Orange : Color.Green)
            .WithDescription(
                $"Across **{result.TotalOwnEvents}** event(s) they organized or hosted in the last " +
                $"**{_config.GetActivityRetentionDays()} days** (the voice history the bot keeps), " +
                $"showing up later than **{grace} min** after start counts as late.")
            .AddField("🔴 Late", $"{result.TimesLate}{pct}", true)
            .AddField("🟢 On time", result.OnTime.ToString(), true)
            .AddField("⚪ No-shows", result.NoShows.ToString(), true);

        if (result.LateEvents.Count > 0)
        {
            var lines = result.LateEvents
                .Take(MaxListedEvents)
                .Select(e => $"• `{e.StartUtc:yyyy-MM-dd HH:mm}` UTC — **+{e.MinutesLate} min**" +
                             $"{(e.WasCancelled ? " *(cancelled)*" : "")} — {Truncate(e.Title, 48)}");
            var more = result.LateEvents.Count > MaxListedEvents
                ? $"\n…and {result.LateEvents.Count - MaxListedEvents} more."
                : "";
            embed.AddField("Late arrivals", string.Join("\n", lines) + more);
        }

        if (result.NoShowEvents.Count > 0)
        {
            var lines = result.NoShowEvents
                .Take(MaxListedEvents)
                .Select(e => $"• `{e.StartUtc:yyyy-MM-dd HH:mm}` UTC" +
                             $"{(e.WasCancelled ? " *(cancelled)*" : "")} — {Truncate(e.Title, 48)}");
            var more = result.NoShowEvents.Count > MaxListedEvents
                ? $"\n…and {result.NoShowEvents.Count - MaxListedEvents} more."
                : "";
            embed.AddField("No-shows", string.Join("\n", lines) + more);
        }

        embed.WithFooter(
            $"Arrival = earliest Events-category voice join, vs. scheduled start. " +
            $"Late scanned up to {MaxLateHours}h past start; cancelled slots are included.");

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    /// <summary>
    /// Core computation. Loads the target's organized/hosted events plus their
    /// voice sessions in the EVENTS category, then classifies each event by the
    /// earliest qualifying arrival relative to StartUtc + grace.
    /// </summary>
    private async Task<LateCheckResult> ComputeAsync(ulong guildId, ulong userId, int graceMinutes)
    {
        var buffer = TimeSpan.FromMinutes(_config.AutoPromotionEventBufferMinutes);
        var categoryId = _config.EventsCategoryId;
        var legacyChannelId = _config.EventsVoiceChannelId;
        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Every own (organized or hosted) event that has already started, within
        // the voice history the bot keeps; an older event has no voice data and
        // would read as a no-show. We
        // deliberately do NOT filter out Cancelled rows here: the duplicate-
        // series churn stamps real, attended events as Cancelled, so dropping
        // them silently hid genuine misses (an officer who skipped their own
        // event). Cancelled slots are instead kept and annotated below.
        var oldest = now.AddDays(-_config.GetActivityRetentionDays());
        var rows = await db.ClanEvents
            .Where(e => e.GuildId == guildId
                     && (e.OrganizerId == userId || e.HostId == userId)
                     && e.StartUtc <= now
                     && e.StartUtc >= oldest)
            .ToListAsync();

        if (rows.Count == 0)
            return new LateCheckResult();

        // Collapse the duplicate-series rows: one real event per distinct start
        // time. The representative row prefers a non-cancelled occurrence (for
        // the truest title/end), falling back to a cancelled one when that's
        // all that survives. A slot counts as cancelled only if EVERY row for
        // that start time is cancelled.
        var slots = rows
            .GroupBy(e => e.StartUtc)
            .Select(g =>
            {
                var rep = g.OrderBy(e => e.Status == ClanEventStatus.Cancelled ? 1 : 0)
                           .ThenByDescending(e => (int)e.Status)
                           .First();
                return new
                {
                    rep.StartUtc,
                    rep.EndUtc,
                    rep.Title,
                    WasCancelled = g.All(e => e.Status == ClanEventStatus.Cancelled)
                };
            })
            .OrderByDescending(s => s.StartUtc)
            .ToList();

        // Pull this member's voice sessions in the EVENTS category once, then
        // match them to each event window in memory (avoids one query/event).
        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                     && v.UserId == userId
                     && (v.CategoryId == categoryId
                         || (v.CategoryId == null && v.ChannelId == legacyChannelId)))
            .Select(v => new { v.JoinedAt, v.LeftAt })
            .ToListAsync();

        var result = new LateCheckResult { TotalOwnEvents = slots.Count };

        foreach (var evt in slots)
        {
            var winStart = evt.StartUtc - buffer;
            // Extend the scan window so a very-late arrival reads as LATE rather
            // than a no-show: reach to at least MaxLateHours past start.
            var lateHorizon = evt.StartUtc + TimeSpan.FromHours(MaxLateHours);
            var bufferedEnd = evt.EndUtc + buffer;
            var winEnd = bufferedEnd > lateHorizon ? bufferedEnd : lateHorizon;

            DateTime? firstJoin = null;
            foreach (var s in sessions)
            {
                var sessionEnd = s.LeftAt ?? now;
                // Overlaps the buffered window?
                if (s.JoinedAt < winEnd && sessionEnd > winStart)
                {
                    if (firstJoin is null || s.JoinedAt < firstJoin)
                        firstJoin = s.JoinedAt;
                }
            }

            if (firstJoin is null)
            {
                result.NoShows++;
                result.NoShowEvents.Add(new SlotOutcome(evt.Title, evt.StartUtc, 0, evt.WasCancelled));
                continue;
            }

            var minutesAfterStart = (int)Math.Round((firstJoin.Value - evt.StartUtc).TotalMinutes);
            if (minutesAfterStart > graceMinutes)
            {
                result.TimesLate++;
                result.LateEvents.Add(
                    new SlotOutcome(evt.Title, evt.StartUtc, Math.Max(0, minutesAfterStart), evt.WasCancelled));
            }
            else
            {
                result.OnTime++;
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves a human-readable name for the target. When the command supplied
    /// a live user object we use it directly; for the raw-ID path (members who
    /// already left) we fall back to KnownMembers, then the most recent
    /// attendance snapshot's name, then the bare ID.
    /// </summary>
    private async Task<string> ResolveDisplayNameAsync(ulong guildId, ulong userId, SocketUser? user)
    {
        if (user is not null)
            return (user as IGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var known = await db.KnownMembers
            .Where(k => k.GuildId == guildId && k.UserId == userId)
            .Select(k => new { k.DisplayName, k.Username })
            .FirstOrDefaultAsync();
        if (known is not null)
        {
            if (!string.IsNullOrWhiteSpace(known.DisplayName)) return known.DisplayName;
            if (!string.IsNullOrWhiteSpace(known.Username)) return known.Username;
        }

        var lastSnapshotName = await db.EventAttendances
            .Where(a => a.GuildId == guildId && a.UserId == userId && a.Username != "")
            .OrderByDescending(a => a.EventEndUtc)
            .Select(a => a.Username)
            .FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(lastSnapshotName)) return lastSnapshotName!;

        return $"User {userId}";
    }

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

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "(untitled)" : (s.Length <= max ? s : s[..(max - 1)] + "…");

    private sealed class LateCheckResult
    {
        public int TotalOwnEvents { get; set; }
        public int TimesLate { get; set; }
        public int OnTime { get; set; }
        public int NoShows { get; set; }
        public List<SlotOutcome> LateEvents { get; } = new();
        public List<SlotOutcome> NoShowEvents { get; } = new();
        public int ShowedUp => TimesLate + OnTime;
    }

    private readonly record struct SlotOutcome(
        string Title, DateTime StartUtc, int MinutesLate, bool WasCancelled);
}
