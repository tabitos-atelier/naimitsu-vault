// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// A test-only subclass of AutoBackupService.
/// Injects the source DB path and failure flag path from the outside, allowing
/// RunBackup()'s I/O behavior to be verified against the real file system.
/// </summary>
internal sealed class TestableAutoBackupService : AutoBackupService, IDisposable
{
    private readonly string  _sourceDbPath;
    private readonly string  _failureFlagPath;
    private readonly string? _dataDir;
    private readonly string? _fixedTempFolderName;
    // Defaults to an isolated per-instance temp path rather than the real static PendingBackupFlagPath,
    // so tests that don't care about this flag (most ViewModel tests just need a compiling constructor
    // argument) never touch/pollute the real data/ folder next to the test executable.
    private readonly string _pendingBackupFlagPath =
        Path.Combine(Path.GetTempPath(), $"naimitsu_test_pending_backup_{Guid.NewGuid():N}.flag");

    internal TestableAutoBackupService(
        string sourceDbPath, string failureFlagPath,
        string? dataDir = null, string? fixedTempFolderName = null, string? pendingBackupFlagPath = null)
        : base(NullLogger<AutoBackupService>.Instance)
    {
        _sourceDbPath         = sourceDbPath;
        _failureFlagPath      = failureFlagPath;
        _dataDir              = dataDir;
        _fixedTempFolderName  = fixedTempFolderName;
        if (pendingBackupFlagPath != null) _pendingBackupFlagPath = pendingBackupFlagPath;
    }

    internal override string GetSourceDbPath()          => _sourceDbPath;
    internal override string GetFailureFlagPath()        => _failureFlagPath;
    internal override string GetDataDir()                => _dataDir ?? base.GetDataDir();
    internal override string NewTempFolderName()          => _fixedTempFolderName ?? base.NewTempFolderName();
    internal override string GetPendingBackupFlagPath()   => _pendingBackupFlagPath;

    // Most call sites don't hold a reference to `using`, so the finalizer is the actual cleanup
    // path for those; Dispose() just gives tests that do keep a reference a deterministic option.
    public void Dispose()
    {
        try { File.Delete(_pendingBackupFlagPath); } catch { /* best-effort test-temp cleanup */ }
        GC.SuppressFinalize(this);
    }

    ~TestableAutoBackupService()
    {
        try { File.Delete(_pendingBackupFlagPath); } catch { /* best-effort test-temp cleanup */ }
    }
}
