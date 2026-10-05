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
/// One-time startup service that backfills MessageEvents from Discord channel history.
/// Runs once, then stops. Skips messages already recorded.
///
/// ── Permission filtering ──
/// Many guilds have intentionally-restricted channels (officer-only, staff-only,
/// archive-only) that the bot can't read. Without pre-filtering, every startup
/// would attempt GetMessagesAsync on each restricted channel, fail with HTTP
/// 50001 "Missing Access", and log a warning per channel. That noise makes the
/// startup log hard to scan and obscures real issues.
///
/// We pre-check ViewChannel + ReadMessageHistory on each channel before
/// attempting backfill. Skipped channels get a debug-level log line so they
/// remain auditable when LogLevel is set to Debug, but stay invisible at the
/// default Information level. We still keep the try/catch around BackfillChannelAsync
/// because a channel CAN have its permissions change between the pre-check and
/// the API call (rare but possible) — the catch handles that race cleanly.
/// </summary>
public class HistoryBackfillService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<HistoryBackfillService> _logger;
    private readonly BotConfig _config;

    public HistoryBackfillService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<HistoryBackfillService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for Discord to be ready
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        // Determine cutoff: last recorded message (with 1-min overlap) or full window
        DateTime cutoff;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var latestTimestamp = await db.MessageEvents
                .OrderByDescending(m => m.Timestamp)
                .Select(m => (DateTime?)m.Timestamp)
                .FirstOrDefaultAsync(stoppingToken);

            if (latestTimestamp.HasValue)
            {
                cutoff = latestTimestamp.Value.AddMinutes(-1); // small overlap to avoid gaps
                _logger.LogInformation("Incremental backfill from {Cutoff}", cutoff);
            }
            else
            {
                cutoff = DateTime.UtcNow.AddDays(-_config.WindowDays);
                _logger.LogInformation("Full backfill: last {Days} days", _config.WindowDays);
            }
        }

        foreach (var guild in _client.Guilds)
        {
            _logger.LogInformation("Backfilling guild {Guild}...", guild.Name);

            // Resolve the bot's effective permissions per channel via its guild user.
            // CurrentUser may briefly be null if gateway state is still warming up;
            // if so, fall back to attempting every channel (old behavior) rather
            // than skipping all of them.
            var botMember = guild.CurrentUser;

            var attempted = 0;
            var skippedNoPerms = 0;

            foreach (var channel in guild.TextChannels)
            {
                if (botMember is not null && !BotCanRead(botMember, channel))
                {
                    skippedNoPerms++;
                    _logger.LogDebug(
                        "Skipping backfill for #{Channel} in {Guild}: bot lacks ViewChannel or ReadMessageHistory",
                        channel.Name, guild.Name);
                    continue;
                }

                attempted++;
                try
                {
                    await BackfillChannelAsync(channel, guild.Id, cutoff, stoppingToken);
                }
                catch (Exception ex)
                {
                    // Defensive: handles the rare case where permissions change
                    // between the pre-check and the API call, or where the
                    // pre-check missed something the API enforces.
                    _logger.LogWarning(ex, "Could not backfill #{Channel} in {Guild}",
                        channel.Name, guild.Name);
                }
            }

            _logger.LogInformation(
                "Backfill scan for {Guild}: attempted={Attempted}, skipped (no perms)={Skipped}",
                guild.Name, attempted, skippedNoPerms);
        }

        _logger.LogInformation("Message history backfill complete!");
    }

    /// <summary>
    /// Returns true if the bot has both ViewChannel and ReadMessageHistory on
    /// the given text channel. Both are required for GetMessagesAsync to
    /// succeed; missing either produces a 50001 "Missing Access" from Discord.
    /// </summary>
    private static bool BotCanRead(SocketGuildUser botMember, SocketTextChannel channel)
    {
        var perms = botMember.GetPermissions(channel);
        return perms.ViewChannel && perms.ReadMessageHistory;
    }

    private async Task BackfillChannelAsync(
        SocketTextChannel channel, ulong guildId, DateTime cutoff, CancellationToken ct)
    {
        var count = 0;
        var skipped = 0;
        var batch = new List<MessageEvent>();

        // ── load existing event keys for dedup ──────────────────────
        HashSet<(ulong userId, long ticks)> existing;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            existing = (await db.MessageEvents
                .Where(m => m.GuildId == guildId && m.Timestamp >= cutoff)
                .Select(m => new { m.UserId, m.Timestamp })
                .ToListAsync(ct))
                .Select(m => (m.UserId, m.Timestamp.Ticks))
                .ToHashSet();
        }

        // Discord returns messages newest-first
        var messages = channel.GetMessagesAsync(limit: int.MaxValue).Flatten();

        await foreach (var msg in messages.WithCancellation(ct))
        {
            if (msg.Timestamp.UtcDateTime < cutoff)
                break;

            if (msg.Author.IsBot) continue;

            // skip duplicates
            var key = (msg.Author.Id, msg.Timestamp.UtcDateTime.Ticks);
            if (existing.Contains(key))
            {
                skipped++;
                continue;
            }

            batch.Add(new MessageEvent
            {
                GuildId = guildId,
                UserId = msg.Author.Id,
                Timestamp = msg.Timestamp.UtcDateTime
            });
            count++;

            if (batch.Count >= 500)
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                db.MessageEvents.AddRange(batch);
                await db.SaveChangesAsync(ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            db.MessageEvents.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }

        if (count > 0 || skipped > 0)
            _logger.LogInformation(
                "  #{Channel}: backfilled {Count} new messages (skipped {Skipped} duplicates)",
                channel.Name, count, skipped);
    }
}