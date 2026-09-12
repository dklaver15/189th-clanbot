namespace ClanGuardBot.Services;

/// <summary>
/// The decision half of reconciling open Valheim sessions against the player count
/// the server reports on every DiscordConnector event.
///
/// ── Why this is its own file ──
/// Deliberately free of Discord, EF Core and every other dependency, so the rule
/// that decides whether to CLOSE someone's session can be exercised directly. It
/// is the highest-consequence logic in the Valheim integration — a wrong answer
/// silently truncates a real player's recorded hours — and it is pure arithmetic,
/// so there is no excuse for it to be reachable only through a database and a
/// gateway connection. <see cref="ValheimEventParser"/> is split out for the same
/// reason.
///
/// ── The problem being solved ──
/// The integration is event-sourced with no poll behind it (A2S is silent on a
/// crossplay server), so a dropped leave event leaves a session open forever. Every
/// event carries %NUM_PLAYERS%, so comparing that to our open-session count turns
/// each event into a reconciliation point — the heartbeat the design otherwise
/// lacks.
/// </summary>
public static class ValheimSessionReconciler
{
    /// <summary>
    /// How far the open-session count may exceed the server's reported count before
    /// it counts as a leak rather than noise.
    ///
    /// <para>One, because it is undocumented whether %NUM_PLAYERS% on a join counts
    /// the joining player, or whether on a leave it still counts the leaver — and the
    /// two may differ. A tolerance of one absorbs that ambiguity in either direction.
    /// The cost is never trimming a SINGLE stale session while others are online, and
    /// that case is covered anyway: the moment the server empties,
    /// <see cref="Decide"/>'s exact zero rule sweeps it up.</para>
    /// </summary>
    public const int CountTolerance = 1;

    /// <summary>
    /// Consecutive over-count events required before trimming. Three, so a real leak
    /// (which persists) is separated from a transient ordering artefact (which
    /// resolves itself on the next event).
    /// </summary>
    public const int EventsBeforeTrim = 3;

    /// <summary>How many sessions to close, and what the persistence counter becomes.</summary>
    /// <param name="Trim">Number of open sessions to close, oldest-seen first. 0 = do nothing.</param>
    /// <param name="Counter">The caller's new consecutive-over-count value.</param>
    public readonly record struct Decision(int Trim, int Counter);

    /// <summary>
    /// Decides what to do given how many sessions we believe are open, how many the
    /// server says are connected, and how many consecutive events have disagreed.
    /// </summary>
    /// <param name="openCount">Open sessions we are tracking.</param>
    /// <param name="reported">%NUM_PLAYERS% from the server. Never negative.</param>
    /// <param name="counter">Consecutive over-count events so far.</param>
    public static Decision Decide(int openCount, int reported, int counter)
    {
        // We are level with, or behind, the server. Behind is normal and harmless —
        // a player who joined while the bot was down has no session at all, and
        // inventing one would be worse than missing it.
        if (openCount <= reported) return new Decision(0, 0);

        // Exact: the server says nobody is connected, so every open row is a ghost.
        // No heuristic needed, and this is what makes a single leaked session
        // self-heal as soon as the server next empties out.
        if (reported == 0) return new Decision(openCount, 0);

        // Inside the ambiguity band — could just be how the count is reported for
        // this event kind. Leave the counter alone rather than accumulating toward a
        // trim on what may be normal behaviour.
        if (openCount <= reported + CountTolerance) return new Decision(0, counter);

        var next = counter + 1;
        if (next < EventsBeforeTrim) return new Decision(0, next);

        // Persistent and beyond tolerance: trim down to what the server reports.
        return new Decision(openCount - reported, 0);
    }
}
