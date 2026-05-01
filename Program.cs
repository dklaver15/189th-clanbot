using ClanGuardBot.Briefing;
using ClanGuardBot.Data;
using ClanGuardBot.Handlers;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
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
    builder.Services.AddSingleton<SlashCommandHandler>();
    builder.Services.AddSingleton<RankTrackingHandler>();

    // OnboardingReminderHandler: registered before GamertagCommandHandler because
    // the gamertag handler depends on it to notify when a Guest saves gamertags.
    builder.Services.AddSingleton<OnboardingReminderHandler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OnboardingReminderHandler>());

    builder.Services.AddSingleton<GamertagCommandHandler>();

    // NOTE: RecruitCommandHandler was removed — recruits are now auto-logged by
    // RankTrackingHandler when a member gains the RCT role. See TryLogRecruitAsync
    // in RankTrackingHandler.cs.

    // PromotionService: shared core used by both /promote and AutoPromotionService.
    // Registered before the consumers that inject it.
    builder.Services.AddSingleton<PromotionService>();

    builder.Services.AddSingleton<PromoteCommandHandler>();
    builder.Services.AddSingleton<DemoteCommandHandler>();
    builder.Services.AddSingleton<SetNickCommandHandler>();

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

    // ApolloEventHandler: parses #events channel posts and syncs to Google Calendar.
    builder.Services.AddSingleton<ApolloEventHandler>();

    // CompEventCommandHandler: /comp-event slash command for CPT+ officers.
    builder.Services.AddSingleton<CompEventCommandHandler>();

    // ── Hosted Services ──────────────────────────────────────────────
    builder.Services.AddHostedService<DiscordBotService>();
    builder.Services.AddHostedService<HistoryBackfillService>();
    builder.Services.AddHostedService<ApolloBackfillService>();
    builder.Services.AddHostedService<AwolCheckService>();

    // VoiceSessionCleanupService: one-shot service that runs at startup to close
    // stuck voice sessions (LeftAt IS NULL rows that don't match current Discord
    // voice state). Prevents orphaned sessions from inflating activity totals.
    builder.Services.AddHostedService<VoiceSessionCleanupService>();

    // EventAttendanceSnapshotService: timer-driven (default 5 min) + startup
    // catch-up pass. Writes EventAttendance rows shortly after each clan event
    // ends, so attendance history survives CalendarEvent row deletion (which
    // happens when Apollo messages are cleaned up). Consumed by AutoPromotionService
    // for SGT+ eligibility checks.
    builder.Services.AddHostedService<EventAttendanceSnapshotService>();

    // AutoPromotionService: nightly check that auto-promotes RCT → SGT based on
    // time-in-rank + activity thresholds (msgs/voice for RCT→CPL; event attendance
    // for CPL→SGT). Respects the AutoPromotionEnabled, AutoPromotionDryRun, and
    // AutoPromotionDryRunRanks config flags.
    builder.Services.AddHostedService<AutoPromotionService>();

    // RosterExportService: singleton so SlashCommandHandler can inject it,
    // + hosted service so its background loop runs automatically.
    builder.Services.AddSingleton<RosterExportService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RosterExportService>());

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