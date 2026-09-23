// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression tests for AuditLogService.FlushPendingRestoreAuditAsync (Rev. 9).
/// Because RestoreExecuted is an event tied to specific vaults,
/// unlike FlushPreAuthFailuresAsync (a vault-independent counter), a log entry is written
/// only when it matches a DbNumber targeted by the marker, and unrelated vaults are left
/// untouched.
/// </summary>
public sealed class AuditLogServiceRestoreFlushTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"naimitsu_alsrf_{Guid.NewGuid():N}");
    private readonly TestDb _db = TestDb.Create();

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private AuditLogService MakeService()
        => new(new AuditLogRepository(_db.Factory), null!, null!, null!, null!);

    private async Task<List<AuditLog>> ReadAllLogsAsync()
    {
        await using var ctx = _db.Factory.CreateDbContext();
        return await ctx.AuditLogs.ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FlushPendingRestoreAuditAsync_NoMarkerFile_DoesNothing()
    {
        var service = MakeService();

        await service.FlushPendingRestoreAuditAsync(default, currentDbNumber: 1, _dataDir, TestContext.Current.CancellationToken);

        Assert.Empty(await ReadAllLogsAsync());
    }

    [Fact]
    public async Task FlushPendingRestoreAuditAsync_CurrentDbNumberMatches_WritesLogAndRemovesFromMarker()
    {
        RestoreAuditMarker.Write(_dataDir, AuditEventCode.RestoreExecuted, [1, 2, 3]);
        var service = MakeService();

        await service.FlushPendingRestoreAuditAsync(default, currentDbNumber: 2, _dataDir, TestContext.Current.CancellationToken);

        var logs = await ReadAllLogsAsync();
        var log = Assert.Single(logs);
        Assert.Equal((int)AuditEventCode.RestoreExecuted, log.EventCode);
        Assert.Null(log.Payload);

        // 2 is removed from the target set, but 1 and 3 remain in the marker
        var remaining = RestoreAuditMarker.TryRead(_dataDir);
        Assert.NotNull(remaining);
        Assert.Equal([1, 3], remaining.Value.DbNumbers);
    }

    [Fact]
    public async Task FlushPendingRestoreAuditAsync_CurrentDbNumberDoesNotMatch_DoesNotWriteLogAndKeepsMarker()
    {
        RestoreAuditMarker.Write(_dataDir, AuditEventCode.RestoreExecuted, [2, 3]);
        var service = MakeService();

        // Vault 1 is not a target of this restore → not recorded
        await service.FlushPendingRestoreAuditAsync(default, currentDbNumber: 1, _dataDir, TestContext.Current.CancellationToken);

        Assert.Empty(await ReadAllLogsAsync());
        var remaining = RestoreAuditMarker.TryRead(_dataDir);
        Assert.NotNull(remaining);
        Assert.Equal([2, 3], remaining.Value.DbNumbers);
    }

    [Fact]
    public async Task FlushPendingRestoreAuditAsync_LastDbNumberFlushed_DeletesMarkerFile()
    {
        RestoreAuditMarker.Write(_dataDir, AuditEventCode.RestoreExecuted, [5]);
        var service = MakeService();

        await service.FlushPendingRestoreAuditAsync(default, currentDbNumber: 5, _dataDir, TestContext.Current.CancellationToken);

        Assert.Null(RestoreAuditMarker.TryRead(_dataDir));
        Assert.Single(await ReadAllLogsAsync());
    }

    [Fact]
    public async Task FlushPendingRestoreAuditAsync_CreatedAtUsesOriginalMarkerTimestamp_NotFlushTime()
    {
        RestoreAuditMarker.Write(_dataDir, AuditEventCode.RestoreExecuted, [1]);
        var originalEntry = RestoreAuditMarker.TryRead(_dataDir)!.Value;
        var service = MakeService();

        await service.FlushPendingRestoreAuditAsync(default, currentDbNumber: 1, _dataDir, TestContext.Current.CancellationToken);

        var log = Assert.Single(await ReadAllLogsAsync());
        Assert.Equal(originalEntry.TimestampUnix, log.CreatedAt);
    }
}
