using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The <c>/say</c> slash command. Lets an officer write a message that the bot
/// posts in its own voice, so an announcement reads as coming from the 189th
/// rather than from one person. Three subcommands:
///   • post   opens the compose modal and publishes the message.
///   • edit   reopens that text, prefilled, and applies the fix in place.
///   • delete pulls the message and keeps the wording on the audit trail.
///
/// ── Why the text lives in a modal ──
/// A slash string option cannot carry line breaks or blank lines, which is most
/// of what an announcement needs. The cost is that a modal has to BE the
/// response to the interaction (it cannot be deferred first) and Discord allows
/// 3 seconds for that, so nothing on the path to opening one may make a REST
/// call. Both modals here are built from cached state and the local database
/// only. Every gate then runs a SECOND time on submit, because a modal can sit
/// open for minutes while roles or channel overwrites change underneath it.
///
/// ── Who can use it ──
/// <see cref="BotConfig.SayCommandMinRank"/> and above, with the usual
/// Administrator / Manage Roles bypass. Posting anonymously as the bot is the
/// kind of thing that needs a paper trail, so every post, edit and delete is
/// written to <see cref="BotConfig.SayAuditChannelId"/> (falling back to
/// HqChannelId) with the actor, the destination and the text.
///
/// ── What can be edited ──
/// Only messages this command posted, matched by row in
/// <see cref="SayMessage"/>. The bot authors plenty of messages it manages
/// itself (event posts, polls, the XP board) and a hand-edit of one of those
/// would be overwritten on the next render or corrupt what the renderer reads
/// back, so those are refused rather than guessed at.
///
/// ── Pings ──
/// Nothing in the body ever pings. A raw role or user mention pasted into the
/// text renders as a mention but stays silent, because the allowed-mentions
/// payload only ever names the target picked in the <c>ping</c> option. That
/// keeps a stray @everyone in pasted copy from waking the server up. Pinging
/// @everyone for real needs the ping_everyone flag AND the Mention Everyone
/// permission on the caller. An edit sends AllowedMentions.None, so fixing a
/// typo can never produce a fresh notification.
///
/// ── Routing ──
/// Self-registers SlashCommandExecuted and ModalSubmitted, filtered to
/// <see cref="CommandName"/> and the two modal prefixes, so it coexists with
/// every other handler on those events.
/// </summary>
public sealed class SayCommandHandler
{
    public const string CommandName = "say";

    private const string PostModalPrefix = "say:";
    private const string EditModalPrefix = "sayedit:";
    private const string BodyInputId     = "say_body";
    private const string TitleInputId    = "say_title";

    // Discord's own caps are 2000 for message content and 4096 for an embed
    // description. The plain-text box is held under the cap to leave room for a
    // ping line above the message.
    private const int MaxPlainBody  = 1900;
    private const int MaxEmbedBody  = 4000;
    private const int MaxEmbedTitle = 200;
    private const int MaxMessage    = 2000;

    // Gold for a post, blue for an edit, red for a delete, so the audit channel
    // reads at a glance.
    private static readonly Color Accent     = new(0xF1C40F);
    private static readonly Color EditColor   = new(0x3498DB);
    private static readonly Color DeleteColor = new(0xE74C3C);

    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _services;
    private readonly ILogger<SayCommandHandler> _logger;
    private readonly BotConfig _config;

    public SayCommandHandler(
        DiscordSocketClient client,
        IServiceProvider services,
        ILogger<SayCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _client   = client;
        _services = services;
        _logger   = logger;
        _config   = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.ModalSubmitted       += OnModalSubmittedAsync;
    }

