using System.Net;
using System.Text;
using System.Text.Json;
using ClanGuardBot.AI;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// Cap on output tokens. With <see cref="EnableThinking"/> = false the model
    /// emits no reasoning tokens, so this only needs to cover the ~600-word
    /// (~800-token) briefing text; 4000 is generous headroom. If thinking is ever
    /// re-enabled, raise this well above the expected thinking budget — Sonnet 5's
    /// adaptive thinking counts against this cap and starved the old 1500, which
    /// produced truncated or empty (stop_reason=max_tokens) briefings.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4000;

    /// <summary>
    /// Adaptive-thinking/token-spend effort passed to the model (low/medium/high/
    /// xhigh/max), or null for the API default ("high"). Default "medium": on
    /// Sonnet 5 this is roughly Sonnet 4.6 at high effort — a small step up from
    /// the old model at controlled cost. See ClaudeOptions/effort docs.
    /// </summary>
    public string? Effort { get; set; } = "medium";

    /// <summary>
    /// Whether to let the model run adaptive thinking. Default false: the briefing
    /// is a data-summary task that doesn't need step-by-step reasoning, and
    /// disabling thinking means no reasoning tokens are billed — keeping the cost
    /// profile close to the old thinking-off Sonnet 4.6 while using the stronger
    /// Sonnet 5 base model at <see cref="Effort"/> = medium.
    /// </summary>
    public bool EnableThinking { get; set; }

    /// <summary>
    /// Total attempts for the AI generation call within a single run, including
    /// the first. 3 → one initial try plus two retries. Only *transient*
    /// failures (429, 5xx, network/timeout) are retried; auth/bad-request
    /// errors (401/403/400) fail fast — retrying an invalid API key just burns
    /// time and spams the log. Set to 1 to disable retry.
    /// </summary>
    public int MaxGenerateAttempts { get; set; } = 3;

    /// <summary>Base backoff between transient retries; doubles each attempt (2s, 4s, ...).</summary>
    public int RetryBaseDelaySeconds { get; set; } = 2;
}

