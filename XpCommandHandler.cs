using System.Text;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The member-facing and officer-facing XP commands, plus the leaderboard's
/// pagination buttons.
///
///   /xp [member]        — a member's season card: level, progress, placement,
///                         lifetime total, and a per-source breakdown.
///   /xp-leaderboard     — the standings as a private, paged view.
///   /xp-season          — status / start / end. start and end are officer-gated
///                         (XpAdminMinRank); seasons NEVER roll over on their own.
///   /xp-adjust          — manual grant or deduction with a mandatory reason.
///                         Gated on the HQ role (BotConfig.XpAdjustRoleId), NOT on
///                         the rank threshold the season subcommands use.
///
/// ── Why paging is private ──
/// The pinned board is one shared message, so paging it in place would mean one
/// member's click changes what the entire clan sees, two people clicking fight
/// each other, and the refresh cycle has to guess whether to snap back to page 1.
/// Instead the board's buttons open an EPHEMERAL view — visible only to whoever
/// clicked, never posted into the channel, and cleared by Discord when they
/// dismiss it or reload the client. Paging inside that view edits it in place.
///
/// Discord expires an interaction token after 15 minutes, so a private view left
/// open overnight stops responding. That's handled rather than fixed: the buttons
/// simply stop working and the member clicks the board again. Keeping them alive
/// would mean re-posting messages nobody asked for.
///
/// ── The breakdown is the feature ──
/// /xp exists mostly so nobody has to take the number on faith. Every point
/// traces back to a ledger row and the card shows the split by source, so "why is
/// he above me" always has a checkable answer. A leaderboard people suspect is
/// arbitrary is worse than no leaderboard.
///
/// ── XP and rank are unrelated, and the copy says so ──
/// Every surface here repeats it, because the most likely way this feature causes
/// a problem is a member assuming a high level entitles them to a promotion.
/// </summary>
public sealed class XpCommandHandler
{
    public const string CommandName = "xp";
    public const string LeaderboardCommandName = "xp-leaderboard";
    public const string SeasonCommandName = "xp-season";
    public const string AdjustCommandName = "xp-adjust";
    public const string DmsCommandName = "xp-dms";

    /// <summary>Prefix for every button this handler owns. Keeps the filter cheap.</summary>
    private const string ButtonPrefix = "xp:";

    private const int ProgressBarWidth = 12;

    /// <summary>Ceiling on the courtesy board refresh an officer command waits for.</summary>
    private static readonly TimeSpan BoardRefreshTimeout = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly XpService _xp;
    private readonly XpLeaderboardService _board;
    private readonly XpAccrualService _accrual;
    private readonly EventTimeParser _time;
    private readonly BotConfig _config;
    private readonly ILogger<XpCommandHandler> _logger;

