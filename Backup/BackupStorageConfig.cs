namespace ClanGuardBot.Models;

/// <summary>
/// Selects which off-droplet storage backend the SqliteBackupService uses.
/// Read once at DI registration time in Program.cs — changing this value
/// requires a restart.
/// </summary>
public enum BackupStorageProvider
{
    /// <summary>
    /// Cloudflare R2 (S3-compatible). Current default. Requires
    /// <see cref="BotConfig.BackupR2"/> to be fully populated.
    /// </summary>
    R2 = 0,

    /// <summary>
    /// Google Drive via a service account. Legacy. Only works against a
    /// Workspace Shared Drive — service accounts have zero quota on
    /// personal Drive, so uploads to a My Drive folder fail with HTTP 403
    /// "Service Accounts do not have storage quota." Kept compiled so a
    /// rollback is a config change, not a code change.
    /// </summary>
    GoogleDrive = 1,
}

/// <summary>
/// Cloudflare R2 (S3-compatible) settings. Bound from the
/// <c>BotConfig:BackupR2</c> section of appsettings.json plus
/// <c>BotConfig__BackupR2__*</c> environment variables.
///
/// ── Which values go where ──
///   appsettings.json (committed to git):
///     • Bucket              — bucket name, not secret
///     • AccountEndpoint     — your account ID is in the URL, not secret
///
///   .env file on the droplet (NOT committed):
///     • AccessKeyId         — CLANGUARD_R2_ACCESS_KEY_ID
///     • SecretAccessKey     — CLANGUARD_R2_SECRET_ACCESS_KEY
///
/// docker-compose maps the .env values via:
///     BotConfig__BackupR2__AccessKeyId=${CLANGUARD_R2_ACCESS_KEY_ID}
///     BotConfig__BackupR2__SecretAccessKey=${CLANGUARD_R2_SECRET_ACCESS_KEY}
/// </summary>
public class BackupR2Settings
{
    /// <summary>
    /// R2 bucket name, e.g. "clanguard-backups". Created in the Cloudflare
    /// dashboard under R2 → Create bucket. Globally unique within your
    /// account; lowercase + hyphens only.
    /// </summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// S3-API endpoint URL for your R2 account, in the form
    /// <c>https://&lt;account-id&gt;.r2.cloudflarestorage.com</c>. Shown in
    /// the R2 dashboard next to the bucket. NOT the jurisdiction-specific
    /// endpoint (EU / FedRAMP) — use the generic one unless you have a
    /// specific reason. Not a secret.
    /// </summary>
    public string AccountEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Access Key ID from the R2 API token. Treat as a secret — anyone with
    /// the pair can read, write, and delete every object in the scoped
    /// bucket(s). Flow via environment, not appsettings.json.
    /// </summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Secret Access Key from the R2 API token. Shown exactly once at token
    /// creation — store in Proton Pass / 1Password immediately. Flow via
    /// environment, not appsettings.json.
    /// </summary>
    public string SecretAccessKey { get; set; } = string.Empty;
}
