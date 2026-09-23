// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests.Stubs;

/// <summary>
/// IAuditLogService spy stub. Records the number of LogAsync calls and their EventCode.
/// TC-AL-16: verifies that SecretViewedCount is exactly 1.
/// TC-AL-17: verifies that TotalLogCount is 0.
/// </summary>
internal sealed class StubAuditLogService : IAuditLogService
{
    private int _totalLogCount;
    private int _secretViewedCount;
    private int _autoRecoveredCount;
    private int _fileContentTypeRepairedCount;

    public int TotalLogCount                  => _totalLogCount;
    public int SecretViewedCount              => _secretViewedCount;
    public int AutoRecoveredCount             => _autoRecoveredCount;
    public int FileContentTypeRepairedCount   => _fileContentTypeRepairedCount;
    public AuditPayload? LastPayload          { get; private set; }

    /// <summary>Every LogAsync call in order, for tests that need to inspect more than the last one
    /// (e.g. one audit entry per repaired file).</summary>
    public List<(AuditEventCode Code, AuditPayload? Payload)> Calls { get; } = [];

    public Task LogAsync(
        AuditEventCode code,
        AuditPayload? payload,
        DekScope dek,
        CancellationToken ct = default)
    {
        _totalLogCount++;
        if (code == AuditEventCode.SecretViewed) _secretViewedCount++;
        if (code == AuditEventCode.UnifiedDbAutoRecovered || code == AuditEventCode.VaultDbAutoRecovered) _autoRecoveredCount++;
        if (code == AuditEventCode.FileContentTypeRepaired) _fileContentTypeRepairedCount++;
        LastPayload = payload;
        Calls.Add((code, payload));
        return Task.CompletedTask;
    }

    public Task LogAuthFailedAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<AuditLogDisplayItem>> GetRecentAsync(int count, DekScope dek, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditLogDisplayItem>>(Array.Empty<AuditLogDisplayItem>());

    public Task<IReadOnlyList<AuditLogDisplayItem>> GetAllAfterAsync(DateTimeOffset since, DekScope dek, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditLogDisplayItem>>(Array.Empty<AuditLogDisplayItem>());

    public Task PurgeOldLogsAsync(DekScope? purgeLogDek = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> HasAnyLogsAsync(CancellationToken ct = default)
        => Task.FromResult(_totalLogCount > 0);

    public Task FlushPreAuthFailuresAsync(int count, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task FlushPendingRestoreAuditAsync(DekScope dek, int currentDbNumber, string dataDir, CancellationToken ct = default)
        => Task.CompletedTask;
}
