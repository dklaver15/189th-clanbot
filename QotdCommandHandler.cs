using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The <c>/qotd</c> slash command — post a Question of the Day. Modelled on the
/// /event creation flow: running the command opens a DM, the bot asks for the
/// question, shows a preview with Create/Cancel buttons, then posts the embed
/// (with the 189th logo) to <see cref="BotConfig.QotdChannelId"/>.
///
/// Gated to <see cref="BotConfig.QotdMinRank"/> and above (default SGT). Admins
/// and anyone with Manage Roles always pass.
///
/// ── Routing ──
/// Self-registers SlashCommandExecuted (the /qotd entry point), MessageReceived
/// (the DM question text) and ButtonExecuted (the "qotd:" confirm/cancel
/// buttons). The DM/button handlers filter to the caller's active session, so
/// this coexists with every other handler on those events.
///
/// ── Sessions ──
/// In-memory, keyed by author id, expired after <see cref="IdleTimeout"/> of
/// inactivity and removed on completion/cancel. A restart drops any in-progress
/// session; the author just re-runs /qotd.
/// </summary>
public sealed class QotdCommandHandler
{
    public const string CommandName = "qotd";

    private const string Prefix = "qotd:";
    private const int MaxQuestionLength = 1000;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private static readonly Color Accent = new(0xF1C40F); // gold — grabs attention

    private readonly ConcurrentDictionary<ulong, QotdSession> _sessions = new();

    private readonly DiscordSocketClient _client;
    private readonly ILogger<QotdCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly System.Threading.Timer _idleSweep;

