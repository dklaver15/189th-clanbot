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
/// Maintains a single "Upcoming Events" board in the event post channel as a
/// STICKY message — the bot keeps it as the newest message so it's the first
/// thing members see when they open the channel. It lists every upcoming event
/// with its start time and a jump link to that event's RSVP post.
///
/// ── Sticky, not pinned ──
/// Discord can't hold a message at the visual top of a channel, and pinning only
/// puts it in the pin list (and spams a "pinned a message" notice on every
/// re-post). So the board rides the BOTTOM instead: whenever a newer message
/// appears, it's re-posted underneath and the previous copy deleted. It is
/// intentionally NOT pinned; a legacy pinned board is unpinned on sight. Re-posts
/// are silent and debounced (a burst of chatter coalesces into one), and skipped
/// entirely when the board is already newest — then it's only edited in place if
/// its contents changed.
///
/// ── Triggers ──
/// • a gateway MessageReceived in the channel (debounced ~3s) — covers member
///   chatter, reminders, event posts, anything;
/// • the event create / sort hooks, which call <see cref="RefreshAsync"/> directly;
/// • a 60s self-heal poll as a safety net (e.g. if the board is deleted).
///
/// ── Survives /sort ──
/// EventChannelSorter only deletes messages tracked in the ClanEvents table; the
/// board isn't one, so a sort never touches it — it just re-sticks afterwards.
/// </summary>
public sealed class UpcomingEventsBoardService : BackgroundService
{
    public const string BoardTitle = "📅 Upcoming Events";
    private const string BoardFooter = "Auto-updated • tap a title to open the event and RSVP";
    private const int MaxDescription = 3900; // headroom under Discord's 4096 embed-description cap
    private const int BoardScanLimit = 50;   // recent messages scanned to locate the board

    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StickDebounce = TimeSpan.FromSeconds(3);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<UpcomingEventsBoardService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly object _stickGate = new();
    private CancellationTokenSource? _pendingStick;

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

        // Re-stick promptly whenever something newer lands in the channel.
        _client.MessageReceived += OnMessageReceivedAsync;

        _logger.LogInformation(
            "UpcomingEventsBoardService started; sticky board, self-heal poll every {Seconds}s (enabled={Enabled})",
            (int)PollInterval.TotalSeconds, _config.EventBoardEnabled);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Upcoming-events board poll failed; will retry next cycle"); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _client.MessageReceived -= OnMessageReceivedAsync;
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (!_config.EventBoardEnabled) return Task.CompletedTask;
        if (message.Channel.Id != _config.GetEventPostChannelId()) return Task.CompletedTask;

        // Ignore the board's own (re)posts so re-sticking never feeds itself a loop.
        if (message.Author.Id == _client.CurrentUser?.Id
            && message is IUserMessage um && IsBoardMessage(um))
            return Task.CompletedTask;

        DebounceRestick();
        return Task.CompletedTask;
    }

    private void DebounceRestick()
    {
        CancellationTokenSource cts;
        lock (_stickGate)
        {
            _pendingStick?.Cancel();
            cts = _pendingStick = new CancellationTokenSource();
        }
        _ = RunDebouncedRestickAsync(cts);
    }

    private async Task RunDebouncedRestickAsync(CancellationTokenSource cts)
    {
        try
        {
            try { await Task.Delay(StickDebounce, cts.Token); }
            catch (OperationCanceledException) { return; } // a newer message superseded this re-stick

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Board re-stick (debounced) failed");
        }
        finally
        {
            lock (_stickGate) { if (_pendingStick == cts) _pendingStick = null; }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Ensures the board is the newest message in the channel and up to date. If
    /// it's already newest, edits it in place only when the contents changed;
    /// otherwise re-posts it at the bottom (silent, un-pinned) and deletes any
    /// previous copies. Serialized so concurrent triggers can't double-post.
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

            // Locate the board (and any stray duplicates) in recent history, and
            // find the newest message id of any kind so we know if it's on top.
            var recent = (await channel.GetMessagesAsync(BoardScanLimit).FlattenAsync()).ToList();
            var boards = recent.OfType<IUserMessage>().Where(IsBoardMessage).ToList();
            var newestId = recent.Count > 0 ? recent.Max(m => m.Id) : 0UL;
            var boardIsNewest = boards.Count == 1 && boards[0].Id == newestId;

            if (boardIsNewest)
            {
                var board = boards[0];

                // Converge to "no pin" if a legacy pinned board is still around.
                if (board.IsPinned)
                {
                    try { await board.UnpinAsync(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Board: unpin failed for {Msg}", board.Id); }
                }

                var current = board.Embeds.FirstOrDefault();
                if (current is not null && current.Title == BoardTitle && current.Description == description)
                    return; // already newest and unchanged — nothing to do

                await board.ModifyAsync(m => m.Embed = embed);
                return;
            }

            // Board is missing, buried under newer messages, or duplicated → post a
            // fresh one at the bottom (silent, un-pinned) and remove older copies.
            var posted = await channel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);
            foreach (var old in boards)
            {
                try { await old.DeleteAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Board: couldn't delete old board {Msg}", old.Id); }
            }

            _logger.LogInformation(
                "Board: (re)posted sticky upcoming-events board {Msg} in channel {Channel}", posted.Id, channelId);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsBoardMessage(IUserMessage m) =>
        m.Author.Id == _client.CurrentUser!.Id
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
