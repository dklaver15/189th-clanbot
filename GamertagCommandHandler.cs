using System.Collections.Concurrent;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using System.Text.RegularExpressions;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the gamertags flow as a button-driven two-step modal:
///
///   • "Enter Gamertags" button ("gamertags:open")  → Modal 1 (EA, Steam, PSN)
///   • Modal 1 submit                                → ephemeral "Continue" button
///   • "Continue to PAGE 2" button ("gamertags_continue") → Modal 2 (Xbox, Embark, Bungie)
///   • Modal 2 submit                                → write to roster sheet
///
/// The persistent "Enter Gamertags" button is posted by
/// GamertagSetupCommandHandler ("/setup-gamertags"). This handler owns
/// the runtime flow only — neither registering nor advertising any slash
/// command of its own. The button CustomId is exposed as a public const so
/// the setup handler can reference it without a circular dependency.
///
/// ── Why two modals? ──
/// Discord modals cap at 5 text inputs but we collect 6 gamertags. A two-page
/// flow is the simplest workaround. Modal 2 must be opened from a fresh
/// interaction (a button), not as a follow-up to Modal 1's submission —
/// hence the intermediate ephemeral "Continue" button.
/// </summary>
public partial class GamertagCommandHandler
{
    /// <summary>
    /// CustomId for the persistent "Enter Gamertags" button posted by
    /// GamertagSetupCommandHandler. Stable across restarts so messages
    /// posted in past runs continue to route correctly.
    /// </summary>
    public const string OpenButtonId = "gamertags:open";

    private const string ContinueButtonId = "gamertags_continue";
    private const string Modal1Id         = "gamertags_modal_1";
    private const string Modal2Id         = "gamertags_modal_2";

    private readonly GoogleSheetsService _sheetsService;
    private readonly OnboardingReminderHandler _onboardingReminder;
    private readonly ILogger<GamertagCommandHandler> _logger;
    private static readonly Regex DiscriminatorPattern = MyRegex();

    /// <summary>
    /// Temporary storage for partial submissions (Modal 1 data waiting for Modal 2).
    /// Key = userId
    /// </summary>
    private readonly ConcurrentDictionary<ulong, PartialGamertags> _pendingSubmissions = new();

    public GamertagCommandHandler(
        GoogleSheetsService sheetsService,
        OnboardingReminderHandler onboardingReminder,
        ILogger<GamertagCommandHandler> logger)
    {
        _sheetsService = sheetsService;
        _onboardingReminder = onboardingReminder;
        _logger = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.ModalSubmitted += OnModalSubmittedAsync;
        client.ButtonExecuted += OnButtonExecutedAsync;
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        switch (component.Data.CustomId)
        {
            case OpenButtonId:
                await ShowModal1Async(component);
                break;
            case ContinueButtonId:
                await ShowModal2Async(component);
                break;
        }
    }

    private static async Task ShowModal1Async(SocketMessageComponent component)
    {
        var modal = new ModalBuilder()
            .WithTitle("Enter Gamertags (1/2)")
            .WithCustomId(Modal1Id)
            .AddTextInput("EA Gamertag", "ea_tag", TextInputStyle.Short, placeholder: "(leave blank if n/a)", required: false)
            .AddTextInput("Steam Gamertag", "steam_tag", TextInputStyle.Short, placeholder: "(leave blank if n/a)", required: false)
            .AddTextInput("PSN Gamertag", "psn_tag", TextInputStyle.Short, placeholder: "(leave blank if n/a)", required: false)
            .Build();

        await component.RespondWithModalAsync(modal);
    }

    private static async Task ShowModal2Async(SocketMessageComponent component)
    {
        // Modal 2 (Xbox, Embark, Bungie) — triggered from a button, which IS allowed
        var modal2 = new ModalBuilder()
            .WithTitle("Enter Gamertags (2/2)")
            .WithCustomId(Modal2Id)
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
            case Modal1Id:
                await HandleModal1Async(modal);
                break;
            case Modal2Id:
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
            .WithButton("➡️ Continue to PAGE 2", ContinueButtonId, ButtonStyle.Primary)
            .Build();

        // Trailing \u200B (zero-width space) forces Discord to render the blank line
        // between the text and the button — a literal blank line gets stripped.
        const string message = """
            ## ⚠️ Step 1 of 2 — Not Done Yet

            ✅ Got your **EA**, **Steam**, and **PSN** tags.
            ❌ Still need: **Xbox**, **Embark**, **Bungie**.

            ***Your gamertags will NOT be saved until you complete Page 2.***

            ⬇️ Click the button below to finish.
            """ + "\n\u200B";

        await modal.RespondAsync(
            message,
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
                    "⚠️ Something went wrong — your first set of gamertags was lost. "
                    + "Please click the **Enter Gamertags** button again to start over.",
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
                modal.User.Id, discordName, partial.EA, partial.Steam, partial.PSN,
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

            // Notify the onboarding reminder handler (triggers a 24h reminder if still a Guest)
            if (modal.GuildId.HasValue)
            {
                _onboardingReminder.NotifyGamertagCompleted(modal.GuildId.Value, modal.User.Id);
            }
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