using System.Text.Json;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Posts a reminder in <see cref="BotConfig.AwolWipeReminderChannelId"/> once a
/// month — on the <see cref="BotConfig.AwolWipeReminderDayOfMonth"/> (default the
/// 23rd) at <see cref="BotConfig.AwolWipeReminderHourEt"/> US Eastern — tagging
/// <see cref="BotConfig.AwolWipeReminderMentionUserIds"/> to wipe the AWOLs
/// (<c>/kick-awols</c>) and clear the list (<c>/clear-awol-list</c>).
///
/// ── Schedule ──
/// Fires on the configured day-of-month, clamped to the month's last day via
/// <see cref="DateTime.DaysInMonth"/> so a value like 31 still fires on Feb
/// 28/29 rather than skipping the month.
///
/// ── Dedupe across restarts ──
/// The last month a reminder was posted ("yyyy-MM", Eastern) is persisted to
/// <c>data/awol-wipe-reminder.json</c> (next to the SQLite DB). The scheduler
/// alone never double-posts — once a month's fire instant has passed,
/// <see cref="ComputeNextFireUtc"/> returns next month — but the stamp also
/// powers a startup catch-up: if the bot was down at the fire instant and comes
/// back up later in the same month, it posts the (still-relevant) reminder then
/// instead of silently skipping the month.
///
/// ── Gating ──
/// Idle unless <see cref="BotConfig.AwolWipeReminderEnabled"/> is true and
/// <see cref="BotConfig.AwolWipeReminderChannelId"/> is non-zero, so enabling or
/// disabling the reminder is a pure config change with no redeploy.
/// </summary>
public sealed class AwolWipeReminderService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    // Clan-local time, matching WeeklyOfficerBriefingService.
    private static readonly TimeZoneInfo EasternTz =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<AwolWipeReminderService> _logger;

    private readonly string _statePath =
        Path.Combine(AppContext.BaseDirectory, "data", "awol-wipe-reminder.json");

    // The "yyyy-MM" (Eastern) of the month a reminder was last posted. Null until
    // the first post. Persisted across restarts for dedupe + catch-up.
    private string? _lastSentMonth;

    public AwolWipeReminderService(
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<AwolWipeReminderService> logger)
    {
        _client = client;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadState();

        // Wait for the gateway so channel resolution / posting is usable.
        while (_client.ConnectionState != ConnectionState.Connected)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "AwolWipeReminderService started; enabled={Enabled}, channel={ChannelId}, day={Day}, hourEt={Hour}, mentions={Mentions}",
            _config.AwolWipeReminderEnabled, _config.AwolWipeReminderChannelId,
            _config.AwolWipeReminderDayOfMonth, _config.AwolWipeReminderHourEt,
            _config.AwolWipeReminderMentionUserIds?.Count ?? 0);

        // ── Startup catch-up ──
        // If we're already at/after this month's fire instant and haven't posted
        // for this month yet, the bot was down when the reminder was due — post
        // it now while it's still the same (relevant) month.
        try
        {
            await RunCatchUpIfNeededAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AwolWipeReminderService catch-up check failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRun = ComputeNextFireUtc(DateTime.UtcNow);
            var delay = nextRun - DateTime.UtcNow;

            _logger.LogInformation(
                "Next monthly AWOL wipe reminder scheduled for {NextRun:u} ({Delay} from now)",
                nextRun, delay);

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            var monthKey = MonthKey(TimeZoneInfo.ConvertTimeFromUtc(nextRun, EasternTz));
            try
            {
                if (_lastSentMonth != monthKey)
                    await SendReminderAsync(nextRun, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AwolWipeReminderService scheduled post failed; will retry next month");
            }
        }
    }

    /// <summary>
    /// The fire instant (UTC) for the given Eastern year/month: the configured
    /// hour on the configured day-of-month (clamped to the month's last day).
    /// </summary>
    private DateTime ComputeFireInstantUtc(int year, int month)
    {
        var hour = Math.Clamp(_config.AwolWipeReminderHourEt, 0, 23);
        var day = Math.Clamp(_config.AwolWipeReminderDayOfMonth, 1, DateTime.DaysInMonth(year, month));
        var fireEt = new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(fireEt, EasternTz);
    }

    /// <summary>
    /// The next fire instant strictly after <paramref name="nowUtc"/>. If this
    /// month's instant has already passed, rolls to next month.
    /// </summary>
    internal DateTime ComputeNextFireUtc(DateTime nowUtc)
    {
        var nowEt = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, EasternTz);
        var thisMonth = ComputeFireInstantUtc(nowEt.Year, nowEt.Month);
        if (thisMonth > nowUtc) return thisMonth;

        var next = nowEt.AddMonths(1);
        return ComputeFireInstantUtc(next.Year, next.Month);
    }

    /// <summary>
    /// On startup, post the reminder if the current month's fire instant has
    /// already passed and we haven't posted for this month. Because the fire
    /// instant is built from the *current* Eastern month, "now ≥ fire instant"
    /// guarantees we're still inside that month (past the fire day, before
    /// month-end), so a catch-up never posts a stale prior-month reminder.
    /// </summary>
    private async Task RunCatchUpIfNeededAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var nowEt = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, EasternTz);
        var thisMonthFire = ComputeFireInstantUtc(nowEt.Year, nowEt.Month);
        var monthKey = MonthKey(nowEt);

        if (nowUtc >= thisMonthFire && _lastSentMonth != monthKey)
        {
            _logger.LogInformation(
                "AwolWipeReminderService catch-up: month {Month} fire instant {Fire:u} already passed and not yet posted; posting now",
                monthKey, thisMonthFire);
            await SendReminderAsync(thisMonthFire, ct);
        }
    }

    /// <summary>
    /// Resolves the target channel and posts the reminder (tagging the configured
    /// users), then stamps the month so it won't be re-posted. The month is
    /// stamped only on a successful post, so a failure is retried by the next
    /// monthly fire or the next startup catch-up. <paramref name="fireInstantUtc"/>
    /// determines which Eastern month the message names and stamps.
    /// </summary>
    private async Task SendReminderAsync(DateTime fireInstantUtc, CancellationToken ct)
    {
        if (!_config.AwolWipeReminderEnabled)
        {
            _logger.LogDebug("AwolWipeReminderService: disabled, skipping post");
            return;
        }

        if (_config.AwolWipeReminderChannelId == 0)
        {
            _logger.LogWarning("AwolWipeReminderService: AwolWipeReminderChannelId not set, skipping post");
            return;
        }

        var monthEt = TimeZoneInfo.ConvertTimeFromUtc(fireInstantUtc, EasternTz);
        var monthKey = MonthKey(monthEt);

        // Resolve from the gateway cache first; fall back to a REST lookup so an
        // uncached channel still resolves.
        var channel = _client.GetChannel(_config.AwolWipeReminderChannelId) as IMessageChannel
                      ?? await _client.Rest.GetChannelAsync(_config.AwolWipeReminderChannelId) as IMessageChannel;

        if (channel is null)
        {
            _logger.LogWarning(
                "AwolWipeReminderService: could not resolve channel {ChannelId}; skipping {Month} reminder",
                _config.AwolWipeReminderChannelId, monthKey);
            return;
        }

        var mentionIds = _config.AwolWipeReminderMentionUserIds ?? new List<ulong>();
        var mentionPrefix = mentionIds.Count > 0
            ? string.Join(" ", mentionIds.Select(id => $"<@{id}>")) + "\n\n"
            : string.Empty;

        var message =
            mentionPrefix +
            "**Monthly AWOL reminder**\n\n" +
            "Time to wipe the AWOLs and clear the list:\n" +
            $"• `/kick-awols` — remove the members who have been on the list " +
            $"{_config.AwolKickMinListedDays}+ days\n" +
            "• `/clear-awol-list` — clear the HQ AWOL list (recent listings stay)\n\n" +
            "_Automated monthly reminder from the 189th Clanguard bot._";

        // Whitelist exactly the configured user IDs so only they get pinged,
        // regardless of anything else parsed from the message content.
        var allowedMentions = mentionIds.Count > 0
            ? new AllowedMentions { UserIds = mentionIds.ToList() }
            : AllowedMentions.None;

        // Linked timeout so a wedged send can never stall the monthly loop.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var options = new RequestOptions { CancelToken = cts.Token };

        await channel.SendMessageAsync(text: message, allowedMentions: allowedMentions, options: options);

        _lastSentMonth = monthKey;
        SaveState();

        _logger.LogInformation(
            "AwolWipeReminderService posted {Month} reminder to channel {ChannelId} (mentions={Mentions})",
            monthKey, _config.AwolWipeReminderChannelId, mentionIds.Count);
    }

    private static string MonthKey(DateTime et) => et.ToString("yyyy-MM");

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var json = File.ReadAllText(_statePath);
            var loaded = JsonSerializer.Deserialize<State>(json);
            _lastSentMonth = loaded?.LastSentMonth;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AwolWipeReminderService: failed to load state, starting fresh");
            _lastSentMonth = null;
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var json = JsonSerializer.Serialize(new State { LastSentMonth = _lastSentMonth });
            File.WriteAllText(_statePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AwolWipeReminderService: failed to persist state");
        }
    }

    private sealed class State
    {
        public string? LastSentMonth { get; set; }
    }
}
