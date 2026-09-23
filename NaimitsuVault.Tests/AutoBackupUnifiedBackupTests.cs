// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Verifies the bulk-backup / sanitize spec (the shared data flow for both manual and automatic backups).
///
/// Since PerformUnifiedBackup is the common execution code path called by both the manual backup
/// (SettingsViewModel) and the automatic backup (RunBackup), this test drives AutoBackupService
/// directly to verify the core logic shared by both paths (tmp_GUID staging, atomic rename,
/// generation rotation), plus the pending-backup flag that gates the automatic path.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class AutoBackupUnifiedBackupTests : IDisposable
{
    private readonly string _tmpRoot;

    public AutoBackupUnifiedBackupTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), $"naimitsu_unified_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    private static string CreateRealSqliteFile(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // PRAGMA user_version commits a write transaction, flushing the SQLite file header to disk.
        // With only Open()/Close(), the file would remain 0 bytes, so this one line is essential.
        cmd.CommandText = "PRAGMA user_version = 1;";
        cmd.ExecuteNonQuery();
        return path;
    }

    private static string MakeValidVaultFileName()
    {
        var raw = new byte[36];
        "nkdb"u8.CopyTo(raw.AsSpan(0, 4));
        System.Security.Cryptography.RandomNumberGenerator.Fill(raw.AsSpan(4));
        return Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // TC-AB-01
    // PerformUnifiedBackup copies the unified DB plus all vault files together into a single
    // Naimitsu_yyyyMMdd_HHmmss folder, leaving no tmp_ folder behind

    [Fact]
    public void PerformUnifiedBackup_CopiesNkdbAndAllVaultFiles_IntoSingleTimestampedFolder()
    {
        var dataDir = Path.Combine(_tmpRoot, "data");
        Directory.CreateDirectory(dataDir);
        var dbPath = CreateRealSqliteFile(Path.Combine(dataDir, "NaimitsuVault.nkdb"));
        var vault1 = CreateRealSqliteFile(Path.Combine(dataDir, MakeValidVaultFileName()));
        var vault2 = CreateRealSqliteFile(Path.Combine(dataDir, MakeValidVaultFileName()));

        var backupDir = Path.Combine(_tmpRoot, "backup");
        Directory.CreateDirectory(backupDir);

        var svc = new TestableAutoBackupService(dbPath, Path.Combine(_tmpRoot, "flag"), dataDir: dataDir);

        bool success = svc.PerformUnifiedBackup(backupDir);

        Assert.True(success);
        var subDirs = Directory.GetDirectories(backupDir);
        Assert.Single(subDirs);
        Assert.Matches(@"^Naimitsu_\d{8}_\d{6}$", Path.GetFileName(subDirs[0]));
        Assert.True(File.Exists(Path.Combine(subDirs[0], "NaimitsuVault.nkdb")));
        Assert.True(File.Exists(Path.Combine(subDirs[0], Path.GetFileName(vault1))));
        Assert.True(File.Exists(Path.Combine(subDirs[0], Path.GetFileName(vault2))));
        // No tmp_ folder remains (proof the atomic rename completed)
        Assert.DoesNotContain(subDirs, d => Path.GetFileName(d).StartsWith("tmp_", StringComparison.Ordinal));
    }

    // TC-AB-02
    // The temp folder name is "tmp_" + a GUID in "N" format (fixed at 36 characters)

    [Fact]
    public void NewTempFolderName_IsTmpPrefixPlusGuidN_36CharsFixed()
    {
        var svc = new TestableAutoBackupService("unused", "unused");
        var name = svc.NewTempFolderName();

        Assert.StartsWith("tmp_", name);
        Assert.Equal(36, name.Length);
        // The 32 characters after "tmp_" consist only of hex digits (GUID "N" format)
        Assert.Matches("^[0-9a-f]{32}$", name[4..]);
    }

    // TC-AB-03
    // Exactly MaxGenerations (3) healthy folders is not "one too many" - nothing gets deleted, and
    // all 3 remain. (Until 2026-08-20 this used a >= check paired with a single-oldest deletion,
    // which meant every RunBackup() call once 3 existed immediately dropped back to 2 - the steady
    // state was MaxGenerations-1, not MaxGenerations. Reported as "only 2 generations survive".)

    [Fact]
    public void RotateGenerations_WhenHealthyCountIsExactlyThree_DeletesNothing()
    {
        var backupDir = Path.Combine(_tmpRoot, "backup_rotate");
        Directory.CreateDirectory(backupDir);

        CreateHealthyGeneration(backupDir, "Naimitsu_20260101_000000");
        CreateHealthyGeneration(backupDir, "Naimitsu_20260102_000000");
        CreateHealthyGeneration(backupDir, "Naimitsu_20260103_000000");

        var svc = new AutoBackupService(NullLogger<AutoBackupService>.Instance);
        svc.RotateGenerations(backupDir);

        var remaining = Directory.GetDirectories(backupDir).Select(d => Path.GetFileName(d)!).OrderBy(n => n).ToArray();
        Assert.Equal(
            ["Naimitsu_20260101_000000", "Naimitsu_20260102_000000", "Naimitsu_20260103_000000"],
            remaining);
    }

    // TC-AB-03b
    // Once healthy folders exceed 3 (e.g. left over from before the fix above, or a manually copied
    // extra folder), rotation trims down to exactly the newest 3 in a single pass - not just the one
    // oldest folder - so any pre-existing backlog self-heals in one RunBackup() call.

    [Fact]
    public void RotateGenerations_WhenHealthyCountExceedsThree_TrimsToNewestThreeInOnePass()
    {
        var backupDir = Path.Combine(_tmpRoot, "backup_rotate_excess");
        Directory.CreateDirectory(backupDir);

        CreateHealthyGeneration(backupDir, "Naimitsu_20260101_000000");
        CreateHealthyGeneration(backupDir, "Naimitsu_20260102_000000");
        CreateHealthyGeneration(backupDir, "Naimitsu_20260103_000000");
        CreateHealthyGeneration(backupDir, "Naimitsu_20260104_000000");
        CreateHealthyGeneration(backupDir, "Naimitsu_20260105_000000");

        var svc = new AutoBackupService(NullLogger<AutoBackupService>.Instance);
        svc.RotateGenerations(backupDir);

        var remaining = Directory.GetDirectories(backupDir).Select(d => Path.GetFileName(d)!).OrderBy(n => n).ToArray();
        Assert.Equal(
            ["Naimitsu_20260103_000000", "Naimitsu_20260104_000000", "Naimitsu_20260105_000000"],
            remaining);
    }

    // TC-AB-04
    // Folders that violate the naming convention, folders missing an nkdb, and folders with a
    // 0-byte nkdb are excluded from the generation count and physically deleted immediately

    [Fact]
    public void RotateGenerations_RemovesCorruptOrMisnamedFolders_RegardlessOfCount()
    {
        var backupDir = Path.Combine(_tmpRoot, "backup_corrupt_rotate");
        Directory.CreateDirectory(backupDir);

        // Violates the naming convention (simulating leftover old tmp_ debris)
        Directory.CreateDirectory(Path.Combine(backupDir, "tmp_orphaned1234567890123456789012"));
        // No nkdb present
        Directory.CreateDirectory(Path.Combine(backupDir, "Naimitsu_20260101_000000"));
        // nkdb is 0 bytes
        var zeroByteGen = Path.Combine(backupDir, "Naimitsu_20260102_000000");
        Directory.CreateDirectory(zeroByteGen);
        File.WriteAllBytes(Path.Combine(zeroByteGen, "NaimitsuVault.nkdb"), []);
        // Only this single healthy one should remain
        CreateHealthyGeneration(backupDir, "Naimitsu_20260103_000000");

        var svc = new AutoBackupService(NullLogger<AutoBackupService>.Instance);
        svc.RotateGenerations(backupDir);

        var remaining = Directory.GetDirectories(backupDir).Select(d => Path.GetFileName(d)!).ToArray();
        Assert.Equal(["Naimitsu_20260103_000000"], remaining);
    }

    // TC-AB-05
    // Execution boundary condition: if PerformUnifiedBackup fails, RunBackup() must not run
    // rotation at all, and existing valid generation folders must not get caught up and deleted.
    // Also confirms the pending-backup flag survives a failed attempt, so the change isn't lost -
    // the next successful backup should still pick it up.

    [Fact]
    public void RunBackup_WhenStagingFails_DoesNotRotateExistingGenerations()
    {
        var dataDir = Path.Combine(_tmpRoot, "data_fail");
        Directory.CreateDirectory(dataDir);
        var dbPath = CreateRealSqliteFile(Path.Combine(dataDir, "NaimitsuVault.nkdb"));

        var backupDir = Path.Combine(_tmpRoot, "backup_fail");
        Directory.CreateDirectory(backupDir);

        // Place 3 existing healthy generation folders (normally enough to trigger rotation, depending on the location)
        CreateHealthyGeneration(backupDir, "Naimitsu_20260101_000000");
        CreateHealthyGeneration(backupDir, "Naimitsu_20260102_000000");
        CreateHealthyGeneration(backupDir, "Naimitsu_20260103_000000");

        const string fixedTmpName = "tmp_blockedforboundarytest";
        // Pre-place the tmp_ path as a file to force PerformUnifiedBackup to fail
        File.WriteAllText(Path.Combine(backupDir, fixedTmpName), "blocked");

        var svc = new TestableAutoBackupService(
            dbPath, Path.Combine(_tmpRoot, "flag_fail"),
            dataDir: dataDir, fixedTempFolderName: fixedTmpName)
        {
            IsEnabled = true,
            Folder    = backupDir,
        };
        svc.MarkContentChanged(); // otherwise RunBackup() short-circuits before ever reaching staging

        Assert.Equal(AutoBackupService.AutoBackupResult.Failed, svc.RunBackup());

        // On failure, execution stops at the staging step, so the existing 3 generations must remain completely unchanged
        var remaining = Directory.GetDirectories(backupDir)
            .Select(d => Path.GetFileName(d)!)
            .Where(n => n.StartsWith("Naimitsu_", StringComparison.Ordinal))
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(
            ["Naimitsu_20260101_000000", "Naimitsu_20260102_000000", "Naimitsu_20260103_000000"],
            remaining);

        // The pending flag must NOT be cleared on failure - the change is still un-backed-up
        Assert.True(File.Exists(svc.GetPendingBackupFlagPath()));
    }

    // TC-AB-06
    // Confirms the symmetry that the manual path (calling PerformUnifiedBackup directly) and the
    // automatic path (via RunBackup) are exactly the same execution code path.
    // RunBackup() merely adds the pending-flag gate in front of PerformUnifiedBackup, so once that
    // gate is satisfied the resulting generation folder structure is indistinguishable between the two paths.

    [Fact]
    public void RunBackup_AndDirectPerformUnifiedBackup_ProduceIdenticalFolderStructure()
    {
        var dataDir = Path.Combine(_tmpRoot, "data_symmetry");
        Directory.CreateDirectory(dataDir);
        var dbPath = CreateRealSqliteFile(Path.Combine(dataDir, "NaimitsuVault.nkdb"));
        CreateRealSqliteFile(Path.Combine(dataDir, MakeValidVaultFileName()));

        var autoBackupDir   = Path.Combine(_tmpRoot, "backup_auto");
        var manualBackupDir = Path.Combine(_tmpRoot, "backup_manual");
        Directory.CreateDirectory(autoBackupDir);
        Directory.CreateDirectory(manualBackupDir);

        var autoSvc = new TestableAutoBackupService(dbPath, Path.Combine(_tmpRoot, "flag_a"), dataDir: dataDir)
        {
            IsEnabled = true,
            Folder    = autoBackupDir,
        };
        autoSvc.MarkContentChanged(); // otherwise RunBackup() short-circuits without ever staging
        autoSvc.RunBackup();

        var manualSvc = new TestableAutoBackupService(dbPath, Path.Combine(_tmpRoot, "flag_m"), dataDir: dataDir);
        bool manualSuccess = manualSvc.PerformUnifiedBackup(manualBackupDir);

        Assert.True(manualSuccess);

        string[] FilesOf(string root) => Directory.GetDirectories(root)
            .SelectMany(Directory.GetFiles)
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToArray()!;

        Assert.Equal(FilesOf(autoBackupDir), FilesOf(manualBackupDir));
    }

    private static void CreateHealthyGeneration(string backupDir, string folderName)
    {
        var path = Path.Combine(backupDir, folderName);
        Directory.CreateDirectory(path);
        CreateRealSqliteFile(Path.Combine(path, "NaimitsuVault.nkdb"));
    }

    // TC-AB-07
    // RunBackup() must skip entirely - without staging anything - when MarkContentChanged() was never
    // called since the last backup. This is the core of the flag-based design requested in place of an
    // earlier content-comparison approach: no confirmed Secret/StoredFile/Profile save or delete means
    // nothing worth protecting has changed, regardless of what else happened (settings changes, audit
    // log writes, favicon prefetch, shadow-file rewrites, etc. never touch the flag).

    [Fact]
    public void RunBackup_WhenNoContentChangeWasMarked_SkipsWithoutStagingAnything()
    {
        var dataDir = Path.Combine(_tmpRoot, "data_no_flag");
        Directory.CreateDirectory(dataDir);
        var dbPath = CreateRealSqliteFile(Path.Combine(dataDir, "NaimitsuVault.nkdb"));

        var backupDir = Path.Combine(_tmpRoot, "backup_no_flag");
        Directory.CreateDirectory(backupDir);

        var svc = new TestableAutoBackupService(dbPath, Path.Combine(_tmpRoot, "flag_no_flag"), dataDir: dataDir)
        {
            IsEnabled = true,
            Folder    = backupDir,
        };

        Assert.Equal(AutoBackupService.AutoBackupResult.SkippedNoChange, svc.RunBackup());
        Assert.Empty(Directory.GetDirectories(backupDir));
    }

    // TC-AB-08
    // After MarkContentChanged(), RunBackup() creates a generation and clears the pending flag - so an
    // immediate second RunBackup() (nothing marked in between) correctly skips rather than creating a
    // second generation for the same unbacked-up change.

    [Fact]
    public void RunBackup_AfterMarkContentChanged_CreatesGenerationThenClearsFlagForNextRun()
    {
        var dataDir = Path.Combine(_tmpRoot, "data_marked");
        Directory.CreateDirectory(dataDir);
        var dbPath = CreateRealSqliteFile(Path.Combine(dataDir, "NaimitsuVault.nkdb"));

        var backupDir = Path.Combine(_tmpRoot, "backup_marked");
        Directory.CreateDirectory(backupDir);

        var svc = new TestableAutoBackupService(dbPath, Path.Combine(_tmpRoot, "flag_marked"), dataDir: dataDir)
        {
            IsEnabled = true,
            Folder    = backupDir,
        };

        svc.MarkContentChanged();
        Assert.Equal(AutoBackupService.AutoBackupResult.Created, svc.RunBackup());
        Assert.Single(Directory.GetDirectories(backupDir));
        Assert.False(File.Exists(svc.GetPendingBackupFlagPath()));

        // Nothing was marked since that backup - must skip, not create a second generation
        Assert.Equal(AutoBackupService.AutoBackupResult.SkippedNoChange, svc.RunBackup());
        Assert.Single(Directory.GetDirectories(backupDir));
    }

    // TC-AB-09
    // A manual backup (PerformUnifiedBackup, called directly - e.g. from the Settings page) also
    // clears the pending flag on success, since it captures the current state just as completely as
    // an automatic one. Without this, a manual backup right after an edit would leave the flag set,
    // and the very next automatic exit (with no further edits) would create a redundant second
    // generation of identical content.

    [Fact]
    public void PerformUnifiedBackup_AfterMarkContentChanged_AlsoClearsThePendingFlag()
    {
        var dataDir = Path.Combine(_tmpRoot, "data_manual_clears");
        Directory.CreateDirectory(dataDir);
        var dbPath = CreateRealSqliteFile(Path.Combine(dataDir, "NaimitsuVault.nkdb"));

        var backupDir = Path.Combine(_tmpRoot, "backup_manual_clears");
        Directory.CreateDirectory(backupDir);

        var svc = new TestableAutoBackupService(dbPath, Path.Combine(_tmpRoot, "flag_manual_clears"), dataDir: dataDir)
        {
            IsEnabled = true,
            Folder    = backupDir,
        };

        svc.MarkContentChanged();
        Assert.True(svc.PerformUnifiedBackup(backupDir));
        Assert.False(File.Exists(svc.GetPendingBackupFlagPath()));

        // The next automatic exit, with nothing further marked, must skip
        Assert.Equal(AutoBackupService.AutoBackupResult.SkippedNoChange, svc.RunBackup());
        Assert.Single(Directory.GetDirectories(backupDir));
    }

    // TC-AB-10
    // MarkContentChanged() is idempotent and does not error when called multiple times before a
    // backup ever runs (e.g. several edits in one session) - only one generation should result once
    // RunBackup() finally runs.

    [Fact]
    public void MarkContentChanged_CalledMultipleTimes_StillProducesExactlyOneGeneration()
    {
        var dataDir = Path.Combine(_tmpRoot, "data_multi_mark");
        Directory.CreateDirectory(dataDir);
        var dbPath = CreateRealSqliteFile(Path.Combine(dataDir, "NaimitsuVault.nkdb"));

        var backupDir = Path.Combine(_tmpRoot, "backup_multi_mark");
        Directory.CreateDirectory(backupDir);

        var svc = new TestableAutoBackupService(dbPath, Path.Combine(_tmpRoot, "flag_multi_mark"), dataDir: dataDir)
        {
            IsEnabled = true,
            Folder    = backupDir,
        };

        svc.MarkContentChanged();
        svc.MarkContentChanged();
        svc.MarkContentChanged();

        Assert.Equal(AutoBackupService.AutoBackupResult.Created, svc.RunBackup());
        Assert.Single(Directory.GetDirectories(backupDir));
    }

    // TC-AB-11
    // Calling PerformUnifiedBackup twice back-to-back must always produce two distinct generation
    // folders, whether or not both calls land within the same wall-clock second (the common case for
    // two fast successive calls in a test, and for a user double-clicking "Backup now"). Until this
    // fix, a same-second collision appended a GUID suffix that GenerationFolderPattern didn't
    // recognize, so the very next RotateGenerations() call misjudged the fresh folder as
    // corrupt/invalid and deleted it immediately - silently losing the backup that just "succeeded".
    // Mirrors RestoreHelperTests's identical archived_* collision test for AuthService.Restore.

    [Fact]
    public void PerformUnifiedBackup_CalledTwiceInQuickSuccession_CreatesDistinctFoldersRatherThanCollision()
    {
        var dataDir = Path.Combine(_tmpRoot, "data_quick_succession");
        Directory.CreateDirectory(dataDir);
        var dbPath = CreateRealSqliteFile(Path.Combine(dataDir, "NaimitsuVault.nkdb"));

        var backupDir = Path.Combine(_tmpRoot, "backup_quick_succession");
        Directory.CreateDirectory(backupDir);

        var svc = new TestableAutoBackupService(dbPath, Path.Combine(_tmpRoot, "flag_quick_succession"), dataDir: dataDir);

        Assert.True(svc.PerformUnifiedBackup(backupDir));
        var firstDirs = Directory.GetDirectories(backupDir);
        Assert.Single(firstDirs);

        Assert.True(svc.PerformUnifiedBackup(backupDir));
        var secondDirs = Directory.GetDirectories(backupDir);

        Assert.Equal(2, secondDirs.Length);
        Assert.Contains(firstDirs[0], secondDirs);
        // Both folders must satisfy the naming convention rotation relies on to judge a folder healthy -
        // otherwise the very next rotation would immediately delete whichever one collided.
        foreach (var d in secondDirs)
            Assert.Matches(@"^Naimitsu_\d{8}_\d{6}(-\d+)?$", Path.GetFileName(d));
    }

    // TC-AB-12
    // A generation folder with the numbered collision suffix ("-2") that PerformUnifiedBackup now
    // appends on a same-second name clash must be recognized as healthy by RotateGenerations, not
    // deleted as a naming-convention violation.

    [Fact]
    public void RotateGenerations_KeepsHealthyFolderWithNumberedCollisionSuffix()
    {
        var backupDir = Path.Combine(_tmpRoot, "backup_rotate_suffix");
        Directory.CreateDirectory(backupDir);

        CreateHealthyGeneration(backupDir, "Naimitsu_20260101_000000");
        CreateHealthyGeneration(backupDir, "Naimitsu_20260101_000000-2");

        var svc = new AutoBackupService(NullLogger<AutoBackupService>.Instance);
        svc.RotateGenerations(backupDir);

        var remaining = Directory.GetDirectories(backupDir).Select(d => Path.GetFileName(d)!).OrderBy(n => n).ToArray();
        Assert.Equal(["Naimitsu_20260101_000000", "Naimitsu_20260101_000000-2"], remaining);
    }
}
