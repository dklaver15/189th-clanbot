namespace ClanGuardBot.Models;

/// <summary>
/// Tracks a single user's activity within a guild.
/// One record per user per guild.
/// </summary>
public class UserActivity
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>Messages sent in the current 28-day window.</summary>
    public int MessageCount { get; set; }

    /// <summary>Total voice channel seconds in the current 28-day window.</summary>
    public long VoiceSeconds { get; set; }

    /// <summary>When this user's current tracking window started.</summary>
    public DateTime WindowStart { get; set; }

    /// <summary>When the user last joined a voice channel (null if not currently in voice).</summary>
    public DateTime? VoiceJoinedAt { get; set; }
}

/// <summary>
/// Tracks when a user was assigned the AWOL role so we know
/// when the 2-day grace period expires.
/// </summary>
public class AwolRecord
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>When the AWOL role was assigned.</summary>
    public DateTime AssignedAt { get; set; }

    /// <summary>Whether the HQ notification has already been posted.</summary>
    public bool NotificationSent { get; set; }

    /// <summary>When the HQ notification was posted (null if not yet).</summary>
    public DateTime? NotificationSentAt { get; set; }
}
