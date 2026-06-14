using System.Text.Json.Serialization;

namespace ClanGuardBot.Models;

/// <summary>
/// DTOs for the API-Sports MMA "fights" feed
/// (<see href="https://api-sports.io/documentation/mma/v1"/>), used by
/// <see cref="Services.UfcResultsService"/> to power <c>/ufc-results</c>.
///
/// Why a second provider: SportsDataIO's free tier scrambles result/stat fields
/// (methods, rounds, even winners), so it's unusable for results. API-Sports
/// returns real, unscrambled outcomes on its free tier (100 calls/day) — plenty
/// for a weekly results post. Schedule + reminders stay on SportsDataIO, which
/// is accurate for schedules even on the free tier.
///
/// Shape (per a live response): every API-Sports endpoint wraps its payload in
/// <c>{ get, parameters, errors, results, paging, response[] }</c>. A fight
/// carries the event name in <c>slug</c>, the weight class in <c>category</c>,
/// a UTC <c>date</c> (ISO-8601 with offset), <c>is_main</c>, a status block, and
/// <c>fighters.first/second</c> each with a <c>winner</c> flag. The free feed
/// does not expose a victory method / finishing round, so those are intentionally
/// absent — the embed shows the winner and omits method gracefully.
/// </summary>
public sealed class ApiSportsResponse<T>
{
    [JsonPropertyName("results")]
    public int Results { get; set; }

    [JsonPropertyName("response")]
    public List<T> Response { get; set; } = new();
}

public sealed class ApiSportsFight
{
    public int Id { get; set; }

    /// <summary>ISO-8601 with offset, e.g. "2023-08-26T09:00:00+00:00" (UTC).</summary>
    public string? Date { get; set; }

    public long? Timestamp { get; set; }

    /// <summary>Event/card name, e.g. "UFC Fight Night: Holloway vs. The Korean Zombie".</summary>
    public string? Slug { get; set; }

    /// <summary>True for the headline bout of the card.</summary>
    [JsonPropertyName("is_main")]
    public bool? IsMain { get; set; }

    /// <summary>Weight class, e.g. "Featherweight".</summary>
    public string? Category { get; set; }

    public ApiSportsStatus? Status { get; set; }

    public ApiSportsFighters? Fighters { get; set; }

    /// <summary>True once the bout has a final status (FT). Used to filter out scheduled/in-progress fights.</summary>
    [JsonIgnore]
    public bool IsFinished =>
        string.Equals(Status?.Short, "FT", StringComparison.OrdinalIgnoreCase)
        || (Status?.Long?.Contains("Finished", StringComparison.OrdinalIgnoreCase) ?? false);
}

public sealed class ApiSportsStatus
{
    public string? Long { get; set; }
    public string? Short { get; set; }
}

public sealed class ApiSportsFighters
{
    public ApiSportsFighter? First { get; set; }
    public ApiSportsFighter? Second { get; set; }
}

public sealed class ApiSportsFighter
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Logo { get; set; }
    public bool? Winner { get; set; }
}
