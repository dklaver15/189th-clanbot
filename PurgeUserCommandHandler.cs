using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /purge-user slash command. Deletes a target member's most recent
/// messages across the WHOLE server (every text channel the bot can read), up to
/// a caller-supplied count (default 100).
///
/// ── Semantics ──
///   • The <c>count</c> option means "N of the TARGET'S OWN messages" — not "scan
///     the last N messages." We page each channel's history newest→older,
///     collecting only messages authored by the target, until we have the newest
///     <c>count</c> of them across the entire server, then delete exactly those.
///   • Because it's server-wide, we collect candidates from every readable text
///     channel and then keep only the globally-newest <c>count</c>. An early-stop
///     optimisation halts scanning a channel once every remaining (older) message
///     in it is already older than the current count-th newest candidate — the
///     threshold only ever moves newer, so anything below it can never qualify.
///
/// ── Safety ──
///   • Administrator-only. This is a broad, destructive, cross-channel action.
///   • Requires <c>confirm:true</c>. Without it the command runs a DRY-RUN:
///     it reports how many messages WOULD be deleted and in which channels,
///     deleting nothing. This doubles as a preview.
///   • Pinned messages are PRESERVED (mirrors /clear-awol-list) so the purge
///     can't silently nuke a pinned announcement the target happened to post.
///   • Only channels where the bot has View + Read Message History are scanned;
///     deletion additionally needs Manage Messages. Channels missing Manage
///     Messages are reported as skipped rather than failing the whole run.
///
/// ── Discord mechanics (same as ClearAwolListCommandHandler) ──
///   • Bulk delete only accepts messages younger than 14 days, max 100 per call.
///     Older messages are deleted one at a time, throttled to stay under the
///     per-channel rate limit.
/// </summary>
public class PurgeUserCommandHandler
{
    public const string CommandName = "purge-user";

    private const int DefaultCount = 100;
    private const int MaxCount = 1000;

    /// <summary>Discord's hard cutoff for bulk delete eligibility.</summary>
    private static readonly TimeSpan BulkDeleteWindow = TimeSpan.FromDays(14);

    /// <summary>Per-message delay on the slow (>14 day) deletion path.</summary>
    private static readonly TimeSpan SlowDeleteDelay = TimeSpan.FromMilliseconds(750);

    /// <summary>Per-channel page cap so a runaway history scan can't hang the bot.</summary>
    private const int MaxPagesPerChannel = 50; // 50 * 100 = 5k messages/channel

    /// <summary>
    /// How many channels to scan at once. Different channels use independent
    /// Discord rate-limit buckets, so scanning concurrently is safe and roughly
    /// divides wall-clock scan time by this factor. Kept well under Discord's
    /// global ~50 req/s ceiling.
    /// </summary>
    private const int MaxConcurrentChannelScans = 8;

    private readonly ILogger<PurgeUserCommandHandler> _logger;

