using ClanGuardBot.Handlers;
using ClanGuardBot.Models;
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
    private readonly GamertagCommandHandler _gamertagHandler;
    private readonly LookupCommandHandler _lookupHandler;
    private readonly RankTrackingHandler _rankHandler;
    private readonly TicketReminderHandler _ticketReminderHandler;
    private readonly GuestReminderHandler _guestReminderHandler;
    private readonly OnboardingReminderHandler _onboardingReminderHandler;
    private readonly BumpReminderHandler _bumpReminderHandler;
    private readonly ApolloEventHandler _apolloEventHandler;
    private readonly ApolloMessageCaptureHandler _apolloCaptureHandler;
    private readonly CompEventCommandHandler _compEventHandler;
    private readonly PromoteCommandHandler _promoteHandler;
    private readonly DemoteCommandHandler _demoteHandler;
    private readonly SetNickCommandHandler _setNickHandler;
    private readonly SeedPromotionCreditCommandHandler _seedHandler;
    private readonly EventCreditCommandHandler _eventCreditHandler;
    private readonly AttendanceCommandHandler _attendanceHandler;
    private readonly KickAwolsCommandHandler _kickAwolsHandler;
    private readonly ClearAwolListCommandHandler _clearAwolListHandler;
    private readonly BriefingNowCommandHandler _briefingNowHandler;
    private readonly CleanupCalendarDupesCommandHandler _cleanupCalendarDupesHandler;
    private readonly CalendarCommandHandler _calendarHandler;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly BotConfig _config;

    public DiscordBotService(
        DiscordSocketClient client,
        ActivityTrackingHandler activityHandler,
        GamertagCommandHandler gamertagHandler,
        LookupCommandHandler lookupHandler,
        RankTrackingHandler rankHandler,
        TicketReminderHandler ticketReminderHandler,
        GuestReminderHandler guestReminderHandler,
        OnboardingReminderHandler onboardingReminderHandler,
        BumpReminderHandler bumpReminderHandler,
        ApolloEventHandler apolloEventHandler,
        ApolloMessageCaptureHandler apolloCaptureHandler,
        CompEventCommandHandler compEventHandler,
        PromoteCommandHandler promoteHandler,
        DemoteCommandHandler demoteHandler,
        SetNickCommandHandler setNickHandler,
        SeedPromotionCreditCommandHandler seedHandler,
        EventCreditCommandHandler eventCreditHandler,
        AttendanceCommandHandler attendanceHandler,
        KickAwolsCommandHandler kickAwolsHandler,
        ClearAwolListCommandHandler clearAwolListHandler,
        BriefingNowCommandHandler briefingNowHandler,
        CleanupCalendarDupesCommandHandler cleanupCalendarDupesHandler,
        CalendarCommandHandler calendarHandler,
        ILogger<DiscordBotService> logger,
        IOptions<BotConfig> config)
    {
        _client                      = client;
        _activityHandler             = activityHandler;
        _gamertagHandler             = gamertagHandler;
        _lookupHandler               = lookupHandler;
        _rankHandler                 = rankHandler;
        _ticketReminderHandler       = ticketReminderHandler;
        _guestReminderHandler        = guestReminderHandler;
        _onboardingReminderHandler   = onboardingReminderHandler;
        _bumpReminderHandler         = bumpReminderHandler;
        _apolloEventHandler          = apolloEventHandler;
        _apolloCaptureHandler        = apolloCaptureHandler;
        _compEventHandler            = compEventHandler;
        _promoteHandler              = promoteHandler;
        _demoteHandler               = demoteHandler;
        _setNickHandler              = setNickHandler;
        _seedHandler                 = seedHandler;
        _eventCreditHandler          = eventCreditHandler;
        _attendanceHandler           = attendanceHandler;
        _kickAwolsHandler            = kickAwolsHandler;
        _clearAwolListHandler        = clearAwolListHandler;
        _briefingNowHandler          = briefingNowHandler;
        _cleanupCalendarDupesHandler = cleanupCalendarDupesHandler;
        _calendarHandler             = calendarHandler;
        _logger                      = logger;
        _config                      = config.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log    += LogAsync;
        _client.Ready  += OnReadyAsync;

        _activityHandler.Register(_client);
        _gamertagHandler.Register(_client);
        _lookupHandler.Register(_client);
        _rankHandler.Register(_client);
        _ticketReminderHandler.Register(_client);
        _guestReminderHandler.Register(_client);
        _onboardingReminderHandler.Register(_client);
        _bumpReminderHandler.Register(_client);
        _apolloEventHandler.Register(_client);
        _apolloCaptureHandler.Register(_client);
        _compEventHandler.Register(_client);
        _promoteHandler.Register(_client);
        _demoteHandler.Register(_client);
        _setNickHandler.Register(_client);
        _seedHandler.Register(_client);
        _eventCreditHandler.Register(_client);
        _attendanceHandler.Register(_client);
        _kickAwolsHandler.Register(_client);
        _clearAwolListHandler.Register(_client);
        _briefingNowHandler.Register(_client);
        _cleanupCalendarDupesHandler.Register(_client);
        _calendarHandler.Register(_client);

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

                new SlashCommandBuilder()
                    .WithName("gamertags")
                    .WithDescription("Enter your gamertags for EA, Steam, PSN, Xbox, Embark, and Bungie")
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("lookup")
                    .WithDescription("Look up someone's gamertags from the roster")
                    .AddOption("user", ApplicationCommandOptionType.User,
                        "The clan member to look up", isRequired: true)
                    .Build(),

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
                    .WithDescription("Show today's clan event attendance, grouped by event (MAJ+ only)")
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

                // /briefing-now — manually trigger the weekly officer briefing
                // out of band from the Sunday cron. Honours the same DryRun
                // config flag as the scheduled run, so officers can validate
                // the pipeline without posting to HQ.
                new SlashCommandBuilder()
                    .WithName("briefing-now")
                    .WithDescription($"Generate the weekly officer briefing immediately ({_config.BriefingNowMinRank}+ only)")
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