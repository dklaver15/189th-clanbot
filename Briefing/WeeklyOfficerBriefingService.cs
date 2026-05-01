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
                "[DRY RUN] Briefing not posted. Cost ~{Cost:C4}.\n{Briefing}",
                result.EstimatedCostUsd, result.Text);
            return;
        }

        await PostToDiscordAsync(result.Text, ct);
    }

    private async Task PostToDiscordAsync(string content, CancellationToken ct)
    {
        var rawChannel = await discord.GetChannelAsync(_options.OfficerChannelId);
        if (rawChannel is not IMessageChannel channel)
        {
            logger.LogError(
                "Officer channel {Id} not found or not a message channel",
                _options.OfficerChannelId);
            return;
        }

        // Discord collapses consecutive bulleted sections into a single visual block,
        // even when the source markdown has blank lines between them. Inject a
        // zero-width-space line before every "### " header so the renderer
        // treats them as separate blocks. The U+200B character is invisible.
        var spaced = InjectSectionSpacers(content);

        foreach (var chunk in ChunkForDiscord(spaced))
        {
            await channel.SendMessageAsync(chunk, options: new RequestOptions { CancelToken = ct });
        }

        logger.LogInformation("Posted weekly briefing to channel {Id}", _options.OfficerChannelId);
    }

    /// <summary>
    /// Inserts a zero-width-space line before every Markdown H3 header so Discord
    /// renders a visible gap between sections. Without this, sections that follow
    /// a bulleted list collapse into the previous list visually — the "### Risk Watch"
    /// header gets glued onto the last Promotion Candidates bullet.
    /// </summary>
    private static string InjectSectionSpacers(string content)
    {
        var lines = content.Split('\n');
        var output = new System.Text.StringBuilder(content.Length + 64);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // First line is never preceded by a spacer; otherwise add one before any "### " header.
            if (i > 0 && line.TrimStart().StartsWith("### "))
            {
                output.Append('\u200B').Append('\n');
            }
            output.Append(line);
            if (i < lines.Length - 1) output.Append('\n');
        }

        return output.ToString();
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

    /// <summary>
    /// Discord caps messages at 2000 chars. Split on paragraph boundaries when possible,
    /// fall back to hard chunking. Keeps a safety margin at 1900.
    /// </summary>
    private static IEnumerable<string> ChunkForDiscord(string content, int maxLen = 1900)
    {
        if (content.Length <= maxLen)
        {
            yield return content;
            yield break;
        }

        var paragraphs = content.Split("\n\n", StringSplitOptions.None);
        var buffer = new System.Text.StringBuilder();

        foreach (var para in paragraphs)
        {
            // Single paragraph already too big — hard split.
            if (para.Length > maxLen)
            {
                if (buffer.Length > 0) { yield return buffer.ToString(); buffer.Clear(); }
                for (var i = 0; i < para.Length; i += maxLen)
                    yield return para.Substring(i, Math.Min(maxLen, para.Length - i));
                continue;
            }

            if (buffer.Length + para.Length + 2 > maxLen)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (buffer.Length > 0) buffer.Append("\n\n");
            buffer.Append(para);
        }

        if (buffer.Length > 0) yield return buffer.ToString();
    }
}