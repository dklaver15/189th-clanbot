using System.Text;
using ClanGuardBot.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Uploads a local video file to the clan's YouTube channel.
///
/// ── Why OAuth and not google-credentials.json ──
/// The YouTube Data API does not let a service account own or post to a
/// channel. Uploads have to be made AS the Google account that owns the
/// channel, so this signs in with a user OAuth client plus a refresh token
/// minted once by that account (<see cref="BotConfig.YouTubeRefreshToken"/>).
/// The Google client library swaps the refresh token for a short-lived access
/// token on demand.
///
/// ── Quota ──
/// Since 2026-06-01 videos.insert has its own quota bucket: 100 uploads per
/// project per day by default, separate from the 10,000-unit pool the other
/// endpoints share. Only uploads count; forwards cost nothing. A quotaExceeded error
/// surfaces to the uploader as a normal failure and the button stays live.
/// </summary>
public sealed class YouTubeUploadService
{
    private const string ApplicationName = "ClanGuardBot";

    // YouTube rejects titles over 100 characters and any title or description
    // containing '<' or '>'. Descriptions are capped at 5,000 bytes, and tags at
    // 500 characters counting a comma between each.
    private const int MaxTitleLength = 100;
    private const int MaxDescriptionBytes = 5000;
    private const int MaxTagsLength = 500;

    private readonly BotConfig _config;
    private readonly ILogger<YouTubeUploadService> _logger;

    public YouTubeUploadService(IOptions<BotConfig> config, ILogger<YouTubeUploadService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public bool IsConfigured => _config.IsYouTubeConfigured;

    /// <summary>
    /// Uploads the file and returns the new YouTube video id. The
    /// <paramref name="hashtags"/> (no <c>#</c>) end the description on their own
    /// line and are also sent as the video's tags.
    /// </summary>
    public async Task<string> UploadAsync(
        string localPath,
        string title,
        string description,
        IReadOnlyList<string> hashtags,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("YouTube upload is not configured.");

        using var service = CreateService();

        var video = new Video
        {
            Snippet = new VideoSnippet
            {
                Title       = SanitizeTitle(title),
                Description = ComposeDescription(description, hashtags),
                Tags        = BuildTags(hashtags),
                CategoryId  = _config.YouTubeCategoryId,
            },
            Status = new VideoStatus
            {
                PrivacyStatus           = _config.YouTubePrivacyStatus,
                SelfDeclaredMadeForKids = false,
            },
        };

        await using var fs = File.OpenRead(localPath);
        var request = service.Videos.Insert(video, "snippet,status", fs, "video/*");
        // 8 MiB chunks: resumable upload retries a failed chunk rather than the
        // whole file, and it keeps the buffer small on the 640m container.
        request.ChunkSize = 8 * ResumableUpload.MinimumChunkSize;

        var progress = await request.UploadAsync(ct);
        if (progress.Status != UploadStatus.Completed)
        {
            throw new InvalidOperationException(
                $"YouTube upload did not complete: {progress.Status} — {progress.Exception?.Message ?? "unknown"}",
                progress.Exception);
        }

        var id = request.ResponseBody.Id;
        _logger.LogInformation(
            "Uploaded video to YouTube: id={Id} title={Title} privacy={Privacy}",
            id, video.Snippet.Title, request.ResponseBody.Status?.PrivacyStatus);
        return id;
    }

    private YouTubeService CreateService()
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId     = _config.YouTubeClientId,
                ClientSecret = _config.YouTubeClientSecret,
            },
            Scopes = new[] { YouTubeService.Scope.YoutubeUpload },
        });

        var credential = new UserCredential(
            flow, "clan-youtube", new TokenResponse { RefreshToken = _config.YouTubeRefreshToken });

        return new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName       = ApplicationName,
        });
    }

    private static string StripAngles(string s) => s.Replace("<", "").Replace(">", "");

    private static string SanitizeTitle(string title)
    {
        var t = StripAngles(title).Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (t.Length == 0) t = "Clan clip";
        return t.Length <= MaxTitleLength ? t : t[..(MaxTitleLength - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The description as sent: angle brackets stripped, then the hashtags on
    /// their own last line. A description too long for YouTube's byte cap is cut
    /// above that line, so the hashtags always survive.
    /// </summary>
    internal static string ComposeDescription(string description, IReadOnlyList<string> hashtags)
    {
        var body = StripAngles(description).Trim();
        var line = Hashtags.Format(hashtags);
        var tail = line.Length == 0 ? "" : body.Length == 0 ? line : "\n\n" + line;

        // Byte cap, not char cap: cut a text element at a time, so an emoji is never split.
        var budget = MaxDescriptionBytes - Encoding.UTF8.GetByteCount(tail);
        if (Encoding.UTF8.GetByteCount(body) > budget)
            body = body[..TextElements.FittingLength(body, budget, Encoding.UTF8.GetByteCount)].TrimEnd();
        return body + tail;
    }

    /// <summary>The hashtags as YouTube tags, in order, as many as fit the 500-character cap.</summary>
    internal static List<string> BuildTags(IReadOnlyList<string> hashtags)
    {
        var tags = new List<string>();
        var length = 0;
        foreach (var tag in hashtags)
        {
            var next = length + tag.Length + (tags.Count > 0 ? 1 : 0);
            if (next > MaxTagsLength) break;
            tags.Add(tag);
            length = next;
        }
        return tags;
    }
}
