using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Owns the runtime flow of the officer application system:
///
///   • Button click ("officer_app:open")    → eligibility check → modal open
///   • Modal submit ("officer_app:submit")  → re-validate → write row →
///                                            build dossier → post to HQ →
///                                            ephemeral confirmation
///
/// Eligibility rules (enforced at BOTH button click AND modal submit, because
/// a member could be demoted or AWOL'd in the gap between the two):
///
///   1. Must hold a rank role at or above OfficerAppMinimumRank (default SGT)
///   2. Must NOT currently hold the AWOL role
///   3. Must NOT have a Pending OfficerApplication row already on file
///
/// The duplicate-pending check is the most expensive (DB hit) so it runs last
/// in the chain. Members who fail any check get an ephemeral rejection
/// explaining which gate they hit; the modal never opens for ineligible
/// members and ineligible submissions are dropped before any row is written.
///
/// ── Phase 1 placeholder is replaced ──
/// The Phase 1 button handler that lived on OfficerApplicationSetupCommandHandler
/// has been removed; this handler now owns the "officer_app:open" CustomId.
/// Both handlers can coexist subscribing to ButtonExecuted — Discord.Net
/// delivers every interaction to every subscriber, and each handler filters
/// by CustomId — but to avoid two responses to the same click, the setup
/// handler no longer subscribes.
/// </summary>
public sealed class OfficerApplicationModalHandler
{
    public const string OpenButtonId   = "officer_app:open";
    public const string SubmitModalId  = "officer_app:submit";

    private const string Q1Id = "q1_area";
    private const string Q2Id = "q2_ideas";
    private const string Q3Id = "q3_conflict";

    private readonly IServiceProvider _services;
    private readonly OfficerApplicationDossierBuilder _dossier;
    private readonly ILogger<OfficerApplicationModalHandler> _logger;
    private readonly BotConfig _config;

    public OfficerApplicationModalHandler(
        IServiceProvider services,
        OfficerApplicationDossierBuilder dossier,
        ILogger<OfficerApplicationModalHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _dossier  = dossier;
        _logger   = logger;
        _config   = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.ButtonExecuted += OnButtonExecutedAsync;
        client.ModalSubmitted += OnModalSubmittedAsync;
    }

