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
///   /redsec-add-win     — record a win: season, date and 1–4 clan members (HQ).
///   /redsec-remove-win  — delete a win entered by mistake (HQ).
///   /redsec-wins        — public season card with the win leaderboard (everyone).
/// </summary>
public sealed partial class RedsecCommandHandler
{
    public const string AddCommandName = "redsec-add-win";
    public const string RemoveCommandName = "redsec-remove-win";
    public const string WinsCommandName = "redsec-wins";

    private const string SeasonOption = "season";
    private const string WinOption = "win";
    private const int MaxSeason = 99;
    private const int OperatorsShown = 12;
    private const int RecentWinsShown = 5;

    private static readonly string[] PlayerOptions = { "player1", "player2", "player3", "player4" };
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
                .WithMaxValue(MaxSeason))
            .AddOption(PlayerOptions[0], ApplicationCommandOptionType.User,
                "A clan member on the winning squad", isRequired: true);

        for (var i = 1; i < PlayerOptions.Length; i++)
        {
            builder.AddOption(PlayerOptions[i], ApplicationCommandOptionType.User,
                "Another clan member on the squad. Leave blank for a non-clan player.", isRequired: false);
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
        if (command.Data.Name is not (AddCommandName or RemoveCommandName or WinsCommandName))
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
        foreach (var name in PlayerOptions)
        {
            if (options.FirstOrDefault(o => o.Name == name)?.Value is not SocketUser picked) continue;

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
        }

        if (season < 1 || players.Count == 0)
        {
            await command.FollowupAsync("Give me a season and at least one clan member.", ephemeral: true);
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
            }));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        var seasonTotal = await db.RedsecWins.CountAsync(w => w.GuildId == guildId && w.Season == season);

        _logger.LogInformation(
            "REDSEC win #{Id} recorded by {Caller} ({CallerId}): season {Season}, {Date}, players {Players}",
            win.Id, caller.Username, caller.Id, season, wonOn, string.Join(",", newSquad));

        var randoms = RedsecWinsRenderer.SquadSize - players.Count;
        var squad = string.Join("\n", players.Select(p => $"• {p.Mention}"));
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

    // ─── /redsec-wins ────────────────────────────────────────────────────────

    private async Task HandleWinsAsync(SocketSlashCommand command, SocketGuild guild)
    {
        var requested = command.Data.Options.FirstOrDefault(o => o.Name == SeasonOption)?.Value as long?;

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
            await command.RespondAsync("No ranked REDSEC wins have been recorded yet.", ephemeral: true);
            return;
        }

        var season = requested is long r ? (int)r : seasonCounts[0].Season;
        if (seasonCounts.All(s => s.Season != season))
        {
            var known = string.Join(", ", seasonCounts.Select(s => $"Season {s.Season}"));
            await command.RespondAsync($"No wins recorded for Season {season}. Seasons with wins: {known}.",
                ephemeral: true);
            return;
        }

        await command.DeferAsync();

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

        var tallies = wins
            .SelectMany(w => playersByWin[w.Id])
            .GroupBy(p => p.UserId)
            .Select(g => (Member: Member(g.Key), Wins: g.Count()))
            .OrderByDescending(t => t.Wins)
            .ThenBy(t => t.Member.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var operators = new List<RedsecOperatorLine>();
        for (var i = 0; i < tallies.Count; i++)
        {
            var place = i > 0 && tallies[i].Wins == tallies[i - 1].Wins ? operators[i - 1].Place : i + 1;
            operators.Add(new RedsecOperatorLine(place, tallies[i].Member, tallies[i].Wins));
        }

        var recent = wins.Take(RecentWinsShown)
            .Select(w => new RedsecWinLine(w.Id, w.WonOn,
                playersByWin[w.Id].Select(p => Member(p.UserId)).ToList(), w.Notes))
            .ToList();

        var card = new RedsecSeasonCard(
            season,
            wins.Count,
            wins.Count(w => playersByWin[w.Id].Count() >= RedsecWinsRenderer.SquadSize),
            operators.Count,
            wins[0].WonOn,
            operators.Take(OperatorsShown).ToList(),
            recent);

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

        if (png is null)
        {
            embed.AddField("Top operators", string.Join("\n",
                operators.Take(10).Select(o => $"**#{o.Place}** <@{o.Member.UserId}> — {o.Wins}")));
            embed.AddField("Recent wins", string.Join("\n", recent.Select(FormatWinLine)));
            await command.FollowupAsync(embed: embed.Build(), allowedMentions: AllowedMentions.None);
            return;
        }

        var fileName = $"redsec-season-{season}.png";
        embed.WithImageUrl($"attachment://{fileName}");
        if (operators.Count > OperatorsShown)
            embed.WithFooter($"Card shows the top {OperatorsShown} of {operators.Count} operators.");

        using var fa = new FileAttachment(new MemoryStream(png), fileName);
        await command.FollowupWithFileAsync(fa, embed: embed.Build(), allowedMentions: AllowedMentions.None);
    }

    // ─── Autocomplete ────────────────────────────────────────────────────────

    private async Task OnAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        try
        {
            var name = interaction.Data.CommandName;
            if (name is not (RemoveCommandName or WinsCommandName)) return;

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

            if (name == RemoveCommandName && interaction.Data.Current?.Name == WinOption)
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
                    return new AutocompleteResult(label.Length > 100 ? label[..99] + "…" : label, (long)w.Id);
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

    private static string DisplayNameOf(SocketUser user) =>
        (user as SocketGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username;

    private static string FormatDate(DateOnly date) =>
        date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    private static string FormatWinLine(RedsecWinLine w)
    {
        var line = $"`#{w.Id}` {w.WonOn.ToString("MMM d", CultureInfo.InvariantCulture)} — " +
                   string.Join(", ", w.Players.Select(p => $"<@{p.UserId}>"));
        var randoms = RedsecWinsRenderer.SquadSize - w.Players.Count;
        return randoms > 0 ? $"{line} +{randoms}" : line;
    }

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
