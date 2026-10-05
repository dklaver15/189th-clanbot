using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The DM gamertag wizard. Triggered by /gamertags (or the "Enter Gamertags"
/// button), it collects the member's six platform tags and writes them to the
/// roster sheet.
///
/// ── Single-board design ──
/// The whole wizard is ONE message (the "board"), posted once on start and
/// edited in place at every step. The board shows a checklist of all six
/// platforms (answered ones with their value, the current one highlighted, the
/// rest pending) plus the current question and its buttons. Because we never
/// post a second message, the board never moves and Discord never has to scroll
/// — which sidesteps the client quirk where a freshly-posted button row gets
/// clipped below the fold.
///
/// ── Why not a modal? ──
/// The old flow was a two-page modal (Discord caps modals at 5 inputs, we need
/// 6) and members routinely missed page 2. A DM board has no field cap and shows
/// everything at once.
///
/// ── Routing ──
/// Self-registers MessageReceived (typed replies) and ButtonExecuted (buttons,
/// prefixed "gtwiz:"). Both filter to the member's active session. The entry
/// points live on <see cref="GamertagCommandHandler"/>, which calls
/// <see cref="StartAsync"/>.
///
/// ── Preserve-on-skip ──
/// On start the board is prefilled from the member's existing roster row, so
/// Keep leaves a tag as-is and Clear removes it; updating one tag never wipes
/// the others.
///
/// ── Sessions ──
/// In-memory, keyed by user id, expired after <see cref="IdleTimeout"/> and
/// removed on completion/cancel. A restart drops in-progress sessions; the
/// member just re-runs /gamertags.
/// </summary>
public sealed partial class GamertagWizard
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private const string Prefix = "gtwiz:";

    private static readonly Regex DiscriminatorPattern = MyRegex();

    /// <summary>The six platform steps, in the order they're asked.</summary>
    private static readonly GamertagWizardStep[] PlatformOrder =
    {
        GamertagWizardStep.Ea,
        GamertagWizardStep.Steam,
        GamertagWizardStep.Psn,
        GamertagWizardStep.Xbox,
        GamertagWizardStep.Embark,
        GamertagWizardStep.Bungie,
        GamertagWizardStep.YouTube,
    };

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
    /// Opens a DM and posts the board. Returns false if the member's DMs are
    /// closed. Prefills the draft from the roster so Keep/Skip preserve tags.
    /// </summary>
    public async Task<bool> StartAsync(IUser user, ulong guildId)
    {
        PruneExpired();

        var (dm, dmFailure) = await DmGuard.TryOpenAsync(user);
        if (dm is null)
        {
            _logger.LogInformation("Could not open DM with {User} for /gamertags: {Failure}", user.Id, dmFailure);
            return false;
        }

        var discordName = user.GlobalName ?? user.Username;

        // Prefill from the existing roster row so Keep/Skip preserve tags. A read
        // FAILURE must not start the wizard from blanks — a member who then skips
        // through would overwrite their saved tags with empties on Save. So abort
        // on exception and let them retry. (A genuine "no row yet" returns null,
        // which is fine: that's a real first-time member starting empty.)
        GamertagLookupResult? existing;
        try
        {
            existing = await _sheetsService.LookupGamertagsAsync(user.Id, discordName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gamertag prefill lookup failed for {User}; aborting to avoid data loss", user.Id);
            try
            {
                await dm.SendMessageAsync(
                    "⚠️ I couldn't load your current gamertags from the roster just now, so I didn't start — **your existing tags are safe**. Please try `/gamertags` again in a moment.");
            }
            catch { /* DM failed too — nothing more we can do */ }
            return true; // we reached their DMs (with the error), so don't report a DM failure
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
                YouTube     = existing?.YouTube ?? string.Empty,
            },
        };
        _sessions[user.Id] = session;

        try
        {
            session.BoardMessage = await dm.SendMessageAsync(
                embed: BuildBoardEmbed(session), components: BuildBoardComponents(session));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not send opening board to {User} for /gamertags", user.Id);
            _sessions.TryRemove(user.Id, out _);
            return false;
        }
    }

    // ─── Typed replies ─────────────────────────────────────────────────────

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage) return;
        if (message.Channel is not IDMChannel) return;
        if (!_sessions.TryGetValue(message.Author.Id, out var s)) return;

        if (IsExpired(s))
        {
            _sessions.TryRemove(message.Author.Id, out _);
            await RepostBoardAsync(s, BuildClosingEmbed("⌛ Gamertag setup timed out",
                "That setup expired from inactivity and **nothing was saved**. Run `/gamertags` to start over."));
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var text = message.Content.Trim();

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _sessions.TryRemove(message.Author.Id, out _);
            await RepostBoardAsync(s, BuildClosingEmbed("❌ Cancelled",
                "Nothing was saved. Run `/gamertags` to start again anytime."));
            return;
        }

        // Confirm is a button-only step — ignore stray text there.
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
    /// Handles a typed reply on a platform step: keep/skip/clear keywords,
    /// validation (Embark/Bungie need Name#1234), stores the value, advances, and
    /// re-renders the board in place. Buttons are the primary path, but typing
    /// stays supported.
    /// </summary>
    private async Task HandlePlatformTextAsync(GamertagWizardSession s, string text)
    {
        var step  = s.Step;
        var lower = text.ToLowerInvariant();

        if (lower is "keep" or "skip" or "next" or "leave")
        {
            // Leave the current value (possibly empty) untouched.
        }
        else if (lower is "clear" or "none" or "remove" or "delete" or "n/a")
        {
            SetField(s.Draft, step, string.Empty);
        }
        else
        {
            if (RequiresDiscriminator(step) && !DiscriminatorPattern.IsMatch(text))
            {
                // Repost so the inline error lands below the member's bad input.
                await RepostBoardAsync(s, BuildBoardEmbed(s, error:
                    $"⚠️ **{PlatformName(step)}** tags look like `Name#1234` (a name, then `#`, then 4 digits). Try again, or use the buttons."),
                    BuildBoardComponents(s));
                return;
            }
            SetField(s.Draft, step, text);
        }

        await AdvanceAsync(s, NextStep(step), viaInteraction: null);
    }

    // ─── Buttons ───────────────────────────────────────────────────────────

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(Prefix, StringComparison.Ordinal)) return;

        if (!_sessions.TryGetValue(component.User.Id, out var s))
        {
            await RespondStaleAsync(component);
            return;
        }
        if (IsExpired(s))
        {
            _sessions.TryRemove(component.User.Id, out _);
            await RespondStaleAsync(component, "⌛ Timed out. Run `/gamertags` again.");
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;

        var parts = component.Data.CustomId.Split(':'); // gtwiz:<kind>[:<step>:<action>]
        var kind  = parts.Length > 1 ? parts[1] : string.Empty;
        try
        {
            switch (kind)
            {
                case "save":   await OnSaveAsync(s, component);          break;
                case "cancel": await OnCancelAsync(s, component);        break;
                case "set":    await OnSetButtonAsync(s, component, parts); break;
                default:       await component.DeferAsync();             break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gamertag wizard button {CustomId} failed", component.Data.CustomId);
        }
    }

    /// <summary>
    /// Handles a Keep / Skip / Clear button. The CustomId carries the step it
    /// belongs to, so a click on a stale board state (step mismatch) is ignored.
    /// </summary>
    private async Task OnSetButtonAsync(GamertagWizardSession s, SocketMessageComponent c, string[] parts)
    {
        // Acknowledge immediately (deferred update: no visual change) so we never
        // miss Discord's 3-second window even if the bot was briefly busy.
        try
        {
            await c.DeferAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Set-button defer failed for {User}", s.Draft.UserId);
            return;
        }

        if (parts.Length < 4) return;

        var stepStr = parts[2];
        var action  = parts[3];

        if (!string.Equals(stepStr, s.Step.ToString(), StringComparison.Ordinal)) return;

        var step = s.Step;
        switch (action)
        {
            case "keep":
            case "skip":
                break;
            case "clear":
                SetField(s.Draft, step, string.Empty);
                break;
            default:
                return;
        }

        await AdvanceAsync(s, NextStep(step), viaInteraction: c);
    }

    private async Task OnCancelAsync(GamertagWizardSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.Draft.UserId, out _);
        await AckAsync(c);
        await EditBoardViaInteractionAsync(c, BuildClosingEmbed("❌ Cancelled", "Nothing was saved."), null);
    }

    private async Task OnSaveAsync(GamertagWizardSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.Draft.UserId, out _);
        await AckAsync(c);

        var d = s.Draft;

        // Show progress while the sheet write happens (can exceed the 3s window).
        await EditBoardViaInteractionAsync(c, BuildClosingEmbed("⏳ Saving…", "Writing your gamertags to the roster sheet."), null);

        try
        {
            await _sheetsService.WriteGamertagsAsync(
                d.UserId, d.DiscordName, d.Ea, d.Steam, d.Psn, d.Xbox, d.Embark, d.Bungie, d.YouTube);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save gamertags for {User}", d.UserId);

            // Surface a short reason so an admin reading the DM has a clue without
            // digging through logs. The full stack trace is logged above.
            var reason = ex is Google.GoogleApiException g
                ? $"Google Sheets returned {(int)g.HttpStatusCode} ({g.HttpStatusCode})."
                : ex.Message;
            if (reason.Length > 200) reason = reason[..200] + "…";

            await EditBoardViaInteractionAsync(c, BuildClosingEmbed("❌ Save failed",
                "Couldn't write to the roster sheet. Please try `/gamertags` again or contact an admin.\n\n" +
                $"-# Reason: {reason}"), null);
            return;
        }

        await EditBoardViaInteractionAsync(c, BuildSavedEmbed(d), null);

        if (d.GuildId != 0)
            _onboardingReminder.NotifyGamertagCompleted(d.GuildId, d.UserId);
    }

    /// <summary>
    /// Advances to the next step and re-renders the board. A button click edits
    /// the board in place (it's already the newest message, so it stays put). A
    /// typed reply reposts the board at the bottom instead — otherwise the
    /// member's typed tags would stack below it and push the card out of view.
    /// </summary>
    private async Task AdvanceAsync(GamertagWizardSession s, GamertagWizardStep next, SocketMessageComponent? viaInteraction)
    {
        s.Step = next;
        var embed   = BuildBoardEmbed(s);
        var buttons = BuildBoardComponents(s);

        if (viaInteraction is not null)
            await EditBoardViaInteractionAsync(viaInteraction, embed, buttons);
        else
            await RepostBoardAsync(s, embed, buttons);
    }

    // ─── Board rendering ───────────────────────────────────────────────────

    private static readonly Color FormColor = new(0x5865F2);

    /// <summary>Builds the board embed: the six-platform checklist plus the current question (or an inline error / confirm prompt).</summary>
    private Embed BuildBoardEmbed(GamertagWizardSession s, string? error = null)
    {
        var d          = s.Draft;
        var currentIdx = Array.IndexOf(PlatformOrder, s.Step); // -1 at Confirm
        var atConfirm  = s.Step == GamertagWizardStep.Confirm;

        var list = new StringBuilder();
        for (var i = 0; i < PlatformOrder.Length; i++)
        {
            var p     = PlatformOrder[i];
            var name  = PlatformName(p);
            var val   = GetField(d, p);
            var shown = string.IsNullOrWhiteSpace(val) ? "—" : val;

            // Show the value on EVERY row (including not-yet-reached ones) so a
            // returning member sees their whole prefilled roster up front and
            // knows nothing was lost. The marker just tracks progress: ✅ done,
            // ▶️ current, ▫️ still to come.
            var marker = atConfirm || i < currentIdx ? "✅"
                       : i == currentIdx             ? "▶️"
                       :                               "▫️";
            list.AppendLine($"{marker} **{name}** — {shown}");
        }

        var eb = new EmbedBuilder()
            .WithColor(FormColor)
            .WithDescription(list.ToString());

        if (atConfirm)
        {
            eb.WithTitle("📋 Confirm your gamertags")
              .AddField("Ready to save?", "Tap **Save** to write these to the roster sheet, or **Cancel** to discard.")
              .WithFooter("Nothing has been saved yet.");
        }
        else
        {
            var name = PlatformName(s.Step);
            var val  = GetField(d, s.Step);
            eb.WithTitle($"🎮 Your gamertags  ·  {StepNumber(s.Step)}/{PlatformOrder.Length}");

            string q;
            if (!string.IsNullOrWhiteSpace(val))
            {
                q = $"Current: **{val}**\nSend a new tag to change it, or use the buttons below.";
            }
            else
            {
                q = $"Send your **{name}** tag, or tap **Skip**.";
                var hint = PlatformHint(s.Step);
                if (!string.IsNullOrEmpty(hint)) q += $"\n{hint}";
            }

            if (!string.IsNullOrWhiteSpace(error)) q = $"{error}\n\n{q}";
            eb.AddField($"Now: {name}", q);
            eb.WithFooter("Reply in this DM or use the buttons • \"cancel\" to quit • times out after 15 min");
        }

        return eb.Build();
    }

    /// <summary>Buttons for the current step: Keep/Clear or Skip on platform steps; Save/Cancel at Confirm.</summary>
    private static MessageComponent BuildBoardComponents(GamertagWizardSession s)
    {
        var b = new ComponentBuilder();

        if (s.Step == GamertagWizardStep.Confirm)
        {
            b.WithButton("Save",   $"{Prefix}save",   ButtonStyle.Success)
             .WithButton("Cancel", $"{Prefix}cancel", ButtonStyle.Danger);
            return b.Build();
        }

        var step     = s.Step;
        var hasValue = !string.IsNullOrWhiteSpace(GetField(s.Draft, step));
        if (hasValue)
        {
            b.WithButton("Keep",   $"{Prefix}set:{step}:keep",  ButtonStyle.Primary)
             .WithButton("Clear",  $"{Prefix}set:{step}:clear", ButtonStyle.Secondary)
             .WithButton("Cancel", $"{Prefix}cancel",           ButtonStyle.Danger);
        }
        else
        {
            b.WithButton("Skip",   $"{Prefix}set:{step}:skip", ButtonStyle.Primary)
             .WithButton("Cancel", $"{Prefix}cancel",          ButtonStyle.Danger);
        }
        return b.Build();
    }

    /// <summary>The final "saved" board: every platform checked, with its value.</summary>
    private Embed BuildSavedEmbed(GamertagDraft d)
    {
        var list = new StringBuilder();
        foreach (var p in PlatformOrder)
        {
            var val = GetField(d, p);
            list.AppendLine($"✅ **{PlatformName(p)}** — {(string.IsNullOrWhiteSpace(val) ? "—" : val)}");
        }

        return new EmbedBuilder()
            .WithColor(Color.Green)
            .WithTitle("🎮 Gamertags Saved!")
            .WithDescription(list.ToString())
            .WithFooter("Exported to the roster sheet • run /gamertags anytime to update.")
            .Build();
    }

    private static Embed BuildClosingEmbed(string title, string body) =>
        new EmbedBuilder()
            .WithColor(FormColor)
            .WithTitle(title)
            .WithDescription(body)
            .Build();

    // ─── Board edit plumbing ───────────────────────────────────────────────

    /// <summary>Edits the board in place via the stored handle (used by the idle-timeout sweep, where no new user message triggered it).</summary>
    private async Task EditBoardAsync(GamertagWizardSession s, Embed embed, MessageComponent? components = null)
    {
        if (s.BoardMessage is null) return;
        try
        {
            await s.BoardMessage.ModifyAsync(m =>
            {
                m.Embed      = embed;
                m.Components = components ?? new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to edit gamertag board for {User}", s.Draft.UserId);
        }
    }

    /// <summary>
    /// Reposts the board as a fresh message at the bottom and deletes the old
    /// one — used on the typed-reply path so the card follows the member's typed
    /// tags instead of scrolling out of view above them. New message first, so a
    /// failed delete still leaves exactly one live board.
    /// </summary>
    private async Task RepostBoardAsync(GamertagWizardSession s, Embed embed, MessageComponent? components = null)
    {
        var old = s.BoardMessage;
        try
        {
            s.BoardMessage = await s.Dm.SendMessageAsync(
                embed: embed, components: components ?? new ComponentBuilder().Build());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to repost gamertag board for {User}", s.Draft.UserId);
            return;
        }

        if (old is not null)
        {
            try { await old.DeleteAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete previous gamertag board for {User}", s.Draft.UserId); }
        }
    }

    /// <summary>Edits the board through a button interaction (the board IS the interaction's message).</summary>
    private async Task EditBoardViaInteractionAsync(SocketMessageComponent c, Embed embed, MessageComponent? components)
    {
        try
        {
            await c.ModifyOriginalResponseAsync(m =>
            {
                m.Embed      = embed;
                m.Components = components ?? new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to edit gamertag board via interaction");
        }
    }

    /// <summary>Acknowledges a button interaction (deferred update — no visual change).</summary>
    private async Task AckAsync(SocketMessageComponent c)
    {
        try { await c.DeferAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Gamertag button ack failed"); }
    }

    /// <summary>A click arrived for a session that no longer exists — edit that stale board to an "expired" note.</summary>
    private async Task RespondStaleAsync(SocketMessageComponent c, string? message = null)
    {
        var embed = BuildClosingEmbed("That gamertag setup has expired",
            message is null ? "Run `/gamertags` to start again." : message + " Run `/gamertags` to start again.");
        try
        {
            await c.UpdateAsync(m =>
            {
                m.Embed      = embed;
                m.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clear stale gamertag board");
        }
    }

    // ─── Step metadata helpers ─────────────────────────────────────────────

    private static GamertagWizardStep NextStep(GamertagWizardStep step) => step switch
    {
        GamertagWizardStep.Ea     => GamertagWizardStep.Steam,
        GamertagWizardStep.Steam  => GamertagWizardStep.Psn,
        GamertagWizardStep.Psn    => GamertagWizardStep.Xbox,
        GamertagWizardStep.Xbox   => GamertagWizardStep.Embark,
        GamertagWizardStep.Embark => GamertagWizardStep.Bungie,
        GamertagWizardStep.Bungie => GamertagWizardStep.YouTube,
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
        GamertagWizardStep.YouTube => "YouTube",
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
        GamertagWizardStep.YouTube => d.YouTube,
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
            case GamertagWizardStep.YouTube: d.YouTube = value; break;
        }
    }

    private static int StepNumber(GamertagWizardStep step) =>
        Array.IndexOf(PlatformOrder, step) is var i && i >= 0 ? i + 1 : PlatformOrder.Length;

    // ─── Lifecycle ─────────────────────────────────────────────────────────

    private async Task SweepIdleAsync()
    {
        try
        {
            foreach (var kv in _sessions)
            {
                if (!IsExpired(kv.Value)) continue;
                if (_sessions.TryRemove(kv.Key, out var s))
                {
                    await EditBoardAsync(s, BuildClosingEmbed("⌛ Gamertag setup timed out",
                        "Looks like you stepped away — I've cancelled this setup and **nothing was saved**. Run `/gamertags` whenever you're ready."));
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

    private async Task SafeSend(GamertagWizardSession s, string content)
    {
        try { await s.Dm.SendMessageAsync(content); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send gamertag wizard DM to {User}", s.Draft.UserId); }
    }

    [GeneratedRegex(@"^.+#\d{4}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
