using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The DM gamertag wizard — the same shape as <see cref="EventCreationWizard"/>.
/// Triggered by /gamertags (or the persistent "Enter Gamertags" button), it
/// walks the member through their six platform tags one at a time in their DMs,
/// then writes the result to the roster sheet.
///
/// ── Why a DM wizard? ──
/// The old flow was a two-page modal (Discord caps modals at 5 inputs but we
/// collect 6). Lots of members never realized there was a page 2 and submitted
/// half their tags. A DM wizard has no field cap, asks one thing at a time, and
/// shows a confirm card before anything is saved.
///
/// ── Routing ──
/// Self-registers MessageReceived (DM text replies) and ButtonExecuted (the
/// Save/Cancel buttons, prefixed "gtwiz:"). Both filter to the member's active
/// session, so this coexists with every other handler on those events. The
/// entry points (slash command + open button) live on
/// <see cref="GamertagCommandHandler"/>, which calls <see cref="StartAsync"/>.
///
/// ── Preserve-on-skip ──
/// On start we prefill the draft from the member's existing roster row. Each
/// prompt offers `keep` (leave as-is) and `clear` (remove); a bare reply
/// replaces the value. So updating one tag never wipes the others.
///
/// ── Sessions ──
/// In-memory, keyed by user id, expired after <see cref="IdleTimeout"/> of
/// inactivity and removed on completion/cancel.
/// </summary>
public sealed partial class GamertagWizard
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private const string Prefix = "gtwiz:";

    private static readonly Regex DiscriminatorPattern = MyRegex();

    private readonly ConcurrentDictionary<ulong, GamertagWizardSession> _sessions = new();

    private readonly GoogleSheetsService _sheetsService;
    private readonly OnboardingReminderHandler _onboardingReminder;
    private readonly ILogger<GamertagWizard> _logger;
    private readonly System.Threading.Timer _idleSweep;

    public GamertagWizard(
        GoogleSheetsService sheetsService,
        OnboardingReminderHandler onboardingReminder,
        ILogger<GamertagWizard> logger)
    {
        _sheetsService     = sheetsService;
        _onboardingReminder = onboardingReminder;
        _logger            = logger;

        // Proactively time out abandoned DM sessions: every minute, DM anyone
        // who's gone idle past IdleTimeout and drop their session.
        _idleSweep = new System.Threading.Timer(_ => _ = SweepIdleAsync(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceivedAsync;
        client.ButtonExecuted  += OnButtonExecutedAsync;
    }

    // ─── Entry point (called by GamertagCommandHandler) ────────────────────

    /// <summary>
    /// Opens a DM and starts the wizard. Returns false if the member's DMs are
    /// closed (the caller surfaces that). Prefills the draft from the roster so
    /// skipped fields keep their existing values.
    /// </summary>
    public async Task<bool> StartAsync(IUser user, ulong guildId)
    {
        PruneExpired();

        IDMChannel dm;
        try
        {
            dm = await user.CreateDMChannelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not open DM with {User} for /gamertags", user.Id);
            return false;
        }

        var discordName = user.GlobalName ?? user.Username;

        // Prefill from the existing roster row so "keep"/skip preserves tags.
        GamertagLookupResult? existing = null;
        try
        {
            existing = await _sheetsService.LookupGamertagsAsync(user.Id, discordName);
        }
        catch (Exception ex)
        {
            // Non-fatal: if the lookup fails we just start from blanks.
            _logger.LogWarning(ex, "Could not prefill existing gamertags for {User}", user.Id);
        }

        var session = new GamertagWizardSession
        {
            Dm             = dm,
            StartedAt      = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            Step           = GamertagWizardStep.Ea,
            Draft = new GamertagDraft
            {
                GuildId     = guildId,
                UserId      = user.Id,
                DiscordName = discordName,
                HadExisting = existing is not null,
                Ea          = existing?.EA     ?? string.Empty,
                Steam       = existing?.Steam  ?? string.Empty,
                Psn         = existing?.PSN    ?? string.Empty,
                Xbox        = existing?.Xbox   ?? string.Empty,
                Embark      = existing?.Embark ?? string.Empty,
                Bungie      = existing?.Bungie ?? string.Empty,
            },
        };
        _sessions[user.Id] = session;

        try
        {
            var intro = existing is null
                ? "🎮 **Let's register your gamertags!** I'll ask about one platform at a time.\n\n" +
                  "For any platform you don't use, just type `skip`. Type `cancel` anytime to quit — nothing is saved until the end."
                : "🎮 **Let's update your gamertags!** I'll go through one platform at a time and show what you've got now.\n\n" +
                  "Send a new tag to change it, type `keep` to leave it, or `clear` to remove it. Type `cancel` to quit — nothing is saved until the end.";
            await dm.SendMessageAsync(intro);
            await PromptStepAsync(session);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not send opening DM to {User} for /gamertags", user.Id);
            _sessions.TryRemove(user.Id, out _);
            return false;
        }
    }

    // ─── DM text replies ───────────────────────────────────────────────────

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage) return;
        if (message.Channel is not IDMChannel) return;
        if (!_sessions.TryGetValue(message.Author.Id, out var s)) return;

        if (IsExpired(s))
        {
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, "⌛ That gamertag setup expired from inactivity and **nothing was saved**. Run `/gamertags` to start over.");
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var text = message.Content.Trim();

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, "❌ Cancelled. Nothing was saved. Run `/gamertags` to start again anytime.");
            return;
        }

        try
        {
            switch (s.Step)
            {
                case GamertagWizardStep.Ea:     await HandlePlatformAsync(s, text, "EA",     v => s.Draft.Ea     = v, GamertagWizardStep.Steam);  break;
                case GamertagWizardStep.Steam:  await HandlePlatformAsync(s, text, "Steam",  v => s.Draft.Steam  = v, GamertagWizardStep.Psn);    break;
                case GamertagWizardStep.Psn:    await HandlePlatformAsync(s, text, "PSN",    v => s.Draft.Psn    = v, GamertagWizardStep.Xbox);   break;
                case GamertagWizardStep.Xbox:   await HandlePlatformAsync(s, text, "Xbox",   v => s.Draft.Xbox   = v, GamertagWizardStep.Embark); break;
                case GamertagWizardStep.Embark: await HandlePlatformAsync(s, text, "Embark", v => s.Draft.Embark = v, GamertagWizardStep.Bungie, requireDiscriminator: true); break;
                case GamertagWizardStep.Bungie: await HandlePlatformAsync(s, text, "Bungie", v => s.Draft.Bungie = v, GamertagWizardStep.Confirm, requireDiscriminator: true); break;
                // Confirm is a button step — ignore stray text.
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gamertag wizard step {Step} failed for {User}", s.Step, message.Author.Id);
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, "Something went wrong. Run `/gamertags` to start over.");
        }
    }

    /// <summary>
    /// Handles one platform step: interprets keep/skip/clear keywords, validates
    /// (Embark/Bungie need a Name#1234 discriminator), stores the value, and
    /// advances to <paramref name="next"/>.
    /// </summary>
    private async Task HandlePlatformAsync(
        GamertagWizardSession s, string text, string platform,
        Action<string> set, GamertagWizardStep next,
        bool requireDiscriminator = false)
    {
        var lower = text.ToLowerInvariant();

        if (lower is "keep" or "skip" or "next" or "leave")
        {
            // Leave the current value (which may be empty) untouched.
        }
        else if (lower is "clear" or "none" or "remove" or "delete" or "n/a")
        {
            set(string.Empty);
        }
        else
        {
            if (requireDiscriminator && !DiscriminatorPattern.IsMatch(text))
            {
                await s.Dm.SendMessageAsync(embed: Form(
                    $"⚠️ {platform} format",
                    $"That doesn't look right. **{platform}** tags look like `Name#1234` (a name, then `#`, then 4 digits).\n\n" +
                    "Try again, or type `skip`."));
                return; // stay on this step
            }
            set(text);
        }

        await AdvanceAsync(s, next);
    }

    private async Task AdvanceAsync(GamertagWizardSession s, GamertagWizardStep next)
    {
        s.Step = next;
        if (next == GamertagWizardStep.Confirm)
            await PromptConfirmAsync(s);
        else
            await PromptStepAsync(s);
    }

    // ─── Prompts ───────────────────────────────────────────────────────────

    private static readonly (GamertagWizardStep Step, string Name, string Hint)[] PlatformMeta =
    {
        (GamertagWizardStep.Ea,     "EA",     ""),
        (GamertagWizardStep.Steam,  "Steam",  ""),
        (GamertagWizardStep.Psn,    "PSN",    ""),
        (GamertagWizardStep.Xbox,   "Xbox",   ""),
        (GamertagWizardStep.Embark, "Embark", "e.g. `Guardian#7028`"),
        (GamertagWizardStep.Bungie, "Bungie", "e.g. `Guardian#1234`"),
    };

    private async Task PromptStepAsync(GamertagWizardSession s)
    {
        var meta = PlatformMeta.First(m => m.Step == s.Step);
        var current = meta.Step switch
        {
            GamertagWizardStep.Ea     => s.Draft.Ea,
            GamertagWizardStep.Steam  => s.Draft.Steam,
            GamertagWizardStep.Psn    => s.Draft.Psn,
            GamertagWizardStep.Xbox   => s.Draft.Xbox,
            GamertagWizardStep.Embark => s.Draft.Embark,
            GamertagWizardStep.Bungie => s.Draft.Bungie,
            _                         => string.Empty,
        };

        var stepNum = Array.FindIndex(PlatformMeta, m => m.Step == s.Step) + 1;
        var title   = $"🎮 {meta.Name} tag  ·  {stepNum}/6";

        string body;
        if (!string.IsNullOrWhiteSpace(current))
        {
            body = $"Current: **{current}**\n\nSend a new tag to change it, type `keep` to leave it, or `clear` to remove it.";
        }
        else
        {
            body = $"Send your **{meta.Name}** gamertag, or type `skip` if you don't have one.";
            if (!string.IsNullOrEmpty(meta.Hint))
                body += $"\n{meta.Hint}";
        }

        await s.Dm.SendMessageAsync(embed: Form(title, body));
    }

    private async Task PromptConfirmAsync(GamertagWizardSession s)
    {
        s.Step = GamertagWizardStep.Confirm;
        var d = s.Draft;

        string F(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

        var embed = new EmbedBuilder()
            .WithTitle("📋 Confirm your gamertags")
            .WithColor(new Color(0x5865F2))
            .AddField("EA",     F(d.Ea),     true)
            .AddField("Steam",  F(d.Steam),  true)
            .AddField("PSN",    F(d.Psn),    true)
            .AddField("Xbox",   F(d.Xbox),   true)
            .AddField("Embark", F(d.Embark), true)
            .AddField("Bungie", F(d.Bungie), true)
            .WithFooter("Save writes these to the roster sheet. Nothing has been saved yet.")
            .Build();

        var buttons = new ComponentBuilder()
            .WithButton("Save", $"{Prefix}save",   ButtonStyle.Success)
            .WithButton("Cancel", $"{Prefix}cancel", ButtonStyle.Danger)
            .Build();

        await s.Dm.SendMessageAsync(
            "Here's everything — save it?", embed: embed, components: buttons);
    }

    // ─── Buttons (Save / Cancel) ───────────────────────────────────────────

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(Prefix, StringComparison.Ordinal)) return;

        if (!_sessions.TryGetValue(component.User.Id, out var s))
        {
            await ClearButtons(component, "That gamertag setup has expired. Run `/gamertags` to start again.");
            return;
        }
        if (IsExpired(s))
        {
            _sessions.TryRemove(component.User.Id, out _);
            await ClearButtons(component, "⌛ Timed out. Run `/gamertags` again.");
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;

        var kind = component.Data.CustomId.Substring(Prefix.Length);
        try
        {
            switch (kind)
            {
                case "save":   await OnSaveAsync(s, component);   break;
                case "cancel": await OnCancelAsync(s, component); break;
                default:       await component.DeferAsync();      break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gamertag wizard button {CustomId} failed", component.Data.CustomId);
        }
    }

    private async Task OnSaveAsync(GamertagWizardSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.Draft.UserId, out _);

        // Ack + strip buttons immediately (also prevents a double-submit). The
        // sheet write can exceed Discord's 3-second interaction window.
        try
        {
            await c.UpdateAsync(m =>
            {
                m.Content    = "⏳ Saving your gamertags…";
                m.Embed      = null;
                m.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Save ack (UpdateAsync) failed for {User}", s.Draft.UserId);
        }

        var d = s.Draft;
        try
        {
            await _sheetsService.WriteGamertagsAsync(
                d.UserId, d.DiscordName, d.Ea, d.Steam, d.Psn, d.Xbox, d.Embark, d.Bungie);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save gamertags for {User}", d.UserId);
            await FinalizeAsync(c, "❌ Failed to save your gamertags. Please try `/gamertags` again or contact an admin.");
            return;
        }

        string Field(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v;
        var saved = new EmbedBuilder()
            .WithTitle("🎮 Gamertags Saved!")
            .WithColor(Color.Green)
            .AddField("EA",     Field(d.Ea),     true)
            .AddField("Steam",  Field(d.Steam),  true)
            .AddField("PSN",    Field(d.Psn),    true)
            .AddField("Xbox",   Field(d.Xbox),   true)
            .AddField("Embark", Field(d.Embark), true)
            .AddField("Bungie", Field(d.Bungie), true)
            .WithFooter("Your gamertags have been exported to the roster sheet.")
            .Build();

        try { await c.Channel.SendMessageAsync(embed: saved); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send saved confirmation to {User}", d.UserId); }

        await FinalizeAsync(c, "✅ **Saved!** You can run `/gamertags` again anytime to update them.");

        // Triggers a 24h onboarding reminder cleanup if this member was still a Guest.
        if (d.GuildId != 0)
            _onboardingReminder.NotifyGamertagCompleted(d.GuildId, d.UserId);
    }

    private async Task OnCancelAsync(GamertagWizardSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.Draft.UserId, out _);
        await ClearButtons(c, "❌ Cancelled. Nothing was saved.");
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static readonly Color FormColor = new(0x5865F2);

    /// <summary>Builds the consistent "form" embed used for every wizard prompt.</summary>
    private static Embed Form(string title, string? body = null)
    {
        var eb = new EmbedBuilder()
            .WithColor(FormColor)
            .WithTitle(title)
            .WithFooter("Reply in this DM • type \"cancel\" to quit • times out after 15 min");
        if (!string.IsNullOrWhiteSpace(body)) eb.WithDescription(body);
        return eb.Build();
    }

    /// <summary>Times out idle sessions: DMs the member, then drops the session.</summary>
    private async Task SweepIdleAsync()
    {
        try
        {
            foreach (var kv in _sessions)
            {
                if (!IsExpired(kv.Value)) continue;
                if (_sessions.TryRemove(kv.Key, out var s))
                {
                    try
                    {
                        await s.Dm.SendMessageAsync(embed: Form(
                            "⌛ Gamertag setup timed out",
                            "Looks like you stepped away — I've cancelled this setup and **nothing was saved**. Run `/gamertags` whenever you're ready to start again."));
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to send gamertag timeout DM to {User}", kv.Key); }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Gamertag wizard idle sweep failed"); }
    }

    private static bool IsExpired(GamertagWizardSession s) =>
        DateTime.UtcNow - s.LastActivityAt > IdleTimeout;

    private void PruneExpired()
    {
        foreach (var kv in _sessions)
            if (IsExpired(kv.Value))
                _sessions.TryRemove(kv.Key, out _);
    }

    private async Task ClearButtons(SocketMessageComponent c, string content)
    {
        try
        {
            await c.UpdateAsync(m =>
            {
                m.Content    = content;
                m.Embed      = null;
                m.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to clear gamertag wizard buttons"); }
    }

    /// <summary>
    /// Edits the already-acknowledged Save message to its final text. Used on the
    /// save path because that path already called UpdateAsync (the immediate ack),
    /// and a component interaction can only be updated once.
    /// </summary>
    private async Task FinalizeAsync(SocketMessageComponent c, string content)
    {
        try
        {
            await c.ModifyOriginalResponseAsync(m =>
            {
                m.Content    = content;
                m.Embed      = null;
                m.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to finalize gamertag save message"); }
    }

    private async Task SafeSend(GamertagWizardSession s, string content)
    {
        try { await s.Dm.SendMessageAsync(content); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send gamertag wizard DM to {User}", s.Draft.UserId); }
    }

    [GeneratedRegex(@"^.+#\d{4}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
