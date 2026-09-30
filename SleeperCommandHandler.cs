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
/// Owns the Sleeper fantasy football slash commands.
///
///   • /sleeper-standings: the league table, on demand                 (everyone)
///   • /sleeper-matchups:  this week's games and live scores           (everyone)
///   • /sleeper-link:      bind a Discord account to a Sleeper account (everyone; others = Fantasy Mod)
///
/// The standings are deliberately a command rather than a pinned board: the league
/// is a side activity for part of the server, so it is pulled when someone wants it
/// instead of occupying a channel all season.
///
/// ── Gateway safety ──
/// Every command can hit the network, and Discord.NET runs handlers ON the gateway
/// task, so each one dispatches to Task.Run and returns immediately. Same rule that
/// came out of /health freezing during a long purge.
///
/// SyncWithHandlers: CommandsCommandHandler.BuildCatalog (permission labels).
/// </summary>
public sealed class SleeperCommandHandler
{
    public const string StandingsCommandName = "sleeper-standings";
    public const string MatchupsCommandName  = "sleeper-matchups";
    public const string LinkCommandName      = "sleeper-link";

    private static readonly string[] CommandNames =
    {
        StandingsCommandName,
        MatchupsCommandName,
        LinkCommandName,
    };

    private readonly SleeperApiService _api;
    private readonly SleeperLinkService _links;
    private readonly BotConfig _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<SleeperCommandHandler> _logger;

    public SleeperCommandHandler(
        SleeperApiService api,
        SleeperLinkService links,
        IOptions<BotConfig> config,
        IServiceProvider services,
        ILogger<SleeperCommandHandler> logger)
    {
        _api = api;
        _links = links;
        _config = config.Value;
        _services = services;
        _logger = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    // ─── Command definitions ─────────────────────────────────────────────────

    public static SlashCommandProperties BuildStandingsCommand() =>
        new SlashCommandBuilder()
            .WithName(StandingsCommandName)
            .WithDescription("Show the fantasy football league standings")
            .Build();

    public static SlashCommandProperties BuildMatchupsCommand() =>
        new SlashCommandBuilder()
            .WithName(MatchupsCommandName)
            .WithDescription("Show this week's fantasy matchups and live scores")
            .AddOption("week", ApplicationCommandOptionType.Integer,
                "A specific NFL week (defaults to the current one)",
                isRequired: false, minValue: 1, maxValue: 18)
            .Build();

    public static SlashCommandProperties BuildLinkCommand() =>
        new SlashCommandBuilder()
            .WithName(LinkCommandName)
            .WithDescription("Link your Discord account to your Sleeper account")
            .AddOption("username", ApplicationCommandOptionType.String,
                "Your Sleeper username, exactly as it appears in Sleeper", isRequired: false)
            .AddOption("user", ApplicationCommandOptionType.User,
                "Link on someone else's behalf (Fantasy Mod only)", isRequired: false)
            .AddOption("unlink", ApplicationCommandOptionType.Boolean,
                "Remove an existing link instead of creating one", isRequired: false)
            .Build();

    // ─── Dispatch ────────────────────────────────────────────────────────────

    private Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!CommandNames.Contains(command.Data.Name)) return Task.CompletedTask;

