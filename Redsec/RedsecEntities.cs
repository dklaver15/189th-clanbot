namespace ClanGuardBot.Models;

/// <summary>A ranked REDSEC (Quads) win, recorded by hand with /redsec-add-win.</summary>
public class RedsecWin
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Battlefield season number the win was earned in, e.g. 4 for "Season 4".</summary>
    public int Season { get; set; }

    /// <summary>The clan-local (EventDefaultTimeZone) date of the win.</summary>
    public DateOnly WonOn { get; set; }

    public string? Notes { get; set; }

    public ulong RecordedByUserId { get; set; }
    public DateTime RecordedUtc { get; set; }
}

/// <summary>
/// A clan member on a <see cref="RedsecWin"/> squad. A win has 1 to 4 of these;
/// the rest of the squad were non-clan players.
/// </summary>
public class RedsecWinPlayer
{
    public int Id { get; set; }

    /// <summary>FK to <see cref="RedsecWin"/>.Id.</summary>
    public int RedsecWinId { get; set; }

    public ulong UserId { get; set; }

    /// <summary>Display name when the win was recorded. Used when the member has since left.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Kills plus assists in this win. Null when not entered; ranks as 0.</summary>
    public int? Engagements { get; set; }
}
