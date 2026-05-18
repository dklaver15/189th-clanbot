using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO.Compression;

namespace ClanGuardBot.Services;

/// <summary>
/// Nightly off-droplet backup of the SQLite database to Google Drive.
///
/// ── Why this exists ──
/// clanguard.db on a single DigitalOcean droplet is one hardware failure (or
/// one fat-fingered `docker compose down -v`) away from losing every
/// VoiceSession, EventAttendance, RankHistory, InviteJoin, SecurityAuditRecord,
/// and AwolKickAuditRecord we have. The bot stamps a lot of operational
/// history that can't be reconstructed from Discord alone. This service is
/// the disaster recovery answer.
///
/// ── The snapshot mechanism: VACUUM INTO ──
/// SQLite's VACUUM INTO produces a clean, defragmented, single-file copy of
/// the database in one SQL statement. Key properties for our case:
///   • Atomic against live writers — the bot keeps running during the backup.
///   • WAL-aware — pages still in the WAL are merged into the output file,
///     so you get a fully consistent snapshot without needing to checkpoint.
///   • Single file out — no .db-wal / .db-shm sidecars to worry about.
///   • Smaller output — drops free pages, so the .db file shrinks vs. the
///     live one. Compression on top gets it small enough to gzip-upload in
///     well under a second on a typical 18MB DB.
///
/// We open our own SqliteConnection rather than borrowing the EF context's
/// connection. EF's connection pool would otherwise hold the backup file
/// reference longer than needed, and VACUUM INTO works fine from a fresh
/// connection.
///
/// ── Scheduling ──
/// Same pattern as RosterExportService and AutoPromotionService: BackgroundService,
/// compute next run at BackupHourUtc, sleep, run, repeat. Default 5 UTC (1am ET,
/// well between auto-promotion at 3 UTC and roster export at 6 UTC).
///
/// On startup, if we missed today's window by less than 12h we still try to
/// run a catch-up backup — losing a daily backup because the bot redeployed
/// 5 minutes after the scheduled hour would defeat the point of the service.
/// Beyond 12h we skip to the next cycle so a long outage doesn't immediately
/// burn a backup slot on a system that may still be unhealthy.
///
/// ── Retention ──
/// Each cycle lists every file in the Drive folder, sorts by created time,
/// and deletes anything older than BackupRetentionDays (default 14). Pruning
/// only runs after a successful upload — never on a failed cycle, so a
/// broken upload doesn't progressively cannibalise the historical record.
///
/// ── Failure handling ──
/// Per-cycle try/catch. On exception:
///   • Stamp BotState.LastSqliteBackupError with the message
///   • Log full exception
///   • Sleep until the next scheduled cycle (no aggressive retry — a failed
///     daily backup is annoying, not catastrophic, and aggressive retry on
///     auth failures would hammer Drive)
/// On success: clear LastSqliteBackupError, stamp LastSqliteBackupCompletedUtc
/// and LastSqliteBackupSizeBytes.
///
/// ── Dry-run mode ──
/// When BotConfig.BackupDryRun=true, the local snapshot + gzip is still
/// produced (useful for verifying the SQLite copy itself works), but the
/// Drive upload + retention pruning are skipped. The cycle still stamps
/// LastSqliteBackupCompletedUtc so /health reports recent activity, with a
/// "(dry-run)" suffix in the size value to make it obvious nothing was
/// uploaded. The local file is then cleaned up.
///
/// ── Local file lifecycle ──
/// Working files (.db snapshot + .db.gz) are written under
/// AppContext.BaseDirectory/backups/, created with permissive parent paths
/// matching how Program.cs creates the data/ directory. Files are deleted
/// after a successful upload (or in the dry-run case, after the gzip is
/// measured). On a crash mid-cycle, leftover files just get cleaned up on
/// the next cycle's prep step.
/// </summary>
public class SqliteBackupService : BackgroundService
{
    private const string BackupSubdir   = "backups";
    private const string SnapshotName   = "clanguard-snapshot.db";
    private const string CompressedExt  = ".db.gz";

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly GoogleDriveBackupClient _drive;
    private readonly ILogger<SqliteBackupService> _logger;
    private readonly BotConfig _config;

