using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Owns the HQ-side review flow for officer applications: approve / deny
/// buttons on the dossier embed, the approval-confirmation ephemeral
/// prompt, the denial-reason modal, in-place dossier embed updates,
/// applicant DM delivery, and audit field persistence.
///
/// ── Interaction map ──
///
///   ┌─────────────────────────────────────────────────────────────────┐
///   │  Dossier embed in HQ channel (posted by OfficerApplicationModalHandler) │
///   │  ┌─[ ✅ Approve ]─┐  ┌─[ ❌ Deny ]─┐                              │
///   └────────┬──────────────────┬──────────────────────────────────────┘
///            │                  │
///            │                  └──── officer_app:deny:{id}
///            │                       └─→ deny-reason modal pops
///            │                            (officer_app:deny_modal:{id})
///            │                            └─→ on submit: persist + update +
///            │                                DM applicant
///            │
///            └──── officer_app:approve:{id}
///                  └─→ ephemeral confirmation prompt with:
///                       ┌─[ ✅ Confirm Approval ]─┐  ┌─[ Cancel ]─┐
///                       │                            │
///                       └── officer_app:approve_confirm:{id}
///                            └─→ persist + update + DM applicant
///                                                    │
///                                                    └── officer_app:approve_cancel:{id}
///                                                         └─→ dismiss ephemeral, no-op
///
/// ── Eligibility gates (enforced on every action) ──
///   1. Reviewer must hold the HQ role (or Administrator)
///   2. Reviewer must NOT be the applicant — self-review is blocked
///   3. Application must still be in Pending state — races where two
///      reviewers click simultaneously resolve to "already reviewed"
///      for the second clicker
///   4. Application must still exist in the database
///
/// ── DM delivery ──
///   DMs are best-effort. If the applicant has server DMs disabled
///   (HttpException with code 50007), the review still completes — the
///   DB row is updated, the dossier embed is updated, and the reviewing
///   officer gets an ephemeral note that the DM bounced.
///
/// ── Dossier message lost ──
///   If application.DossierMessageId no longer resolves (manual delete),
///   the review still completes against the database, and a fallback
///   text summary is posted to the HQ channel so the audit trail isn't
///   broken. The original applicant DM still fires.
/// </summary>
public sealed class OfficerApplicationReviewHandler
{
    private const string ButtonPrefix       = "officer_app";
    private const string ActionApprove      = "approve";
    private const string ActionDeny         = "deny";
    private const string ActionConfirmAppr  = "approve_confirm";
    private const string ActionCancelAppr   = "approve_cancel";
    private const string ActionDenyModal    = "deny_modal";
    private const string DenyReasonInputId  = "deny_reason";

    private static readonly Color ApprovedColor = new(0x4A, 0xC9, 0x59);
    private static readonly Color DeniedColor   = new(0xC9, 0x42, 0x42);

    private readonly IServiceProvider _services;
    private readonly OfficerApplicationDossierBuilder _dossier;
    private readonly ILogger<OfficerApplicationReviewHandler> _logger;
    private readonly BotConfig _config;

    public OfficerApplicationReviewHandler(
        IServiceProvider services,
        OfficerApplicationDossierBuilder dossier,
        ILogger<OfficerApplicationReviewHandler> logger,
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

    /// <summary>
    /// Builds the approve / deny action row attached to a freshly-posted
    /// dossier. Static so OfficerApplicationModalHandler can call it
    /// without taking a dependency on this handler instance.
    /// </summary>
    public static MessageComponent BuildPendingButtons(int applicationId) =>
        new ComponentBuilder()
            .WithButton("Approve",
                $"{ButtonPrefix}:{ActionApprove}:{applicationId}",
                ButtonStyle.Success,
                new Emoji("✅"))
            .WithButton("Deny",
                $"{ButtonPrefix}:{ActionDeny}:{applicationId}",
                ButtonStyle.Danger,
                new Emoji("❌"))
            .Build();

    // ─── Dispatch ─────────────────────────────────────────────────────

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (!TryParseCustomId(component.Data.CustomId, out var action, out var appId))
            return;

        try
        {
            switch (action)
            {
                case ActionApprove:        await HandleApproveClickAsync(component, appId); break;
                case ActionDeny:           await HandleDenyClickAsync(component, appId); break;
                case ActionConfirmAppr:    await HandleApproveConfirmAsync(component, appId); break;
                case ActionCancelAppr:     await HandleApproveCancelAsync(component); break;
                // ActionDenyModal is a modal CustomId, dispatched by OnModalSubmittedAsync
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error handling officer-app review button {Action} for app #{AppId}",
                action, appId);
            await TryRespondError(component);
        }
    }

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        if (!TryParseCustomId(modal.Data.CustomId, out var action, out var appId))
            return;
        if (action != ActionDenyModal) return;

