using ClanGuardBot.Models;
using Discord.WebSocket;

namespace ClanGuardBot.Services;

/// <summary>
/// Compares the role names in config against the roles that actually exist in
/// the guild, and against each other, so a typo or a renamed/deleted Discord
/// role surfaces as a startup warning instead of as silently wrong behaviour.
///
/// Why this exists: several features resolve a role by NAME out of config
/// (RankRoles, ShortWindowRoles, ExemptRoles, AwolRoleName) and treat "no
/// match" as a legitimate state rather than an error. Two examples of what
/// that costs:
///
/// - AWOL windows. BotConfig.GetWindowDaysForRoles grants the regular
///   WindowDays only to members holding a RankRoles entry that is not also a
///   ShortWindowRoles entry. A rank role that exists in Discord but is missing
///   or misspelled in RankRoles looks exactly like "member has no rank", so
///   everyone at that rank quietly drops to the short window and starts
///   getting flagged AWOL twice as fast.
///
/// - Kick protection. KickAwolsCommandHandler finds AwolKickMinRank inside
///   RankRoles with FindIndex. If it is not in the list the result is -1 and
///   the "don't kick leadership" guard turns itself off.
///
/// Both fail open and quiet. The checks below are the cheap counterweight:
/// one pass over the guild's role list at startup, reported once per guild.
///
/// Everything here is a warning, never a throw. A missing role is usually a
/// config that has drifted, not a reason to refuse to start, and the bot is
/// more useful running with a loud log line than not running at all.
/// </summary>
public static class RoleConfigValidator
{
    /// <summary>
    /// One problem found in the role configuration. Setting is the config key
    /// to go fix, Detail says what is wrong, and Impact says what silently
    /// misbehaves until it is fixed.
    /// </summary>
    public sealed record RoleConfigIssue(string Setting, string Detail, string Impact);

    /// <summary>
    /// Runs every check against one guild. Returns an empty list when the
    /// configuration is consistent with the guild's roles.
    /// </summary>
    public static IReadOnlyList<RoleConfigIssue> Validate(BotConfig config, SocketGuild guild)
    {
        var guildRoleNames = guild.Roles
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Validate(config, guildRoleNames);
    }

    /// <summary>
    /// Guild-free overload so the checks can be exercised without a live
    /// gateway connection.
    /// </summary>
    public static IReadOnlyList<RoleConfigIssue> Validate(
        BotConfig config, ISet<string> guildRoleNames)
    {
        var issues = new List<RoleConfigIssue>();

        var rankRoles  = config.GetRankRolesList();
        var shortRoles = config.GetShortWindowRolesList();
        var exemptRoles = config.GetExemptRolesList();

        // -- 1. Config names roles that do not exist in the guild --
        AddMissing(issues, "RankRoles", rankRoles, guildRoleNames,
            "members at that rank are treated as having no rank at all, which puts them on the "
            + $"{config.ShortWindowDays}-day AWOL window instead of {config.WindowDays} and drops any "
            + "rank-gated protection they should have");

        AddMissing(issues, "ShortWindowRoles", shortRoles, guildRoleNames,
            "nothing breaks, but the entry is dead weight and hides a rename");

        AddMissing(issues, "ExemptRoles", exemptRoles, guildRoleNames,
            "nobody is exempted by that name, so members who should never be flagged AWOL will be");

        if (!string.IsNullOrWhiteSpace(config.AwolRoleName)
            && !guildRoleNames.Contains(config.AwolRoleName))
        {
            issues.Add(new RoleConfigIssue(
                "AwolRoleName",
                $"\"{config.AwolRoleName}\" does not exist in the guild",
                "the AWOL sweep cannot assign or remove the role, so it does nothing at all"));
        }

        // -- 2. A rank gate names a rank that is not in RankRoles --
        // These are all resolved by looking the string up inside RankRoles.
        // A miss is not an error at the call site, it just disables or
        // mis-tiers the gate, so it has to be caught here.
        foreach (var (setting, value, impact) in RankGates(config))
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (rankRoles.Contains(value, StringComparer.OrdinalIgnoreCase)) continue;

            issues.Add(new RoleConfigIssue(
                setting,
                $"\"{value}\" is not in RankRoles",
                impact));
        }

        foreach (var rank in config.GetAutoPromotionDryRunRanksList())
        {
            if (rankRoles.Contains(rank, StringComparer.OrdinalIgnoreCase)) continue;

            issues.Add(new RoleConfigIssue(
                "AutoPromotionDryRunRanks",
                $"\"{rank}\" is not in RankRoles",
                "that entry never matches anyone, so promotions at that rank are not held in dry run"));
        }

