using System.Text;
using System.Text.Json;
using ClanGuardBot.AI;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Briefing;

public sealed class WeeklyBriefingOptions
{
    /// <summary>Discord channel ID where the briefing is posted.</summary>
    public ulong OfficerChannelId { get; set; }

    /// <summary>Day of week to run, in UTC. Default: Sunday.</summary>
    public DayOfWeek RunOnDayUtc { get; set; } = DayOfWeek.Sunday;

    /// <summary>Time of day to run, in UTC. Default: 14:00 UTC = 09:00 CDT / 08:00 CST.</summary>
    public TimeOnly RunAtUtc { get; set; } = new(14, 0);

    /// <summary>If true, log the briefing instead of posting to Discord. Useful while iterating.</summary>
    public bool DryRun { get; set; }

    /// <summary>Cap on output tokens. 1500 ≈ ~1100 words; well above the 400-word target.</summary>
    public int MaxOutputTokens { get; set; } = 1500;
}

public sealed class WeeklyOfficerBriefingService(
    IAiService ai,
    IBriefingDataCollector collector,
    DiscordSocketClient discord,
    IOptions<WeeklyBriefingOptions> options,
    ILogger<WeeklyOfficerBriefingService> logger) : BackgroundService
{
    private readonly WeeklyBriefingOptions _options = options.Value;

    // Display dates in clan-local Eastern Time, matching CompEventCommandHandler.
    // The scheduled Sunday 14:00 UTC run is still Sunday in ET; this only matters
    // for /briefing-now invocations triggered late at night UTC, where the UTC
    // date and ET date differ.
    private static readonly TimeZoneInfo EasternTz =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly JsonSerializerOptions PromptJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForDiscordReadyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRun = ComputeNextRunUtc(DateTime.UtcNow);
            var delay = nextRun - DateTime.UtcNow;

            logger.LogInformation(
                "Next weekly briefing scheduled for {NextRun:u} ({Delay} from now)",
                nextRun, delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunBriefingNowAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Weekly briefing run failed; will retry on next schedule");
            }
        }
    }

    /// <summary>
    /// Run a briefing immediately. Exposed so you can trigger it from a slash command
    /// (e.g. /briefing-now) for testing.
    /// </summary>
    public async Task RunBriefingNowAsync(CancellationToken ct = default)
    {
        var weekEnd = DateTime.UtcNow;
        var weekStart = weekEnd.AddDays(-7);

        var context = await collector.CollectAsync(weekStart, weekEnd, ct);
        var userMessage = JsonSerializer.Serialize(context, PromptJson);

        logger.LogDebug("Briefing prompt payload: {Bytes} bytes", userMessage.Length);

        var result = await ai.GenerateAsync(
            BriefingPrompts.SystemPrompt,
            userMessage,
            _options.MaxOutputTokens,
            ct);

        if (_options.DryRun)
        {
            logger.LogInformation(
                "[DRY RUN] Briefing not posted. Model: {Model}. Cost ~{Cost:C4}.\n{Briefing}",
                result.Model, result.EstimatedCostUsd, result.Text);
            return;
        }

        await PostToDiscordAsync(result.Text, result.Model, weekStart, weekEnd, ct);
    }

    /// <summary>
    /// Posts the briefing to HQ as an embed (date range header + model footer)
    /// with the full markdown briefing attached as a .md file. This keeps the
    /// channel readable in scrollback — the embed acts as a per-week reference
    /// card, and the attached file holds the full content rendered as markdown
    /// by any viewer that opens it.
    /// </summary>
    private async Task PostToDiscordAsync(
        string content,
        string model,
        DateTime weekStartUtc,
        DateTime weekEndUtc,
        CancellationToken ct)
    {
        var rawChannel = await discord.GetChannelAsync(_options.OfficerChannelId);
        if (rawChannel is not IMessageChannel channel)
        {
            logger.LogError(
                "Officer channel {Id} not found or not a message channel",
                _options.OfficerChannelId);
            return;
        }

        var weekStartEt = TimeZoneInfo.ConvertTimeFromUtc(weekStartUtc, EasternTz);
        var weekEndEt = TimeZoneInfo.ConvertTimeFromUtc(weekEndUtc, EasternTz);

        // Always render month on both sides (e.g. "May 4 - May 11") so the format
        // stays consistent whether or not the range crosses a month boundary.
        var dateRange = $"{weekStartEt:MMMM d} - {weekEndEt:MMMM d}";

        // Filename uses week-ending date in ET so it sorts naturally and lines
        // up with the title. Short form keeps the attachment label compact in Discord.
        var filename = $"briefing-{weekEndEt:yyyy-MM-dd}.md";

        var embed = new EmbedBuilder()
            .WithTitle($"Weekly Officer Briefing — {dateRange}")
            .WithColor(new Color(0x2B6CB0))
            .WithFooter($"Model: {model}")
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await channel.SendFileAsync(
            stream,
            filename,
            embed: embed,
            options: new RequestOptions { CancelToken = ct });

        logger.LogInformation(
            "Posted weekly briefing to channel {Id} as embed + attachment '{Filename}'",
            _options.OfficerChannelId, filename);
    }

    private async Task WaitForDiscordReadyAsync(CancellationToken ct)
    {
        if (discord.ConnectionState == ConnectionState.Connected)
            return;

        var ready = new TaskCompletionSource();
        Task Handler() { ready.TrySetResult(); return Task.CompletedTask; }
        discord.Ready += Handler;
        try
        {
            await ready.Task.WaitAsync(ct);
        }
        finally
        {
            discord.Ready -= Handler;
        }
    }

    internal DateTime ComputeNextRunUtc(DateTime nowUtc)
    {
        var daysUntil = ((int)_options.RunOnDayUtc - (int)nowUtc.DayOfWeek + 7) % 7;
        var candidate = nowUtc.Date.AddDays(daysUntil) + _options.RunAtUtc.ToTimeSpan();
        return candidate <= nowUtc ? candidate.AddDays(7) : candidate;
    }
}