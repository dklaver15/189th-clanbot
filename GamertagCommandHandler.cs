
using System.Collections.Concurrent;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using System.Text.RegularExpressions;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /gamertags slash command using a two-step modal flow.
/// Modal 1 (from slash command): EA, Steam, PSN
/// Modal 2 (from button click): Xbox, Embark, Bungie
/// </summary>
public partial class GamertagCommandHandler
{
    private readonly GoogleSheetsService _sheetsService;
    private readonly ILogger<GamertagCommandHandler> _logger;
    private static readonly Regex DiscriminatorPattern = MyRegex();

    /// <summary>
    /// Temporary storage for partial submissions (Modal 1 data waiting for Modal 2).
    /// Key = userId
    /// </summary>
    private readonly ConcurrentDictionary<ulong, PartialGamertags> _pendingSubmissions = new();

    public GamertagCommandHandler(GoogleSheetsService sheetsService, ILogger<GamertagCommandHandler> logger)
    {
        _sheetsService = sheetsService;
        _logger = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.ModalSubmitted += OnModalSubmittedAsync;
        client.ButtonExecuted += OnButtonExecutedAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "gamertags") return;

        // Show the first modal (EA, Steam, PSN)
        var modal = new ModalBuilder()
            .WithTitle("Enter Gamertags (1/2)")
            .WithCustomId("gamertags_modal_1")
            .AddTextInput("EA Gamertag", "ea_tag", TextInputStyle.Short, placeholder: "(leave blank if n/a)", required: false)
            .AddTextInput("Steam Gamertag", "steam_tag", TextInputStyle.Short, placeholder: "(leave blank if n/a)", required: false)
            .AddTextInput("PSN Gamertag", "psn_tag", TextInputStyle.Short, placeholder: "(leave blank if n/a)", required: false)
            .Build();

        await command.RespondWithModalAsync(modal);
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.Data.CustomId != "gamertags_continue") return;

        // Show the second modal (Xbox, Embark, Bungie) — triggered from a button, which IS allowed
        var modal2 = new ModalBuilder()
            .WithTitle("Enter Gamertags (2/2)")
            .WithCustomId("gamertags_modal_2")
            .AddTextInput("Xbox Gamertag", "xbox_tag", TextInputStyle.Short, placeholder: "(leave blank if n/a)", required: false)
            .AddTextInput("Embark Gamertag", "embark_tag", TextInputStyle.Short, 
                placeholder: "e.g. Guardian#7028", required: false)
            .AddTextInput("Bungie Gamertag", "bungie_tag", TextInputStyle.Short, 
                placeholder: "e.g. Guardian#1234", required: false)
            .Build();

        await component.RespondWithModalAsync(modal2);
    }

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        switch (modal.Data.CustomId)
        {
            case "gamertags_modal_1":
                await HandleModal1Async(modal);
                break;
            case "gamertags_modal_2":
                await HandleModal2Async(modal);
                break;
        }
    }

    private async Task HandleModal1Async(SocketModal modal)
    {
        var components = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? "");

        // Store partial data
        _pendingSubmissions[modal.User.Id] = new PartialGamertags
        {
            EA = components.GetValueOrDefault("ea_tag", ""),
            Steam = components.GetValueOrDefault("steam_tag", ""),
            PSN = components.GetValueOrDefault("psn_tag", "")
        };

        // Send an ephemeral message with a button to open Modal 2
        var button = new ComponentBuilder()
            .WithButton("Continue to Xbox / Embark / Bungie →", "gamertags_continue", ButtonStyle.Primary)
            .Build();

        await modal.RespondAsync(
            "✅ Got your EA, Steam, and PSN tags! Click the button below to enter the rest.",
            components: button,
            ephemeral: true);
    }

    private async Task HandleModal2Async(SocketModal modal)
    {
        await modal.DeferAsync(ephemeral: true);

        try
        {
            if (!_pendingSubmissions.TryRemove(modal.User.Id, out var partial))
            {
                await modal.FollowupAsync(
                    "⚠️ Something went wrong — your first set of gamertags was lost. Please run `/gamertags` again.",
                    ephemeral: true);
                return;
            }

            var components = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? "");
            var xbox = components.GetValueOrDefault("xbox_tag", "");
            var embark = components.GetValueOrDefault("embark_tag", "");
            var bungie = components.GetValueOrDefault("bungie_tag", "");
            
            // Validate Embark
            if (!string.IsNullOrWhiteSpace(embark) && !DiscriminatorPattern.IsMatch(embark))
            {
                _pendingSubmissions[modal.User.Id] = partial; // put it back
                await modal.FollowupAsync(
                    "⚠️ Invalid **Embark** gamertag. Expected format: `Name#1234`. Please click the button and try again.",
                    ephemeral: true);
                return;
            }

            // Validate Bungie format
            if (!string.IsNullOrWhiteSpace(bungie) && !DiscriminatorPattern.IsMatch(bungie))
            {
                _pendingSubmissions[modal.User.Id] = partial; // put it back
                await modal.FollowupAsync(
                    "⚠️ Invalid **Bungie** gamertag format. Expected format: `Name#1234` (name followed by # and 4 digits).",
                    ephemeral: true);
                return;
            }

            var discordName = modal.User.GlobalName ?? modal.User.Username;

            await _sheetsService.WriteGamertagsAsync(
                discordName, partial.EA, partial.Steam, partial.PSN,
                xbox, embark, bungie);

            var embed = new EmbedBuilder()
                .WithTitle("🎮 Gamertags Saved!")
                .WithColor(Color.Green)
                .AddField("EA", string.IsNullOrWhiteSpace(partial.EA) ? "—" : partial.EA, true)
                .AddField("Steam", string.IsNullOrWhiteSpace(partial.Steam) ? "—" : partial.Steam, true)
                .AddField("PSN", string.IsNullOrWhiteSpace(partial.PSN) ? "—" : partial.PSN, true)
                .AddField("Xbox", string.IsNullOrWhiteSpace(xbox) ? "—" : xbox, true)
                .AddField("Embark", string.IsNullOrWhiteSpace(embark) ? "—" : embark, true)
                .AddField("Bungie", string.IsNullOrWhiteSpace(bungie) ? "—" : bungie, true)
                .WithFooter("Your gamertags have been exported to the roster sheet.")
                .Build();

            await modal.FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save gamertags for {User}", modal.User.Username);
            await modal.FollowupAsync(
                "❌ Failed to save your gamertags. Please try again or contact an admin.",
                ephemeral: true);
        }
    }

    private class PartialGamertags
    {
        public string EA { get; set; } = "";
        public string Steam { get; set; } = "";
        public string PSN { get; set; } = "";
    }

    [GeneratedRegex(@"^.+#\d{4}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}