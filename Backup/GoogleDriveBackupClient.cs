using ClanGuardBot.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DriveData = Google.Apis.Drive.v3.Data;

namespace ClanGuardBot.Services;

/// <summary>
/// Legacy Google Drive implementation of <see cref="IBackupStorageClient"/>.
/// Kept compiled so a rollback to Drive is a config change
/// (BackupStorageProvider=GoogleDrive) rather than a code revert, but
/// no longer the default — see the "Why this is legacy" section below.
///
/// ── Why this is legacy ──
/// Service accounts have zero personal storage quota. Drive used to let a
/// service account upload to a folder it had Editor on and charge storage
/// to the folder's owner; that behaviour was reversed and the API now
/// returns HTTP 403 "Service Accounts do not have storage quota. Leverage
/// shared drives." for every upload to a folder inside My Drive.
///
/// The only viable Drive layout left is a Workspace Shared Drive (formerly
/// Team Drive), which requires a paid Google Workspace edition (Business
/// Standard or higher). If you have Workspace:
///   1. Create a Shared Drive.
///   2. Add the service account email as Content Manager.
///   3. Put BackupDriveFolderId on a folder inside the Shared Drive.
///   4. Set BackupStorageProvider=GoogleDrive.
///
/// If you don't have Workspace, use <see cref="R2BackupClient"/> instead
/// (set BackupStorageProvider=R2).
///
/// ── Scopes ──
/// Uses DriveService.Scope.DriveFile, which scopes access to files the
/// service account itself created or had explicitly shared with it. Strictly
/// narrower than full Drive access — even if the credential leaks, an
/// attacker can't walk the whole Drive.
///
/// ── Encryption note ──
/// Backup files are not encrypted at rest by this code beyond Google's
/// at-rest encryption. SqliteBackupCrypto handles encryption client-side
/// when BackupEncryptionPassphrase is set; treat the backup folder's access
/// list the same way you'd treat live production data either way.
/// </summary>
public class GoogleDriveBackupClient : IBackupStorageClient
{
    private const string ApplicationName = "ClanGuardBot";
    private const string MimeGzip = "application/gzip";

    private readonly BotConfig _config;
    private readonly ILogger<GoogleDriveBackupClient> _logger;

    public GoogleDriveBackupClient(
        IOptions<BotConfig> config,
        ILogger<GoogleDriveBackupClient> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public string ProviderName => "GoogleDrive";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.BackupDriveFolderId)
        && !string.IsNullOrWhiteSpace(_config.GoogleCredentialsPath)
        && File.Exists(_config.GoogleCredentialsPath);

    /// <summary>
    /// Uploads a local file to the configured backup folder. Returns the
    /// Drive file ID on success.
    /// </summary>
    public async Task<string> UploadAsync(
        string localPath,
        string remoteName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.BackupDriveFolderId))
            throw new InvalidOperationException(
                "BotConfig.BackupDriveFolderId is not set — cannot upload backup");

        using var service = CreateService();

        var metadata = new DriveData.File
        {
            Name = remoteName,
            Parents = new[] { _config.BackupDriveFolderId }
        };

        await using var fs = File.OpenRead(localPath);

        var request = service.Files.Create(metadata, fs, MimeGzip);
        request.Fields = "id, name, size, createdTime";
        request.SupportsAllDrives = true;

        var progress = await request.UploadAsync(ct);
        if (progress.Status != Google.Apis.Upload.UploadStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Drive upload did not complete: {progress.Status} — {progress.Exception?.Message ?? "unknown"}",
                progress.Exception);
        }

        var uploaded = request.ResponseBody;
        _logger.LogInformation(
            "Backup uploaded to Drive: id={Id} name={Name} size={Size}",
            uploaded.Id, uploaded.Name, uploaded.Size);
        return uploaded.Id;
    }

    /// <summary>
    /// Lists every backup file in the configured folder, oldest first.
    /// Filters to .db.gz / .db.gz.enc suffixes so we don't accidentally
    /// delete unrelated files if someone reuses the folder.
    /// </summary>
    public async Task<IReadOnlyList<BackupFile>> ListAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.BackupDriveFolderId))
            return Array.Empty<BackupFile>();

        using var service = CreateService();

        var files = new List<BackupFile>();
        string? pageToken = null;

        do
        {
            var req = service.Files.List();
            req.Q = $"'{_config.BackupDriveFolderId}' in parents and trashed = false";
            req.Fields = "nextPageToken, files(id, name, createdTime, size)";
            req.SupportsAllDrives = true;
            req.IncludeItemsFromAllDrives = true;
            req.PageSize = 100;
            req.PageToken = pageToken;
            req.OrderBy = "createdTime";

            var result = await req.ExecuteAsync(ct);
            if (result.Files is not null)
            {
                foreach (var f in result.Files)
                {
                    if (f.Name is null) continue;
                    if (!f.Name.EndsWith(".db.gz", StringComparison.OrdinalIgnoreCase)
                        && !f.Name.EndsWith(".db.gz.enc", StringComparison.OrdinalIgnoreCase))
                        continue;
                    files.Add(new BackupFile(
                        f.Id,
                        f.Name,
                        f.CreatedTimeDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow,
                        f.Size ?? 0));
                }
            }
            pageToken = result.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return files;
    }

    /// <summary>Deletes a single Drive file by ID. Used for retention pruning.</summary>
    public async Task DeleteAsync(string fileId, CancellationToken ct = default)
    {
        using var service = CreateService();
        var req = service.Files.Delete(fileId);
        req.SupportsAllDrives = true;
        await req.ExecuteAsync(ct);
        _logger.LogInformation("Deleted Drive backup file id={Id}", fileId);
    }

    private DriveService CreateService()
    {
        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(DriveService.Scope.DriveFile);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });
    }
}