        // Offload immediately: these handlers all make an outbound HTTP call, and
        // awaiting one on the gateway task is what froze /health during a purge.
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling /{Command} for {User}",
                    command.Data.Name, command.User.Username);
                try
                {
                    if (command.HasResponded)
                        await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                    else
                        await command.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                }
                catch { /* interaction already expired */ }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case StandingsCommandName: await HandleStandingsAsync(command); break;
            case MatchupsCommandName:  await HandleMatchupsAsync(command); break;
            case LinkCommandName:      await HandleLinkAsync(command); break;
        }
    }

    // ─── /sleeper-standings ──────────────────────────────────────────────────

    private async Task HandleStandingsAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();
        if (!await EnsureEnabledAsync(command)) return;

        var league = await _api.GetLeagueAsync();
        if (league is null)
        {
            await command.FollowupAsync(NotFoundMessage(), ephemeral: true);
            return;
        }

        var teams = await _api.GetTeamsWithRecordsAsync();
        var state = await _api.GetNflStateAsync();
        var links = command.GuildId is ulong gid
            ? await _links.GetSleeperToDiscordAsync(gid)
            : null;

        var embed = SleeperFormat.BuildStandingsEmbed(
            league, teams, links, _config.SleeperMentionLinkedMembers, state);

        // Mentions are rendered as text only. A standings table that pings sixteen
        // people every time someone runs it would be intolerable in a shared channel.
        await command.FollowupAsync(embed: embed, allowedMentions: AllowedMentions.None);
    }

    // ─── /sleeper-matchups ───────────────────────────────────────────────────

    private async Task HandleMatchupsAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();
        if (!await EnsureEnabledAsync(command)) return;

        var league = await _api.GetLeagueAsync();
        if (league is null)
        {
            await command.FollowupAsync(NotFoundMessage(), ephemeral: true);
            return;
        }

        if (!league.IsDrafted)
        {
            await command.FollowupAsync(
                $"**{SleeperFormat.Escape(league.Name)}** has not drafted yet, so there are no matchups. " +
                "Run `/sleeper-standings` to see who has joined so far.",
                ephemeral: true);
            return;
        }

        var state = await _api.GetNflStateAsync();
        var requested = command.Data.Options.FirstOrDefault(o => o.Name == "week")?.Value as long?;
        var week = (int)(requested ?? state?.EffectiveWeek ?? 1);

        var teams = await _api.GetTeamsWithRecordsAsync();
        var sides = await _api.GetMatchupsAsync(week);
        var games = SleeperFormat.PairGames(sides, teams);

        if (games.Count == 0)
        {
            await command.FollowupAsync(
                $"No matchups came back for week {week}. Weeks past the regular season, or before the season starts, have nothing to show.",
                ephemeral: true);
            return;
        }

        var links = command.GuildId is ulong gid
            ? await _links.GetSleeperToDiscordAsync(gid)
            : null;

        var embed = SleeperFormat.BuildMatchupsEmbed(
            league, week, games, links, _config.SleeperMentionLinkedMembers);

        await command.FollowupAsync(embed: embed, allowedMentions: AllowedMentions.None);
    }

    // ─── /sleeper-link ───────────────────────────────────────────────────────

    private async Task HandleLinkAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is not ulong guildId || command.User is not SocketGuildUser caller)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        if (!await EnsureEnabledAsync(command)) return;

        var username = (command.Data.Options.FirstOrDefault(o => o.Name == "username")?.Value as string ?? "").Trim();
        var onBehalfOf = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;
        var unlink = command.Data.Options.FirstOrDefault(o => o.Name == "unlink")?.Value as bool? ?? false;

        // Linking or unlinking someone else is a moderator action. Otherwise anyone
        // could bind a teammate's Sleeper account to their own Discord account.
        if (onBehalfOf is not null && onBehalfOf.Id != caller.Id && !HasAdminRole(caller))
        {
            await command.FollowupAsync(
                "Only Fantasy Mods can link someone else. Leave `user` blank to link yourself.",
                ephemeral: true);
            return;
        }

        var targetId = onBehalfOf?.Id ?? caller.Id;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        if (unlink)
        {
            var removed = await db.SleeperLinks
                .Where(l => l.GuildId == guildId && l.DiscordUserId == targetId)
                .ToListAsync();

            if (removed.Count == 0)
            {
                await command.FollowupAsync(
                    targetId == caller.Id ? "You are not linked to a Sleeper account." : $"<@{targetId}> is not linked to a Sleeper account.",
                    ephemeral: true, allowedMentions: AllowedMentions.None);
                return;
            }

            db.SleeperLinks.RemoveRange(removed);
            await db.SaveChangesAsync();
            _links.Invalidate(guildId);

            await command.FollowupAsync(
                targetId == caller.Id ? "Your Sleeper link is removed." : $"<@{targetId}>'s Sleeper link is removed.",
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            await ShowCurrentLinkAsync(command, db, guildId, targetId, caller.Id);
            return;
        }

        var user = await _api.LookupUserAsync(username);
        if (user is null)
        {
            await command.FollowupAsync(
                $"Sleeper has no account called **{SleeperFormat.Escape(username)}**. Check the spelling in the Sleeper app under your profile.",
                ephemeral: true);
            return;
        }

        // Membership is the only thing that can be verified: Sleeper has no OAuth,
        // so a typed name proves nothing on its own. Requiring the account to
        // already be in the league narrows a link from "anyone on Sleeper" to
        // "one of the people we are actually playing against".
        var teams = await _api.GetTeamsAsync();
        var inLeague = teams.Any(t => string.Equals(t.OwnerId, user.UserId, StringComparison.Ordinal));
        if (!inLeague)
        {
            var league = await _api.GetLeagueAsync();
            await command.FollowupAsync(
                $"**{SleeperFormat.Escape(user.DisplayName)}** is a real Sleeper account but is not in " +
                $"**{SleeperFormat.Escape(league?.Name ?? "the league")}**. Join the league first, then link.",
                ephemeral: true);
            return;
        }

        // League membership proves the account exists, not who owns it, so an
        // account already linked to someone else can only be moved by a mod.
        var ownerId = await db.SleeperLinks
            .Where(l => l.GuildId == guildId && l.SleeperUserId == user.UserId && l.DiscordUserId != targetId)
            .Select(l => (ulong?)l.DiscordUserId)
            .FirstOrDefaultAsync();
        if (ownerId is ulong owner && !HasAdminRole(caller))
        {
            await command.FollowupAsync(
                $"**{SleeperFormat.Escape(user.DisplayName)}** is already linked to <@{owner}>. If that's wrong, " +
                "ask a Fantasy Mod to relink it with the `user` option.",
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        // Re-linking overwrites in BOTH directions: one Discord account per Sleeper
        // account and vice versa. Clearing the other side stops two members both
        // claiming the same team.
        var existing = await db.SleeperLinks
            .Where(l => l.GuildId == guildId
                     && (l.DiscordUserId == targetId || l.SleeperUserId == user.UserId))
            .ToListAsync();
        if (existing.Count > 0)
            db.SleeperLinks.RemoveRange(existing);

        db.SleeperLinks.Add(new SleeperLink
        {
            GuildId = guildId,
            DiscordUserId = targetId,
            SleeperUserId = user.UserId,
            SleeperUsername = user.DisplayName,
            LinkedUtc = DateTime.UtcNow,
            LinkedByUserId = caller.Id,
        });

        await db.SaveChangesAsync();
        _links.Invalidate(guildId);

        _logger.LogInformation("Sleeper link: Discord {Target} to {SleeperUser} ({SleeperId}) by {Caller}",
            targetId, user.DisplayName, user.UserId, caller.Id);

        var team = teams.FirstOrDefault(t => string.Equals(t.OwnerId, user.UserId, StringComparison.Ordinal));
        var teamNote = team is null ? string.Empty : $" You are **{SleeperFormat.Escape(team.Label)}**.";
        var forWhom = targetId == caller.Id ? "You are" : $"<@{targetId}> is";

        await command.FollowupAsync(
            $"{forWhom} now linked to Sleeper account **{SleeperFormat.Escape(user.DisplayName)}**.{teamNote} " +
            "Fantasy posts will tag the right person from here on.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    private async Task ShowCurrentLinkAsync(
        SocketSlashCommand command, BotDbContext db, ulong guildId, ulong targetId, ulong callerId)
    {
        var link = await db.SleeperLinks
            .FirstOrDefaultAsync(l => l.GuildId == guildId && l.DiscordUserId == targetId);

        if (link is null)
        {
            var who = targetId == callerId ? "You are" : $"<@{targetId}> is";
            await command.FollowupAsync(
                $"{who} not linked yet. Run `/sleeper-link username:<your Sleeper username>` to link. " +
                "Your username is in the Sleeper app under your profile.",
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        // Read the CURRENT name off the league rather than the stored one, which is
        // allowed to go stale: Sleeper lets people rename themselves at any point.
        var teams = await _api.GetTeamsAsync();
        var team = teams.FirstOrDefault(t => string.Equals(t.OwnerId, link.SleeperUserId, StringComparison.Ordinal));
        var currentName = team?.Owner?.DisplayName ?? link.SleeperUsername;

        var whoIs = targetId == callerId ? "You are" : $"<@{targetId}> is";
        var teamNote = team is null
            ? " That account is not currently in the league."
            : $" Team: **{SleeperFormat.Escape(team.Label)}**.";

        await command.FollowupAsync(
            $"{whoIs} linked to **{SleeperFormat.Escape(currentName)}**.{teamNote}\n" +
            "Run `/sleeper-link unlink:true` to remove it, or pass a `username` to change it.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    // ─── Shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Readiness check. There is no API key to validate, so this is only "is the
    /// feature on and is a league id set". The message names both keys because the
    /// two failure modes look identical from the member's side.
    /// </summary>
    private async Task<bool> EnsureEnabledAsync(SocketSlashCommand command)
    {
        if (_config.SleeperEnabled && _api.IsConfigured) return true;

        await command.FollowupAsync(
            "The fantasy football integration is not set up yet (`SleeperEnabled` / `SleeperLeagueId`). Ask an admin.",
            ephemeral: true);
        return false;
    }

    /// <summary>
    /// What to say when Sleeper answers but has nothing for the configured id.
    /// Sleeper returns a null body for an unknown league rather than an error, so a
    /// wrong league id and a transient outage arrive identically. The wording covers
    /// both without asserting which one it is.
    /// </summary>
    private string NotFoundMessage() =>
        $"Sleeper returned nothing for league `{_api.LeagueId}`. Either the id in `SleeperLeagueId` is wrong " +
        "or Sleeper is not answering right now. Try again shortly, and if it keeps happening check the id.";

    private bool HasAdminRole(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;
        if (_config.SleeperAdminRoleId == 0) return false;
        return user.Roles.Any(r => r.Id == _config.SleeperAdminRoleId);
    }
}
