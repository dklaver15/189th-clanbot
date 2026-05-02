using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles two slash commands for manually adjusting a member's bot-tracked
/// event-attendance count at their current rank:
///   • /add-event-credit @user
///   • /remove-event-credit @user
///
/// ── Why this exists ──
/// Auto-promotion eligibility for CPL+ tiers depends on cumulative event
/// attendance, which is normally collected automatically by
/// EventAttendanceSnapshotService from voice sessions. Occasionally an
/// officer needs to nudge that count up or down — for example, to credit
/// a member who attended an event the bot couldn't observe (Discord
/// outage, voice tracking gap, etc.), or to remove an erroneously credited
/// row.
///
/// ── How it works ──
/// /add-event-credit inserts a new EventAttendance row with a sentinel
/// CalendarEventId of 0 (real CalendarEvent IDs are auto-increment positive
/// integers from EF, so 0 is unambiguous as "manual adjustment, not tied to
/// a real CalendarEvent"). The row's EventStartUtc and EventEndUtc are both
/// set to "now" so promotion math (which counts rows where
/// EventEndUtc &gt;= rankAssignedAt) sees this credit immediately.
/// AttendedMinutes is 0 since it isn't real voice-session time. Username is
/// captured from the target's live DisplayName so /attendance and other
/// readers can surface a human name even if the user later leaves the server.
///
/// /remove-event-credit deletes the most recent manual-adjustment row
/// (CalendarEventId == 0) for the target user that falls within their
/// current rank window — i.e. with EventEndUtc on/after the later of
/// AssignedAt and SeedAppliedAt. It will never remove a real (snapshotted)
/// event-attendance row; that protects historical attendance from being
/// destroyed by accident. If no manual row is present at the current rank,
/// the command refuses with an explanatory message.
///
/// ── Seed is untouched ──
/// This handler does NOT touch RankHistory.EventsAttendedAtRankBeforeBot.
/// That field exists for the one-time spreadsheet backfill and shouldn't
/// drift after seeding. All adjustments here happen on the EventAttendance
/// table side.
///
/// ── Permissions ──
/// Same gate model as /promote and /demote, but with a higher floor:
/// caller must be CPT or above (or a server Administrator).
/// </summary>
public class EventCreditCommandHandler
{
    private const string AddCommandName = "add-event-credit";
    private const string RemoveCommandName = "remove-event-credit";

    /// <summary>
    /// Minimum rank required to use these commands. Hardcoded rather than
    /// reading from BotConfig so it can't drift below the intended floor.
    /// </summary>
    private const string MinRankFloor = "CPT";

    /// <summary>
    /// Sentinel value indicating an EventAttendance row is a manual
    /// adjustment, not derived from a real CalendarEvent. Real CalendarEvent
    /// IDs are EF auto-increment positive integers, so 0 is unambiguous.
    /// </summary>
    private const int ManualAdjustmentCalendarEventId = 0;

    private readonly IServiceProvider _services;
    private readonly ILogger<EventCreditCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly PromotionService _promotion;