        try
        {
            await HandleDenySubmitAsync(modal, appId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error handling officer-app denial submission for app #{AppId}", appId);
            try
            {
                if (modal.HasResponded)
                    await modal.FollowupAsync("Something went wrong recording the denial. Check the bot logs.", ephemeral: true);
                else
                    await modal.RespondAsync("Something went wrong recording the denial. Check the bot logs.", ephemeral: true);
            }
            catch { /* swallow */ }
        }
    }

    // ─── Approve flow ─────────────────────────────────────────────────

    private async Task HandleApproveClickAsync(SocketMessageComponent component, int appId)
    {
        var (verdict, application) = await PreflightAsync(component, appId);
        if (verdict is not null)
        {
            await component.RespondAsync(verdict, ephemeral: true);
            return;
        }

        // Ephemeral confirmation prompt — guards against fat-finger clicks
        // since the action triggers an immediate DM that can't be unsent.
        var applicantMention = $"<@{application!.UserId}>";
        var prompt = $"Approve application **#{application.Id}** from {applicantMention}?\n"
                   + $"This will DM them the decision.";

        var components = new ComponentBuilder()
            .WithButton("Confirm Approval",
                $"{ButtonPrefix}:{ActionConfirmAppr}:{appId}",
                ButtonStyle.Success,
                new Emoji("✅"))
            .WithButton("Cancel",
                $"{ButtonPrefix}:{ActionCancelAppr}:{appId}",
                ButtonStyle.Secondary)
            .Build();

        await component.RespondAsync(prompt, components: components, ephemeral: true);
    }

    private async Task HandleApproveConfirmAsync(SocketMessageComponent component, int appId)
    {
        await component.DeferAsync();

        var (verdict, application) = await PreflightAsync(component, appId);
        if (verdict is not null)
        {
            await component.ModifyOriginalResponseAsync(p =>
            {
                p.Content    = verdict;
                p.Components = new ComponentBuilder().Build();
            });
            return;
        }

        await FinalizeReviewAsync(
            component,
            application!,
            OfficerApplicationStatus.Approved,
            reviewNotes: null);
    }

    private async Task HandleApproveCancelAsync(SocketMessageComponent component)
    {
        await component.UpdateAsync(p =>
        {
            p.Content    = "Cancelled.";
            p.Components = new ComponentBuilder().Build();
        });
    }

    // ─── Deny flow ────────────────────────────────────────────────────

