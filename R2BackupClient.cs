using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Cloudflare R2 implementation of <see cref="IBackupStorageClient"/>. R2
/// speaks the S3 API, so the standard AWS SDK works against it with three
/// tweaks: a custom <c>ServiceURL</c>, path-style addressing, and an
/// "auto" authentication region.
///
/// ── Why R2 ──
/// Replaces the previous Google Drive backup target. Google service accounts
/// have zero personal storage quota and Workspace Shared Drives require a
/// paid plan — every upload to a personal-Drive folder fails with
/// "Service Accounts do not have storage quota." R2 has no equivalent
/// gotcha, has a generous free tier, and isn't subject to OAuth-consent
/// quirks. SqliteBackupCrypto already encrypts client-side, so R2 is dumb
/// blob storage.
///
/// ── Credentials setup (one-time) ──
///   1. Cloudflare dashboard → R2 → create bucket (e.g. clanguard-backups).
///   2. Manage R2 API Tokens → Create API Token.
///      Permissions: Object Read &amp; Write (NOT Admin — the bot doesn't need
///      to create or delete buckets).
///      Specify bucket: the one created above (least privilege).
///   3. Capture the Access Key ID, Secret Access Key, and S3 endpoint URL.
///      The secret is shown exactly once.
///   4. Drop the keys into the droplet's .env file as
///      CLANGUARD_R2_ACCESS_KEY_ID and CLANGUARD_R2_SECRET_ACCESS_KEY.
///      docker-compose maps them into BotConfig__BackupR2__AccessKeyId and
///      BotConfig__BackupR2__SecretAccessKey.
///   5. Set BackupR2.Bucket and BackupR2.AccountEndpoint in appsettings.json
///      (these are not secret — the account ID in the endpoint is fine to
///      commit).
///
/// ── Encryption note ──
/// Same as before: encryption happens in SqliteBackupCrypto BEFORE the upload
/// reaches this client. R2's at-rest encryption is a defence-in-depth layer
/// on top of that, not the primary protection.
/// </summary>
public class R2BackupClient : IBackupStorageClient
{
    private const string MimeGzip = "application/gzip";

    private readonly BotConfig _config;
    private readonly ILogger<R2BackupClient> _logger;

    public R2BackupClient(
        IOptions<BotConfig> config,
        ILogger<R2BackupClient> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public string ProviderName => "CloudflareR2";

    public bool IsConfigured =>
        _config.BackupR2 is { } r2
        && !string.IsNullOrWhiteSpace(r2.Bucket)
        && !string.IsNullOrWhiteSpace(r2.AccountEndpoint)
        && !string.IsNullOrWhiteSpace(r2.AccessKeyId)
        && !string.IsNullOrWhiteSpace(r2.SecretAccessKey);

    public async Task<string> UploadAsync(
        string localPath,
        string remoteName,
        CancellationToken ct = default)
    {
        EnsureConfigured();
        using var client = CreateClient();

        await using var fs = File.OpenRead(localPath);
        var request = new PutObjectRequest
        {
            BucketName  = _config.BackupR2.Bucket,
            Key         = remoteName,
            InputStream = fs,
            ContentType = MimeGzip,
            // R2 historically rejected the SDK's chunked-SigV4 payload
            // signing. Disabling payload signing on the request still keeps
            // the request itself signed (transport is HTTPS); only the
            // body-chunk signatures are skipped. Safe for our use case —
            // the file is already AES-256-GCM encrypted client-side.
            DisablePayloadSigning = true,
        };

        var response = await client.PutObjectAsync(request, ct);
        if ((int)response.HttpStatusCode is < 200 or >= 300)
        {
            throw new InvalidOperationException(
                $"R2 upload did not complete: HTTP {(int)response.HttpStatusCode} {response.HttpStatusCode}");
        }

        _logger.LogInformation(
            "Backup uploaded to R2: bucket={Bucket} key={Key} etag={ETag}",
            _config.BackupR2.Bucket, remoteName, response.ETag);

        // For R2 / S3 there's no separate file ID — the object key is the
        // address. Return the key so DeleteAsync can be called with it
        // later for retention pruning.
        return remoteName;
    }

    public async Task<IReadOnlyList<BackupFile>> ListAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return Array.Empty<BackupFile>();

        using var client = CreateClient();

        var files = new List<BackupFile>();
        string? continuationToken = null;

        do
        {
            var req = new ListObjectsV2Request
            {
                BucketName        = _config.BackupR2.Bucket,
                ContinuationToken = continuationToken,
                MaxKeys           = 1000,
            };

            var resp = await client.ListObjectsV2Async(req, ct);
            if (resp.S3Objects is not null)
            {
                foreach (var obj in resp.S3Objects)
                {
                    if (obj.Key is null) continue;
                    // Match GoogleDriveBackupClient: only count files that
                    // look like our backups so a bucket reuse can't burn
                    // unrelated files at prune time.
                    if (!obj.Key.EndsWith(".db.gz", StringComparison.OrdinalIgnoreCase)
                        && !obj.Key.EndsWith(".db.gz.enc", StringComparison.OrdinalIgnoreCase))
                        continue;
                    files.Add(new BackupFile(
                        Id:         obj.Key,
                        Name:       obj.Key,
                        CreatedUtc: (obj.LastModified ?? DateTime.UtcNow).ToUniversalTime(),
                        Size:       obj.Size ?? 0L));
                }
            }

            continuationToken = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        // Mirror GoogleDriveBackupClient's oldest-first ordering for
        // consistency, even though PruneOldBackupsAsync doesn't require it.
        return files.OrderBy(f => f.CreatedUtc).ToList();
    }

    public async Task DeleteAsync(string fileId, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var client = CreateClient();

        var req = new DeleteObjectRequest
        {
            BucketName = _config.BackupR2.Bucket,
            Key        = fileId,
        };
        await client.DeleteObjectAsync(req, ct);
        _logger.LogInformation(
            "Deleted R2 backup object: bucket={Bucket} key={Key}",
            _config.BackupR2.Bucket, fileId);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "R2BackupClient is missing one or more of: BackupR2.Bucket, " +
                "BackupR2.AccountEndpoint, BackupR2.AccessKeyId, BackupR2.SecretAccessKey. " +
                "Check appsettings.json and the droplet's .env file.");
    }

    private AmazonS3Client CreateClient()
    {
        var creds = new BasicAWSCredentials(
            _config.BackupR2.AccessKeyId,
            _config.BackupR2.SecretAccessKey);

        var s3Config = new AmazonS3Config
        {
            ServiceURL           = _config.BackupR2.AccountEndpoint,
            ForcePathStyle       = true,
            // R2 doesn't use AWS regions, but the SDK signs requests with a
            // region in the SigV4 string-to-sign. "auto" is Cloudflare's
            // documented sentinel.
            AuthenticationRegion = "auto",
        };

        return new AmazonS3Client(creds, s3Config);
    }
}
