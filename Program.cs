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
    builder.Services.AddSingleton<RecruitCommandHandler>();
    builder.Services.AddSingleton<PromoteCommandHandler>();
    builder.Services.AddSingleton<DemoteCommandHandler>();
    builder.Services.AddSingleton<SetNickCommandHandler>();

    // TicketReminderHandler: singleton for event registration + hosted service for startup recovery.
    builder.Services.AddSingleton<TicketReminderHandler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TicketReminderHandler>());

    // GuestReminderHandler: same pattern.
    builder.Services.AddSingleton<GuestReminderHandler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<GuestReminderHandler>());

    // ApolloEventHandler: parses #events channel posts and syncs to Google Calendar.
    builder.Services.AddSingleton<ApolloEventHandler>();

    // CompEventCommandHandler: /comp-event slash command for CPT+ officers.
    builder.Services.AddSingleton<CompEventCommandHandler>();

    // ── Hosted Services ──────────────────────────────────────────────
    builder.Services.AddHostedService<DiscordBotService>();
    builder.Services.AddHostedService<HistoryBackfillService>();
    builder.Services.AddHostedService<ApolloBackfillService>();
    builder.Services.AddHostedService<AwolCheckService>();

    // RosterExportService: singleton so SlashCommandHandler can inject it,
    // + hosted service so its background loop runs automatically.
    builder.Services.AddSingleton<RosterExportService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RosterExportService>());

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