    private async Task HandleDenyClickAsync(SocketMessageComponent component, int appId)
    {
        var (verdict, application) = await PreflightAsync(component, appId);
        if (verdict is not null)
        {
            await component.RespondAsync(verdict, ephemeral: true);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle($"Deny Application #{application!.Id}")
            .WithCustomId($"{ButtonPrefix}:{ActionDenyModal}:{appId}")
            .AddTextInput(
                label: "Reason for denial (will be sent to applicant)",
                customId: DenyReasonInputId,
                style: TextInputStyle.Paragraph,
                placeholder: "Be specific and constructive — this is what they'll see.",
                minLength: 30,
                maxLength: 1000,
                required: true)
            .Build();

        await component.RespondWithModalAsync(modal);
    }

    private async Task HandleDenySubmitAsync(SocketModal modal, int appId)
    {
        await modal.DeferAsync(ephemeral: true);

        var (verdict, application) = await PreflightAsync(modal, appId);
        if (verdict is not null)
        {
            await modal.FollowupAsync(verdict, ephemeral: true);
            return;
        }

        var reason = modal.Data.Components
            .FirstOrDefault(c => c.CustomId == DenyReasonInputId)?.Value?.Trim();

        if (string.IsNullOrWhiteSpace(reason))
        {
            await modal.FollowupAsync("Denial reason is required.", ephemeral: true);
            return;
        }

        await FinalizeReviewAsync(
            modal,
            application!,
            OfficerApplicationStatus.Denied,
            reviewNotes: reason);
    }

    // ─── Common finalize ──────────────────────────────────────────────

    /// <summary>
    /// Writes the review row, updates the dossier embed in place (or posts
    /// a fallback if the dossier is gone), DMs the applicant, and informs
    /// the reviewing officer of the outcome.
    /// </summary>
    private async Task FinalizeReviewAsync(
        SocketInteraction interaction,
        OfficerApplication application,
        OfficerApplicationStatus newStatus,
        string? reviewNotes)
    {
        var reviewer = interaction.User as SocketUser;
        if (reviewer is null)
        {
            await SafeFollowupAsync(interaction, "Could not resolve reviewer identity.");
            return;
        }

        // ── Persist ─────────────────────────────────────────────────
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var row = await db.OfficerApplications.FirstOrDefaultAsync(a => a.Id == application.Id);
        if (row is null)
        {
            await SafeFollowupAsync(interaction, "Application disappeared from the database. Refresh and try again.");
            return;
        }

        // Last-second race check — a concurrent reviewer may have flipped
        // status in the time between PreflightAsync and now.
        if (row.Status != OfficerApplicationStatus.Pending)
        {
            await SafeFollowupAsync(interaction,
                $"⛔ This application was already {row.Status.ToString().ToLower()} by another reviewer.");
            return;
        }

        var reviewerDisplayName = (reviewer as SocketGuildUser)?.DisplayName ?? reviewer.Username;

        row.Status              = newStatus;
        row.ReviewedAt          = DateTime.UtcNow;
        row.ReviewedByUserId    = reviewer.Id;
        row.ReviewedByUsername  = reviewerDisplayName;
        row.ReviewNotes         = reviewNotes;
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Officer application #{Id} {Status} by {Reviewer} ({ReviewerId})",
            row.Id, newStatus, reviewerDisplayName, reviewer.Id);

        // ── Update dossier embed in place (or fallback) ─────────────
        await UpdateDossierAsync(interaction, row, reviewer);

        // ── DM applicant ────────────────────────────────────────────
        var dmOutcome = await DmApplicantAsync(interaction, row, reviewer);

        // ── Confirm to reviewer ─────────────────────────────────────
        var dmNote = dmOutcome switch
        {
            DmOutcome.Sent       => "Applicant has been notified.",
            DmOutcome.Blocked    => "⚠️ Applicant has DMs disabled — please reach out to them directly.",
            DmOutcome.UserGone   => "⚠️ Applicant has left the server — no DM sent.",
            DmOutcome.Failed     => "⚠️ DM delivery failed — please reach out to the applicant directly.",
            _                    => string.Empty,
        };

        var verb = newStatus == OfficerApplicationStatus.Approved ? "approved" : "denied";
        var emoji = newStatus == OfficerApplicationStatus.Approved ? "✅" : "❌";
        var summary = $"{emoji} Application **#{row.Id}** {verb}. {dmNote}";

        await SafeFinalizeResponseAsync(interaction, summary);
    }

    private async Task UpdateDossierAsync(
        SocketInteraction interaction,
        OfficerApplication application,
        SocketUser reviewer)
    {
        var guild = (interaction.Channel as SocketGuildChannel)?.Guild;
        if (guild is null) return;

        var hqChannel = _config.OfficerAppHqChannelId != 0
            ? guild.GetTextChannel(_config.OfficerAppHqChannelId)
            : null;
        if (hqChannel is null) return;

        IUserMessage? dossierMessage = null;
        if (application.DossierMessageId is { } messageId)
        {
            try { dossierMessage = await hqChannel.GetMessageAsync(messageId) as IUserMessage; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not fetch dossier message {MessageId} for review of app #{AppId}",
                    messageId, application.Id);
            }
        }