    public XpCommandHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        XpService xp,
        XpLeaderboardService board,
        XpAccrualService accrual,
        EventTimeParser time,
        IOptions<BotConfig> config,
        ILogger<XpCommandHandler> logger)
    {
        _services = services;
        _client = client;
        _xp = xp;
        _board = board;
        _accrual = accrual;
        _time = time;
        _config = config.Value;
        _logger = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.ButtonExecuted       += OnButtonAsync;
    }

    // ─── Command definitions ─────────────────────────────────────────────────

    public static SlashCommandProperties BuildXpCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Show your clan XP card — level, season standing, and where the XP came from")
            .AddOption("member", ApplicationCommandOptionType.User,
                "Someone else's card (defaults to you)", isRequired: false)
            .Build();

    public static SlashCommandProperties BuildLeaderboardCommand() =>
        new SlashCommandBuilder()
            .WithName(LeaderboardCommandName)
            .WithDescription("Show the XP standings for the current season, privately")
            .AddOption("page", ApplicationCommandOptionType.Integer,
                "Which page to open (defaults to page 1)", isRequired: false)
            .Build();

    public static SlashCommandProperties BuildSeasonCommand(string minRank) =>
        new SlashCommandBuilder()
            .WithName(SeasonCommandName)
            .WithDescription($"View or manage the XP season ({minRank}+ to start/end)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("status")
                .WithDescription("Show the current season, how long is left, and the podium")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("start")
                .WithDescription($"Open an XP season now, or line one up to open later ({minRank}+ only)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("name", ApplicationCommandOptionType.String,
                    "Optional season name, e.g. \"Launch\"", isRequired: false)
                .AddOption("when", ApplicationCommandOptionType.String,
                    "When to open it, e.g. \"August 1\" or \"Saturday 8pm\". Blank opens it now.", isRequired: false)
                .AddOption("end_when", ApplicationCommandOptionType.String,
                    "When to close it automatically, e.g. \"August 31\" (runs through that whole day)", isRequired: false)
                .AddOption("days", ApplicationCommandOptionType.Integer,
                    "Planned length in days. Display only, and ignored entirely if you set end_when.", isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("end")
                .WithDescription($"Close the season now, or set it to close on its own ({minRank}+ only)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("when", ApplicationCommandOptionType.String,
                    "Close it automatically at this time instead of now, e.g. \"August 31\"", isRequired: false)
                .AddOption("confirm", ApplicationCommandOptionType.Boolean,
                    "Required to close it right now. Not needed when you set a time.", isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("cancel")
                .WithDescription($"Call off a scheduled season start, or a scheduled close ({minRank}+ only)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("confirm", ApplicationCommandOptionType.Boolean,
                    "Must be true", isRequired: true))
            .Build();

    public static SlashCommandProperties BuildAdjustCommand() =>
        new SlashCommandBuilder()
            .WithName(AdjustCommandName)
            .WithDescription("Grant or deduct XP manually (HQ only)")
            .AddOption("member", ApplicationCommandOptionType.User,
                "The member to adjust", isRequired: true)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("amount")
                .WithDescription("XP to add (negative to deduct)")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(true)
                // Bounded so the (int) narrowing below can't wrap: 2147483648 would
                // become int.MinValue — a "grant" silently becoming the largest
                // possible deduction — and Math.Abs(int.MinValue) then throws AFTER
                // the award was already written.
                .WithMinValue(-1_000_000)
                .WithMaxValue(1_000_000))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("reason")
                .WithDescription("Why. Shown in the ledger and on their /xp card.")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                // Discord allows 6000 characters in a string option. This value is
                // echoed into a plain message (2000 cap) AND stored as the ledger note
                // that /xp renders into an embed field (1024 cap), so it is bounded
                // here rather than truncated in three places later.
                .WithMinLength(1)
                .WithMaxLength(200))
            .Build();

    public static SlashCommandProperties BuildDmsCommand() =>
        new SlashCommandBuilder()
            .WithName(DmsCommandName)
            .WithDescription("Turn level-up DMs on or off for yourself")
            .AddOption("enabled", ApplicationCommandOptionType.Boolean,
                "true to receive them, false to stop", isRequired: true)
            .Build();

    // ─── Routing ─────────────────────────────────────────────────────────────

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not (CommandName or LeaderboardCommandName or SeasonCommandName
            or AdjustCommandName or DmsCommandName))
            return;

        try
        {
            if (!_config.XpEnabled)
            {
                await command.RespondAsync(
                    "The XP ladder isn't enabled on this server yet — an officer can turn it on in the bot config.",
                    ephemeral: true);
                return;
            }

            if (command.GuildId is null)
            {
                await command.RespondAsync("This command only works in the server.", ephemeral: true);
                return;
            }

            switch (command.Data.Name)
            {
                case CommandName:            await HandleCardAsync(command); break;
                case LeaderboardCommandName: await HandleLeaderboardAsync(command); break;
                case SeasonCommandName:      await HandleSeasonAsync(command); break;
                case AdjustCommandName:      await HandleAdjustAsync(command); break;
                case DmsCommandName:         await HandleDmsAsync(command); break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", command.Data.Name);
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

    // ─── Pagination buttons ──────────────────────────────────────────────────

    /// <summary>
    /// Handles the board's and the private view's buttons.
    ///
    /// A click from the PINNED BOARD opens a new private view. A click from
    /// inside a private view edits that view in place. The source is told apart
    /// by the message's Ephemeral flag, which means one set of custom ids covers
    /// both and the ids stay stateless — nothing to rehydrate after a restart, so
    /// the buttons on a board posted weeks ago still work.
    /// </summary>
    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        var id = component.Data.CustomId;
        if (string.IsNullOrEmpty(id) || !id.StartsWith(ButtonPrefix, StringComparison.Ordinal)) return;

        try
        {
            // The disabled "Page 2 / 5" label, and the inert ids the pager gives
            // Prev on the first page and Next on the last (they exist only so two
            // buttons in one message can never share a custom id). Discord
            // shouldn't deliver a click for a disabled button, but acknowledging
            // costs nothing and avoids a stuck "thinking" spinner if it ever does.
            if (id.StartsWith("xp:noop", StringComparison.Ordinal))
            {
                await component.DeferAsync();
                return;
            }

            // Clicked from inside a DM, so there is no guild context on the
            // interaction — the guild id rides in the custom id instead. This
            // MUST be handled before the guild guard below, which would
            // otherwise drop it silently.
            if (id.StartsWith("xp:dmoff:", StringComparison.Ordinal))
            {
                await HandleDmOptOutButtonAsync(component, id);
                return;
            }

            if (component.GuildId is null) return;
            if (component.Channel is not IGuildChannel guildChannel) return;
            if (guildChannel.Guild is not SocketGuild guild) return;

            var fromEphemeral = component.Message?.Flags?.HasFlag(MessageFlags.Ephemeral) == true;

            // Rendering can involve cold avatar fetches, which will blow through
            // Discord's 3-second response deadline. Acknowledge first, then work.
            //
            // The two defer calls are NOT interchangeable. DeferAsync sends
            // DeferredUpdateMessage — it acknowledges without creating anything, so
            // ModifyOriginalResponseAsync then edits the message the button is on
            // (the private view). DeferLoadingAsync sends
            // DeferredChannelMessageWithSource, which is what's needed from the
            // pinned board: it opens a fresh ephemeral "thinking" message that the
            // followup fills in, leaving the public board untouched.
            if (fromEphemeral) await component.DeferAsync();
            else await component.DeferLoadingAsync(ephemeral: true);

            int page;
            if (id == "xp:find")
            {
                page = await _board.FindPageForMemberAsync(guild, component.User.Id, CancellationToken.None);
                if (page == 0)
                {
                    await component.FollowupAsync(
                        "You're not on the board yet this season — turn up to an event and you will be. `/xp` shows your card either way.",
                        ephemeral: true);
                    return;
                }
            }
            else if (id.StartsWith("xp:page:", StringComparison.Ordinal)
                     && int.TryParse(id.AsSpan("xp:page:".Length), out var target))
            {
                page = target;
            }
            else
            {
                // Unrecognised xp: id. We already deferred, so we must answer or the
                // member is left with a "thinking" placeholder that never resolves.
                await component.FollowupAsync("That button is no longer valid — use the board again.", ephemeral: true);
                return;
            }

            var render = await _board.RenderPageAsync(guild, page, forBoard: false, CancellationToken.None);
            if (render is null)
            {
                await component.FollowupAsync("Couldn't build the leaderboard right now. Try again shortly.", ephemeral: true);
                return;
            }

            if (fromEphemeral)
                await UpdatePrivateViewAsync(component, render);
            else
                await SendPrivateViewAsync(component, render);
        }
        catch (Exception ex)
        {
            // Usually an expired interaction token (15 minutes) on a private view
            // left open, where nothing can be sent anyway. But a click on the PINNED
            // BOARD is a fresh token, so a render or DB failure there would otherwise
            // strand the member on a spinner — try to close it out, best effort.
            _logger.LogDebug(ex, "XP: button interaction {Id} failed", id);
            try { await component.FollowupAsync("Couldn't load that page — try again in a moment.", ephemeral: true); }
            catch { /* token expired, or already answered */ }
        }
    }

    private static async Task SendPrivateViewAsync(SocketMessageComponent component, XpPageRender render)
    {
        if (render.Png is null)
        {
            await component.FollowupAsync(embed: render.Embed, components: render.Components, ephemeral: true);
            return;
        }

        using var fa = new FileAttachment(new MemoryStream(render.Png), render.FileName);
        await component.FollowupWithFileAsync(fa, embed: render.Embed, components: render.Components, ephemeral: true);
    }

    private static async Task UpdatePrivateViewAsync(SocketMessageComponent component, XpPageRender render)
    {
        if (render.Png is null)
        {
            await component.ModifyOriginalResponseAsync(m =>
            {
                m.Embed = render.Embed;
                m.Components = render.Components;
            });
            return;
        }

        using var fa = new FileAttachment(new MemoryStream(render.Png), render.FileName);
        await component.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = render.Embed;
            m.Components = render.Components;
            m.Attachments = new List<FileAttachment> { fa };
        });
    }

    // ─── Level-up DM opt-out ─────────────────────────────────────────────────

    /// <summary>
    /// The "Stop these DMs" button carried on every level-up DM.
    ///
    /// Opting out from the DM itself is the only version of this that actually
    /// gets used — asking someone to go back to the server and run a command to
    /// stop an unwanted DM is how a bot gets muted instead.
    ///
    /// The button is edited away on success so the message can't be clicked
    /// twice, and the confirmation names the command to undo it.
    /// </summary>
    private async Task HandleDmOptOutButtonAsync(SocketMessageComponent component, string id)
    {
        if (!ulong.TryParse(id.AsSpan("xp:dmoff:".Length), out var guildId) || guildId == 0) return;

        // Acknowledge FIRST. The DB write below can throw (a double-click races the
        // unique index), and an unacknowledged interaction shows the member "This
        // interaction failed" while they stay opted IN — the precise outcome that
        // makes someone mute the bot instead.
        await component.DeferAsync();

        await SetDmPreferenceAsync(guildId, component.User.Id, enabled: false);

        var embed = new EmbedBuilder()
            .WithTitle("🔕 Level-up DMs off")
            .WithColor(new Color(0x8B96A8))
            .WithDescription(
                "You won't get these any more.\n\n" +
                "Your XP still counts exactly the same — check it any time with `/xp`. " +
                "To turn these back on, run `/xp-dms enabled:true` in the server.")
            .Build();

        try
        {
            // Replace the original DM so the button can't be pressed again.
            await component.ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embed;
                m.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "XP: couldn't edit the level-up DM after opt-out for {User}", component.User.Id);
        }

        _logger.LogInformation("XP: {User} opted out of level-up DMs", component.User.Id);
    }

    private async Task HandleDmsAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var enabled = command.Data.Options.FirstOrDefault(o => o.Name == "enabled")?.Value as bool? ?? true;
        var guildId = command.GuildId!.Value;

        await SetDmPreferenceAsync(guildId, command.User.Id, enabled);

        await command.FollowupAsync(
            enabled
                ? "✅ Level-up DMs are **on**. You'll get a note each time you gain a level."
                : "🔕 Level-up DMs are **off**. Your XP still counts the same — check it with `/xp`.",
            ephemeral: true);
    }

    /// <summary>
    /// Upserts the opt-out row. Absence of a row means "opted in", so turning
    /// DMs back on deletes rather than flags — the table only ever holds people
    /// who currently want silence.
    /// </summary>
    private async Task SetDmPreferenceAsync(ulong guildId, ulong userId, bool enabled)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var existing = await db.XpDmOptOuts
            .FirstOrDefaultAsync(o => o.GuildId == guildId && o.UserId == userId);

        if (enabled)
        {
            if (existing is not null) db.XpDmOptOuts.Remove(existing);
        }
        else if (existing is null)
        {
            db.XpDmOptOuts.Add(new XpDmOptOut
            {
                GuildId       = guildId,
                UserId        = userId,
                OptedOutAtUtc = DateTime.UtcNow,
            });
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // Two clicks delivered concurrently: both read no row, both insert, one
            // loses to the unique index. The member's intent is already satisfied by
            // the winner, so this is success, not an error.
            _logger.LogDebug(ex, "XP: DM preference write raced for {User}; treating as applied", userId);
        }
    }

    // ─── /xp ─────────────────────────────────────────────────────────────────

    private async Task HandleCardAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();

        var guildId = command.GuildId!.Value;
        var target = command.Data.Options.FirstOrDefault(o => o.Name == "member")?.Value as SocketUser ?? command.User;
        var displayName = (target as SocketGuildUser)?.DisplayName ?? target.GlobalName ?? target.Username;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var p = await _xp.GetProfileAsync(db, guildId, target.Id, displayName, CancellationToken.None);

        var embed = new EmbedBuilder()
            .WithTitle($"XP — {displayName}")
            .WithColor(new Color(0xF1C40F))
            .WithThumbnailUrl(target.GetAvatarUrl() ?? target.GetDefaultAvatarUrl());

        var header = new StringBuilder();

        if (p.Season is null)
        {
            header.Append("_No season is running right now — XP isn't being counted._\n\n");
            header.Append($"**All-time XP:** {p.AllTimeXp:N0}");
        }
        else
        {
            header.Append($"**Level {p.Level}** · {p.SeasonXp:N0} XP · {XpService.SeasonLabel(p.Season)}\n");

            if (p.XpForThisLevel > 0)
            {
                var remaining = p.XpForThisLevel - p.XpIntoLevel;
                header.Append($"{ProgressBar(p.XpIntoLevel, p.XpForThisLevel)}  ");
                header.Append($"`{p.XpIntoLevel:N0}/{p.XpForThisLevel:N0}` — **{remaining:N0}** to Level {p.Level + 1}");
            }
            else
            {
                header.Append("_Max level reached._");
            }
        }

        embed.WithDescription(header.ToString());

        if (p.Season is not null)
            embed.AddField("Season placing",
                p.Place > 0 ? $"**#{p.Place}** of {p.TotalRanked:N0}" : "_Unranked — earn some XP_",
                inline: true);

        embed.AddField("All-time XP", $"{p.AllTimeXp:N0}", inline: true);

        embed.AddField("Seasons",
            p.SeasonsPlayed > 0
                ? $"{p.SeasonsPlayed} played" + (p.BestSeasonPlace > 0 ? $" · best finish **#{p.BestSeasonPlace}**" : "")
                : "_First season_",
            inline: true);

        if (p.Breakdown.Count > 0)
        {
            var lines = p.Breakdown
                .Where(kv => kv.Value != 0)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{XpService.SourceLabel(kv.Key)} — **{kv.Value:N0}**");

            embed.AddField("Where it came from", string.Join('\n', lines));
        }

        embed.WithFooter("XP is recognition only — it does not affect promotions.");

        await command.FollowupAsync(embed: embed.Build());
    }

    private static string ProgressBar(int into, int width)
    {
        if (width <= 0) return new string('█', ProgressBarWidth);
        var filled = (int)Math.Round((double)into / width * ProgressBarWidth);
        filled = Math.Clamp(filled, 0, ProgressBarWidth);
        return "`" + new string('█', filled) + new string('░', ProgressBarWidth - filled) + "`";
    }

    // ─── /xp-leaderboard ─────────────────────────────────────────────────────

    private async Task HandleLeaderboardAsync(SocketSlashCommand command)
    {
        // Ephemeral: anyone can check the standings from anywhere without dumping
        // a full leaderboard into whatever channel they happened to be in.
        await command.DeferAsync(ephemeral: true);

        if (command.Channel is not IGuildChannel gc || gc.Guild is not SocketGuild guild)
        {
            await command.FollowupAsync("This command only works in the server.", ephemeral: true);
            return;
        }

        var page = command.Data.Options.FirstOrDefault(o => o.Name == "page")?.Value is long n ? (int)n : 1;

        var render = await _board.RenderPageAsync(guild, Math.Max(1, page), forBoard: false, CancellationToken.None);
        if (render is null)
        {
            await command.FollowupAsync("Couldn't build the leaderboard right now. Try again shortly.", ephemeral: true);
            return;
        }

        if (render.Png is null)
        {
            await command.FollowupAsync(embed: render.Embed, components: render.Components, ephemeral: true);
            return;
        }

        using var fa = new FileAttachment(new MemoryStream(render.Png), render.FileName);
        await command.FollowupWithFileAsync(fa, embed: render.Embed, components: render.Components, ephemeral: true);
    }

    // ─── /xp-season ──────────────────────────────────────────────────────────

    private async Task HandleSeasonAsync(SocketSlashCommand command)
    {
        var sub = command.Data.Options.FirstOrDefault();
        var subName = sub?.Name ?? "status";

        switch (subName)
        {
            case "start":  await HandleSeasonStartAsync(command, sub!); break;
            case "end":    await HandleSeasonEndAsync(command, sub!); break;
            case "cancel": await HandleSeasonCancelAsync(command, sub!); break;
            default:       await HandleSeasonStatusAsync(command); break;
        }
    }

    private async Task HandleSeasonStartAsync(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await RequireAdminAsync(command, "Starting an XP season")) return;

        var name    = sub.Options.FirstOrDefault(o => o.Name == "name")?.Value as string;
        var days    = sub.Options.FirstOrDefault(o => o.Name == "days")?.Value is long d ? (int?)d : null;
        var whenRaw = (sub.Options.FirstOrDefault(o => o.Name == "when")?.Value as string ?? "").Trim();
        var endRaw  = (sub.Options.FirstOrDefault(o => o.Name == "end_when")?.Value as string ?? "").Trim();

        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        DateTime? startUtc = null;
        if (whenRaw.Length > 0)
        {
            var parsed = await ParseWhenAsync(db, command.User.Id, whenRaw, endOfDay: false);
            if (parsed.Error is not null) { await command.FollowupAsync(parsed.Error, ephemeral: true); return; }

            if (parsed.Utc!.Value <= DateTime.UtcNow)
            {
                await command.FollowupAsync(
                    $"I read that as <t:{Unix(parsed.Utc.Value)}:F>, which has already passed. " +
                    "Give me a time in the future, or leave `when` blank to open the season now.",
                    ephemeral: true);
                return;
            }

            startUtc = parsed.Utc;
        }

        DateTime? autoEndUtc = null;
        if (endRaw.Length > 0)
        {
            // endOfDay: a bare date means the season runs THROUGH that day. "August 31"
            // closes at midnight going into September 1, not at midnight starting it.
            var parsed = await ParseWhenAsync(db, command.User.Id, endRaw, endOfDay: true);
            if (parsed.Error is not null) { await command.FollowupAsync(parsed.Error, ephemeral: true); return; }

            autoEndUtc = parsed.Utc;

            var effectiveStart = startUtc ?? DateTime.UtcNow;
            if (autoEndUtc <= effectiveStart)
            {
                await command.FollowupAsync(
                    $"I read the close as <t:{Unix(autoEndUtc.Value)}:F>, which is not after the season starts " +
                    $"(<t:{Unix(effectiveStart)}:F>). Pick a later time.",
                    ephemeral: true);
                return;
            }
        }

        var opened = await _xp.StartSeasonAsync(db, guildId, name, days, startUtc, autoEndUtc, CancellationToken.None);
        if (opened is null)
        {
            var active = await _xp.GetActiveSeasonAsync(db, guildId, CancellationToken.None);
            if (active is not null)
            {
                await command.FollowupAsync(
                    $"**{XpService.SeasonLabel(active)}** is already running. End it first with " +
                    "`/xp-season end confirm:true`, then start the next one.",
                    ephemeral: true);
                return;
            }

            var pending = await _xp.GetScheduledSeasonAsync(db, guildId, CancellationToken.None);
            await command.FollowupAsync(
                pending is null
                    ? "I could not open a season. Try again, and check the logs if it keeps failing."
                    : $"**{XpService.SeasonLabel(pending)}** is already lined up to open " +
                      $"<t:{Unix(pending.StartUtc)}:F>. Call it off with `/xp-season cancel confirm:true` " +
                      "if you want to set it up differently.",
                ephemeral: true);
            return;
        }

        await RefreshBoardAsync(guildId);

        var reply = new StringBuilder();

        if (opened.Status == XpSeasonStatus.Scheduled)
        {
            reply.Append($"🗓️ **{XpService.SeasonLabel(opened)}** is lined up to open ");
            reply.Append($"<t:{Unix(opened.StartUtc)}:F> ({XpLeaderboardService.FormatRelative(opened.StartUtc)}).\n");
            reply.Append($"_Read in {_lastZoneRead}._\n\n");
            reply.Append("Nothing accrues until then. The board will show the countdown, and the bot posts an ");
            reply.Append("announcement the moment it opens, so you do not need to be around for it.");
        }
        else
        {
            reply.Append($"✅ **{XpService.SeasonLabel(opened)}** is open. XP starts accruing from now.");
        }

        if (opened.AutoEndUtc is { } close)
        {
            reply.Append($"\n\nIt closes by itself <t:{Unix(close)}:F>: standings locked in, results card posted, ");
            reply.Append("XP stops until you start the next one.");
            if (days is not null)
                reply.Append("\n\n(I ignored `days`, since `end_when` sets the real date.)");
        }
        else if (opened.EndUtc is { } target)
        {
            reply.Append($"\n\nPlanned to run until <t:{Unix(target)}:D>, but that is display only: it will **not** ");
            reply.Append("end by itself. Set a real close with `/xp-season end when:\"...\"`.");
        }

        await command.FollowupAsync(reply.ToString(), ephemeral: true);

        _logger.LogInformation("XP: {User} created Season {Number} ({Status})",
            command.User.Id, opened.Number, opened.Status);
    }

    private async Task HandleSeasonEndAsync(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await RequireAdminAsync(command, "Ending an XP season")) return;

        var whenRaw = (sub.Options.FirstOrDefault(o => o.Name == "when")?.Value as string ?? "").Trim();
        var confirm = sub.Options.FirstOrDefault(o => o.Name == "confirm")?.Value as bool? ?? false;

        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // ── Scheduling a close rather than closing now ──
        // No confirm needed. Nothing is destroyed by setting a date, and it can be
        // moved or called off right up until it fires.
        if (whenRaw.Length > 0)
        {
            var parsed = await ParseWhenAsync(db, command.User.Id, whenRaw, endOfDay: true);
            if (parsed.Error is not null) { await command.FollowupAsync(parsed.Error, ephemeral: true); return; }

            var endUtc = parsed.Utc!.Value;
            if (endUtc <= DateTime.UtcNow)
            {
                await command.FollowupAsync(
                    $"I read that as <t:{Unix(endUtc)}:F>, which has already passed. Pick a future time, " +
                    "or run `/xp-season end confirm:true` to close it right now.",
                    ephemeral: true);
                return;
            }

            var scheduled = await _xp.ScheduleEndAsync(db, guildId, endUtc, CancellationToken.None);
            if (scheduled is null)
            {
                var any = await _xp.GetActiveSeasonAsync(db, guildId, CancellationToken.None)
                          ?? await _xp.GetScheduledSeasonAsync(db, guildId, CancellationToken.None);

                await command.FollowupAsync(
                    any is null
                        ? "There is no season to close. Start one with `/xp-season start`."
                        : $"That close time is not after **{XpService.SeasonLabel(any)}** starts " +
                          $"(<t:{Unix(any.StartUtc)}:F>). Pick a later time.",
                    ephemeral: true);
                return;
            }

            _board.InvalidateBoard(guildId);
            await RefreshBoardAsync(guildId);

            await command.FollowupAsync(
                $"🗓️ **{XpService.SeasonLabel(scheduled)}** will close <t:{Unix(endUtc)}:F> " +
                $"({XpLeaderboardService.FormatRelative(endUtc)}), read in {_lastZoneRead}. " +
                "Standings get locked in and the results card " +
                "posts on its own, so you do not need to be around. " +
                "Call it off with `/xp-season cancel confirm:true`.",
                ephemeral: true);

            _logger.LogInformation("XP: {User} scheduled Season {Number} to close at {End}",
                command.User.Id, scheduled.Number, endUtc.ToString("u"));
            return;
        }

        // ── Closing right now ──
        if (!confirm)
        {
            await command.FollowupAsync(
                "This locks in the final standings and **stops XP accruing** until you start the next season. " +
                "Lifetime totals are kept. Re-run with `confirm:true` if that is what you want, or give me a " +
                "`when:` to have it close on its own instead.",
                ephemeral: true);
            return;
        }

        // Credit everything earned since the last accrual tick BEFORE freezing the
        // standings, exactly as the scheduled close does. Without this, activity in
        // the last few minutes is not merely stale on the card, it is lost for good:
        // no XpAward row was ever written for it, and once the season is Ended
        // accrual returns at the no-season guard and the NEXT season clamps its
        // lookback to its own StartUtc. It never reaches AllTimeXp either.
        if (_client.GetGuild(guildId) is { } sweepGuild)
        {
            try { await _accrual.RunFinalSweepAsync(sweepGuild, CancellationToken.None); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "XP: final accrual pass failed before a manual close of guild {Guild}", guildId);
            }
        }

        var result = await _xp.EndSeasonAsync(db, guildId, CancellationToken.None);
        if (result is null)
        {
            var pending = await _xp.GetScheduledSeasonAsync(db, guildId, CancellationToken.None);
            await command.FollowupAsync(
                pending is null
                    ? "No season is running, so there is nothing to end."
                    : $"No season is running. **{XpService.SeasonLabel(pending)}** is lined up for " +
                      $"<t:{Unix(pending.StartUtc)}:F>. Call that off with `/xp-season cancel confirm:true`.",
                ephemeral: true);
            return;
        }

        var finished  = result.Value.Season;
        var standings = result.Value.Standings;

        await RefreshBoardAsync(guildId);

        var posted = _client.GetGuild(guildId) is { } guild
                     && await _board.PostSeasonResultsAsync(guild, finished, standings, CancellationToken.None);

        // Report what actually happened. The season is already closed and cannot be
        // reopened, so telling an officer the results were posted when no announce
        // channel resolved would send them hunting for a card that does not exist.
        await command.FollowupAsync(
            $"✅ **{XpService.SeasonLabel(finished)}** is closed, {standings.Count} member(s) placed. " +
            (posted
                ? "The results card is posted. "
                : "⚠️ I could **not** post the results card, check XpLevelUpAnnounceChannelId. ") +
            "No XP will accrue until you run `/xp-season start`.",
            ephemeral: true);

        _logger.LogInformation("XP: {User} closed Season {Number}", command.User.Id, finished.Number);
    }

    /// <summary>
    /// Calls off whichever schedule is pending.
    ///
    /// A pending START wins over a pending CLOSE when both somehow exist, because a
    /// season that has not opened yet carries its close date with it: cancelling the
    /// start removes both, and cancelling only the close would leave a season still
    /// lined up to open, which is not what anyone typing "cancel" means.
    /// </summary>
    private async Task HandleSeasonCancelAsync(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await RequireAdminAsync(command, "Cancelling an XP season schedule")) return;

        var confirm = sub.Options.FirstOrDefault(o => o.Name == "confirm")?.Value as bool? ?? false;
        if (!confirm)
        {
            await command.FollowupAsync("Re-run with `confirm:true` to call off the pending schedule.", ephemeral: true);
            return;
        }

        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        if (await _xp.CancelScheduledStartAsync(db, guildId, CancellationToken.None) is { } cancelled)
        {
            _board.InvalidateBoard(guildId);
            await RefreshBoardAsync(guildId);

            await command.FollowupAsync(
                $"🚫 **{XpService.SeasonLabel(cancelled)}** will no longer open on <t:{Unix(cancelled.StartUtc)}:F>. " +
                "Nothing was earned against it, so nothing is lost. Set it up again with `/xp-season start` " +
                "whenever you like.",
                ephemeral: true);

            _logger.LogInformation("XP: {User} cancelled scheduled Season {Number}",
                command.User.Id, cancelled.Number);
            return;
        }

        if (await _xp.ClearScheduledEndAsync(db, guildId, CancellationToken.None) is { } cleared)
        {
            _board.InvalidateBoard(guildId);
            await RefreshBoardAsync(guildId);

            await command.FollowupAsync(
                $"🚫 **{XpService.SeasonLabel(cleared)}** will no longer close on its own. It keeps running until " +
                "you end it with `/xp-season end confirm:true`." +
                (cleared.EndUtc is { } target
                    ? $" The board still shows <t:{Unix(target)}:D> as the target."
                    : ""),
                ephemeral: true);

            _logger.LogInformation("XP: {User} cleared the automatic close on Season {Number}",
                command.User.Id, cleared.Number);
            return;
        }

        await command.FollowupAsync(
            "Nothing is scheduled. No season is waiting to open, and none is set to close on its own.",
            ephemeral: true);
    }

    /// <summary>
    /// Reads an officer's typed time in THEIR timezone (the one /event timezone
    /// saved, falling back to BotConfig.EventDefaultTimeZone) and returns a UTC
    /// instant. Same parser the event wizard uses, so "August 1", "Saturday 8pm" and
    /// "in 3 days" all behave the way they already do everywhere else in the bot.
    ///
    /// <paramref name="endOfDay"/> handles the one place a bare date is ambiguous. A
    /// date on its own parses to midnight AT THE START of that day, which is right
    /// for a season opening and wrong for one closing: "end on August 31" means the
    /// 31st is the last day you can earn, so the close is midnight going into
    /// September 1. The roll forward is done in local time, not by adding 24 hours to
    /// the UTC value, so it stays midnight across a daylight-saving change.
    /// </summary>
    private async Task<(DateTime? Utc, string? Error)> ParseWhenAsync(
        BotDbContext db, ulong userId, string input, bool endOfDay)
    {
        var iana = (await db.UserTimeZones.FirstOrDefaultAsync(t => t.UserId == userId))?.IanaId;
        var tz   = _time.ResolveZone(iana);
        _lastZoneRead = iana is null ? $"{tz.Id} (server default, you have no `/timezone` set)" : tz.Id;

        var parsed = _time.ParseStart(input, tz);
        if (!parsed.Success)
            return (null, parsed.Error ?? "I could not read that time. Try \"August 1\" or \"Saturday 8pm\".");

        var utc = parsed.StartUtc;

        if (endOfDay && !parsed.HasTimeOfDay)
        {
            var nextMidnightLocal = TimeZoneInfo.ConvertTimeFromUtc(utc, tz).Date.AddDays(1);
            if (!TryLocalToUtc(nextMidnightLocal, tz, out var rolled))
                return (null, "That date lands on a daylight-saving change I cannot resolve. Give me an explicit time.");
            utc = rolled;
        }

        return (utc, null);
    }

    /// <summary>
    /// Local wall-clock to UTC, tolerating the spring-forward gap where a local time
    /// does not exist by stepping forward until one does. Midnight is a real gap in
    /// some zones (Brazil used to spring forward at midnight), and throwing an
    /// unhandled ArgumentException at an officer scheduling a season is not an
    /// acceptable way to find that out.
    /// </summary>
    private static bool TryLocalToUtc(DateTime local, TimeZoneInfo tz, out DateTime utc)
    {
        var candidate = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        for (var i = 0; i < 4; i++)
        {
            if (!tz.IsInvalidTime(candidate))
            {
                utc = TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
                return true;
            }

            candidate = candidate.AddHours(1);
        }

        utc = default;
        return false;
    }

    /// <summary>
    /// Which zone ParseWhenAsync last read a time in, echoed back to the officer.
    /// An hour-wrong season boundary is otherwise near-invisible: the confirmation
    /// renders &lt;t:...&gt; in the reader's OWN zone, so it looks plausible whether or
    /// not the input was interpreted the way they meant. Naming the zone makes a
    /// mismatch obvious. Set on every parse, read immediately after.
    /// </summary>
    private string _lastZoneRead = string.Empty;

    private static long Unix(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    private async Task HandleSeasonStatusAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();

        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var season = await _xp.GetActiveSeasonAsync(db, guildId, CancellationToken.None);
        if (season is null)
        {
            var pending = await _xp.GetScheduledSeasonAsync(db, guildId, CancellationToken.None);

            // A season lined up to open is the headline. Leading with "no season is
            // running" when one starts in six hours is technically true and useless.
            if (pending is not null)
            {
                var upcoming = new StringBuilder();
                upcoming.Append($"**{XpService.SeasonLabel(pending)}** opens <t:{Unix(pending.StartUtc)}:F> ");
                upcoming.Append($"({XpLeaderboardService.FormatRelative(pending.StartUtc)}).\n\n");
                upcoming.Append("Nothing is being counted until then. Everyone starts at zero.");

                if (pending.AutoEndUtc is { } pendingClose)
                    upcoming.Append($"\n\nIt closes by itself <t:{Unix(pendingClose)}:F>.");
                else if (pending.EndUtc is { } pendingTarget)
                    upcoming.Append($"\n\nPlanned to run until <t:{Unix(pendingTarget)}:D>, which is a target, not a deadline.");

                await command.FollowupAsync(upcoming.ToString());
                return;
            }

            var last = await db.XpSeasons
                .Where(s => s.GuildId == guildId && s.Status == XpSeasonStatus.Ended)
                .OrderByDescending(s => s.Number)
                .FirstOrDefaultAsync();

            await command.FollowupAsync(
                last is null
                    ? "No XP season has ever been run here. An officer opens one with `/xp-season start`."
                    : $"**No season is running.** The last one was {XpService.SeasonLabel(last)}, closed " +
                      $"<t:{Unix(last.ClosedUtc ?? last.StartUtc)}:R>. " +
                      "XP isn't accruing until an officer runs `/xp-season start`.");
            return;
        }

        var podium = await _xp.GetSeasonStandingsAsync(db, guildId, season.Id, 3, CancellationToken.None);
        var participants = await db.XpMemberSeasons
            .CountAsync(m => m.GuildId == guildId && m.SeasonId == season.Id && m.Xp > 0);

        var medals = new[] { "🥇", "🥈", "🥉" };
        var podiumText = podium.Count == 0
            ? "_Nobody on the board yet._"
            : string.Join('\n', podium.Select((s, i) =>
                $"{medals[Math.Min(i, medals.Length - 1)]} **{Sanitize(s.Username)}** — Lv {s.Level} · {s.Xp:N0} XP"));

        var embed = new EmbedBuilder()
            .WithTitle($"🗓️ {XpService.SeasonLabel(season)}")
            .WithColor(new Color(0xF1C40F))
            .AddField("Started", $"<t:{Unix(season.StartUtc)}:D>", inline: true)
            .AddField(season.AutoEndUtc is not null ? "Closes" : "Planned end",
                season.EndUtc is { } e ? $"<t:{Unix(e)}:R>" : "_None set_", inline: true)
            .AddField("On the board", $"{participants:N0} member(s)", inline: true)
            .AddField("Podium", podiumText)
            .WithFooter(season.AutoEndUtc is not null
                ? "This season closes on its own at the time above."
                : "This season only ends when an officer ends it. The date is a target, not a deadline.")
            .Build();

        await command.FollowupAsync(embed: embed);
    }

    // ─── /xp-adjust ──────────────────────────────────────────────────────────

    private async Task HandleAdjustAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await RequireAdjustRoleAsync(command)) return;

        var caller = (SocketGuildUser)command.User;
        var target = command.Data.Options.FirstOrDefault(o => o.Name == "member")?.Value as SocketUser;
        var amount = command.Data.Options.FirstOrDefault(o => o.Name == "amount")?.Value is long a ? (int)a : 0;
        var reason = (command.Data.Options.FirstOrDefault(o => o.Name == "reason")?.Value as string ?? "").Trim();

        if (target is null || amount == 0 || string.IsNullOrWhiteSpace(reason))
        {
            await command.FollowupAsync("Give me a member, a non-zero amount, and a reason.", ephemeral: true);
            return;
        }

        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var season = await _xp.GetActiveSeasonAsync(db, guildId, CancellationToken.None);
        if (season is null)
        {
            await command.FollowupAsync(
                "No season is running, so there's nothing to adjust. Start one with `/xp-season start` first.",
                ephemeral: true);
            return;
        }

        var displayName = (target as SocketGuildUser)?.DisplayName ?? target.GlobalName ?? target.Username;

        // A fresh key every time, so repeated grants stack instead of overwriting
        // each other — and every one stays visible in the ledger.
        var key = $"manual:{Guid.NewGuid():N}";
        var note = $"{reason} — by {caller.DisplayName}";

        await _xp.AwardAsync(db, guildId, season.Id, target.Id, XpSource.Manual,
            key, amount, note, DateTime.UtcNow, CancellationToken.None);

        // MUST flush first. RecomputeMemberAsync sums the ledger with a server-side
        // aggregate, and EF Core does not include pending Added entities in one — so
        // recomputing before saving wrote the PRE-adjustment total, told the officer
        // the wrong number, and left /xp showing a headline that contradicted its own
        // breakdown (which reads the ledger directly).
        await db.SaveChangesAsync();

        await _xp.RecomputeMemberAsync(db, guildId, season.Id, target.Id, displayName, CancellationToken.None);
        await db.SaveChangesAsync();

        await RefreshBoardAsync(guildId);

        var profile = await _xp.GetProfileAsync(db, guildId, target.Id, displayName, CancellationToken.None);

        await command.FollowupAsync(
            $"✅ {(amount > 0 ? "Granted" : "Deducted")} **{Math.Abs(amount):N0} XP** " +
            $"{(amount > 0 ? "to" : "from")} **{displayName}** — {reason}\n" +
            $"They're now on **{profile.SeasonXp:N0} XP** (Level {profile.Level}) this season.",
            ephemeral: true);

        _logger.LogInformation("XP: {Caller} adjusted {Target} by {Amount} ({Reason})",
            caller.Id, target.Id, amount, reason);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes the board immediately after a state change, so an officer who just
    /// started or ended a season isn't left explaining a stale leaderboard for the
    /// next ten minutes. Invalidates the render signature first, since a season
    /// change alters content the signature alone might not catch.
    /// </summary>
    /// <summary>
    /// Pushes an immediate board redraw after a state change, so an officer is not
    /// left looking at a stale board for ten minutes.
    ///
    /// Bounded, because this sits between the officer and their reply. A refresh can
    /// post, edit, pin and delete several messages, any of which can be rate limited,
    /// and the interaction token it is holding up expires after 15 minutes. If it
    /// overruns, abandon it: the service's own timer will redraw shortly anyway.
    /// </summary>
    private async Task RefreshBoardAsync(ulong guildId)
    {
        try
        {
            _board.InvalidateBoard(guildId);
            using var cts = new CancellationTokenSource(BoardRefreshTimeout);
            await _board.RefreshAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("XP: board refresh took longer than {Sec}s after a state change; " +
                "leaving it to the next scheduled refresh", BoardRefreshTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XP: board refresh failed after a state change");
        }
    }

    /// <summary>
    /// The gate for /xp-adjust, which is deliberately NOT the rank gate the season
    /// commands use. See BotConfig.XpAdjustRoleId for why this one command is
    /// singled out.
    ///
    /// Fails closed in both misconfigured directions: a role id naming a role that
    /// no longer exists denies everyone (and says so in the log, rather than
    /// silently letting the whole officer corps through), while an id of 0 means
    /// "not configured" and hands back to the old rank gate so an upgrade that
    /// forgets the key does not lock the command out entirely.
    /// </summary>
    private async Task<bool> RequireAdjustRoleAsync(SocketSlashCommand command)
    {
        if (command.User is not SocketGuildUser user)
        {
            await command.FollowupAsync("That command only works inside the server.", ephemeral: true);
            return false;
        }

        if (_config.XpAdjustRoleId == 0)
        {
            _logger.LogWarning("XpAdjustRoleId is not set; falling back to the XpAdminMinRank gate for /xp-adjust");
            return await RequireAdminAsync(command, "Adjusting XP");
        }

        // Matches how /ticket-panel gates: an Administrator
        // passes, because anyone holding Administrator can give themselves the role
        // in about four seconds and pretending otherwise only makes the check look
        // stronger than it is.
        if (user.GuildPermissions.Administrator) return true;
        if (user.Roles.Any(r => r.Id == _config.XpAdjustRoleId)) return true;

        if (user.Guild.GetRole(_config.XpAdjustRoleId) is null)
        {
            _logger.LogError(
                "XpAdjustRoleId {Role} does not exist in guild {Guild} — /xp-adjust is denied to everyone " +
                "except Administrators until it is corrected",
                _config.XpAdjustRoleId, user.Guild.Id);
        }

        await command.FollowupAsync(
            $"❌ Adjusting XP is restricted to <@&{_config.XpAdjustRoleId}>.",
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
        return false;
    }

    private async Task<bool> RequireAdminAsync(SocketSlashCommand command, string what)
    {
        if (command.User is SocketGuildUser user && HasAdminPermission(user)) return true;

        await command.FollowupAsync(
            $"❌ {what} is restricted to **{_config.XpAdminMinRank} and above**.",
            ephemeral: true);
        return false;
    }

    /// <summary>
    /// Same gate shape the other officer commands use: Administrator or Manage
    /// Roles always passes, otherwise the caller's highest rank role must sit at
    /// or above XpAdminMinRank in the configured RankRoles order.
    /// </summary>
    private bool HasAdminPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator || user.GuildPermissions.ManageRoles)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex = rankRoles.IndexOf(_config.XpAdminMinRank);
        if (minIndex < 0)
        {
            _logger.LogWarning(
                "XpAdminMinRank '{MinRank}' not found in RankRoles — XP admin commands will be admin-only",
                _config.XpAdminMinRank);
            return false;
        }

        var highestUserIndex = user.Roles
            .Select(r => rankRoles.IndexOf(r.Name))
            .DefaultIfEmpty(-1)
            .Max();

        return highestUserIndex >= minIndex;
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        return s.Replace("`", "'").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "(").Replace("]", ")").Trim();
    }
}
