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
///                       target, excluding cancelled events, that have already
///                       started.
///   • "Showed up"     = the target has a VoiceSession in the EVENTS category
///                       (matched by EventsCategoryId; pre-migration rows with
///                       a null CategoryId fall back to EventsVoiceChannelId)
///                       overlapping the buffered window
///                       [StartUtc - buffer, EndUtc + buffer].
///   • "Late"          = their EARLIEST qualifying join is more than the grace
///                       period (default 15 min, overridable via the grace
///                       option) after StartUtc.
///   • "No-show"       = no qualifying voice session at all — reported
///                       separately, NOT counted as late.
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
                "The member whose own events to check", isRequired: true)
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
        if (targetUser is null)
        {
            await command.FollowupAsync("⚠️ You must select a user.", ephemeral: true);
            return;
        }

        var grace = DefaultGraceMinutes;
        if (command.Data.Options.FirstOrDefault(o => o.Name == "grace")?.Value is long g)
            grace = (int)Math.Clamp(g, 0, 240);

        var displayName = (targetUser as IGuildUser)?.DisplayName
                          ?? targetUser.GlobalName ?? targetUser.Username;

        var result = await ComputeAsync(guildId, targetUser.Id, grace);

        if (result.TotalOwnEvents == 0)
        {
            await command.FollowupAsync(
                $"**{displayName}** hasn't organized or hosted any (non-cancelled) events that have happened yet.",
                ephemeral: true);
            return;
        }

        var pct = result.ShowedUp > 0
            ? $" ({100.0 * result.TimesLate / result.ShowedUp:0}% of the times they showed)"
            : "";

        var embed = new EmbedBuilder()
            .WithAuthor(displayName, targetUser.GetDisplayAvatarUrl())
            .WithTitle("⏰ Late-to-own-events report")
            .WithColor(result.TimesLate > 0 ? Color.Orange : Color.Green)
            .WithDescription(
                $"Across **{result.TotalOwnEvents}** event(s) they organized or hosted, " +
                $"showing up later than **{grace} min** after start counts as late.")
            .AddField("🔴 Late", $"{result.TimesLate}{pct}", true)
            .AddField("🟢 On time", result.OnTime.ToString(), true)
            .AddField("⚪ No-shows", result.NoShows.ToString(), true);

        if (result.LateEvents.Count > 0)
        {
            var lines = result.LateEvents
                .Take(MaxListedEvents)
                .Select(e => $"• `{e.StartUtc:yyyy-MM-dd HH:mm}` UTC — **+{e.MinutesLate} min** — {Truncate(e.Title, 50)}");
            var more = result.LateEvents.Count > MaxListedEvents
                ? $"\n…and {result.LateEvents.Count - MaxListedEvents} more."
                : "";
            embed.AddField("Late arrivals", string.Join("\n", lines) + more);
        }

        embed.WithFooter("Arrival = earliest voice join in the Events category, vs. scheduled start.");

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

        var events = await db.ClanEvents
            .Where(e => e.GuildId == guildId
                     && (e.OrganizerId == userId || e.HostId == userId)
                     && e.Status != ClanEventStatus.Cancelled
                     && e.StartUtc <= now)
            .OrderByDescending(e => e.StartUtc)
            .ToListAsync();

        if (events.Count == 0)
            return new LateCheckResult();

        // Pull this member's voice sessions in the EVENTS category once, then
        // match them to each event window in memory (avoids one query/event).
        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                     && v.UserId == userId
                     && (v.CategoryId == categoryId
                         || (v.CategoryId == null && v.ChannelId == legacyChannelId)))
            .Select(v => new { v.JoinedAt, v.LeftAt })
            .ToListAsync();

        var result = new LateCheckResult { TotalOwnEvents = events.Count };

        foreach (var evt in events)
        {
            var winStart = evt.StartUtc - buffer;
            var winEnd = evt.EndUtc + buffer;

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
                continue;
            }

            var minutesAfterStart = (int)Math.Round((firstJoin.Value - evt.StartUtc).TotalMinutes);
            if (minutesAfterStart > graceMinutes)
            {
                result.TimesLate++;
                result.LateEvents.Add(new LateEvent(evt.Title, evt.StartUtc, Math.Max(0, minutesAfterStart)));
            }
            else
            {
                result.OnTime++;
            }
        }

        return result;
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
        public List<LateEvent> LateEvents { get; } = new();
        public int ShowedUp => TimesLate + OnTime;
    }

    private readonly record struct LateEvent(string Title, DateTime StartUtc, int MinutesLate);
}
