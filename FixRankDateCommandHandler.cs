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
/// /fix-rank-date — repairs a member whose rank-assigned date was reset by
/// their rank role coming off and going back on.
///
/// ── What goes wrong ──
/// A member holding no rank role at all has their RankHistory row deleted by
/// <see cref="RankTrackingHandler"/>; putting the role back writes a fresh row
/// dated now. Their time-in-rank restarts and every activity total at that rank
/// reads zero, because MessageEvents, <see cref="VoiceActivityHelper"/> and
/// <see cref="EventAttendanceHelper"/> all filter on <c>&gt;= AssignedAt</c>.
/// /promo-eligibility and the nightly auto-promotion both believe it.
///
/// RankTrackingHandler now repairs this automatically at the moment the role
/// returns (see its TryResolveRestoredAssignmentAsync). This command is for the
/// members it already happened to, and for losses older than
/// BotConfig.RankRestoreWindowDays, which the automatic path deliberately won't
/// reach back to on its own.
///
/// ── Evidence-based, not a date picker ──
/// There is no "set the date to whatever I say" option, on purpose. The new
/// date comes from the append-only <see cref="RankChange"/> log via
/// <see cref="RankRestoreHelper"/> — the same walk the automatic repair uses —
/// so the command can only ever restore a date the bot itself recorded, and
/// only when the log actually shows this rank being lost and regained. A
/// promotion also passes through a rank-less moment, and the helper is built to
/// not match those; see its docs. If an officer needs to assert a date the bot
/// never observed, that is a deliberate DB edit, not this command.
///
/// ── Dry-run by default ──
/// Same model as /gamertag-backfill-ids: the command reports what it would
/// change and only writes when invoked with <c>apply:true</c>. The preview
/// shows the activity totals that come back, because that is the thing the
/// officer is actually trying to confirm.
///
/// ── Permissions ──
/// CPT+ or Administrator, matching /add-event-credit — this moves the same
/// promotion math that command does.
/// </summary>
public sealed class FixRankDateCommandHandler
{
    public const string CommandName = "fix-rank-date";

    /// <summary>
    /// Minimum rank required. Hardcoded rather than read from BotConfig so it
    /// can't drift below the intended floor.
    /// SyncWithHandlers: EventCreditCommandHandler.MinRankFloor.
    /// </summary>
    private const string MinRankFloor = "CPT";

    private readonly IServiceProvider _services;
    private readonly ILogger<FixRankDateCommandHandler> _logger;
    private readonly BotConfig _config;

