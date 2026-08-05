using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The <c>/jotd</c> slash command — post a Joke of the Day. Modelled directly on
/// <see cref="QotdCommandHandler"/> (/qotd): running the command opens a DM, the
/// bot asks for the joke, shows a preview with Post it/Cancel buttons, then posts
/// the embed (with the 189th logo) to <see cref="BotConfig.JotdChannelId"/>.
///
/// Gated to <see cref="BotConfig.JotdMinRank"/> and above (default SGT). Admins
/// and anyone with Manage Roles always pass.
///
/// ── Routing ──
/// Self-registers SlashCommandExecuted (the /jotd entry point), MessageReceived
/// (the DM joke text) and ButtonExecuted (the "jotd:" confirm/cancel buttons).
/// The DM/button handlers filter to the caller's active session, so this coexists
/// with every other handler on those events.
///
/// ── Sessions ──
/// In-memory, keyed by author id, expired after <see cref="IdleTimeout"/> of
/// inactivity and removed on completion/cancel. A restart drops any in-progress
/// session; the author just re-runs /jotd.
/// </summary>
public sealed class JotdCommandHandler
{
    public const string CommandName = "jotd";

    private const string Prefix = "jotd:";
    private const int MaxJokeLength = 1000;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private static readonly Color Accent = new(0xF1C40F); // gold — grabs attention

    private readonly ConcurrentDictionary<ulong, JotdSession> _sessions = new();

    private readonly DiscordSocketClient _client;
    private readonly ILogger<JotdCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly System.Threading.Timer _idleSweep;

