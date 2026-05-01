using ClanGuardBot.Briefing;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /briefing-now slash command. Triggers the weekly officer
/// briefing immediately (out of band from its Sunday 14:00 UTC schedule).
/// Behaviour mirrors the scheduled run — if WeeklyBriefing.DryRun is true,
/// the briefing is logged instead of posted.
///
/// Implementation notes:
///   • Permission gate is BotConfig.BriefingNowMinRank (default BG+).
///     Briefing posts to the HQ channel; only HQ should be able to trigger it.
///   • Discord requires a response within 3 seconds, so the command defers
///     immediately and uses follow-up messages for progress and result.
///     The Claude API call typically takes 5–15 seconds.
///   • The handler delegates the actual work to WeeklyOfficerBriefingService
///     so behaviour stays identical between scheduled and manual runs.
/// </summary>
public class BriefingNowCommandHandler
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger<BriefingNowCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly WeeklyOfficerBriefingService _briefing;

    public BriefingNowCommandHandler(
        DiscordSocketClient client,
        ILogger<BriefingNowCommandHandler> logger,
        IOptions<BotConfig> config,
        WeeklyOfficerBriefingService briefing)
    {
        _client = client;
        _logger = logger;
        _config = config.Value;
        _briefing = briefing;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandExecuted;
    }

    private Task OnSlashCommandExecuted(SocketSlashCommand cmd)
    {
        if (cmd.Data.Name != "briefing-now") return Task.CompletedTask;

        // Dispatch off the gateway thread. The Claude API call inside
        // HandleBriefingNowAsync takes 5–15 seconds, which is far too long to
        // block Discord.NET's gateway loop. We return Task.CompletedTask
        // immediately and let the work run in the background — DeferAsync +
        // FollowupAsync work just as well from a Task.Run continuation.
        _ = Task.Run(async () =>
        {
            try
            {
                await cmd.DeferAsync(ephemeral: true);
                await HandleBriefingNowAsync(cmd);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in /briefing-now");
                try
                {
                    await cmd.FollowupAsync($"❌ Unexpected error: {ex.Message}", ephemeral: true);
                }
                catch
                {
                    // Already responded, or interaction token expired — nothing
                    // useful left to do beyond the log entry above.
                }
            }
        });

        return Task.CompletedTask;
    }

    private async Task HandleBriefingNowAsync(SocketSlashCommand cmd)
    {
        var guild = (cmd.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await cmd.FollowupAsync("This command must be used inside a server.", ephemeral: true);
            return;
        }

        // ── Permission check ─────────────────────────────────────────────
        var invoker = guild.GetUser(cmd.User.Id);
        if (invoker is null || !InvokerHasPermission(invoker))
        {
            await cmd.FollowupAsync(
                $"⛔ You need to be **{_config.BriefingNowMinRank}** or higher to use this command.",
                ephemeral: true);
            return;
        }

        // ── Initial progress message — Claude calls take 5–15 seconds ────
        await cmd.FollowupAsync(
            "⏳ Generating briefing… this usually takes 5–15 seconds.",
            ephemeral: true);

        try
        {
            await _briefing.RunBriefingNowAsync();

            await cmd.FollowupAsync(
                "✅ Briefing run complete. Check the HQ channel " +
                "(or the bot logs if `DryRun` is enabled in config).",
                ephemeral: true);

            _logger.LogInformation(
                "/briefing-now triggered by {Invoker}",
                cmd.User.Username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Briefing run failed via /briefing-now");
            await cmd.FollowupAsync(
                $"❌ Briefing run failed: {ex.Message}",
                ephemeral: true);
        }
    }

    /// <summary>
    /// True if the invoker has Administrator OR a rank role at or above BriefingNowMinRank.
    /// </summary>
    private bool InvokerHasPermission(SocketGuildUser invoker)
    {
        if (invoker.GuildPermissions.Administrator) return true;

        var rankList = _config.GetRankRolesList();
        var minRankIdx = rankList.FindIndex(r =>
            r.Equals(_config.BriefingNowMinRank, StringComparison.OrdinalIgnoreCase));

        if (minRankIdx < 0)
        {
            _logger.LogWarning(
                "BriefingNowMinRank '{Rank}' not found in RankRoles list. " +
                "Permission check will deny everyone except Admins.",
                _config.BriefingNowMinRank);
            return false;
        }

        foreach (var role in invoker.Roles)
        {
            var idx = rankList.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= minRankIdx) return true;
        }
        return false;
    }
}