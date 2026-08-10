using System.Collections.Concurrent;
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

    /// <summary>Ceiling on the retry backoff, so a row is still retried periodically.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly IMeetingTranscriber _transcriber;
    private readonly IAiService _ai;
    private readonly ILogger<MeetingMinutesService> _logger;
    private readonly BotConfig _config;

    /// <summary>
    /// Per-recording retry backoff: recording id -> (consecutive failures, earliest next attempt).
    ///
    /// ── Why ────────────────────────────────────────────────────────────────
    /// Without this, a failing row is retried every poll (2 min) for the full 6h
    /// processing window. On 2026-08-09 that turned ONE recurring crash in the
    /// transcriber into roughly 180 full-restart transcription attempts, pinning
    /// the droplet's single core all night and finishing no closer than it
    /// started. Backing off turns the same fault into a handful of attempts.
    ///
    /// It also unblocks the queue. TickAsync processes one row per poll, oldest
    /// first, so a poisoned recording used to starve every later meeting for the
    /// whole 6h. A row that is waiting out its backoff is skipped, which lets the
    /// meetings behind it through.
    ///
    /// Deliberately in-memory, not a DB column. Backoff state is inherently
    /// transient, and a bot restart is a legitimate reason to try again
    /// immediately, so persisting it would buy nothing worth a migration.
    /// </summary>
    private readonly ConcurrentDictionary<int, (int Failures, DateTime NextAttemptUtc)> _backoff = new();

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

        // Still one in-flight recording per tick (transcription is heavy), but the
        // oldest row no longer gets an unconditional claim on the slot: one that is
        // serving a backoff steps aside so the meetings behind it can run.
        var inFlight = await db.MeetingRecordings
            .Where(m => m.State == MeetingRecordingState.Transcribing
                     || m.State == MeetingRecordingState.Summarized)
            .OrderBy(m => m.MeetingStartUtc)
            .ToListAsync(ct);

        PruneBackoff(inFlight);

        // Age-out is checked across ALL in-flight rows, not just the one we pick,
        // so a row cannot outlive the processing window simply by being in backoff.
        foreach (var stale in inFlight.Where(m => now - m.StateUpdatedUtc > MaxProcessingAge).ToList())
        {
            // Capture the stage before the transition overwrites it: "died in
            // Transcribing" and "died in Summarized" point at very different causes.
            var stage = stale.State.ToString();
            stale.ErrorMessage = $"Stuck in {stage} for over {MaxProcessingAge.TotalHours:0}h, giving up.";
            _logger.LogWarning("Minutes pipeline for '{Title}' (#{Id}) {Msg}",
                stale.MeetingTitle, stale.Id, stale.ErrorMessage);
            Transition(stale, MeetingRecordingState.Failed, now);
            await db.SaveChangesAsync(ct);
            _backoff.TryRemove(stale.Id, out _);
            await MeetingFailureNotice.PostAsync(_client, _config, _logger, stale, stage, ct);
            inFlight.Remove(stale);
        }

        var rec = inFlight.FirstOrDefault(m =>
            !_backoff.TryGetValue(m.Id, out var b) || now >= b.NextAttemptUtc);

        if (rec is null)
        {
            // Either nothing is in flight, or everything in flight is waiting out a
            // backoff. Either way there is time to run retention.
            await RunRetentionAsync(db, now, ct);
            return;
        }

        try
        {
            if (rec.State == MeetingRecordingState.Transcribing)
            {
                if (string.IsNullOrWhiteSpace(rec.AudioDirPath))
                {
                    rec.ErrorMessage = "No audio was captured for this meeting.";
                    _logger.LogWarning("'{Title}' (#{Id}) has no audio dir, failing.", rec.MeetingTitle, rec.Id);
                    Transition(rec, MeetingRecordingState.Failed, now);
                    await db.SaveChangesAsync(ct);
                    _backoff.TryRemove(rec.Id, out _);
                    await MeetingFailureNotice.PostAsync(_client, _config, _logger, rec, "Transcribing", ct);
                    return;
                }

                // Transcribe (skip if a prior attempt already produced the text).
                if (string.IsNullOrWhiteSpace(rec.TranscriptText))
                {
                    var result = await _transcriber.TranscribeAsync(rec.AudioDirPath, ct);
                    if (string.IsNullOrWhiteSpace(result.Transcript))
                    {
                        rec.ErrorMessage = "Transcript was empty (no speech captured).";
                        _logger.LogWarning("'{Title}' (#{Id}) produced an empty transcript, failing.",
                            rec.MeetingTitle, rec.Id);
                        Transition(rec, MeetingRecordingState.Failed, now);
                        await db.SaveChangesAsync(ct);
                        _backoff.TryRemove(rec.Id, out _);
                        await MeetingFailureNotice.PostAsync(_client, _config, _logger, rec, "Transcribing", ct);
                        return;
                    }

                    rec.TranscriptText = result.Transcript;
                    rec.TranscriptPath = Path.Combine(rec.AudioDirPath, "transcript.txt");
                    rec.ErrorMessage = null;
                    // Persist the transcript BEFORE the costly Claude call so a
                    // crash here doesn't re-run Whisper.
                    await db.SaveChangesAsync(ct);
                    // Real progress: whatever was failing has stopped failing.
                    _backoff.TryRemove(rec.Id, out _);
                }

                var (minutes, actionItemsJson) = await GenerateMinutesAsync(rec, ct);
                rec.MinutesText = minutes;
                rec.ActionItemsJson = actionItemsJson;
                rec.ErrorMessage = null;
                Transition(rec, MeetingRecordingState.Summarized, now);
                await db.SaveChangesAsync(ct);
                _backoff.TryRemove(rec.Id, out _);
            }

            if (rec.State == MeetingRecordingState.Summarized)
            {
                var messageId = await PostMinutesAsync(rec, ct);
                if (messageId is null)
                {
                    // Nothing reached Discord, so this is NOT Posted. Marking it
                    // Posted anyway (the old behaviour) meant an unresolvable
                    // minutes channel looked like success: the row read Posted, the
                    // audio was pruned on schedule, and nobody ever saw the minutes.
                    // Staying Summarized retries next tick, and the age-out will
                    // fail it loudly if the channel stays unreachable.
                    rec.ErrorMessage = "Minutes were generated but the minutes channel could not be resolved.";
                    await db.SaveChangesAsync(ct);
                    BackOff(rec, now);
                    return;
                }

                rec.MinutesMessageId = messageId;
                rec.ErrorMessage = null;
                Transition(rec, MeetingRecordingState.Posted, now);
                await db.SaveChangesAsync(ct);
                _backoff.TryRemove(rec.Id, out _);
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
            BackOff(rec, now);
        }
    }

    /// <summary>
    /// Push this recording's next attempt out, doubling each consecutive failure up
    /// to <see cref="MaxBackoff"/>. Over the 6h processing window that turns ~180
    /// attempts into roughly a dozen, which is the difference between a wasted night
    /// of CPU and a handful of log lines.
    /// </summary>
    private void BackOff(MeetingRecording rec, DateTime now)
    {
        var failures = _backoff.TryGetValue(rec.Id, out var prior) ? prior.Failures + 1 : 1;
        // Cap the exponent before the shift, not the result, so this cannot overflow
        // on a row that somehow fails a very large number of times.
        var minutes = Math.Min(Math.Pow(2, Math.Min(failures, 10)), MaxBackoff.TotalMinutes);
        var next = now.AddMinutes(minutes);
        _backoff[rec.Id] = (failures, next);

        _logger.LogWarning(
            "Recording '{Title}' (#{Id}) has failed {Failures} time(s) in a row; " +
            "next attempt no earlier than {Next:HH:mm} UTC ({Minutes:0} min).",
            rec.MeetingTitle, rec.Id, failures, next, minutes);
    }

    /// <summary>
    /// Drop backoff entries for recordings that are no longer in flight, so the map
    /// tracks the queue rather than growing for the life of the process.
    /// </summary>
    private void PruneBackoff(IEnumerable<MeetingRecording> inFlight)
    {
        var live = inFlight.Select(m => m.Id).ToHashSet();
        foreach (var id in _backoff.Keys.Where(id => !live.Contains(id)).ToList())
            _backoff.TryRemove(id, out _);
    }

    /// <summary>
    /// System prompt for the single-shot path and the map-reduce reduce step's
    /// sibling. Kept verbatim so a normal-length meeting's minutes are byte-for-byte
    /// what they were before chunking was introduced.
    /// </summary>
    private const string MinutesSystemPrompt =
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

    // ── Claude: minutes + action items ────────────────────────────────────────
    // Normal meetings take a single call with the full transcript. An unusually
    // long meeting is summarized map-reduce style — each transcript chunk is
    // condensed to notes, then the notes are reduced into the final minutes — so
    // the transcript can never overflow the model's context window.
    private async Task<(string Minutes, string? ActionItemsJson)> GenerateMinutesAsync(
        MeetingRecording rec, CancellationToken ct)
    {
        var roster = BuildRoster(rec.GuildId);
        var meetingDate = rec.MeetingStartUtc.ToString("yyyy-MM-dd");
        var transcript = rec.TranscriptText ?? string.Empty;

        var singleShotMax = Math.Max(20_000, _config.MeetingMinutesMaxSingleShotChars);
        if (transcript.Length <= singleShotMax)
            return await GenerateSingleShotAsync(roster, rec.MeetingTitle, meetingDate, transcript, ct);

        _logger.LogInformation(
            "Transcript for '{Title}' (#{Id}) is {Chars} chars (> {Max}) — using chunked map-reduce.",
            rec.MeetingTitle, rec.Id, transcript.Length, singleShotMax);
        return await GenerateChunkedAsync(roster, rec.MeetingTitle, meetingDate, transcript, ct);
    }

    private async Task<(string Minutes, string? ActionItemsJson)> GenerateSingleShotAsync(
        string roster, string title, string meetingDate, string transcript, CancellationToken ct)
    {
        var user = new StringBuilder()
            .AppendLine("ROSTER:")
            .AppendLine(roster)
            .AppendLine()
            .AppendLine($"MEETING: {title} ({meetingDate})")
            .AppendLine()
            .AppendLine("TRANSCRIPT:")
            .AppendLine(transcript)
            .ToString();

        var ai = await _ai.GenerateAsync(MinutesSystemPrompt, user, maxTokens: 4000, ct);
        return SplitMinutesAndActions(ai.Text);
    }

    private async Task<(string Minutes, string? ActionItemsJson)> GenerateChunkedAsync(
        string roster, string title, string meetingDate, string transcript, CancellationToken ct)
    {
        var chunkChars = Math.Max(20_000, _config.MeetingMinutesChunkChars);
        var chunks = ChunkTranscript(transcript, chunkChars);

        // Defensive cap on an absurdly long transcript: re-chunk into fewer, larger
        // pieces rather than firing an unbounded number of map calls.
        const int maxChunks = 24;
        if (chunks.Count > maxChunks)
        {
            chunks = ChunkTranscript(transcript, (transcript.Length / maxChunks) + 1);
            _logger.LogWarning(
                "Transcript exceeded {Max} chunks at the configured size; regrouped into {N} larger chunks.",
                maxChunks, chunks.Count);
        }

        const string mapSystem =
            "You are condensing ONE part of a longer meeting transcript for the 189th, a military-themed " +
            "gaming clan. You receive a ROSTER of current members by Discord display name and one PART of the " +
            "transcript, whose every line is \"[timestamp] DisplayName: text\" — the DisplayName is the verified " +
            "speaker.\n\n" +
            "Extract only what THIS part contains, as compact notes under these headers: Discussion (bulleted " +
            "key points, grouped by topic), Decisions, and Action Items (owner — task — optional next step). " +
            "Refer to people by their ROSTER display name; resolve third-person mentions to the closest roster " +
            "name and append \"(unverified)\" if absent or ambiguous; never invent real names.\n\n" +
            "Do NOT write an overall summary, intro, or closing — output only the notes for this part. If the " +
            "part has little content, output little.";

        // Map: condense each chunk to compact notes.
        var notes = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            var mapUser = new StringBuilder()
                .AppendLine("ROSTER:")
                .AppendLine(roster)
                .AppendLine()
                .AppendLine($"MEETING: {title} ({meetingDate}) — PART {i + 1} of {chunks.Count}")
                .AppendLine()
                .AppendLine($"TRANSCRIPT (part {i + 1}/{chunks.Count}):")
                .AppendLine(chunks[i])
                .ToString();

            var ai = await _ai.GenerateAsync(mapSystem, mapUser, maxTokens: 2000, ct);
            notes.AppendLine($"--- Notes from part {i + 1} of {chunks.Count} ---");
            notes.AppendLine(ai.Text.Trim());
            notes.AppendLine();
        }

        // Reduce: turn the ordered notes into the final minutes. Same output
        // contract as the single-shot path, so SplitMinutesAndActions is unchanged.
        const string reduceSystem =
            "You are generating meeting minutes for the 189th, a military-themed gaming clan. " +
            "You receive (1) a ROSTER of current members by Discord display name (some with @username), and " +
            "(2) NOTES compiled in order from consecutive parts of the meeting transcript (each part already " +
            "condensed). The notes refer to people by verified Discord display name.\n\n" +
            "Produce concise, accurate minutes in Markdown with these sections: a short Summary (2-4 sentences); " +
            "Key Discussion Points (grouped by topic, bulleted); Decisions; and Action Items. Merge duplicated or " +
            "continued points across parts into one coherent set; never list the same item twice.\n\n" +
            "For Action Items and any third-person mention, resolve the named person to the closest matching " +
            "ROSTER display name. If a mention is ambiguous or absent from the roster, keep the name as given and " +
            "append \"(unverified)\". Always refer to people by Discord display name; never invent real names.\n\n" +
            "Do not invent content the notes do not support; if they are sparse, keep the minutes short.\n\n" +
            "After the Markdown minutes, output the action items again as a JSON array inside a single fenced " +
            "```json block, each item {\"owner\": display name, \"task\": ..., \"next\": optional next step or due}. " +
            "Output nothing after the JSON block.";

        var reduceUser = new StringBuilder()
            .AppendLine("ROSTER:")
            .AppendLine(roster)
            .AppendLine()
            .AppendLine($"MEETING: {title} ({meetingDate})")
            .AppendLine()
            .AppendLine("NOTES (in order):")
            .AppendLine(notes.ToString())
            .ToString();

        var reduced = await _ai.GenerateAsync(reduceSystem, reduceUser, maxTokens: 4000, ct);
        return SplitMinutesAndActions(reduced.Text);
    }

    /// <summary>
    /// Split a transcript into chunks no larger than <paramref name="chunkChars"/>,
    /// breaking only on line (utterance) boundaries so a line is never cut mid-way.
    /// A single line longer than the budget is hard-split as a last resort.
    /// </summary>
    private static List<string> ChunkTranscript(string transcript, int chunkChars)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in transcript.Split('\n'))
        {
            // +1 for the newline we'd add before this line.
            if (sb.Length > 0 && sb.Length + line.Length + 1 > chunkChars)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }

            if (line.Length > chunkChars)
            {
                for (var off = 0; off < line.Length; off += chunkChars)
                    chunks.Add(line.Substring(off, Math.Min(chunkChars, line.Length - off)));
                continue;
            }

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
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

        // Reclaim audio from terminally-failed recordings. The keep-last-N logic
        // above only tracks successful (Posted) rows, so a Failed row that captured
        // audio (e.g. an empty transcript, or a row aged out while Transcribing)
        // would otherwise leave its audio dir on disk forever.
        //
        // But NOT immediately. This originally deleted a failed row's audio on the
        // very next sweep, which on 2026-08-09 destroyed two meetings' recordings
        // within minutes of a transcriber crash loop failing them out: no retry was
        // possible and there was nothing left to diagnose. A failure is nearly
        // always infrastructure, so the audio now outlives the failure by
        // MeetingFailedAudioRetentionDays and the disk is still bounded.
        var graceDays = Math.Max(0, _config.MeetingFailedAudioRetentionDays);
        var failedCutoff = now - TimeSpan.FromDays(graceDays);
        var failedWithAudio = await db.MeetingRecordings
            .Where(m => m.AudioDirPath != null
                     && m.State == MeetingRecordingState.Failed
                     && m.StateUpdatedUtc <= failedCutoff)
            .ToListAsync(ct);
        foreach (var rec in failedWithAudio)
        {
            try
            {
                if (!string.IsNullOrEmpty(rec.AudioDirPath) && Directory.Exists(rec.AudioDirPath))
                    Directory.Delete(rec.AudioDirPath, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to delete audio for failed recording '{Title}' (#{Id}); clearing path anyway.",
                    rec.MeetingTitle, rec.Id);
            }

            rec.AudioDirPath = null;
            rec.TranscriptPath = null; // row stays Failed; only the on-disk audio is reclaimed
            pruned = true;
            _logger.LogInformation("Reclaimed audio for failed recording '{Title}' (#{Id}).",
                rec.MeetingTitle, rec.Id);
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
