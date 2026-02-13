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
/// </summary>
public class AwolCheckService : BackgroundService
{
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

        _logger.LogInformation("AWOL check service started. Running every {Interval} minutes.",
            _config.CheckIntervalMinutes);

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

            var voiceSeconds = await GetVoiceSecondsAsync(db, guild.Id, member.Id, windowStart, ct);

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

        foreach (var record in pendingNotifications)
        {
            var member = guild.GetUser(record.UserId);
            var activityWindowDays = member is not null
                ? _config.GetWindowDaysForRoles(member.Roles.Select(r => r.Name))
                : _config.WindowDays;
            var windowStart = DateTime.UtcNow.AddDays(-activityWindowDays);

            var messageCount = await db.MessageEvents
                .CountAsync(m => m.GuildId == guild.Id
                              && m.UserId == record.UserId
                              && m.Timestamp >= windowStart, ct);

            var voiceSeconds = await GetVoiceSecondsAsync(db, guild.Id, record.UserId, windowStart, ct);

            var embed = new EmbedBuilder()
                .WithTitle("⚠️ AWOL Member — Ready for Review")
                .WithColor(Color.Red)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField("User", member is not null
                    ? $"{member.Mention} ({member.Username})"
                    : $"{record.Username} (ID: {record.UserId})")
                .AddField("AWOL Since", record.AssignedAt.ToString("yyyy-MM-dd HH:mm UTC"), inline: true)
                .AddField("Grace Period Expired",
                    record.AssignedAt.AddDays(_config.AwolGraceDays).ToString("yyyy-MM-dd HH:mm UTC"),
                    inline: true)
                .AddField($"Messages ({activityWindowDays}d)", messageCount.ToString(), inline: true)
                .AddField($"Voice Time ({activityWindowDays}d)",
                    $"{voiceSeconds / 3600.0:F1} hours", inline: true);

            if (member is not null)
            {
                embed.AddField("Joined Server",
                    member.JoinedAt?.ToString("yyyy-MM-dd") ?? "Unknown", inline: true);
                embed.AddField("Roles",
                    string.Join(", ", member.Roles
                        .Where(r => !r.IsEveryone)
                        .Select(r => r.Name)));
            }

            embed.WithFooter("ClanGuard Bot • Use server moderation tools to take action");

            try
            {
                await hqChannel.SendMessageAsync(embed: embed.Build());
                record.NotificationSent = true;
                record.NotificationSentAt = DateTime.UtcNow;

                _logger.LogInformation("Posted AWOL notification for {Username} in {Guild} #{Channel}",
                    record.Username, guild.Name, hqChannel.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send AWOL notification for {Username}", record.Username);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Calculates total voice seconds within the window, including any currently-open session.
    /// </summary>
    private static async Task<long> GetVoiceSecondsAsync(
        BotDbContext db, ulong guildId, ulong userId, DateTime windowStart, CancellationToken ct)
    {
        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                        && v.UserId == userId
                        && v.JoinedAt >= windowStart)
            .ToListAsync(ct);

        long totalSeconds = 0;
        foreach (var session in sessions)
        {
            var end = session.LeftAt ?? DateTime.UtcNow;
            var start = session.JoinedAt < windowStart ? windowStart : session.JoinedAt;
            totalSeconds += (long)(end - start).TotalSeconds;
        }

        // Also check for sessions that started before the window but are still open or ended within it
        var overlapping = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                        && v.UserId == userId
                        && v.JoinedAt < windowStart
                        && (v.LeftAt == null || v.LeftAt > windowStart))
            .ToListAsync(ct);

        foreach (var session in overlapping)
        {
            var end = session.LeftAt ?? DateTime.UtcNow;
            totalSeconds += (long)(end - windowStart).TotalSeconds;
        }

        return totalSeconds;
    }

    private static bool IsExempt(SocketGuildUser member, List<string> exemptRoles)
    {
        return member.Roles.Any(r =>
            exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}