        if (dossierMessage is not null && dossierMessage.Embeds.Count > 0)
        {
            var reviewedEmbed = _dossier.BuildReviewedEmbedFromExisting(
                dossierMessage.Embeds.First(), application, reviewer);

            await dossierMessage.ModifyAsync(p =>
            {
                p.Embed      = reviewedEmbed;
                p.Components = new ComponentBuilder().Build();
            });
        }
        else
        {
            // Fallback per Phase 3 decision #8 — original dossier is gone,
            // so post a summary line so the audit trail isn't broken.
            var statusWord = application.Status == OfficerApplicationStatus.Approved ? "approved" : "denied";
            var emoji      = application.Status == OfficerApplicationStatus.Approved ? "✅" : "❌";
            var fallback = $"{emoji} Application **#{application.Id}** for <@{application.UserId}> "
                         + $"was {statusWord} by {reviewer.Mention} "
                         + $"<t:{new DateTimeOffset(application.ReviewedAt!.Value, TimeSpan.Zero).ToUnixTimeSeconds()}:R>. "
                         + "_(original dossier message was deleted)_";

            await hqChannel.SendMessageAsync(
                fallback,
                allowedMentions: AllowedMentions.None);
        }
    }

    private async Task<DmOutcome> DmApplicantAsync(
        SocketInteraction interaction,
        OfficerApplication application,
        SocketUser reviewer)
    {
        var guild = (interaction.Channel as SocketGuildChannel)?.Guild;
        if (guild is null) return DmOutcome.Failed;

        var applicant = guild.GetUser(application.UserId);
        if (applicant is null) return DmOutcome.UserGone;

        var body = BuildDmBody(application, reviewer);

        try
        {
            var dm = await applicant.CreateDMChannelAsync();
            await dm.SendMessageAsync(body, allowedMentions: AllowedMentions.None);
            return DmOutcome.Sent;
        }
        catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
        {
            _logger.LogInformation(
                "Applicant {UserId} for app #{AppId} has DMs disabled — review still recorded",
                application.UserId, application.Id);
            return DmOutcome.Blocked;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to DM applicant {UserId} for app #{AppId}",
                application.UserId, application.Id);
            return DmOutcome.Failed;
        }
    }

    private static string BuildDmBody(OfficerApplication application, SocketUser reviewer)
    {
        if (application.Status == OfficerApplicationStatus.Approved)
        {
            return "🎉 **Your officer application has been approved.**\n\n"
                 + $"Welcome to the officer corps. {reviewer.Mention} will follow up with next steps.\n\n"
                 + "_— 189th HQ_";
        }

        var reason = string.IsNullOrWhiteSpace(application.ReviewNotes)
            ? "_(no reason provided)_"
            : application.ReviewNotes;

        return "Your officer application has been reviewed and we've decided not to move forward at this time.\n\n"
             + "**Reason from HQ:**\n"
             + $"> {reason.Replace("\n", "\n> ")}\n\n"
             + "You're welcome to reapply in the future as you continue to contribute.\n\n"
             + "_— 189th HQ_";
    }

    // ─── Preflight (eligibility / lookup) ─────────────────────────────

    /// <summary>
    /// Returns (verdictMessage, application). If verdictMessage is non-null,
    /// the caller should respond with it and abort. If null, the application
    /// is loaded and eligible for review.
    /// </summary>
    private async Task<(string? Verdict, OfficerApplication? Application)> PreflightAsync(
        SocketInteraction interaction, int appId)
    {
        var user = interaction.User as SocketGuildUser;
        if (user is null)
            return ("This action only works inside the server.", null);

        // Gate 1 — HQ role
        if (!HasHqPermission(user))
            return ("⛔ Only HQ can review officer applications.", null);

        // Gate 2 — application exists and is still Pending
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var application = await db.OfficerApplications
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appId);

        if (application is null)
            return ($"⛔ Application #{appId} no longer exists in the database.", null);

        if (application.Status != OfficerApplicationStatus.Pending)
        {
            var statusWord = application.Status.ToString().ToLower();
            var reviewedBy = application.ReviewedByUsername ?? "another reviewer";
            return ($"⛔ This application was already **{statusWord}** by {reviewedBy}. "
                  + "Refresh the channel to see the updated dossier.", null);
        }

        // Gate 3 — not self-review
        if (application.UserId == user.Id)
            return ("⛔ You cannot review your own application.", null);

        return (null, application);
    }

    private bool HasHqPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;
        if (_config.OfficerAppHqRoleId == 0) return false;
        return user.Roles.Any(r => r.Id == _config.OfficerAppHqRoleId);
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static bool TryParseCustomId(string customId, out string action, out int appId)
    {
        action = string.Empty;
        appId  = 0;
        var parts = customId.Split(':');
        if (parts.Length != 3 || parts[0] != ButtonPrefix) return false;
        if (!int.TryParse(parts[2], out appId)) return false;
        action = parts[1];
        return true;
    }

    private async Task TryRespondError(SocketMessageComponent component)
    {
        try
        {
            if (component.HasResponded)
                await component.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            else
                await component.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
        }
        catch { /* swallow */ }
    }

    private static async Task SafeFollowupAsync(SocketInteraction interaction, string message)
    {
        try { await interaction.FollowupAsync(message, ephemeral: true); }
        catch { /* swallow */ }
    }

    /// <summary>
    /// Updates the ephemeral approval-confirmation message, or follows up
    /// on a deferred modal interaction, depending on which type fired.
    /// </summary>
    private static async Task SafeFinalizeResponseAsync(SocketInteraction interaction, string message)
    {
        try
        {
            switch (interaction)
            {
                case SocketMessageComponent comp:
                    // Approve flow: deferred earlier with DeferAsync(), update
                    // the original ephemeral to show the outcome and clear buttons.
                    await comp.ModifyOriginalResponseAsync(p =>
                    {
                        p.Content    = message;
                        p.Components = new ComponentBuilder().Build();
                    });
                    break;

                case SocketModal modal:
                    // Deny flow: deferred earlier with DeferAsync(ephemeral: true),
                    // follow up with the outcome.
                    await modal.FollowupAsync(message, ephemeral: true);
                    break;
            }
        }
        catch { /* swallow */ }
    }

    private enum DmOutcome
    {
        Sent,
        Blocked,
        UserGone,
        Failed,
    }
}