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

    private async Task OnSlashCommandExecuted(SocketSlashCommand cmd)
    {
        if (cmd.Data.Name != CommandName) return;

        await cmd.DeferAsync(ephemeral: true);

        try
        {
            await HandleAsync(cmd);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in /{Command}", CommandName);
            await cmd.FollowupAsync($"❌ Unexpected error: {ex.Message}", ephemeral: true);
        }
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
            $"🔎 Scanning channels for **{targetName}**'s recent messages…",
            ephemeral: true);

        // ── Collect the globally-newest `count` messages by the target ───
        var (toDelete, scannedChannels, unreadableChannels) =
            await CollectNewestAsync(guild, targetId, count);

        if (toDelete.Count == 0)
        {
            await cmd.FollowupAsync(
                $"No deletable messages from **{targetName}** were found across " +
                $"{scannedChannels} scanned channel(s). (Pinned messages are never touched.)",
                ephemeral: true);
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
                (unreadableChannels > 0
                    ? $"\n\n⚠️ {unreadableChannels} channel(s) couldn't be scanned (missing View / Read History)."
                    : "") +
                $"\n\nRe-run with `confirm:true` to delete.";

            await cmd.FollowupAsync(preview, ephemeral: true);
            return;
        }

        // ── Execute deletion ─────────────────────────────────────────────
        var deleted = 0;
        var failed = 0;
        var skippedNoPerm = 0;
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

            // Bulk-delete recent messages in chunks of 100 (Discord's per-call max).
            foreach (var chunk in Chunk(recent, 100))
            {
                if (chunk.Count >= 2)
                {
                    try
                    {
                        await channel.DeleteMessagesAsync(chunk);
                        deleted += chunk.Count;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "/{Command}: bulk delete failed in #{Channel}; falling back to per-message.",
                            CommandName, channel.Name);
                        foreach (var m in chunk)
                            await SlowDeleteAsync(m, channel, () => deleted++, () => failed++);
                    }
                }
                else if (chunk.Count == 1)
                {
                    await SlowDeleteAsync(chunk[0], channel, () => deleted++, () => failed++);
                }
            }

            // Slow-delete old (>14d) messages one at a time.
            foreach (var m in old)
                await SlowDeleteAsync(m, channel, () => deleted++, () => failed++);
        }

        var summary =
            $"✅ Deleted **{deleted}** message(s) from **{targetName}** across " +
            $"**{byChannel.Count}** channel(s).";
        if (skippedNoPerm > 0)
            summary += $"\n• Skipped (bot lacks Manage Messages): {skippedNoPerm}";
        if (failed > 0)
            summary += $"\n• Failed: {failed} (see bot logs)";
        if (unreadableChannels > 0)
            summary += $"\n• Channels not scanned (missing View / Read History): {unreadableChannels}";
        if (toDelete.Count >= count)
            summary += $"\n\nℹ️ Hit the requested cap of {count}. Run again to remove more.";

        _logger.LogInformation(
            "/{Command} by {Invoker}: target={Target}, deleted={Deleted}, failed={Failed}, skippedNoPerm={Skipped}",
            CommandName, cmd.User.Username, targetId, deleted, failed, skippedNoPerm);

        await cmd.FollowupAsync(summary, ephemeral: true);
    }

    /// <summary>
    /// Pages every readable text channel newest→older, collecting the target's
    /// non-pinned messages, and returns the globally-newest <paramref name="count"/>
    /// of them. Early-stops a channel once all remaining messages are older than
    /// the current count-th newest candidate.
    /// </summary>
    private async Task<(List<IMessage> ToDelete, int Scanned, int Unreadable)> CollectNewestAsync(
        SocketGuild guild, ulong targetId, int count)
    {
        var candidates = new List<IMessage>();
        var scanned = 0;
        var unreadable = 0;

        foreach (var channel in guild.TextChannels)
        {
            var perms = guild.CurrentUser.GetPermissions(channel);
            if (!perms.ViewChannel || !perms.ReadMessageHistory)
            {
                unreadable++;
                continue;
            }

            scanned++;
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

                candidates.AddRange(batch.Where(m => m.Author.Id == targetId && !m.IsPinned));

                // Early stop: once we already have enough candidates, anything
                // older than the current count-th newest can never make the cut.
                if (candidates.Count >= count)
                {
                    var threshold = candidates
                        .OrderByDescending(m => m.Timestamp)
                        .ElementAt(count - 1)
                        .Timestamp;

                    // Oldest message on this page — if it's already past the
                    // threshold, every older page is too. Stop scanning here.
                    if (batch.Min(m => m.Timestamp) <= threshold)
                        break;
                }

                if (batch.Count < 100) break; // reached start of channel history
            }
        }

        var toDelete = candidates
            .OrderByDescending(m => m.Timestamp)
            .Take(count)
            .ToList();

        return (toDelete, scanned, unreadable);
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
}