    public FixRankDateCommandHandler(
        IServiceProvider services,
        ILogger<FixRankDateCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Restore a member's rank-assigned date after their rank role was removed and re-added (CPT+)")
            .AddOption("member", ApplicationCommandOptionType.User,
                "The member whose rank date was reset", isRequired: true)
            .AddOption("apply", ApplicationCommandOptionType.Boolean,
                "Actually write the date. Leave off for a dry-run preview.", isRequired: false)
            .Build();

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            await HandleAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", CommandName);
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleAsync(SocketSlashCommand command)
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

        var target = command.Data.Options
            .FirstOrDefault(o => o.Name == "member")?.Value as SocketGuildUser;
        if (target is null)
        {
            await command.FollowupAsync("❌ Could not find that member.", ephemeral: true);
            return;
        }

        if (target.IsBot)
        {
            await command.FollowupAsync("❌ Bots don't hold ranks.", ephemeral: true);
            return;
        }

        var apply = (bool)(command.Data.Options
            .FirstOrDefault(o => o.Name == "apply")?.Value ?? false);

        var guildId = command.GuildId.Value;
        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var rankRecord = await db.RankHistories
            .FirstOrDefaultAsync(r => r.GuildId == guildId && r.UserId == target.Id);

        if (rankRecord is null)
        {
            // Nothing to repair: with no row, promotion math already falls back
            // to the join date rather than to a reset timestamp, and
            // RankReconcileService creates the row on its next pass.
            await command.FollowupAsync(
                $"⚠️ {target.Mention} has no RankHistory row, so there's no date to fix. " +
                $"Promotion math is falling back to their join date, and the rank reconciler " +
                $"will create the row on its next pass.",
                ephemeral: true);
            return;
        }

        // window: null — an officer naming a specific member has already made
        // the call that this repair is warranted, so unlike the automatic path
        // there's no cutoff. See RankRestoreHelper's `window` docs.
        var result = await RankRestoreHelper.ResolveOriginalAssignmentAsync(
            db, guildId, target.Id, rankRecord.RankName, now,
            window: null,
            joinedAt: target.JoinedAt?.UtcDateTime);

        if (result.OriginalAssignedAt is null)
        {
            await command.FollowupAsync(
                embed: BuildNothingToDoEmbed(target, rankRecord, result),
                ephemeral: true);
            return;
        }

        var restoredAt = result.OriginalAssignedAt.Value;

        // Guard against a no-op write. Also catches a second run of the command
        // on a member who was already repaired.
        if (Math.Abs((restoredAt - rankRecord.AssignedAt).TotalSeconds) < 1)
        {
            await command.FollowupAsync(
                $"✅ {target.Mention}'s rank-assigned date already matches the rank-change log " +
                $"({Timestamp(rankRecord.AssignedAt, 'F')}). Nothing to fix.",
                ephemeral: true);
            return;
        }

        // Totals as they read now vs. as they'd read from the restored date.
        var before = await ReadActivityAsync(db, guildId, target.Id, rankRecord, rankRecord.AssignedAt);
        var after = await ReadActivityAsync(db, guildId, target.Id, rankRecord, restoredAt);

        if (apply)
        {
            rankRecord.AssignedAt = restoredAt;
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "/fix-rank-date: {Caller} repaired {Target} ({TargetId}) at rank {Rank} — " +
                "AssignedAt {Old:o} → {New:o} (lost at {LostAt:o}). Activity at rank now " +
                "{Msgs} msgs / {Voice:F1}h voice / {Events} events.",
                caller.Username, target.Username, target.Id, rankRecord.RankName,
                before.Since, restoredAt, result.LostAt,
                after.Messages, after.VoiceHours, after.Events);
        }

