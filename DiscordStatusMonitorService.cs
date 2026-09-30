using System.Net.Http.Json;
using System.Text.Json.Serialization;
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
/// Polls discordstatus.com's Statuspage JSON API and announces new /
/// updated incidents in the configured channel.
///
/// ── Source ──
/// <c>https://discordstatus.com/api/v2/incidents.json</c> returns the
/// last ~50 incidents in reverse-chronological order, each with a list
/// of <c>incident_updates</c> covering every status change (investigating,
/// identified, monitoring, resolved, postmortem). This endpoint
/// intentionally excludes scheduled maintenance — those live under
/// <c>/api/v2/scheduled-maintenances.json</c> and are not polled here,
/// so "ignore maintenance" comes for free.
///
/// ── Why polling, not webhooks ──
/// Discord's Statuspage webhook subscriptions don't fire for every
/// incident (Atlassian gates this on Discord's side). Polling the JSON
/// directly catches everything that gets posted to discordstatus.com.
/// The CDN caches the JSON aggressively so a 2-minute poll is the
/// practical floor — going faster doesn't buy fresher data.
///
/// ── Dedupe ──
/// Keyed on <c>incident_update.id</c>, not <c>incident.id</c>. Each
/// phase of an incident is its own update with a stable id, so a fresh
/// status flip ("identified" → "monitoring") posts as a new message
/// rather than re-posting the original. Dedupe rows live in
/// <see cref="DiscordStatusIncidentUpdate"/>.
///
/// ── First-run sync ──
/// On a fresh deploy (empty dedupe table) we seed the table from the
/// current API response without posting anything. Otherwise the first
/// poll would dump every incident from the past few months into the
/// channel. Subsequent polls only post updates whose id wasn't in the
/// seeded set.
///
/// ── Mentions ──
/// Default <c>@here</c>, configurable via
/// <see cref="BotConfig.DiscordStatusMention"/>. Same emit pattern as
/// <c>AuditLogWatcherHandler</c> — text + AllowedMentions.Everyone,
/// because Discord ignores @here in embed fields. Bot needs the
/// "Mention Everyone" permission in the target channel or the ping
/// renders as plain text.
///
/// ── Resilience ──
/// Failures (network, JSON, channel-resolve) log and retry on the next
/// tick. No state is lost; the next successful poll will re-evaluate
/// the same incidents and post anything still missing from the dedupe
/// table. Fail-open posture: a Statuspage outage shouldn't keep the
/// rest of the bot from running.
/// </summary>
public sealed class DiscordStatusMonitorService : BackgroundService
{
    private const string IncidentsUrl = "https://discordstatus.com/api/v2/incidents.json";
    private const string UserAgent =
        "ClanGuardBot/1.0 (Discord moderation bot for the 189th clan)";

    private static readonly TimeSpan StartupDelay     = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Named HttpClient for this service, registered in Program.cs. It exists
    /// only to attach an explicit ConnectTimeout: the default handler has none,
    /// so a connect that hangs eats the whole <see cref="RequestTimeout"/>
    /// budget before the failure is even logged.
    /// </summary>
    public const string HttpClientName = "discordstatus";

    /// <summary>Connect-phase budget. Consumed by Program.cs when it builds the handler.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Whole-request budget: connect, TLS, headers and body.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiscordSocketClient _client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscordStatusMonitorService> _logger;
    private readonly BotConfig _config;

    public DiscordStatusMonitorService(
        IHttpClientFactory httpClientFactory,
        DiscordSocketClient client,
        IServiceScopeFactory scopeFactory,
        ILogger<DiscordStatusMonitorService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _client            = client;
        _scopeFactory      = scopeFactory;
        _logger            = logger;
        _config            = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.DiscordStatusMonitorEnabled)
        {
            _logger.LogInformation(
                "DiscordStatusMonitorService is disabled (DiscordStatusMonitorEnabled=false) — skipping.");
            return;
        }

        if (_config.DiscordStatusChannelId == 0)
        {
            _logger.LogWarning(
                "DiscordStatusChannelId is 0 — service will not post. Set it in BotConfig.");
            return;
        }