    public static SlashCommandProperties BuildCommand(string minRank) =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription($"Post a message as the bot, and fix it afterwards ({minRank}+ only)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("post")
                .WithDescription("Write a message and post it as the bot")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("channel")
                    .WithDescription("Where to post it. Defaults to the channel you run this in.")
                    .WithType(ApplicationCommandOptionType.Channel)
                    .WithRequired(false)
                    .AddChannelType(ChannelType.Text)
                    .AddChannelType(ChannelType.News)
                    .AddChannelType(ChannelType.PublicThread)
                    .AddChannelType(ChannelType.PrivateThread))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("as_embed")
                    .WithDescription("Post it inside an embed with the 189th logo instead of plain text")
                    .WithType(ApplicationCommandOptionType.Boolean)
                    .WithRequired(false))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("ping")
                    .WithDescription("A role or member to tag above the message")
                    .WithType(ApplicationCommandOptionType.Mentionable)
                    .WithRequired(false))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("ping_everyone")
                    .WithDescription("Also tag @everyone (needs the Mention Everyone permission)")
                    .WithType(ApplicationCommandOptionType.Boolean)
                    .WithRequired(false)))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("edit")
                .WithDescription("Fix the wording of a message the bot posted for you")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("message")
                    .WithDescription("Message link, or the message ID (right-click the message to copy either)")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("delete")
                .WithDescription("Remove a message the bot posted for you")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("message")
                    .WithDescription("Message link, or the message ID (right-click the message to copy either)")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)))
            .Build();

    // ─── Entry point ───────────────────────────────────────────────────────

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            var sub  = command.Data.Options.FirstOrDefault();
            var opts = sub?.Options;

            switch (sub?.Name)
            {
                case "edit":   await HandleEditOpenAsync(command, opts);   break;
                case "delete": await HandleDeleteAsync(command, opts);     break;
                default:       await HandlePostOpenAsync(command, opts);   break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /say");
            try
            {
                if (command.HasResponded)
                    await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                else
                    await command.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    // ─── /say post ─────────────────────────────────────────────────────────

    /// <summary>
    /// Validates everything that can be validated from cache, then opens the
    /// compose modal. Nothing here may await a REST call: the modal has to be
    /// the very first response to the interaction and Discord allows 3 seconds.
    /// </summary>
    private async Task HandlePostOpenAsync(
        SocketSlashCommand command, IReadOnlyCollection<SocketSlashCommandDataOption>? opts)
    {
        if (!await PassesGateAsync(command)) return;

        var caller   = (SocketGuildUser)command.User;
        var guild    = caller.Guild;
        var asEmbed  = GetBool(opts, "as_embed");
        var everyone = GetBool(opts, "ping_everyone");

        // ── Target channel ──
        var picked   = GetOption(opts, "channel")?.Value as IGuildChannel;
        var targetId = picked?.Id ?? command.ChannelId ?? 0;

        if (guild.GetChannel(targetId) is not SocketTextChannel target)
        {
            await command.RespondAsync(
                "I can't see that channel. Pick a text channel or thread I have access to.",
                ephemeral: true);
            return;
        }

        if (!await PassesChannelGateAsync(command, caller, guild, target, everyone)) return;

        // ── Ping target ──
        var (pingKind, pingId) = GetOption(opts, "ping")?.Value switch
        {
            SocketRole role      => ('r', role.Id),
            IRole role           => ('r', role.Id),
            SocketGuildUser user => ('u', user.Id),
            IUser user           => ('u', user.Id),
            _                    => ('n', 0UL),
        };

        var modal = new ModalBuilder()
            .WithTitle(Truncate($"Post as the bot in #{target.Name}", 45))
            .WithCustomId($"{PostModalPrefix}{target.Id}:{(asEmbed ? 1 : 0)}:{pingKind}{pingId}:{(everyone ? 1 : 0)}");

        if (asEmbed)
        {
            modal.AddTextInput(
                label: "Heading (optional)",
                customId: TitleInputId,
                style: TextInputStyle.Short,
                placeholder: "Shown in bold at the top of the embed",
                minLength: 0, maxLength: MaxEmbedTitle, required: false);
        }

        modal.AddTextInput(
            label: "Message",
            customId: BodyInputId,
            style: TextInputStyle.Paragraph,
            placeholder: "Type it exactly as you want it posted. Line breaks and Discord formatting both work.",
            minLength: 1, maxLength: asEmbed ? MaxEmbedBody : MaxPlainBody, required: true);

        await command.RespondWithModalAsync(modal.Build());
    }

    private async Task HandlePostSubmitAsync(SocketModal modal)
    {
        await modal.DeferAsync(ephemeral: true);

        if (!TryParsePostCustomId(modal.Data.CustomId, out var targetId, out var asEmbed,
                                  out var pingKind, out var pingId, out var everyone))
        {
            await modal.FollowupAsync("That form is from an older version of the command. Run `/say post` again.", ephemeral: true);
            return;
        }

        if (modal.GuildId is null || modal.User is not SocketGuildUser caller)
        {
            await modal.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        // Re-run the gates. A modal can sit open for minutes, and roles or
        // channel overwrites can change while it does.
        if (!_config.SayEnabled || !HasPermission(caller))
        {
            await modal.FollowupAsync("You no longer have permission to post as the bot.", ephemeral: true);
            return;
        }

        var guild = caller.Guild;
        if (guild.GetChannel(targetId) is not SocketTextChannel target)
        {
            await modal.FollowupAsync("That channel has gone away. Run `/say post` again and pick another one.", ephemeral: true);
            return;
        }

        if (!CanSendIn(caller, target) || !CanSendIn(guild.CurrentUser, target))
        {
            await modal.FollowupAsync(
                $"Posting in {MentionUtils.MentionChannel(target.Id)} isn't possible any more (permissions changed).",
                ephemeral: true);
            return;
        }

        if (everyone && !CanMentionEveryone(caller, target))
        {
            await modal.FollowupAsync("You no longer have the Mention Everyone permission in that channel.", ephemeral: true);
            return;
        }

        var (body, title) = ReadModalFields(modal);
        if (string.IsNullOrWhiteSpace(body))
        {
            await modal.FollowupAsync("There was nothing in the message box, so nothing was posted.", ephemeral: true);
            return;
        }

        // ── Ping line ──
        // Mentions inside an embed are inert, so the tags always go in the
        // plain content above it.
        var pings = new List<string>();
        if (everyone) pings.Add("@everyone");
        if (pingKind == 'r') pings.Add(MentionUtils.MentionRole(pingId));
        if (pingKind == 'u') pings.Add(MentionUtils.MentionUser(pingId));
        var pingLine = string.Join(" ", pings);

        var allowed = new AllowedMentions
        {
            AllowedTypes       = everyone ? AllowedMentionTypes.Everyone : AllowedMentionTypes.None,
            MentionRepliedUser = false,
        };
        if (pingKind == 'r') allowed.RoleIds = new List<ulong> { pingId };
        if (pingKind == 'u') allowed.UserIds = new List<ulong> { pingId };

        var (content, embed) = BuildPayload(asEmbed, pingLine, title, body, guild);

        if (!asEmbed && content is not null && content.Length > MaxMessage)
        {
            await modal.FollowupAsync(
                $"That comes to {content.Length} characters with the ping line, and Discord's limit is {MaxMessage}. " +
                "Trim it or run `/say post` with as_embed:true, which allows more room.",
                ephemeral: true);
            return;
        }

        IUserMessage posted;
        try
        {
            posted = await target.SendMessageAsync(text: content, embed: embed, allowedMentions: allowed);
        }
        catch (Discord.Net.HttpException ex)
        {
            _logger.LogWarning(ex, "Discord refused a /say post to {ChannelId}", target.Id);
            await modal.FollowupAsync($"Discord refused the post: {ex.Reason ?? ex.Message}", ephemeral: true);
            return;
        }

        await RecordPostAsync(guild.Id, target.Id, posted.Id, caller, asEmbed, title, body, pingLine);

        _logger.LogInformation(
            "/say post by {User} ({UserId}) to #{Channel} ({ChannelId}), embed={Embed}, ping={Ping}, everyone={Everyone}, {Length} chars",
            caller.Username, caller.Id, target.Name, target.Id, asEmbed, pingKind, everyone, body.Length);

        var note = pingKind == 'n' && !everyone
            ? "Nothing in the text pinged anyone."
            : "Only the tag you picked pinged.";

        await modal.FollowupAsync(
            $"Posted in {MentionUtils.MentionChannel(target.Id)}. {note} Typo? `/say edit` with this link.\n{posted.GetJumpUrl()}",
            ephemeral: true);

        await LogAuditAsync(guild, caller, "Message posted as the bot", Accent, target.Id,
            posted.GetJumpUrl(), asEmbed, pingLine, title, body, previousBody: null);
    }

    // ─── /say edit ─────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the edit modal prefilled with the wording as it was typed. The
    /// prefill comes from the database rather than from Discord on purpose: a
    /// message fetch is a REST call, and there is no room for one before a modal
    /// response. A local SQLite read costs a few milliseconds.
    /// </summary>
    private async Task HandleEditOpenAsync(
        SocketSlashCommand command, IReadOnlyCollection<SocketSlashCommandDataOption>? opts)
    {
        if (!await PassesGateAsync(command)) return;

        var caller = (SocketGuildUser)command.User;
        var raw    = GetOption(opts, "message")?.Value?.ToString() ?? string.Empty;

        if (!TryParseMessageReference(raw, out var messageId))
        {
            await command.RespondAsync(
                "I couldn't read a message from that. Right-click the message and use Copy Message Link, or Copy Message ID with developer mode on.",
                ephemeral: true);
            return;
        }

        var row = await FindRowAsync(caller.Guild.Id, messageId);
        if (row is null)
        {
            await command.RespondAsync(
                "I can only edit messages posted through `/say`. Anything else the bot writes (event posts, polls, the XP board) is rendered from its own data and would be overwritten on the next refresh.",
                ephemeral: true);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle("Edit the bot's message")
            .WithCustomId($"{EditModalPrefix}{row.Id}");

        if (row.AsEmbed)
        {
            modal.AddTextInput(
                label: "Heading (optional)",
                customId: TitleInputId,
                style: TextInputStyle.Short,
                placeholder: "Shown in bold at the top of the embed",
                minLength: 0, maxLength: MaxEmbedTitle, required: false,
                value: Truncate(row.Title ?? string.Empty, MaxEmbedTitle));
        }

        modal.AddTextInput(
            label: "Message",
            customId: BodyInputId,
            style: TextInputStyle.Paragraph,
            placeholder: "Fix whatever needs fixing. The tags above the message stay as they were.",
            minLength: 1, maxLength: row.AsEmbed ? MaxEmbedBody : MaxPlainBody, required: true,
            value: Truncate(row.Body, row.AsEmbed ? MaxEmbedBody : MaxPlainBody));

        await command.RespondWithModalAsync(modal.Build());
    }

    private async Task HandleEditSubmitAsync(SocketModal modal)
    {
        await modal.DeferAsync(ephemeral: true);

        if (!int.TryParse(modal.Data.CustomId[EditModalPrefix.Length..], out var rowId))
        {
            await modal.FollowupAsync("That form is from an older version of the command. Run `/say edit` again.", ephemeral: true);
            return;
        }

        if (modal.GuildId is null || modal.User is not SocketGuildUser caller)
        {
            await modal.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        if (!_config.SayEnabled || !HasPermission(caller))
        {
            await modal.FollowupAsync("You no longer have permission to edit the bot's messages.", ephemeral: true);
            return;
        }

        using var scope = _services.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var row = await db.SayMessages.FirstOrDefaultAsync(m => m.Id == rowId && m.DeletedUtc == null);

        if (row is null || row.GuildId != caller.Guild.Id)
        {
            await modal.FollowupAsync("That message is no longer on file. It may have been deleted already.", ephemeral: true);
            return;
        }

        var guild = caller.Guild;
        if (guild.GetChannel(row.ChannelId) is not SocketTextChannel target)
        {
            await modal.FollowupAsync("The channel that message was in has gone away.", ephemeral: true);
            return;
        }

        var (body, title) = ReadModalFields(modal);
        if (string.IsNullOrWhiteSpace(body))
        {
            await modal.FollowupAsync("The message box came back empty, so nothing was changed.", ephemeral: true);
            return;
        }

        var pingLine = row.PingLine ?? string.Empty;
        var (content, embed) = BuildPayload(row.AsEmbed, pingLine, title, body, guild);

        if (!row.AsEmbed && content is not null && content.Length > MaxMessage)
        {
            await modal.FollowupAsync(
                $"That comes to {content.Length} characters with the ping line, and Discord's limit is {MaxMessage}. Trim it and try again.",
                ephemeral: true);
            return;
        }

        IMessage? live;
        try
        {
            live = await target.GetMessageAsync(row.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch message {MessageId} for a /say edit", row.MessageId);
            live = null;
        }

        if (live is not IUserMessage editable)
        {
            await modal.FollowupAsync(
                "That message isn't there any more. Someone may have deleted it in Discord.",
                ephemeral: true);
            return;
        }

        var previousBody = row.Body;

        try
        {
            // AllowedMentions.None on an edit: a tag added while fixing a typo
            // must never produce a fresh notification.
            await editable.ModifyAsync(m =>
            {
                m.Content         = content ?? string.Empty;
                m.Embed           = embed;
                m.AllowedMentions = AllowedMentions.None;
            });
        }
        catch (Discord.Net.HttpException ex)
        {
            _logger.LogWarning(ex, "Discord refused a /say edit of {MessageId}", row.MessageId);
            await modal.FollowupAsync($"Discord refused the edit: {ex.Reason ?? ex.Message}", ephemeral: true);
            return;
        }

        row.Body           = body;
        row.Title          = row.AsEmbed ? (string.IsNullOrWhiteSpace(title) ? null : title) : null;
        row.EditedUtc      = DateTime.UtcNow;
        row.EditedByUserId = caller.Id;
        row.EditedByName   = caller.DisplayName;
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "/say edit by {User} ({UserId}) of message {MessageId} in #{Channel}",
            caller.Username, caller.Id, row.MessageId, target.Name);

        await modal.FollowupAsync(
            $"Updated in {MentionUtils.MentionChannel(target.Id)}. Nobody was re-pinged.\n{editable.GetJumpUrl()}",
            ephemeral: true);

        await LogAuditAsync(guild, caller, "Bot message edited", EditColor, target.Id,
            editable.GetJumpUrl(), row.AsEmbed, pingLine, title, body, previousBody);
    }

    // ─── /say delete ───────────────────────────────────────────────────────

    private async Task HandleDeleteAsync(
        SocketSlashCommand command, IReadOnlyCollection<SocketSlashCommandDataOption>? opts)
    {
        if (!await PassesGateAsync(command)) return;

        await command.DeferAsync(ephemeral: true);

        var caller = (SocketGuildUser)command.User;
        var guild  = caller.Guild;
        var raw    = GetOption(opts, "message")?.Value?.ToString() ?? string.Empty;

        if (!TryParseMessageReference(raw, out var messageId))
        {
            await command.FollowupAsync(
                "I couldn't read a message from that. Right-click the message and use Copy Message Link, or Copy Message ID with developer mode on.",
                ephemeral: true);
            return;
        }

        using var scope = _services.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var row = await db.SayMessages.FirstOrDefaultAsync(
            m => m.GuildId == guild.Id && m.MessageId == messageId && m.DeletedUtc == null);

        if (row is null)
        {
            await command.FollowupAsync(
                "I can only remove messages posted through `/say`. For anything else, delete it in Discord directly.",
                ephemeral: true);
            return;
        }

        var target = guild.GetChannel(row.ChannelId) as SocketTextChannel;

        if (target is not null)
        {
            try
            {
                if (await target.GetMessageAsync(row.MessageId) is IMessage live)
                    await live.DeleteAsync();
            }
            catch (Discord.Net.HttpException ex)
            {
                _logger.LogWarning(ex, "Discord refused a /say delete of {MessageId}", row.MessageId);
                await command.FollowupAsync($"Discord refused the delete: {ex.Reason ?? ex.Message}", ephemeral: true);
                return;
            }
        }

        row.DeletedUtc      = DateTime.UtcNow;
        row.DeletedByUserId = caller.Id;
        row.DeletedByName   = caller.DisplayName;
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "/say delete by {User} ({UserId}) of message {MessageId}",
            caller.Username, caller.Id, row.MessageId);

        // Hand the wording back so a mis-posted message can be re-sent
        // somewhere else without retyping it.
        await command.FollowupAsync(
            $"Removed. Here is the text if you want it again:\n>>> {Truncate(row.Body, 1500)}",
            ephemeral: true);

        await LogAuditAsync(guild, caller, "Bot message deleted", DeleteColor, row.ChannelId,
            jumpUrl: null, row.AsEmbed, row.PingLine ?? string.Empty, row.Title ?? string.Empty,
            body: row.Body, previousBody: null);
    }

    // ─── Modal routing ─────────────────────────────────────────────────────

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        var isPost = modal.Data.CustomId.StartsWith(PostModalPrefix, StringComparison.Ordinal);
        var isEdit = modal.Data.CustomId.StartsWith(EditModalPrefix, StringComparison.Ordinal);
        if (!isPost && !isEdit) return;

        try
        {
            if (isEdit) await HandleEditSubmitAsync(modal);
            else        await HandlePostSubmitAsync(modal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling a /say modal");
            try { await modal.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    // ─── Rendering ─────────────────────────────────────────────────────────

    /// <summary>
    /// Turns the stored pieces into what actually goes on the wire. Shared by
    /// post and edit so an edited message renders identically to the original.
    /// </summary>
    private static (string? Content, Embed? Embed) BuildPayload(
        bool asEmbed, string pingLine, string title, string body, SocketGuild guild)
    {
        if (!asEmbed)
        {
            var text = pingLine.Length > 0 ? $"{pingLine}\n{body}" : body;
            return (text, null);
        }

        var eb = new EmbedBuilder()
            .WithColor(Accent)
            .WithDescription(body);

        if (!string.IsNullOrWhiteSpace(title))
            eb.WithTitle(Truncate(title, 256));

        if (!string.IsNullOrWhiteSpace(guild.IconUrl))
            eb.WithThumbnailUrl(guild.IconUrl);

        return (pingLine.Length > 0 ? pingLine : null, eb.Build());
    }

    // ─── Storage ───────────────────────────────────────────────────────────

    private async Task RecordPostAsync(
        ulong guildId, ulong channelId, ulong messageId, SocketGuildUser author,
        bool asEmbed, string title, string body, string pingLine)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            db.SayMessages.Add(new SayMessage
            {
                GuildId      = guildId,
                ChannelId    = channelId,
                MessageId    = messageId,
                AuthorUserId = author.Id,
                AuthorName   = author.DisplayName,
                AsEmbed      = asEmbed,
                Title        = asEmbed && !string.IsNullOrWhiteSpace(title) ? title : null,
                Body         = body,
                PingLine     = pingLine.Length > 0 ? pingLine : null,
                PostedUtc    = DateTime.UtcNow,
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // The message is already live, so this is not worth failing the
            // command over. The cost is that /say edit will not recognise it.
            _logger.LogError(ex, "Could not record the /say post {MessageId} for later editing", messageId);
        }
    }

    private async Task<SayMessage?> FindRowAsync(ulong guildId, ulong messageId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        return await db.SayMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.GuildId == guildId && m.MessageId == messageId && m.DeletedUtc == null);
    }

    // ─── Audit trail ───────────────────────────────────────────────────────

    /// <summary>
    /// Posts who did what, where. Best effort: a missing or unreachable audit
    /// channel is logged and never fails the command, since the message has
    /// already gone out (or gone away) by this point.
    /// </summary>
    private async Task LogAuditAsync(
        SocketGuild guild,
        SocketGuildUser actor,
        string heading,
        Color color,
        ulong channelId,
        string? jumpUrl,
        bool asEmbed,
        string pingLine,
        string title,
        string body,
        string? previousBody)
    {
        var auditChannelId = _config.SayAuditChannelId != 0 ? _config.SayAuditChannelId : _config.HqChannelId;
        if (auditChannelId == 0)
        {
            _logger.LogWarning(
                "Neither SayAuditChannelId nor HqChannelId is set, so the /say action by {User} was not logged to Discord",
                actor.Id);
            return;
        }

        if (guild.GetTextChannel(auditChannelId) is not SocketTextChannel audit)
        {
            _logger.LogWarning(
                "The /say audit channel {ChannelId} did not resolve in {Guild}, so the action by {User} was not logged to Discord",
                auditChannelId, guild.Name, actor.Id);
            return;
        }

        try
        {
            var eb = new EmbedBuilder()
                .WithColor(color)
                .WithTitle(heading)
                .AddField("By", $"{actor.Mention} ({actor.DisplayName})", true)
                .AddField("Channel", MentionUtils.MentionChannel(channelId), true)
                .AddField("Format", asEmbed ? "Embed" : "Plain text", true)
                .AddField("Tagged", pingLine.Length > 0 ? pingLine : "nobody", true)
                .WithCurrentTimestamp();

            if (!string.IsNullOrWhiteSpace(jumpUrl))
                eb.WithDescription($"[Jump to it]({jumpUrl})");

            if (!string.IsNullOrWhiteSpace(title))
                eb.AddField("Heading", Truncate(title, 256));

            if (previousBody is not null)
                eb.AddField("Was", Truncate(previousBody, 1000));

            eb.AddField(previousBody is not null ? "Now" : "Text", Truncate(body, 1000));

            await audit.SendMessageAsync(embed: eb.Build(), allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the /say audit entry for {User}", actor.Id);
        }
    }

    // ─── Gates ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The checks every subcommand shares. Responds with the reason and returns
    /// false when it fails. Awaits only the response, so it stays safe on the
    /// path to opening a modal.
    /// </summary>
    private async Task<bool> PassesGateAsync(SocketSlashCommand command)
    {
        if (!_config.SayEnabled)
        {
            await command.RespondAsync(
                "Posting as the bot is switched off right now (SayEnabled is false in the bot config).",
                ephemeral: true);
            return false;
        }

        if (command.GuildId is null || command.User is not SocketGuildUser caller)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return false;
        }

        if (!HasPermission(caller))
        {
            await command.RespondAsync(
                $"Posting as the bot is restricted to **{_config.SayCommandMinRank} and above**.",
                ephemeral: true);
            return false;
        }

        return true;
    }

    private async Task<bool> PassesChannelGateAsync(
        SocketSlashCommand command, SocketGuildUser caller, SocketGuild guild,
        SocketTextChannel target, bool everyone)
    {
        // The caller cannot use the bot to reach a channel they are locked out
        // of themselves.
        if (!CanSendIn(caller, target))
        {
            await command.RespondAsync(
                $"You don't have permission to post in {MentionUtils.MentionChannel(target.Id)}, so I won't post there for you.",
                ephemeral: true);
            return false;
        }

        if (!CanSendIn(guild.CurrentUser, target))
        {
            await command.RespondAsync(
                $"I can't post in {MentionUtils.MentionChannel(target.Id)}. Give my role Send Messages there and try again.",
                ephemeral: true);
            return false;
        }

        if (everyone && !CanMentionEveryone(caller, target))
        {
            await command.RespondAsync(
                "Tagging @everyone needs the Mention Everyone permission in that channel. " +
                "Run it again without ping_everyone, or ask an admin to grant it.",
                ephemeral: true);
            return false;
        }

        return true;
    }

    /// <summary>
    /// SyncWithHandlers: ReminderCommandHandler.HasPermission. Administrator /
    /// Manage Roles bypass, else any role at or above SayCommandMinRank in the
    /// RankRoles ladder.
    /// </summary>
    private bool HasPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator || user.GuildPermissions.ManageRoles)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex  = rankRoles.IndexOf(_config.SayCommandMinRank);
        if (minIndex < 0)
        {
            _logger.LogWarning(
                "SayCommandMinRank '{MinRank}' not found in RankRoles, so /say will be admin-only",
                _config.SayCommandMinRank);
            return false;
        }

        var highestUserIndex = user.Roles
            .Select(r => rankRoles.IndexOf(r.Name))
            .DefaultIfEmpty(-1)
            .Max();

        return highestUserIndex >= minIndex;
    }

    /// <summary>
    /// Threads carry their own permission bit, so a plain SendMessages check
    /// would wrongly refuse a thread the member can clearly post in.
    /// </summary>
    private static bool CanSendIn(SocketGuildUser user, SocketTextChannel channel)
    {
        var perms = user.GetPermissions(channel);
        if (!perms.ViewChannel) return false;
        return channel is SocketThreadChannel ? perms.SendMessagesInThreads : perms.SendMessages;
    }

    private static bool CanMentionEveryone(SocketGuildUser user, SocketTextChannel channel) =>
        user.GuildPermissions.Administrator || user.GetPermissions(channel).MentionEveryone;

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static SocketSlashCommandDataOption? GetOption(
        IReadOnlyCollection<SocketSlashCommandDataOption>? opts, string name) =>
        opts?.FirstOrDefault(o => o.Name == name);

    private static bool GetBool(IReadOnlyCollection<SocketSlashCommandDataOption>? opts, string name) =>
        GetOption(opts, name)?.Value as bool? ?? false;

    private static (string Body, string Title) ReadModalFields(SocketModal modal)
    {
        var fields = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? string.Empty);
        return (fields.GetValueOrDefault(BodyInputId, string.Empty).Trim(),
                fields.GetValueOrDefault(TitleInputId, string.Empty).Trim());
    }

    /// <summary>
    /// Accepts a full message link or a bare message ID. The link form is what
    /// Copy Message Link produces; the bare ID is what Copy Message ID produces
    /// with developer mode on, and people paste both.
    /// </summary>
    private static bool TryParseMessageReference(string raw, out ulong messageId)
    {
        messageId = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim().TrimEnd('/');
        var last    = trimmed.Split('/').LastOrDefault() ?? string.Empty;

        // A link can carry a query string, e.g. ?jump=1.
        var queryStart = last.IndexOf('?');
        if (queryStart >= 0) last = last[..queryStart];

        return ulong.TryParse(last, out messageId) && messageId > 0;
    }

    /// <summary>Parses "say:{channelId}:{embed}:{kind}{pingId}:{everyone}".</summary>
    private static bool TryParsePostCustomId(
        string customId, out ulong channelId, out bool asEmbed,
        out char pingKind, out ulong pingId, out bool everyone)
    {
        channelId = 0; asEmbed = false; pingKind = 'n'; pingId = 0; everyone = false;

        var parts = customId[PostModalPrefix.Length..].Split(':');
        if (parts.Length != 4) return false;
        if (!ulong.TryParse(parts[0], out channelId)) return false;

        asEmbed  = parts[1] == "1";
        everyone = parts[3] == "1";

        if (parts[2].Length < 2) return false;
        pingKind = parts[2][0];
        if (pingKind is not ('n' or 'r' or 'u')) return false;
        if (!ulong.TryParse(parts[2][1..], out pingId)) return false;
        if (pingKind == 'n') pingId = 0;

        return true;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..(max - 1)] + "…");
}
