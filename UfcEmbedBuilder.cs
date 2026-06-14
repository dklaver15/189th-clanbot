using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.Services;

/// <summary>
/// Renders the UFC embeds: the day-before reminder, <c>/ufc-schedule</c>
/// (upcoming events), and <c>/ufc-results</c> (a finished card). Times render as
/// Discord &lt;t:unix&gt; markdown via <see cref="EventTimeParser.Stamp"/> so
/// each viewer sees their own zone. The "official picture" (a Wikipedia event
/// poster, resolved by <see cref="UfcEventPosterService"/>) is attached as the
/// embed image when available.
/// </summary>
public static class UfcEmbedBuilder
{
    private static readonly Color UfcRed = new(0xD20A0A);

    // ─── Reminder (day-before) ─────────────────────────────────────────────

    public static Embed BuildReminderEmbed(UfcEvent ev, string? posterUrl)
    {
        var startUtc = ev.StartUtc;

        var eb = new EmbedBuilder()
            .WithTitle($"🥊 Fight Night Incoming: {Name(ev)}")
            .WithColor(UfcRed);

        if (MainEvent(ev) is { } main)
            eb.AddField("Main Event", main, inline: false);

        if (startUtc is { } s)
            eb.AddField("Starts", $"{EventTimeParser.Stamp(s, 'F')}\n🕐 {EventTimeParser.Stamp(s, 'R')}", inline: false);

        eb.WithDescription("Grab your snacks — there's a card tomorrow. Hop in voice and watch with the clan! 🍿");
        eb.WithFooter("Watch legally on ESPN+ / UFC Fight Pass • times shown in your local zone");

        ApplyImage(eb, ev, posterUrl, posterAsImage: true);

        return eb.Build();
    }

    // ─── /ufc-schedule (upcoming) ──────────────────────────────────────────

    public static Embed BuildScheduleEmbed(IReadOnlyList<UfcEvent> upcoming, string? posterUrl)
    {
        var eb = new EmbedBuilder()
            .WithTitle("🥊 Upcoming UFC Events")
            .WithColor(UfcRed);

        if (upcoming.Count == 0)
        {
            eb.WithDescription("No upcoming events found right now. Check back later!");
            return eb.Build();
        }

        foreach (var ev in upcoming.Take(8))
        {
            var startUtc = ev.StartUtc;
            var when = startUtc is { } s
                ? $"{EventTimeParser.Stamp(s, 'F')} • {EventTimeParser.Stamp(s, 'R')}"
                : "Date TBA";

            var main = MainEvent(ev);
            var value = main is null ? when : $"{when}\n🥇 {main}";

            eb.AddField(Name(ev), value, inline: false);
        }

        eb.WithFooter("Watch legally on ESPN+ / UFC Fight Pass • times shown in your local zone");

        // A poster for the nearest event gives the list a visual anchor.
        ApplyImage(eb, upcoming[0], posterUrl, posterAsImage: true);

        return eb.Build();
    }

    // ─── /ufc-results ──────────────────────────────────────────────────────

