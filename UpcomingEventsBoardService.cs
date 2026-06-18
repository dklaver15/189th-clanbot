using System.Text;
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
/// Maintains a single, pinned "Upcoming Events" board in the event post channel:
/// one bot message, EDITED in place (never deleted, never moved), listing every
/// upcoming event with its start time and a jump link to its RSVP post. It gives
/// members one stable place to find events instead of scrolling/searching the
/// cluttered channel.
///
/// ── Why it survives /sort ──
/// <see cref="EventChannelSorter"/> only deletes messages whose IDs live in the
/// ClanEvents table; the board is not a ClanEvent, so a sort can never delete or
/// reorder it. A sort DOES change every event's MessageId, so the jump links go
/// stale — <see cref="EventManagementHandler"/> calls <see cref="RefreshAsync"/>
/// right after a sort to rewrite them.
///
/// ── Persistence-free (pin discovery) ──
/// The board is found by scanning the channel's pins for a bot-authored message
/// whose embed title is <see cref="BoardTitle"/> — no DB column or config to
/// store a message id, nothing to migrate. If it was manually unpinned it's
/// recovered from recent history (and re-pinned) rather than duplicated; if it
/// was deleted, a fresh one is posted and pinned. A 60s self-heal poll keeps it
/// current even for changes that don't call RefreshAsync directly
/// (cancellations, auto-archive, series promotion).
/// </summary>
public sealed class UpcomingEventsBoardService : BackgroundService
{
    public const string BoardTitle = "📅 Upcoming Events";
    private const string BoardFooter = "Auto-updated • tap a title to open the event and RSVP";
    private const int MaxDescription = 3900; // headroom under Discord's 4096 embed-description cap

    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<UpcomingEventsBoardService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public UpcomingEventsBoardService(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<UpcomingEventsBoardService> logger)
    {
        _services = services;
        _client   = client;
        _config   = config.Value;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "UpcomingEventsBoardService started; self-heal poll every {Seconds}s (enabled={Enabled})",
            (int)PollInterval.TotalSeconds, _config.EventBoardEnabled);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Upcoming-events board poll failed; will retry next cycle"); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Rebuilds the board from the DB and edits the pinned message in place
    /// (creating + pinning it if missing). Safe to call from anywhere; serialized
    /// so concurrent triggers (the poll plus a /sort or a create) can't race into
    /// two boards. Never throws to its caller's critical path — callers should
    /// still wrap in try/catch, but internal Discord hiccups are logged and
    /// swallowed by the poll loop.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!_config.EventBoardEnabled) return;

        var channelId = _config.GetEventPostChannelId();
        if (_client.GetChannel(channelId) is not SocketTextChannel channel)
        {
            _logger.LogDebug("Board: event channel {Channel} not reachable; skipping", channelId);
            return;
        }
        if (_client.CurrentUser is null) return;

        await _refreshLock.WaitAsync(ct);
        try
        {
            var guildId = channel.Guild.Id;
            var now = DateTime.UtcNow;

            List<ClanEvent> events;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                events = await db.ClanEvents
                    .Where(e => e.Status == ClanEventStatus.Scheduled
                             && e.MessageId != 0          // only posted occurrences have something to link to
                             && e.ChannelId == channelId
                             && e.EndUtc > now)           // upcoming + currently-running, not ended
                    .OrderBy(e => e.StartUtc)
                    .ToListAsync(ct);
            }

            var description = BuildDescription(events, guildId, channelId);
            var embed = new EmbedBuilder()
                .WithTitle(BoardTitle)
                .WithColor(new Color(0xFAA61A))
                .WithDescription(description)
                .WithFooter(BoardFooter)
                .Build();

            var board = await FindBoardAsync(channel, ct);

            // Missing → post a fresh board and pin it.
            if (board is null)
            {
                var posted = await channel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);
                try { await posted.PinAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Board: posted board {Msg} but pinning failed", posted.Id); }
                _logger.LogInformation("Board: created upcoming-events board {Msg} in channel {Channel}", posted.Id, channelId);
                return;
            }

            // Recover from a manual unpin so it stays in the pin list.
            if (!board.IsPinned)
            {
                try { await board.PinAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Board: re-pin failed for {Msg}", board.Id); }
            }

            // Edit only when the content actually changed — avoids needless edits
            // and rate-limit churn on every 60s poll.
            var current = board.Embeds.FirstOrDefault();
            if (current is not null && current.Title == BoardTitle && current.Description == description)
                return;

            await board.ModifyAsync(m => m.Embed = embed);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IUserMessage?> FindBoardAsync(SocketTextChannel channel, CancellationToken ct)
    {
        // The board lives in the channel's pins (that's the whole point) — look there first.
        var pins = await channel.GetPinnedMessagesAsync();
        var board = pins.OfType<IUserMessage>().FirstOrDefault(IsBoardMessage);
        if (board is not null) return board;

        // Fallback: it exists but was manually unpinned — recover it from recent
        // history instead of posting a duplicate. (Re-pin happens in RefreshAsync.)
        var recent = await channel.GetMessagesAsync(50).FlattenAsync();
        return recent.OfType<IUserMessage>().FirstOrDefault(IsBoardMessage);
    }

    private bool IsBoardMessage(IUserMessage m) =>
        m.Author.Id == _client.CurrentUser.Id
        && m.Embeds.Any(e => string.Equals(e.Title, BoardTitle, StringComparison.Ordinal));

    private static string BuildDescription(IReadOnlyList<ClanEvent> events, ulong guildId, ulong channelId)
    {
        if (events.Count == 0)
            return "_No upcoming events right now. Check back soon!_";

        var sb = new StringBuilder();
        var shown = 0;
        foreach (var ev in events)
        {
            var jump = $"https://discord.com/channels/{guildId}/{channelId}/{ev.MessageId}";
            var line =
                $"**[{Sanitize(ev.Title)}]({jump})**\n" +
                $"{EventTimeParser.Stamp(ev.StartUtc, 'f')} · {EventTimeParser.Stamp(ev.StartUtc, 'R')}\n\n";

            // Stay under the embed-description cap; note the overflow rather than truncate silently.
            if (sb.Length + line.Length > MaxDescription)
            {
                sb.Append($"_…and {events.Count - shown} more — scroll the channel._");
                break;
            }

            sb.Append(line);
            shown++;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Strip characters that would break the markdown link label.</summary>
    private static string Sanitize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Untitled event";
        return title.Replace("[", "(").Replace("]", ")").Replace("`", "'").Trim();
    }
}
