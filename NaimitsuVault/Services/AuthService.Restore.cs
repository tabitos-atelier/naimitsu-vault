// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
//
// Restore flow. Rev.22: individual restore (per-vault selective restore) was removed. The only
// remaining path is a full overwrite restore, gated by a backup-authenticity check.

using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;

namespace NaimitsuVault.Services;

public partial class AuthService
{
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // AuthenticateBackupNkdbAsync — Backup authentication gate (proof of backup ownership)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// Matches against the backup unified database's (.nkdb) KSharedSlot_N (0x0001-0x0003) to
    /// recover K_shared. Used purely as a proof-of-ownership gate before a restore is allowed to
    /// proceed; the caller does not need to keep the returned K_shared (the restore copy itself is
    /// password-independent - see RestoreAllFromBackupAsync).
    /// Returns a pinned K_shared buffer on success (the caller is responsible for calling ZeroMemory).
    /// Returns null on failure.
    /// </summary>
    public async Task<byte[]?> AuthenticateBackupNkdbAsync(string backupNkdbPath, char[] password)
    {
        var coarse = ComputeCoarseHash(password.AsSpan());
        var slots  = await ReadAllKSharedSlotsAsync(backupNkdbPath);

        foreach (var (_, slot) in slots)
        {
            if (slot.Length < SlotTotalSize) continue;

            // coarse_hash pre-filter (O(1) comparison)
            if (!slot.AsSpan(SlotCoarseHashOffset, SlotCoarseHashSize)
                      .SequenceEqual(coarse.AsSpan(0, SlotCoarseHashSize)))
                continue;

            // A span cannot be captured by the lambda below; the salt is public data, so copy it.
            var unifiedSalt  = slot.AsSpan(SlotUnifiedSaltOffset, SlotUnifiedSaltSize).ToArray();
            var kMaster      = GC.AllocateArray<byte>(32, pinned: true);
            var kShared      = GC.AllocateArray<byte>(32, pinned: true);
            try
            {
                // Off the UI thread (this runs behind the restore window's auth dialog, which used to
                // freeze for the whole derivation). It only proves ownership of the backup and never writes
                // to the session, so unlike the unlock path it needs no stale-result guard. `password` is
                // the caller's buffer, wiped only after this method's returned task completes.
                await Task.Run(() => DeriveArgon2id(password.AsSpan(), unifiedSalt, kMaster));
                crypto.Decrypt(slot.AsSpan(SlotWrappedKShOffset), kMaster, kShared);
                CryptographicOperations.ZeroMemory(kMaster);
                return kShared; // decryption succeeded -> return K_shared
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(kMaster);
                CryptographicOperations.ZeroMemory(kShared);
            }
        }

        return null;
    }

