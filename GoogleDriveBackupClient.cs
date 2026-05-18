using ClanGuardBot.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DriveData = Google.Apis.Drive.v3.Data;

namespace ClanGuardBot.Services;

/// <summary>
/// Thin wrapper around the Google Drive v3 API for the SQLite backup pipeline.
/// Mirrors the style of GoogleSheetsService — service account credentials are
/// loaded fresh for each call (the underlying HTTP client is short-lived,
/// matching how GoogleSheetsService handles spreadsheet writes — keeps the
/// behaviour identical for the same credential refresh edge cases).
///
/// ── Scopes ──
/// Uses DriveService.Scope.DriveFile, which scopes access to files the service
/// account itself created or had explicitly shared with it. Strictly narrower
/// than full Drive access — even if the credential leaks, an attacker can't
/// walk the whole Drive.
///
/// ── Folder setup (READ THIS BEFORE DEPLOYING) ──
/// Service accounts have no personal storage quota. There are two viable
/// folder layouts:
///
///   1. PREFERRED — Shared Drive (formerly Team Drive).
///      Create a Shared Drive, add the service account as a Content Manager,
///      put BackupDriveFolderId on a folder inside the Shared Drive. Storage
///      counts against the Shared Drive's quota (unlimited on Workspace,
///      generous on personal). Survives if the original owner's account is
///      ever closed.
///
///   2. ACCEPTABLE — A folder in someone's "My Drive".
///      Share the folder with the service account email (Editor permission).
///      Storage counts against the folder owner's My Drive quota. If the owner
///      ever loses access to their Google account, the backups go with them.
///
/// SupportsAllDrives=true is set on every request so both layouts work
/// without code changes.
///
/// ── Encryption note ──
/// Backup files are not encrypted at rest by this code beyond Google's
/// at-rest encryption. The SQLite snapshot contains Discord member IDs,
/// message timestamps, voice session times, security audit rows, AWOL
/// records, gamertag mappings, etc. Treat the backup folder's access list
/// the same way you'd treat live production data.
/// </summary>
public class GoogleDriveBackupClient
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
    /// Filters to .db.gz suffix so we don't accidentally delete unrelated
    /// files if someone reuses the folder.
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
                    if (f.Name is null || !f.Name.EndsWith(".db.gz", StringComparison.OrdinalIgnoreCase))
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

public sealed record BackupFile(string Id, string Name, DateTime CreatedUtc, long Size);