    public JotdCommandHandler(
        DiscordSocketClient client,
        ILogger<JotdCommandHandler> logger,
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
            .WithDescription($"Post a Joke of the Day — I'll DM you to set it up ({minRank}+ only)")
            .Build();

    // ─── /jotd entry point ─────────────────────────────────────────────────

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            await HandleStartAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /jotd");
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
                $"❌ Posting a Joke of the Day is restricted to **{_config.JotdMinRank} and above**.",
                ephemeral: true);
            return;
        }

        var (dm, dmFailure) = await DmGuard.TryOpenAsync(command.User);
        if (dm is null)
        {
            _logger.LogInformation("Could not open DM with {User} for /jotd: {Failure}",
                command.User.Id, dmFailure);
            await command.FollowupAsync(DmGuard.AdviceFor(dmFailure), ephemeral: true);
            return;
        }

        PruneExpired();
        _sessions[command.User.Id] = new JotdSession
        {
            AuthorId       = command.User.Id,
            Dm             = dm,
            GuildId        = command.GuildId.Value,
            AuthorName     = guildUser.DisplayName,
            Step           = JotdStep.Joke,
            LastActivityAt = DateTime.UtcNow,
        };

        try
        {
            await dm.SendMessageAsync(embed: Form("😂 Joke of the Day",
                "What's the **joke** you'd like to post? Send it as your next message.\n\n" +
                "You can use Discord formatting — `**bold**`, `*italics*`, line breaks, emoji, etc."));
            await command.FollowupAsync("📬 Check your DMs — I'll walk you through your Joke of the Day there.", ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not send opening DM to {User} for /jotd", command.User.Id);
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
                "That setup expired from inactivity and **nothing was posted**. Run `/jotd` to start over."));
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var text = message.Content.Trim();

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSendEmbed(s, Form("Cancelled", "No joke was posted. Run `/jotd` to start again anytime."));
            return;
        }

        // Only the Joke step takes text — Confirm is a button step.
        if (s.Step != JotdStep.Joke) return;

        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                await s.Dm.SendMessageAsync(embed: Form("😂 Need a joke",
                    "Send me the joke you'd like to post — e.g. `Why did the scarecrow win an award? He was outstanding in his field.`"));
                return;
            }
            if (text.Length > MaxJokeLength)
            {
                await s.Dm.SendMessageAsync(embed: Form("✂️ A bit too long",
                    $"Keep it under {MaxJokeLength} characters — trim it down and send again."));
                return;
            }

            s.Joke = text;
            await PromptConfirmAsync(s);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JOTD joke step failed for {User}", message.Author.Id);
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, "Something went wrong. Run `/jotd` to start over.");
        }
    }

    // ─── Buttons ───────────────────────────────────────────────────────────

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(Prefix, StringComparison.Ordinal)) return;

        if (!_sessions.TryGetValue(component.User.Id, out var s))
        {
            await component.RespondAsync("That setup has expired. Run `/jotd` to start again.");
            return;
        }
        if (IsExpired(s))
        {
            _sessions.TryRemove(component.User.Id, out _);
            await ClearButtons(component, "⌛ Timed out. Run `/jotd` again.");
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
            _logger.LogError(ex, "JOTD button {CustomId} failed", component.Data.CustomId);
            await SafeSend(s, "⚠️ Something went wrong on that step. Type `cancel` and run `/jotd` to start over.");
        }
    }

    private async Task OnConfirmAsync(JotdSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.AuthorId, out _);

        var guild   = _client.GetGuild(s.GuildId);
        var channel = guild?.GetTextChannel(_config.JotdChannelId);
        if (channel is null)
        {
            await ClearButtons(c, "❌ I couldn't find the Joke of the Day channel. Check the bot's config / channel access.");
            return;
        }

        try
        {
            await channel.SendMessageAsync(embed: BuildPublicEmbed(s, guild!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post JOTD to channel {ChannelId}", _config.JotdChannelId);
            await ClearButtons(c, "❌ Something went wrong posting the joke. Make sure I can post in that channel, then try `/jotd` again.");
            return;
        }

        await ClearButtons(c, $"✅ **Posted!** Your Joke of the Day is live in {MentionUtils.MentionChannel(channel.Id)}.");
    }

    private async Task OnCancelAsync(JotdSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.AuthorId, out _);
        await ClearButtons(c, "❌ Cancelled. Nothing was posted.");
    }

    // ─── Embeds ────────────────────────────────────────────────────────────

    private async Task PromptConfirmAsync(JotdSession s)
    {
        s.Step = JotdStep.Confirm;

        var guild   = _client.GetGuild(s.GuildId);
        var preview = BuildPublicEmbed(s, guild);

        var buttons = new ComponentBuilder()
            .WithButton("Post it", $"{Prefix}confirm", ButtonStyle.Success)
            .WithButton("Cancel",  $"{Prefix}cancel",  ButtonStyle.Danger);

        var channelMention = MentionUtils.MentionChannel(_config.JotdChannelId);
        await s.Dm.SendMessageAsync(
            $"Here's how it'll look in {channelMention} — post it?",
            embed: preview,
            components: buttons.Build());
    }

    /// <summary>
    /// Builds the public JOTD embed: the 189th logo (guild icon) as a thumbnail
    /// to grab attention, the joke rendered as a Discord-markdown blockquote, and
    /// an attribution footer.
    /// </summary>
    private Embed BuildPublicEmbed(JotdSession s, SocketGuild? guild)
    {
        // Render the joke as a blockquote so it stands out. A multi-line joke
        // needs "> " on each line for Discord to quote the whole block.
        var quoted = string.Join("\n", s.Joke
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => $"> {line}"));

        var eb = new EmbedBuilder()
            .WithColor(Accent)
            .WithTitle("😂 Joke of the Day")
            .WithDescription($"{quoted}\n\n😆 *Drop a reaction if it got you!*")
            .WithFooter($"189th • Joke of the Day • shared by {s.AuthorName}")
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
        var minIndex  = rankRoles.IndexOf(_config.JotdMinRank);
        if (minIndex < 0)
        {
            _logger.LogWarning(
                "JotdMinRank '{MinRank}' not found in RankRoles — /jotd will be admin-only",
                _config.JotdMinRank);
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
                            "Looks like you stepped away — I've cancelled this setup and **nothing was posted**. Run `/jotd` whenever you're ready."));
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to send JOTD timeout DM to {User}", kv.Key); }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "JOTD idle sweep failed"); }
    }

    private static bool IsExpired(JotdSession s) =>
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

    private async Task SafeSend(JotdSession s, string content)
    {
        try { await s.Dm.SendMessageAsync(content); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send JOTD DM"); }
    }

    private async Task SafeSendEmbed(JotdSession s, Embed embed)
    {
        try { await s.Dm.SendMessageAsync(embed: embed); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send JOTD DM"); }
    }

    private enum JotdStep { Joke, Confirm }

    private sealed class JotdSession
    {
        public ulong AuthorId { get; init; }
        public IDMChannel Dm { get; init; } = null!;
        public ulong GuildId { get; init; }
        public string AuthorName { get; init; } = string.Empty;
        public string Joke { get; set; } = string.Empty;
        public JotdStep Step { get; set; }
        public DateTime LastActivityAt { get; set; }
    }
}
