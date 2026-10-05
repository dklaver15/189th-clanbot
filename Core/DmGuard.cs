using Discord;
using Discord.Net;

namespace ClanGuardBot.Services;

/// <summary>
/// The guard every DM open in this bot goes through.
///
/// ── The hazard ──
/// Opening a DM channel (POST /users/@me/channels) sits in its own heavily
/// rate-limited bucket, shared across every DM the bot opens for any reason, so
/// a caller's own pacing does not protect it. Under Discord.NET's default
/// RetryMode.RetryRateLimit a 429 is silently AWAITED rather than thrown: it is
/// not an error, so a try/catch never sees it, and the caller simply stops on
/// that one await for however long Discord said to wait.
///
/// Where that lands depends on the caller, and both outcomes have already
/// happened here. In a loop it parks the whole loop (/kick-awols, 2026-06-19).
/// On a gateway event handler it parks the gateway task, and then no
/// interaction gets acknowledged inside Discord's 3 second window, so unrelated
/// commands fail with "The application did not respond" (2026-08-05).
///
/// ── The guard ──
/// RetryMode.AlwaysFail turns a 429 into a thrown RateLimitedException instead
/// of a silent wait, and a CancellationToken caps any single attempt regardless
/// of cause (slow open, hung socket). Both are needed: the timeout alone would
/// still let a 429 burn the full window.
///
/// ── Scope ──
/// This covers opening the channel, which is the contended bucket. Sending to
/// an already-open DM rides a per-channel bucket that is far harder to exhaust,
/// so the wizards keep their own send code. <see cref="TrySendAsync"/> guards
/// both, for the best-effort case where nobody is waiting on the result.
/// </summary>
public static class DmGuard
{
    /// <summary>Cap on a single DM attempt. 5s is the established value here.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Why a DM channel could not be opened.</summary>
    public enum OpenFailure
    {
        /// <summary>Opened fine.</summary>
        None,

        /// <summary>The member does not accept DMs from the server, or the open was refused.</summary>
        Closed,

        /// <summary>Discord rate limited us, or the attempt ran past <see cref="Timeout"/>.</summary>
        Throttled,
    }

    /// <summary>What to tell someone whose DM we could not open because it is closed.</summary>
    public const string ClosedAdvice =
        "Enable **Direct Messages** from server members in your Privacy Settings, then run the command again.";

    /// <summary>What to tell someone whose DM we could not open because Discord throttled us.</summary>
    public const string ThrottledAdvice =
        "Discord is throttling me at the moment, so I couldn't open a DM. Give it a minute and run the command again.";

    /// <summary>
    /// The right thing to say for a given failure. Worth keeping distinct: telling
    /// someone to check their privacy settings when the real problem is a rate
    /// limit sends them to fix something that isn't broken.
    /// </summary>
    public static string AdviceFor(OpenFailure failure) =>
        failure == OpenFailure.Throttled ? ThrottledAdvice : ClosedAdvice;

    /// <summary>
    /// Opens a DM channel under the guard. Returns the channel, or null plus the
    /// reason. Never throws.
    /// </summary>
    public static async Task<(IDMChannel? Dm, OpenFailure Failure)> TryOpenAsync(IUser user)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var options = new RequestOptions
        {
            RetryMode   = RetryMode.AlwaysFail,
            CancelToken = cts.Token,
        };

        try
        {
            return (await user.CreateDMChannelAsync(options), OpenFailure.None);
        }
        catch (RateLimitedException)
        {
            return (null, OpenFailure.Throttled);
        }
        catch (OperationCanceledException)
        {
            return (null, OpenFailure.Throttled);
        }
        catch
        {
            return (null, OpenFailure.Closed);
        }
    }

    /// <summary>
    /// Best-effort DM: opens and sends under the guard, swallowing every failure.
    /// Returns true if it was delivered. For notification loops, where a closed
    /// DM is normal and nobody is waiting on the outcome.
    /// </summary>
    public static async Task<bool> TrySendAsync(IUser user, Embed? embed = null, string? text = null)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var options = new RequestOptions
        {
            RetryMode   = RetryMode.AlwaysFail,
            CancelToken = cts.Token,
        };

        try
        {
            var dm = await user.CreateDMChannelAsync(options);
            await dm.SendMessageAsync(text, embed: embed, options: options);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