    public QotdCommandHandler(
        DiscordSocketClient client,
        ILogger<QotdCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _client = client;
        _logger = logger;
        _config = config.Value;

        _idleSweep = new System.Threading.Timer(_ => _ = SweepIdleAsync(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.MessageReceived      += OnMessageReceivedAsync;
        client.ButtonExecuted       += OnButtonExecutedAsync;
    }

    public static SlashCommandProperties BuildCommand(string minRank) =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription($"Post a Question of the Day — I'll DM you to set it up ({minRank}+ only)")
            .Build();

    // ─── /qotd entry point ─────────────────────────────────────────────────

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            await HandleStartAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /qotd");
            try { await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    private async Task HandleStartAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var guildUser = command.User as SocketGuildUser;
        if (guildUser is null || !HasPermission(guildUser))
        {
            await command.FollowupAsync(
                $"❌ Posting a Question of the Day is restricted to **{_config.QotdMinRank} and above**.",
                ephemeral: true);
            return;
        }

        var (dm, dmFailure) = await DmGuard.TryOpenAsync(command.User);
        if (dm is null)
        {
            _logger.LogInformation("Could not open DM with {User} for /qotd: {Failure}",
                command.User.Id, dmFailure);
            await command.FollowupAsync(DmGuard.AdviceFor(dmFailure), ephemeral: true);
            return;
        }

        PruneExpired();
        _sessions[command.User.Id] = new QotdSession
        {
            AuthorId       = command.User.Id,
            Dm             = dm,
            GuildId        = command.GuildId.Value,
            AuthorName     = guildUser.DisplayName,
            Step           = QotdStep.Question,
            LastActivityAt = DateTime.UtcNow,
        };

        try
        {
            await dm.SendMessageAsync(embed: Form("🧠 Question of the Day",
                "What's the **question** you'd like to post? Send it as your next message.\n\n" +
                "You can use Discord formatting — `**bold**`, `*italics*`, line breaks, emoji, etc."));
            await command.FollowupAsync("📬 Check your DMs — I'll walk you through your Question of the Day there.", ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not send opening DM to {User} for /qotd", command.User.Id);
            _sessions.TryRemove(command.User.Id, out _);
            await command.FollowupAsync(
                "I couldn't DM you. Enable **Direct Messages** from server members (Privacy Settings) and try again.",
                ephemeral: true);
        }
    }

    // ─── DM text replies ───────────────────────────────────────────────────

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage) return;
        if (message.Channel is not IDMChannel) return;
        if (!_sessions.TryGetValue(message.Author.Id, out var s)) return;

        if (IsExpired(s))
        {
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSendEmbed(s, Form("⌛ Timed out",
                "That setup expired from inactivity and **nothing was posted**. Run `/qotd` to start over."));
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var text = message.Content.Trim();

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSendEmbed(s, Form("Cancelled", "No question was posted. Run `/qotd` to start again anytime."));
            return;
        }

        // Only the Question step takes text — Confirm is a button step.
        if (s.Step != QotdStep.Question) return;

        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                await s.Dm.SendMessageAsync(embed: Form("🧠 Need a question",
                    "Send me the question you'd like to post — e.g. `What's the best loadout you've run this week?`"));
                return;
            }
            if (text.Length > MaxQuestionLength)
            {
                await s.Dm.SendMessageAsync(embed: Form("✂️ A bit too long",
                    $"Keep it under {MaxQuestionLength} characters — trim it down and send again."));
                return;
            }

            s.Question = text;
            await PromptConfirmAsync(s);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QOTD question step failed for {User}", message.Author.Id);
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, "Something went wrong. Run `/qotd` to start over.");
        }
    }

    // ─── Buttons ───────────────────────────────────────────────────────────

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(Prefix, StringComparison.Ordinal)) return;

        if (!_sessions.TryGetValue(component.User.Id, out var s))
        {
            await component.RespondAsync("That setup has expired. Run `/qotd` to start again.");
            return;
        }
        if (IsExpired(s))
        {
            _sessions.TryRemove(component.User.Id, out _);
            await ClearButtons(component, "⌛ Timed out. Run `/qotd` again.");
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var kind = component.Data.CustomId[Prefix.Length..];

        try
        {
            switch (kind)
            {
                case "confirm": await OnConfirmAsync(s, component); break;
                case "cancel":  await OnCancelAsync(s, component);  break;
                default:        await component.DeferAsync();       break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QOTD button {CustomId} failed", component.Data.CustomId);
            await SafeSend(s, "⚠️ Something went wrong on that step. Type `cancel` and run `/qotd` to start over.");
        }
    }

    private async Task OnConfirmAsync(QotdSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.AuthorId, out _);

        var guild   = _client.GetGuild(s.GuildId);
        var channel = guild?.GetTextChannel(_config.QotdChannelId);
        if (channel is null)
        {
            await ClearButtons(c, "❌ I couldn't find the Question of the Day channel. Check the bot's config / channel access.");
            return;
        }

        try
        {
            await channel.SendMessageAsync(embed: BuildPublicEmbed(s, guild!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post QOTD to channel {ChannelId}", _config.QotdChannelId);
            await ClearButtons(c, "❌ Something went wrong posting the question. Make sure I can post in that channel, then try `/qotd` again.");
            return;
        }

        await ClearButtons(c, $"✅ **Posted!** Your Question of the Day is live in {MentionUtils.MentionChannel(channel.Id)}.");
    }

    private async Task OnCancelAsync(QotdSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.AuthorId, out _);
        await ClearButtons(c, "❌ Cancelled. Nothing was posted.");
    }

    // ─── Embeds ────────────────────────────────────────────────────────────

    private async Task PromptConfirmAsync(QotdSession s)
    {
        s.Step = QotdStep.Confirm;

        var guild   = _client.GetGuild(s.GuildId);
        var preview = BuildPublicEmbed(s, guild);

        var buttons = new ComponentBuilder()
            .WithButton("Post it", $"{Prefix}confirm", ButtonStyle.Success)
            .WithButton("Cancel",  $"{Prefix}cancel",  ButtonStyle.Danger);

        var channelMention = MentionUtils.MentionChannel(_config.QotdChannelId);
        await s.Dm.SendMessageAsync(
            $"Here's how it'll look in {channelMention} — post it?",
            embed: preview,
            components: buttons.Build());
    }

    /// <summary>
    /// Builds the public QOTD embed: the 189th logo (guild icon) as a thumbnail
    /// to grab attention, the question rendered as a Discord-markdown blockquote,
    /// and an attribution footer.
    /// </summary>
    private Embed BuildPublicEmbed(QotdSession s, SocketGuild? guild)
    {
        // Render the question as a blockquote so it stands out. A multi-line
        // question needs "> " on each line for Discord to quote the whole block.
        var quoted = string.Join("\n", s.Question
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => $"> {line}"));

        var eb = new EmbedBuilder()
            .WithColor(Accent)
            .WithTitle("🧠 Question of the Day")
            .WithDescription($"{quoted}\n\n💬 *Drop your answer in the chat below!*")
            .WithFooter($"189th • Question of the Day • asked by {s.AuthorName}")
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(guild?.IconUrl))
            eb.WithThumbnailUrl(guild!.IconUrl);

        return eb.Build();
    }

    // ─── Permission gate (mirrors EventCommandHandler) ─────────────────────

    private bool HasPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator || user.GuildPermissions.ManageRoles)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex  = rankRoles.IndexOf(_config.QotdMinRank);
        if (minIndex < 0)
        {
            _logger.LogWarning(
                "QotdMinRank '{MinRank}' not found in RankRoles — /qotd will be admin-only",
                _config.QotdMinRank);
            return false;
        }

        var highestUserIndex = user.Roles
            .Select(r => rankRoles.IndexOf(r.Name))
            .DefaultIfEmpty(-1)
            .Max();

        return highestUserIndex >= minIndex;
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static Embed Form(string title, string? body = null)
    {
        var eb = new EmbedBuilder()
            .WithColor(Accent)
            .WithTitle(title)
            .WithFooter("Reply in this DM • type \"cancel\" to quit • times out after 15 min");
        if (!string.IsNullOrWhiteSpace(body)) eb.WithDescription(body);
        return eb.Build();
    }

    private async Task SweepIdleAsync()
    {
        try
        {
            foreach (var kv in _sessions)
            {
                if (!IsExpired(kv.Value)) continue;
                if (_sessions.TryRemove(kv.Key, out var s))
                {
                    try
                    {
                        await s.Dm.SendMessageAsync(embed: Form("⌛ Timed out",
                            "Looks like you stepped away — I've cancelled this setup and **nothing was posted**. Run `/qotd` whenever you're ready."));
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to send QOTD timeout DM to {User}", kv.Key); }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "QOTD idle sweep failed"); }
    }

    private static bool IsExpired(QotdSession s) =>
        DateTime.UtcNow - s.LastActivityAt > IdleTimeout;

    private void PruneExpired()
    {
        foreach (var kv in _sessions)
            if (IsExpired(kv.Value))
                _sessions.TryRemove(kv.Key, out _);
    }

    private async Task ClearButtons(SocketMessageComponent c, string content)
    {
        await c.UpdateAsync(m =>
        {
            m.Content    = content;
            m.Embed      = null;
            m.Components = new ComponentBuilder().Build();
        });
    }

    private async Task SafeSend(QotdSession s, string content)
    {
        try { await s.Dm.SendMessageAsync(content); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send QOTD DM"); }
    }

    private async Task SafeSendEmbed(QotdSession s, Embed embed)
    {
        try { await s.Dm.SendMessageAsync(embed: embed); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send QOTD DM"); }
    }

    private enum QotdStep { Question, Confirm }

    private sealed class QotdSession
    {
        public ulong AuthorId { get; init; }
        public IDMChannel Dm { get; init; } = null!;
        public ulong GuildId { get; init; }
        public string AuthorName { get; init; } = string.Empty;
        public string Question { get; set; } = string.Empty;
        public QotdStep Step { get; set; }
        public DateTime LastActivityAt { get; set; }
    }
}
