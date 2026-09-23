// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Repositories;

namespace NaimitsuVault.Tests;

/// <summary>
/// I/O environment destruction tests (TC-SFR-13 .. TC-SFR-16).
/// Verifies the robustness of AutoBackupService.RunBackup() and SaveDraftAsync() against
/// IOException / UnauthorizedAccessException.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class IoEnvironmentDestructionTests : IDisposable
{
    // A unique temp folder used by each test
    private readonly string _tmpRoot;

    public IoEnvironmentDestructionTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), $"naimitsu_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpRoot);
    }

    public void Dispose()
    {
        // The SQLite connection pool can leave a file held open, so clear it first
        SqliteConnection.ClearAllPools();
        // Clear the ReadOnly attribute on any file that still has it before deleting
        foreach (var fi in new DirectoryInfo(_tmpRoot).EnumerateFiles("*", SearchOption.AllDirectories))
            fi.Attributes &= ~FileAttributes.ReadOnly;
        Directory.Delete(_tmpRoot, recursive: true);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a valid SQLite file at a temp path and returns it.
    /// </summary>
    private string CreateRealSqliteFile(string name = "source.nkdb")
    {
        var path = Path.Combine(_tmpRoot, name);
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // PRAGMA user_version commits a write transaction, flushing the SQLite file header to disk.
        // With only Open()/Close(), the file would remain 0 bytes, so this one line is essential.
        cmd.CommandText = "PRAGMA user_version = 1;";
        cmd.ExecuteNonQuery();
        return path;
    }

    private string FailureFlagPath(string folderName = "flags")
    {
        var dir = Path.Combine(_tmpRoot, folderName);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "backup_failed.flag");
    }

    // ── TC-SFR-13 ─────────────────────────────────────────────────────────────
    // IOException simulation:
    // fix the tmp_ folder name and pre-place a file with that name, so that
    // Directory.CreateDirectory(tmpPath) throws IOException -> catch-all -> failure flag written

    [Fact]
    public void RunBackup_TempFolderCreationBlocked_WritesFailureFlagAndDoesNotPropagate()
    {
        var sourceDb    = CreateRealSqliteFile();
        var backupDir   = Path.Combine(_tmpRoot, "backup");
        Directory.CreateDirectory(backupDir);

        const string fixedTmpName = "tmp_fixedforiotest";
        // Pre-place the tmp_ path as a "regular file" rather than a directory
        // -> Directory.CreateDirectory(tmpPath) throws IOException
        File.WriteAllText(Path.Combine(backupDir, fixedTmpName), "blocked");

        var flagPath = FailureFlagPath();
        var svc = new TestableAutoBackupService(sourceDb, flagPath, fixedTempFolderName: fixedTmpName)
        {
            IsEnabled = true,
            Folder    = backupDir,
        };
        svc.MarkContentChanged(); // RunBackup() now short-circuits without this pending-backup flag

        // No exception must propagate
        var ex = Record.Exception(() => svc.RunBackup());
        Assert.Null(ex);

        // The failure flag must have been written
        Assert.True(File.Exists(flagPath),
            "The failure flag was not written after an IOException occurred");
    }

    // ── TC-SFR-14 ─────────────────────────────────────────────────────────────
    // SqliteException simulation:
    // place an invalid (corrupt) vault file inside data/ and have SqliteConnection.BackupDatabase()
    // throw an exception -> catch-all -> failure flag written

    [Fact]
    public void RunBackup_CorruptVaultFileDuringCopy_WritesFailureFlagAndDoesNotPropagate()
    {
        var sourceDb  = CreateRealSqliteFile();
        var backupDir = Path.Combine(_tmpRoot, "backup_corrupt");
        Directory.CreateDirectory(backupDir);

        var dataDir = Path.Combine(_tmpRoot, "data_corrupt");
        Directory.CreateDirectory(dataDir);

        // Place a corrupt file that satisfies the 48-character Base64Url / "nkdb" prefix requirement
        var raw = new byte[36];
        "nkdb"u8.CopyTo(raw.AsSpan(0, 4));
        System.Security.Cryptography.RandomNumberGenerator.Fill(raw.AsSpan(4));
        var corruptName = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllBytes(Path.Combine(dataDir, corruptName), "not a valid sqlite file"u8.ToArray());

        var flagPath = FailureFlagPath("flags_corrupt");
        var svc = new TestableAutoBackupService(sourceDb, flagPath, dataDir: dataDir)
        {
            IsEnabled = true,
            Folder    = backupDir,
        };
        svc.MarkContentChanged(); // RunBackup() now short-circuits without this pending-backup flag

        var ex = Record.Exception(() => svc.RunBackup());
        Assert.Null(ex);

        Assert.True(File.Exists(flagPath),
            "The failure flag was not written after the corrupt vault file's copy failed");
    }

    // ── TC-SFR-15 ─────────────────────────────────────────────────────────────
    // An IOException against SaveDraftAsync - injected via FaultInjectionDbContextFactory.
    // Confirms IOException propagates because SaveDraftAsync never swallows exceptions

    [Fact]
    public async Task SaveDraftAsync_InjectedIoException_Propagates()
    {
        var factory = new FaultInjectionDbContextFactory(
            new IOException("Test-only: simulated disk full"));

        var repo = new SecretDraftsRepository(factory, new IdentityCryptoService());

        await Assert.ThrowsAsync<IOException>(
            () => repo.SaveDraftAsync(1, new byte[64], DateTime.UtcNow));
    }

    // ── TC-SFR-16 ─────────────────────────────────────────────────────────────
    // An UnauthorizedAccessException against SaveDraftAsync - likewise injected via FaultInjection.
    // Confirms the repository layer does not swallow security exceptions

    [Fact]
    public async Task SaveDraftAsync_InjectedUnauthorizedAccessException_Propagates()
    {
        var factory = new FaultInjectionDbContextFactory(
            new UnauthorizedAccessException("Test-only: simulated no permission"));

        var repo = new SecretDraftsRepository(factory, new IdentityCryptoService());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => repo.SaveDraftAsync(1, new byte[64], DateTime.UtcNow));
    }
}
