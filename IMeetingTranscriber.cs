using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Turns a recorded meeting directory (per-speaker Ogg segments + manifest.json)
/// into one chronological, speaker-labelled transcript. The production
/// implementation calls the local faster-whisper sidecar over HTTP; the
/// interface exists so a hosted Whisper API could be swapped in later without
/// touching MeetingMinutesService.
/// </summary>
public interface IMeetingTranscriber
{
    Task<MeetingTranscript> TranscribeAsync(string audioDir, CancellationToken ct);
}

/// <summary>Result of transcribing a meeting.</summary>
public sealed record MeetingTranscript(string Transcript, int SegmentCount, int SpeakerCount);

/// <summary>
/// Calls the transcriber sidecar's /transcribe endpoint. Uses a long timeout —
/// transcription of an hour of speech can take several minutes, and this runs
/// on a background worker with no user waiting. Throws on failure so the
/// MeetingMinutesService can leave the row in Transcribing and retry.
/// </summary>
public sealed class HttpMeetingTranscriber : IMeetingTranscriber
{
    private const string HttpClientName = "meeting-transcriber";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpMeetingTranscriber> _logger;
    private readonly BotConfig _config;

    public HttpMeetingTranscriber(
        IHttpClientFactory httpFactory,
        ILogger<HttpMeetingTranscriber> logger,
        IOptions<BotConfig> config)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _config = config.Value;
    }

    public async Task<MeetingTranscript> TranscribeAsync(string audioDir, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(_config.MeetingTranscriberBaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromMinutes(_config.MeetingTranscriberTimeoutMinutes);
        if (!string.IsNullOrEmpty(_config.MeetingTranscriberSharedSecret))
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "x-transcriber-secret", _config.MeetingTranscriberSharedSecret);

        using var resp = await client.PostAsJsonAsync("transcribe", new { audioDir }, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string detail;
            try { detail = await resp.Content.ReadAsStringAsync(ct); }
            catch { detail = "(no body)"; }
            throw new HttpRequestException(
                $"Transcriber /transcribe returned {(int)resp.StatusCode}: {detail}");
        }

        var result = await resp.Content.ReadFromJsonAsync<TranscribeResponse>(Json, ct)
            ?? throw new InvalidOperationException("Empty response from transcriber.");

        _logger.LogInformation(
            "Transcribed {Segments} segment(s) across {Speakers} speaker(s) from {Dir}.",
            result.SegmentCount, result.SpeakerCount, audioDir);

        return new MeetingTranscript(result.Transcript ?? string.Empty, result.SegmentCount, result.SpeakerCount);
    }

    private sealed record TranscribeResponse(string? Transcript, int SegmentCount, int SpeakerCount);
}