    private static async Task<List<(int DbNumber, byte[] Slot)>> ReadAllKSharedSlotsAsync(string nkdbPath)
    {
        var result = new List<(int, byte[])>();
        // immutable=1: this reads the backup's own .nkdb, which must be left byte-for-byte
        // untouched. Mode=ReadOnly alone still lets SQLite create -wal/-shm sidecar files next to
        // a WAL-mode source the moment a connection opens, and they survive connection close/pool
        // clearing (removing them needs a checkpoint, a write operation). immutable=1 skips the
        // WAL/locking machinery entirely so no sidecar files are ever created.
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource  = new Uri(nkdbPath).AbsoluteUri + "?immutable=1",
            Mode        = SqliteOpenMode.ReadOnly,
            ForeignKeys = false,
        };
        try
        {
            await using var conn = new SqliteConnection(csb.ToString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT ConfigKey, ConfigValue FROM UnifiedMetadata WHERE ConfigKey BETWEEN {UnifiedMetadataKey.KSharedSlot_1} AND {UnifiedMetadataKey.KSharedSlot_3}";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.IsDBNull(1)) continue;
                int key  = reader.GetInt32(0);
                var blob = (byte[])reader[1];
                result.Add((key, blob));
            }
        }
        catch { }
        return result;
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // ValidateBackupFilesAsync — pre-copy validation (3 lines of defense, run before the auth gate)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// Validates every backup file (vault files + unified database, if present) before anything else
    /// happens - no password, no local file touched. 1 failure aborts the whole restore attempt
    /// (throws <see cref="RestoreValidationException"/>); the data is sensitive enough that a partial
    /// restore built from a mix of validated/unvalidated files is worse than no restore.
    /// </summary>
    public async Task ValidateBackupFilesAsync(string backupNkdbPath, string backupSourceDir)
    {
        var vaultCandidates = ScanVaultCandidates(backupSourceDir);
        foreach (var candidate in vaultCandidates)
            await DatabaseRestoreValidator.ValidateOrThrowAsync(candidate, isUnifiedDb: false);
        if (File.Exists(backupNkdbPath))
            await DatabaseRestoreValidator.ValidateOrThrowAsync(backupNkdbPath, isUnifiedDb: true);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // HasExistingLocalData — first guard's trigger condition (destructive overwrite confirmation)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>Whether localDataDir already holds files a restore would overwrite. Used by the UI
    /// to decide whether the destructive overwrite-confirmation dialog needs to appear at all (a
    /// brand-new PC migration - the primary use case - has nothing to lose, so the dialog is skipped).</summary>
    public bool HasExistingLocalData(string localDataDir)
        => Directory.Exists(localDataDir) && Directory.GetFiles(localDataDir).Length > 0;

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // RestoreAllFromBackupAsync — whole-tree overwrite copy (the only restore path since Rev.22)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// Overwrite-copies the contents of the backup source folder directly to local data/. Callers
    /// must have already validated the backup files (ValidateBackupFilesAsync) and confirmed the
    /// backup's authenticity (AuthenticateBackupNkdbAsync) before calling this - this method itself
    /// performs neither and copies unconditionally, exactly like an IT professional copying the
    /// backup folder directly over data/.
    /// </summary>
    /// <param name="backupNkdbPath">Path to the backup unified database.</param>
    /// <param name="backupSourceDir">Backup source folder (parent folder of the .nkdb).</param>
    /// <param name="localDataDir">Absolute path of the local data/ directory.</param>
    public async Task<(int Count, bool NkdbCopied)> RestoreAllFromBackupAsync(
        string backupNkdbPath, string backupSourceDir, string localDataDir)
    {
        var vaultCandidates = ScanVaultCandidates(backupSourceDir);

        int  restoredCount = 0;
        bool nkdbCopied    = false;

        ArchiveExistingDataFolder(localDataDir);

        if (!Directory.Exists(localDataDir))
            Directory.CreateDirectory(localDataDir);

        // Copy the vault files
        foreach (var backupFilePath in vaultCandidates)
        {
            var fileName  = Path.GetFileName(backupFilePath);
            var localPath = Path.Combine(localDataDir, fileName);
            if (string.Equals(backupFilePath, localPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var tmpPath = localPath + ".restore_tmp";
            try
            {
                File.Copy(backupFilePath, tmpPath, overwrite: true);
                SqliteConnection.ClearAllPools();
                File.Move(tmpPath, localPath, overwrite: true);
                logger.LogInformation("Restore: vault file restore completed. {F}", fileName);
                restoredCount++;
            }
            catch (Exception ex)
            {
                logger.LogError("Restore: vault file copy failed. {F} [{ExType}]", fileName, ex.GetType().Name);
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
        }

        // Overwrite-copy the unified database itself
        if (File.Exists(backupNkdbPath))
        {
            var localNkdbPath = Path.Combine(localDataDir, "NaimitsuVault.nkdb");
            var tmpNkdbPath   = localNkdbPath + ".restore_tmp";
            try
            {
                File.Copy(backupNkdbPath, tmpNkdbPath, overwrite: true);
                SqliteConnection.ClearAllPools();
                File.Move(tmpNkdbPath, localNkdbPath, overwrite: true);
                logger.LogInformation("Restore: unified database restore completed (direct backup copy).");
                restoredCount++;
                nkdbCopied = true;
            }
            catch (Exception ex)
            {
                logger.LogError("Restore: unified database copy failed. [{ExType}]", ex.GetType().Name);
                try { if (File.Exists(tmpNkdbPath)) File.Delete(tmpNkdbPath); } catch { }
            }
        }

        // The DPAPI-wrapped Windows Hello credentials the backup carried over (KSharedHello in the
        // copied nkdb, VaultDEKHello in each copied vault file) are tied to the machine/Windows
        // account that created them and are not guaranteed valid on this one. Rather than leaving a
        // silently-broken entry for the user to discover later, invalidate them unconditionally so
        // Hello always starts from a clean, re-enrollable state after a restore. Best-effort: a
        // failure here must not make an otherwise-successful restore look like it failed.
        try
        {
            await InvalidateWindowsHelloEverywhereAsync(localDataDir);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Restore: failed to invalidate Windows Hello credentials after restore. [{ExType}]", ex.GetType().Name);
        }

        return (restoredCount, nkdbCopied);
    }

    /// <summary>
    /// Enumerates the local unified database's VaultRegistries.DbNumber as plaintext.
    /// DbNumber itself is an unencrypted plaintext PK, so no K_shared or password is required.
    /// </summary>
    public async Task<IReadOnlyList<int>> GetLocalVaultDbNumbersAsync()
    {
        await using var db = await unifiedFactory.CreateDbContextAsync();
        return await db.VaultRegistries.Select(v => v.DbNumber).ToListAsync();
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // Pre-overwrite safety net (second guard)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// Best-effort safety net against a mistaken restore: copies data/'s existing top-level files
    /// (NaimitsuVault.nkdb and vault files) into data/archived_yyyyMMdd_HHmmss/ before the restore
    /// overwrites them. The subfolder is intentionally excluded from every other top-level-only scan over
    /// data/ (orphan file GC, shadow file recovery, EAC brute-force scan, ScanVaultCandidates), so
    /// archiving here cannot make those treat the copies as live/orphaned state.
    /// A failure here does not block the restore itself (the operator's explicit restore intent
    /// takes priority); it only logs a warning.
    /// </summary>
    private void ArchiveExistingDataFolder(string localDataDir)
    {
        try
        {
            if (!HasExistingLocalData(localDataDir)) return;

            // Release any pooled connection still holding a handle on the local unified DB (e.g. the
            // short-lived EF Core context ValidateBackupFilesAsync/AuthenticateBackupNkdbAsync just
            // used) so SQLite performs its close-time WAL checkpoint before this reads the file list.
            // Without this, -wal/-shm sidecars could still hold uncommitted pages, so a raw File.Copy
            // of the trio would risk archiving a torn, self-inconsistent snapshot instead of the same
            // pattern already used before every other file copy/move in this class.
            SqliteConnection.ClearAllPools();

            var existingFiles = Directory.GetFiles(localDataDir);

            var archiveName = $"archived_{DateTime.Now:yyyyMMdd_HHmmss}";
            var archivePath = Path.Combine(localDataDir, archiveName);
            for (int suffix = 2; Directory.Exists(archivePath); suffix++)
                archivePath = Path.Combine(localDataDir, $"{archiveName}-{suffix}");

            Directory.CreateDirectory(archivePath);
            foreach (var file in existingFiles)
                File.Copy(file, Path.Combine(archivePath, Path.GetFileName(file)), overwrite: false);

            logger.LogInformation(
                "Archived existing data/ contents before restore. Folder={Folder} FileCount={Count}",
                Path.GetFileName(archivePath), existingFiles.Length);

            RotateArchivedFolders(localDataDir);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to archive existing data/ contents before restore. [{ExType}]", ex.GetType().Name);
        }
    }

    private static readonly Regex ArchivedFolderPattern = new(@"^archived_\d{8}_\d{6}(-\d+)?$", RegexOptions.Compiled);
    private const int MaxArchiveGenerations = 3;

    /// <summary>
    /// Keeps only the newest MaxArchiveGenerations archived_* folders under data/, deleting older ones.
    /// archived_* is an internal safety net (not user-authored data the user chose to keep), so unlike
    /// the app's other data this rotation runs unconditionally with no confirmation dialog or audit log -
    /// the same treatment AutoBackupService.RotateGenerations already gives its own generation folders.
    /// Folder names sort lexicographically = chronologically (fixed-width date/time segments), matching
    /// AutoBackupService's ordering assumption.
    /// </summary>
    private void RotateArchivedFolders(string localDataDir)
    {
        var excessFolders = new DirectoryInfo(localDataDir)
            .GetDirectories()
            .Where(d => ArchivedFolderPattern.IsMatch(d.Name))
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .Skip(MaxArchiveGenerations);

        foreach (var folder in excessFolders)
        {
            try
            {
                folder.Delete(recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to delete an old archived_* folder. [{ExType}]", ex.GetType().Name);
            }
        }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // Helper methods
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>Enumerates 48-character Base64Url files (with the "nkdb" prefix) under data/.</summary>
    internal static List<string> ScanVaultCandidates(string dataDir)
    {
        if (!Directory.Exists(dataDir)) return [];
        var result = new List<string>();
        foreach (var f in Directory.GetFiles(dataDir))
        {
            var name = Path.GetFileName(f);
            if (name.Length != 48) continue;
            try
            {
                var raw = DecodeBase64UrlNoPad(name);
                if (raw.Length >= 4 && raw.AsSpan(0, 4).SequenceEqual("nkdb"u8))
                    result.Add(f);
            }
            catch { }
        }
        return result;
    }
}
