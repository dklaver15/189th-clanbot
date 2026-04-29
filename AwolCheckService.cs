using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Background service that periodically:
///   1. Resets expired activity windows (rolling 28 days)
///   2. Checks for users below activity thresholds and assigns the AWOL role
///   3. Posts HQ notifications for users who've been AWOL for 2+ days
///
/// Voice time is capped per-session via MaxSingleSessionHours so stuck or orphaned
/// voice sessions cannot inflate a user's "active" status.
///
/// ── Reserve exemption ──
/// Members holding the role named in BotConfig.ReserveRoleName (default "Reserve")
/// are fully exempt from AWOL tracking. They are skipped in Step 1 (no activity
/// record is created), Step 2 (never assigned the AWOL role), and effectively
/// Step 3 (no AWOL record exists to notify on). The exemption is implemented by
/// BotConfig.GetExemptRolesList(), which appends ReserveRoleName to the regular
/// ExemptRoles list — so anyone reading this code sees a single uniform exempt
/// check, while the config keeps Reserve as a distinct named concept rather than
/// burying it in ExemptRoles. The /kick-awols command performs an additional
/// explicit Reserve check as belt-and-suspenders defense in case the role was
/// added between AWOL assignment and kick.
///
/// ── Notification retry policy ──
/// Notifications can fail for legitimate reasons (channel temporarily
/// unreachable, bot lost permission, network blip). On failure, we stamp
/// LastNotificationAttemptUtc and let the next cycle try again — no
/// give-up after a few attempts, because some failures resolve themselves
/// within a day or two.
///
/// However, if a record has been failing to notify for more than
/// NotificationGiveUpWindowDays, we mark it as resolved ("given up") so
/// it stops occupying the pending-notification queue forever. The
/// 2026-04-25 backlog of 156 stale records from February onward — caused
/// by a permissions issue on #awol-list that was only fixed months later
/// — was the motivating case. With a give-up window in place, those
/// records would have been auto-resolved within a week instead of piling
/// up indefinitely.
///
/// If the underlying issue is fixed and the user is still AWOL, the next
/// cycle's Step 2 will see no pending record and create a fresh one.
/// Given-up records are NOT deleted; they're simply marked NotificationSent=true
/// with a synthetic NotificationSentAt timestamp, so audit history is
/// preserved.
///
/// ── Role-removed cleanup ──
/// Step 2 already handles "user got active again" — when activity rises
/// above threshold, the bot removes the AWOL role and resolves any pending
/// records. But the role can also be removed externally:
///   • Officer manually removes via Discord UI
///   • User leaves and rejoins the server
///   • Mass role cleanup during a clan event
///   • Bot was offline when removal happened via /awol-exempt or activity
///
/// In any of these cases, the AwolRecord would otherwise stay pending
/// forever. Step 3 now also checks "does the user still have the AWOL role?"
/// before attempting notification; if not, the record is resolved as
/// "role no longer present" and notification is skipped.
/// </summary>
public class AwolCheckService : BackgroundService
{
    /// <summary>
    /// Records older than this with at least one failed notification attempt
    /// are considered "given up." See class doc for rationale.
    /// </summary>
    private static readonly TimeSpan NotificationGiveUpWindow = TimeSpan.FromDays(7);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<AwolCheckService> _logger;
    private readonly BotConfig _config;

    public AwolCheckService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<AwolCheckService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until the Discord client is fully connected and has guild data
        while (_client.ConnectionState != ConnectionState.Connected ||
               !_client.Guilds.Any())
        {
            _logger.LogInformation("Waiting for Discord client to be ready...");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        // Give guilds a moment to finish downloading members
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        // Log the effective exempt roles list at startup so it's visible in logs
        // that Reserve (and any other configured exemptions) are being honored.
        var exemptList = _config.GetExemptRolesList();
        _logger.LogInformation(
            "AWOL check service started. Running every {Interval} minutes. " +
            "NotificationGiveUpWindow={GiveUp}d. Exempt roles ({Count}): {Roles}",
            _config.CheckIntervalMinutes,
            (int)NotificationGiveUpWindow.TotalDays,
            exemptList.Count,
            string.Join(", ", exemptList));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunChecksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during AWOL check cycle");
            }