    public PurgeUserCommandHandler(ILogger<PurgeUserCommandHandler> logger)
    {
        _logger = logger;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Delete a member's recent messages server-wide (Admin only)")
            .AddOption("user", ApplicationCommandOptionType.User,
                "The member whose messages to delete", isRequired: false)
            .AddOption("user_id", ApplicationCommandOptionType.String,
                "Raw numeric ID instead of the picker — use for kicked/banned members who already left",
                isRequired: false)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("count")
                .WithDescription($"How many of their messages to delete (default {DefaultCount}, max {MaxCount})")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(false)
                .WithMinValue(1)
                .WithMaxValue(MaxCount))
            .AddOption("confirm", ApplicationCommandOptionType.Boolean,
                "Set true to actually delete. Omit for a dry-run preview.", isRequired: false)
            .Build();

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandExecuted;
    }

    private Task OnSlashCommandExecuted(SocketSlashCommand cmd)
    {
        if (cmd.Data.Name != CommandName) return Task.CompletedTask;

        // Offload the whole run to a background task and return immediately.
        // Discord.NET's gateway awaits each event handler before dispatching the
        // next gateway event, so a multi-minute purge running inline here blocks
        // EVERY other interaction (e.g. /health) until it finishes — they time
        // out with "application did not respond". Doing the work off the gateway
        // loop keeps the bot responsive during a purge. DeferAsync still runs
        // first (inside the task) to acknowledge within Discord's 3s window.
        _ = Task.Run(async () =>
        {
            try
            {
                await cmd.DeferAsync(ephemeral: true);
                await HandleAsync(cmd);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in /{Command}", CommandName);
                try
                {
                    await cmd.FollowupAsync($"❌ Unexpected error: {ex.Message}", ephemeral: true);
                }
                catch (Exception followupEx)
                {
                    _logger.LogWarning(followupEx,
                        "/{Command}: couldn't report the error back to the invoker.", CommandName);
                }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketSlashCommand cmd)
    {
        var guild = (cmd.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await cmd.FollowupAsync("This command must be used inside a server.", ephemeral: true);
            return;
        }

        // ── Permission check: Administrator only ─────────────────────────
        var invoker = guild.GetUser(cmd.User.Id);
        if (invoker is null || !invoker.GuildPermissions.Administrator)
        {
            await cmd.FollowupAsync(
                "⛔ This command is restricted to server **Administrators**.",
                ephemeral: true);
            return;
        }

        // ── Options ──────────────────────────────────────────────────────
        // Target resolves from EITHER the picker OR a raw numeric ID. The raw-ID
        // path is the important one here: this command's main job is cleaning up
        // after a member has been kicked or banned, and the user picker can't
        // select someone who's no longer in the guild. Mirrors /late-check.
        var target = cmd.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;
        var rawId  = cmd.Data.Options.FirstOrDefault(o => o.Name == "user_id")?.Value as string;

        ulong targetId;
        if (target is not null)
        {
            targetId = target.Id;
        }
        else if (string.IsNullOrWhiteSpace(rawId) || !ulong.TryParse(rawId.Trim(), out targetId))
        {
            await cmd.FollowupAsync(
                "⚠️ Select a user, or pass a numeric `user_id` (use this for members who were kicked/banned).",
                ephemeral: true);
            return;
        }

        var count = DefaultCount;
        if (cmd.Data.Options.FirstOrDefault(o => o.Name == "count")?.Value is long c)
            count = (int)Math.Clamp(c, 1, MaxCount);

        var confirm = (bool)(cmd.Data.Options.FirstOrDefault(o => o.Name == "confirm")?.Value ?? false);

        var targetName = target is not null
            ? ((target as IGuildUser)?.DisplayName ?? target.GlobalName ?? target.Username)
            : $"User {targetId}";

        await cmd.FollowupAsync(
            $"🔎 Scanning channels for **{targetName}**'s recent messages… " +
            "I'll **DM you** the results when it's done.",
            ephemeral: true);

        // ── Collect the globally-newest `count` messages by the target ───
        var (toDelete, scannedChannels, unreadableChannels) =
            await CollectNewestAsync(guild, targetId, count);

        if (toDelete.Count == 0)
        {
            await ReportResultAsync(cmd, invoker,
                $"No deletable messages from **{targetName}** were found across " +
                $"{scannedChannels} scanned channel(s). (Pinned messages are never touched.)");
            return;
        }

        var byChannel = toDelete
            .GroupBy(m => m.Channel.Id)
            .Select(g => (ChannelId: g.Key, Name: g.First().Channel.Name, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        // ── Dry-run preview (default) ────────────────────────────────────
        if (!confirm)
        {
            var lines = byChannel
                .Take(20)
                .Select(x => $"• <#{x.ChannelId}> — {x.Count}");
            var more = byChannel.Count > 20 ? $"\n…and {byChannel.Count - 20} more channel(s)." : "";

            var preview =
                $"🧾 **Dry run** — would delete **{toDelete.Count}** message(s) from **{targetName}** " +
                $"across **{byChannel.Count}** channel(s):\n" +
                string.Join("\n", lines) + more +
                FormatUnreadable(unreadableChannels) +
                $"\n\nRe-run with `confirm:true` to delete.";

            await ReportResultAsync(cmd, invoker, preview);
            return;
        }

        // ── Execute deletion ─────────────────────────────────────────────
        var deleted = 0;
        var failed = 0;
        var skippedNoPerm = 0;
        // Actual per-channel deleted counts (may differ from the scan's byChannel
        // if a channel is skipped for lack of Manage Messages or a delete fails).
        var deletedByChannel = new List<(ulong Id, string Name, int Count)>();
        var bulkCutoff = DateTimeOffset.UtcNow - BulkDeleteWindow + TimeSpan.FromMinutes(1);

        foreach (var grp in toDelete.GroupBy(m => m.Channel.Id))
        {
            if (guild.GetChannel(grp.Key) is not SocketTextChannel channel)
            {
                failed += grp.Count();
                continue;
            }

            var perms = guild.CurrentUser.GetPermissions(channel);
            if (!perms.ManageMessages)
            {
                skippedNoPerm += grp.Count();
                _logger.LogWarning(
                    "/{Command}: missing Manage Messages in #{Channel}; skipping {Count} message(s).",
                    CommandName, channel.Name, grp.Count());
                continue;
            }

            var msgs = grp.ToList();
            var recent = msgs.Where(m => m.Timestamp > bulkCutoff).ToList();
            var old    = msgs.Where(m => m.Timestamp <= bulkCutoff).ToList();
            var channelDeleted = 0;

            // Bulk-delete recent messages in chunks of 100 (Discord's per-call max).
            foreach (var chunk in Chunk(recent, 100))
            {
                if (chunk.Count >= 2)
                {
                    try
                    {
                        await channel.DeleteMessagesAsync(chunk);
                        deleted += chunk.Count;
                        channelDeleted += chunk.Count;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "/{Command}: bulk delete failed in #{Channel}; falling back to per-message.",
                            CommandName, channel.Name);
                        foreach (var m in chunk)
                            await SlowDeleteAsync(m, channel, () => { deleted++; channelDeleted++; }, () => failed++);
                    }
                }
                else if (chunk.Count == 1)
                {
                    await SlowDeleteAsync(chunk[0], channel, () => { deleted++; channelDeleted++; }, () => failed++);
                }
            }

            // Slow-delete old (>14d) messages one at a time.
            foreach (var m in old)
                await SlowDeleteAsync(m, channel, () => { deleted++; channelDeleted++; }, () => failed++);

            if (channelDeleted > 0)
                deletedByChannel.Add((channel.Id, channel.Name, channelDeleted));
        }

        var summary =
            $"✅ Deleted **{deleted}** message(s) from **{targetName}** across " +
            $"**{deletedByChannel.Count}** channel(s):";

        var deletedLines = deletedByChannel
            .OrderByDescending(x => x.Count)
            .Take(20)
            .Select(x => $"• <#{x.Id}> — {x.Count}");
        summary += "\n" + string.Join("\n", deletedLines);
        if (deletedByChannel.Count > 20)
            summary += $"\n…and {deletedByChannel.Count - 20} more channel(s).";

        if (skippedNoPerm > 0)
            summary += $"\n• Skipped (bot lacks Manage Messages): {skippedNoPerm}";
        if (failed > 0)
            summary += $"\n• Failed: {failed} (see bot logs)";
        summary += FormatUnreadable(unreadableChannels);
        if (toDelete.Count >= count)
            summary += $"\n\nℹ️ Hit the requested cap of {count}. Run again to remove more.";

        _logger.LogInformation(
            "/{Command} by {Invoker}: target={Target}, deleted={Deleted}, failed={Failed}, skippedNoPerm={Skipped}",
            CommandName, cmd.User.Username, targetId, deleted, failed, skippedNoPerm);

        // Deliver the result via DM rather than an interaction followup. A big
        // purge (especially many >14-day messages at 750ms each) can outlast the
        // 15-minute interaction-token window; the deletions themselves use the
        // bot token and are unaffected, but the followup would fail. A DM has no
        // such limit, so the admin always gets the summary.
        await ReportResultAsync(cmd, invoker, summary);
    }

    /// <summary>
    /// Delivers a result to the admin who invoked the command — used for the
    /// dry-run preview, the "nothing found" message, AND the post-deletion
    /// summary, so every outcome arrives the same way. Prefers a DM (survives the
    /// 15-minute interaction-token limit); if their DMs are closed, falls back to
    /// an EPHEMERAL interaction followup so no one but the admin sees the
    /// moderation action. That followup only works while the interaction token is
    /// still alive (<15 min), so on a long run where DMs are also closed we
    /// deliberately post nothing — logging only — rather than leak the result
    /// into a public channel. Any deletions have already completed regardless of
    /// how this message is delivered.
    /// </summary>
    private async Task ReportResultAsync(SocketSlashCommand cmd, SocketGuildUser invoker, string summary)
    {
        try
        {
            var dm = await invoker.CreateDMChannelAsync();
            await dm.SendMessageAsync(summary);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "/{Command}: couldn't DM the summary to {User} (DMs closed?); trying an ephemeral followup.",
                CommandName, invoker.Username);
        }

        try
        {
            // Ephemeral so only the invoking admin can see it. Fails if the
            // 15-minute interaction token has already expired.
            await cmd.FollowupAsync(summary, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "/{Command}: couldn't deliver the summary by DM or ephemeral followup " +
                "(token likely expired). Result logged only, not posted publicly.",
                CommandName);
        }
    }

    /// <summary>
    /// Collects the target's non-pinned messages across every readable text
    /// channel and returns the globally-newest <paramref name="count"/> of them.
    ///
    /// Channels are scanned CONCURRENTLY (bounded by <see cref="MaxConcurrentChannelScans"/>).
    /// Reads to different channels hit independent Discord rate-limit buckets, so
    /// serialising them was the main reason a low-activity target on a many-channel
    /// server took many minutes: the cross-channel early-stop can't arm until
    /// `count` messages are found, so a target with fewer than `count` messages
    /// forced every channel to be paged to its full depth. Doing it in parallel
    /// cuts wall-clock time roughly by the concurrency factor.
    /// </summary>
    private async Task<(List<IMessage> ToDelete, int Scanned, List<ulong> Unreadable)> CollectNewestAsync(
        SocketGuild guild, ulong targetId, int count)
    {
        var readable = new List<SocketTextChannel>();
        var unreadable = new List<ulong>();

        foreach (var channel in guild.TextChannels)
        {
            // Skip the text chat embedded in voice/stage channels. Discord.NET
            // models those as a SocketTextChannel subclass, so they show up in
            // guild.TextChannels — but they're throwaway in-call chatter, not
            // worth purging, and would otherwise clutter the "couldn't scan"
            // list when the bot lacks access to them.
            if (channel is SocketVoiceChannel)
                continue;

            var perms = guild.CurrentUser.GetPermissions(channel);
            if (!perms.ViewChannel || !perms.ReadMessageHistory)
                unreadable.Add(channel.Id);
            else
                readable.Add(channel);
        }

        using var gate = new SemaphoreSlim(MaxConcurrentChannelScans);
        var perChannel = await Task.WhenAll(readable.Select(async channel =>
        {
            await gate.WaitAsync();
            try { return await ScanChannelAsync(channel, targetId, count); }
            finally { gate.Release(); }
        }));

        var toDelete = perChannel
            .SelectMany(list => list)
            .OrderByDescending(m => m.Timestamp)
            .Take(count)
            .ToList();

        return (toDelete, readable.Count, unreadable);
    }

    /// <summary>
    /// Pages a single channel newest→older, collecting the target's non-pinned
    /// messages, up to <see cref="MaxPagesPerChannel"/> pages. Stops early once it
    /// has <paramref name="count"/> messages from this channel alone — no single
    /// channel can contribute more than that to the global newest-`count`.
    /// </summary>
    private async Task<List<IMessage>> ScanChannelAsync(
        SocketTextChannel channel, ulong targetId, int count)
    {
        var found = new List<IMessage>();
        ulong? cursor = null;
        var pages = 0;

        while (pages++ < MaxPagesPerChannel)
        {
            List<IMessage> batch;
            try
            {
                batch = (cursor is null
                    ? await channel.GetMessagesAsync(100).FlattenAsync()
                    : await channel.GetMessagesAsync(cursor.Value, Direction.Before, 100).FlattenAsync())
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "/{Command}: failed to fetch history from #{Channel}; skipping rest.",
                    CommandName, channel.Name);
                break;
            }

            if (batch.Count == 0) break;
            cursor = batch.Min(m => m.Id);

            found.AddRange(batch.Where(m => m.Author.Id == targetId && !m.IsPinned));

            if (found.Count >= count) break;      // this channel already has enough
            if (batch.Count < 100) break;         // reached start of channel history
        }

        return found;
    }

    private async Task SlowDeleteAsync(
        IMessage msg, SocketTextChannel channel, Action onSuccess, Action onFail)
    {
        try
        {
            await msg.DeleteAsync();
            onSuccess();
            await Task.Delay(SlowDeleteDelay);
        }
        catch (Exception ex)
        {
            onFail();
            _logger.LogWarning(ex,
                "/{Command}: failed to delete message {MessageId} in #{Channel}.",
                CommandName, msg.Id, channel.Name);
        }
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    /// <summary>
    /// Renders the list of channels the bot couldn't scan (missing View / Read
    /// History) as channel mentions, capped so the message can't blow past
    /// Discord's 2000-char limit on a server with many locked channels. Returns
    /// an empty string when nothing was skipped.
    /// </summary>
    private static string FormatUnreadable(List<ulong> unreadable)
    {
        if (unreadable.Count == 0) return "";

        const int max = 20;
        var mentions = string.Join(", ", unreadable.Take(max).Select(id => $"<#{id}>"));
        var more = unreadable.Count > max ? $" …and {unreadable.Count - max} more" : "";
        return $"\n\n⚠️ Couldn't scan {unreadable.Count} channel(s) (missing View / Read History): {mentions}{more}";
    }
}