    // ─── Button click → modal ─────────────────────────────────────────

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.Data.CustomId != OpenButtonId) return;

        try
        {
            await HandleOpenAsync(component);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening officer application modal");
            try
            {
                if (component.HasResponded)
                    await component.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                else
                    await component.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* swallow */ }
        }
    }

    private async Task HandleOpenAsync(SocketMessageComponent component)
    {
        var member = component.User as SocketGuildUser;
        if (member is null)
        {
            await component.RespondAsync(
                "This button only works inside the server.", ephemeral: true);
            return;
        }

        // Eligibility pre-check so ineligible members never see the modal
        var verdict = await CheckEligibilityAsync(member);
        if (verdict is not null)
        {
            await component.RespondAsync(verdict, ephemeral: true);
            return;
        }

        // Pop the modal. CustomId is stable across restarts.
        var modal = new ModalBuilder()
            .WithTitle("189th Officer Application")
            .WithCustomId(SubmitModalId)
            .AddTextInput(
                label: "Where do you want to contribute?",
                customId: Q1Id,
                style: TextInputStyle.Short,
                placeholder: "Recruiting, events, Discord/tooling, training, etc.",
                minLength: 3,
                maxLength: 200,
                required: true)
            .AddTextInput(
                label: "What ideas would you bring to the 189th?",
                customId: Q2Id,
                style: TextInputStyle.Paragraph,
                placeholder: "Specific improvements or initiatives you have in mind",
                minLength: 30,
                maxLength: 1000,
                required: true)
            .AddTextInput(
                label: "How do you handle conflict?",
                customId: Q3Id,
                style: TextInputStyle.Paragraph,
                placeholder: "Two clanmates arguing during an event, derailing the squad — what do you do?",
                minLength: 30,
                maxLength: 1000,
                required: true)
            .Build();

        await component.RespondWithModalAsync(modal);
    }

    // ─── Modal submit → write row + post dossier ──────────────────────

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        if (modal.Data.CustomId != SubmitModalId) return;

        try
        {
            await HandleSubmitAsync(modal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing officer application submission");
            try
            {
                if (modal.HasResponded)
                    await modal.FollowupAsync("Something went wrong submitting your application. HQ has been notified — please try again later.", ephemeral: true);
                else
                    await modal.RespondAsync("Something went wrong submitting your application. HQ has been notified — please try again later.", ephemeral: true);
            }
            catch { /* swallow */ }
        }
    }

    private async Task HandleSubmitAsync(SocketModal modal)
    {
        await modal.DeferAsync(ephemeral: true);

        var member = modal.User as SocketGuildUser;
        if (member is null)
        {
            await modal.FollowupAsync("This form only works inside the server.", ephemeral: true);
            return;
        }

        // Defensive re-validation — the user could have been demoted, gained
        // AWOL, or had a Pending row created (e.g. by a race with a second
        // open browser tab) between button click and submit. Cheap to re-do.
        var verdict = await CheckEligibilityAsync(member);
        if (verdict is not null)
        {
            await modal.FollowupAsync(verdict, ephemeral: true);
            return;
        }

        // ── Extract answers ─────────────────────────────────────────
        var components = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? "");
        var q1 = components.GetValueOrDefault(Q1Id, "").Trim();
        var q2 = components.GetValueOrDefault(Q2Id, "").Trim();
        var q3 = components.GetValueOrDefault(Q3Id, "").Trim();

        // ── Persist the row ─────────────────────────────────────────
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var application = new OfficerApplication
        {
            GuildId          = member.Guild.Id,
            UserId           = member.Id,
            DisplayName      = member.DisplayName ?? member.Username,
            Q1AreaOfInterest = q1,
            Q2Ideas          = q2,
            Q3Conflict       = q3,
            SubmittedAt      = DateTime.UtcNow,
            Status           = OfficerApplicationStatus.Pending,
        };

        db.OfficerApplications.Add(application);
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Officer application #{Id} submitted by {User} ({UserId})",
            application.Id, member.Username, member.Id);

        // ── Resolve HQ channel ──────────────────────────────────────
        if (_config.OfficerAppHqChannelId == 0)
        {
            _logger.LogWarning(
                "OfficerAppHqChannelId is not configured; application #{Id} stored but no HQ post made",
                application.Id);

            await modal.FollowupAsync(
                "✅ Your application was received, but HQ notification is misconfigured. "
                + "Please ping an officer directly so they know to review it.",
                ephemeral: true);
            return;
        }

        var hqChannel = member.Guild.GetTextChannel(_config.OfficerAppHqChannelId);
        if (hqChannel is null)
        {
            _logger.LogWarning(
                "OfficerAppHqChannelId {ChannelId} not resolvable in guild {GuildId}; application #{Id} stored but no HQ post made",
                _config.OfficerAppHqChannelId, member.Guild.Id, application.Id);

            await modal.FollowupAsync(
                "✅ Your application was received, but HQ channel could not be located. "
                + "Please ping an officer directly so they know to review it.",
                ephemeral: true);
            return;
        }

        // ── Build & post dossier ────────────────────────────────────
        var dossierEmbed = await _dossier.BuildAsync(member, application);

        var hqMention = _config.OfficerAppHqRoleId != 0
            ? $"<@&{_config.OfficerAppHqRoleId}>"
            : string.Empty;

        var posted = await hqChannel.SendMessageAsync(
            text: hqMention.Length > 0 ? $"{hqMention} new officer application submitted." : null,
            embed: dossierEmbed,
            components: OfficerApplicationReviewHandler.BuildPendingButtons(application.Id),
            allowedMentions: _config.OfficerAppHqRoleId != 0
                ? new AllowedMentions
                {
                    RoleIds = new List<ulong> { _config.OfficerAppHqRoleId },
                    MentionRepliedUser = false,
                }
                : AllowedMentions.None);

        // Persist the dossier message ID so Phase 3 review buttons can locate
        // the original embed without a channel-history scan.
        application.DossierMessageId = posted.Id;
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Officer application #{Id} dossier posted to channel {ChannelId} as message {MessageId}",
            application.Id, hqChannel.Id, posted.Id);

        // ── Discussion thread (best-effort) ─────────────────────────
        // Spawn a thread off the dossier message so HQ can deliberate
        // without polluting the main channel. Thread inherits the HQ
        // channel's permissions, so it stays HQ-only by construction.
        // Failure (e.g. bot missing CreatePublicThreads perm) is logged
        // but doesn't roll back the application — the dossier is already
        // posted and the voting buttons are functional regardless.
        try
        {
            var thread = await hqChannel.CreateThreadAsync(
                name: BuildDiscussionThreadName(application),
                autoArchiveDuration: ThreadArchiveDuration.OneWeek,
                message: posted);

            await thread.SendMessageAsync(
                "💬 Discuss the candidate here. "
                + "HQ can approve or deny the application using the buttons on the dossier above.",
                allowedMentions: AllowedMentions.None);

            _logger.LogInformation(
                "Discussion thread created for app #{AppId}: thread {ThreadId}",
                application.Id, thread.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create discussion thread for app #{AppId}; dossier still posted",
                application.Id);
        }

        // ── Confirm to applicant ────────────────────────────────────
        await modal.FollowupAsync(
            "✅ **Your application has been submitted.**\n"
            + "HQ will review and reach out with a decision. "
            + "Thanks for stepping up.",
            ephemeral: true);
    }

    /// <summary>
    /// Discord caps thread names at 100 chars. The "App #N — " prefix is
    /// usually 7-9 chars, leaving plenty of room for the display name with
    /// truncation only in extreme cases.
    /// </summary>
    private static string BuildDiscussionThreadName(OfficerApplication application)
    {
        var prefix      = $"App #{application.Id} — ";
        var displayName = string.IsNullOrWhiteSpace(application.DisplayName)
            ? "Applicant"
            : application.DisplayName;

        const int maxLen = 100;
        var remaining = maxLen - prefix.Length;
        if (displayName.Length > remaining)
            displayName = displayName[..(remaining - 1)] + "…";

        return prefix + displayName;
    }

    // ─── Eligibility ──────────────────────────────────────────────────

    /// <summary>
    /// Returns null if the member is eligible to apply, or an ephemeral
    /// message describing the failed check. Used at both button-click and
    /// modal-submit time.
    /// </summary>
    private async Task<string?> CheckEligibilityAsync(SocketGuildUser member)
    {
        // Gate 1 — rank floor
        var rankList = _config.GetRankRolesList();
        var minRankIdx = rankList.FindIndex(r =>
            r.Equals(_config.OfficerAppMinimumRank, StringComparison.OrdinalIgnoreCase));

        if (minRankIdx < 0)
        {
            _logger.LogWarning(
                "OfficerAppMinimumRank '{Rank}' not found in RankRoles list — denying everyone",
                _config.OfficerAppMinimumRank);
            return "⛔ The officer application system is misconfigured. Please ping HQ directly.";
        }

        if (!MemberRankIsAtOrAbove(member, rankList, minRankIdx))
        {
            return $"⛔ You must be **{_config.OfficerAppMinimumRank}** or higher to apply for an officer position. "
                + "Keep contributing — promotions open this door.";
        }

        // Gate 2 — not currently AWOL
        var isAwol = member.Roles.Any(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));
        if (isAwol)
        {
            return "⛔ You're currently flagged AWOL. Get caught up on activity and clear the flag before applying.";
        }

        // Gate 3 — no pending application already on file
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var hasPending = await db.OfficerApplications
            .AnyAsync(a => a.GuildId == member.Guild.Id
                        && a.UserId  == member.Id
                        && a.Status  == OfficerApplicationStatus.Pending);

        if (hasPending)
        {
            return "ℹ️ You already have an application under review. HQ will reach out — no need to resubmit.";
        }

        return null;
    }

    /// <summary>
    /// True if the member holds any rank role at or above the given index in
    /// the rank list. SyncWithHandlers: mirrors KickAwolsCommandHandler.MemberRankIsAtOrAbove.
    /// </summary>
    private static bool MemberRankIsAtOrAbove(
        SocketGuildUser member, List<string> rankList, int minRankIdx)
    {
        foreach (var role in member.Roles)
        {
            var idx = rankList.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= minRankIdx) return true;
        }
        return false;
    }
}