    public SqliteBackupService(
        IServiceProvider services,
        DiscordSocketClient client,
        GoogleDriveBackupClient drive,
        ILogger<SqliteBackupService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _drive = drive;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.BackupEnabled)
        {
            _logger.LogInformation("SqliteBackupService disabled (BotConfig.BackupEnabled=false); exiting");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.BackupDriveFolderId) && !_config.BackupDryRun)
        {
            _logger.LogWarning(
                "SqliteBackupService: BackupEnabled=true but BackupDriveFolderId is unset and BackupDryRun is false. " +
                "Service will exit. Set the folder ID or flip to dry-run.");
            return;
        }

        // Wait for Discord ready so we don't race the rest of startup writing logs / DB churn.
        while (_client.ConnectionState != ConnectionState.Connected
               && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        if (stoppingToken.IsCancellationRequested) return;
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        _logger.LogInformation(
            "SqliteBackupService started — will back up at {Hour:D2}:00 UTC daily (DryRun={DryRun}, Retention={Days}d)",
            _config.BackupHourUtc, _config.BackupDryRun, _config.BackupRetentionDays);

        // Startup catch-up: if we're past today's hour but no backup yet today.
        await TryStartupCatchUpAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextRun = now.Date.AddHours(_config.BackupHourUtc);
                if (nextRun <= now) nextRun = nextRun.AddDays(1);

                var delay = nextRun - now;
                _logger.LogInformation("Next SQLite backup at {NextRun} (in {Hours:F1}h)", nextRun, delay.TotalHours);
                await Task.Delay(delay, stoppingToken);

                await RunBackupCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SqliteBackupService loop hit an unexpected error — sleeping 30 min and retrying");
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }
    }

    private async Task TryStartupCatchUpAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            var todayRun = now.Date.AddHours(_config.BackupHourUtc);

            if (now < todayRun) return;                       // hasn't happened yet today
            if ((now - todayRun) > TimeSpan.FromHours(12))    // too far past, skip to tomorrow
            {
                _logger.LogInformation(
                    "SqliteBackupService startup: today's window ({Run:HH:mm}) is more than 12h in the past — skipping catch-up",
                    todayRun);
                return;
            }

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await db.BotStates.AsNoTracking().FirstOrDefaultAsync(ct);
            if (state?.LastSqliteBackupCompletedUtc is DateTime last && last >= todayRun)
            {
                _logger.LogInformation(
                    "SqliteBackupService startup: today's backup already completed at {Last} — no catch-up needed",
                    last);
                return;
            }

            _logger.LogInformation(
                "SqliteBackupService startup: catching up — today's window passed at {Run} with no completed backup",
                todayRun);
            await RunBackupCycleAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup catch-up backup failed; will try again at next scheduled run");
        }
    }

    private async Task RunBackupCycleAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var workDir = Path.Combine(AppContext.BaseDirectory, BackupSubdir);
        Directory.CreateDirectory(workDir);

        // Clean any stragglers from a previous crashed cycle so we don't keep
        // disk filling with abandoned snapshots.
        TryCleanupWorkDir(workDir);

        var snapshotPath  = Path.Combine(workDir, SnapshotName);
        var dbPath        = Path.Combine(AppContext.BaseDirectory, "data", "clanguard.db");
        var timestamp     = started.ToString("yyyy-MM-ddTHH-mm-ssZ");
        var remoteName    = $"clanguard-backup-{timestamp}{CompressedExt}";
        var compressedPath = Path.Combine(workDir, remoteName);

        try
        {
            // ── 1. VACUUM INTO snapshot ──
            await ProduceSnapshotAsync(dbPath, snapshotPath, ct);
            var snapshotBytes = new FileInfo(snapshotPath).Length;
            _logger.LogInformation("VACUUM INTO snapshot: {Bytes} bytes at {Path}", snapshotBytes, snapshotPath);

            // ── 2. gzip compress ──
            await CompressAsync(snapshotPath, compressedPath, ct);
            var compressedBytes = new FileInfo(compressedPath).Length;
            _logger.LogInformation(
                "Compressed snapshot: {Bytes} bytes ({Ratio:0.#}% of source)",
                compressedBytes,
                snapshotBytes == 0 ? 0 : (100.0 * compressedBytes / snapshotBytes));

            // ── 3. Upload (unless dry-run) ──
            string? driveFileId = null;
            if (_config.BackupDryRun)
            {
                _logger.LogInformation("BackupDryRun=true; skipping Drive upload");
            }
            else
            {
                driveFileId = await _drive.UploadAsync(compressedPath, remoteName, ct);
            }

            // ── 4. Retention prune (success-only, and only when not dry-run) ──
            if (!_config.BackupDryRun)
            {
                await PruneOldBackupsAsync(ct);
            }

            // ── 5. Stamp BotState ──
            await StampSuccessAsync(compressedBytes, ct);

            _logger.LogInformation(
                "SQLite backup complete in {Elapsed:F1}s (driveFileId={Id}, dryRun={DryRun})",
                (DateTime.UtcNow - started).TotalSeconds,
                driveFileId ?? "—",
                _config.BackupDryRun);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite backup cycle failed");
            await StampFailureAsync(ex.Message, ct);
            throw;  // surface to outer loop logger for stack trace; outer loop swallows and continues
        }
        finally
        {
            TryDelete(snapshotPath);
            TryDelete(compressedPath);
        }
    }

    private static async Task ProduceSnapshotAsync(string dbPath, string snapshotPath, CancellationToken ct)
    {
        // Microsoft.Data.Sqlite parameters can't be used in DDL — VACUUM INTO
        // is one of the few statements that requires a literal path. Quote with
        // doubled single quotes per SQLite's string-literal escape rules.
        TryDelete(snapshotPath);

        var connectionString = $"Data Source={dbPath};Mode=ReadWrite;Cache=Shared";
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var escaped = snapshotPath.Replace("'", "''");
        cmd.CommandText = $"VACUUM INTO '{escaped}'";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CompressAsync(string source, string dest, CancellationToken ct)
    {
        TryDelete(dest);
        await using var input  = File.OpenRead(source);
        await using var output = File.Create(dest);
        await using var gz     = new GZipStream(output, CompressionLevel.Optimal);
        await input.CopyToAsync(gz, ct);
    }

    private async Task PruneOldBackupsAsync(CancellationToken ct)
    {
        if (_config.BackupRetentionDays <= 0)
        {
            _logger.LogInformation("BackupRetentionDays <= 0; skipping retention prune");
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-_config.BackupRetentionDays);
        IReadOnlyList<BackupFile> files;
        try
        {
            files = await _drive.ListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list existing backups for retention prune — leaving prune to next cycle");
            return;
        }

        var stale = files.Where(f => f.CreatedUtc < cutoff).ToList();
        if (stale.Count == 0)
        {
            _logger.LogInformation(
                "Retention prune: {Total} backups in folder, none older than {Cutoff}",
                files.Count, cutoff);
            return;
        }

        _logger.LogInformation(
            "Retention prune: deleting {Count} backup(s) older than {Cutoff}",
            stale.Count, cutoff);

        foreach (var file in stale)
        {
            try
            {
                await _drive.DeleteAsync(file.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete stale backup {Name} ({Id}) — will retry next cycle", file.Name, file.Id);
            }
        }
    }

    // ── BotState helpers ────────────────────────────────────────────

    private async Task StampSuccessAsync(long compressedBytes, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var state = await GetOrCreateBotStateAsync(db, ct);
        state.LastSqliteBackupCompletedUtc = DateTime.UtcNow;
        state.LastSqliteBackupSizeBytes = compressedBytes;
        state.LastSqliteBackupError = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task StampFailureAsync(string error, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await GetOrCreateBotStateAsync(db, ct);
            // Don't overwrite LastSqliteBackupCompletedUtc — preserves the
            // "last good" timestamp so /health can show "✅ 3 days ago" alongside
            // the ⚠️ error, making the staleness obvious.
            state.LastSqliteBackupError = Truncate(error, 800);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stamp BotState.LastSqliteBackupError");
        }
    }

    /// <summary>
    /// SyncWithHandlers: AutoPromotionService.GetOrCreateBotStateAsync.
    /// Same one-row singleton pattern.
    /// </summary>
    private static async Task<BotState> GetOrCreateBotStateAsync(BotDbContext db, CancellationToken ct)
    {
        var state = await db.BotStates.FirstOrDefaultAsync(ct);
        if (state is null)
        {
            state = new BotState();
            db.BotStates.Add(state);
            await db.SaveChangesAsync(ct);
        }
        return state;
    }

    // ── File-system helpers ─────────────────────────────────────────

    private void TryCleanupWorkDir(string dir)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                try { File.Delete(path); }
                catch (Exception ex) { _logger.LogDebug(ex, "Could not clean stale backup file {Path}", path); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate {Dir} for prep cleanup", dir);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