        await command.FollowupAsync(
            embed: BuildResultEmbed(target, rankRecord, restoredAt, result, before, after, apply),
            ephemeral: true);
    }

    /// <summary>
    /// The embed for "the log doesn't support a repair." Says which of the
    /// three reasons applies, because they call for different follow-ups.
    /// </summary>
    private Embed BuildNothingToDoEmbed(
        SocketGuildUser target, RankHistory rankRecord, RankRestoreHelper.Result result)
    {
        var embed = new EmbedBuilder()
            .WithAuthor(target.GlobalName ?? target.Username, target.GetDisplayAvatarUrl())
            .WithColor(Color.LightGrey)
            .WithTitle("⚪ Nothing to fix")
            .AddField("Current Rank", rankRecord.RankName, inline: true)
            .AddField("Rank Assigned", Timestamp(rankRecord.AssignedAt, 'F'), inline: true);

        if (result.RejectedByJoinDate)
        {
            embed.WithDescription(
                $"The rank-change log does show {target.Mention} losing and regaining " +
                $"**{rankRecord.RankName}**, but the original assignment predates their current " +
                $"membership — they left and rejoined since. Restoring it would hand them tenure " +
                $"from a previous stint, so it's refused. Their current date stands.");
        }
        else if (result.LostAt is not null)
        {
            embed.WithDescription(
                $"A **{rankRecord.RankName}** loss is on record at {Timestamp(result.LostAt.Value, 'F')}, " +
                $"but the log doesn't carry an earlier assignment of that rank to restore from — " +
                $"the bot never observed them receiving it. Their current date stands.");
        }
        else
        {
            embed.WithDescription(
                $"The rank-change log shows no sign of {target.Mention} losing and regaining " +
                $"**{rankRecord.RankName}**, so their rank-assigned date is the real one. " +
                $"If it still looks wrong, check `/timeline` — a promotion or demotion resets " +
                $"the clock by design, and that is not something this command undoes.");
        }

        return embed.WithCurrentTimestamp().Build();
    }

    private Embed BuildResultEmbed(
        SocketGuildUser target,
        RankHistory rankRecord,
        DateTime restoredAt,
        RankRestoreHelper.Result result,
        ActivitySnapshot before,
        ActivitySnapshot after,
        bool applied)
    {
        var embed = new EmbedBuilder()
            .WithAuthor(target.GlobalName ?? target.Username, target.GetDisplayAvatarUrl())
            .WithColor(applied ? Color.Green : Color.Gold)
            .WithTitle(applied ? "✅ Rank date restored" : "🔍 Dry run — nothing written")
            .WithDescription(applied
                ? $"{target.Mention}'s rank-assigned date has been restored from the rank-change log."
                : $"{target.Mention}'s rank-assigned date can be restored from the rank-change log. " +
                  $"Re-run with `apply:true` to write it.")
            .AddField("Current Rank", rankRecord.RankName, inline: true)
            .AddField("Lost At",
                result.LostAt is not null ? Timestamp(result.LostAt.Value, 'F') : "—", inline: true)
            .AddField(applied ? "Was" : "Currently",
                $"{Timestamp(before.Since, 'F')}\n{DaysInRank(before.Since)} in rank",
                inline: false)
            .AddField(applied ? "Now" : "Would become",
                $"{Timestamp(restoredAt, 'F')}\n{DaysInRank(restoredAt)} in rank",
                inline: false)
            .AddField("Activity at rank",
                $"💬 {before.Messages} → **{after.Messages}** messages\n" +
                $"🔊 {before.VoiceHours:F1}h → **{after.VoiceHours:F1}h** voice\n" +
                $"🎯 {before.Events} → **{after.Events}** events",
                inline: false);

        if (rankRecord.EventsAttendedAtRankBeforeBot == 0)
        {
            embed.AddField("ℹ️ Seed credit",
                "Seed events aren't recoverable — they lived only on the deleted row and aren't in " +
                "the rank-change log. This member currently shows a seed of 0. If they had " +
                "spreadsheet credit at this rank, clear their \"Seed Applied\" cell and re-run " +
                "`/seed-promotion-credit`.",
                inline: false);
        }

        embed.WithFooter(applied
            ? "Auto-promotion will read the restored date on its next run."
            : "Nothing was written. Add apply:true to commit.");

        return embed.WithCurrentTimestamp().Build();
    }

    /// <summary>Activity totals at a rank, as of a candidate assignment date.</summary>
    private record ActivitySnapshot(DateTime Since, int Messages, double VoiceHours, int Events);

    /// <summary>
    /// Reads the three promotion inputs against a candidate <paramref name="since"/>.
    /// Uses the same primitives as AutoPromotionService and /promo-eligibility —
    /// MaxSingleSessionHours cap included — so the preview matches what the
    /// nightly job will see once the date is written.
    /// </summary>
    private async Task<ActivitySnapshot> ReadActivityAsync(
        BotDbContext db, ulong guildId, ulong userId, RankHistory rankRecord, DateTime since)
    {
        var messages = await db.MessageEvents
            .CountAsync(m => m.GuildId == guildId && m.UserId == userId && m.Timestamp >= since);

        var voiceSeconds = await VoiceActivityHelper.GetVoiceSecondsAsync(
            db, guildId, userId, since, _config.MaxSingleSessionHours);

        var events = await EventAttendanceHelper.CountEventsAttendedAsync(
            db, guildId, userId, since,
            rankRecord.SeedAppliedAt,
            rankRecord.EventsAttendedAtRankBeforeBot);

        return new ActivitySnapshot(since, messages, voiceSeconds / 3600.0, events);
    }

    private static string DaysInRank(DateTime since)
    {
        var days = (int)(DateTime.UtcNow - since).TotalDays;
        return days == 1 ? "1 day" : $"{days} days";
    }

    private static string Timestamp(DateTime utc, char style) =>
        $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds()}:{style}>";

    /// <summary>
    /// CPT+ rank gate, matching /add-event-credit. Administrator bypasses the
    /// rank check entirely.
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