public sealed class WeeklyOfficerBriefingService(
    IAiService ai,
    IBriefingDataCollector collector,
    DiscordSocketClient discord,
    IServiceScopeFactory scopeFactory,
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

        // ── Catch-up check ──
        // The scheduler computes its next run purely in memory, so a container
        // recreate (deploy/crash/OOM) that lands *after* the Sunday trigger —
        // OR a run that threw at the trigger instant (e.g. a transient API
        // error, or an invalid API key) — silently loses that week with no
        // recovery until the next Sunday. On startup we check whether the most
        // recent scheduled run actually completed; if not, we run it now for
        // the correct historical week window. See RunCatchUpIfNeededAsync.
        try
        {
            await RunCatchUpIfNeededAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during weekly-briefing catch-up check");
        }

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
                // The scheduled run covers the week ending at the trigger we
                // just woke for (nextRun), not "now minus 7" — they're within
                // seconds of each other here, but using the trigger keeps the
                // window identical to what catch-up would have produced.
                await RunBriefingForWindowAsync(nextRun.AddDays(-7), nextRun, stoppingToken);

                // Stamp only after a successful generate + post. A throw above
                // leaves the slot unstamped, so the next startup will catch it
                // up — this is what turns a one-off failure into a recoverable
                // one instead of a silently-skipped week.
                await StampBriefingCompletedAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Weekly briefing run failed; slot left unstamped for startup catch-up");
            }
        }
    }

    /// <summary>
    /// Run a briefing immediately for the trailing 7-day window. Exposed for
    /// the /briefing-now slash command. Manual runs deliberately do NOT stamp
    /// BotState — a mid-week test must not make the catch-up think the weekly
    /// slot was filled.
    /// </summary>
    public Task RunBriefingNowAsync(CancellationToken ct = default)
    {
        var weekEnd = DateTime.UtcNow;
        return RunBriefingForWindowAsync(weekEnd.AddDays(-7), weekEnd, ct);
    }

    /// <summary>
    /// Core run: collect the snapshot for [weekStart, weekEnd), generate the
    /// briefing (with transient-aware retry), and either post it or log it in
    /// dry-run. Throws on unrecoverable failure (e.g. 401) so the caller can
    /// decide whether to stamp the slot complete.
    /// </summary>
    private async Task RunBriefingForWindowAsync(DateTime weekStartUtc, DateTime weekEndUtc, CancellationToken ct)
    {
        var context = await collector.CollectAsync(weekStartUtc, weekEndUtc, ct);
        var userMessage = JsonSerializer.Serialize(context, PromptJson);

        logger.LogDebug("Briefing prompt payload: {Bytes} bytes", userMessage.Length);

        var result = await GenerateWithRetryAsync(userMessage, ct);

        // Defense in depth: GenerateWithRetryAsync already throws on an empty
        // completion, so we should never get here with blank text. Guard anyway
        // — posting a 0-byte briefing (embed + empty attachment) and then
        // stamping the week complete is exactly the silent failure this feature
        // must never produce. Throw so the slot stays unstamped for catch-up.
        if (string.IsNullOrWhiteSpace(result.Text))
            throw new EmptyCompletionException("blank text post-generation");

        if (_options.DryRun)
        {
            logger.LogInformation(
                "[DRY RUN] Briefing not posted. Model: {Model}. Cost ~{Cost:C4}.\n{Briefing}",
                result.Model, result.EstimatedCostUsd, result.Text);
            return;
        }

        await PostToDiscordAsync(result.Text, result.Model, weekStartUtc, weekEndUtc, ct);
    }

    /// <summary>
    /// Calls the AI service, retrying only on transient failures (HTTP 429,
    /// 5xx, or a network/timeout with no response). Non-transient failures —
    /// most importantly 401 invalid-api-key and other 4xx — throw on the first
    /// attempt: they will not fix themselves within the run, and retrying them
    /// only delays the inevitable and floods the log.
    /// </summary>
    private async Task<AiResult> GenerateWithRetryAsync(string userMessage, CancellationToken ct)
    {
        var attempts = Math.Max(1, _options.MaxGenerateAttempts);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ai.GenerateAsync(
                    BriefingPrompts.SystemPrompt, userMessage, _options.MaxOutputTokens, ct,
                    effort: _options.Effort, enableThinking: _options.EnableThinking);
            }
            catch (Exception ex) when (attempt < attempts && IsTransient(ex, ct))
            {
                var backoff = TimeSpan.FromSeconds(_options.RetryBaseDelaySeconds * Math.Pow(2, attempt - 1));
                logger.LogWarning(
                    ex,
                    "Briefing AI call failed (attempt {Attempt}/{Max}); retrying in {Backoff}",
                    attempt, attempts, backoff);
                await Task.Delay(backoff, ct);
            }
        }
    }

    /// <summary>
    /// Transient = worth retrying. A response with 429 or any 5xx is transient.
    /// A timeout/socket failure surfaces as HttpRequestException with a null
    /// StatusCode, or as TaskCanceledException when our own token wasn't the
    /// thing that cancelled (i.e. the HttpClient timeout fired) — both transient.
    /// Anything with a 4xx status (401/403/400/404) is NOT transient.
    /// </summary>
    private static bool IsTransient(Exception ex, CancellationToken ct) => ex switch
    {
        // A 2xx-but-empty completion is usually a one-off (a stray max_tokens/
        // pause_turn or a server-side hiccup); a re-ask normally returns text,
        // so retry it like a transient network failure rather than shipping a
        // 0-byte briefing.
        EmptyCompletionException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException http =>
            http.StatusCode == HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500,
        TaskCanceledException => !ct.IsCancellationRequested,
        _ => false
    };

    /// <summary>
    /// Posts the briefing to the briefings channel as TWO adjacent messages:
    ///   1. An embed acting as a per-week reference card (date range + model footer).
    ///   2. The full markdown briefing as a .md attachment.
    ///
    /// They are split because Discord's renderer always places attachments above
    /// embeds within a single message — there is no flag to reverse that. Sending
    /// the embed first as its own message keeps the card visually on top, which
    /// makes scrollback easier to scan.
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

        // Embed first — this is the visual header card for the week.
        await channel.SendMessageAsync(
            embed: embed,
            options: new RequestOptions { CancelToken = ct });

        // Then the .md attachment underneath.
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await channel.SendFileAsync(
            stream,
            filename,
            options: new RequestOptions { CancelToken = ct });

        logger.LogInformation(
            "Posted weekly briefing to channel {Id} (embed + attachment '{Filename}')",
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

    /// <summary>
    /// The most recent scheduled trigger at or before <paramref name="nowUtc"/>.
    /// Derived from ComputeNextRunUtc (which is always strictly future) by
    /// stepping back one week, so the two stay in lockstep by construction.
    /// </summary>
    internal DateTime ComputeMostRecentRunUtc(DateTime nowUtc) =>
        ComputeNextRunUtc(nowUtc).AddDays(-7);

    /// <summary>
    /// On startup, recover a missed scheduled briefing.
    ///
    /// ── Decision logic ──
    /// Let mostRecent = the most recent scheduled trigger that has already
    /// passed. Read BotState.LastBriefingCompletedUtc:
    ///   • null            → fresh DB / first deploy of this feature: run a
    ///                        catch-up for the most recent past week.
    ///   • &lt; mostRecent → the most recent scheduled briefing never completed
    ///                        (skipped by a post-trigger recreate, or it threw):
    ///                        run a catch-up for that week.
    ///   • &gt;= mostRecent → already done: no catch-up; the loop schedules the
    ///                        next one normally.
    ///
    /// Note: this recovers the single most recent missed week, not every
    /// historical gap — same scope as the auto-promotion catch-up. The window
    /// passed to the run is [mostRecent-7, mostRecent), NOT "now minus 7", so a
    /// catch-up that fires days late still reports the correct dates.
    /// </summary>
    private async Task RunCatchUpIfNeededAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var mostRecent = ComputeMostRecentRunUtc(now);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var state = await GetOrCreateBotStateAsync(db, ct);
        var lastRun = state.LastBriefingCompletedUtc;

        if (lastRun.HasValue && lastRun.Value >= mostRecent)
        {
            logger.LogInformation(
                "Weekly briefing: most recent scheduled run ({MostRecent:u}) already completed at {LastRun:u}; no catch-up needed",
                mostRecent, lastRun.Value);
            return;
        }

        logger.LogInformation(
            "Weekly briefing: catch-up triggered. Most recent scheduled run was {MostRecent:u}; last completed run was {LastRun}.",
            mostRecent,
            lastRun.HasValue ? lastRun.Value.ToString("u") : "never");

        await RunBriefingForWindowAsync(mostRecent.AddDays(-7), mostRecent, ct);
        await StampBriefingCompletedAsync(ct);
    }

    /// <summary>
    /// Records that a scheduled/catch-up briefing completed successfully.
    /// Best-effort: a failure here only costs an extra (harmless) catch-up on
    /// the next restart, so it never propagates out of a successful run.
    /// </summary>
    private async Task StampBriefingCompletedAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await GetOrCreateBotStateAsync(db, ct);
            state.LastBriefingCompletedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stamp BotState.LastBriefingCompletedUtc");
        }
    }

    /// <summary>
    /// Loads the singleton BotState row, creating it if missing. Always returns
    /// a tracked entity so callers can mutate fields and SaveChanges.
    /// Mirrors AutoPromotionService.GetOrCreateBotStateAsync.
    /// </summary>
    private static async Task<BotState> GetOrCreateBotStateAsync(BotDbContext db, CancellationToken ct)
    {
        var state = await db.BotStates.FirstOrDefaultAsync(ct);
        if (state is null)
        {
            state = new BotState();
            db.BotStates.Add(state);
            await db.SaveChangesAsync(ct);
        }
        return state;
    }
}