        // -- 3. Internal consistency --
        // ShortWindowRoles entries that are also ranks are fine and expected
        // (RCT). An entry that is neither a rank nor a guild role is a typo,
        // already covered above. What is worth flagging is the degenerate case
        // where every rank is a short-window role, which would mean nobody can
        // ever earn the regular window.
        if (rankRoles.Count > 0
            && rankRoles.All(r => shortRoles.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            issues.Add(new RoleConfigIssue(
                "ShortWindowRoles",
                "every RankRoles entry is also a ShortWindowRoles entry",
                $"no member can ever qualify for the {config.WindowDays}-day window; the whole server "
                + $"is judged on {config.ShortWindowDays} days"));
        }

        return issues;
    }

    /// <summary>
    /// Formats the issues for a log line or a /health field. Returns null when
    /// there is nothing to report.
    /// </summary>
    public static string? Describe(IReadOnlyList<RoleConfigIssue> issues, int max = 6)
    {
        if (issues.Count == 0) return null;

        var lines = issues
            .Take(max)
            .Select(i => $"- {i.Setting}: {i.Detail}");

        var text = string.Join("\n", lines);
        if (issues.Count > max)
            text += $"\n- ...and {issues.Count - max} more";

        return text;
    }

    // -- Helpers ------------------------------------------------------

    private static void AddMissing(
        List<RoleConfigIssue> issues,
        string setting,
        IEnumerable<string> configured,
        ISet<string> guildRoleNames,
        string impact)
    {
        foreach (var name in configured)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (guildRoleNames.Contains(name)) continue;

            issues.Add(new RoleConfigIssue(
                setting,
                $"\"{name}\" does not exist in the guild",
                impact));
        }
    }

    /// <summary>
    /// Every config value that has to name an entry in RankRoles, paired with
    /// what goes wrong when it does not. Add new *MinRank settings here when
    /// they are introduced.
    /// </summary>
    private static IEnumerable<(string Setting, string Value, string Impact)> RankGates(BotConfig config)
    {
        yield return ("AwolKickMinRank", config.AwolKickMinRank,
            "the /kick-awols guard that protects leadership is disabled, so a high-ranking member "
            + "flagged AWOL would be kicked");
        yield return ("CompEventMinRank", config.CompEventMinRank,
            "the /comp-event rank gate cannot resolve");
        yield return ("EventCommandMinRank", config.EventCommandMinRank,
            "the /event rank gate cannot resolve");
        yield return ("EventTemplateManageMinRank", config.EventTemplateManageMinRank,
            "the event template management gate cannot resolve");
        yield return ("ReminderCommandMinRank", config.ReminderCommandMinRank,
            "the /reminder rank gate cannot resolve");
        yield return ("SayCommandMinRank", config.SayCommandMinRank,
            "the /say rank gate cannot resolve");
        yield return ("QotdMinRank", config.QotdMinRank,
            "the /qotd rank gate cannot resolve");
        yield return ("JotdMinRank", config.JotdMinRank,
            "the /jotd rank gate cannot resolve");
        yield return ("PromoteDemoteMinRank", config.PromoteDemoteMinRank,
            "the /promote and /demote rank gate cannot resolve");
        yield return ("BriefingNowMinRank", config.BriefingNowMinRank,
            "the /briefing-now rank gate cannot resolve");
        yield return ("InviteManagementMinRank", config.InviteManagementMinRank,
            "the invite management rank gate cannot resolve");
        yield return ("OfficerAppMinimumRank", config.OfficerAppMinimumRank,
            "the officer application minimum rank cannot resolve");
        yield return ("InviteLinkFilterExemptMinRank", config.InviteLinkFilterExemptMinRank,
            "no rank is exempt from the invite link filter");
        yield return ("NicknameImpersonationMinRank", config.NicknameImpersonationMinRank,
            "the nickname impersonation gate cannot resolve");
        yield return ("TokenGrabberScannerExemptMinRank", config.TokenGrabberScannerExemptMinRank,
            "no rank is exempt from the token grabber scanner");
        yield return ("HoneypotExemptMinRank", config.HoneypotExemptMinRank,
            "no rank is exempt from the honeypot");
        yield return ("XpAdminMinRank", config.XpAdminMinRank,
            "the XP admin rank gate cannot resolve");
    }
}
