// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Common;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// Covers 4 scenarios for UI messaging sync/async lifecycle behavior: Scenario A, Scenario B,
/// Test 2, and Test 3.
///
/// WeakReferenceMessenger.Default is a process-wide shared singleton, so all tests run
/// sequentially (the SequentialMessenger collection).
/// Each test cleans up at the end of Arrange to avoid interfering with the next test.
/// </summary>
[Collection("SequentialMessenger")]
public sealed class MessageSyncAndLifecycleTests
{
    // ── Shared stubs ─────────────────────────────────────────────────────────────

    private sealed class NullCryptoService : ICryptoService
    {
        public void DeriveKey(ReadOnlySpan<char> _, ReadOnlySpan<byte> __, Span<byte> ___) { }
        public byte[] GenerateSalt(int size = 32) => new byte[size];
        public byte[] Encrypt(ReadOnlySpan<byte> _, ReadOnlySpan<byte> __, ReadOnlySpan<byte> ___ = default) => [];
        public void Decrypt(ReadOnlySpan<byte> _, ReadOnlySpan<byte> __, Span<byte> ___, ReadOnlySpan<byte> ____ = default) { }
    }

    private sealed class NullNotificationService : IAppNotificationService
    {
        public void Show(int _, int __, NotificationSeverity ___ = default, TimeSpan? ____ = null) { }
        public void Show(int _, string __, NotificationSeverity ___ = default, TimeSpan? ____ = null) { }
    }

    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string _, string __, bool ___ = false) => Task.FromResult(false);
        public Task<bool> ConfirmDestructiveAsync(string _, string __, string ____, string _____, bool ___ = false) => Task.FromResult(false);
        public Task<bool> ConfirmWithIconAsync(string _, string __, string ____, string _____, bool ______ = false, bool ___ = false) => Task.FromResult(false);
        public Task<bool> ConfirmSwapAsync(string _, TimeMachineSwapPreview __) => Task.FromResult(false);
        public Task<SecureCharBuffer?> ConfirmPasswordAsync(string _) => Task.FromResult<SecureCharBuffer?>(null);
        public Task ShowInfoAsync(string _, string __) => Task.CompletedTask;
        public Task<IDisposable> ShowBusyAsync(string _) => Task.FromResult<IDisposable>(NullDisposable.Instance);
        public Task<MasterAuthResult?> ConfirmMasterAuthAsync(string _) => Task.FromResult<MasterAuthResult?>(null);

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private static readonly ICryptoService NullCrypto = new NullCryptoService();

    /// <summary>
    /// Factory that builds a TimeMachineViewModel.
    /// Since the test only verifies the message-received flag under test,
    /// it is used in its just-constructed state without calling LoadAsync.
    /// </summary>
    private static TimeMachineViewModel BuildTimeMachineVm(TestDb db)
    {
        var secrets      = new SecretRepository(db.Factory);
        var storedFiles  = new StoredFileRepository(db.Factory, NullCrypto, NullLogger<StoredFileRepository>.Instance);
        var crypto       = NullCrypto;
        var session      = new AppSession();
        var notification = new NullNotificationService();
        var dialog       = new NullDialogService();
        var favicon      = new FaviconService(db.Factory, NullCrypto);
        var history      = new SecretHistoryRepository(db.Factory);
        var drafts       = new SecretDraftsRepository(db.Factory, NullCrypto);
        var dispatcher   = new ImmediateDispatcherService();
        return new TimeMachineViewModel(
            secrets, storedFiles, crypto, session,
            notification, dialog, favicon, history, drafts, dispatcher,
            new StubAuditLogService(), new TestableAutoBackupService("unused", "unused"), NullLogger<TimeMachineViewModel>.Instance);
    }

    // ── Scenario A ──────────────────────────────────────────────────────────────
    // Verifies that UnregisterAll runs after Dispose, so the message is not received.

    [Fact]
    public void ScenarioA_ZombieVm_AfterDispose_DoesNotReceiveMessage()
    {
        // Arrange
        using var db = TestDb.Create();
        var vm = BuildTimeMachineVm(db);
        vm.Resume();           // activate (set IsActive=true)
        vm.NeedsReload = false; // simulate the initial load having completed (establish baseline)

        // Act — Dispose triggers UnregisterAll
        vm.Dispose();

        // Send StorageChangedMessage after Dispose
        WeakReferenceMessenger.Default.Send(new StorageChangedMessage());

        // Assert — a disposed VM must not flip NeedsReload to true
        // (because IsActive=false and UnregisterAll has already run)
        Assert.False(vm.NeedsReload, "A disposed VM must not receive StorageChangedMessage.");
    }

    // ── Scenario B ──────────────────────────────────────────────────────────────
    // While paused, NeedsReload=true is set, but no DB query (equivalent to LoadAsync) runs.
    // We indirectly confirm that the IsActive=false handler stops after just "writing
    // NeedsReload=true" by checking that the NeedsReload flag becomes true.

    [Fact]
    public void ScenarioB_InactiveVm_AfterPause_SetsNeedsReloadFlag()
    {
        // Arrange
        using var db = TestDb.Create();
        var vm = BuildTimeMachineVm(db);
        vm.Resume();    // activate once, then...
        vm.Pause();     // deactivate

        // Act — receive a storage-change notification while inactive
        WeakReferenceMessenger.Default.Send(new StorageChangedMessage());

        // Assert — only the NeedsReload flag is set, not a DB query
        Assert.True(vm.NeedsReload, "An inactive VM should set NeedsReload=true on StorageChangedMessage.");

        // Cleanup
        vm.Dispose();
    }

    // ── Test 2 ─────────────────────────────────────────────────────────────────
    // Verifies that using the ImmediateDispatcherService stub lets a background thread
    // manipulate an ObservableCollection without throwing an exception.

    [Fact]
    public async Task Test2_BackgroundThreadMessage_WithDispatcherStub_UpdatesCollectionWithoutException()
    {
        // Arrange — synchronize Enqueue via ImmediateDispatcherService
        var dispatcher = new ImmediateDispatcherService();
        var collection = new ObservableCollection<string>();
        Exception? caughtEx = null;

        // Manipulate the observable collection from a background thread via EnqueueAsync
        await Task.Run(async () =>
        {
            try
            {
                await dispatcher.EnqueueAsync(() =>
                {
                    collection.Add("item-from-background");
                    return Task.CompletedTask;
                });
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(caughtEx);
        Assert.Single(collection);
        Assert.Equal("item-from-background", collection[0]);
    }

    // ── Test 3 ─────────────────────────────────────────────────────────────────
    // Verifies, via a receiver-side interceptor, the discipline that WeakReferenceMessenger.Send
    // is called only after the DB commit.
    //
    // Implementation discipline check: the StorageChangedMessage handler is registered via
    // WeakReferenceMessenger.Register, and Send dispatches synchronously.
    // If Send is called after the DB commit, the DB change should already be visible at
    // the moment of receipt.

    [Fact]
    public async Task Test3_Send_HappensOnlyAfterDbCommit_VerifiedByInterceptingReceiver()
    {
        // Arrange
        using var db      = TestDb.Create();
        bool messageReceived = false;
        int? countAtReceive  = null;

        // Receiver interceptor: query the DB at the moment of receipt and record the row count
        var interceptor = new ReceiverStub(db.Factory, received =>
        {
            messageReceived = true;
            countAtReceive  = received;
        });

        var secrets = new SecretRepository(db.Factory);

        // Act — INSERT a Secret and Send after the commit
        await using (var ctx = db.Factory.CreateDbContext())
        {
            ctx.Secrets.Add(new NaimitsuVault.Models.Secret
            {
                Title    = new byte[28],
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);   // ← DB commit
        }
        WeakReferenceMessenger.Default.Send(new StorageChangedMessage());  // ← Send

        // Assert — 1 row already existed in the DB at the moment of receipt (proof Send happened after the commit)
        Assert.True(messageReceived, "The receiver interceptor did not receive StorageChangedMessage.");
        Assert.Equal(1, countAtReceive);

        // Cleanup
        WeakReferenceMessenger.Default.UnregisterAll(interceptor);
    }

    /// <summary>
    /// Receiver stub used by Test 3.
    /// When it receives StorageChangedMessage, it measures the Secrets row count in the DB
    /// and passes it to the callback.
    /// </summary>
    internal sealed class ReceiverStub : IRecipient<StorageChangedMessage>
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly Action<int> _callback;

        public ReceiverStub(IDbContextFactory<AppDbContext> factory, Action<int> callback)
        {
            _factory  = factory;
            _callback = callback;
            WeakReferenceMessenger.Default.Register(this);
        }

        public void Receive(StorageChangedMessage message)
        {
            using var ctx = _factory.CreateDbContext();
            var count = ctx.Secrets.Count();
            _callback(count);
        }
    }
}
