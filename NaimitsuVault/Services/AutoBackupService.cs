// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace NaimitsuVault.Services;

/// <summary>
/// Atomically bulk-copies the unified DB (NaimitsuVault.nkdb) plus all anonymized vault files under data/
/// to the specified folder. Both manual backup (VaultOperationsViewModel) and automatic backup (RunBackup,
/// called synchronously at app exit) share <see cref="PerformUnifiedBackup"/> as the common execution code
/// path, keeping the data flow fully symmetric between the two.
///
/// The automatic path additionally skips entirely when nothing worth protecting has changed since the
/// last backup, tracked via <see cref="MarkContentChanged"/>: callers touch the pending-backup flag file
/// exactly at the confirmed-content write points (Secret/StoredFile save+delete+undelete, Profile save -
/// never drafts, never automatic/view-triggered writes like audit logging, favicon prefetch, or the
/// shadow-file self-heal). This flag-based design replaced an earlier same-day attempt at comparing DB
/// content directly: that approach kept discovering new "changes on every session regardless of user
/// action" write paths to exclude (VaultRegistries.LastAccessedAt/ShadowFileKey, the AuditLogs table,
/// FaviconCache entries fetched merely by viewing a secret) and had no way to guarantee the exclusion list
/// was complete. Explicitly marking the flag only at the handful of confirmed-save call sites is bounded
/// and auditable instead.
/// IsEnabled / Folder are synced by AppSettingsViewModel.LoadAsync() / PersistGeneralSettingsAsync().
/// </summary>
public class AutoBackupService(ILogger<AutoBackupService> logger)
{
    // (-\d+)? allows the numbered collision suffix that PerformUnifiedBackup appends when two backups
    // land in the same second (e.g. "-2"). Without it, RotateGenerations misjudged such a folder as
    // corrupt/invalid and deleted it immediately after creation - see PerformUnifiedBackup's comment
    // at the collision-suffix site for the full incident. Matches AuthService.Restore's
    // ArchivedFolderPattern, which uses the identical suffix scheme for the same reason.
    private static readonly Regex GenerationFolderPattern = new(@"^Naimitsu_\d{8}_\d{6}(-\d+)?$", RegexOptions.Compiled);
    // Exactly the shape NewTempFolderName() produces ("tmp_" + Guid "N"). A folder that merely starts
    // with "tmp_" may belong to the user and must never be touched.
    private static readonly Regex TempFolderPattern = new(@"^tmp_[0-9a-f]{32}$", RegexOptions.Compiled);
    private const int MaxGenerations = 3;

    public bool IsEnabled { get; set; }
    public string? Folder { get; set; }

    /// <summary>
    /// Set to true during Route A (restricted read-only viewing mode).
    /// RunBackup() checks this flag and skips entirely while writes are prohibited.
    /// </summary>
    public bool IsReadOnlyRestricted { get; set; }

    private static string GetDefaultDataDir() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
    private static string GetDefaultDbPath()   => Path.Combine(GetDefaultDataDir(), "NaimitsuVault.nkdb");

    /// <summary>Virtual for testability.</summary>
    internal virtual string GetDataDir() => GetDefaultDataDir();

    /// <summary>Virtual for testability (delegates to private static GetDefaultDbPath()).</summary>
    internal virtual string GetSourceDbPath() => GetDefaultDbPath();

    /// <summary>Virtual for testability.</summary>
    internal virtual string NewTempFolderName() => "tmp_" + Guid.NewGuid().ToString("N");

