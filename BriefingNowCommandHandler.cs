using System.Globalization;
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
///     Briefing posts to the briefings channel; only HQ should be able to trigger it.
///   • Discord requires a response within 3 seconds, so the command defers
///     immediately and uses follow-up messages for progress and result.
///     The Claude API call typically takes 10–30 seconds.
///   • The handler delegates the actual work to WeeklyOfficerBriefingService
///     so behaviour stays identical between scheduled and manual runs.
/// </summary>
public class BriefingNowCommandHandler
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger<BriefingNowCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly WeeklyBriefingOptions _briefingOptions;
    private readonly WeeklyOfficerBriefingService _briefing;

    public BriefingNowCommandHandler(
        DiscordSocketClient client,
        ILogger<BriefingNowCommandHandler> logger,
        IOptions<BotConfig> config,
        IOptions<WeeklyBriefingOptions> briefingOptions,
        WeeklyOfficerBriefingService briefing)
    {
        _client = client;
        _logger = logger;
        _config = config.Value;
        _briefingOptions = briefingOptions.Value;
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
        // HandleBriefingNowAsync takes 10–30 seconds, which is far too long to
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

        // ── Optional explicit window (start_date / end_date) ─────────────
        // Both-or-neither. Dates are clan-local Eastern calendar days; the
        // service converts them to the UTC window. Parse & validate BEFORE the
        // progress message so a typo fails fast without a 10–30s wait.
        if (!TryReadWindow(cmd, out var window, out var parseError))
        {
            await cmd.FollowupAsync(parseError, ephemeral: true);
            return;
        }

        // ── Initial progress message — Claude calls take 10–30 seconds ───
        var windowNote = window is { } w
            ? $" for **{w.Start:yyyy-MM-dd} → {w.End:yyyy-MM-dd}** (Eastern)"
            : string.Empty;
        await cmd.FollowupAsync(
            $"⏳ Generating briefing{windowNote}… this usually takes 10–30 seconds.",
            ephemeral: true);

        try
        {
            if (window is { } win)
                await _briefing.RunBriefingForDatesAsync(win.Start, win.End);
            else
                await _briefing.RunBriefingNowAsync();

            // <#id> renders as a clickable channel link in the ephemeral, so the
            // invoker can jump straight to the post they just triggered. The run
            // posts the HQ briefing and — when RecruitmentChannelId is set — the
            // recruitment briefing too, so mention both channels.
            var channelsNote = _briefingOptions.RecruitmentChannelId != 0
                ? $"<#{_briefingOptions.OfficerChannelId}> and <#{_briefingOptions.RecruitmentChannelId}>"
                : $"<#{_briefingOptions.OfficerChannelId}>";
            await cmd.FollowupAsync(
                $"✅ Briefing run complete. Check {channelsNote} " +
                "(or the bot logs if `DryRun` is enabled in config).",
                ephemeral: true);

            _logger.LogInformation(
                "/briefing-now triggered by {Invoker}{Window}",
                cmd.User.Username,
                window is { } lw ? $" for {lw.Start:yyyy-MM-dd}→{lw.End:yyyy-MM-dd} ET" : string.Empty);
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
    /// Reads the optional start_date/end_date options. Returns true when the
    /// input is valid, setting <paramref name="window"/> to null (no custom
    /// window → trailing 7 days) or a validated (Start, End) pair. Returns false
    /// with a user-facing <paramref name="error"/> on any problem. Enforces
    /// both-or-neither, YYYY-MM-DD format, Start &lt; End, and a sane max span.
    /// </summary>
    private static bool TryReadWindow(
        SocketSlashCommand cmd,
        out (DateOnly Start, DateOnly End)? window,
        out string error)
    {
        window = null;
        error = string.Empty;

        string? Opt(string name) =>
            (cmd.Data.Options?.FirstOrDefault(o => o.Name == name)?.Value as string)?.Trim();

        var startRaw = Opt("start_date");
        var endRaw = Opt("end_date");
        var hasStart = !string.IsNullOrWhiteSpace(startRaw);
        var hasEnd = !string.IsNullOrWhiteSpace(endRaw);

        if (!hasStart && !hasEnd)
            return true; // no window supplied → default trailing-7-days behaviour

        if (hasStart ^ hasEnd)
        {
            error = "⚠️ Provide **both** `start_date` and `end_date`, or neither (which uses the trailing 7 days).";
            return false;
        }

        const string fmt = "yyyy-MM-dd";
        if (!DateOnly.TryParseExact(startRaw, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !DateOnly.TryParseExact(endRaw, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            error = "⚠️ Dates must be in `YYYY-MM-DD` format — e.g. `2026-06-28`.";
            return false;
        }

        if (end <= start)
        {
            error = $"⚠️ `end_date` must be after `start_date` (you gave `{startRaw}` → `{endRaw}`).";
            return false;
        }

        // Guard against typo-scale ranges (e.g. a wrong year) pulling an enormous
        // data snapshot. 366 days covers any reasonable manual backfill.
        if (end.DayNumber - start.DayNumber > 366)
        {
            error = "⚠️ That window is over a year long — double-check the dates and pick a shorter range.";
            return false;
        }

        window = (start, end);
        return true;
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