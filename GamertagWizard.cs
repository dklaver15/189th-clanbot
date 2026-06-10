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
                  "For each one, send your tag or tap **Skip**. Tap **Cancel** anytime to quit — nothing is saved until the end."
                : "🎮 **Let's update your gamertags!** I'll go through one platform at a time and show what you've got now.\n\n" +
                  "Send a new tag to change it, or use the **Keep** / **Clear** buttons. Tap **Cancel** to quit — nothing is saved until the end.";
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

        // Confirm is a button step — ignore stray text there.
        if (s.Step == GamertagWizardStep.Confirm) return;

        try
        {
            await HandlePlatformTextAsync(s, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gamertag wizard step {Step} failed for {User}", s.Step, message.Author.Id);
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, "Something went wrong. Run `/gamertags` to start over.");
        }
    }

    /// <summary>
    /// Handles a typed reply on a platform step: interprets keep/skip/clear
    /// keywords, validates (Embark/Bungie need a Name#1234 discriminator),
    /// stores the value, and advances. Buttons are the primary path, but typing
    /// stays supported.
    /// </summary>
    private async Task HandlePlatformTextAsync(GamertagWizardSession s, string text)
    {
        var step  = s.Step;
        var lower = text.ToLowerInvariant();

        if (lower is "keep" or "skip" or "next" or "leave")
        {
            // Leave the current value (which may be empty) untouched.
        }
        else if (lower is "clear" or "none" or "remove" or "delete" or "n/a")
        {
            SetField(s.Draft, step, string.Empty);
        }
        else
        {
            if (RequiresDiscriminator(step) && !DiscriminatorPattern.IsMatch(text))
            {
                await s.Dm.SendMessageAsync(embed: Form(
                    $"⚠️ {PlatformName(step)} format",
                    $"That doesn't look right. **{PlatformName(step)}** tags look like `Name#1234` (a name, then `#`, then 4 digits).\n\n" +
                    "Try again, or use the buttons above."));
                return; // stay on this step
            }
            SetField(s.Draft, step, text);
        }

        // Leave the answered prompt visible (embed shows the result) with its
        // buttons disabled, then move on.
        await ResolvePromptByEditAsync(s, step);
        await AdvanceAsync(s, NextStep(step));
    }

    private async Task AdvanceAsync(GamertagWizardSession s, GamertagWizardStep next)
    {
        s.Step = next;
        if (next == GamertagWizardStep.Confirm)
            await PromptConfirmAsync(s);
        else
            await PromptStepAsync(s);
    }

    // ─── Step metadata helpers ─────────────────────────────────────────────

    private static GamertagWizardStep NextStep(GamertagWizardStep step) => step switch
    {
        GamertagWizardStep.Ea     => GamertagWizardStep.Steam,
        GamertagWizardStep.Steam  => GamertagWizardStep.Psn,
        GamertagWizardStep.Psn    => GamertagWizardStep.Xbox,
        GamertagWizardStep.Xbox   => GamertagWizardStep.Embark,
        GamertagWizardStep.Embark => GamertagWizardStep.Bungie,
        _                         => GamertagWizardStep.Confirm,
    };

    private static bool RequiresDiscriminator(GamertagWizardStep step) =>
        step is GamertagWizardStep.Embark or GamertagWizardStep.Bungie;

    private static string PlatformName(GamertagWizardStep step) => step switch
    {
        GamertagWizardStep.Ea     => "EA",
        GamertagWizardStep.Steam  => "Steam",
        GamertagWizardStep.Psn    => "PSN",
        GamertagWizardStep.Xbox   => "Xbox",
        GamertagWizardStep.Embark => "Embark",
        GamertagWizardStep.Bungie => "Bungie",
        _                         => string.Empty,
    };

    private static string PlatformHint(GamertagWizardStep step) => step switch
    {
        GamertagWizardStep.Embark => "e.g. `Guardian#7028`",
        GamertagWizardStep.Bungie => "e.g. `Guardian#1234`",
        _                         => string.Empty,
    };

    private static string GetField(GamertagDraft d, GamertagWizardStep step) => step switch
    {
        GamertagWizardStep.Ea     => d.Ea,
        GamertagWizardStep.Steam  => d.Steam,
        GamertagWizardStep.Psn    => d.Psn,
        GamertagWizardStep.Xbox   => d.Xbox,
        GamertagWizardStep.Embark => d.Embark,
        GamertagWizardStep.Bungie => d.Bungie,
        _                         => string.Empty,
    };

    private static void SetField(GamertagDraft d, GamertagWizardStep step, string value)
    {
        switch (step)
        {
            case GamertagWizardStep.Ea:     d.Ea     = value; break;
            case GamertagWizardStep.Steam:  d.Steam  = value; break;
            case GamertagWizardStep.Psn:    d.Psn    = value; break;
            case GamertagWizardStep.Xbox:   d.Xbox   = value; break;
            case GamertagWizardStep.Embark: d.Embark = value; break;
            case GamertagWizardStep.Bungie: d.Bungie = value; break;
        }
    }

    private static int StepNumber(GamertagWizardStep step) => step switch
    {
        GamertagWizardStep.Ea     => 1,
        GamertagWizardStep.Steam  => 2,
        GamertagWizardStep.Psn    => 3,
        GamertagWizardStep.Xbox   => 4,
        GamertagWizardStep.Embark => 5,
        GamertagWizardStep.Bungie => 6,
        _                         => 6,
    };

    // ─── Prompts ───────────────────────────────────────────────────────────

    private async Task PromptStepAsync(GamertagWizardSession s)
    {
        var step    = s.Step;
        var name    = PlatformName(step);
        var current = GetField(s.Draft, step);
        var title   = $"🎮 {name} tag  ·  {StepNumber(step)}/6";
        var hasValue = !string.IsNullOrWhiteSpace(current);

        string body;
        var buttons = new ComponentBuilder();
        if (hasValue)
        {
            body = $"Current: **{current}**\n\nSend a new tag to change it, or use the buttons below.";
            buttons.WithButton("Keep",  $"{Prefix}set:{step}:keep",  ButtonStyle.Primary)
                   .WithButton("Clear", $"{Prefix}set:{step}:clear", ButtonStyle.Secondary)
                   .WithButton("Cancel", $"{Prefix}cancel",          ButtonStyle.Danger);
        }
        else
        {
            body = $"Send your **{name}** gamertag, or use the buttons below.";
            var hint = PlatformHint(step);
            if (!string.IsNullOrEmpty(hint)) body += $"\n{hint}";
            buttons.WithButton("Skip",  $"{Prefix}set:{step}:skip", ButtonStyle.Primary)
                   .WithButton("Cancel", $"{Prefix}cancel",         ButtonStyle.Danger);
        }

        s.LastPromptHadValue = hasValue;
        s.LastPromptMessage  = await s.Dm.SendMessageAsync(embed: Form(title, body), components: buttons.Build());
    }

    /// <summary>
    /// Resolves the current step's prompt after a typed reply: leaves the embed
    /// in place (now showing the resulting value) and swaps the buttons for a
    /// disabled set, so the answered prompt stays visible but inert.
    /// </summary>
    private async Task ResolvePromptByEditAsync(GamertagWizardSession s, GamertagWizardStep step)
    {
        var msg = s.LastPromptMessage;
        var hadValue = s.LastPromptHadValue;
        s.LastPromptMessage = null;
        if (msg is null) return;

        var embed   = BuildResolvedEmbed(step, GetField(s.Draft, step));
        var buttons = BuildDisabledButtons(step, hadValue);
        try { await msg.ModifyAsync(m => { m.Embed = embed; m.Components = buttons; }); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to resolve gamertag prompt for {User}", s.Draft.UserId); }
    }

    /// <summary>The answered-prompt embed: same title, now showing the final value (or "none").</summary>
    private static Embed BuildResolvedEmbed(GamertagWizardStep step, string value)
    {
        var shown = string.IsNullOrWhiteSpace(value) ? "*(none)*" : $"**{value}**";
        return new EmbedBuilder()
            .WithColor(ResolvedColor)
            .WithTitle($"✅ {PlatformName(step)} tag  ·  {StepNumber(step)}/6")
            .WithDescription(shown)
            .Build();
    }

    /// <summary>The same buttons the prompt showed, all disabled.</summary>
    private static MessageComponent BuildDisabledButtons(GamertagWizardStep step, bool hadValue)
    {
        var b = new ComponentBuilder();
        if (hadValue)
        {
            b.WithButton("Keep",  $"{Prefix}set:{step}:keep",  ButtonStyle.Primary,   disabled: true)
             .WithButton("Clear", $"{Prefix}set:{step}:clear", ButtonStyle.Secondary, disabled: true)
             .WithButton("Cancel", $"{Prefix}cancel",          ButtonStyle.Danger,    disabled: true);
        }
        else
        {
            b.WithButton("Skip",  $"{Prefix}set:{step}:skip", ButtonStyle.Primary, disabled: true)
             .WithButton("Cancel", $"{Prefix}cancel",         ButtonStyle.Danger,  disabled: true);
        }
        return b.Build();
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

        var parts = component.Data.CustomId.Split(':'); // gtwiz:<kind>[:<step>:<action>]
        var kind  = parts.Length > 1 ? parts[1] : string.Empty;
        try
        {
            switch (kind)
            {
                case "save":   await OnSaveAsync(s, component);   break;
                case "cancel": await OnCancelAsync(s, component); break;
                case "set":    await OnSetButtonAsync(s, component, parts); break;
                default:       await component.DeferAsync();      break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gamertag wizard button {CustomId} failed", component.Data.CustomId);
        }
    }

    /// <summary>
    /// Handles a Keep / Skip / Clear button on a platform step. The CustomId
    /// carries the step it belongs to, so a click on a stale earlier prompt
    /// (step mismatch) is ignored rather than mis-applied.
    /// </summary>
    private async Task OnSetButtonAsync(GamertagWizardSession s, SocketMessageComponent c, string[] parts)
    {
        // Acknowledge immediately with a deferred update (type 6): it changes
        // nothing on screen and doesn't resize the clicked message, so we never
        // miss Discord's 3-second window even if the bot was briefly busy (e.g.
        // mid-restart when a click arrives from another device). We do the real
        // work below and edit the message afterwards via the interaction.
        try
        {
            await c.DeferAsync();
        }
        catch (Exception ex)
        {
            // Token already expired/invalid (bot was down past the window) — the
            // member can just click again now that we're responsive.
            _logger.LogDebug(ex, "Set-button defer failed for {User}", s.Draft.UserId);
            return;
        }

        if (parts.Length < 4) return;

        var stepStr = parts[2];
        var action  = parts[3];

        // Guard against a click on a previous step's (now stale) buttons.
        if (!string.Equals(stepStr, s.Step.ToString(), StringComparison.Ordinal)) return;

        var step     = s.Step;
        var hadValue = s.LastPromptHadValue;

        switch (action)
        {
            case "keep":
            case "skip":
                // Leave the current value (possibly empty) untouched.
                break;
            case "clear":
                SetField(s.Draft, step, string.Empty);
                break;
            default:
                return;
        }

        // Build the resolved view of the clicked prompt now, before AdvanceAsync
        // changes s.Step.
        var embed   = BuildResolvedEmbed(step, GetField(s.Draft, step));
        var buttons = BuildDisabledButtons(step, hadValue);

        // Advance FIRST. Posting the next prompt as a brand-new message is what
        // makes Discord's client scroll to the bottom, and it only does so while
        // the member is still pinned there. If we resized the clicked message
        // first, a short prompt — like a "Skip" step — could nudge the view a few
        // pixels off the bottom and the next message wouldn't auto-scroll.
        // Editing the now-older clicked message afterwards doesn't move scroll.
        await AdvanceAsync(s, NextStep(step));

        // Resolve the clicked message in place: its embed stays (now showing the
        // result) with disabled buttons. After a deferred update, the clicked
        // message is the interaction's original response.
        try
        {
            await c.ModifyOriginalResponseAsync(m =>
            {
                m.Embed      = embed;
                m.Components = buttons;
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Set-button resolve failed for {User}", s.Draft.UserId);
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

    /// <summary>Muted grey for an answered/resolved prompt, so it visibly recedes behind the active one.</summary>
    private static readonly Color ResolvedColor = new(0x4E5058);

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