    public EventCreditCommandHandler(
        IServiceProvider services,
        ILogger<EventCreditCommandHandler> logger,
        IOptions<BotConfig> config,
        PromotionService promotion)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
        _promotion = promotion;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not (AddCommandName or RemoveCommandName))
            return;

        try
        {
            switch (command.Data.Name)
            {
                case AddCommandName:
                    await HandleAddAsync(command);
                    break;
                case RemoveCommandName:
                    await HandleRemoveAsync(command);
                    break;
            }
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

    private async Task HandleAddAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var pre = await PreflightAsync(command);
        if (!pre.Ok) return;

        // Look up the rank info so we can surface the new total in the
        // confirmation message and log line. We require an existing rank
        // record — auto-creation is intentionally NOT done here, since the
        // command is meant for active members the bot already tracks.
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var rankRecord = await db.RankHistories
            .FirstOrDefaultAsync(r => r.GuildId == pre.Guild!.Id && r.UserId == pre.Target!.Id);

        if (rankRecord is null)
        {
            await command.FollowupAsync(
                $"❌ {pre.Target!.Mention} has no rank history yet. The bot will create one automatically once they're observed (message, voice join, or rank role change). Try again after that.",
                ephemeral: true);
            return;
        }

        var now = DateTime.UtcNow;

        // Compute pre-add total for the audit log + response.
        var preAddTotal = await ComputeTotalEventsAtRankAsync(db, rankRecord, now);

        // Insert the manual-adjustment row. EventStartUtc and EventEndUtc are
        // both set to "now" so the row is in the current rank window and
        // immediately counts toward eligibility on the next /run. Real
        // attendance rows always have EventStart < EventEnd; for a manual
        // credit, equal values are fine because nothing in the codebase reads
        // the duration off this table. Username comes from DisplayName so the
        // row carries a human-readable identifier in case the target later
        // leaves the server (matches what EventAttendanceSnapshotService does
        // for organic rows).
        db.EventAttendances.Add(new EventAttendance
        {
            GuildId         = pre.Guild!.Id,
            UserId          = pre.Target!.Id,
            CalendarEventId = ManualAdjustmentCalendarEventId,
            Username        = pre.Target.DisplayName,
            EventStartUtc   = now,
            EventEndUtc     = now,
            AttendedMinutes = 0,
            RecordedAt      = now,
        });

        await db.SaveChangesAsync();

        var postAddTotal = preAddTotal + 1;

        _logger.LogInformation(
            "Manual event credit added: {Caller} → {Target} at rank {Rank}: {Pre} → {Post}",
            pre.Caller!.Username, pre.Target.Username, rankRecord.RankName, preAddTotal, postAddTotal);

        await command.FollowupAsync(
            $"✅ Added 1 event credit to {pre.Target.Mention} at **{rankRecord.RankName}**.\n" +
            $"Total events at rank: **{preAddTotal} → {postAddTotal}**",
            ephemeral: true);
    }

    private async Task HandleRemoveAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var pre = await PreflightAsync(command);
        if (!pre.Ok) return;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var rankRecord = await db.RankHistories
            .FirstOrDefaultAsync(r => r.GuildId == pre.Guild!.Id && r.UserId == pre.Target!.Id);

        if (rankRecord is null)
        {
            await command.FollowupAsync(
                $"❌ {pre.Target!.Mention} has no rank history yet — nothing to remove.",
                ephemeral: true);
            return;
        }

        // Find the most recent manual-adjustment row in the current rank
        // window. "Current rank window" = EventEndUtc on/after the later of
        // AssignedAt and SeedAppliedAt, mirroring the cutoff used by
        // EventAttendanceHelper.CountEventsAttendedAsync. Restricting to
        // CalendarEventId == 0 ensures we never delete a real (snapshotted)
        // event-attendance row.
        var rankWindowStart = rankRecord.SeedAppliedAt.HasValue
                              && rankRecord.SeedAppliedAt.Value > rankRecord.AssignedAt
            ? rankRecord.SeedAppliedAt.Value
            : rankRecord.AssignedAt;

        var manualRow = await db.EventAttendances
            .Where(ea => ea.GuildId == pre.Guild!.Id
                      && ea.UserId == pre.Target!.Id
                      && ea.CalendarEventId == ManualAdjustmentCalendarEventId
                      && ea.EventEndUtc >= rankWindowStart)
            .OrderByDescending(ea => ea.RecordedAt)
            .FirstOrDefaultAsync();

        if (manualRow is null)
        {
            await command.FollowupAsync(
                $"❌ {pre.Target!.Mention} has no manual event credits at **{rankRecord.RankName}** to remove. " +
                "This command only removes credits previously added via `/add-event-credit` — it never deletes real attendance history.",
                ephemeral: true);
            return;
        }

        var now = DateTime.UtcNow;
        var preRemoveTotal = await ComputeTotalEventsAtRankAsync(db, rankRecord, now);

        db.EventAttendances.Remove(manualRow);
        await db.SaveChangesAsync();

        var postRemoveTotal = preRemoveTotal - 1;

        _logger.LogInformation(
            "Manual event credit removed: {Caller} → {Target} at rank {Rank}: {Pre} → {Post} (deleted EventAttendance Id={RowId})",
            pre.Caller!.Username, pre.Target!.Username, rankRecord.RankName, preRemoveTotal, postRemoveTotal, manualRow.Id);

        await command.FollowupAsync(
            $"✅ Removed 1 manual event credit from {pre.Target.Mention} at **{rankRecord.RankName}**.\n" +
            $"Total events at rank: **{preRemoveTotal} → {postRemoveTotal}**",
            ephemeral: true);
    }

    /// <summary>
    /// Result of preflight validation. Ok=true means the other three fields
    /// are populated and the caller can proceed; Ok=false means the user has
    /// already been told why the request was rejected (via FollowupAsync) and
    /// the caller should just return.
    ///
    /// Returned as a record rather than via `out` parameters because async
    /// methods cannot have out parameters in C#.
    /// </summary>
    private record PreflightResult(
        bool Ok,
        SocketGuildUser? Caller,
        SocketGuildUser? Target,
        SocketGuild? Guild);

    private static readonly PreflightResult PreflightFail =
        new(false, null, null, null);

    /// <summary>
    /// Shared validation: guild context, permission gate, and target lookup.
    /// On failure, sends an ephemeral error message and returns Ok=false.
    /// </summary>
    private async Task<PreflightResult> PreflightAsync(SocketSlashCommand command)
    {
        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return PreflightFail;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasMinRankFloor(caller))
        {
            await command.FollowupAsync(
                $"❌ You don't have permission to use this command (requires {MinRankFloor}+).",
                ephemeral: true);
            return PreflightFail;
        }

        var memberOption = command.Data.Options.FirstOrDefault(o => o.Name == "member");
        var target = memberOption?.Value as SocketGuildUser;
        if (target is null)
        {
            await command.FollowupAsync("❌ Could not find that member.", ephemeral: true);
            return PreflightFail;
        }

        if (target.IsBot)
        {
            await command.FollowupAsync("❌ Bots can't have event credits.", ephemeral: true);
            return PreflightFail;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await command.FollowupAsync("Could not determine the guild for this command.", ephemeral: true);
            return PreflightFail;
        }

        return new PreflightResult(true, caller, target, guild);
    }

    /// <summary>
    /// Computes total events at the user's current rank — same math as
    /// EventAttendanceHelper.CountEventsAttendedAsync, but expressed inline
    /// because we already have the RankHistory row in hand and don't want
    /// to look it up a second time.
    /// </summary>
    private static async Task<int> ComputeTotalEventsAtRankAsync(
        BotDbContext db, RankHistory rh, DateTime now)
    {
        var effectiveSince = rh.SeedAppliedAt.HasValue && rh.SeedAppliedAt.Value > rh.AssignedAt
            ? rh.SeedAppliedAt.Value
            : rh.AssignedAt;

        var botTracked = await db.EventAttendances
            .CountAsync(ea => ea.GuildId == rh.GuildId
                           && ea.UserId == rh.UserId
                           && ea.EventEndUtc >= effectiveSince
                           && ea.EventEndUtc <= now);

        return rh.EventsAttendedAtRankBeforeBot + botTracked;
    }

    /// <summary>
    /// CPT+ rank gate, matching the model used by /promote and /demote but
    /// with a higher floor. Administrator bypasses the rank check entirely.
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