        // Wait for the Discord gateway to come up. Channel resolution
        // via _client.GetChannel returns null until the bot has joined
        // and cached the relevant guild.
        while (_client.ConnectionState != ConnectionState.Connected
               && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        if (stoppingToken.IsCancellationRequested) return;

        await Task.Delay(StartupDelay, stoppingToken);

        var pollInterval = TimeSpan.FromMinutes(
            Math.Max(1, _config.DiscordStatusPollIntervalMinutes));

        _logger.LogInformation(
            "DiscordStatusMonitorService starting. Poll={Poll}, Channel={ChannelId}, Mention='{Mention}'",
            pollInterval, _config.DiscordStatusChannelId, _config.DiscordStatusMention);

        while (!stoppingToken.IsCancellationRequested)
        {
            var ok = false;
            try
            {
                ok = await PollAndPostAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in DiscordStatusMonitorService cycle.");
            }

            try
            {
                await Task.Delay(ok ? pollInterval : RetryAfterFailure, stoppingToken);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task<bool> PollAndPostAsync(CancellationToken ct)
    {
        // ── Fetch ───────────────────────────────────────────────────────
        StatuspageIncidentsResponse? response;
        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            http.Timeout = RequestTimeout;

            using var req = new HttpRequestMessage(HttpMethod.Get, IncidentsUrl);
            req.Headers.UserAgent.ParseAdd(UserAgent);

            using var httpResponse = await http.SendAsync(req, ct);
            httpResponse.EnsureSuccessStatusCode();

            response = await httpResponse.Content
                .ReadFromJsonAsync<StatuspageIncidentsResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DiscordStatusMonitorService: fetch failed.");
            await RecordPollErrorAsync($"Fetch failed: {ex.Message}", ct);
            return false;
        }

        if (response?.Incidents is null)
        {
            _logger.LogWarning("DiscordStatusMonitorService: empty response from Statuspage.");
            await RecordPollErrorAsync("Empty response from Statuspage", ct);
            return false;
        }

        // ── Dedupe + first-run seed ────────────────────────────────────
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var isFirstRun = !await db.DiscordStatusIncidentUpdates.AnyAsync(ct);

        var knownIds = new HashSet<string>(
            await db.DiscordStatusIncidentUpdates
                .Select(x => x.UpdateId)
                .ToListAsync(ct),
            StringComparer.Ordinal);

        // Flatten incident → updates and order oldest-first so a burst of
        // new updates posts in chronological order rather than the API's
        // reverse-chronological default.
        var pending = response.Incidents
            .Where(i => !string.IsNullOrEmpty(i.Id))
            .SelectMany(i => (i.IncidentUpdates ?? new List<IncidentUpdateDto>())
                .Where(u => !string.IsNullOrEmpty(u.Id) && !knownIds.Contains(u.Id))
                .Select(u => (Incident: i, Update: u)))
            .OrderBy(t => t.Update.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();

        // A quiet cycle is still a successful poll. Stamp liveness (which
        // also clears any stale error) before returning, otherwise the
        // /health timestamp only advances when Discord actually has an
        // incident: a perfectly healthy monitor reads as dead for weeks,
        // and a single transient fetch error sticks forever because the
        // only code that clears it is never reached.
        if (pending.Count == 0)
        {
            await StampPollSuccessAsync(db, ct);
            return true;
        }

        IMessageChannel? channel = null;
        if (!isFirstRun)
        {
            channel = _client.GetChannel(_config.DiscordStatusChannelId) as IMessageChannel;
            if (channel is null)
            {
                // Recorded through the same path as a fetch failure, not just
                // logged. This branch is only reachable when there ARE updates
                // waiting to post, so silently returning false would leave
                // /health showing a frozen "last good poll" with a zero failure
                // count: identical to healthy-but-quiet, which is exactly the
                // ambiguity the counter exists to remove.
                _logger.LogWarning(
                    "DiscordStatusMonitorService: channel {ChannelId} did not resolve. " +
                    "Skipping this cycle, will retry.",
                    _config.DiscordStatusChannelId);
                await RecordPollErrorAsync(
                    $"Channel {_config.DiscordStatusChannelId} did not resolve "
                    + "(check DiscordStatusChannelId and the bot's access to it)", ct);
                return false;
            }
        }

        var newRows = 0;
        foreach (var (incident, update) in pending)
        {
            db.DiscordStatusIncidentUpdates.Add(new DiscordStatusIncidentUpdate
            {
                UpdateId   = update.Id!,
                IncidentId = incident.Id!,
                Status     = update.Status ?? string.Empty,
                PostedAt   = DateTime.UtcNow,
                Seeded     = isFirstRun,
            });
            newRows++;

            if (!isFirstRun && channel is not null)
            {
                // Saved before posting so a restart mid-burst can't repeat the ping.
                await db.SaveChangesAsync(CancellationToken.None);
                try
                {
                    await PostUpdateAsync(channel, incident, update);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "DiscordStatusMonitorService: failed to post update {UpdateId} " +
                        "(incident {IncidentId}). Will not retry — dedupe row is already saved.",
                        update.Id, incident.Id);
                }
            }
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);

        // ── Stamp BotState liveness ───────────────────────────────────
        // Done unconditionally on a successful poll, even when no new rows
        // were added. Surfaced on /health so officers can confirm the
        // monitor is alive during quiet stretches.
        await StampPollSuccessAsync(db, ct);

        if (isFirstRun)
        {
            _logger.LogInformation(
                "DiscordStatusMonitorService: first-run sync complete — seeded {Count} updates, " +
                "no messages posted.",
                newRows);
        }
        else
        {
            _logger.LogInformation(
                "DiscordStatusMonitorService: posted {Count} new update(s) to channel {ChannelId}.",
                newRows, _config.DiscordStatusChannelId);
        }

        return true;
    }

    /// <summary>
    /// Stamps a successful poll on the BotState row: refreshes both the
    /// completed and attempt timestamps, clears any previous error, and resets
    /// the consecutive-failure counter. Idempotent, and runs inside the same
    /// DbContext scope as the poll.
    ///
    /// Must be reached on EVERY successful poll including quiet ones that found
    /// nothing to post. An early return that skips it freezes the /health
    /// timestamp at the last poll that happened to have work to do, which reads
    /// as a dead monitor.
    ///
    /// If an escalation was outstanding, posts the recovery notice afterwards.
    /// </summary>
    private async Task StampPollSuccessAsync(BotDbContext db, CancellationToken ct)
    {
        var state = await db.BotStates.FirstOrDefaultAsync(ct);
        if (state is null)
        {
            state = new BotState();
            db.BotStates.Add(state);
        }

        // Captured before the reset so the recovery notice can report how bad
        // it got.
        var failures  = state.DiscordStatusPollConsecutiveFailures;
        var alertedAt = state.DiscordStatusPollAlertedAtUtc;

        var now = DateTime.UtcNow;
        state.LastDiscordStatusPollCompletedUtc     = now;
        state.LastDiscordStatusPollAttemptUtc       = now;
        state.LastDiscordStatusPollError            = null;
        state.DiscordStatusPollConsecutiveFailures  = 0;
        state.DiscordStatusPollAlertedAtUtc         = null;
        await db.SaveChangesAsync(ct);

        // Posted after the save, deliberately. If Discord rejects the message
        // the cleared state still stands, so the next failure can escalate
        // again rather than being suppressed by a stale alert marker.
        if (alertedAt.HasValue)
            await PostMonitorAlertAsync(recovered: true, failures: failures, lastGoodPoll: null, error: null);
    }

    /// <summary>
    /// Stamps a failed poll: refreshes the attempt timestamp, records the
    /// message, and increments the consecutive-failure counter. Deliberately
    /// preserves LastDiscordStatusPollCompletedUtc so /health can show "last
    /// good poll 12m ago" alongside "8 consecutive failures". Uses a fresh
    /// scope so an EF or DB error inside the main poll cannot poison this write.
    ///
    /// Escalates to the alert channel exactly once per outage, on the poll that
    /// crosses BotConfig.DiscordStatusFailureAlertThreshold. The marker that
    /// keeps it to once is DiscordStatusPollAlertedAtUtc.
    /// </summary>
    private async Task RecordPollErrorAsync(string message, CancellationToken ct)
    {
        var       shouldAlert  = false;
        var       failures     = 0;
        DateTime? lastGoodPoll = null;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await db.BotStates.FirstOrDefaultAsync(ct);
            if (state is null)
            {
                state = new BotState();
                db.BotStates.Add(state);
            }

            state.LastDiscordStatusPollError =
                message.Length > 800 ? message[..800] : message;
            state.LastDiscordStatusPollAttemptUtc = DateTime.UtcNow;
            state.DiscordStatusPollConsecutiveFailures++;

            failures     = state.DiscordStatusPollConsecutiveFailures;
            lastGoodPoll = state.LastDiscordStatusPollCompletedUtc;

            var threshold = _config.DiscordStatusFailureAlertThreshold;
            if (threshold > 0
                && failures >= threshold
                && state.DiscordStatusPollAlertedAtUtc is null)
            {
                state.DiscordStatusPollAlertedAtUtc = DateTime.UtcNow;
                shouldAlert = true;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DiscordStatusMonitorService: failed to record poll error to BotState.");
            // The marker may not have persisted, so do not post: an alert whose
            // suppression marker was never saved would repeat every retry.
            return;
        }

        if (shouldAlert)
            await PostMonitorAlertAsync(recovered: false, failures: failures,
                                        lastGoodPoll: lastGoodPoll, error: message);
    }

    /// <summary>
    /// Posts the "monitor is failing" escalation or its matching recovery
    /// notice to <see cref="BotConfig.DiscordStatusAlertChannelId"/>, falling
    /// back to <see cref="BotConfig.HqChannelId"/>.
    ///
    /// Never throws: an alert that cannot be delivered must not take down the
    /// poll loop that is already having a bad day. Sends with
    /// AllowedMentions.None because this is officer plumbing, not something to
    /// ping the server about.
    /// </summary>
    private async Task PostMonitorAlertAsync(
        bool recovered, int failures, DateTime? lastGoodPoll, string? error)
    {
        try
        {
            var channelId = _config.DiscordStatusAlertChannelId != 0
                ? _config.DiscordStatusAlertChannelId
                : _config.HqChannelId;

            if (channelId == 0)
            {
                _logger.LogWarning(
                    "DiscordStatusMonitorService: wanted to post a monitor alert but neither "
                    + "DiscordStatusAlertChannelId nor HqChannelId is set.");
                return;
            }

            if (_client.GetChannel(channelId) is not IMessageChannel channel)
            {
                _logger.LogWarning(
                    "DiscordStatusMonitorService: alert channel {ChannelId} did not resolve.",
                    channelId);
                return;
            }

            EmbedBuilder embed;
            if (recovered)
            {
                embed = new EmbedBuilder()
                    .WithTitle("Discord status monitor is working again")
                    .WithDescription(
                        $"Polling recovered after **{failures:N0}** failed attempt(s). "
                        + "Discord incident announcements are going out normally again.")
                    .WithColor(Color.Green);
            }
            else
            {
                embed = new EmbedBuilder()
                    .WithTitle("Discord status monitor is failing")
                    .WithDescription(
                        $"The last **{failures:N0}** attempts to read discordstatus.com have failed, "
                        + "so Discord incident announcements are not going out. Nothing else in the "
                        + "bot is affected, and the monitor keeps retrying on its own.")
                    .WithColor(new Color(0xE67E22))
                    .AddField("Last good poll", RelativeStamp(lastGoodPoll), inline: true)
                    .AddField("Retrying", "every minute", inline: true)
                    .AddField("Error", $"```{Truncate(error ?? "unknown", 300)}```", inline: false);
            }

            await channel.SendMessageAsync(
                embed: embed
                    .WithFooter("ClanGuard • Discord Status Monitor")
                    .WithCurrentTimestamp()
                    .Build(),
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DiscordStatusMonitorService: failed to post the monitor {Kind} alert.",
                recovered ? "recovery" : "failure");
        }
    }

    /// <summary>Discord relative timestamp, or "never" for a null.</summary>
    private static string RelativeStamp(DateTime? utc) =>
        utc.HasValue
            ? $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds()}:R>"
            : "never";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    private async Task PostUpdateAsync(
        IMessageChannel channel,
        IncidentDto incident,
        IncidentUpdateDto update)
    {
        var status = (update.Status ?? incident.Status ?? "unknown").ToLowerInvariant();
        var impact = (incident.Impact ?? "none").ToLowerInvariant();

        // Resolved is always green regardless of impact — it's good news.
        // Otherwise color by severity.
        var color = status == "resolved"
            ? Color.Green
            : impact switch
            {
                "critical" => Color.Red,
                "major"    => new Color(0xE67E22),  // orange
                "minor"    => new Color(0xF1C40F),  // yellow
                _          => Color.LightGrey,
            };

        var title  = incident.Name ?? "Discord Incident";
        var url    = !string.IsNullOrWhiteSpace(incident.Shortlink) ? incident.Shortlink
                                                                    : "https://discordstatus.com";
        var body   = string.IsNullOrWhiteSpace(update.Body) ? "(no details provided)" : update.Body!;
        var stamp  = update.DisplayAt ?? update.CreatedAt ?? DateTimeOffset.UtcNow;

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithUrl(url)
            .WithDescription(body)
            .WithColor(color)
            .AddField("Status", Capitalize(status), inline: true)
            .AddField("Impact", Capitalize(impact), inline: true)
            .WithFooter("ClanGuard • Discord Status Monitor")
            .WithTimestamp(stamp)
            .Build();

        // ── Mention plumbing ──────────────────────────────────────────
        // Mirrors AuditLogWatcherHandler. Discord ignores @here / @everyone
        // inside embed fields — the mention has to be in the message text
        // AND the AllowedMentions flag has to permit it.
        string messageContent = string.Empty;
        AllowedMentions allowedMentions = AllowedMentions.None;

        var mention = _config.DiscordStatusMention;
        if (!string.IsNullOrWhiteSpace(mention))
        {
            messageContent = mention;
            if (mention.Contains("@here", StringComparison.OrdinalIgnoreCase)
                || mention.Contains("@everyone", StringComparison.OrdinalIgnoreCase))
            {
                allowedMentions = new AllowedMentions
                {
                    AllowedTypes = AllowedMentionTypes.Everyone,
                };
            }
        }

        await channel.SendMessageAsync(
            text:            messageContent,
            embed:           embed,
            allowedMentions: allowedMentions);
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ── Statuspage DTOs ────────────────────────────────────────────────
    // Subset of the public Statuspage v2 schema. Only the fields we
    // actually consume are deserialized; everything else is dropped.

    private sealed class StatuspageIncidentsResponse
    {
        [JsonPropertyName("incidents")]
        public List<IncidentDto>? Incidents { get; set; }
    }

    private sealed class IncidentDto
    {
        [JsonPropertyName("id")]               public string? Id { get; set; }
        [JsonPropertyName("name")]             public string? Name { get; set; }
        [JsonPropertyName("status")]           public string? Status { get; set; }
        [JsonPropertyName("impact")]           public string? Impact { get; set; }
        [JsonPropertyName("shortlink")]        public string? Shortlink { get; set; }
        [JsonPropertyName("created_at")]       public DateTimeOffset? CreatedAt { get; set; }
        [JsonPropertyName("incident_updates")] public List<IncidentUpdateDto>? IncidentUpdates { get; set; }
    }

    private sealed class IncidentUpdateDto
    {
        [JsonPropertyName("id")]         public string? Id { get; set; }
        [JsonPropertyName("status")]     public string? Status { get; set; }
        [JsonPropertyName("body")]       public string? Body { get; set; }
        [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
        [JsonPropertyName("display_at")] public DateTimeOffset? DisplayAt { get; set; }
    }
}