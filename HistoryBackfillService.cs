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

            foreach (var channel in guild.TextChannels)
            {
                try
                {
                    await BackfillChannelAsync(channel, guild.Id, cutoff, stoppingToken);
                }
                catch (Exception ex)
                {
                    // Permission errors, etc. — just skip the channel
                    _logger.LogWarning(ex, "Could not backfill #{Channel} in {Guild}",
                        channel.Name, guild.Name);
                }
            }
        }

        _logger.LogInformation("Message history backfill complete!");
    }

    private async Task BackfillChannelAsync(
    SocketTextChannel channel, ulong guildId, DateTime cutoff, CancellationToken ct)
{
    var count = 0;
    var skipped = 0;                          // ← add this
    var batch = new List<MessageEvent>();

    // ── NEW: load existing event keys for dedup ──────────────────
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
    // ── END NEW ──────────────────────────────────────────────────

    // Discord returns messages newest-first
    var messages = channel.GetMessagesAsync(limit: int.MaxValue).Flatten();

    await foreach (var msg in messages.WithCancellation(ct))
    {
        if (msg.Timestamp.UtcDateTime < cutoff)
            break;

        if (msg.Author.IsBot) continue;

        // ── NEW: skip duplicates ─────────────────────────────────
        var key = (msg.Author.Id, msg.Timestamp.UtcDateTime.Ticks);
        if (existing.Contains(key))
        {
            skipped++;
            continue;
        }
        // ── END NEW ──────────────────────────────────────────────

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

    if (count > 0 || skipped > 0)                // ← updated condition
        _logger.LogInformation("  #{Channel}: backfilled {Count} new messages (skipped {Skipped} duplicates)",
            channel.Name, count, skipped);         // ← updated log
}
}