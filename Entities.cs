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

    /// <summary>When the user last joined a voice channel (null if not currently in voice).</summary>
    public DateTime? VoiceJoinedAt { get; set; }
}

/// <summary>
/// A single recorded message event.
/// </summary>
public class MessageEvent
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// A single recorded voice session (join → leave).
/// </summary>
public class VoiceSession
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }

    /// <summary>The voice channel ID (for filtering by specific channels like Events).</summary>
    public ulong? ChannelId { get; set; }

    /// <summary>The voice channel name at time of join.</summary>
    public string? ChannelName { get; set; }
}

/// <summary>
/// Tracks when a user was assigned their current rank role.
/// Only the latest record per user/guild matters for "time in rank".
/// </summary>
public class RankHistory
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>The rank role name (e.g. "PVT", "SGT").</summary>
    public string RankName { get; set; } = string.Empty;

    /// <summary>When this rank was first detected on the user.</summary>
    public DateTime AssignedAt { get; set; }
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

/// <summary>
/// Persists a pending ticket reminder so it survives bot restarts.
/// Removed once the reminder fires or is cancelled.
/// </summary>
public class TicketReminder
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }

    /// <summary>When the ticket channel was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the reminder should fire.</summary>
    public DateTime ReminderAt { get; set; }
}