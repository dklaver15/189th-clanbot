using System.Text.Json.Serialization;

namespace ClanGuardBot.Models;

/// <summary>
/// DTOs for the SportsDataIO MMA "Scores" feed
/// (<see href="https://sportsdata.io/developers/api-documentation/mma"/>).
///
/// These mirror the documented JSON shape of the <c>Schedule</c> and
/// <c>Event</c> endpoints. We deserialize with
/// <c>PropertyNameCaseInsensitive = true</c> and every field is nullable, so a
/// minor schema drift (a renamed or dropped field) degrades to a blank in the
/// embed rather than throwing — the bot keeps working, you just lose that one
/// value until the DTO is updated.
///
/// ── Time zone ──
/// SportsDataIO returns all dates/times in <b>US Eastern</b> with no offset
/// suffix (e.g. <c>2026-07-12T22:00:00</c>). <see cref="Services.UfcApiService"/>
/// parses them as Eastern and converts to UTC before they reach Discord, so the
/// &lt;t:unix&gt; markdown renders in each viewer's own zone.
/// </summary>
public sealed class UfcEvent
{
    public int EventId { get; set; }
    public int? LeagueId { get; set; }

    /// <summary>Full event name, e.g. "UFC 300: Pereira vs. Hill".</summary>
    public string? Name { get; set; }

    public string? ShortName { get; set; }
    public int? Season { get; set; }

    /// <summary>Event date (Eastern), e.g. "2026-07-12T00:00:00".</summary>
    public string? Day { get; set; }

    /// <summary>Scheduled start (Eastern), e.g. "2026-07-12T22:00:00". May be null.</summary>
    public string? DateTime { get; set; }

    /// <summary>"Scheduled", "Final", "Canceled", etc.</summary>
    public string? Status { get; set; }

    public bool? Active { get; set; }

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

    [JsonIgnore]
    public string FullName =>
        string.Join(" ", new[] { FirstName, LastName }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}
