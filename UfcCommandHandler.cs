using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The <c>/ufc-schedule</c> and <c>/ufc-results</c> slash commands — open to all
/// members. Schedule lists upcoming UFC events; results shows the most recent
/// card's outcomes. Both pull from <see cref="UfcApiService"/> (SportsDataIO)
/// and attach an official event poster via <see cref="UfcEventPosterService"/>.
///
/// Replies are public (not ephemeral) so the whole channel sees them — these are
/// fun, shareable lookups, not officer tooling.
/// </summary>
public sealed class UfcCommandHandler
{
    public const string ScheduleCommandName = "ufc-schedule";
    public const string ResultsCommandName  = "ufc-results";

    private readonly UfcApiService _api;
    private readonly UfcResultsService _results;
    private readonly UfcEventPosterService _posters;
    private readonly ILogger<UfcCommandHandler> _logger;

    public UfcCommandHandler(
        UfcApiService api,
        UfcResultsService results,
        UfcEventPosterService posters,
        ILogger<UfcCommandHandler> logger)
    {
        _api = api;
        _results = results;
        _posters = posters;
        _logger = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    public static SlashCommandProperties BuildScheduleCommand() =>
        new SlashCommandBuilder()
            .WithName(ScheduleCommandName)
            .WithDescription("Show upcoming UFC events")
            .Build();

    public static SlashCommandProperties BuildResultsCommand() =>
        new SlashCommandBuilder()
            .WithName(ResultsCommandName)
            .WithDescription("Show results from the latest UFC event")
            .Build();

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not (ScheduleCommandName or ResultsCommandName))
            return;

        try
        {
            await HandleAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", command.Data.Name);
            try { await command.FollowupAsync("Something went wrong fetching UFC data. Try again in a bit.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();

        if (command.Data.Name == ScheduleCommandName)
            await HandleScheduleAsync(command);
        else
            await HandleResultsAsync(command);
    }

    private async Task HandleScheduleAsync(SocketSlashCommand command)
    {
        if (!_api.IsConfigured)
        {
            await command.FollowupAsync(
                "UFC schedule isn't set up yet — an officer needs to add a SportsDataIO API key to the bot config.",
                ephemeral: true);
            return;
        }

        var upcoming = await _api.GetUpcomingAsync(max: 8);

        if (upcoming.Count == 0)
        {
            await command.FollowupAsync("Couldn't find any upcoming UFC events right now.");
            return;
        }

        var poster = await _posters.GetPosterUrlAsync(upcoming[0].Name);
        var embed = UfcEmbedBuilder.BuildScheduleEmbed(upcoming, poster);
        await command.FollowupAsync(embed: embed);
    }

    private async Task HandleResultsAsync(SocketSlashCommand command)
    {
        if (!_results.IsConfigured)
        {
            await command.FollowupAsync(
                "UFC results aren't set up yet — an officer needs to add an API-Sports MMA key to the bot config.",
                ephemeral: true);
            return;
        }

        var ev = await _results.GetLatestEventResultsAsync();

        if (ev is null)
        {
            await command.FollowupAsync("Couldn't find a recent UFC event with results yet.");
            return;
        }

        var poster = await _posters.GetPosterUrlAsync(ev.Name);
        var embed = UfcEmbedBuilder.BuildResultsEmbed(ev, poster);
        await command.FollowupAsync(embed: embed);
    }
}
