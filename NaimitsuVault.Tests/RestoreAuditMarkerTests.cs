// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression tests for the marker file (RestoreAuditMarker) used to defer audit log
/// recording of RestoreExecuted events.
/// </summary>
public sealed class RestoreAuditMarkerTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"naimitsu_ram_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private string MarkerPath => Path.Combine(_dataDir, "pending_restore_audit.flag");

    [Fact]
    public void Write_ThenTryRead_ReturnsCodeTimestampAndDbNumbers()
    {
        RestoreAuditMarker.Write(_dataDir, AuditEventCode.RestoreExecuted, [1, 2, 3]);

        var entry = RestoreAuditMarker.TryRead(_dataDir);

        Assert.NotNull(entry);
        Assert.Equal(AuditEventCode.RestoreExecuted, entry.Value.Code);
        Assert.Equal([1, 2, 3], entry.Value.DbNumbers);
        Assert.True(entry.Value.TimestampUnix > 0);
    }

    [Fact]
    public void Write_EmptyDbNumberList_DoesNotCreateFile()
    {
        RestoreAuditMarker.Write(_dataDir, AuditEventCode.RestoreExecuted, []);

        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void TryRead_NoFile_ReturnsNull()
    {
        Assert.Null(RestoreAuditMarker.TryRead(_dataDir));
    }

    [Fact]
    public void TryRead_CorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(MarkerPath, "not a valid marker");

        Assert.Null(RestoreAuditMarker.TryRead(_dataDir));
    }

    [Fact]
    public void RemoveAndRewrite_RemovesOnlyGivenDbNumber_PreservesTimestamp()
    {
        RestoreAuditMarker.Write(_dataDir, AuditEventCode.RestoreExecuted, [2, 3]);
        var original = RestoreAuditMarker.TryRead(_dataDir)!.Value;

        RestoreAuditMarker.RemoveAndRewrite(_dataDir, original, 2);

        var updated = RestoreAuditMarker.TryRead(_dataDir);
        Assert.NotNull(updated);
        Assert.Equal([3], updated.Value.DbNumbers);
        Assert.Equal(original.TimestampUnix, updated.Value.TimestampUnix);
        Assert.Equal(original.Code, updated.Value.Code);
    }

    [Fact]
    public void RemoveAndRewrite_LastDbNumberRemoved_DeletesFile()
    {
        RestoreAuditMarker.Write(_dataDir, AuditEventCode.RestoreExecuted, [1]);
        var entry = RestoreAuditMarker.TryRead(_dataDir)!.Value;

        RestoreAuditMarker.RemoveAndRewrite(_dataDir, entry, 1);

        Assert.False(File.Exists(MarkerPath));
        Assert.Null(RestoreAuditMarker.TryRead(_dataDir));
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        RestoreAuditMarker.Write(_dataDir, AuditEventCode.RestoreExecuted, [1]);
        Assert.True(File.Exists(MarkerPath));

        RestoreAuditMarker.Delete(_dataDir);

        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void Delete_NoFile_DoesNotThrow()
    {
        RestoreAuditMarker.Delete(_dataDir);
    }
}
