using Discord;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Steps of the DM gamertag wizard. Every step is driven by a DM reply; the
/// final Confirm step is button-only (Save / Cancel). The six platform steps
/// run in a fixed order so a member is walked through one tag at a time —
/// replacing the old two-page modal where the second page was easy to miss.
/// </summary>
public enum GamertagWizardStep
{
    Ea,
    Steam,
    Psn,
    Xbox,
    Embark,
    Bungie,
    Confirm, // button step
}

/// <summary>
/// The data collected by the gamertag wizard. Prefilled from the roster sheet
/// on start (so skipping a field preserves what the member already had) and
/// handed to <see cref="ClanGuardBot.Services.GoogleSheetsService.WriteGamertagsAsync"/>
/// on confirm.
/// </summary>
public sealed class GamertagDraft
{
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public string DiscordName { get; set; } = string.Empty;

    /// <summary>True when the member already had a roster row — drives "keep / clear" prompt wording.</summary>
    public bool HadExisting { get; set; }

    public string Ea { get; set; } = string.Empty;
    public string Steam { get; set; } = string.Empty;
    public string Psn { get; set; } = string.Empty;
    public string Xbox { get; set; } = string.Empty;
    public string Embark { get; set; } = string.Empty;
    public string Bungie { get; set; } = string.Empty;
}

/// <summary>
/// In-memory state for one member's active gamertag wizard session. Held in a
/// ConcurrentDictionary keyed by user id; expired on idle and removed on
/// completion/cancel. A bot restart drops in-progress sessions, which is
/// acceptable — the member simply re-runs /gamertags.
/// </summary>
public sealed class GamertagWizardSession
{
    public GamertagWizardStep Step { get; set; }
    public GamertagDraft Draft { get; set; } = new();
    public IDMChannel Dm { get; set; } = null!;
    public DateTime StartedAt { get; set; }
    public DateTime LastActivityAt { get; set; }

    /// <summary>
    /// The most recent platform prompt message, kept so it can be resolved once
    /// the step is answered (by click or by typing) — the embed stays visible
    /// with its buttons disabled, so all six choices remain on screen.
    /// </summary>
    public IUserMessage? LastPromptMessage { get; set; }

    /// <summary>Whether the current prompt offered Keep/Clear (true) or Skip (false) — drives the disabled-button layout when it's resolved.</summary>
    public bool LastPromptHadValue { get; set; }
}
