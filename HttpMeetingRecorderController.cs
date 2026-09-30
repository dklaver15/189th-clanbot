using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Real <see cref="IMeetingRecorderController"/> — drives the @discordjs/voice
/// recorder sidecar over its localhost/compose-network HTTP control API.
/// Registered in place of <see cref="LoggingMeetingRecorderController"/> when
/// BotConfig.MeetingRecorderBaseUrl is set (see Program.cs).
///
/// Snowflake IDs are sent as strings, not numbers: a Discord id can exceed
/// 2^53 and would lose precision as a JSON number on the JS side.
///
/// Failures throw. That's intentional — MeetingRecordingScheduler catches a
/// failed StartRecordingAsync, leaves the row in Announced, and retries on the
/// next poll until the recording window closes, so a sidecar that's briefly
/// down (e.g. mid-redeploy) doesn't lose the meeting.
/// </summary>
public sealed class HttpMeetingRecorderController : IMeetingRecorderController
{
    private const string HttpClientName = "meeting-recorder";

    // /record waits for the voice connection (up to 30s in the recorder) plus the
    // guild/channel fetch, and /stop can wait on a join still in progress before
    // flushing (~20s), so both need longer than that or a slow-but-successful
    // call looks like a failure here.
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpMeetingRecorderController> _logger;
    private readonly BotConfig _config;

    public HttpMeetingRecorderController(
        IHttpClientFactory httpFactory,
        ILogger<HttpMeetingRecorderController> logger,
        IOptions<BotConfig> config)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _config = config.Value;
    }

    public async Task StartRecordingAsync(MeetingRecorderStartContext ctx, CancellationToken ct)
    {
        var client = CreateClient(ControlTimeout);
        var body = new
        {
            meetingRecordingId = ctx.MeetingRecordingId,
            guildId = ctx.GuildId.ToString(),
            voiceChannelId = ctx.VoiceChannelId.ToString(),
            meetingTitle = ctx.MeetingTitle,
            expectedStopUtc = ctx.ExpectedStopUtc.ToUniversalTime().ToString("o"),
        };

        using var resp = await client.PostAsJsonAsync("record", body, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(resp, ct);
            throw new HttpRequestException(
                $"Recorder /record returned {(int)resp.StatusCode}: {detail}");
        }

        _logger.LogInformation(
            "Recorder started capture for '{Title}' (#{Id}) in VC {ChannelId}.",
            ctx.MeetingTitle, ctx.MeetingRecordingId, ctx.VoiceChannelId);
    }

    public async Task<string?> StopRecordingAsync(int meetingRecordingId, CancellationToken ct)
    {
        var client = CreateClient(ControlTimeout);
        var body = new { meetingRecordingId };

        using var resp = await client.PostAsJsonAsync("stop", body, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(resp, ct);
            throw new HttpRequestException(
                $"Recorder /stop returned {(int)resp.StatusCode}: {detail}");
        }

        var result = await resp.Content.ReadFromJsonAsync<StopResponse>(Json, ct);
        _logger.LogInformation(
            "Recorder stopped capture for #{Id}; audio dir: {Dir}.",
            meetingRecordingId, result?.AudioDir ?? "(none)");
        return result?.AudioDir;
    }

    public async Task<MeetingRecorderStatus?> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            var client = CreateClient(DefaultTimeout);
            using var resp = await client.GetAsync("health", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var health = await resp.Content.ReadFromJsonAsync<HealthResponse>(Json, ct);
            if (health is null) return null;

            var finalized = new Dictionary<int, string?>();
            foreach (var id in health.Finalized ?? [])
                finalized[id] = null;
            foreach (var (key, reason) in health.FinalizedReasons ?? [])
                if (int.TryParse(key, out var id))
                    finalized[id] = reason;

            return new MeetingRecorderStatus(health.Ready, health.Recording, finalized);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Unknown → null, so the scheduler relies on its backstop. Never throw:
            // a health blip must not disturb the state machine.
            _logger.LogDebug(ex, "Recorder /health check failed; will rely on the backstop.");
            return null;
        }
    }

    private HttpClient CreateClient(TimeSpan timeout)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(_config.MeetingRecorderBaseUrl.TrimEnd('/') + "/");
        client.Timeout = timeout;
        if (!string.IsNullOrEmpty(_config.MeetingRecorderSharedSecret))
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "x-recorder-secret", _config.MeetingRecorderSharedSecret);
        return client;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return "(no body)"; }
    }

    private sealed record StopResponse(string? AudioDir);

    private sealed record HealthResponse(
        bool Ok, bool Ready, int? Recording, List<int>? Finalized, Dictionary<string, string?>? FinalizedReasons);
}
