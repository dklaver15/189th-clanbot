namespace ClanGuardBot.Services;

/// <summary>
/// Storage-agnostic contract for off-droplet SQLite backup uploads. Implemented
/// by <see cref="GoogleDriveBackupClient"/> (legacy, requires Workspace Shared
/// Drive — service accounts have no quota on personal Drive) and
/// <see cref="R2BackupClient"/> (current default).
///
/// SqliteBackupService depends on this interface only; the concrete client is
/// chosen at DI registration time based on BotConfig.BackupStorageProvider.
/// Swapping providers is a config change, not a code change.
/// </summary>
public interface IBackupStorageClient
{
    /// <summary>
    /// Short human-readable name of the underlying provider, used in log
    /// lines and the SqliteBackupService startup warning. E.g. "GoogleDrive",
    /// "CloudflareR2".
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// True when every required config key for this provider is set. False
    /// when the provider would throw on first call. Checked at service
    /// startup so we fail loud at boot instead of silently producing
    /// local-only backups.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Uploads a local file to the configured remote location. Returns an
    /// opaque identifier that can be passed back to <see cref="DeleteAsync"/>
    /// later. For Drive this is the Drive file ID; for R2 it's the object
    /// key. Treat as a black box.
    /// </summary>
    Task<string> UploadAsync(string localPath, string remoteName, CancellationToken ct = default);

    /// <summary>
    /// Lists every backup file at the configured remote location, oldest
    /// first. Implementations filter to the .db.gz / .db.gz.enc suffixes so
    /// that reusing a bucket/folder for other files can't accidentally
    /// expose them to retention pruning.
    /// </summary>
    Task<IReadOnlyList<BackupFile>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes a single backup by the ID returned from a prior
    /// <see cref="UploadAsync"/> or <see cref="ListAsync"/> call. Used for
    /// retention pruning.
    /// </summary>
    Task DeleteAsync(string fileId, CancellationToken ct = default);
}

/// <summary>
/// Provider-agnostic backup file metadata. <see cref="Id"/> is whatever the
/// provider uses to address the file (Drive file ID, S3 object key, …) and
/// should be treated as opaque by callers.
/// </summary>
public sealed record BackupFile(string Id, string Name, DateTime CreatedUtc, long Size);