            await Task.Delay(TimeSpan.FromMinutes(_config.CheckIntervalMinutes), stoppingToken);
        }
    }

    private async Task RunChecksAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting AWOL check cycle at {Time}", DateTime.UtcNow);

        foreach (var guild in _client.Guilds)
        {
            try
            {
                await ProcessGuildAsync(guild, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing guild {GuildName} ({GuildId})",
                    guild.Name, guild.Id);
            }
        }

        // Prune events older than the longest window + a buffer
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var pruneDate = DateTime.UtcNow.AddDays(-(_config.WindowDays + 7));

            var oldMessages = await db.MessageEvents
                .Where(m => m.Timestamp < pruneDate)
                .ExecuteDeleteAsync(ct);

            var oldSessions = await db.VoiceSessions
                .Where(v => v.LeftAt != null && v.LeftAt < pruneDate)
                .ExecuteDeleteAsync(ct);

            if (oldMessages > 0 || oldSessions > 0)
                _logger.LogInformation("Pruned {Messages} old message events and {Sessions} old voice sessions",
                    oldMessages, oldSessions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pruning old events");
        }

        _logger.LogInformation("AWOL check cycle complete");
    }

    private async Task ProcessGuildAsync(SocketGuild guild, CancellationToken ct)
    {
        await guild.DownloadUsersAsync();

        var awolRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));

        if (awolRole is null)
        {
            _logger.LogWarning("AWOL role '{RoleName}' not found in guild {Guild}. Skipping.",
                _config.AwolRoleName, guild.Name);
            return;
        }

        var hqChannel = guild.TextChannels.FirstOrDefault(c =>
            c.Name.Equals(_config.HqChannelName, StringComparison.OrdinalIgnoreCase));

        if (hqChannel is null)
        {
            _logger.LogWarning("HQ channel '{ChannelName}' not found in guild {Guild}. Skipping notifications.",
                _config.HqChannelName, guild.Name);
        }

        // GetExemptRolesList() appends ReserveRoleName, so Reserve members are
        // automatically treated as exempt for both Step 1 (activity record creation)
        // and Step 2 (AWOL role assignment).
        var exemptRoles = _config.GetExemptRolesList();
        var minVoiceSeconds = (long)(_config.MinVoiceHours * 3600);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // ------------------------------------------------------------------
        // Step 1: Ensure all guild members have an activity record
        // ------------------------------------------------------------------
        var existingUserIds = (await db.UserActivities
                .Where(a => a.GuildId == guild.Id)
                .Select(a => a.UserId)
                .ToListAsync(ct))
                .ToHashSet();

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;
            if (IsExempt(member, exemptRoles)) continue;

            if (!existingUserIds.Contains(member.Id))
            {
                db.UserActivities.Add(new UserActivity
                {
                    GuildId = guild.Id,
                    UserId = member.Id,
                    Username = member.ToString() ?? member.Username
                });
            }
        }
        await db.SaveChangesAsync(ct);

        // ------------------------------------------------------------------
        // Step 2: Check each non-exempt member's activity in their window
        // ------------------------------------------------------------------
        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;
            // Reserve members are inside exemptRoles — they will never be
            // assigned the AWOL role here. See class doc "Reserve exemption".
            if (IsExempt(member, exemptRoles)) continue;

            var userWindowDays = _config.GetWindowDaysForRoles(member.Roles.Select(r => r.Name));
            var windowStart = DateTime.UtcNow.AddDays(-userWindowDays);

            // Must have been in the server long enough for a full window
            if (member.JoinedAt.HasValue &&
                member.JoinedAt.Value.UtcDateTime > windowStart)
                continue;

            var messageCount = await db.MessageEvents
                .CountAsync(m => m.GuildId == guild.Id
                              && m.UserId == member.Id
                              && m.Timestamp >= windowStart, ct);

            var voiceSeconds = await VoiceActivityHelper.GetVoiceSecondsAsync(
                db, guild.Id, member.Id, windowStart,
                _config.MaxSingleSessionHours, ct);

            var meetsMessages = messageCount >= _config.MinMessages;
            var meetsVoice = voiceSeconds >= minVoiceSeconds;
            var hasAwolRole = member.Roles.Any(r => r.Id == awolRole.Id);

            if (meetsMessages || meetsVoice)
            {
                // Active — remove AWOL if they have it
                if (hasAwolRole)
                {
                    try
                    {
                        await member.RemoveRoleAsync(awolRole);
                        _logger.LogInformation(
                            "Removed AWOL role from {Username} ({UserId}) in {Guild} — " +
                            "Messages: {Messages}, Voice: {VoiceHours:F1}h (window: {Window}d)",
                            member.Username, member.Id, guild.Name,
                            messageCount, voiceSeconds / 3600.0, userWindowDays);

                        var pendingRecords = await db.AwolRecords
                            .Where(r => r.GuildId == guild.Id
                                     && r.UserId == member.Id
                                     && !r.NotificationSent)
                            .ToListAsync(ct);

                        foreach (var record in pendingRecords)
                        {
                            record.NotificationSent = true;
                            record.NotificationSentAt = DateTime.UtcNow;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove AWOL role from {Username} in {Guild}",
                            member.Username, guild.Name);
                    }
                }
            }
            else
            {
                // Inactive — assign AWOL if they don't have it
                if (!hasAwolRole)
                {
                    try
                    {
                        await member.AddRoleAsync(awolRole);
                        _logger.LogInformation(
                            "Assigned AWOL role to {Username} ({UserId}) in {Guild} — " +
                            "Messages: {Messages}, Voice: {VoiceHours:F1}h (window: {Window}d)",
                            member.Username, member.Id, guild.Name,
                            messageCount, voiceSeconds / 3600.0, userWindowDays);

                        var existingRecord = await db.AwolRecords
                            .FirstOrDefaultAsync(r => r.GuildId == guild.Id
                                                   && r.UserId == member.Id
                                                   && !r.NotificationSent, ct);

                        if (existingRecord is null)
                        {
                            db.AwolRecords.Add(new AwolRecord
                            {
                                GuildId = guild.Id,
                                UserId = member.Id,
                                Username = member.ToString() ?? member.Username,
                                AssignedAt = DateTime.UtcNow,
                                NotificationSent = false
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to assign AWOL role to {Username} in {Guild}",
                            member.Username, guild.Name);
                    }
                }
            }
        }
        await db.SaveChangesAsync(ct);

        // ------------------------------------------------------------------
        // Step 3: Post HQ notifications for users past the grace period
        // ------------------------------------------------------------------
        if (hqChannel is null) return;

        var graceCutoff = DateTime.UtcNow.AddDays(-_config.AwolGraceDays);
        var pendingNotifications = await db.AwolRecords
            .Where(r => r.GuildId == guild.Id
                     && !r.NotificationSent
                     && r.AssignedAt <= graceCutoff)
            .ToListAsync(ct);

        // Counters for end-of-step summary logging.
        var notified  = 0;
        var givenUp   = 0;
        var roleGone  = 0;
        var userGone  = 0;
        var failed    = 0;

        foreach (var record in pendingNotifications)
        {
            // ── Give-up check ──
            // If we've been trying for more than NotificationGiveUpWindow and
            // still haven't succeeded, mark the record resolved with a
            // synthetic NotificationSentAt and a log line so it's traceable.
            // Only applies after at least one attempt has been recorded —
            // otherwise a backlog from a long bot outage would all
            // immediately give up without ever being tried.
            var giveUpCutoff = DateTime.UtcNow - NotificationGiveUpWindow;
            if (record.LastNotificationAttemptUtc.HasValue
                && record.AssignedAt < giveUpCutoff)
            {
                record.NotificationSent = true;
                record.NotificationSentAt = DateTime.UtcNow;
                givenUp++;
                _logger.LogInformation(
                    "AWOL notification given up for {Username} ({UserId}) — assigned {AssignedAt:yyyy-MM-dd HH:mm} UTC, last attempt {LastAttempt:yyyy-MM-dd HH:mm} UTC, exceeded {Window}d retry window",
                    record.Username, record.UserId,
                    record.AssignedAt, record.LastNotificationAttemptUtc.Value,
                    (int)NotificationGiveUpWindow.TotalDays);
                continue;
            }

            // ── Role-removed cleanup ──
            // The user might have left the guild, or had their AWOL role
            // removed by a path that doesn't touch the AwolRecord (manual
            // removal in Discord UI, /awol-exempt, mass cleanup, etc.). In
            // either case the pending record is moot — close it and skip
            // notification so we don't surface stale "AWOL Member — Ready
            // for Review" embeds for users who aren't actually AWOL anymore.
            var member = guild.GetUser(record.UserId);
            if (member is null)
            {
                record.NotificationSent = true;
                record.NotificationSentAt = DateTime.UtcNow;
                userGone++;
                _logger.LogInformation(
                    "AWOL notification skipped for {Username} ({UserId}): user no longer in guild",
                    record.Username, record.UserId);
                continue;
            }

            var hasAwolRole = member.Roles.Any(r => r.Id == awolRole.Id);
            if (!hasAwolRole)
            {
                record.NotificationSent = true;
                record.NotificationSentAt = DateTime.UtcNow;
                roleGone++;
                _logger.LogInformation(
                    "AWOL notification skipped for {Username} ({UserId}): AWOL role no longer present",
                    record.Username, record.UserId);
                continue;
            }

            // ── Build the notification embed ──
            var activityWindowDays =
                _config.GetWindowDaysForRoles(member.Roles.Select(r => r.Name));
            var windowStart = DateTime.UtcNow.AddDays(-activityWindowDays);

            var messageCount = await db.MessageEvents
                .CountAsync(m => m.GuildId == guild.Id
                              && m.UserId == record.UserId
                              && m.Timestamp >= windowStart, ct);

            var voiceSeconds = await VoiceActivityHelper.GetVoiceSecondsAsync(
                db, guild.Id, record.UserId, windowStart,
                _config.MaxSingleSessionHours, ct);

            var embed = new EmbedBuilder()
                .WithTitle("⚠️ AWOL Member — Ready for Review")
                .WithColor(Color.Red)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField("User", $"{member.Mention} ({member.Username})")
                .AddField("AWOL Since", record.AssignedAt.ToString("yyyy-MM-dd HH:mm UTC"), inline: true)
                .AddField("Grace Period Expired",
                    record.AssignedAt.AddDays(_config.AwolGraceDays).ToString("yyyy-MM-dd HH:mm UTC"),
                    inline: true)
                .AddField($"Messages ({activityWindowDays}d)", messageCount.ToString(), inline: true)
                .AddField($"Voice Time ({activityWindowDays}d)",
                    $"{voiceSeconds / 3600.0:F1} hours", inline: true)
                .AddField("Joined Server",
                    member.JoinedAt?.ToString("yyyy-MM-dd") ?? "Unknown", inline: true)
                .AddField("Roles",
                    string.Join(", ", member.Roles
                        .Where(r => !r.IsEveryone)
                        .Select(r => r.Name)))
                .WithFooter("ClanGuard Bot • Use server moderation tools to take action");

            // Stamp the attempt timestamp BEFORE posting, so a retry policy can
            // reason about "we tried" even if SendMessageAsync throws and the
            // catch block runs. SaveChangesAsync at the end of the loop persists
            // both success and failure attempts.
            record.LastNotificationAttemptUtc = DateTime.UtcNow;

            try
            {
                await hqChannel.SendMessageAsync(embed: embed.Build());
                record.NotificationSent = true;
                record.NotificationSentAt = DateTime.UtcNow;
                notified++;

                _logger.LogInformation("Posted AWOL notification for {Username} in {Guild} #{Channel}",
                    record.Username, guild.Name, hqChannel.Name);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Failed to send AWOL notification for {Username}", record.Username);
            }
        }

        await db.SaveChangesAsync(ct);

        if (pendingNotifications.Count > 0)
        {
            _logger.LogInformation(
                "AWOL Step 3 summary for {Guild}: notified={Notified}, given up={GivenUp}, role gone={RoleGone}, user gone={UserGone}, failed={Failed} (out of {Total} pending)",
                guild.Name, notified, givenUp, roleGone, userGone, failed, pendingNotifications.Count);
        }
    }

    private static bool IsExempt(SocketGuildUser member, List<string> exemptRoles)
    {
        return member.Roles.Any(r =>
            exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}