    public static Embed BuildResultsEmbed(UfcEvent ev, string? posterUrl)
    {
        var eb = new EmbedBuilder()
            .WithTitle($"🏆 Results: {Name(ev)}")
            .WithColor(UfcRed);

        var startUtc = ev.StartUtc;
        if (startUtc is { } s)
            eb.WithDescription(EventTimeParser.Stamp(s, 'F'));

        var decided = ev.Fights
            .Where(f => f.Fighters.Count >= 2)
            .OrderByDescending(f => f.Order ?? int.MinValue)
            .ToList();

        if (decided.Count == 0)
        {
            eb.AddField("​", "No results posted yet for this event.", inline: false);
        }
        else
        {
            // 25-field embed cap; leave headroom, a card is ~13 bouts.
            foreach (var fight in decided.Take(20))
            {
                var header = string.IsNullOrWhiteSpace(fight.WeightClass) ? "Bout" : fight.WeightClass!;
                eb.AddField(header, FormatFightResult(fight), inline: false);
            }
        }

        eb.WithFooter("Results via API-Sports");

        ApplyImage(eb, ev, posterUrl, posterAsImage: false);

        return eb.Build();
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static string Name(UfcEvent ev) =>
        !string.IsNullOrWhiteSpace(ev.Name) ? ev.Name! : "UFC Event";

    /// <summary>
    /// Attaches the visual: the Wikipedia event poster when we have one (as a big
    /// image, or a corner thumbnail for results), otherwise falls back to a
    /// main-event fighter photo from the data feed — shown as a thumbnail since a
    /// headshot looks better small than stretched full-width. Many events
    /// (especially upcoming/oddly-named ones) have no Wikipedia article, so this
    /// fallback keeps the embed from being image-less.
    /// </summary>
    private static void ApplyImage(EmbedBuilder eb, UfcEvent ev, string? posterUrl, bool posterAsImage)
    {
        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            if (posterAsImage) eb.WithImageUrl(posterUrl);
            else eb.WithThumbnailUrl(posterUrl);
            return;
        }

        if (FighterImage(ev) is { } fighterImg)
            eb.WithThumbnailUrl(fighterImg);
    }

    /// <summary>A main-event fighter's photo URL from the feed, or null if none.</summary>
    private static string? FighterImage(UfcEvent ev)
    {
        var main = ev.Fights
            .OrderByDescending(f => f.Order ?? int.MinValue)
            .FirstOrDefault();

        return main?.Fighters
            .Select(f => f.Logo)
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
    }

    /// <summary>
    /// "Fighter A vs. Fighter B" for the main event — the bout with the highest
    /// card order (or the last listed when order is absent).
    /// </summary>
    private static string? MainEvent(UfcEvent ev)
    {
        var main = ev.Fights
            .Where(f => f.Fighters.Count >= 2)
            .OrderByDescending(f => f.Order ?? int.MinValue)
            .FirstOrDefault();

        if (main is null) return null;

        var a = main.Fighters[0].FullName;
        var b = main.Fighters[1].FullName;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return null;
        return $"{a} vs. {b}";
    }

    /// <summary>
    /// "Winner def. Loser by KO/TKO (R2, 3:45)" when decided; falls back to the
    /// matchup line while a bout is still pending.
    /// </summary>
    private static string FormatFightResult(UfcFight fight)
    {
        var a = fight.Fighters[0];
        var b = fight.Fighters[1];

        var winner = fight.Fighters.FirstOrDefault(f => f.Winner == true);
        var loser  = fight.Fighters.FirstOrDefault(f => f.Winner == false);

        if (winner is null || loser is null || string.IsNullOrWhiteSpace(winner.FullName))
        {
            // Undecided / no result yet — show the matchup.
            var an = string.IsNullOrWhiteSpace(a.FullName) ? "TBD" : a.FullName;
            var bn = string.IsNullOrWhiteSpace(b.FullName) ? "TBD" : b.FullName;
            return $"{an} vs. {bn} — *pending*";
        }

        // Method (KO/TKO, decision, …) and finishing round aren't always
        // available — the free results feed omits them — so show each only when
        // present rather than inventing a default.
        var method = string.IsNullOrWhiteSpace(fight.ResultType) ? null : fight.ResultType!.Trim();

        string? roundPart = null;
        if (fight.ResultRound is { } round && round > 0)
        {
            var clock = fight.ResultClock is { } secs && secs >= 0
                ? $", {secs / 60}:{secs % 60:00}"
                : "";
            roundPart = $"R{round}{clock}";
        }

        var tail = (method, roundPart) switch
        {
            (not null, not null) => $" by {method} ({roundPart})",
            (not null, null)     => $" by {method}",
            (null, not null)     => $" ({roundPart})",
            _                    => "",
        };

        return $"**{winner.FullName}** def. {loser.FullName}{tail}";
    }
}
