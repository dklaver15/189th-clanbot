using System.Text.Json.Serialization;

namespace ClanGuardBot.Models;

/// <summary>
/// The normalized render model for UFC events, built from API-Sports MMA data by
/// <see cref="Services.UfcApiService"/> and consumed by
/// <see cref="Services.UfcEmbedBuilder"/>. One shape powers schedule, results,
/// and the day-before reminder. API-Sports dates are already UTC, so
/// <see cref="StartUtc"/> is stored directly — Discord's &lt;t:unix&gt; markdown
/// then localizes per viewer.
/// </summary>
public sealed class UfcEvent
{
    /// <summary>
    /// Stable identity for the event, used to dedupe reminders across restarts.
    /// The API-Sports card slug (the event name) — distinct per card.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Card name, e.g. "UFC Fight Night: Holloway vs. The Korean Zombie".</summary>
    public string? Name { get; set; }

    /// <summary>Event start in UTC, or null if unknown.</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>"Final", "Scheduled", etc.</summary>
    public string? Status { get; set; }

    public List<UfcFight> Fights { get; set; } = new();
}

public sealed class UfcFight
{
    public int FightId { get; set; }

    /// <summary>Bout order on the card (1 = first prelim). Used to find the main event (highest order).</summary>
    public int? Order { get; set; }

    public string? Status { get; set; }
    public string? WeightClass { get; set; }

    /// <summary>"Main Card", "Preliminary Card", "Early Preliminary Card".</summary>
    public string? CardSegment { get; set; }

    public string? Referee { get; set; }
    public int? Rounds { get; set; }

    public bool? Active { get; set; }

    // ── Result fields (populated once the bout is decided) ──
    public int? WinnerId { get; set; }

    /// <summary>"Decision", "KO/TKO", "Submission", etc.</summary>
    public string? ResultType { get; set; }

    public int? ResultRound { get; set; }

    /// <summary>Clock at the finish, in seconds elapsed in the final round.</summary>
    public int? ResultClock { get; set; }

    public List<UfcFighter> Fighters { get; set; } = new();
}

public sealed class UfcFighter
{
    public int FighterId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }

    /// <summary>True for the winner of a decided bout, false for the loser, null if undecided.</summary>
    public bool? Winner { get; set; }

    /// <summary>Pre-fight moneyline, when the betting subfeed is included.</summary>
    public int? Moneyline { get; set; }

    /// <summary>Fighter photo URL from the data feed (API-Sports "logo"). Used as a
    /// poster fallback when no Wikipedia event poster is found.</summary>
    public string? Logo { get; set; }

    [JsonIgnore]
    public string FullName =>
        string.Join(" ", new[] { FirstName, LastName }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}
