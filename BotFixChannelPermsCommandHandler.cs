using System.Text;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /bot-fix-channel-perms slash command — walks every channel and
/// category in the guild and ensures the bot has a <b>member-level</b>
/// permission overwrite granting Manage Channel, so /channel-rename (and any
/// other channel edit) works everywhere.
///
/// Why a member overwrite rather than a role grant: Discord resolves channel
/// permissions in precedence order, and a member allow overwrite is the highest
/// precedence below Administrator/owner. It therefore beats an @everyone or
/// category-level deny that a neutral role override would NOT override. This is
/// exactly the situation that blocks the rename: the bot has Manage Channels
/// server-wide, but a higher-up deny wins on specific channels.
///
/// Safety:
///  • Administrator-gated.
///  • Dry-run by default — it reports what it WOULD change and applies nothing
///    unless explicitly run with dry_run:false.
///  • Idempotent — channels already granted are skipped, so re-running is a
///    no-op and it only ever flips the single Manage Channel bit (existing
///    View/Send/etc. overwrites are preserved via Modify, not overwritten).
///  • Per-channel Forbidden errors are caught and reported rather than aborting
///    the whole run (a channel the bot can't manage at all is listed for a
///    manual fix).
/// </summary>
public class BotFixChannelPermsCommandHandler
{
    public const string CommandName = "bot-fix-channel-perms";

    // Keep the per-channel listing from blowing past Discord's 2000-char
    // message limit; anything beyond this is summarized as "+N more".
    private const int MaxListedChannels = 40;

    private readonly ILogger<BotFixChannelPermsCommandHandler> _logger;

    public BotFixChannelPermsCommandHandler(ILogger<BotFixChannelPermsCommandHandler> logger)
    {
        _logger = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not CommandName)
            return;

        try
        {
            await HandleFix(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", CommandName);
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleFix(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.Channel is not SocketGuildChannel originChannel)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var guild = originChannel.Guild;

        if (command.User is not SocketGuildUser caller || !caller.GuildPermissions.Administrator)
        {
            await command.FollowupAsync(
                "❌ This command is Administrator-only — it changes channel permissions server-wide.",
                ephemeral: true);
            return;
        }

        // dry_run defaults to TRUE when the option is omitted, so the safe path
        // is the default and applying changes requires an explicit dry_run:false.
        var dryRunOption = command.Data.Options.FirstOrDefault(o => o.Name == "dry_run");
        var dryRun = dryRunOption?.Value as bool? ?? true;

        var botUser = guild.CurrentUser;
        if (botUser is null)
        {
            await command.FollowupAsync("❌ Couldn't resolve the bot's own guild membership.", ephemeral: true);
            return;
        }

        var alreadyOk = 0;
        var changed   = new List<string>();
        var failed    = new List<string>();

        // guild.Channels includes categories plus text/voice/stage/forum/etc.
        // Order: categories first (so a fixed category benefits any synced
        // children), then everything else.
        var ordered = guild.Channels
            .OrderByDescending(c => c is SocketCategoryChannel)
            .ThenBy(c => c.Position);

        foreach (var channel in ordered)
        {
            var existing = channel.GetPermissionOverwrite(botUser);

            if (existing?.ManageChannel == PermValue.Allow)
            {
                alreadyOk++;
                continue;
            }

            var label = Describe(channel);

            if (dryRun)
            {
                changed.Add(label);
                continue;
            }

            try
            {
                var updated = (existing ?? OverwritePermissions.InheritAll)
                    .Modify(manageChannel: PermValue.Allow);

                await channel.AddPermissionOverwriteAsync(botUser, updated,
                    new RequestOptions { AuditLogReason = $"bot-fix-channel-perms by {caller.Username}" });

                changed.Add(label);
            }
            catch (Discord.Net.HttpException ex)
                when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
            {
                failed.Add(label);
                _logger.LogWarning(
                    "bot-fix-channel-perms: forbidden on {Channel} ({Id}) — bot lacks Manage Roles there",
                    channel.Name, channel.Id);
            }
            catch (Exception ex)
            {
                failed.Add(label);
                _logger.LogWarning(ex,
                    "bot-fix-channel-perms: failed on {Channel} ({Id})", channel.Name, channel.Id);
            }
        }

        _logger.LogInformation(
            "bot-fix-channel-perms ({Mode}) by {Caller} in {Guild}: alreadyOk={Ok}, changed={Changed}, failed={Failed}",
            dryRun ? "DRY RUN" : "APPLIED", caller.Username, guild.Name,
            alreadyOk, changed.Count, failed.Count);

        await command.FollowupAsync(BuildReport(dryRun, alreadyOk, changed, failed), ephemeral: true);
    }

    private static string Describe(SocketGuildChannel channel)
    {
        var kind = channel switch
        {
            SocketCategoryChannel => "category",
            SocketStageChannel    => "stage",
            SocketVoiceChannel    => "voice",
            SocketTextChannel     => "text",
            _                     => "channel"
        };
        return $"{channel.Name} ({kind})";
    }

    private static string BuildReport(bool dryRun, int alreadyOk, List<string> changed, List<string> failed)
    {
        var sb = new StringBuilder();

        sb.AppendLine(dryRun
            ? "🔎 **Bot Channel Permissions — DRY RUN** (nothing was changed)"
            : "✅ **Bot Channel Permissions — APPLIED**");
        sb.AppendLine();
        sb.AppendLine($"Already had access: **{alreadyOk}**");
        sb.AppendLine(dryRun
            ? $"Would be granted Manage Channel: **{changed.Count}**"
            : $"Granted Manage Channel: **{changed.Count}**");
        if (failed.Count > 0)
            sb.AppendLine($"Could not update (needs manual fix): **{failed.Count}**");

        AppendList(sb, dryRun ? "Would change" : "Changed", changed);
        AppendList(sb, "Failed — bot lacks Manage Roles here", failed);

        if (dryRun && changed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Run again with **dry_run: false** to apply.");
        }

        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string heading, List<string> items)
    {
        if (items.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine($"__{heading}__");
        sb.AppendLine("```");
        foreach (var item in items.Take(MaxListedChannels))
            sb.AppendLine(item);
        if (items.Count > MaxListedChannels)
            sb.AppendLine($"... +{items.Count - MaxListedChannels} more");
        sb.AppendLine("```");
    }
}
