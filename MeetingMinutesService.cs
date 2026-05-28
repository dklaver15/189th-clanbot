using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

namespace ClanGuardBot.Services;

/// <summary>
/// The back half of the meeting-recording pipeline: it picks up MeetingRecording
/// rows the scheduler parked in <see cref="MeetingRecordingState.Transcribing"/>
/// and drives them to Posted, then prunes old audio.
///
/// Per row:
///   Transcribing → call the transcriber sidecar → store TranscriptText →
///                  ask Claude for minutes + action items → store them → Summarized
///   Summarized   → post the minutes to Discord → Posted → run keep-last-N prune
///
/// Restart-safe and idempotent at each step: TranscriptText is persisted before
/// the (more expensive) Claude call, and MinutesText before posting, so a crash
/// resumes without redoing finished work. Failures leave the row in place to
/// retry next tick; a row stuck too long is failed out.
///
/// Feature-gated on BotConfig.MeetingTranscriberBaseUrl — empty means the
/// transcriber sidecar isn't deployed, so this service is a quiet no-op and
/// recordings simply accumulate in Transcribing until it's configured.
/// </summary>
public class MeetingMinutesService : BackgroundService
{
    /// <summary>A row sitting in Transcribing/Summarized longer than this is failed out.</summary>
    private static readonly TimeSpan MaxProcessingAge = TimeSpan.FromHours(6);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly IMeetingTranscriber _transcriber;
    private readonly IAiService _ai;
    private readonly ILogger<MeetingMinutesService> _logger;
    private readonly BotConfig _config;

