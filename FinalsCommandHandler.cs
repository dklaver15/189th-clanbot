using System.Text;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The <c>/finals-rank</c> slash command — looks up a player's current standing on
/// THE FINALS ranked leaderboard. Three modes:
///   • no options       → looks up the caller (via their roster gamertags);
///   • member:@someone   → looks up that clan member (via their roster gamertags);
///   • name:"text"       → free-text search of the leaderboard by player name.
///
/// Member lookups resolve the player through the gamertags the roster already
/// collects (Embark ID first, then Steam/PSN/Xbox). Replies are public — rank cards
/// are shareable; only setup/loading/not-found notices are ephemeral.
///
/// The ranked leaderboard is the global top 10,000, so a member who isn't found is
/// simply unranked (outside the top 10k) or hasn't linked a matching handle — not
/// an error.
/// </summary>
public sealed class FinalsCommandHandler
{
    public const string CommandName = "finals-rank";

    private readonly FinalsApiService _api;
    private readonly GoogleSheetsService _sheets;
    private readonly BotConfig _config;
    private readonly ILogger<FinalsCommandHandler> _logger;

    public FinalsCommandHandler(
        FinalsApiService api,
        GoogleSheetsService sheets,
        IOptions<BotConfig> config,
        ILogger<FinalsCommandHandler> logger)
    {
        _api = api;
        _sheets = sheets;
        _config = config.Value;
        _logger = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Look up a player's rank on THE FINALS leaderboard")
            .AddOption("member", ApplicationCommandOptionType.User,
                "A clan member to look up (defaults to you)", isRequired: false)
            .AddOption("name", ApplicationCommandOptionType.String,
                "Search the leaderboard by player name instead", isRequired: false)
            .Build();

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            await command.DeferAsync();
            await HandleAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", CommandName);
            try { await command.FollowupAsync("Something went wrong looking that up. Try again in a bit.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        if (!_config.FinalsEnabled)
        {
            await command.FollowupAsync(
                "THE FINALS lookups aren't enabled on this server yet — an officer can turn them on in the bot config.",
                ephemeral: true);
            return;
        }

        var nameOption = command.Data.Options.FirstOrDefault(o => o.Name == "name")?.Value as string;
        var memberOption = command.Data.Options.FirstOrDefault(o => o.Name == "member")?.Value as SocketUser;

        if (!string.IsNullOrWhiteSpace(nameOption))
        {
            await HandleNameSearchAsync(command, nameOption.Trim());
            return;
        }

        var target = memberOption ?? command.User;
        await HandleMemberLookupAsync(command, target);
    }

    private async Task HandleMemberLookupAsync(SocketSlashCommand command, IUser target)
    {
        var displayName = (target as IGuildUser)?.DisplayName
                          ?? (target as SocketGuildUser)?.DisplayName
                          ?? target.GlobalName
                          ?? target.Username;

        GamertagLookupResult? tags;
        try
        {
            tags = await _sheets.LookupGamertagsAsync(target.Id, displayName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FINALS lookup: roster read failed for {User}", target.Id);
            await command.FollowupAsync("Couldn't read the roster sheet right now. Try again shortly.", ephemeral: true);
            return;
        }

        if (tags is null
            || (string.IsNullOrWhiteSpace(tags.Embark)
                && string.IsNullOrWhiteSpace(tags.Steam)
                && string.IsNullOrWhiteSpace(tags.PSN)
                && string.IsNullOrWhiteSpace(tags.Xbox)))
        {
            var who = target.Id == command.User.Id ? "You haven't" : $"**{displayName}** hasn't";
            await command.FollowupAsync(
                $"{who} linked a gamertag the FINALS leaderboard can match. Add an Embark ID (or Steam/PSN/Xbox name) via **/gamertags**.",
                ephemeral: true);
            return;
        }

        var match = _api.Match(tags);
        if (match is null)
        {
            if (!_api.IsWarmedUp)
            {
                await command.FollowupAsync(
                    "Still loading the leaderboard (the first fetch takes a moment) — try again shortly.",
                    ephemeral: true);
                return;
            }

            var who = target.Id == command.User.Id ? "You're" : $"**{displayName}** is";
            await command.FollowupAsync(
                $"{who} not on the current ranked leaderboard (it's the global top 10,000). Climb into the top 10k to show up here!",
                ephemeral: true);
            return;
        }

        var embed = BuildPlayerEmbed(match.Entry, displayName, target.GetAvatarUrl() ?? target.GetDefaultAvatarUrl(), match.MatchedVia);
        await command.FollowupAsync(embed: embed);
    }

    private async Task HandleNameSearchAsync(SocketSlashCommand command, string query)
    {
        var results = await _api.SearchByNameAsync(query, max: 10);

        if (results.Count == 0)
        {
            await command.FollowupAsync(
                $"No leaderboard players found matching **{query}** (the ranked board is the global top 10,000).",
                ephemeral: true);
            return;
        }

        // Single hit → show the full card; multiple → a compact list.
        if (results.Count == 1)
        {
            var e = results[0];
            await command.FollowupAsync(embed: BuildPlayerEmbed(e, e.Name, null, "Search"));
            return;
        }

        var sb = new StringBuilder();
        foreach (var e in results)
        {
            sb.AppendLine(
                $"`#{e.Rank,-6:N0}` {FinalsFormat.LeagueEmoji(e.League)} **{Sanitize(e.Name)}** — " +
                $"{e.League} · {e.RankScore:N0}");
        }

        var embed = new EmbedBuilder()
            .WithTitle($"🏆 THE FINALS — results for \"{query}\"")
            .WithColor(new Color(0xE63946))
            .WithDescription(sb.ToString().TrimEnd())
            .WithFooter($"Season {SeasonLabel()} • top {results.Count} matches by rank")
            .Build();

        await command.FollowupAsync(embed: embed);
    }

    private Embed BuildPlayerEmbed(FinalsEntry e, string title, string? thumbnailUrl, string matchedVia)
    {
        var builder = new EmbedBuilder()
            .WithTitle($"🏆 {Sanitize(e.Name)}")
            .WithColor(new Color(0xE63946))
            .AddField("League", $"{FinalsFormat.LeagueEmoji(e.League)} {(string.IsNullOrWhiteSpace(e.League) ? "Unranked" : e.League)}", inline: true)
            .AddField("Rank Score", $"{e.RankScore:N0}", inline: true)
            .AddField("Global Rank", $"#{e.Rank:N0}", inline: true)
            .AddField("24h Change", FinalsFormat.ChangeMarker(e.Change), inline: true)
            .AddField("Platforms", FinalsFormat.Platforms(e), inline: true);

        if (!string.IsNullOrWhiteSpace(e.ClubTag))
            builder.AddField("Club", $"[{e.ClubTag}]", inline: true);

        if (!string.IsNullOrWhiteSpace(thumbnailUrl))
            builder.WithThumbnailUrl(thumbnailUrl);

        builder.WithFooter($"Season {SeasonLabel()} • matched via {matchedVia} • THE FINALS leaderboard");
        return builder.Build();
    }

    private string SeasonLabel()
    {
        var v = string.IsNullOrWhiteSpace(_config.FinalsLeaderboardVersion) ? "s10" : _config.FinalsLeaderboardVersion.Trim();
        // "s10" → "10"; leave non-season ids (cb1, ob) as-is.
        return v.StartsWith("s") && int.TryParse(v[1..], out var n) ? n.ToString() : v;
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "Unknown";
        return s.Replace("`", "'").Replace("*", "\\*").Replace("_", "\\_").Trim();
    }
}
