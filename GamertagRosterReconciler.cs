using ClanGuardBot.Models;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Offline-gap cleanup for the gamertag roster sheet. The live path
/// (MemberLifecycleHandler on UserLeft) removes a member's row the moment they
/// leave/are kicked/are banned — but Discord doesn't replay UserLeft for
/// departures that happen while the bot is offline, so those rows would linger.
/// This periodic sweep catches them.
///
/// ── How it works ──
/// On startup (after a grace delay) and every RetentionRosterReconcileHours, it
/// downloads the full guild member list and asks
/// <see cref="GoogleSheetsService.ReconcileGamertagsAsync"/> to delete any sheet
/// row whose Discord ID is no longer in the guild.
///
/// ── Legacy rows ──
/// Rows without a parseable Discord ID (older name-only rows) are never deleted
/// — see ReconcileGamertagsAsync. They're counted and surfaced in the log so an
/// officer knows how many remain; they self-resolve once that member re-runs
/// /gamertags (which backfills their ID).
///
/// ── Safety ──
/// Mirrors MemberRosterReconciler: a full DownloadUsersAsync first (so an
/// incomplete cache can't manufacture phantom departures), an empty-member-list
/// guard, and a per-sweep deletion cap inside the sheet service. Disabled
/// automatically when no roster spreadsheet is configured. All failures are
/// caught and logged; a sweep error never crashes the host.
/// </summary>
public sealed class GamertagRosterReconciler : BackgroundService
{
    // After MemberRosterReconciler's 60s grace, so the gateway is settled.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(90);

    // Brake against a botched member fetch: if more rows than this look
    // departed in one sweep, the sheet service deletes nothing and warns.
    private const int MaxDeletionsPerSweep = 50;

    private readonly DiscordSocketClient _client;
    private readonly GoogleSheetsService _sheets;
    private readonly BotConfig _config;
    private readonly ILogger<GamertagRosterReconciler> _logger;

    public GamertagRosterReconciler(
        DiscordSocketClient client,
        GoogleSheetsService sheets,
        IOptions<BotConfig> config,
        ILogger<GamertagRosterReconciler> logger)
    {
        _client  = client;
        _sheets  = sheets;
        _config  = config.Value;
        _logger  = logger;
    }

    private TimeSpan ReconcileInterval =>
        TimeSpan.FromHours(Math.Max(1, _config.RetentionRosterReconcileHours));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_config.GoogleSpreadsheetId))
        {
            _logger.LogInformation("GamertagRosterReconciler disabled — no roster spreadsheet configured.");
            return;
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "GamertagRosterReconciler started; sweeping on startup and every {Hours}h",
            Math.Max(1, _config.RetentionRosterReconcileHours));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GamertagRosterReconciler sweep failed; will retry next interval");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReconcileAsync()
    {
        var guild = _client.Guilds.FirstOrDefault();
        if (guild is null)
        {
            _logger.LogWarning("Gamertag reconcile: no guild connected — skipping sweep");
            return;
        }

        // Complete the member cache before treating "not present" as "departed".
        await guild.DownloadUsersAsync();

        var currentIds = guild.Users.Where(u => !u.IsBot).Select(u => u.Id).ToHashSet();
        if (currentIds.Count == 0)
        {
            // A genuinely empty list almost certainly means the download failed —
            // never reconcile against it, or we'd wipe the whole roster.
            _logger.LogWarning("Gamertag reconcile: guild member list came back empty — skipping sweep");
            return;
        }

        var result = await _sheets.ReconcileGamertagsAsync(currentIds, MaxDeletionsPerSweep);

        if (result.Aborted)
        {
            _logger.LogWarning(
                "Gamertag reconcile aborted: {Candidates} candidate row(s) exceeded the safety cap " +
                "of {Cap}, or the sheet was unresolved. Kept {Kept}, {Legacy} legacy/no-ID row(s) untouched.",
                result.Candidates, MaxDeletionsPerSweep, result.Kept, result.LegacySkipped);
            return;
        }

        if (result.Deleted > 0 || result.LegacySkipped > 0)
        {
            _logger.LogInformation(
                "Gamertag reconcile complete: removed {Deleted} departed row(s), kept {Kept}, " +
                "left {Legacy} legacy/no-ID row(s) untouched.",
                result.Deleted, result.Kept, result.LegacySkipped);
        }
        else
        {
            _logger.LogDebug("Gamertag reconcile complete: nothing to remove (kept {Kept}).", result.Kept);
        }
    }
}