    // Flag file that carries a shutdown-time automatic backup failure (or a run skipped because the
    // folder was missing) over to the next launch. Its content is the reason: FailureReasonFailed or
    // FailureReasonFolderMissing. Written only by RunBackup - a manual backup failure is reported on the
    // spot, so it must not leave a "the last exit's automatic backup failed" notice behind.
    public static string FailureFlagPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "backup_failed.flag");

    /// <summary>Virtual for testability (delegates to static FailureFlagPath).</summary>
    internal virtual string GetFailureFlagPath() => FailureFlagPath;

    internal const string FailureReasonFailed = "Failed";
    internal const string FailureReasonFolderMissing = "FolderMissing";

    /// <summary>
    /// Reads and deletes the failure flag left by the previous exit. Returns false when there is none.
    /// <paramref name="folderMissing"/> is true only for an explicit FolderMissing reason; any other
    /// content (including the exception type name written by older versions) is treated as a failure.
    /// </summary>
    public static bool TryConsumeFailureFlag(out bool folderMissing)
        => TryConsumeFailureFlag(FailureFlagPath, out folderMissing);

    internal static bool TryConsumeFailureFlag(string path, out bool folderMissing)
    {
        folderMissing = false;
        if (!File.Exists(path)) return false;
        try { folderMissing = File.ReadAllText(path).Trim() == FailureReasonFolderMissing; }
        catch { /* Unreadable: report it as a failure */ }
        try { File.Delete(path); } catch { /* Ignore deletion failure */ }
        return true;
    }

    // Flag file marking "confirmed vault content changed since the last successful backup". Deliberately
    // a plain file rather than a DB row: it must survive a crash between the content write and the next
    // exit, and must be settable without a live DEK (MarkContentChanged is called from save/delete
    // ViewModels that already hold one, but the flag mechanism itself has no crypto dependency).
    public static string PendingBackupFlagPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "pending_backup.flag");

    /// <summary>Virtual for testability (delegates to static PendingBackupFlagPath).</summary>
    internal virtual string GetPendingBackupFlagPath() => PendingBackupFlagPath;

    /// <summary>
    /// Marks that confirmed vault content changed since the last backup. Call this exactly at
    /// confirmed-save/delete/undelete points for Secrets, StoredFiles, and the Profile - never for
    /// drafts (SecretDrafts, the Profile TwinB slot) and never for automatic/view-triggered writes
    /// (audit log entries, favicon prefetch cache, shadow-file self-heal, settings changes). Idempotent
    /// and safe to call from any thread; failures are logged and swallowed (never blocks the caller's
    /// save flow over a backup-scheduling detail).
    /// </summary>
    public void MarkContentChanged()
    {
        try
        {
            var path = GetPendingBackupFlagPath();
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            if (!File.Exists(path)) File.Create(path).Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to mark pending backup flag. ErrorType={ErrorType}", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Outcome of an automatic backup attempt (<see cref="RunBackup"/>), for the caller to decide
    /// whether an audit log entry is warranted. Not used by the manual path (<see cref="PerformUnifiedBackup"/>),
    /// which always either succeeds or fails.
    /// </summary>
    public enum AutoBackupResult
    {
        /// <summary>
        /// Disabled, no folder configured, restricted read-only mode is active, or the folder doesn't
        /// exist while nothing changed (nothing would have been backed up anyway).
        /// </summary>
        NotAttempted,
        /// <summary>Confirmed content changed, but the backup folder did not exist, so no backup was made.</summary>
        SkippedFolderMissing,
        /// <summary>A new generation was created.</summary>
        Created,
        /// <summary>No confirmed content changed since the last backup; nothing was written.</summary>
        SkippedNoChange,
        Failed,
    }

    /// <summary>Called at app exit (automatic backup). Synchronous processing right before App.Exit().</summary>
    public AutoBackupResult RunBackup()
    {
        if (IsReadOnlyRestricted)
        {
            logger.LogInformation("Skipped automatic backup because restricted read-only mode is active.");
            return AutoBackupResult.NotAttempted;
        }
        if (!IsEnabled || string.IsNullOrWhiteSpace(Folder)) return AutoBackupResult.NotAttempted;

        bool hasPendingChanges = File.Exists(GetPendingBackupFlagPath());
        if (!Directory.Exists(Folder))
        {
            if (!hasPendingChanges) return AutoBackupResult.NotAttempted;
            // The pending flag stays in place, so the next exit with the folder present backs everything up.
            logger.LogWarning("Skipped automatic backup because the backup folder was not found.");
            WriteFailureFlag(FailureReasonFolderMissing);
            return AutoBackupResult.SkippedFolderMissing;
        }

        // No confirmed change since the last backup is not enough to skip: the folder may hold no backup
        // at all (just switched to it, automatic backup just turned on, backups deleted by hand, a new USB
        // drive). Checked only when the flag is absent, so a normal exit with changes never lists the folder.
        if (!hasPendingChanges)
        {
            if (HasHealthyGeneration(Folder))
            {
                logger.LogInformation("Skipped automatic backup because no confirmed vault content changed since the last backup.");
                return AutoBackupResult.SkippedNoChange;
            }
            logger.LogInformation("Running automatic backup without content changes because the backup folder holds no healthy generation.");
        }

        if (PerformUnifiedBackup(Folder)) return AutoBackupResult.Created;
        WriteFailureFlag(FailureReasonFailed);
        return AutoBackupResult.Failed;
    }

    private void WriteFailureFlag(string reason)
    {
        try { File.WriteAllText(GetFailureFlagPath(), reason); }
        catch { /* Ignore flag write failure */ }
    }

    /// <summary>
    /// Atomically copies the unified DB plus all vault files to destinationFolder.
    /// Manual backup (VaultOperationsViewModel.BackupDatabaseAsync) calls this same method,
    /// keeping the data flow fully symmetric between manual and automatic backup.
    /// </summary>
    /// <returns>True on success. False on failure (exceptions never propagate out). The failure flag for the
    /// next launch is written by <see cref="RunBackup"/>, not here, so a manual backup failure leaves none.</returns>
    public bool PerformUnifiedBackup(string destinationFolder)
    {
        var tmpPath = Path.Combine(destinationFolder, NewTempFolderName());
        try
        {
            // Step 1: isolate to a temporary staging area
            Directory.CreateDirectory(tmpPath);

            SqliteConnection.ClearAllPools();

            // Step 2: bulk file copy (unified DB)
            var dbPath = GetSourceDbPath();
            if (File.Exists(dbPath))
                CopyDatabaseFile(dbPath, Path.Combine(tmpPath, Path.GetFileName(dbPath)));

            // Step 3: bulk file copy (all 48-character anonymized vault files)
            foreach (var vaultPath in AuthService.ScanVaultCandidates(GetDataDir()))
                CopyDatabaseFile(vaultPath, Path.Combine(tmpPath, Path.GetFileName(vaultPath)));

            // Step 4: atomic rename
            // BackupDatabase returns the dst connection to the pool, so release it before Directory.Move (avoids Windows file locking)
            SqliteConnection.ClearAllPools();
            // A numbered "-2", "-3", ... suffix (not a GUID) so the folder still matches
            // GenerationFolderPattern below and survives RotateGenerations instead of being
            // misjudged as corrupt/invalid and deleted right after creation. Matches the same
            // collision-handling scheme as AuthService.Restore's ArchiveExistingDataFolder.
            var folderName = $"Naimitsu_{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}";
            var finalPath = Path.Combine(destinationFolder, folderName);
            for (int suffix = 2; Directory.Exists(finalPath); suffix++)
                finalPath = Path.Combine(destinationFolder, $"{folderName}-{suffix}");
            Directory.Move(tmpPath, finalPath);

            logger.LogInformation("Bulk backup completed.");

            // Only drive generation rotation / pending-flag clear right after the atomic rename succeeds
            try { RotateGenerations(destinationFolder); }
            catch (Exception ex)
            {
                logger.LogWarning("Generation rotation failed. ErrorType={ErrorType}", ex.GetType().Name);
            }

            if (File.Exists(GetFailureFlagPath()))
                try { File.Delete(GetFailureFlagPath()); } catch { /* Ignore deletion failure */ }

            // This backup now covers everything MarkContentChanged was raised for - including a manual
            // backup, which makes the next automatic exit correctly skip if nothing further changed.
            try { if (File.Exists(GetPendingBackupFlagPath())) File.Delete(GetPendingBackupFlagPath()); }
            catch { /* Ignore deletion failure - worst case, the next backup runs once more than strictly necessary */ }

            return true;
        }
        catch (Exception ex)
        {
            // ex.Message may contain the OS username and full paths, so log only the type name
            logger.LogWarning("Bulk backup failed. ErrorType={ErrorType}", ex.GetType().Name);
            try { if (Directory.Exists(tmpPath)) Directory.Delete(tmpPath, recursive: true); }
            catch { /* Ignore tmp deletion failure (collected as leftover cruft on the next rotation) */ }
            return false;
        }
    }

    /// <summary>Copies the file using the SqliteConnection.BackupDatabase() API, which is safe even in WAL mode.</summary>
    private static void CopyDatabaseFile(string sourcePath, string destPath)
    {
        using var src = new SqliteConnection($"Data Source={sourcePath}");
        src.Open();
        using var dst = new SqliteConnection($"Data Source={destPath}");
        dst.Open();
        src.BackupDatabase(dst);
    }

    /// <summary>
    /// Generation rotation. Deletes only folders this app itself created in the backup folder: leftover
    /// staging folders (tmp_ + 32 hex digits), corrupt generation folders (Naimitsu_ name but a missing or
    /// empty nkdb), and healthy generations beyond the newest 3. Every other folder is left untouched -
    /// the backup folder may be a place like Desktop or Documents that holds the user's own folders.
    /// </summary>
    /// <remarks>
    /// Until 2026-10-19 (v1.0.0-v1.0.3) every subfolder whose name did not match GenerationFolderPattern
    /// was deleted with its contents, including the user's unrelated folders, both on manual and automatic
    /// backup. Rule 1 (no deletion without the user's explicit action) forbids that: when a folder cannot be
    /// positively identified as ours, it is kept.
    /// </remarks>
    internal void RotateGenerations(string destinationFolder)
    {
        // Release SQLite files in past-generation folders in case they're locked via the connection pool
        SqliteConnection.ClearAllPools();
        var dir = new DirectoryInfo(destinationFolder);
        var healthy = new List<DirectoryInfo>();

        foreach (var sub in dir.GetDirectories())
        {
            if (TempFolderPattern.IsMatch(sub.Name))
            {
                TryDeleteFolder(sub);
                continue;
            }

            // Not ours: never delete, never count.
            if (!GenerationFolderPattern.IsMatch(sub.Name)) continue;

            bool isHealthy;
            try { isHealthy = IsHealthyGeneration(sub); }
            catch (Exception ex)
            {
                // Could not tell: keep it (fail safe toward not deleting) and leave it out of the count.
                logger.LogWarning("Could not check a generation folder; leaving it in place. ErrorType={ErrorType}", ex.GetType().Name);
                continue;
            }

            if (!isHealthy)
            {
                TryDeleteFolder(sub);
                continue;
            }

            healthy.Add(sub);
        }

        // Delete every folder beyond the newest MaxGenerations, not just one: a single "delete only
        // the oldest" step used to be paired with a >= check, which made the steady-state count
        // MaxGenerations-1 instead of MaxGenerations (fixed 2026-08-20 - reported as "only 2
        // generations survive, not 3"). Deleting all excess in one pass also lets a folder count that
        // built up before this fix (e.g. from stale >= behavior or manual copies) self-heal in a
        // single RunBackup() call instead of trickling down by one per call.
        foreach (var excess in healthy.OrderByDescending(d => d.Name, StringComparer.Ordinal).Skip(MaxGenerations))
            TryDeleteFolder(excess);
    }

    /// <summary>
    /// True when the backup folder holds at least one healthy generation, by the same criteria
    /// RotateGenerations uses. If the folder cannot be read, returns false so that a backup is attempted:
    /// writing one more generation is the safe direction, and a failure is then reported as usual.
    /// </summary>
    internal bool HasHealthyGeneration(string destinationFolder)
    {
        try
        {
            return new DirectoryInfo(destinationFolder).GetDirectories()
                .Any(d => GenerationFolderPattern.IsMatch(d.Name) && IsHealthyGeneration(d));
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not list the backup folder's generations. ErrorType={ErrorType}", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>A generation folder is healthy when it directly holds a non-empty unified DB file.</summary>
    private static bool IsHealthyGeneration(DirectoryInfo generation)
    {
        var nkdb = new FileInfo(Path.Combine(generation.FullName, "NaimitsuVault.nkdb"));
        return nkdb.Exists && nkdb.Length > 0;
    }

    private void TryDeleteFolder(DirectoryInfo dir)
    {
        try { dir.Delete(recursive: true); }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to delete an invalid/corrupt folder. ErrorType={ErrorType}", ex.GetType().Name);
        }
    }
}
