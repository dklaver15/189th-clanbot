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
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);

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
            return false;
        }

        if (response?.Incidents is null)
        {
            _logger.LogWarning("DiscordStatusMonitorService: empty response from Statuspage.");
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

        if (pending.Count == 0) return true;

        IMessageChannel? channel = null;
        if (!isFirstRun)
        {
            channel = _client.GetChannel(_config.DiscordStatusChannelId) as IMessageChannel;
            if (channel is null)
            {
                _logger.LogWarning(
                    "DiscordStatusMonitorService: channel {ChannelId} did not resolve. " +
                    "Skipping this cycle — will retry.",
                    _config.DiscordStatusChannelId);
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
                try
                {
                    await PostUpdateAsync(channel, incident, update);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "DiscordStatusMonitorService: failed to post update {UpdateId} " +
                        "(incident {IncidentId}). Will not retry — dedupe row will be saved.",
                        update.Id, incident.Id);
                }
            }
        }

        if (newRows > 0)
            await db.SaveChangesAsync(ct);

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
