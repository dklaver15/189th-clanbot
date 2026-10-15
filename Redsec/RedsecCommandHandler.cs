using System.Globalization;
using System.Text.RegularExpressions;
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
/// Ranked REDSEC win tracking.
///
///   /redsec-add-win          — record a win: season, date, 3–4 clan members and their engagements (HQ).
///   /redsec-remove-win       — delete a win entered by mistake (HQ).
///   /redsec-set-engagements  — set one player's engagements on a recorded win (HQ).
///   /redsec-wins             — public season card with the win leaderboard (everyone).
///
/// Members are ranked by wins, then by total engagements (kills + assists).
/// </summary>
public sealed partial class RedsecCommandHandler
{
    public const string AddCommandName = "redsec-add-win";
    public const string RemoveCommandName = "redsec-remove-win";
    public const string WinsCommandName = "redsec-wins";
    public const string SetEngagementsCommandName = "redsec-set-engagements";

    private const string SeasonOption = "season";
    private const string WinOption = "win";
    private const int MaxSeason = 99;
    private const int OperatorsShown = 12;
    private const int RecentWinsShown = 5;
    private const int RequiredClanPlayers = 3;
    private const int MaxEngagements = 500;

    private static readonly string[] PlayerOptions = { "player1", "player2", "player3", "player4" };
    private static readonly string[] EngagementOptions =
        { "player1_engagements", "player2_engagements", "player3_engagements", "player4_engagements" };
    private static readonly Color RedsecRed = new(0xFF4148);

    private readonly IServiceProvider _services;
    private readonly RedsecWinsRenderer _renderer;
    private readonly EventTimeParser _time;
    private readonly BotConfig _config;
    private readonly ILogger<RedsecCommandHandler> _logger;

    public RedsecCommandHandler(
        IServiceProvider services,
        RedsecWinsRenderer renderer,
        EventTimeParser time,
        IOptions<BotConfig> config,
        ILogger<RedsecCommandHandler> logger)
    {
        _services = services;
        _renderer = renderer;
        _time = time;
        _config = config.Value;
        _logger = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.AutocompleteExecuted += OnAutocompleteAsync;
    }

    /// <summary>HQ role, a listed user, or an Administrator. Also used by the command catalog.</summary>
    public static bool CanManage(SocketGuildUser user, BotConfig config) =>
        user.GuildPermissions.Administrator
        || config.RedsecManagerUserIds.Contains(user.Id)
        || (config.RedsecManagerRoleId != 0 && user.Roles.Any(r => r.Id == config.RedsecManagerRoleId));

    // ─── Command definitions ─────────────────────────────────────────────────

