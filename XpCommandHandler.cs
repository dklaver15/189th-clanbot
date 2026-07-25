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

    /// <summary>Prefix for every button this handler owns. Keeps the filter cheap.</summary>
    private const string ButtonPrefix = "xp:";

    private const int ProgressBarWidth = 12;

    private readonly IServiceProvider _services;
    private readonly XpService _xp;
    private readonly XpLeaderboardService _board;
    private readonly XpLeaderboardRenderer _renderer;
    private readonly BotConfig _config;
    private readonly ILogger<XpCommandHandler> _logger;

    public XpCommandHandler(
        IServiceProvider services,
        XpService xp,
        XpLeaderboardService board,
        XpLeaderboardRenderer renderer,
        IOptions<BotConfig> config,
        ILogger<XpCommandHandler> logger)
    {
        _services = services;
        _xp = xp;
        _board = board;
        _renderer = renderer;
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
                .WithDescription($"Open a new XP season — XP only accrues while one is running ({minRank}+ only)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("name", ApplicationCommandOptionType.String,
                    "Optional season name, e.g. \"Launch\"", isRequired: false)
                .AddOption("days", ApplicationCommandOptionType.Integer,
                    "Planned length in days — display only, it will NOT end by itself", isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("end")
                .WithDescription($"Close the current season and post the results ({minRank}+ only)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("confirm", ApplicationCommandOptionType.Boolean,
                    "Must be true — this locks in the standings and stops XP accruing", isRequired: true))
            .Build();

    public static SlashCommandProperties BuildAdjustCommand(string minRank) =>
        new SlashCommandBuilder()
            .WithName(AdjustCommandName)
            .WithDescription($"Grant or deduct XP manually ({minRank}+ only)")
            .AddOption("member", ApplicationCommandOptionType.User,
                "The member to adjust", isRequired: true)
            .AddOption("amount", ApplicationCommandOptionType.Integer,
                "XP to add (negative to deduct)", isRequired: true)
            .AddOption("reason", ApplicationCommandOptionType.String,
                "Why — shown in the ledger and on their /xp card", isRequired: true)
            .Build();

    // ─── Routing ─────────────────────────────────────────────────────────────

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not (CommandName or LeaderboardCommandName or SeasonCommandName or AdjustCommandName))
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
            // The disabled "Page 2 / 5" label. Discord shouldn't deliver a click
            // for a disabled button, but acknowledging costs nothing and avoids a
            // stuck "thinking" spinner if it ever does.
            if (id == "xp:noop")
            {
                await component.DeferAsync();
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
            // Most likely cause is an expired interaction token (15 minutes) on a
            // private view someone left open. Nothing useful to say to them — the
            // client shows "This interaction failed", and clicking the board again
            // works fine.
            _logger.LogDebug(ex, "XP: button interaction {Id} failed", id);
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
            case "start": await HandleSeasonStartAsync(command, sub!); break;
            case "end":   await HandleSeasonEndAsync(command, sub!); break;
            default:      await HandleSeasonStatusAsync(command); break;
        }
    }

    private async Task HandleSeasonStartAsync(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await RequireAdminAsync(command, "Starting an XP season")) return;

        var name = sub.Options.FirstOrDefault(o => o.Name == "name")?.Value as string;
        var days = sub.Options.FirstOrDefault(o => o.Name == "days")?.Value is long d ? (int?)d : null;

        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var opened = await _xp.StartSeasonAsync(db, guildId, name, days, CancellationToken.None);
        if (opened is null)
        {
            var active = await _xp.GetActiveSeasonAsync(db, guildId, CancellationToken.None);
            await command.FollowupAsync(
                $"**{XpService.SeasonLabel(active)}** is already running. End it first with `/xp-season end confirm:true`, " +
                "then start the next one.",
                ephemeral: true);
            return;
        }

        await RefreshBoardAsync(guildId);

        var plannedEnd = opened.EndUtc is not null
            ? $" It's set to run about **{days ?? _config.XpSeasonLengthDays} days** — that's display only; it will **not** end by itself, run `/xp-season end` when you're ready."
            : " No planned end date is set.";

        await command.FollowupAsync(
            $"✅ **{XpService.SeasonLabel(opened)}** is open — XP starts accruing from now.{plannedEnd}",
            ephemeral: true);

        _logger.LogInformation("XP: {User} opened Season {Number}", command.User.Id, opened.Number);
    }

    private async Task HandleSeasonEndAsync(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await RequireAdminAsync(command, "Ending an XP season")) return;

        var confirm = sub.Options.FirstOrDefault(o => o.Name == "confirm")?.Value as bool? ?? false;
        if (!confirm)
        {
            await command.FollowupAsync(
                "This locks in the final standings and **stops XP accruing** until you start the next season. " +
                "Lifetime totals are kept. Re-run with `confirm:true` if that's what you want.",
                ephemeral: true);
            return;
        }

        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var result = await _xp.EndSeasonAsync(db, guildId, CancellationToken.None);
        if (result is null)
        {
            await command.FollowupAsync("No season is running, so there's nothing to end.", ephemeral: true);
            return;
        }

        var finished = result.Value.Season;
        var standings = result.Value.Standings;

        await RefreshBoardAsync(guildId);
        await PostSeasonResultsAsync(command, finished, standings);

        await command.FollowupAsync(
            $"✅ **{XpService.SeasonLabel(finished)}** is closed — {standings.Count} member(s) placed, and the results are posted. " +
            "No XP will accrue until you run `/xp-season start`.",
            ephemeral: true);

        _logger.LogInformation("XP: {User} closed Season {Number}", command.User.Id, finished.Number);
    }

    /// <summary>
    /// Posts the end-of-season results card to the announce channel (falling back
    /// to the board channel). This is the permanent artefact of a season — the
    /// numbers behind it reset, so if this never gets posted the season may as
    /// well not have happened.
    /// </summary>
    private async Task PostSeasonResultsAsync(
        SocketSlashCommand command, XpSeason finished, IReadOnlyList<XpStanding> standings)
    {
        var channelId = _config.XpLevelUpAnnounceChannelId != 0
            ? _config.XpLevelUpAnnounceChannelId
            : _config.XpBoardChannelId;

        if (channelId == 0) return;
        if (command.Channel is not IGuildChannel gc || gc.Guild is not SocketGuild guild) return;
        if (guild.GetChannel(channelId) is not SocketTextChannel channel) return;

        var podium = standings.Take(3)
            .Select(s => new XpBoardEntry(
                s.Place, s.UserId, s.Username, s.Level, s.Xp,
                guild.GetUser(s.UserId)?.GetDisplayAvatarUrl(ImageFormat.Png, 128)))
            .ToList();

        var medals = new[] { "🥇", "🥈", "🥉" };
        var lines = podium.Count == 0
            ? "_Nobody placed — the season closed with no XP earned._"
            : string.Join('\n', podium.Select((s, i) =>
                $"{medals[Math.Min(i, medals.Length - 1)]} <@{s.UserId}> — **{s.Xp:N0} XP** (Level {s.Level})"));

        var png = podium.Count > 0
            ? await _renderer.TryRenderSeasonResultsAsync(
                XpService.SeasonLabel(finished), podium, standings.Count, CancellationToken.None)
            : null;

        var embed = new EmbedBuilder()
            .WithTitle($"🏁 {XpService.SeasonLabel(finished)} — final standings")
            .WithColor(new Color(0xF1C40F))
            .WithDescription(
                $"{lines}\n\n{standings.Count:N0} member(s) placed. Season XP resets to zero for the next season — " +
                "lifetime totals and every past result are kept. Check yours with `/xp`.")
            .WithCurrentTimestamp();

        if (png is not null) embed.WithImageUrl("attachment://xp-season-results.png");

        try
        {
            if (png is null)
            {
                await channel.SendMessageAsync(embed: embed.Build(), allowedMentions: AllowedMentions.None);
            }
            else
            {
                using var fa = new FileAttachment(new MemoryStream(png), "xp-season-results.png");
                await channel.SendFileAsync(fa, embed: embed.Build(), allowedMentions: AllowedMentions.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XP: failed to post season results");
        }
    }

    private async Task HandleSeasonStatusAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();

        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var season = await _xp.GetActiveSeasonAsync(db, guildId, CancellationToken.None);
        if (season is null)
        {
            var last = await db.XpSeasons
                .Where(s => s.GuildId == guildId)
                .OrderByDescending(s => s.Number)
                .FirstOrDefaultAsync();

            await command.FollowupAsync(
                last is null
                    ? "No XP season has ever been run here. An officer opens one with `/xp-season start`."
                    : $"**No season is running.** The last one was {XpService.SeasonLabel(last)}, closed " +
                      $"<t:{new DateTimeOffset(last.ClosedUtc ?? last.StartUtc, TimeSpan.Zero).ToUnixTimeSeconds()}:R>. " +
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
            .AddField("Started", $"<t:{new DateTimeOffset(season.StartUtc, TimeSpan.Zero).ToUnixTimeSeconds()}:D>", inline: true)
            .AddField("Planned end", season.EndUtc is { } e
                ? $"<t:{new DateTimeOffset(e, TimeSpan.Zero).ToUnixTimeSeconds()}:R>"
                : "_None set_", inline: true)
            .AddField("On the board", $"{participants:N0} member(s)", inline: true)
            .AddField("Podium", podiumText)
            .WithFooter("Seasons are ended by hand — the planned end is a target, not a deadline.")
            .Build();

        await command.FollowupAsync(embed: embed);
    }

    // ─── /xp-adjust ──────────────────────────────────────────────────────────

    private async Task HandleAdjustAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await RequireAdminAsync(command, "Adjusting XP")) return;

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
    private async Task RefreshBoardAsync(ulong guildId)
    {
        try
        {
            _board.InvalidateBoard(guildId);
            await _board.RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "XP: board refresh failed after a state change");
        }
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
