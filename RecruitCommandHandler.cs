using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /recruit slash command.
/// Opens a modal for an onboarding user to enter a recruit's name,
/// then logs it to a dedicated Google Sheet with timestamp and who logged it.
/// </summary>
public class RecruitCommandHandler
{
    private readonly GoogleSheetsService _sheetsService;
    private readonly ILogger<RecruitCommandHandler> _logger;

    public RecruitCommandHandler(
        GoogleSheetsService sheetsService,
        ILogger<RecruitCommandHandler> logger)
    {
        _sheetsService = sheetsService;
        _logger = logger;
    }

    /// <summary>Register event handlers on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.ModalSubmitted += OnModalSubmittedAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "recruit") return;

        var modal = new ModalBuilder()
            .WithTitle("Log a Recruit")
            .WithCustomId("recruit_modal")
            .AddTextInput("Recruit Name", "recruit_name", TextInputStyle.Short,
                placeholder: "Enter the recruit's name", required: true)
            .Build();

        await command.RespondWithModalAsync(modal);
    }

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        if (modal.Data.CustomId != "recruit_modal") return;

        await modal.DeferAsync(ephemeral: true);

        try
        {
            var components = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? "");
            var recruitName = components.GetValueOrDefault("recruit_name", "").Trim();

            if (string.IsNullOrWhiteSpace(recruitName))
            {
                await modal.FollowupAsync(
                    "⚠️ Recruit name cannot be empty. Please try again.",
                    ephemeral: true);
                return;
            }

            var loggedBy = modal.User.GlobalName ?? modal.User.Username;
            var nowUtc = DateTime.UtcNow;

            await _sheetsService.WriteRecruitLogAsync(recruitName, loggedBy, nowUtc);

            var embed = new EmbedBuilder()
                .WithTitle("📋 Recruit Logged!")
                .WithColor(Color.Green)
                .AddField("Recruit", recruitName, true)
                .AddField("Logged By", loggedBy, true)
                .AddField("Date", nowUtc.ToString("yyyy-MM-dd"), true)
                .AddField("Time (UTC)", nowUtc.ToString("HH:mm:ss"), true)
                .WithFooter("ClanGuard Bot • Recruit Log")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await modal.FollowupAsync(embed: embed, ephemeral: true);

            _logger.LogInformation(
                "Recruit '{RecruitName}' logged by {LoggedBy} at {Timestamp}",
                recruitName, loggedBy, nowUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log recruit for {User}", modal.User.Username);
            await modal.FollowupAsync(
                "❌ Failed to log the recruit. Please try again or contact an admin.",
                ephemeral: true);
        }
    }
}