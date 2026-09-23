// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using CommunityToolkit.Mvvm.Messaging;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// AuditLogWrittenMessage is broadcast from AuditLogService's write paths so ShellWindow can flip
/// the AuditLog nav item enabled the moment the first entry lands (instead of only reacting to
/// secret-count changes, which misses the common "view a secret" / "copy a field" flows) and so an
/// already-open DashboardPage/AuditLogPage can refresh live. These tests verify the message actually
/// fires - once, with the right Code/EventLevel - from each of AuditLogService's write methods.
/// No TC-xxx-nn ID: this class tests message-broadcast behavior, not BuildSummary output (same
/// convention as AuditLogServiceRestoreFlushTests).
/// WeakReferenceMessenger.Default is a process-wide shared singleton (see MessageSyncAndLifecycleTests),
/// so this class runs in the same serialized "SequentialMessenger" collection to avoid cross-talk
/// with any other test sending/receiving messages concurrently.
/// </summary>
[Collection("SequentialMessenger")]
public sealed class AuditLogWrittenMessageTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"naimitsu_alwm_{Guid.NewGuid():N}");
    private readonly TestDb _db = TestDb.Create();
    private readonly AppSession _session = new();
    private readonly IdentityCryptoService _crypto = new();

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private AuditLogService MakeService()
        => new(new AuditLogRepository(_db.Factory), _crypto, _session,
            new SecretRepository(_db.Factory),
            new StoredFileRepository(_db.Factory, _crypto, Microsoft.Extensions.Logging.Abstractions.NullLogger<StoredFileRepository>.Instance));

    // Collects every AuditLogWrittenMessage sent while this recipient is registered. A fresh object
    // per test (not `this`) so WeakReferenceMessenger.Default - a process-wide singleton shared
    // across parallel test classes - never cross-talks between tests.
    private static (object recipient, List<AuditLogWrittenMessage> received) Listen()
    {
        var recipient = new object();
        var received  = new List<AuditLogWrittenMessage>();
        WeakReferenceMessenger.Default.Register<object, AuditLogWrittenMessage>(recipient, (_, msg) => received.Add(msg));
        return (recipient, received);
    }

    [Fact]
    public async Task LogAsync_SendsAuditLogWrittenMessage_WithCodeAndEventLevel()
    {
        var (recipient, received) = Listen();
        try
        {
            var service = MakeService();
            _session.SetKey(new byte[32]);

            await service.LogAsync(AuditEventCode.MasterPasswordChanged, null, _session.GetKey(), TestContext.Current.CancellationToken);

            var msg = Assert.Single(received);
            Assert.Equal(AuditEventCode.MasterPasswordChanged, msg.Code);
            Assert.Equal(2, msg.EventLevel); // MasterPasswordChanged is a level-2 (critical) event
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task LogAuthFailedAsync_SendsAuditLogWrittenMessage_WithAuthFailedCode()
    {
        var (recipient, received) = Listen();
        try
        {
            var service = MakeService();

            await service.LogAuthFailedAsync(TestContext.Current.CancellationToken);

            var msg = Assert.Single(received);
            Assert.Equal(AuditEventCode.AuthFailed, msg.Code);
            Assert.Equal(1, msg.EventLevel);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task FlushPreAuthFailuresAsync_ZeroCount_SendsNoMessage()
    {
        var (recipient, received) = Listen();
        try
        {
            var service = MakeService();

            await service.FlushPreAuthFailuresAsync(0, TestContext.Current.CancellationToken);

            Assert.Empty(received);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task FlushPreAuthFailuresAsync_PositiveCount_SendsExactlyOneMessage()
    {
        var (recipient, received) = Listen();
        try
        {
            var service = MakeService();
            _session.SetKey(new byte[32]);

            // 3 rows written, but only one notification - listeners only care that logs now exist.
            await service.FlushPreAuthFailuresAsync(3, TestContext.Current.CancellationToken);

            var msg = Assert.Single(received);
            Assert.Equal(AuditEventCode.AuthFailed, msg.Code);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task FlushPendingRestoreAuditAsync_MarkerMatches_SendsAuditLogWrittenMessage()
    {
        var (recipient, received) = Listen();
        try
        {
            RestoreAuditMarker.Write(_dataDir, AuditEventCode.RestoreExecuted, [1]);
            var service = MakeService();

            await service.FlushPendingRestoreAuditAsync(default, currentDbNumber: 1, _dataDir, TestContext.Current.CancellationToken);

            var msg = Assert.Single(received);
            Assert.Equal(AuditEventCode.RestoreExecuted, msg.Code);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task FlushPendingRestoreAuditAsync_NoMarkerFile_SendsNoMessage()
    {
        var (recipient, received) = Listen();
        try
        {
            var service = MakeService();

            await service.FlushPendingRestoreAuditAsync(default, currentDbNumber: 1, _dataDir, TestContext.Current.CancellationToken);

            Assert.Empty(received);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }
}