    public MeetingMinutesService(
        IServiceProvider services,
        DiscordSocketClient client,
        IMeetingTranscriber transcriber,
        IAiService ai,
        ILogger<MeetingMinutesService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _transcriber = transcriber;
        _ai = ai;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_config.MeetingTranscriberBaseUrl))
        {
            _logger.LogInformation(
                "MeetingMinutesService disabled: MeetingTranscriberBaseUrl is unset.");
            return;
        }

        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.MeetingRecordingPollIntervalMinutes));
        _logger.LogInformation(
            "MeetingMinutesService started. Transcriber={Url}, Retain={Retain}, Poll={Poll}min.",
            _config.MeetingTranscriberBaseUrl, _config.MeetingRecordingRetainCount,
            _config.MeetingRecordingPollIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MeetingMinutesService poll failed; will retry next interval.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var now = DateTime.UtcNow;

        // Process one in-flight recording per tick (transcription is heavy).
        var rec = await db.MeetingRecordings
            .Where(m => m.State == MeetingRecordingState.Transcribing
                     || m.State == MeetingRecordingState.Summarized)
            .OrderBy(m => m.MeetingStartUtc)
            .FirstOrDefaultAsync(ct);

        if (rec is null)
        {
            // Nothing to process — still run retention so prunable audio gets cleaned.
            await RunRetentionAsync(db, now, ct);
            return;
        }

        if (now - rec.StateUpdatedUtc > MaxProcessingAge)
        {
            rec.ErrorMessage = $"Stuck in {rec.State} for over {MaxProcessingAge.TotalHours:0}h — giving up.";
            _logger.LogWarning("Minutes pipeline for '{Title}' (#{Id}) {Msg}",
                rec.MeetingTitle, rec.Id, rec.ErrorMessage);
            Transition(rec, MeetingRecordingState.Failed, now);
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            if (rec.State == MeetingRecordingState.Transcribing)
            {
                if (string.IsNullOrWhiteSpace(rec.AudioDirPath))
                {
                    rec.ErrorMessage = "No audio was captured for this meeting.";
                    _logger.LogWarning("'{Title}' (#{Id}) has no audio dir — failing.", rec.MeetingTitle, rec.Id);
                    Transition(rec, MeetingRecordingState.Failed, now);
                    await db.SaveChangesAsync(ct);
                    return;
                }

                // Transcribe (skip if a prior attempt already produced the text).
                if (string.IsNullOrWhiteSpace(rec.TranscriptText))
                {
                    var result = await _transcriber.TranscribeAsync(rec.AudioDirPath, ct);
                    if (string.IsNullOrWhiteSpace(result.Transcript))
                    {
                        rec.ErrorMessage = "Transcript was empty (no speech captured).";
                        _logger.LogWarning("'{Title}' (#{Id}) produced an empty transcript — failing.",
                            rec.MeetingTitle, rec.Id);
                        Transition(rec, MeetingRecordingState.Failed, now);
                        await db.SaveChangesAsync(ct);
                        return;
                    }

                    rec.TranscriptText = result.Transcript;
                    rec.TranscriptPath = Path.Combine(rec.AudioDirPath, "transcript.txt");
                    rec.ErrorMessage = null;
                    // Persist the transcript BEFORE the costly Claude call so a
                    // crash here doesn't re-run Whisper.
                    await db.SaveChangesAsync(ct);
                }

                var (minutes, actionItemsJson) = await GenerateMinutesAsync(rec, ct);
                rec.MinutesText = minutes;
                rec.ActionItemsJson = actionItemsJson;
                rec.ErrorMessage = null;
                Transition(rec, MeetingRecordingState.Summarized, now);
                await db.SaveChangesAsync(ct);
            }

            if (rec.State == MeetingRecordingState.Summarized)
            {
                var messageId = await PostMinutesAsync(rec, ct);
                if (messageId.HasValue) rec.MinutesMessageId = messageId;
                rec.ErrorMessage = null;
                Transition(rec, MeetingRecordingState.Posted, now);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Posted minutes for '{Title}' (#{Id}).", rec.MeetingTitle, rec.Id);

                await RunRetentionAsync(db, now, ct);
            }
        }
        catch (Exception ex)
        {
            rec.ErrorMessage = Truncate(ex.Message, 800);
            _logger.LogError(ex, "Minutes pipeline failed for '{Title}' (#{Id}); will retry.",
                rec.MeetingTitle, rec.Id);
            await db.SaveChangesAsync(ct);
        }
    }

    // ── Claude: minutes + action items ────────────────────────────────────────
    private async Task<(string Minutes, string? ActionItemsJson)> GenerateMinutesAsync(
        MeetingRecording rec, CancellationToken ct)
    {
        var roster = BuildRoster(rec.GuildId);
        var meetingDate = rec.MeetingStartUtc.ToString("yyyy-MM-dd");

        var system =
            "You are generating meeting minutes for the 189th, a military-themed gaming clan. " +
            "You receive (1) a ROSTER of current members by Discord display name (some with @username), and " +
            "(2) a TRANSCRIPT whose every line is \"[timestamp] DisplayName: text\" — the DisplayName is the " +
            "verified speaker.\n\n" +
            "Produce concise, accurate minutes in Markdown with these sections: a short Summary (2-4 sentences); " +
            "Key Discussion Points (grouped by topic, bulleted); Decisions; and Action Items.\n\n" +
            "For Action Items and any third-person mention in the discussion (e.g. \"Apex will handle spawns\"), " +
            "resolve the named person to the closest matching ROSTER display name. If a mention is ambiguous or " +
            "absent from the roster, keep the name as spoken and append \"(unverified)\". Always refer to people " +
            "by Discord display name; never invent real names.\n\n" +
            "Do not invent content the transcript does not support; if it is sparse, keep the minutes short.\n\n" +
            "After the Markdown minutes, output the action items again as a JSON array inside a single fenced " +
            "```json block, each item {\"owner\": display name, \"task\": ..., \"next\": optional next step or due}. " +
            "Output nothing after the JSON block.";

        var user = new StringBuilder()
            .AppendLine("ROSTER:")
            .AppendLine(roster)
            .AppendLine()
            .AppendLine($"MEETING: {rec.MeetingTitle} ({meetingDate})")
            .AppendLine()
            .AppendLine("TRANSCRIPT:")
            .AppendLine(rec.TranscriptText)
            .ToString();

        var ai = await _ai.GenerateAsync(system, user, maxTokens: 4000, ct);
        return SplitMinutesAndActions(ai.Text);
    }

    /// <summary>Pull the trailing ```json action-items block out of Claude's output.</summary>
    private static (string Minutes, string? ActionItemsJson) SplitMinutesAndActions(string text)
    {
        var m = Regex.Match(text, "```json\\s*(.*?)```",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success)
            return (text.Trim(), null);

        string? actionsJson = null;
        var candidate = m.Groups[1].Value.Trim();
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            actionsJson = candidate;
        }
        catch { /* leave null if Claude's JSON didn't parse */ }

        var minutes = (text[..m.Index] + text[(m.Index + m.Length)..]).Trim();
        return (minutes, actionsJson);
    }

    private string BuildRoster(ulong guildId)
    {
        var guild = _client.GetGuild(guildId);
        if (guild is null) return "(roster unavailable)";

        var names = guild.Users
            .Where(u => !u.IsBot)
            .Take(400)
            .Select(u => u.DisplayName == u.Username ? u.DisplayName : $"{u.DisplayName} (@{u.Username})");

        var roster = string.Join(", ", names);
        return string.IsNullOrWhiteSpace(roster) ? "(roster unavailable)" : roster;
    }

    // ── Discord: post the minutes ──────────────────────────────────────────────
    private async Task<ulong?> PostMinutesAsync(MeetingRecording rec, CancellationToken ct)
    {
        var channelId = _config.MeetingMinutesChannelId != 0
            ? _config.MeetingMinutesChannelId
            : _config.MeetingRecordingAnnouncementChannelId != 0
                ? _config.MeetingRecordingAnnouncementChannelId
                : _config.MeetingVoiceChannelId;

        if (_client.GetChannel(channelId) is not IMessageChannel channel)
        {
            _logger.LogWarning(
                "Could not resolve minutes channel {ChannelId}; minutes saved to DB but not posted.", channelId);
            return null;
        }

        var minutes = rec.MinutesText ?? "(no minutes generated)";
        var date = rec.MeetingStartUtc.ToString("yyyy-MM-dd");

        var embed = new EmbedBuilder()
            .WithTitle($"📋 Minutes — {Trunc(rec.MeetingTitle, 230)}")
            .WithDescription(Trunc(minutes, 4000))
            .WithFooter($"{date} • generated by ClanGuard")
            .WithColor(new Color(0x2ECC71))
            .Build();

        // Always attach the full minutes as a file so nothing is lost to the
        // embed's length cap.
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(minutes));
        var attachment = new FileAttachment(ms, $"minutes_{date}.md");

        var msg = await channel.SendFileAsync(
            attachment, text: null, embed: embed,
            options: new RequestOptions { CancelToken = ct });
        return msg.Id;
    }

    // ── Retention: keep last N audio dirs, prune older (keep transcript+minutes) ──
    private async Task RunRetentionAsync(BotDbContext db, DateTime now, CancellationToken ct)
    {
        var retain = Math.Max(1, _config.MeetingRecordingRetainCount);

        var withAudio = await db.MeetingRecordings
            .Where(m => m.AudioDirPath != null
                     && (m.State == MeetingRecordingState.Posted
                      || m.State == MeetingRecordingState.Summarized
                      || m.State == MeetingRecordingState.Transcribing))
            .OrderByDescending(m => m.MeetingStartUtc)
            .ToListAsync(ct);

        var pruned = false;
        foreach (var rec in withAudio.Skip(retain))
        {
            // Only prune audio we no longer need — never something mid-processing.
            if (rec.State != MeetingRecordingState.Posted) continue;

            try
            {
                if (!string.IsNullOrEmpty(rec.AudioDirPath) && Directory.Exists(rec.AudioDirPath))
                    Directory.Delete(rec.AudioDirPath, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete audio dir for '{Title}' (#{Id}); marking Pruned anyway.",
                    rec.MeetingTitle, rec.Id);
            }

            rec.AudioDirPath = null;
            rec.TranscriptPath = null; // on-disk file is gone; TranscriptText/MinutesText stay in the DB
            Transition(rec, MeetingRecordingState.Pruned, now);
            pruned = true;
            _logger.LogInformation("Pruned audio for '{Title}' (#{Id}) — keeping {Retain} most recent.",
                rec.MeetingTitle, rec.Id, retain);
        }

        if (pruned) await db.SaveChangesAsync(ct);
    }

    private void Transition(MeetingRecording rec, MeetingRecordingState to, DateTime now)
    {
        if (rec.State == to) return;
        _logger.LogDebug("Recording #{Id} '{Title}': {From} → {To}.", rec.Id, rec.MeetingTitle, rec.State, to);
        rec.State = to;
        rec.StateUpdatedUtc = now;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
