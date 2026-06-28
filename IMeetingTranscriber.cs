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
        // Resumability: if a previous attempt's transcription already completed and
        // wrote transcript.json to the shared volume — e.g. the HTTP call timed out
        // (the sidecar keeps running after the client disconnects) or the bot
        // restarted mid-call — reuse it instead of re-running Whisper from scratch.
        // transcript.json is written atomically by the sidecar, so it's only ever
        // present complete.
        var cached = TryReadCachedTranscript(audioDir);
        if (cached is not null)
        {
            _logger.LogInformation(
                "Reusing existing transcript from {Dir} ({Segments} segment(s), {Speakers} speaker(s)) — skipping re-transcription.",
                audioDir, cached.SegmentCount, cached.SpeakerCount);
            return cached;
        }

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

    /// <summary>
    /// Read a completed transcript.json the sidecar previously wrote into the
    /// shared audio dir, if present and non-empty. Returns null when there's no
    /// usable cached transcript (so the caller transcribes normally). The bot and
    /// sidecar share this volume at the same path, so the dir is readable here.
    /// </summary>
    private MeetingTranscript? TryReadCachedTranscript(string audioDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(audioDir)) return null;
            var path = Path.Combine(audioDir, "transcript.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (!root.TryGetProperty("transcript", out var transcriptEl)) return null;
            var transcript = transcriptEl.GetString();
            if (string.IsNullOrWhiteSpace(transcript)) return null;

            var segmentCount = 0;
            var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
            {
                segmentCount = segs.GetArrayLength();
                foreach (var s in segs.EnumerateArray())
                    if (s.TryGetProperty("displayName", out var dn) && dn.GetString() is { } name && name.Length > 0)
                        speakers.Add(name);
            }

            return new MeetingTranscript(transcript, segmentCount, speakers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read cached transcript in {Dir}; will re-transcribe.", audioDir);
            return null;
        }
    }

    private sealed record TranscribeResponse(string? Transcript, int SegmentCount, int SpeakerCount);
}