    public static SlashCommandProperties BuildAddCommand()
    {
        var builder = new SlashCommandBuilder()
            .WithName(AddCommandName)
            .WithDescription("Record a ranked REDSEC win (HQ only)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName(SeasonOption)
                .WithDescription("Battlefield season the win was earned in, e.g. 4")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(true)
                .WithMinValue(1)
                .WithMaxValue(MaxSeason));

        for (var i = 0; i < PlayerOptions.Length; i++)
        {
            var required = i < RequiredClanPlayers;
            builder.AddOption(PlayerOptions[i], ApplicationCommandOptionType.User,
                required
                    ? "A clan member on the winning squad"
                    : "The fourth squad member if they're in the clan. Leave blank for a non-clan player.",
                isRequired: required);
        }

        for (var i = 0; i < EngagementOptions.Length; i++)
        {
            builder.AddOption(EngagementsOption(EngagementOptions[i],
                $"Player {i + 1}'s engagements (kills + assists). Optional; counts as 0 if left blank.",
                required: false));
        }

        return builder
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("date")
                .WithDescription("When it happened, e.g. yesterday, 10/12 or 2026-10-12. Defaults to today.")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)
                .WithMaxLength(40))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("notes")
                .WithDescription("Optional detail shown on the card, e.g. \"14 kills, last-circle clutch\"")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)
                .WithMaxLength(100))
            .Build();
    }

    public static SlashCommandProperties BuildRemoveCommand() =>
        new SlashCommandBuilder()
            .WithName(RemoveCommandName)
            .WithDescription("Delete a ranked REDSEC win that was entered by mistake (HQ only)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName(WinOption)
                .WithDescription("The win to delete. Start typing to search recent wins, or enter its # number.")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(true)
                .WithMinValue(1)
                .WithAutocomplete(true))
            .Build();

    public static SlashCommandProperties BuildSetEngagementsCommand() =>
        new SlashCommandBuilder()
            .WithName(SetEngagementsCommandName)
            .WithDescription("Set a player's engagements (kills + assists) on a recorded REDSEC win (HQ only)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName(WinOption)
                .WithDescription("The win. Start typing to search recent wins, or enter its # number.")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(true)
                .WithMinValue(1)
                .WithAutocomplete(true))
            .AddOption("player", ApplicationCommandOptionType.User,
                "The clan member on that win", isRequired: true)
            .AddOption(EngagementsOption("engagements", "Their kills + assists in that match", required: true))
            .Build();

    private static SlashCommandOptionBuilder EngagementsOption(string name, string description, bool required) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(required)
            .WithMinValue(0)
            .WithMaxValue(MaxEngagements);

    public static SlashCommandProperties BuildWinsCommand() =>
        new SlashCommandBuilder()
            .WithName(WinsCommandName)
            .WithDescription("Show the clan's ranked REDSEC wins for a season")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName(SeasonOption)
                .WithDescription("Which season (defaults to the latest season with a win)")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(false)
                .WithMinValue(1)
                .WithMaxValue(MaxSeason)
                .WithAutocomplete(true))
            .Build();

    // ─── Routing ─────────────────────────────────────────────────────────────

    private Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not (AddCommandName or RemoveCommandName or WinsCommandName or SetEngagementsCommandName))
            return Task.CompletedTask;

        // Off the gateway task: the season card fetches avatars over HTTP.
        _ = Task.Run(async () =>
        {
            try
            {
                if (command.GuildId is null || command.User is not SocketGuildUser user)
                {
                    await command.RespondAsync("This command only works in the server.", ephemeral: true);
                    return;
                }

                switch (command.Data.Name)
                {
                    case AddCommandName:    await HandleAddAsync(command, user); break;
                    case RemoveCommandName: await HandleRemoveAsync(command, user); break;
                    case WinsCommandName:   await HandleWinsAsync(command, user.Guild); break;
                    case SetEngagementsCommandName: await HandleSetEngagementsAsync(command, user); break;
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
        });

        return Task.CompletedTask;
    }

    // ─── /redsec-add-win ─────────────────────────────────────────────────────

    private async Task HandleAddAsync(SocketSlashCommand command, SocketGuildUser caller)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await RequireManagerAsync(command, caller)) return;

        var options = command.Data.Options;
        var season = (int)(options.FirstOrDefault(o => o.Name == SeasonOption)?.Value as long? ?? 0);
        var notes = (options.FirstOrDefault(o => o.Name == "notes")?.Value as string)?.Trim();
        if (string.IsNullOrEmpty(notes)) notes = null;

        var players = new List<SocketUser>();
        var engagements = new Dictionary<ulong, int?>();
        for (var i = 0; i < PlayerOptions.Length; i++)
        {
            var given = options.FirstOrDefault(o => o.Name == EngagementOptions[i])?.Value as long?;
            if (options.FirstOrDefault(o => o.Name == PlayerOptions[i])?.Value is not SocketUser picked)
            {
                if (given is not null)
                {
                    await command.FollowupAsync(
                        $"❌ You entered engagements for player {i + 1} but didn't pick player {i + 1}.",
                        ephemeral: true);
                    return;
                }
                continue;
            }

            if (picked.IsBot)
            {
                await command.FollowupAsync($"❌ {picked.Mention} is a bot, not a squadmate.",
                    ephemeral: true, allowedMentions: AllowedMentions.None);
                return;
            }
            if (players.Any(p => p.Id == picked.Id))
            {
                await command.FollowupAsync($"❌ {picked.Mention} was picked more than once.",
                    ephemeral: true, allowedMentions: AllowedMentions.None);
                return;
            }
            players.Add(picked);
            engagements[picked.Id] = given is long g ? (int)g : null;
        }

        if (season < 1 || players.Count < RequiredClanPlayers)
        {
            await command.FollowupAsync(
                $"Give me a season and at least {RequiredClanPlayers} clan members.", ephemeral: true);
            return;
        }

        var tz = _time.ResolveZone(null);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        var dateInput = options.FirstOrDefault(o => o.Name == "date")?.Value as string;
        if (!TryParseWinDate(dateInput, today, out var wonOn, out var dateError))
        {
            await command.FollowupAsync($"❌ {dateError}", ephemeral: true);
            return;
        }

        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var sameDayIds = await db.RedsecWins
            .Where(w => w.GuildId == guildId && w.Season == season && w.WonOn == wonOn)
            .Select(w => w.Id)
            .ToListAsync();
        var newSquad = players.Select(p => p.Id).ToHashSet();
        var sameSquadSameDay = sameDayIds.Count == 0 ? 0 : (await db.RedsecWinPlayers
                .Where(p => sameDayIds.Contains(p.RedsecWinId))
                .ToListAsync())
            .GroupBy(p => p.RedsecWinId)
            .Count(g => g.Select(p => p.UserId).ToHashSet().SetEquals(newSquad));

        var win = new RedsecWin
        {
            GuildId = guildId,
            Season = season,
            WonOn = wonOn,
            Notes = notes,
            RecordedByUserId = caller.Id,
            RecordedUtc = DateTime.UtcNow,
        };

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.RedsecWins.Add(win);
            await db.SaveChangesAsync();

            db.RedsecWinPlayers.AddRange(players.Select(p => new RedsecWinPlayer
            {
                RedsecWinId = win.Id,
                UserId = p.Id,
                DisplayName = DisplayNameOf(p),
                Engagements = engagements[p.Id],
            }));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        var seasonTotal = await db.RedsecWins.CountAsync(w => w.GuildId == guildId && w.Season == season);

        _logger.LogInformation(
            "REDSEC win #{Id} recorded by {Caller} ({CallerId}): season {Season}, {Date}, players {Players}",
            win.Id, caller.Username, caller.Id, season, wonOn, string.Join(",", newSquad));

        var randoms = RedsecWinsRenderer.SquadSize - players.Count;
        var squad = string.Join("\n", players.Select(p => $"• {p.Mention}{EngagementsSuffix(engagements[p.Id])}"));
        if (randoms > 0) squad += $"\n• +{randoms} non-clan player{(randoms == 1 ? "" : "s")}";

        var embed = new EmbedBuilder()
            .WithColor(RedsecRed)
            .WithTitle($"🏆 Win #{win.Id} recorded")
            .WithDescription($"**Season {season}** · {FormatDate(wonOn)}")
            .AddField("Squad", squad)
            .WithFooter($"Season {season} total: {seasonTotal} win{(seasonTotal == 1 ? "" : "s")} · " +
                        $"Undo with /{RemoveCommandName} win:{win.Id}");
        if (notes is not null) embed.AddField("Notes", notes);
        if (sameSquadSameDay > 0)
        {
            embed.AddField("⚠️ Possible duplicate",
                $"This squad already had {sameSquadSameDay} win{(sameSquadSameDay == 1 ? "" : "s")} recorded " +
                $"on that date. If this one was a mistake, run `/{RemoveCommandName} win:{win.Id}`.");
        }

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    // ─── /redsec-remove-win ──────────────────────────────────────────────────

    private async Task HandleRemoveAsync(SocketSlashCommand command, SocketGuildUser caller)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await RequireManagerAsync(command, caller)) return;

        var id = (int)(command.Data.Options.FirstOrDefault(o => o.Name == WinOption)?.Value as long? ?? 0);
        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var win = await db.RedsecWins.FirstOrDefaultAsync(w => w.Id == id && w.GuildId == guildId);
        if (win is null)
        {
            await command.FollowupAsync($"❌ There's no win #{id}. Start typing in the `win` option to search.",
                ephemeral: true);
            return;
        }

        var players = await db.RedsecWinPlayers.Where(p => p.RedsecWinId == win.Id).ToListAsync();
        db.RedsecWinPlayers.RemoveRange(players);
        db.RedsecWins.Remove(win);
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "REDSEC win #{Id} removed by {Caller} ({CallerId}): season {Season}, {Date}, players {Players}",
            win.Id, caller.Username, caller.Id, win.Season, win.WonOn,
            string.Join(",", players.Select(p => p.UserId)));

        var squad = players.Count == 0 ? "—" : string.Join(", ", players.Select(p => $"<@{p.UserId}>"));
        await command.FollowupAsync(
            $"🗑️ Deleted win #{win.Id}: Season {win.Season}, {FormatDate(win.WonOn)}, {squad}.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    // ─── /redsec-set-engagements ─────────────────────────────────────────────

    private async Task HandleSetEngagementsAsync(SocketSlashCommand command, SocketGuildUser caller)
    {
        await command.DeferAsync(ephemeral: true);
        if (!await RequireManagerAsync(command, caller)) return;

        var options = command.Data.Options;
        var id = (int)(options.FirstOrDefault(o => o.Name == WinOption)?.Value as long? ?? 0);
        var target = options.FirstOrDefault(o => o.Name == "player")?.Value as SocketUser;
        var value = options.FirstOrDefault(o => o.Name == "engagements")?.Value as long?;
        if (target is null || value is null)
        {
            await command.FollowupAsync("Give me a win, a player and their engagements.", ephemeral: true);
            return;
        }

        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var win = await db.RedsecWins.FirstOrDefaultAsync(w => w.Id == id && w.GuildId == guildId);
        if (win is null)
        {
            await command.FollowupAsync($"❌ There's no win #{id}. Start typing in the `win` option to search.",
                ephemeral: true);
            return;
        }

        var squad = await db.RedsecWinPlayers.Where(p => p.RedsecWinId == win.Id).ToListAsync();
        var row = squad.FirstOrDefault(p => p.UserId == target.Id);
        if (row is null)
        {
            var onWin = string.Join(", ", squad.Select(p => $"<@{p.UserId}>"));
            await command.FollowupAsync($"❌ {target.Mention} isn't on win #{win.Id}. That squad was {onWin}.",
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        var previous = row.Engagements;
        row.Engagements = (int)value.Value;
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "REDSEC win #{Id}: {Caller} ({CallerId}) set engagements for {Player} from {Old} to {New}",
            win.Id, caller.Username, caller.Id, target.Id, previous, row.Engagements);

        var was = previous is int p ? $"was {p}" : "not entered before";
        await command.FollowupAsync(
            $"✅ {target.Mention} now has {row.Engagements} engagement{(row.Engagements == 1 ? "" : "s")} " +
            $"on win #{win.Id} (Season {win.Season}, {FormatDate(win.WonOn)}); {was}.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    // ─── /redsec-wins ────────────────────────────────────────────────────────

    private async Task HandleWinsAsync(SocketSlashCommand command, SocketGuild guild)
    {
        var requested = command.Data.Options.FirstOrDefault(o => o.Name == SeasonOption)?.Value as long?;

        // Acknowledge before any I/O. The card itself is posted as a channel message below.
        await command.DeferAsync(ephemeral: true);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var seasonCounts = await db.RedsecWins
            .Where(w => w.GuildId == guild.Id)
            .GroupBy(w => w.Season)
            .Select(g => new { Season = g.Key, Wins = g.Count() })
            .OrderByDescending(s => s.Season)
            .ToListAsync();

        if (seasonCounts.Count == 0)
        {
            await command.FollowupAsync("No ranked REDSEC wins have been recorded yet.", ephemeral: true);
            return;
        }

        var season = requested is long r ? (int)r : seasonCounts[0].Season;
        if (seasonCounts.All(s => s.Season != season))
        {
            var known = string.Join(", ", seasonCounts.Select(s => $"Season {s.Season}"));
            await command.FollowupAsync($"No wins recorded for Season {season}. Seasons with wins: {known}.",
                ephemeral: true);
            return;
        }

        var wins = await db.RedsecWins
            .Where(w => w.GuildId == guild.Id && w.Season == season)
            .OrderByDescending(w => w.WonOn).ThenByDescending(w => w.Id)
            .ToListAsync();
        var winIds = wins.Select(w => w.Id).ToList();
        var playersByWin = (await db.RedsecWinPlayers
                .Where(p => winIds.Contains(p.RedsecWinId))
                .OrderBy(p => p.Id)
                .ToListAsync())
            .ToLookup(p => p.RedsecWinId);

        // Newest snapshot first, so a departed member shows their last known name.
        var snapshotNames = new Dictionary<ulong, string>();
        foreach (var w in wins)
            foreach (var p in playersByWin[w.Id])
                snapshotNames.TryAdd(p.UserId, p.DisplayName);

        RedsecSquadMember Member(ulong userId)
        {
            var live = guild.GetUser(userId);
            return new RedsecSquadMember(
                userId,
                live?.DisplayName ?? snapshotNames.GetValueOrDefault(userId) ?? userId.ToString(),
                live?.GetDisplayAvatarUrl(ImageFormat.Png, 128));
        }

        var operators = RankOperators(wins.SelectMany(w => playersByWin[w.Id]), Member);

        var showEngagements = wins.Any(w => playersByWin[w.Id].Any(p => p.Engagements is not null));

        var recent = wins.Take(RecentWinsShown)
            .Select(w => new RedsecWinLine(w.Id, w.WonOn,
                playersByWin[w.Id].Select(p => Member(p.UserId)).ToList(), w.Notes,
                playersByWin[w.Id].Any(p => p.Engagements is not null)
                    ? playersByWin[w.Id].Sum(p => p.Engagements ?? 0)
                    : null))
            .ToList();

        var card = new RedsecSeasonCard(
            season,
            wins.Count,
            wins.Count(w => playersByWin[w.Id].Count() >= RedsecWinsRenderer.SquadSize),
            operators.Count,
            wins[0].WonOn,
            operators.Take(OperatorsShown).ToList(),
            recent,
            showEngagements);

        var png = await _renderer.TryRenderSeasonAsync(card, CancellationToken.None);

        var embed = new EmbedBuilder()
            .WithColor(RedsecRed)
            .WithTitle($"🏆 Ranked REDSEC · Season {season}")
            .WithDescription(
                $"**{wins.Count}** win{(wins.Count == 1 ? "" : "s")} by **{operators.Count}** " +
                $"operator{(operators.Count == 1 ? "" : "s")}. Last win {FormatDate(wins[0].WonOn)}.");

        if (seasonCounts.Count > 1)
        {
            embed.AddField("All seasons",
                string.Join(" · ", seasonCounts.Select(s => $"S{s.Season}: {s.Wins}")));
        }

        var footer = $"Requested by {DisplayNameOf(command.User)}";
        if (png is not null && operators.Count > OperatorsShown)
            footer += $" · card shows the top {OperatorsShown} of {operators.Count} operators";
        embed.WithFooter(footer);

        var fileName = $"redsec-season-{season}.png";
        if (png is null)
        {
            embed.AddField("Top operators", string.Join("\n",
                operators.Take(10).Select(o => $"**#{o.Place}** <@{o.Member.UserId}> — {o.Wins} " +
                    $"win{(o.Wins == 1 ? "" : "s")}{(showEngagements ? $" · {o.Engagements} eng" : "")}")));
            embed.AddField("Recent wins", string.Join("\n", recent.Select(FormatWinLine)));
        }
        else
        {
            embed.WithImageUrl($"attachment://{fileName}");
        }

        await PostCardAsync(command, embed.Build(), png, fileName, season);
    }

    /// <summary>
    /// Posts the card as a normal channel message, the way the XP board is posted.
    /// The iPad client cropped the same image to 400pt tall when it was sent as the
    /// interaction's own reply. Falls back to a private reply if the bot can't post here.
    /// </summary>
    private async Task PostCardAsync(SocketSlashCommand command, Embed embed, byte[]? png, string fileName, int season)
    {
        var posted = false;
        try
        {
            var channel = await command.GetChannelAsync();
            if (channel is not null)
            {
                if (png is null)
                {
                    await channel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
                }
                else
                {
                    using var fa = new FileAttachment(new MemoryStream(png), fileName);
                    await channel.SendFileAsync(fa, embed: embed, allowedMentions: AllowedMentions.None);
                }
                posted = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't post the REDSEC card in channel {Channel}", command.ChannelId);
        }

        if (posted)
        {
            await command.FollowupAsync($"Posted the Season {season} card.", ephemeral: true);
            return;
        }

        const string note = "I couldn't post in this channel, so here's a private copy.";
        if (png is null)
        {
            await command.FollowupAsync(note, embed: embed, ephemeral: true, allowedMentions: AllowedMentions.None);
        }
        else
        {
            using var fa = new FileAttachment(new MemoryStream(png), fileName);
            await command.FollowupWithFileAsync(fa, note, embed: embed, ephemeral: true,
                allowedMentions: AllowedMentions.None);
        }
    }

    // ─── Autocomplete ────────────────────────────────────────────────────────

    private async Task OnAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        try
        {
            var name = interaction.Data.CommandName;
            if (name is not (RemoveCommandName or WinsCommandName or SetEngagementsCommandName)) return;

            if (interaction.GuildId is not ulong guildId || interaction.User is not SocketGuildUser user)
            {
                await interaction.RespondAsync(Array.Empty<AutocompleteResult>());
                return;
            }

            var typed = interaction.Data.Current?.Value?.ToString()?.Trim() ?? string.Empty;

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            if (name == WinsCommandName && interaction.Data.Current?.Name == SeasonOption)
            {
                var seasons = await db.RedsecWins
                    .Where(w => w.GuildId == guildId)
                    .GroupBy(w => w.Season)
                    .Select(g => new { Season = g.Key, Wins = g.Count() })
                    .OrderByDescending(s => s.Season)
                    .Take(25)
                    .ToListAsync();

                await interaction.RespondAsync(seasons
                    .Where(s => typed.Length == 0 || s.Season.ToString().StartsWith(typed, StringComparison.Ordinal))
                    .Select(s => new AutocompleteResult(
                        $"Season {s.Season} — {s.Wins} win{(s.Wins == 1 ? "" : "s")}", (long)s.Season)));
                return;
            }

            if (name is (RemoveCommandName or SetEngagementsCommandName) && interaction.Data.Current?.Name == WinOption)
            {
                if (!CanManage(user, _config))
                {
                    await interaction.RespondAsync(Array.Empty<AutocompleteResult>());
                    return;
                }

                var wins = (await db.RedsecWins
                        .Where(w => w.GuildId == guildId)
                        .OrderByDescending(w => w.Id)
                        .Take(500)
                        .ToListAsync())
                    .Where(w => typed.Length == 0 || w.Id.ToString().StartsWith(typed, StringComparison.Ordinal))
                    .Take(25)
                    .ToList();
                var ids = wins.Select(w => w.Id).ToList();
                var players = (await db.RedsecWinPlayers
                        .Where(p => ids.Contains(p.RedsecWinId))
                        .OrderBy(p => p.Id)
                        .ToListAsync())
                    .ToLookup(p => p.RedsecWinId);

                await interaction.RespondAsync(wins.Select(w =>
                {
                    var squad = players[w.Id].ToList();
                    var label = $"#{w.Id} · S{w.Season} · {w.WonOn.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} · " +
                                string.Join(", ", squad.Select(p => p.DisplayName));
                    var randoms = RedsecWinsRenderer.SquadSize - squad.Count;
                    if (randoms > 0) label += $" +{randoms}";
                    return new AutocompleteResult(Truncate(label, 100), (long)w.Id);
                }));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "REDSEC autocomplete failed");
            try { await interaction.RespondAsync(Array.Empty<AutocompleteResult>()); }
            catch { /* expired */ }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<bool> RequireManagerAsync(SocketSlashCommand command, SocketGuildUser user)
    {
        if (CanManage(user, _config)) return true;

        if (_config.RedsecManagerRoleId != 0 && user.Guild.GetRole(_config.RedsecManagerRoleId) is null)
        {
            _logger.LogError(
                "RedsecManagerRoleId {Role} does not exist in guild {Guild}; only Administrators and " +
                "RedsecManagerUserIds can manage REDSEC wins", _config.RedsecManagerRoleId, user.Guild.Id);
        }

        var who = _config.RedsecManagerRoleId != 0 ? $"<@&{_config.RedsecManagerRoleId}>" : "HQ";
        await command.FollowupAsync($"❌ Recording REDSEC wins is restricted to {who}.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
        return false;
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = max - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }

    private static string DisplayNameOf(SocketUser user) =>
        (user as SocketGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username;

    private static string FormatDate(DateOnly date) =>
        date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    private static string FormatWinLine(RedsecWinLine w)
    {
        var line = $"`#{w.Id}` {w.WonOn.ToString("MMM d", CultureInfo.InvariantCulture)} — " +
                   string.Join(", ", w.Players.Select(p => $"<@{p.UserId}>"));
        var randoms = RedsecWinsRenderer.SquadSize - w.Players.Count;
        if (randoms > 0) line += $" +{randoms}";
        return w.SquadEngagements is int e ? $"{line} · {e} eng" : line;
    }

    /// <summary>
    /// Ranks by wins, then total engagements (missing counts as 0). Members tied on
    /// both share a place; name only orders them within it.
    /// </summary>
    internal static List<RedsecOperatorLine> RankOperators(
        IEnumerable<RedsecWinPlayer> rows, Func<ulong, RedsecSquadMember> member)
    {
        var tallies = rows
            .GroupBy(p => p.UserId)
            .Select(g => (Member: member(g.Key), Wins: g.Count(), Engagements: g.Sum(p => p.Engagements ?? 0)))
            .OrderByDescending(t => t.Wins)
            .ThenByDescending(t => t.Engagements)
            .ThenBy(t => t.Member.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var operators = new List<RedsecOperatorLine>();
        for (var i = 0; i < tallies.Count; i++)
        {
            var tied = i > 0
                       && tallies[i].Wins == tallies[i - 1].Wins
                       && tallies[i].Engagements == tallies[i - 1].Engagements;
            var place = tied ? operators[i - 1].Place : i + 1;
            operators.Add(new RedsecOperatorLine(place, tallies[i].Member, tallies[i].Wins, tallies[i].Engagements));
        }
        return operators;
    }

    private static string EngagementsSuffix(int? engagements) =>
        engagements is int e ? $" — {e} engagement{(e == 1 ? "" : "s")}" : string.Empty;

    /// <summary>
    /// Parses a past date in the clan's zone. Blank means today. A date without a
    /// year is its most recent occurrence, so "12/30" typed in January is last year.
    /// </summary>
    internal static bool TryParseWinDate(string? input, DateOnly today, out DateOnly date, out string? error)
    {
        error = null;
        date = today;

        var text = input?.Trim().ToLowerInvariant() ?? string.Empty;
        if (text.Length == 0 || text == "today") return true;
        if (text == "yesterday") { date = today.AddDays(-1); return true; }

        text = OrdinalSuffix().Replace(text.Replace(",", " "), "$1");
        text = Whitespace().Replace(text, " ").Trim();

        string[] withYear = { "yyyy-M-d", "M/d/yyyy", "M/d/yy", "M-d-yyyy", "MMM d yyyy", "MMMM d yyyy", "d MMM yyyy", "d MMMM yyyy" };
        string[] withoutYear = { "M/d", "M-d", "MMM d", "MMMM d", "d MMM", "d MMMM" };
        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces;
        var culture = CultureInfo.InvariantCulture;

        if (DateTime.TryParseExact(text, withYear, culture, styles, out var full))
        {
            date = DateOnly.FromDateTime(full);
        }
        else if (DateTime.TryParseExact(text, withoutYear, culture, styles, out var partial))
        {
            date = new DateOnly(today.Year, partial.Month, partial.Day);
            if (date > today) date = date.AddYears(-1);
        }
        else
        {
            error = $"I couldn't read \"{input}\" as a date. Try \"yesterday\", \"10/12\" or \"2026-10-12\".";
            return false;
        }

        if (date > today)
        {
            error = $"{FormatDate(date)} hasn't happened yet.";
            return false;
        }

        return true;
    }

    [GeneratedRegex(@"\b(\d{1,2})(st|nd|rd|th)\b")]
    private static partial Regex OrdinalSuffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
