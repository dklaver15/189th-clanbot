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
/// ── Reserve / exempt-role handling ──
/// Members holding the role named in BotConfig.ReserveRoleName (default "Reserve")
/// — and any role in BotConfig.ExemptRoles — are fully exempt from AWOL tracking.
/// They are skipped in Step 1 (no activity record is created) and Step 2 (never
/// assigned the AWOL role). The exemption is implemented by
/// BotConfig.GetExemptRolesList(), which appends ReserveRoleName to ExemptRoles.
///
/// ── Self-healing exempt cleanup (Step 2 first pass) ──
/// If an exempt member somehow ALREADY has the AWOL role when this service
/// runs (e.g. they were AWOL when an officer applied Reserve and the
/// real-time RankTrackingHandler missed it because the bot was offline), the
/// new pre-pass at the top of Step 2 strips AWOL and resolves any pending
/// AwolRecords. Without this, a Reserve+AWOL member would remain visibly
/// AWOL forever (the regular activity branch is skipped by IsExempt and
/// never reaches the "remove AWOL if active" path). RankTrackingHandler
/// handles this transition in real-time; this self-heal is the safety net
/// for missed gateway events.
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
///   • Bot was offline when removal happened via /clear-awol or activity
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

        // Resolve the HQ/AWOL channel by ID first (rename-proof), falling back
        // to a name lookup so a fresh install still works before an ID is set.
        // The name lookup is brittle — adding an emoji to the channel name
        // breaks the exact-match — which is exactly why ID takes precedence.
        SocketTextChannel? hqChannel = null;
        if (_config.HqChannelId != 0)
        {
            hqChannel = guild.GetTextChannel(_config.HqChannelId);
            if (hqChannel is null)
            {
                _logger.LogWarning(
                    "HqChannelId={ChannelId} did not resolve in guild {Guild}; " +
                    "falling back to HqChannelName='{ChannelName}'.",
                    _config.HqChannelId, guild.Name, _config.HqChannelName);
            }
        }

        hqChannel ??= guild.TextChannels.FirstOrDefault(c =>
            c.Name.Equals(_config.HqChannelName, StringComparison.OrdinalIgnoreCase));

        if (hqChannel is null)
        {
            _logger.LogWarning(
                "HQ channel could not be resolved in guild {Guild} (HqChannelId={ChannelId}, HqChannelName='{ChannelName}'). Skipping notifications.",
                guild.Name, _config.HqChannelId, _config.HqChannelName);
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
        // Counter for end-of-step summary logging on the self-heal pass.
        var selfHealedCount = 0;

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;

            // ── Self-healing exempt cleanup ──
            // If a member is exempt (Reserve / Admin / etc.) but somehow has
            // the AWOL role, strip it here. This catches cases where the
            // RankTrackingHandler real-time path missed the transition (bot
            // offline, missed gateway event, etc.). Without this, a Reserve
            // member who was AWOL before the role transition would remain
            // visibly AWOL forever — the regular activity branch below is
            // skipped by IsExempt and never reaches the "remove AWOL if
            // active" code path.
            if (IsExempt(member, exemptRoles))
            {
                var hasAwolRole = member.Roles.Any(r => r.Id == awolRole.Id);
                if (hasAwolRole)
                {
                    try
                    {
                        await member.RemoveRoleAsync(awolRole,
                            new RequestOptions { AuditLogReason = "AWOL self-heal: member is exempt (Reserve/Admin/etc.)" });

                        var exemptRoleNames = string.Join(", ",
                            member.Roles.Where(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
                                        .Select(r => r.Name));

                        _logger.LogInformation(
                            "Self-healed: removed AWOL role from exempt member {Username} ({UserId}) in {Guild}. " +
                            "Triggering exempt role(s): {ExemptRoles}",
                            member.Username, member.Id, guild.Name, exemptRoleNames);

                        await CloseAwolRecordsAndDeleteEmbedsAsync(db, guild, member, ct);

                        selfHealedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to self-heal AWOL on exempt member {Username} in {Guild}",
                            member.Username, guild.Name);
                    }
                }

                // Continue past — do not run the activity check on exempt members.
                continue;
            }

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
            var hasAwolRoleActive = member.Roles.Any(r => r.Id == awolRole.Id);

            if (meetsMessages || meetsVoice)
            {
                // Active — remove AWOL if they have it
                if (hasAwolRoleActive)
                {
                    try
                    {
                        await member.RemoveRoleAsync(awolRole);
                        _logger.LogInformation(
                            "Removed AWOL role from {Username} ({UserId}) in {Guild} — " +
                            "Messages: {Messages}, Voice: {VoiceHours:F1}h (window: {Window}d)",
                            member.Username, member.Id, guild.Name,
                            messageCount, voiceSeconds / 3600.0, userWindowDays);

                        await CloseAwolRecordsAndDeleteEmbedsAsync(db, guild, member, ct);
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
                if (!hasAwolRoleActive)
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

        if (selfHealedCount > 0)
        {
            _logger.LogInformation(
                "AWOL self-heal removed AWOL role from {Count} exempt member(s) in {Guild}",
                selfHealedCount, guild.Name);
        }

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
        var stale     = 0;

        foreach (var record in pendingNotifications)
        {
            // ── Stale-record guard (blast-radius protection) ──
            // Step 3 will post ANY pending record past the grace cutoff with no
            // upper bound, so anything that resets NotificationSent on historical
            // rows (DB restore, stray bulk UPDATE, backup rollback) would
            // otherwise broadcast weeks-old alerts to the channel all at once.
            // If a record is materially older than the legitimate notify window,
            // treat it as stale: resolve it silently and log a WARNING (not Info)
            // so the anomaly is visible without spamming officers. A normal
            // recent AWOL posts within ~1 day of the grace cutoff and is never
            // caught here. Disabled when AwolNotificationMaxAgeDays <= 0.
            if (_config.AwolNotificationMaxAgeDays > 0
                && record.AssignedAt < DateTime.UtcNow.AddDays(-_config.AwolNotificationMaxAgeDays))
            {
                record.NotificationSent = true;
                record.NotificationSentAt = DateTime.UtcNow;
                stale++;
                _logger.LogWarning(
                    "AWOL notification suppressed (stale): {Username} ({UserId}) assigned {AssignedAt:yyyy-MM-dd HH:mm} UTC is older than the {MaxAge}d notify window. " +
                    "Resolved without posting. If many fire at once, a flag reset (restore / bulk UPDATE) likely re-queued historical records.",
                    record.Username, record.UserId, record.AssignedAt, _config.AwolNotificationMaxAgeDays);
                continue;
            }

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
            // removal in Discord UI, /clear-awol, mass cleanup, etc.). In
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
                // User field layout note: lead with the bold display name (so the
                // reviewing officer sees "SSG.GRAVESTARR" at a glance), then the
                // raw Discord username in parens for cross-referencing the roster
                // sheet, then the mention pill at the end. The mention is what's
                // tappable on mobile to open the profile — leading with it caused
                // "You don't have access to this link" errors on iOS when the
                // raw user-ID link was tapped instead of an in-server mention.
                .AddField("User", $"**[{member.DisplayName}](https://discord.com/users/{member.Id})** ({member.Username})")                .AddField("AWOL Since", record.AssignedAt.ToString("yyyy-MM-dd HH:mm UTC"), inline: true)
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
                var posted = await hqChannel.SendMessageAsync(embed: embed.Build());
                record.NotificationSent = true;
                record.NotificationSentAt = DateTime.UtcNow;
                record.NotificationChannelId = hqChannel.Id;
                record.NotificationMessageId = posted.Id;
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
                "AWOL Step 3 summary for {Guild}: notified={Notified}, given up={GivenUp}, stale suppressed={Stale}, role gone={RoleGone}, user gone={UserGone}, failed={Failed} (out of {Total} pending)",
                guild.Name, notified, givenUp, stale, roleGone, userGone, failed, pendingNotifications.Count);
        }
    }

    /// <summary>
    /// Closes any open AWOL records for a member who is no longer AWOL — whether
    /// they recovered through activity or were self-healed via an exempt role —
    /// and deletes the HQ notification embed if one was posted. The bot posted
    /// the embed itself, so removing it needs no Manage Messages permission.
    ///
    /// Idempotent: records with no stored message ID are simply marked resolved,
    /// a message that is already gone (manually deleted, or cleared by
    /// /clear-awol-list) is treated as success, and the message IDs are nulled
    /// after deletion so a subsequent cycle never retries.
    /// </summary>
    private async Task CloseAwolRecordsAndDeleteEmbedsAsync(
        BotDbContext db, SocketGuild guild, SocketGuildUser member, CancellationToken ct)
    {
        // Catch both pending records (embed not yet posted: NotificationSent
        // false, no message ID) and posted records (NotificationSent true with a
        // stored message ID). The OR covers both states.
        var openRecords = await db.AwolRecords
            .Where(r => r.GuildId == guild.Id
                     && r.UserId == member.Id
                     && (!r.NotificationSent || r.NotificationMessageId != null))
            .ToListAsync(ct);

        foreach (var record in openRecords)
        {
            if (record.NotificationMessageId.HasValue
                && record.NotificationChannelId.HasValue)
            {
                try
                {
                    var notifChannel = guild.GetTextChannel(record.NotificationChannelId.Value);
                    if (notifChannel is not null)
                    {
                        await notifChannel.DeleteMessageAsync(record.NotificationMessageId.Value);
                        _logger.LogInformation(
                            "Deleted AWOL notification embed for {Username} ({UserId}) in #{Channel} on AWOL clear",
                            member.Username, member.Id, notifChannel.Name);
                    }
                }
                catch (Discord.Net.HttpException ex)
                    when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already gone — manually deleted, or removed by
                    // /clear-awol-list. Nothing to do.
                    _logger.LogInformation(
                        "AWOL embed for {Username} ({UserId}) was already gone on AWOL clear",
                        member.Username, member.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to delete AWOL embed for {Username} ({UserId}) on AWOL clear (message {MessageId})",
                        member.Username, member.Id, record.NotificationMessageId.Value);
                }

                record.NotificationChannelId = null;
                record.NotificationMessageId = null;
            }

            record.NotificationSent = true;
            record.NotificationSentAt = DateTime.UtcNow;
        }
    }

    private static bool IsExempt(SocketGuildUser member, List<string> exemptRoles)
    {
        return member.Roles.Any(r =>
            exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}