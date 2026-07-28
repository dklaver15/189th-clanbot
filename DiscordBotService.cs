using ClanGuardBot.Handlers;
using ClanGuardBot.Models;
using ClanGuardBot.PatrolWatch;
using ClanGuardBot.RedditLeads;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Hosted service that manages the Discord client lifecycle —
/// connecting, registering event handlers, and registering slash commands.
/// </summary>
public class DiscordBotService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly ActivityTrackingHandler _activityHandler;
    private readonly AfkExemptionHandler _afkExemptionHandler;
    private readonly GamertagCommandHandler _gamertagHandler;
    private readonly GamertagWizard _gamertagWizard;
    private readonly GamertagSetupCommandHandler _gamertagSetupHandler;
    private readonly GamertagBackfillCommandHandler _gamertagBackfillHandler;
    private readonly LookupCommandHandler _lookupHandler;
    private readonly PromoEligibilityCommandHandler _promoEligibilityHandler;
    private readonly LateCheckCommandHandler _lateCheckHandler;
    private readonly RankTrackingHandler _rankHandler;
    private readonly MemberLifecycleHandler _memberLifecycleHandler;
    private readonly DepartureCaptureHandler _departureCaptureHandler;
    private readonly MemberRosterReconciler _memberRosterReconciler;
    private readonly AccountAgeGateHandler _accountAgeGateHandler;
    private readonly InviteLinkFilterHandler _inviteLinkFilterHandler;
    private readonly AuditLogWatcherHandler _auditLogWatcherHandler;
    private readonly NicknameImpersonationHandler _nicknameImpersonationHandler;
    private readonly WebhookAuditCommandHandler _webhookAuditCommandHandler;
    private readonly TokenGrabberScannerHandler _tokenGrabberScannerHandler;
    private readonly HoneypotHandler _honeypotHandler;
    private readonly BanHammerHandler _banHammerHandler;
    private readonly TicketReminderHandler _ticketReminderHandler;
    private readonly GuestReminderHandler _guestReminderHandler;
    private readonly OnboardingReminderHandler _onboardingReminderHandler;
    private readonly BumpReminderHandler _bumpReminderHandler;
    private readonly ApolloEventHandler _apolloEventHandler;
    private readonly ApolloMessageCaptureHandler _apolloCaptureHandler;
    private readonly CompEventCommandHandler _compEventHandler;
    private readonly EventCommandHandler _eventCommandHandler;
    private readonly EventCreationWizard _eventWizard;
    private readonly ReminderCommandHandler _reminderCommandHandler;
    private readonly ReminderCreationWizard _reminderWizard;
    private readonly EventRsvpInteractionHandler _eventRsvpHandler;
    private readonly EventManagementHandler _eventMgmtHandler;
    private readonly EventTemplateHandler _eventTemplateHandler;
    private readonly QotdCommandHandler _qotdCommandHandler;
    private readonly JotdCommandHandler _jotdCommandHandler;
    private readonly PollCommandHandler _pollCommandHandler;
    private readonly PollVoteInteractionHandler _pollVoteHandler;
    private readonly PollGatewayVoteHandler _pollGatewayVoteHandler;
    private readonly UfcCommandHandler _ufcCommandHandler;
    private readonly FinalsCommandHandler _finalsCommandHandler;
    private readonly XpCommandHandler _xpCommandHandler;
    private readonly PalworldCommandHandler _palworldCommandHandler;
    private readonly SatisfactoryCommandHandler _satisfactoryCommandHandler;
    private readonly PromoteCommandHandler _promoteHandler;
    private readonly DemoteCommandHandler _demoteHandler;
    private readonly SetNickCommandHandler _setNickHandler;
    private readonly BotFixChannelPermsCommandHandler _botFixChannelPermsHandler;
    private readonly SeedPromotionCreditCommandHandler _seedHandler;
    private readonly EventCreditCommandHandler _eventCreditHandler;
    private readonly AttendanceCommandHandler _attendanceHandler;
    private readonly SquadCommandHandler _squadHandler;
    private readonly KickAwolsCommandHandler _kickAwolsHandler;
    private readonly ClearAwolListCommandHandler _clearAwolListHandler;
    private readonly PurgeUserCommandHandler _purgeUserHandler;
    private readonly AllowNewAccountCommandHandler _allowNewAccountHandler;
    private readonly BriefingNowCommandHandler _briefingNowHandler;
    private readonly CleanupCalendarDupesCommandHandler _cleanupCalendarDupesHandler;
    private readonly CalendarCommandHandler _calendarHandler;
    private readonly CommandsCommandHandler _commandsHandler;
    private readonly HealthCommandHandler _healthHandler;
    private readonly TimelineCommandHandler _timelineHandler;
    private readonly CommandUsageTrackingHandler _commandUsageHandler;
    private readonly UsageStatsCommandHandler _usageStatsHandler;
    private readonly SecurityAuditCommandHandler _securityAuditHandler;
    private readonly InviteAttributionService _inviteAttributionService;
    private readonly InviteCommandHandler _inviteCommandHandler;
    private readonly RedditLeadButtonHandler _redditLeadButtonHandler;
    private readonly RedditLeadsCommandHandler _redditLeadsCommandHandler;
    private readonly PatrolWatchCommandHandler _patrolWatchCommandHandler;
    private readonly PatrolNameCommandHandler _patrolNameCommandHandler;
    private readonly OfficerApplicationSetupCommandHandler _officerAppSetupHandler;
    private readonly OfficerApplicationModalHandler _officerAppModalHandler;
    private readonly OfficerApplicationReviewHandler _officerAppReviewHandler;
    private readonly TicketPanelCommandHandler _ticketPanelHandler;
    private readonly TicketInteractionHandler _ticketInteractionHandler;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly BotConfig _config;

    public DiscordBotService(
        DiscordSocketClient client,
        ActivityTrackingHandler activityHandler,
        AfkExemptionHandler afkExemptionHandler,
        GamertagCommandHandler gamertagHandler,
        GamertagWizard gamertagWizard,
        GamertagSetupCommandHandler gamertagSetupHandler,
        GamertagBackfillCommandHandler gamertagBackfillHandler,
        LookupCommandHandler lookupHandler,
        PromoEligibilityCommandHandler promoEligibilityHandler,
        LateCheckCommandHandler lateCheckHandler,
        RankTrackingHandler rankHandler,
        MemberLifecycleHandler memberLifecycleHandler,
        DepartureCaptureHandler departureCaptureHandler,
        MemberRosterReconciler memberRosterReconciler,
        AccountAgeGateHandler accountAgeGateHandler,
        InviteLinkFilterHandler inviteLinkFilterHandler,
        AuditLogWatcherHandler auditLogWatcherHandler,
        NicknameImpersonationHandler nicknameImpersonationHandler,
        WebhookAuditCommandHandler webhookAuditCommandHandler,
        TokenGrabberScannerHandler tokenGrabberScannerHandler,
        HoneypotHandler honeypotHandler,
        BanHammerHandler banHammerHandler,
        TicketReminderHandler ticketReminderHandler,
        GuestReminderHandler guestReminderHandler,
        OnboardingReminderHandler onboardingReminderHandler,
        BumpReminderHandler bumpReminderHandler,
        ApolloEventHandler apolloEventHandler,
        ApolloMessageCaptureHandler apolloCaptureHandler,
        CompEventCommandHandler compEventHandler,
        EventCommandHandler eventCommandHandler,
        EventCreationWizard eventWizard,
        ReminderCommandHandler reminderCommandHandler,
        ReminderCreationWizard reminderWizard,
        EventRsvpInteractionHandler eventRsvpHandler,
        EventManagementHandler eventMgmtHandler,
        EventTemplateHandler eventTemplateHandler,
        QotdCommandHandler qotdCommandHandler,
        JotdCommandHandler jotdCommandHandler,
        PollCommandHandler pollCommandHandler,
        PollVoteInteractionHandler pollVoteHandler,
        PollGatewayVoteHandler pollGatewayVoteHandler,
        UfcCommandHandler ufcCommandHandler,
        FinalsCommandHandler finalsCommandHandler,
        XpCommandHandler xpCommandHandler,
        PalworldCommandHandler palworldCommandHandler,
        SatisfactoryCommandHandler satisfactoryCommandHandler,
        PromoteCommandHandler promoteHandler,
        DemoteCommandHandler demoteHandler,
        SetNickCommandHandler setNickHandler,
        BotFixChannelPermsCommandHandler botFixChannelPermsHandler,
        SeedPromotionCreditCommandHandler seedHandler,
        EventCreditCommandHandler eventCreditHandler,
        AttendanceCommandHandler attendanceHandler,
        SquadCommandHandler squadHandler,
        KickAwolsCommandHandler kickAwolsHandler,
        ClearAwolListCommandHandler clearAwolListHandler,
        PurgeUserCommandHandler purgeUserHandler,
        AllowNewAccountCommandHandler allowNewAccountHandler,
        BriefingNowCommandHandler briefingNowHandler,
        CleanupCalendarDupesCommandHandler cleanupCalendarDupesHandler,
        CalendarCommandHandler calendarHandler,
        CommandsCommandHandler commandsHandler,
        HealthCommandHandler healthHandler,
        TimelineCommandHandler timelineHandler,
        CommandUsageTrackingHandler commandUsageHandler,
        UsageStatsCommandHandler usageStatsHandler,
        SecurityAuditCommandHandler securityAuditHandler,
        InviteAttributionService inviteAttributionService,
        InviteCommandHandler inviteCommandHandler,
        RedditLeadButtonHandler redditLeadButtonHandler,
        RedditLeadsCommandHandler redditLeadsCommandHandler,
        PatrolWatchCommandHandler patrolWatchCommandHandler,
        PatrolNameCommandHandler patrolNameCommandHandler,
        OfficerApplicationSetupCommandHandler officerAppSetupHandler,
        OfficerApplicationModalHandler officerAppModalHandler,
        OfficerApplicationReviewHandler officerAppReviewHandler,
        TicketPanelCommandHandler ticketPanelHandler,
        TicketInteractionHandler ticketInteractionHandler,
        ILogger<DiscordBotService> logger,
        IOptions<BotConfig> config)
    {
        _client                      = client;
        _activityHandler             = activityHandler;
        _afkExemptionHandler         = afkExemptionHandler;
        _gamertagHandler             = gamertagHandler;
        _gamertagWizard              = gamertagWizard;
        _gamertagSetupHandler        = gamertagSetupHandler;
        _gamertagBackfillHandler     = gamertagBackfillHandler;
        _lookupHandler               = lookupHandler;
        _promoEligibilityHandler     = promoEligibilityHandler;
        _lateCheckHandler            = lateCheckHandler;
        _rankHandler                 = rankHandler;
        _memberLifecycleHandler      = memberLifecycleHandler;
        _departureCaptureHandler     = departureCaptureHandler;
        _memberRosterReconciler      = memberRosterReconciler;
        _accountAgeGateHandler       = accountAgeGateHandler;
        _inviteLinkFilterHandler     = inviteLinkFilterHandler;
        _auditLogWatcherHandler      = auditLogWatcherHandler;
        _nicknameImpersonationHandler = nicknameImpersonationHandler;
        _webhookAuditCommandHandler   = webhookAuditCommandHandler;
        _tokenGrabberScannerHandler   = tokenGrabberScannerHandler;
        _honeypotHandler             = honeypotHandler;
        _banHammerHandler            = banHammerHandler;
        _ticketReminderHandler       = ticketReminderHandler;
        _guestReminderHandler        = guestReminderHandler;
        _onboardingReminderHandler   = onboardingReminderHandler;
        _bumpReminderHandler         = bumpReminderHandler;
        _apolloEventHandler          = apolloEventHandler;
        _apolloCaptureHandler        = apolloCaptureHandler;
        _compEventHandler            = compEventHandler;
        _eventCommandHandler         = eventCommandHandler;
        _eventWizard                 = eventWizard;
        _reminderCommandHandler      = reminderCommandHandler;
        _reminderWizard              = reminderWizard;
        _eventRsvpHandler            = eventRsvpHandler;
        _eventMgmtHandler            = eventMgmtHandler;
        _eventTemplateHandler        = eventTemplateHandler;
        _qotdCommandHandler          = qotdCommandHandler;
        _jotdCommandHandler          = jotdCommandHandler;
        _pollCommandHandler          = pollCommandHandler;
        _pollVoteHandler             = pollVoteHandler;
        _pollGatewayVoteHandler      = pollGatewayVoteHandler;
        _ufcCommandHandler           = ufcCommandHandler;
        _finalsCommandHandler        = finalsCommandHandler;
        _xpCommandHandler            = xpCommandHandler;
        _palworldCommandHandler      = palworldCommandHandler;
        _satisfactoryCommandHandler  = satisfactoryCommandHandler;
        _promoteHandler              = promoteHandler;
        _demoteHandler               = demoteHandler;
        _setNickHandler              = setNickHandler;
        _botFixChannelPermsHandler   = botFixChannelPermsHandler;
        _seedHandler                 = seedHandler;
        _eventCreditHandler          = eventCreditHandler;
        _attendanceHandler           = attendanceHandler;
        _squadHandler                = squadHandler;
        _kickAwolsHandler            = kickAwolsHandler;
        _clearAwolListHandler        = clearAwolListHandler;
        _purgeUserHandler            = purgeUserHandler;
        _allowNewAccountHandler      = allowNewAccountHandler;
        _briefingNowHandler          = briefingNowHandler;
        _cleanupCalendarDupesHandler = cleanupCalendarDupesHandler;
        _calendarHandler             = calendarHandler;
        _commandsHandler             = commandsHandler;
        _healthHandler               = healthHandler;
        _timelineHandler             = timelineHandler;
        _commandUsageHandler         = commandUsageHandler;
        _usageStatsHandler           = usageStatsHandler;
        _securityAuditHandler        = securityAuditHandler;
        _inviteAttributionService    = inviteAttributionService;
        _inviteCommandHandler        = inviteCommandHandler;
        _redditLeadButtonHandler     = redditLeadButtonHandler;
        _redditLeadsCommandHandler   = redditLeadsCommandHandler;
        _patrolWatchCommandHandler   = patrolWatchCommandHandler;
        _patrolNameCommandHandler    = patrolNameCommandHandler;
        _officerAppSetupHandler      = officerAppSetupHandler;
        _officerAppModalHandler      = officerAppModalHandler;
        _officerAppReviewHandler     = officerAppReviewHandler;
        _ticketPanelHandler          = ticketPanelHandler;
        _ticketInteractionHandler    = ticketInteractionHandler;
        _logger                      = logger;
        _config                      = config.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log    += LogAsync;
        _client.Ready  += OnReadyAsync;

        _activityHandler.Register(_client);
        _afkExemptionHandler.Register(_client);
        _gamertagHandler.Register(_client);
        _gamertagWizard.Register(_client);
        _gamertagSetupHandler.Register(_client);
        _gamertagBackfillHandler.Register(_client);
        _lookupHandler.Register(_client);
        _promoEligibilityHandler.Register(_client);
        _lateCheckHandler.Register(_client);
        _rankHandler.Register(_client);
        _memberLifecycleHandler.Register(_client);
        _departureCaptureHandler.Register(_client);
        _memberRosterReconciler.Register(_client);
        _accountAgeGateHandler.Register(_client);
        _inviteLinkFilterHandler.Register(_client);
        _auditLogWatcherHandler.Register(_client);
        _nicknameImpersonationHandler.Register(_client);
        _webhookAuditCommandHandler.Register(_client);
        _tokenGrabberScannerHandler.Register(_client);
        _honeypotHandler.Register(_client);
        _banHammerHandler.Register(_client);
        _ticketReminderHandler.Register(_client);
        _guestReminderHandler.Register(_client);
        _onboardingReminderHandler.Register(_client);
        _bumpReminderHandler.Register(_client);

        // Phase 3 cutover: when UseNewApolloPipeline is true, the parser worker
        // + CalendarOutboxWorker own the Apollo→GCal path. ApolloEventHandler
        // stays instantiated for emergency rollback (flip the flag back to
        // false and restart) but never wires up its gateway subscriptions.
        if (!_config.UseNewApolloPipeline)
        {
            _apolloEventHandler.Register(_client);
        }
        else
        {
            _logger.LogInformation(
                "UseNewApolloPipeline=true; ApolloEventHandler.Register skipped. " +
                "ApolloMessageParserWorker + CalendarOutboxWorker own the Apollo→GCal path.");
        }

        _apolloCaptureHandler.Register(_client);
        _compEventHandler.Register(_client);
        _eventCommandHandler.Register(_client);
        _eventWizard.Register(_client);
        _reminderCommandHandler.Register(_client);
        _reminderWizard.Register(_client);
        _eventRsvpHandler.Register(_client);
        _eventMgmtHandler.Register(_client);
        _eventTemplateHandler.Register(_client);
        _qotdCommandHandler.Register(_client);
        _jotdCommandHandler.Register(_client);
        _pollCommandHandler.Register(_client);
        _pollVoteHandler.Register(_client);
        _pollGatewayVoteHandler.Register(_client);
        _ufcCommandHandler.Register(_client);
        _finalsCommandHandler.Register(_client);
        _xpCommandHandler.Register(_client);
        _palworldCommandHandler.Register(_client);
        _satisfactoryCommandHandler.Register(_client);
        _promoteHandler.Register(_client);
        _demoteHandler.Register(_client);
        _setNickHandler.Register(_client);
        _botFixChannelPermsHandler.Register(_client);
        _seedHandler.Register(_client);
        _eventCreditHandler.Register(_client);
        _attendanceHandler.Register(_client);
        _squadHandler.Register(_client);
        _kickAwolsHandler.Register(_client);
        _clearAwolListHandler.Register(_client);
        _purgeUserHandler.Register(_client);
        _allowNewAccountHandler.Register(_client);
        _briefingNowHandler.Register(_client);
        _cleanupCalendarDupesHandler.Register(_client);
        _calendarHandler.Register(_client);
        _commandsHandler.Register(_client);
        _healthHandler.Register(_client);
        _timelineHandler.Register(_client);
        _commandUsageHandler.Register(_client);
        _usageStatsHandler.Register(_client);
        _securityAuditHandler.Register(_client);
        _inviteAttributionService.Register(_client);
        _inviteCommandHandler.Register(_client);
        _redditLeadButtonHandler.Register(_client);
        _redditLeadsCommandHandler.Register(_client);
        _patrolWatchCommandHandler.Register(_client);
        _patrolNameCommandHandler.Register(_client);
        _officerAppSetupHandler.Register(_client);
        _officerAppModalHandler.Register(_client);
        _officerAppReviewHandler.Register(_client);
        _ticketPanelHandler.Register(_client);
        _ticketInteractionHandler.Register(_client);

        await _client.LoginAsync(TokenType.Bot, _config.Token);
        await _client.StartAsync();

        _logger.LogInformation("Discord bot started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        _logger.LogInformation("Discord bot stopped");
    }

    private async Task OnReadyAsync()
    {
        _logger.LogInformation("Bot is connected as {BotUser} to {GuildCount} guild(s)",
            _client.CurrentUser, _client.Guilds.Count);

        try
        {
            // Clear any stale global commands
            await _client.BulkOverwriteGlobalApplicationCommandsAsync(
                Array.Empty<ApplicationCommandProperties>());

            var commands = new[]
            {
                new SlashCommandBuilder()
                    .WithName("awol-status")
                    .WithDescription("Show your current activity stats for this server")
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("awol-check")
                    .WithDescription("Check another user's activity stats (Officer+ only)")
                    .AddOption("user", ApplicationCommandOptionType.User,
                        "The user to check", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("clear-awol")
                    .WithDescription("Clear a user's AWOL status — removes role and pending notifications (Officer+ only)")
                    .AddOption("user", ApplicationCommandOptionType.User,
                        "The user to clear", isRequired: true)
                    .Build(),

                // /gamertags — member-facing. Opens the DM wizard that walks
                // them through their six platform tags one at a time (replaces
                // the old two-page modal). The "Enter Gamertags" button opens
                // the same wizard. Handled by GamertagCommandHandler.
                GamertagCommandHandler.BuildCommand(),

                // /setup-gamertags — posts (or re-posts with force:true) the
                // persistent "Enter Gamertags" button to the configured
                // instructions channel. HQ-gated via BotConfig.GamertagSetupRoleId.
                GamertagSetupCommandHandler.BuildCommand(),

                // /gamertag-backfill-ids — officer-run maintenance: fills missing
                // Discord IDs on legacy roster rows (dry-run unless apply:true).
                GamertagBackfillCommandHandler.BuildCommand(),

                new SlashCommandBuilder()
                    .WithName("lookup")
                    .WithDescription("Look up someone's gamertags from the roster")
                    .AddOption("user", ApplicationCommandOptionType.User,
                        "The clan member to look up", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("promo-eligibility")
                    .WithDescription("Check a member's auto-promotion eligibility")
                    .AddOption("user", ApplicationCommandOptionType.User,
                        "The clan member to check", isRequired: true)
                    .Build(),

                // /late-check — how often a member showed up late to events they
                // organized or hosted (MAJ+). Handled by LateCheckCommandHandler.
                // NOTE: keep CommandsCommandHandler.BuildCatalog in sync.
                LateCheckCommandHandler.BuildCommand(),

                new SlashCommandBuilder()
                    .WithName("roster-export")
                    .WithDescription("Manually trigger a roster export to Google Sheets (Officer+ only)")
                    .Build(),

                // NOTE: /recruit was removed — new recruits are auto-logged by
                // RankTrackingHandler when they gain the RCT role.

                new SlashCommandBuilder()
                    .WithName("comp-event")
                    .WithDescription($"Create a competitive division event on the clan calendar ({_config.CompEventMinRank}+ only)")
                    .Build(),

                // In-house event creation (Apollo replacement). /event opens a
                // DM wizard (gated by EventCommandMinRank); /timezone sets the
                // zone used to read each member's event-time input.
                EventCommandHandler.BuildEventCommand(_config.EventCommandMinRank, _config.EventTemplateManageMinRank),
                EventCommandHandler.BuildTimezoneCommand(),

                // /reminder — officer-gated scheduled announcements
                // (create/list/cancel/edit). DM wizard + ReminderSchedulerService.
                // NOTE: keep CommandsCommandHandler.BuildCatalog in sync.
                ReminderCommandHandler.BuildCommand(_config.ReminderCommandMinRank),

                // /qotd — Question of the Day. Opens a DM wizard (gated by
                // QotdMinRank, default SGT) to collect the question, then posts an
                // embed with the 189th logo to QotdChannelId. NOTE: keep
                // CommandsCommandHandler.BuildCatalog in sync.
                QotdCommandHandler.BuildCommand(_config.QotdMinRank),

                // /jotd — Joke of the Day. Opens a DM wizard (gated by
                // JotdMinRank, default SGT) to collect the joke, then posts an
                // embed with the 189th logo to JotdChannelId. NOTE: keep
                // CommandsCommandHandler.BuildCatalog in sync.
                JotdCommandHandler.BuildCommand(_config.JotdMinRank),

                // /poll — native + anonymous hybrid poll, open to all members.
                PollCommandHandler.BuildCommand(),

                UfcCommandHandler.BuildScheduleCommand(),
                UfcCommandHandler.BuildResultsCommand(),

                // /finals-rank — look up a player's standing on THE FINALS ranked
                // leaderboard (self, a clan member via roster gamertags, or a
                // free-text name search). Open to all members; public reply.
                // Gated at runtime by BotConfig.FinalsEnabled. Handled by
                // FinalsCommandHandler. NOTE: keep CommandsCommandHandler.BuildCatalog
                // in sync when changing /finals-rank.
                FinalsCommandHandler.BuildCommand(),

                // /finals-club — show a club's ranked players (filtered from the
                // cached top-10k leaderboard by club tag). Open to all members;
                // public reply; gated by BotConfig.FinalsEnabled. Same handler as
                // /finals-rank. NOTE: keep CommandsCommandHandler.BuildCatalog in sync.
                FinalsCommandHandler.BuildClubCommand(),

                // /xp, /xp-leaderboard — the seasonal clan XP ladder. Open to all
                // members. XP is a recognition/engagement layer only: it does NOT
                // feed promotions, which stay on event-attendance credit and
                // officer judgement. Runtime-gated by BotConfig.XpEnabled.
                // NOTE: keep CommandsCommandHandler.BuildCatalog in sync.
                XpCommandHandler.BuildXpCommand(),
                XpCommandHandler.BuildLeaderboardCommand(),

                // /xp-season — status is open to all; start/end is gated to
                // XpAdminMinRank (default MAJ) and requires confirm:true because
                // it resets everyone's season XP. /xp-adjust is a manual grant or
                // deduction with a mandatory reason, same gate. Both handled by
                // XpCommandHandler. NOTE: keep CommandsCommandHandler.BuildCatalog
                // in sync.
                XpCommandHandler.BuildSeasonCommand(_config.XpAdminMinRank),
                XpCommandHandler.BuildAdjustCommand(_config.XpAdminMinRank),

                // /xp-dms — per-member switch for the level-up DM. Open to all
                // members; the same thing the "Stop these DMs" button on the DM
                // does, but usable to turn them back ON. NOTE: keep
                // CommandsCommandHandler.BuildCatalog in sync.
                XpCommandHandler.BuildDmsCommand(),

                // /palworld-* — the clan's Palworld server on DatHost, via the
                // game's built-in REST API (RCON is deprecated upstream and unused).
                // status/playtime/leaderboard/link are open to all members;
                // /palworld-admin (announce, kick, ban, unban, save, restart) is
                // gated to the "Palworld Mod" role (BotConfig.PalworldAdminRoleId)
                // so the mods can moderate the game server without anyone holding
                // the Admin Password. All are runtime-gated by BotConfig.PalworldEnabled.
                // Handled by PalworldCommandHandler. NOTE: keep
                // CommandsCommandHandler.BuildCatalog in sync when changing these.
                PalworldCommandHandler.BuildStatusCommand(),
                PalworldCommandHandler.BuildPlaytimeCommand(),
                PalworldCommandHandler.BuildLeaderboardCommand(),
                PalworldCommandHandler.BuildLinkCommand(),
                PalworldCommandHandler.BuildPerformanceCommand(),
                PalworldCommandHandler.BuildNameCommand(),
                PalworldCommandHandler.BuildAdminCommand(),

                // /satisfactory-* — the clan's Satisfactory Dedicated Server via its
                // built-in HTTPS API (same port as the game, self-signed cert). All of
                // these are read-only and open to all members; the write commands
                // (/satisfactory-admin save|restart|command) were removed 2026-07-28 in
                // favour of the host's own Discord bot, whose restart works on a crashed
                // server. Runtime-gated by BotConfig.SatisfactoryEnabled. Handled by
                // SatisfactoryCommandHandler.
                // /satisfactory-mods additionally needs the Ficsit Remote Monitoring
                // mod (BotConfig.FrmEnabled + FrmBaseUrl) — the game API can't list
                // mods — and says so rather than failing when FRM is off.
                // NOTE: keep CommandsCommandHandler.BuildCatalog in sync.
                SatisfactoryCommandHandler.BuildStatusCommand(),
                SatisfactoryCommandHandler.BuildModsCommand(),
                SatisfactoryCommandHandler.BuildReportCommand(),
                SatisfactoryCommandHandler.BuildPlaytimeCommand(),
                SatisfactoryCommandHandler.BuildLeaderboardCommand(),
                SatisfactoryCommandHandler.BuildGraphCommand(),
                SatisfactoryCommandHandler.BuildProductionCommand(),
                SatisfactoryCommandHandler.BuildLinkCommand(),

                new SlashCommandBuilder()
                    .WithName("promote")
                    .WithDescription("Promote a member to the next rank")
                    .AddOption("rank", ApplicationCommandOptionType.String,
                        "The rank to promote to (e.g. pvt, pfc, sgt)", isRequired: true)
                    .AddOption("member", ApplicationCommandOptionType.User,
                        "The member to promote", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("demote")
                    .WithDescription("Demote a member to a lower rank")
                    .AddOption("rank", ApplicationCommandOptionType.String,
                        "The rank to demote to (e.g. rct, pvt, pfc)", isRequired: true)
                    .AddOption("member", ApplicationCommandOptionType.User,
                        "The member to demote", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("setnick")
                    .WithDescription("Change a member's nickname (Officer+ only)")
                    .AddOption("member", ApplicationCommandOptionType.User,
                        "The member to rename", isRequired: true)
                    .AddOption("nickname", ApplicationCommandOptionType.String,
                        "The new nickname", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName(BotFixChannelPermsCommandHandler.CommandName)
                    .WithDescription("Grant the bot Manage Channel on every channel (dry-run by default) — Admin only")
                    .AddOption("dry_run", ApplicationCommandOptionType.Boolean,
                        "Preview without applying. Default true; pass false to apply.", isRequired: false)
                    .AddOption("skip_categories", ApplicationCommandOptionType.String,
                        "Comma-separated category names or IDs to exclude (e.g. Statbot Statdocks)", isRequired: false)
                    .Build(),

                // One-time operational command: reads the "Seed Events" column
                // from the roster sheet and writes those counts into
                // RankHistory.EventsAttendedAtRankBeforeBot so the bot's promotion
                // math includes events that were tracked on the manual
                // spreadsheet before the bot became authoritative. Takes no
                // options — operates on whatever is in the roster sheet.
                new SlashCommandBuilder()
                    .WithName("seed-promotion-credit")
                    .WithDescription($"Apply Seed Events from the roster sheet to the DB ({_config.PromoteDemoteMinRank}+ only)")
                    .Build(),

                // Manual event-credit adjustment commands (CPT+). Add inserts a
                // sentinel EventAttendance row; remove deletes the most recent
                // sentinel row at the user's current rank. Real attendance
                // history is never modified by these commands.
                new SlashCommandBuilder()
                    .WithName("add-event-credit")
                    .WithDescription("Manually add 1 event credit to a member at their current rank (CPT+ only)")
                    .AddOption("member", ApplicationCommandOptionType.User,
                        "The member to credit", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("remove-event-credit")
                    .WithDescription("Remove 1 manual event credit from a member at their current rank (CPT+ only)")
                    .AddOption("member", ApplicationCommandOptionType.User,
                        "The member to debit", isRequired: true)
                    .Build(),

                // /attendance — show today's clan-event attendance grouped by
                // event. Pulls from CalendarEvents + EventAttendance and
                // distinguishes "snapshot pending" from "0 qualifying" using
                // CalendarEvent.LastSnapshotAttemptUtc. Manual credits added
                // today appear in a separate section. Ephemeral, MAJ+ gated.
                new SlashCommandBuilder()
                    .WithName("attendance")
                    .WithDescription("Show recent clan event attendance, grouped by event (MAJ+ only)")
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName("days")
                            .WithDescription("How many days back to include (default 1, max 30)")
                            .WithType(ApplicationCommandOptionType.Integer)
                            .WithRequired(false)
                            .WithMinValue(1)
                            .WithMaxValue(30))
                    .Build(),

                // /squads — randomize everyone currently in the events VC into
                // 4-man squads for Vendetta setup. Officer+ gated (mirrors
                // SlashCommandHandler.HasElevatedPermissions — SyncWithHandlers:
                // SquadCommandHandler.HasElevatedPermissions +
                // CommandsCommandHandler.BuildCatalog). Ephemeral so the running
                // officer can reroll repeatedly to balance teams before calling
                // out party assignments; the 🎲 Reroll button doesn't need an
                // invoker restriction because ephemeral messages are only
                // visible to (and clickable by) the original invoker.
                new SlashCommandBuilder()
                    .WithName(SquadCommandHandler.CommandName)
                    .WithDescription("Randomize everyone in the events VC into squads (Officer+ only)")
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName("size")
                            .WithDescription("Players per squad (default 4)")
                            .WithType(ApplicationCommandOptionType.Integer)
                            .WithRequired(false)
                            .WithMinValue(1)
                            .WithMaxValue(30))
                    .Build(),

                // /kick-awols — bulk-kick all members holding the AWOL role.
                // Multiple safety guards: Reserve role, min rank, role hierarchy.
                // Per-member audit rows written to AwolKickAuditRecords.
                // Defaults to neither confirm nor dry-run; one of the two
                // booleans must be passed explicitly.
                new SlashCommandBuilder()
                    .WithName("kick-awols")
                    .WithDescription($"Kick all members currently flagged AWOL ({_config.AwolKickMinRank}+ only)")
                    .AddOption("confirm", ApplicationCommandOptionType.Boolean,
                        "Set to true to actually kick members", isRequired: false)
                    .AddOption("dry-run", ApplicationCommandOptionType.Boolean,
                        "Set to true to preview without kicking (recommended first)", isRequired: false)
                    .Build(),

                // /clear-awol-list — wipe the HQ channel so the reviewing
                // officer doesn't have to scroll through stale embeds.
                // Destructive: requires confirm:true.
                new SlashCommandBuilder()
                    .WithName("clear-awol-list")
                    .WithDescription($"Delete all messages in #{_config.HqChannelName} ({_config.AwolKickMinRank}+ only)")
                    .AddOption("confirm", ApplicationCommandOptionType.Boolean,
                        "Set to true to confirm deletion", isRequired: false)
                    .Build(),

                // /purge-user — Admin-only. Deletes a member's most recent
                // messages server-wide (default 100, override with count:).
                // Runs a dry-run preview unless confirm:true. Pinned messages
                // are preserved. NOTE: keep CommandsCommandHandler.BuildCatalog
                // in sync.
                PurgeUserCommandHandler.BuildCommand(),

                // /allow-new-account — Admin-only escape hatch for the account-age
                // gate. Writes a one-time exemption for a specific user and lifts
                // any existing ban so a legitimate brand-new account can be
                // unbanned and rejoin without being auto-banned again. The gate
                // consumes the exemption on their next join. NOTE: keep
                // CommandsCommandHandler.BuildCatalog in sync.
                AllowNewAccountCommandHandler.BuildCommand(),

                // /briefing-now — manually trigger the weekly officer briefing
                // out of band from the Sunday cron. Honours the same DryRun
                // config flag as the scheduled run, so officers can validate
                // the pipeline without posting to HQ.
                new SlashCommandBuilder()
                    .WithName("briefing-now")
                    .WithDescription($"Generate the weekly officer briefing immediately ({_config.BriefingNowMinRank}+ only)")
                    .AddOption(
                        "start_date",
                        ApplicationCommandOptionType.String,
                        "Window start, YYYY-MM-DD (Eastern). Pass with end_date; omit both for the trailing 7 days.",
                        isRequired: false)
                    .AddOption(
                        "end_date",
                        ApplicationCommandOptionType.String,
                        "Window end, YYYY-MM-DD (Eastern). Matches the date range shown in the briefing title.",
                        isRequired: false)
                    .Build(),

                // /cleanup-calendar-dupes — reconcile CalendarEvents and the
                // Google Calendar against each other, removing duplicates
                // created by past concurrent-write races between
                // MessageReceived and MessageUpdated for the same Apollo post
                // (the 2026-05-04 / 2026-05-05 incident). dry_run defaults to
                // true; pass dry_run:false to actually apply changes. Officer+
                // gated. The slash-command shape lives on the handler so the
                // option list stays next to the code that consumes it.
                CleanupCalendarDupesCommandHandler.BuildCommand(),

                // /calendar — ephemeral day-grouped view of upcoming events
                // on the clan's Google Calendar. Open to all members; queries
                // GCal directly so it reflects manual edits made via the
                // calendar UI as well as bot-managed Apollo + CompDiv events.
                // Two render modes: list (default) and grid.
                new SlashCommandBuilder()
                    .WithName("calendar")
                    .WithDescription("Show upcoming events from the clan calendar")
                    .AddOption("days", ApplicationCommandOptionType.Integer,
                        "How many days ahead to show (1–30, default 7)",
                        isRequired: false)
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("view")
                        .WithDescription("Display style (default: list)")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false)
                        .AddChoice("List", "list")
                        .AddChoice("Grid (calendar style)", "grid"))
                    .Build(),

                // /commands — self-service catalog of every slash command,
                // filtered to only those the caller can run at their current
                // rank. Open to everyone; rendered ephemerally. Catalog of
                // entries lives in CommandsCommandHandler.BuildCatalog and
                // MUST be updated alongside this list when commands are added,
                // removed, or have their permission gate changed.
                // The optional `style` option flips between the desktop-friendly
                // code-block table and the mobile-friendly markdown list. Discord
                // remembers the last-used option value within a picker session so
                // mobile users only need to pick List once per sitting.
                new SlashCommandBuilder()
                    .WithName(CommandsCommandHandler.CommandName)
                    .WithDescription("Show every slash command available to you at your current rank")
                    .AddOption(new SlashCommandOptionBuilder()
                        .WithName("style")
                        .WithDescription("Display style (default: Table)")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithRequired(false)
                        .AddChoice("Table", "table")
                        .AddChoice("List",  "list"))
                    .Build(),

                // /invite — tracked invite link framework. Seven subcommands
                // (create, assign, edit-notes, list, info, stats, revoke) for
                // labeling invites and surfacing recruitment-source attribution.
                // Per-subcommand permission model lives on the handler; the
                // group itself is open. The slash-command shape lives on the
                // handler so the subcommand list stays next to the code that
                // consumes it. NOTE: also keep CommandsCommandHandler.BuildCatalog
                // in sync when this changes — that's the /commands self-service
                // catalog source of truth.
                InviteCommandHandler.BuildCommand(),

                // /my-invites — top-level, user-facing view of /invite list
                // filtered to invites the caller created. Open to everyone,
                // ephemeral. Same active/inactive/all filter as /invite list.
                // Handler lives on InviteCommandHandler (shares the row-build
                // + render pipeline with /invite list). NOTE: also keep
                // CommandsCommandHandler.BuildCatalog in sync when this changes.
                InviteCommandHandler.BuildMyInvitesCommand(),

                // /leads — Reddit recruitment lead funnel. Two subcommands
                // (stats, recent) for officers (MAJ+) to monitor what the
                // RedditLeadService has surfaced and the conversion rate
                // by subreddit. The button surface for individual leads
                // lives on the embeds posted to the leads channel; this
                // is the read-side dashboard. NOTE: keep CommandsCommandHandler.BuildCatalog
                // in sync when changing /leads.
                RedditLeadsCommandHandler.BuildCommand(),

                // /usage-stats — read side of the slash-command usage log
                // written by CommandUsageTrackingHandler. Three subcommands
                // (top, user, command) for officers (MAJ+) to see who's
                // actually exercising the bot's command surface and which
                // commands are getting traction. Pure analytics — NOT a
                // signal feeding AutoPromotionService. The slash-command
                // shape lives on the handler so the option list stays next
                // to the code that consumes it. NOTE: also keep
                // CommandsCommandHandler.BuildCatalog in sync when changing
                // /usage-stats.
                UsageStatsCommandHandler.BuildCommand(),

                // /security-audit — read side of the SecurityAuditRecords
                // table written by the server-protection feature set
                // (account-age gate today; raid shield / impersonation /
                // webhook audit / token-grabber scanner planned). Single
                // command with optional filters (user, feature, days,
                // limit) rather than subcommands — every read shape is
                // the same WHERE clause with different parameters. MAJ+
                // gated. The slash-command shape lives on the handler.
                // NOTE: also keep CommandsCommandHandler.BuildCatalog in
                // sync when changing /security-audit.
                SecurityAuditCommandHandler.BuildCommand(),

                // /webhook-audit — fresh-fetch + snapshot-reconciled inventory
                // of every webhook in the server. BG+ gated; the surface is
                // essentially a list of every backdoor into channels and
                // belongs at the same sensitivity class as /security-audit.
                // NOTE: keep CommandsCommandHandler.BuildCatalog in sync.
                WebhookAuditCommandHandler.BuildCommand(),

                // /patrol — Patrol Watch opt-out / info surface. Three
                // subcommands (off, on, info). Open to all members; rendered
                // ephemerally. The opt-out toggle is consumed by
                // PatrolWatchService.DoRecomputeAsync to filter the matched-
                // member set before posting the embed in #lfg. NOTE: also keep
                // CommandsCommandHandler.BuildCatalog in sync when changing
                // /patrol.
                PatrolWatchCommandHandler.BuildCommand(),

                // /patrol-name — officer command (Manage Nicknames) to set/clear
                // the canonical name shown for a member on Patrol Watch embeds,
                // working around Discord.Net's Nickname→GlobalName cache fallback.
                // Applies to every tracked game, not just Palworld. Handled by
                // PatrolNameCommandHandler. NOTE: keep CommandsCommandHandler.BuildCatalog
                // in sync when changing /patrol-name.
                PatrolNameCommandHandler.BuildCommand(),

                // /setup-officer-app — HQ-only command that posts (or re-posts
                // with force:true) the persistent "Apply for Officer" button
                // to the configured instructions channel. Phase 1 of the
                // officer application system. Idempotent via
                // BotState.OfficerAppButtonMessageId. NOTE: keep
                // CommandsCommandHandler.BuildCatalog in sync when this changes.
                OfficerApplicationSetupCommandHandler.BuildCommand(),

                // /ticket-panel — HQ-only. Posts (or re-posts with force:true)
                // the persistent ticket panel (category select menu) to
                // BotConfig.TicketCenterChannelId. The select menu, creation
                // modal, and claim/priority/close controls all live on
                // TicketInteractionHandler. NOTE: keep
                // CommandsCommandHandler.BuildCatalog in sync.
                TicketPanelCommandHandler.BuildCommand(),

                // /health — Officer+ diagnostic for uptime / DB / scheduled jobs / queues.
                // All data is read locally so the command works even when Drive / Sheets /
                // Calendar are degraded. NOTE: keep CommandsCommandHandler.BuildCatalog in
                // sync when changing /health.
                HealthCommandHandler.BuildCommand(),

                // /timeline — Officer+ command rendering a single member's full
                // history into one ephemeral embed. Pulls from many BotDbContext
                // tables, no external API calls. NOTE: keep
                // CommandsCommandHandler.BuildCatalog in sync when changing /timeline.
                TimelineCommandHandler.BuildCommand(),

                // /banhammer — manual repost / self-heal for the "days since
                // last ban" counter embed if it ever gets deleted. Officer+
                // gated. NOTE: keep CommandsCommandHandler.BuildCatalog in sync
                // when changing /banhammer.
                BanHammerHandler.BuildCommand(),
            };

            foreach (var guild in _client.Guilds)
            {
                await guild.BulkOverwriteApplicationCommandAsync(commands);
                _logger.LogInformation("Slash commands registered to guild {GuildName}", guild.Name);
            }

            _logger.LogInformation("Slash commands registered");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register slash commands");
        }
    }

    private Task LogAsync(LogMessage message)
    {
        var severity = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error    => LogLevel.Error,
            LogSeverity.Warning  => LogLevel.Warning,
            LogSeverity.Info     => LogLevel.Information,
            LogSeverity.Verbose  => LogLevel.Debug,
            LogSeverity.Debug    => LogLevel.Trace,
            _                    => LogLevel.Information
        };

        _logger.Log(severity, message.Exception, "[Discord] {Source}: {Message}",
            message.Source, message.Message);

        return Task.CompletedTask;
    }
}