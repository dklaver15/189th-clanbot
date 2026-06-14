using System.Text.Json;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Posts a "fight night is coming" reminder to the configured channel the day
/// before a UFC event. Polls the SportsDataIO schedule hourly; when an event
/// falls inside the lead window (<see cref="BotConfig.UfcReminderLeadHours"/>,
/// default 24h) and hasn't been announced yet, it posts the reminder embed —
/// official poster included — and records the event so a restart or the next
/// tick won't double-post.
///
/// ── Dedupe across restarts ──
/// Announced event IDs are persisted to <c>data/ufc-reminders.json</c> (next to
/// the SQLite DB) as an EventId→start-time map. On boot we load it; after each
/// announce we save it; entries for events more than 7 days past are pruned so
/// the file doesn't grow without bound. No DB migration needed — this is a tiny,
/// self-contained bit of state.
///
/// ── Gating ──
/// Idle unless <see cref="BotConfig.UfcEnabled"/> is true, an API key is set,
/// and <see cref="BotConfig.UfcChannelId"/> resolves. Any of those missing and
/// the loop runs but does nothing, so enabling the feature is purely a config
/// change — no redeploy.
/// </summary>
public sealed class UfcReminderService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan PruneAge     = TimeSpan.FromDays(7);

    private readonly UfcApiService _api;
    private readonly UfcEventPosterService _posters;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<UfcReminderService> _logger;

    private readonly string _statePath =
        Path.Combine(AppContext.BaseDirectory, "data", "ufc-reminders.json");

    // EventId (as string) → event start (UTC). Persisted across restarts.
    private Dictionary<string, DateTime> _announced = new();

    public UfcReminderService(
        UfcApiService api,
        UfcEventPosterService posters,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<UfcReminderService> logger)
    {
        _api = api;
        _posters = posters;
        _client = client;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadState();

        // Wait for the gateway so GetChannel resolves.
        while (_client.ConnectionState != ConnectionState.Connected)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            if (stoppingToken.IsCancellationRequested) return;
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "UfcReminderService started; polling every {Minutes} min, lead {Lead}h",
            (int)PollInterval.TotalMinutes, _config.UfcReminderLeadHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.UfcEnabled && _api.IsConfigured)
                    await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UfcReminderService tick failed; will retry next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_config.UfcChannelId == 0)
        {
            _logger.LogDebug("UfcReminderService: UfcChannelId not set, skipping");
            return;
        }

        if (_client.GetChannel(_config.UfcChannelId) is not IMessageChannel channel)
        {
            _logger.LogWarning("UfcReminderService: channel {Channel} is not reachable", _config.UfcChannelId);
            return;
        }

        var leadHours = _config.UfcReminderLeadHours > 0 ? _config.UfcReminderLeadHours : 24;
        var nowUtc = DateTime.UtcNow;

        var upcoming = await _api.GetUpcomingAsync(max: 10, ct);
        var changed = false;

        foreach (var ev in upcoming)
        {
            var startUtc = UfcApiService.ToUtc(ev);
            if (startUtc is not { } start) continue;

            var hoursUntil = (start - nowUtc).TotalHours;
            if (hoursUntil <= 0 || hoursUntil > leadHours) continue;

            var key = ev.EventId.ToString();
            if (_announced.ContainsKey(key)) continue;

            var poster = await _posters.GetPosterUrlAsync(ev.Name, ct);
            var embed = UfcEmbedBuilder.BuildReminderEmbed(ev, poster);

            await channel.SendMessageAsync(embed: embed);
            _announced[key] = start;
            changed = true;

            _logger.LogInformation(
                "UfcReminderService announced event {EventId} ({Name})",
                ev.EventId, ev.Name);
        }

        if (PruneOld(nowUtc) || changed)
            SaveState();
    }

    private bool PruneOld(DateTime nowUtc)
    {
        var cutoff = nowUtc - PruneAge;
        var stale = _announced.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
        foreach (var key in stale) _announced.Remove(key);
        return stale.Count > 0;
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var json = File.ReadAllText(_statePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
            if (loaded is not null) _announced = loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UfcReminderService: failed to load state, starting fresh");
            _announced = new();
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var json = JsonSerializer.Serialize(_announced);
            File.WriteAllText(_statePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UfcReminderService: failed to persist state");
        }
    }
}
