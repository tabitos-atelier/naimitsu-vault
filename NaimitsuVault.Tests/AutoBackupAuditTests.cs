// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Recording automatic backup failures and skipped runs" TC-ABA-01–12.
/// Covers the RunBackup results that leave a trace (SkippedFolderMissing / Failed), the failure flag the
/// next launch reads (its reason, and that a manual backup failure leaves none), and BackupAuditMarker with
/// AuditLogService.FlushPendingBackupAuditAsync, which writes the result of an exit taken while locked
/// once a vault is unlocked again.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class AutoBackupAuditTests : IDisposable
{
    private readonly string _tmpRoot = Path.Combine(Path.GetTempPath(), $"naimitsu_backup_audit_{Guid.NewGuid():N}");
    private readonly TestDb _db = TestDb.Create();

    public AutoBackupAuditTests() => Directory.CreateDirectory(_tmpRoot);

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    private string FlagPath => Path.Combine(_tmpRoot, "backup_failed.flag");

    private static string CreateRealSqliteFile(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version = 1;";
        cmd.ExecuteNonQuery();
        return path;
    }

    private TestableAutoBackupService MakeService(string folder, string? fixedTempFolderName = null)
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(_tmpRoot, "data")).FullName;
        var dbPath = CreateRealSqliteFile(Path.Combine(dataDir, "NaimitsuVault.nkdb"));
        return new TestableAutoBackupService(dbPath, FlagPath, dataDir: dataDir, fixedTempFolderName: fixedTempFolderName)
        {
            IsEnabled = true,
            Folder    = folder,
        };
    }

    // TC-ABA-01: a missing folder with confirmed changes is SkippedFolderMissing; the reason goes to the
    // failure flag, and the pending flag survives so the next exit with the folder present backs up.
    [Fact]
    public void RunBackup_FolderMissingWithChanges_IsSkippedFolderMissing()
    {
        var svc = MakeService(Path.Combine(_tmpRoot, "unplugged"));
        svc.MarkContentChanged();

        Assert.Equal(AutoBackupService.AutoBackupResult.SkippedFolderMissing, svc.RunBackup());
        Assert.Equal(AutoBackupService.FailureReasonFolderMissing, File.ReadAllText(FlagPath));
        Assert.True(File.Exists(svc.GetPendingBackupFlagPath()));
    }

    // TC-ABA-02: a missing folder with nothing changed leaves no trace (nothing would have been backed up).
    [Fact]
    public void RunBackup_FolderMissingWithoutChanges_IsNotAttemptedAndWritesNoFlag()
    {
        var svc = MakeService(Path.Combine(_tmpRoot, "unplugged"));

        Assert.Equal(AutoBackupService.AutoBackupResult.NotAttempted, svc.RunBackup());
        Assert.False(File.Exists(FlagPath));
    }

    // TC-ABA-03: an automatic backup failure writes the Failed reason.
    [Fact]
    public void RunBackup_Failure_WritesFailedReason()
    {
        var backupDir = Directory.CreateDirectory(Path.Combine(_tmpRoot, "backup")).FullName;
        const string fixedTmpName = "tmp_blocked";
        File.WriteAllText(Path.Combine(backupDir, fixedTmpName), "blocked");
        var svc = MakeService(backupDir, fixedTmpName);
        svc.MarkContentChanged();

        Assert.Equal(AutoBackupService.AutoBackupResult.Failed, svc.RunBackup());
        Assert.Equal(AutoBackupService.FailureReasonFailed, File.ReadAllText(FlagPath));
    }

    // TC-ABA-04: a manual backup failure (PerformUnifiedBackup called directly) leaves no failure flag,
    // so the next launch does not claim that the last exit's automatic backup failed.
    [Fact]
    public void PerformUnifiedBackup_Failure_WritesNoFailureFlag()
    {
        var backupDir = Directory.CreateDirectory(Path.Combine(_tmpRoot, "backup")).FullName;
        const string fixedTmpName = "tmp_blocked";
        File.WriteAllText(Path.Combine(backupDir, fixedTmpName), "blocked");
        var svc = MakeService(backupDir, fixedTmpName);

        Assert.False(svc.PerformUnifiedBackup(backupDir));
        Assert.False(File.Exists(FlagPath));
    }

    // TC-ABA-05: a successful backup (manual or automatic) clears a failure flag left by an earlier exit.
    [Fact]
    public void PerformUnifiedBackup_Success_ClearsFailureFlag()
    {
        var backupDir = Directory.CreateDirectory(Path.Combine(_tmpRoot, "backup")).FullName;
        File.WriteAllText(FlagPath, AutoBackupService.FailureReasonFolderMissing);
        var svc = MakeService(backupDir);

        Assert.True(svc.PerformUnifiedBackup(backupDir));
        Assert.False(File.Exists(FlagPath));
    }

    // TC-ABA-06: the next launch reads the reason and deletes the flag.
    [Theory]
    [InlineData("FolderMissing", true)]
    [InlineData("Failed", false)]
    [InlineData("IOException", false)]   // written by versions before the reason was recorded
    public void TryConsumeFailureFlag_ReadsReasonAndDeletes(string content, bool expectedFolderMissing)
    {
        File.WriteAllText(FlagPath, content);

        Assert.True(AutoBackupService.TryConsumeFailureFlag(FlagPath, out var folderMissing));
        Assert.Equal(expectedFolderMissing, folderMissing);
        Assert.False(File.Exists(FlagPath));
    }

    // TC-ABA-07: no flag, no notice.
    [Fact]
    public void TryConsumeFailureFlag_NoFlag_ReturnsFalse()
        => Assert.False(AutoBackupService.TryConsumeFailureFlag(FlagPath, out _));

    // TC-ABA-08: the marker round-trips the code and the exit time.
    [Theory]
    [InlineData(AuditEventCode.AutoBackupExecuted)]
    [InlineData(AuditEventCode.AutoBackupFailed)]
    [InlineData(AuditEventCode.AutoBackupSkippedFolderMissing)]
    public void BackupAuditMarker_RoundTrips(AuditEventCode code)
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BackupAuditMarker.Write(_tmpRoot, code);

        var entry = BackupAuditMarker.TryRead(_tmpRoot);
        Assert.NotNull(entry);
        Assert.Equal(code, entry.Value.Code);
        Assert.InRange(entry.Value.TimestampUnix, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    // TC-ABA-09: a marker holding any other code is ignored, so a tampered or stray file cannot inject
    // an arbitrary event into the audit log.
    [Fact]
    public void BackupAuditMarker_OtherCode_IsIgnored()
    {
        File.WriteAllText(Path.Combine(_tmpRoot, "pending_backup_audit.flag"), $"{(int)AuditEventCode.RestoreExecuted}\n0");
        Assert.Null(BackupAuditMarker.TryRead(_tmpRoot));
    }

    private AuditLogService MakeAuditService()
        => new(new AuditLogRepository(_db.Factory), null!, null!, null!, null!);

    private async Task<List<AuditLog>> ReadAllLogsAsync()
    {
        await using var ctx = _db.Factory.CreateDbContext();
        return await ctx.AuditLogs.ToListAsync(TestContext.Current.CancellationToken);
    }

    // TC-ABA-10: without a marker, nothing is written.
    [Fact]
    public async Task FlushPendingBackupAuditAsync_NoMarker_DoesNothing()
    {
        await MakeAuditService().FlushPendingBackupAuditAsync(default, _tmpRoot, TestContext.Current.CancellationToken);
        Assert.Empty(await ReadAllLogsAsync());
    }

    // TC-ABA-11: the pending entry is written with its code, warning level and the original exit time,
    // and the marker is deleted so it is written only once.
    [Fact]
    public async Task FlushPendingBackupAuditAsync_WritesEntryWithExitTimeAndDeletesMarker()
    {
        BackupAuditMarker.Write(_tmpRoot, AuditEventCode.AutoBackupSkippedFolderMissing);
        var exitTime = BackupAuditMarker.TryRead(_tmpRoot)!.Value.TimestampUnix;

        await MakeAuditService().FlushPendingBackupAuditAsync(default, _tmpRoot, TestContext.Current.CancellationToken);

        var log = Assert.Single(await ReadAllLogsAsync());
        Assert.Equal((int)AuditEventCode.AutoBackupSkippedFolderMissing, log.EventCode);
        Assert.Equal(1, log.EventLevel);
        Assert.Equal(exitTime, log.CreatedAt);
        Assert.Null(BackupAuditMarker.TryRead(_tmpRoot));
    }

    // TC-ABA-12: the backup marker and the restore marker are separate files that do not overwrite each other.
    [Fact]
    public void BackupAuditMarker_DoesNotOverwriteRestoreMarker()
    {
        RestoreAuditMarker.Write(_tmpRoot, AuditEventCode.RestoreExecuted, [1]);
        BackupAuditMarker.Write(_tmpRoot, AuditEventCode.AutoBackupFailed);

        Assert.Equal(AuditEventCode.RestoreExecuted, RestoreAuditMarker.TryRead(_tmpRoot)!.Value.Code);
        Assert.Equal(AuditEventCode.AutoBackupFailed, BackupAuditMarker.TryRead(_tmpRoot)!.Value.Code);
    }
}
