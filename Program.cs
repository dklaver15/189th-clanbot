using ClanGuardBot.Briefing;
using ClanGuardBot.Data;
using ClanGuardBot.Handlers;
using ClanGuardBot.Models;
using ClanGuardBot.PatrolWatch;
using ClanGuardBot.RedditLeads;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Microsoft.Extensions.Options;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // EF Core logs every executed SQL statement at Information, which buries the
    // application's own log lines (and makes incident triage painful). Raise its
    // floor to Warning so we keep EF errors but drop the per-query spam.
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File("logs/clanguard-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information("Starting ClanGuard Bot...");

    var builder = Host.CreateApplicationBuilder(args);

    // Logging
    builder.Services.AddSerilog();

    // Configuration
    builder.Services.Configure<BotConfig>(builder.Configuration.GetSection(BotConfig.Section));

    // Discord client — singleton shared across services
    var discordConfig = new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds
                       | GatewayIntents.GuildMessages
                       | GatewayIntents.GuildMembers
                       | GatewayIntents.GuildVoiceStates
                       | GatewayIntents.GuildInvites
                       | GatewayIntents.GuildPresences
                       | GatewayIntents.GuildBans  // a.k.a. GuildModeration (Discord renamed it server-side; Discord.NET 3.17 keeps the old enum name). Required for AuditLogCreated.
                       | GatewayIntents.DirectMessages  // required for the /event DM creation wizard to receive DM replies
                       | GatewayIntents.GuildMessagePolls  // native /poll vote events (PollVoteAdded/Removed) for analytics capture
                       | GatewayIntents.MessageContent,
        AlwaysDownloadUsers = true,
        LogLevel            = LogSeverity.Info,
        // Cache the most recent 500 messages per channel so that MessageUpdated
        // fires reliably for Apollo event edits even after a short bot restart.
        MessageCacheSize    = 500
    };

    builder.Services.AddSingleton(discordConfig);
    builder.Services.AddSingleton<DiscordSocketClient>(sp =>
        new DiscordSocketClient(sp.GetRequiredService<DiscordSocketConfig>()));

    // Database
    var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
    Directory.CreateDirectory(dataDir);
    var dbPath = Path.Combine(dataDir, "clanguard.db");
    builder.Services.AddDbContext<BotDbContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));

    // ── Google Services ──────────────────────────────────────────────
    builder.Services.AddSingleton<GoogleSheetsService>();
    builder.Services.AddSingleton<GoogleCalendarService>();

    // ── Handlers (singleton so event registrations persist) ──────────
    builder.Services.AddSingleton<ActivityTrackingHandler>();
    builder.Services.AddSingleton<AfkExemptionHandler>();
    builder.Services.AddSingleton<SlashCommandHandler>();
    builder.Services.AddSingleton<RankTrackingHandler>();

    // MemberLifecycleHandler: hooks UserLeft (leaves, kicks, bans) to clean up
    // departing members — clears their RankHistory rows and removes their
    // gamertag roster-sheet row. Clearing RankHistory prevents stale AssignedAt
    // timestamps from prior memberships influencing auto-promotion when a former
    // member rejoins (PromotionService.GetRankInfoAsync also has a read-side
    // defense). Register() is called from DiscordBotService.
    builder.Services.AddSingleton<MemberLifecycleHandler>();

    // DepartureCaptureHandler: retention feature. Hooks UserLeft +
    // AuditLogCreated to record one MemberDeparture row per departure and
    // classify it (voluntary / officer kick / bot AWOL kick / account-age ban
    // / manual ban). Singleton so DepartureClassificationWorker shares its
    // in-memory correlation cache. Register() is called from DiscordBotService.
    // Requires the GuildBans gateway intent (already enabled above for the
    // audit-log watcher).
    builder.Services.AddSingleton<DepartureCaptureHandler>();

    // DepartureClassificationWorker: finalizes stale Pending departures to
    // "Left" once the grace window elapses (no Kick/Ban audit entry observed
    // ⇒ voluntary) and sweeps the correlation cache.
    builder.Services.AddHostedService<DepartureClassificationWorker>();

    // MemberRosterReconciler: self-healing for departures missed while the bot
    // was offline (Discord never replays UserLeft). Persists the present-member
    // roster (KnownMembers), hooks UserJoined to keep it fresh, and on startup
    // + every RetentionRosterReconcileHours diffs it against the live guild,
    // back-classifying any missing members via the REST audit log. Singleton +
    // hosted (same pattern as the reminder handlers); Register() is called from
    // DiscordBotService.
    builder.Services.AddSingleton<MemberRosterReconciler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MemberRosterReconciler>());

    // GamertagRosterReconciler: offline-gap cleanup for the gamertag roster
    // sheet. The live path (MemberLifecycleHandler on UserLeft) removes a row
    // the moment someone leaves; this sweep catches departures that happened
    // while the bot was offline. On startup + every RetentionRosterReconcileHours
    // it downloads the full member list and deletes sheet rows whose Discord ID
    // is no longer in the guild. Legacy rows without an ID are never deleted.
    // Disabled automatically when no roster spreadsheet is configured.
    builder.Services.AddHostedService<GamertagRosterReconciler>();


    // AccountAgeGateHandler: server-protection feature #1. Hooks UserJoined,
    // computes account age from user.CreatedAt, and either alerts or kicks
    // depending on BotConfig.AccountAgeGateMode (Off / AlertOnly / Kick).
    // Defaults to Off — flipping the switch is an explicit opt-in. Posts to
    // BotConfig.SecurityAlertsChannelId (falls back to HqChannelName lookup
    // when unset). Coexists cleanly with InviteAttributionService — both
    // subscribe to UserJoined independently and the kick path does not
    // invalidate invite-use attribution. Register() is called from
    // DiscordBotService.
    builder.Services.AddSingleton<AccountAgeGateHandler>();

    // InviteLinkFilterHandler: server-protection feature #2. Hooks
    // MessageReceived + MessageUpdated, scans for Discord invite URLs, and
    // either alerts or deletes-and-DMs depending on
    // BotConfig.InviteLinkFilterMode (Off / AlertOnly / Enforce). Defaults
    // to Enforce for everyone below BotConfig.InviteLinkFilterExemptMinRank
    // (default MAJ); Administrators and exempt-rank members bypass. Reuses
    // InviteCacheService to identify our own invite codes — every other
    // code is treated as external. Posts to BotConfig.SecurityAlertsChannelId
    // and writes one SecurityAuditRecord row per violation. Register() is
    // called from DiscordBotService.
    builder.Services.AddSingleton<InviteLinkFilterHandler>();

    // AuditLogWatcherHandler: server-protection feature #3. Hooks
    // Discord's AuditLogCreated gateway event, filters to high-signal
    // structural actions (channel deletes, bans, bot adds, role changes,
    // server-settings updates, webhook creates), filters out the bot's
    // own actions to keep the signal clean, and posts alerts to
    // SecurityAlertsChannelId. Critical actions get a @here prefix per
    // AuditLogWatcherCriticalMention. Alert-only for v1 — no
    // auto-response. Requires GuildBans gateway intent (added above;
    // a.k.a. GUILD_MODERATION on Discord's side) AND the View Audit Log
    // permission in the guild.
    builder.Services.AddSingleton<AuditLogWatcherHandler>();

    // NicknameImpersonationHandler: server-protection feature #4. Fires
    // on UserJoined and GuildMemberUpdated (display-name changes only)
    // and compares the suspect's display name against every CPT+
    // officer using a normalize → strip-rank-prefix → exact/Levenshtein/
    // substring match pipeline. Catches typos, Unicode lookalike
    // attacks (Cyrillic/Greek/digit substitution), and "drop the rank
    // prefix" attempts. Alert-only for v1.
    builder.Services.AddSingleton<NicknameImpersonationHandler>();

    // WebhookAuditService: server-protection feature #5. Periodic
    // (default every 6 hours) scan of every webhook in every guild,
    // diffed against the WebhookSnapshots table. Alerts on new /
    // deleted / changed webhooks unless they match the allowlist. The
    // Audit Log Watcher catches WebhookCreated in real time; this is
    // the catch-everything backstop and the persistence layer for
    // /webhook-audit. Requires Manage Webhooks guild permission.
    builder.Services.AddHostedService<WebhookAuditService>();

    // WebhookAuditCommandHandler: read side of the same feature. BG+
    // gated /webhook-audit slash command, lists all live webhooks
    // grouped by channel with first-seen info from the snapshot table.
    builder.Services.AddSingleton<WebhookAuditCommandHandler>();

    // PhishingDomainFeedService: BackgroundService that refreshes the
    // Sinking Yachts phishing-domain feed every 6 hours and exposes
    // it for O(1) lookup. Registered as singleton AND hosted so the
    // scanner handler can inject it while the host runs the loop.
    // AddHttpClient registers IHttpClientFactory which the service uses.
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<PhishingDomainFeedService>();
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<PhishingDomainFeedService>());

    // TokenGrabberScannerHandler: server-protection feature #6. Scans
    // every message and edit for URLs against the phishing feed +
    // officer blocklist + IP-address static pattern. AlertOnly for v1.
    builder.Services.AddSingleton<TokenGrabberScannerHandler>();

    // HoneypotHandler: server-protection feature #7. Subscribes to
    // MessageReceived (rolling index + trap) and Ready (auto-posted warning
    // embed). Behaviour gated by BotConfig.HoneypotMode (Off / AlertOnly /
    // Enforce). On an Enforce hit it bans the account, purges its messages
    // server-wide for the configured window, and bumps the embed's ban
    // counter. Register() is called from DiscordBotService.
    builder.Services.AddSingleton<HoneypotHandler>();

    // BanHammerHandler: the "days since last ban" counter. Subscribes to
    // AuditLogCreated (resets the counter when a human bans another human) and
    // Ready (posts/refreshes the persistent embed), plus a slow timer to climb
    // the day count between bans. Gated by BotConfig.BanHammerEnabled /
    // BanHammerChannelId. Register() is called from DiscordBotService.
    builder.Services.AddSingleton<BanHammerHandler>();

    // OnboardingReminderHandler: registered before GamertagCommandHandler because
    // the gamertag handler depends on it to notify when a Guest saves gamertags.
    builder.Services.AddSingleton<OnboardingReminderHandler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OnboardingReminderHandler>());

    // GamertagWizard: the DM state machine behind /gamertags (and the "Enter
    // Gamertags" button). Self-registers MessageReceived + ButtonExecuted, walks
    // the member through their six platform tags one at a time, and writes the
    // result to the roster sheet. Depends on OnboardingReminderHandler (above)
    // and GoogleSheetsService.
    builder.Services.AddSingleton<GamertagWizard>();

    // GamertagCommandHandler: the /gamertags slash command + "Enter Gamertags"
    // button — both open the DM wizard (GamertagWizard). Replaced the old
    // two-page modal flow.
    builder.Services.AddSingleton<GamertagCommandHandler>();

    // GamertagSetupCommandHandler: /setup-gamertags slash command (HQ-gated
    // via BotConfig.GamertagSetupRoleId or Administrator). Posts the
    // persistent "Enter Gamertags" button to the configured instructions
    // channel. Idempotent — re-running without force:true reports the
    // existing message via BotState.GamertagButtonMessageId. The button click
    // is handled by GamertagCommandHandler, which opens the DM wizard.
    builder.Services.AddSingleton<GamertagSetupCommandHandler>();

    // GamertagBackfillCommandHandler: /gamertag-backfill-ids — officer-run
    // maintenance command that fills missing Discord IDs on legacy roster rows
    // by matching names to current members (dry-run by default). Lets the
    // ID-keyed reconciler eventually manage those rows.
    builder.Services.AddSingleton<GamertagBackfillCommandHandler>();

    // LookupCommandHandler: /lookup slash command. Takes a Discord user picker
    // and returns that user's gamertags from the roster sheet (ephemeral, open
    // to any member). Register() is called from DiscordBotService alongside the
    // other command handlers.
    builder.Services.AddSingleton<LookupCommandHandler>();

    // PromoEligibilityCommandHandler: /promo-eligibility slash command, gated
    // to PromoEligibilityMinRank+ (default CPT). Returns a verdict on whether
    // a named member is eligible for auto-promotion at the next nightly run.
    // Computes from the same primitives AutoPromotionService uses so the
    // verdict matches the production decision path. Register() is called from
    // DiscordBotService alongside the other command handlers.
    builder.Services.AddSingleton<PromoEligibilityCommandHandler>();

    // NOTE: RecruitCommandHandler was removed — recruits are now auto-logged by
    // RankTrackingHandler when a member gains the RCT role. See TryLogRecruitAsync
    // in RankTrackingHandler.cs.

    // PromotionService: shared core used by both /promote and AutoPromotionService.
    // Registered before the consumers that inject it.
    builder.Services.AddSingleton<PromotionService>();

    builder.Services.AddSingleton<PromoteCommandHandler>();
    builder.Services.AddSingleton<DemoteCommandHandler>();
    builder.Services.AddSingleton<SetNickCommandHandler>();
    builder.Services.AddSingleton<BotFixChannelPermsCommandHandler>();

    // SeedPromotionCreditCommandHandler: one-time /seed-promotion-credit command
    // that reads the "Seed Events" column from the roster sheet and writes those
    // values into RankHistory.EventsAttendedAtRankBeforeBot (plus SeedAppliedAt)
    // so pre-existing spreadsheet-tracked event counts carry over to the bot.
    // Register() is called from DiscordBotService alongside the other command handlers.
    builder.Services.AddSingleton<SeedPromotionCreditCommandHandler>();

    // EventCreditCommandHandler: ad-hoc /add-event-credit and /remove-event-credit
    // commands for officers (CPT+) to nudge a member's bot-tracked event count up
    // or down at their current rank. Inserts/deletes rows in EventAttendance with
    // CalendarEventId = 0 as the "manual adjustment" marker; never touches real
    // attendance history.
    builder.Services.AddSingleton<EventCreditCommandHandler>();

    // AttendanceCommandHandler: /attendance slash command. Shows today's clan-event
    // attendance grouped by event, with explicit "snapshot pending" / "in progress"
    // states for events that haven't been processed yet. Manual /add-event-credit
    // rows added today appear in their own section. MAJ+ gated, ephemeral.
    builder.Services.AddSingleton<AttendanceCommandHandler>();

    // SquadCommandHandler: /squads slash command. Pulls everyone currently in the
    // events VC (BotConfig.EventsVoiceChannelId), randomizes them into 4-man squads
    // for Vendetta, and posts the draw ephemerally with a 🎲 Reroll button so the
    // running officer can reshuffle until the teams look fair. Officer+ gated
    // (SyncWithHandlers: SquadCommandHandler.HasElevatedPermissions matches
    // SlashCommandHandler.HasElevatedPermissions). Register() is called from
    // DiscordBotService alongside the other command handlers.
    builder.Services.AddSingleton<SquadCommandHandler>();

    // OfficerApplicationSetupCommandHandler: /setup-officer-app slash command
    // (HQ-only). Posts the persistent "Apply for Officer" button to the
    // configured instructions channel. Idempotent — re-running without
    // force:true reports the existing message via BotState.OfficerAppButtonMessageId.
    // Phase 1 of the officer application system. Phase 2 adds the modal + dossier
    // flow; Phase 3 adds approve/deny review buttons.
    builder.Services.AddSingleton<OfficerApplicationSetupCommandHandler>();

    // OfficerApplicationDossierBuilder: pure(-ish) builder service that
    // assembles the dossier embed from ClanGuard data (rank, tenure, AWOL
    // status, event attendance + voice + message windows). Also exposes
    // BuildReviewedEmbedFromExisting for Phase 3's in-place embed flip on
    // approve/deny. Used by OfficerApplicationModalHandler (initial post)
    // and OfficerApplicationReviewHandler (review). Does not subscribe to
    // Discord events.
    builder.Services.AddSingleton<OfficerApplicationDossierBuilder>();

    // OfficerApplicationModalHandler: owns the button-click → modal flow
    // and the modal-submit → dossier-post pipeline. Subscribes to both
    // ButtonExecuted (officer_app:open) and ModalSubmitted (officer_app:submit).
    // Enforces SGT+ eligibility, not-currently-AWOL, and duplicate-pending
    // rejection at both gates (button click + modal submit) since a member's
    // state can change between the two.
    builder.Services.AddSingleton<OfficerApplicationModalHandler>();

    // OfficerApplicationReviewHandler: owns the HQ review pipeline.
    // Approve/Deny buttons on dossier → confirmation prompt or denial-reason
    // modal → DB update → in-place dossier embed flip (color + title + review
    // field) → applicant DM. Phase 3 of the officer application system.
    // Self-review blocked; HQ-only; race-protected against simultaneous
    // reviewers; DM failures degrade gracefully without rolling back the
    // database state.
    builder.Services.AddSingleton<OfficerApplicationReviewHandler>();

    // KickAwolsCommandHandler: /kick-awols slash command for officers
    // (AwolKickMinRank+). Iterates over members with the AWOL role and removes
    // them from the server, with multiple safety guards (Reserve role, min rank,
    // role hierarchy) and per-member audit rows written to AwolKickAuditRecords.
    builder.Services.AddSingleton<KickAwolsCommandHandler>();

    // ClearAwolListCommandHandler: /clear-awol-list slash command for officers
    // (AwolKickMinRank+). Wipes the awol-list HQ channel so the reviewing
    // officer doesn't have to scroll through months of stale embeds.
    builder.Services.AddSingleton<ClearAwolListCommandHandler>();

    // TicketReminderHandler: singleton for event registration + hosted service for startup recovery.
    builder.Services.AddSingleton<TicketReminderHandler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TicketReminderHandler>());

    // GuestReminderHandler: same pattern.
    builder.Services.AddSingleton<GuestReminderHandler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<GuestReminderHandler>());

    // BumpReminderHandler: listens for Disboard /bump success embeds and posts
    // a reminder to the bump channel when the cooldown window expires without
    // anyone bumping. Same singleton + hosted-service pattern as the other
    // reminder handlers; Register(client) is called from DiscordBotService.
    builder.Services.AddSingleton<BumpReminderHandler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BumpReminderHandler>());

    // DiscordStatusMonitorService: polls discordstatus.com's Statuspage
    // JSON API every couple of minutes and posts new incident updates
    // (investigating / identified / monitoring / resolved) to the
    // configured channel with an @here ping. First-run sync seeds the
    // dedupe table without posting historical incidents. Shares
    // IHttpClientFactory with PhishingDomainFeedService (AddHttpClient
    // is already registered above). Mode-gated via
    // BotConfig.DiscordStatusMonitorEnabled.
    builder.Services.AddHostedService<DiscordStatusMonitorService>();

    // ApolloEventHandler: parses #events channel posts and syncs to Google Calendar.
    builder.Services.AddSingleton<ApolloEventHandler>();

    // Phase 1: lossless capture of every Apollo message (parallel to ApolloEventHandler).
    builder.Services.AddSingleton<ApolloMessageCaptureHandler>();

    // Phase 2: parser worker drains ApolloMessageLog into ApolloEvents staging table.
    builder.Services.AddHostedService<ApolloMessageParserWorker>();

    // Phase 3: drains CalendarOutbox to Google Calendar with retry + backoff.
    // Runs unconditionally; the outbox is only populated when
    // UseNewApolloPipeline=true, so it's a cheap no-op otherwise (one indexed
    // SELECT every 10 seconds against an empty table).
    builder.Services.AddHostedService<CalendarOutboxWorker>();

    // Phase 4: one-shot startup reconciliation of CalendarEvents against the
    // live #events channel — removes orphaned rows whose source Apollo message
    // is gone, and (rebind-aware) re-binds events that were re-posted under a
    // new ID while the bot was down (e.g. a /sort during downtime) instead of
    // deleting them. Must be registered for offline-gap recovery to actually
    // run; without this line it never executes.
    builder.Services.AddHostedService<ApolloReconciliationService>();

    // /calendar: ephemeral day-grouped view of upcoming events from Google Calendar.
    builder.Services.AddSingleton<CalendarCommandHandler>();

    // ── Tracked-invite framework (Phase 1+) ─────────────────────────
    // Three services compose the feature:
    //   • InviteCacheService — in-memory snapshot of every invite's use count
    //     plus the vanity URL counter, hydrated on Ready, refreshed on each
    //     UserJoined diff. Singleton (state lives across requests).
    //   • InviteAttributionService — gateway event handler. Runs the diff
    //     math when UserJoined fires and writes an InviteJoin row.
    //   • InviteCommandHandler — /invite slash command surface.
    // Wired into DiscordBotService.Register for slash + button events.
    builder.Services.AddSingleton<InviteCacheService>();
    builder.Services.AddSingleton<InviteAttributionService>();
    builder.Services.AddSingleton<InviteCommandHandler>();

    // CompEventCommandHandler: /comp-event slash command for CPT+ officers.
    builder.Services.AddSingleton<CompEventCommandHandler>();

    // ── In-house events (Apollo replacement) ─────────────────────────────
    //   • EventTimeParser — natural-language time → UTC (creator-tz aware).
    //   • IEventPublisher — seam to the persistence/posting layer. Currently a
    //     logging scaffold so the wizard can be exercised end-to-end; swap for
    //     the real EventPublisher next slice (no wizard changes).
    //   • EventCreationWizard — DM session state machine (self-registers
    //     MessageReceived + ButtonExecuted).
    //   • EventCommandHandler — /event + /timezone slash commands.
    builder.Services.AddSingleton<EventTimeParser>();
    builder.Services.AddSingleton<EventChannelGate>(); // shared per-channel lock (sorter + reconciliation)
    builder.Services.AddSingleton<EventChannelSorter>();
    builder.Services.AddSingleton<EventPublisher>();
    builder.Services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<EventPublisher>());
    builder.Services.AddSingleton<EventCreationWizard>();
    builder.Services.AddSingleton<EventTemplateHandler>();
    builder.Services.AddSingleton<EventCommandHandler>();
    builder.Services.AddSingleton<EventManagementHandler>();
    builder.Services.AddSingleton<EventRsvpInteractionHandler>();
    builder.Services.AddHostedService<EventReminderService>();
    builder.Services.AddHostedService<EventRecurrenceScheduler>();
    builder.Services.AddHostedService<EventArchiveService>();
    builder.Services.AddHostedService<ClanEventReconciliationService>(); // self-heals lost #events posts

    // /poll — the native + anonymous hybrid poll system. PollPublisher posts the
    // poll (native Discord poll or our custom anonymous embed); PollCommandHandler
    // owns the slash command; PollVoteInteractionHandler handles anonymous vote +
    // close buttons; PollGatewayVoteHandler captures native poll votes off the
    // gateway (needs GuildMessagePolls intent); PollClosingService closes + announces
    // (shared by the Close button and the timed sweep); PollCloseService is the sweep.
    builder.Services.AddSingleton<PollPublisher>();
    builder.Services.AddSingleton<PollClosingService>();
    builder.Services.AddSingleton<PollCommandHandler>();
    builder.Services.AddSingleton<PollVoteInteractionHandler>();
    builder.Services.AddSingleton<PollGatewayVoteHandler>();
    builder.Services.AddHostedService<PollCloseService>();

    // ── UFC / MMA ────────────────────────────────────────────────────
    // UfcApiService is the single API-Sports MMA client (shared cache); UfcEventPosterService
    // resolves official event posters from Wikipedia; UfcCommandHandler owns
    // /ufc-schedule + /ufc-results (Register() called from DiscordBotService);
    // UfcReminderService posts the day-before fight reminder. All keyed off
    // BotConfig.Ufc* — idle until a key + channel are set.
    builder.Services.AddSingleton<UfcApiService>();
    builder.Services.AddSingleton<UfcEventPosterService>();
    builder.Services.AddSingleton<UfcCommandHandler>();
    builder.Services.AddHostedService<UfcReminderService>();

    // ── Hosted Services ──────────────────────────────────────────────
    builder.Services.AddHostedService<DiscordBotService>();
    builder.Services.AddHostedService<HistoryBackfillService>();
    builder.Services.AddHostedService<ApolloBackfillService>();
    builder.Services.AddHostedService<AwolCheckService>();

    // VoiceSessionCleanupService: one-shot service that runs at startup to close
    // stuck voice sessions (LeftAt IS NULL rows that don't match current Discord
    // voice state). Prevents orphaned sessions from inflating activity totals.
    builder.Services.AddHostedService<VoiceSessionCleanupService>();

    // RankChangeBackfillService: one-shot service that seeds the RankChange
    // append-only log from existing RankHistory rows the first time the
    // rank-history-chain feature deploys. After the initial seed, the query
    // returns nothing and the service is a no-op. See its class comment for
    // the idempotency-via-NOT-EXISTS design.
    builder.Services.AddHostedService<RankChangeBackfillService>();

    // EventAttendanceSnapshotService: timer-driven (default 5 min) + startup
    // catch-up pass. Writes EventAttendance rows shortly after each clan event
    // ends. Calendar entries are preserved as historical record per the
    // "Apollo cleanup is not cancellation" policy in
    // ApolloEventHandler.HandleMessageDeletedAsync, so CalendarEvents
    // referenced here continue to exist for the lifetime of the EventAttendance
    // rows that point at them. Consumed by AutoPromotionService for SGT+
    // eligibility checks.
    builder.Services.AddHostedService<EventAttendanceSnapshotService>();

    // MeetingAttendanceSnapshotService: mirror of EventAttendanceSnapshotService
    // for meeting attendance. Snapshots time spent in BotConfig.MeetingVoiceChannelId
    // (15-min threshold by default) against the same Apollo-derived CalendarEvents.
    // Opt-in via config — when MeetingVoiceChannelId == 0, the service exits
    // immediately on startup. Writes MeetingAttendance rows; those rows are
    // folded into EventAttendanceHelper.CountEventsAttendedAsync so meetings
    // "count as events" for promotion math.
    builder.Services.AddHostedService<MeetingAttendanceSnapshotService>();

    // MeetingRecordingScheduler: the scheduling "brain" for meeting recordings.
    // Finds the next meeting by regex-matching CalendarEvent.Title
    // (BotConfig.MeetingTitlePattern), follows reschedules/renames, posts the
    // recording-consent notice, and drives the recorder via
    // IMeetingRecorderController. Opt-in via BotConfig.MeetingRecordingEnabled.
    // The controller is the @discordjs/voice sidecar in production; until that
    // exists, LoggingMeetingRecorderController is a logging no-op so the
    // scheduler runs end-to-end against real CalendarEvent data.
    // Recorder controller selection: use the HTTP sidecar when
    // BotConfig.MeetingRecorderBaseUrl is set, otherwise the logging no-op.
    // This lets the scheduler run before the sidecar exists, and lets you
    // deploy the code first and activate recording later just by setting the
    // base URL — no code change needed.
    var recorderBaseUrl = builder.Configuration
        .GetSection(BotConfig.Section)[nameof(BotConfig.MeetingRecorderBaseUrl)];
    if (string.IsNullOrWhiteSpace(recorderBaseUrl))
        builder.Services.AddSingleton<IMeetingRecorderController, LoggingMeetingRecorderController>();
    else
        builder.Services.AddSingleton<IMeetingRecorderController, HttpMeetingRecorderController>();
    builder.Services.AddHostedService<MeetingRecordingScheduler>();

    // MeetingMinutesService: the back half of the pipeline. Picks up recordings
    // the scheduler parked in Transcribing, runs them through the faster-whisper
    // transcriber sidecar, asks Claude for minutes + action items, posts them,
    // and prunes old audio to the keep-last-N window. Self-gates on
    // BotConfig.MeetingTranscriberBaseUrl (no-op until the sidecar is configured).
    builder.Services.AddSingleton<IMeetingTranscriber, HttpMeetingTranscriber>();
    builder.Services.AddHostedService<MeetingMinutesService>();

    // AutoPromotionService: nightly check that auto-promotes RCT → SGT based on
    // time-in-rank + activity thresholds (msgs/voice for RCT→CPL; event attendance
    // for CPL→SGT). Respects the AutoPromotionEnabled, AutoPromotionDryRun, and
    // AutoPromotionDryRunRanks config flags.
    builder.Services.AddHostedService<AutoPromotionService>();

    // RosterExportService: singleton so SlashCommandHandler can inject it,
    // + hosted service so its background loop runs automatically.
    builder.Services.AddSingleton<RosterExportService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RosterExportService>());

    // SqliteBackupService: nightly VACUUM INTO → gzip → encrypted upload of
    // the SQLite DB. Stamps BotState.LastSqliteBackup* on completion. Surfaced
    // on /health. Requires BackupEnabled=true and the configured storage
    // provider to be reachable. See SqliteBackupService class comment for the
    // full setup.
    //
    // Storage backend is selected at startup from BotConfig.BackupStorageProvider.
    // Both implementations are registered so a config flip is enough to
    // switch providers — no rebuild required.
    builder.Services.AddSingleton<GoogleDriveBackupClient>();
    builder.Services.AddSingleton<R2BackupClient>();
    builder.Services.AddSingleton<IBackupStorageClient>(sp =>
    {
        var cfg = sp.GetRequiredService<IOptions<BotConfig>>().Value;
        return cfg.BackupStorageProvider switch
        {
            BackupStorageProvider.R2          => sp.GetRequiredService<R2BackupClient>(),
            BackupStorageProvider.GoogleDrive => sp.GetRequiredService<GoogleDriveBackupClient>(),
            _ => throw new InvalidOperationException(
                $"Unknown BackupStorageProvider value: {cfg.BackupStorageProvider}"),
        };
    });
    builder.Services.AddHostedService<SqliteBackupService>();

    // HeartbeatService: outbound liveness ping to a configured URL (e.g.
    // Healthchecks.io) every HeartbeatIntervalSeconds. Only pings while the
    // Discord gateway is connected, so a process that's alive but offline
    // from Discord's perspective will trip the monitor's grace window.
    // Configured via BotConfig__HeartbeatPingUrl (set in docker-compose from
    // CLANGUARD_HEARTBEAT_URL on the droplet's .env). See HeartbeatService
    // class comment for the full design rationale.
    builder.Services.AddHostedService<HeartbeatService>();

    // GatewayWatchdogService: detects a wedged/stalled gateway (process alive
    // but no inbound gateway activity) and exits so Docker restarts the bot for
    // a clean reconnect. Complements HeartbeatService, which can't see this
    // state because it trusts ConnectionState. See GatewayWatchdogService.
    builder.Services.AddHostedService<GatewayWatchdogService>();

    // HealthCommandHandler: /health diagnostic. Officer+ gated. Reads from
    // BotState + BotDbContext only — no external API calls, so /health works
    // even when Drive / Sheets / Calendar are degraded.
    builder.Services.AddSingleton<HealthCommandHandler>();

    // TimelineCommandHandler: /timeline slash command for officers (Officer+).
    // Renders a single member's complete history — joins, invite attribution,
    // rank assignment, officer applications, AWOL flags, AWOL kicks, security
    // audit hits, recent events — into one ephemeral embed. Read-only;
    // pulls from BotDbContext + DiscordSocketClient only, no external API
    // calls. NOTE: also keep CommandsCommandHandler.BuildCatalog in sync
    // when changing /timeline.
    builder.Services.AddSingleton<TimelineCommandHandler>();

    // MemberActivityChartRenderer: produces PNG byte arrays of per-member
    // daily-activity charts for inline attachment to /timeline responses.
    // Backed by ScottPlot + SkiaSharp (see ClanGuardBot.csproj for the
    // package references and Dockerfile for the matching font/runtime
    // packages). Returns null on any failure so /timeline degrades to
    // text-only gracefully.
    builder.Services.AddSingleton<MemberActivityChartRenderer>();

    // ── AI / Weekly Briefing ─────────────────────────────────────────
    // WeeklyOfficerBriefingService: scheduled background service that posts a
    // weekly officer briefing to the HQ channel using Claude Sonnet 4.6.
    // Registers IAiService (Claude HTTP client), IBriefingDataCollector, and
    // itself as both a singleton + hosted service. Configuration sections:
    //   "Claude"         { ApiKey, Model }
    //   "WeeklyBriefing" { OfficerChannelId, RunOnDayUtc, RunAtUtc, DryRun, MaxOutputTokens }
    // The API key is loaded from env var Claude__ApiKey in production (set by
    // docker-compose from the ANTHROPIC_API_KEY GitHub Actions secret).
    builder.Services.AddWeeklyOfficerBriefing(builder.Configuration);

    // BriefingNowCommandHandler: /briefing-now slash command for officers
    // (AwolKickMinRank+). Triggers WeeklyOfficerBriefingService.RunBriefingNowAsync
    // immediately so officers can run the briefing on demand without waiting
    // for the Sunday cron. Honours the same DryRun config flag as the scheduled run.
    builder.Services.AddSingleton<BriefingNowCommandHandler>();

    // CleanupCalendarDupesCommandHandler: /cleanup-calendar-dupes slash command
    // for officers (Officer+). Two-pass reconciliation that removes duplicate
    // calendar entries created by past concurrent-write races between
    // MessageReceived and MessageUpdated for the same Apollo post (the
    // 2026-05-04 / 2026-05-05 incident). The per-message lock in
    // ApolloEventHandler prevents new dupes; this command cleans up historical
    // damage. dry_run defaults to true. Pass 1 handles in-DB dupes (deletes DB
    // rows + corresponding GCal events). Pass 2 handles GCal orphans (events
    // ApolloBackfill's older defensive cleanup left behind without removing
    // their GCal counterpart).
    builder.Services.AddSingleton<CleanupCalendarDupesCommandHandler>();

    // CommandsCommandHandler: /command-catalog self-service slash command. Returns
    // a rank-filtered table of every slash command the bot exposes. Open to
    // every member; rendered ephemerally. The catalog of entries inside
    // CommandsCommandHandler.BuildCatalog MUST be kept in sync with the
    // SlashCommandBuilder list in DiscordBotService.OnReadyAsync — adding,
    // removing, or re-gating a command requires updating both.
    builder.Services.AddSingleton<CommandsCommandHandler>();

    // ── Slash-command usage tracking ─────────────────────────────────
    // CommandUsageTrackingHandler: subscribes to SlashCommandExecuted
    // alongside the per-command handlers and writes one row to
    // CommandUsages for every invocation. Captures command name,
    // subcommand path, invoker, and (for an explicit allowlist of
    // choice/boolean params) the values passed. Not an activity signal —
    // AutoPromotionService does not consume CommandUsages. See the handler
    // for the parameter allowlist; extend it when adding new flag-style
    // options worth surfacing in usage analytics.
    //
    // CommandUsagePruneService: nightly sweep that deletes CommandUsages
    // rows older than 90 days. Hot-path inserts stay independent of the
    // retention policy.
    builder.Services.AddSingleton<CommandUsageTrackingHandler>();
    builder.Services.AddHostedService<CommandUsagePruneService>();

    // UsageStatsCommandHandler: /usage-stats slash command (MAJ+). Reads
    // the CommandUsages table written by CommandUsageTrackingHandler. Three
    // subcommands: top (leaderboard), user (per-member history), command
    // (per-command drill-down with parameter breakdown). Catalog entry
    // lives in CommandsCommandHandler.BuildCatalog and the slash-command
    // shape is registered in DiscordBotService.OnReadyAsync via
    // UsageStatsCommandHandler.BuildCommand() — keep all three in sync.
    builder.Services.AddSingleton<UsageStatsCommandHandler>();

    // SecurityAuditCommandHandler: /security-audit slash command (MAJ+). Reads
    // the SecurityAuditRecords table written by the server-protection feature
    // set (account-age gate today; raid shield / impersonation check /
    // webhook audit / token-grabber scanner planned). Filters on user,
    // feature, days (window), and limit (row cap). Single command with
    // optional filters rather than subcommands — every read shape is the
    // same WHERE clause with different parameters. Catalog entry in
    // CommandsCommandHandler.BuildCatalog and slash-command shape in
    // DiscordBotService.OnReadyAsync via SecurityAuditCommandHandler.BuildCommand()
    // — keep all three in sync.
    builder.Services.AddSingleton<SecurityAuditCommandHandler>();

    // ── Reddit recruitment leads ─────────────────────────────────────
    // RedditLeadService: polling background service that watches the
    // configured subreddits for LFG/"looking for clan" posts, runs each
    // through LeadMatcher's three-tier filter (positive keyword → negative
    // keyword → author quality floor), and posts surviving leads to the
    // leads channel as embeds with click-to-claim/contact/outcome buttons.
    // RedditLeadButtonHandler routes the button clicks; RedditLeadsCommandHandler
    // exposes /leads stats and /leads recent. All three (plus the typed
    // Reddit HttpClient + options binding) are wired up by AddRedditLeads.
    // Disabled-by-default in appsettings; enable via RedditLeads:Enabled
    // once the bot Reddit account + script app credentials are in place.
    // NOTE: also keep CommandsCommandHandler.BuildCatalog in sync when
    // changing /leads, same as the rest of the slash-command surface.
    builder.Services.AddRedditLeads(builder.Configuration);

    // ── Patrol Watch ─────────────────────────────────────────────────
    // PatrolWatchService: hosted service that watches voice + presence to
    // surface "squads on patrol" — 3+ members in the same VC playing a
    // matched game (BF6 today; Arc Raiders etc. via config). Posts ONE
    // rolling embed per active patrol to the LFG channel and edits in
    // place as the roster changes. Requires the GuildPresences privileged
    // intent (toggled in Discord Developer Portal AND added to GatewayIntents
    // above). Disabled-by-default in appsettings; enable via PatrolWatch:Enabled.
    // /patrol on/off/info handler registered alongside the other slash-command
    // handlers in DiscordBotService.Register. NOTE: also keep
    // CommandsCommandHandler.BuildCatalog in sync when changing /patrol.
    builder.Services.AddPatrolWatch(builder.Configuration);

    var app = builder.Build();

    // Ensure the database is created / migrated
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        await db.Database.MigrateAsync();
        Log.Information("Database initialized at {DbPath}", dbPath);
    }

    // Register the slash command handler (needs to happen before the bot connects)
    var slashHandler    = app.Services.GetRequiredService<SlashCommandHandler>();
    var discordClient   = app.Services.GetRequiredService<DiscordSocketClient>();
    slashHandler.Register(discordClient);

    // Ensure the Google Sheet has a header row
    var sheetsService = app.Services.GetRequiredService<GoogleSheetsService>();
    await sheetsService.EnsureHeaderRowAsync();
    await sheetsService.EnsureRecruitHeaderRowAsync();

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Bot terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}