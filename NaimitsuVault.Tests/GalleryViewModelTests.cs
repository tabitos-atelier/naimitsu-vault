// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Common;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;
using FC = NaimitsuVault.Common.FileTypeCode;

namespace NaimitsuVault.Tests;

/// <summary>
/// GalleryViewModel.LoadAsync tests covering the throttled ContentTypeCode repair pass and its
/// per-file audit log trail (TC-GAL-01..05, AuditEventCode.FileContentTypeRepaired). Complements
/// StoredFileRepositoryBoundaryTests' TC-SFR-19..21, which cover RepairContentTypeMismatchesAsync
/// itself in isolation.
/// </summary>
[Collection("SequentialMessenger")]
public sealed class GalleryViewModelTests
{
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

    private sealed class NullFilePicker : IFilePickerService
    {
        public Task<SecureCharBuffer?> SaveAsync(string _, IReadOnlyList<(string, string)> __) => Task.FromResult<SecureCharBuffer?>(null);
        public Task<SecureCharBuffer?> OpenAsync(IReadOnlyList<(string, string)> _) => Task.FromResult<SecureCharBuffer?>(null);
        public Task<IReadOnlyList<SecureCharBuffer>> OpenMultipleAsync(IReadOnlyList<(string, string)> _) => Task.FromResult<IReadOnlyList<SecureCharBuffer>>([]);
        public Task<SecureCharBuffer?> OpenFolderAsync() => Task.FromResult<SecureCharBuffer?>(null);
    }

    private sealed class NullWindowService : IWindowService
    {
        public void OpenViewer(int _) { }
        public void CloseViewer(int _) { }
        public void CloseAllViewers() { }
        public void RegisterActiveDialogCloser(Func<Task> _) { }
        public void UnregisterActiveDialogCloser(Func<Task> _) { }
        public Task CloseActiveDialogsAsync() => Task.CompletedTask;
    }

    private static readonly ICryptoService NullCrypto = new IdentityCryptoService();

    private static (TestDb db, AppSession session, GalleryViewModel vm, StubAuditLogService auditLog) BuildVm()
    {
        var db       = TestDb.Create();
        var session  = new AppSession();
        var files    = new StoredFileRepository(db.Factory, NullCrypto, NullLogger<StoredFileRepository>.Instance);
        var secrets  = new SecretRepository(db.Factory);
        var drafts   = new SecretDraftsRepository(db.Factory, NullCrypto);
        var auditLog = new StubAuditLogService();

        var vm = new GalleryViewModel(
            files, secrets, drafts, NullCrypto, session,
            new NullNotificationService(), new NullDialogService(), new NullFilePicker(),
            new NullWindowService(), auditLog, NullLogger<GalleryViewModel>.Instance,
            new ProfileService(db.Factory, NullCrypto, session, NullLogger<ProfileService>.Instance),
            new FaviconService(db.Factory, NullCrypto), new AvatarService(db.Factory, NullCrypto, session),
            new ImmediateDispatcherService(), new TestableAutoBackupService("unused", "unused"));

        return (db, session, vm, auditLog);
    }

    // IdentityCrypto: Encrypt(UTF8(name)) = [28 zero bytes] + UTF8(name)
    private static byte[] EncodeIdentityFileName(string name)
    {
        var raw = Encoding.UTF8.GetBytes(name);
        var enc = new byte[28 + raw.Length];
        raw.CopyTo(enc.AsSpan(28));
        return enc;
    }

    // ── TC-GAL-01 ─────────────────────────────────────────────────────────────
    // A drifted row (.txt stored under the pre-fix OctetStream code) gets repaired on the first
    // LoadAsync, and the repair is recorded as its own AuditEventCode.FileContentTypeRepaired entry
    // carrying the file's Id/Name/old/new ContentTypeCode - not an unverifiable aggregate count.

    [Fact]
    public async Task LoadAsync_DriftedRow_RepairsAndRecordsPerFileAuditLog()
    {
        var (db, session, vm, auditLog) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            int id;

            await using (var ctx = db.Factory.CreateDbContext())
            {
                var file = new StoredFile
                {
                    FileName       = EncodeIdentityFileName("notes.txt"),
                    ContentTypeCode    = FC.OctetStream,   // simulates a pre-fix row
                    FileSize       = 0,
                    FileHash       = new byte[32],
                    FileModifiedAt = DateTime.UnixEpoch,
                };
                ctx.StoredFiles.Add(file);
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                id = file.Id;
            }

            await vm.LoadAsync();

            Assert.Equal(1, auditLog.FileContentTypeRepairedCount);
            Assert.Equal(0, auditLog.AutoRecoveredCount);   // distinct event codes from the DB-shadow-recovery case
            var payload = Assert.IsType<FileContentTypeRepairedPayload>(auditLog.LastPayload);
            Assert.Equal(id, payload.TargetId);
            Assert.Equal("notes.txt", payload.Name);
            Assert.Equal(FC.OctetStream, payload.OldContentType);
            Assert.Equal(FC.PlainText, payload.NewContentType);

            await using var verifyCtx = db.Factory.CreateDbContext();
            var row = await verifyCtx.StoredFiles.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(FC.PlainText, row.ContentTypeCode);
        }
    }

    // ── TC-GAL-02 ─────────────────────────────────────────────────────────────
    // No drift anywhere -> repair corrects nothing -> no audit log entry is written.

    [Fact]
    public async Task LoadAsync_NoDrift_WritesNoAuditLog()
    {
        var (db, session, vm, auditLog) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);

            await using (var ctx = db.Factory.CreateDbContext())
            {
                ctx.StoredFiles.Add(new StoredFile
                {
                    FileName       = EncodeIdentityFileName("photo.png"),
                    ContentTypeCode    = FC.ImagePng,
                    FileSize       = 0,
                    FileHash       = new byte[32],
                    FileModifiedAt = DateTime.UnixEpoch,
                });
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await vm.LoadAsync();

            Assert.Equal(0, auditLog.FileContentTypeRepairedCount);
            Assert.Equal(0, auditLog.TotalLogCount);
            Assert.Single(vm.FileItems);
        }
    }

    // ── TC-GAL-04 ─────────────────────────────────────────────────────────────
    // Multiple drifted rows in the same repair pass each get their own audit log entry.

    [Fact]
    public async Task LoadAsync_MultipleDriftedRows_RecordsOneAuditLogEntryPerFile()
    {
        var (db, session, vm, auditLog) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);

            var hash1 = new byte[32]; hash1[0] = 0x01;
            var hash2 = new byte[32]; hash2[0] = 0x02;
            await using (var ctx = db.Factory.CreateDbContext())
            {
                ctx.StoredFiles.Add(new StoredFile
                {
                    FileName = EncodeIdentityFileName("notes.txt"), ContentTypeCode = FC.OctetStream,
                    FileHash = hash1, FileModifiedAt = DateTime.UnixEpoch,
                });
                ctx.StoredFiles.Add(new StoredFile
                {
                    FileName = EncodeIdentityFileName("readme.md"), ContentTypeCode = FC.OctetStream,
                    FileHash = hash2, FileModifiedAt = DateTime.UnixEpoch,
                });
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await vm.LoadAsync();

            Assert.Equal(2, auditLog.FileContentTypeRepairedCount);
            var names = auditLog.Calls
                .Where(c => c.Code == AuditEventCode.FileContentTypeRepaired)
                .Select(c => ((FileContentTypeRepairedPayload)c.Payload!).Name)
                .ToList();
            Assert.Contains("notes.txt", names);
            Assert.Contains("readme.md", names);
        }
    }

    // ── TC-GAL-03 ─────────────────────────────────────────────────────────────
    // The repair scan is throttled (AppConstants.ContentTypeRepairThrottleMinutes), not gated to
    // "once per session": rapidly revisiting Gallery (e.g. clicking around the nav pane) within the
    // throttle window must NOT re-trigger a full decrypt-and-compare scan of every StoredFile.

    [Fact]
    public async Task LoadAsync_SecondLoadWithinThrottleWindow_DoesNotRescan()
    {
        var (db, session, vm, auditLog) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            int id;
            await using (var ctx = db.Factory.CreateDbContext())
            {
                var file = new StoredFile
                {
                    FileName       = EncodeIdentityFileName("photo.png"),
                    ContentTypeCode    = FC.ImagePng,
                    FileSize       = 0,
                    FileHash       = new byte[32],
                    FileModifiedAt = DateTime.UnixEpoch,
                };
                ctx.StoredFiles.Add(file);
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                id = file.Id;
            }

            await vm.LoadAsync();   // first load: nothing to fix, item shown normally
            Assert.Single(vm.FileItems);
            Assert.Equal(0, auditLog.FileContentTypeRepairedCount);

            // Simulate an out-of-band direct DB edit (e.g. via an external SQLite browser) while the
            // app is still running, immediately after the vm's first LoadAsync this session.
            await using (var ctx = db.Factory.CreateDbContext())
            {
                var row = await ctx.StoredFiles.SingleAsync(f => f.Id == id, TestContext.Current.CancellationToken);
                row.ContentTypeCode = FC.Pdf;   // now mismatches the .png filename
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await vm.LoadAsync();   // second load, still within the throttle window -> no rescan

            Assert.Empty(vm.FileItems);            // quarantined item is excluded from the listing
            Assert.Equal(1, vm.QuarantinedCount);
            Assert.Equal(0, auditLog.FileContentTypeRepairedCount);   // throttled -> not (yet) repaired or logged

            await using var verifyCtx = db.Factory.CreateDbContext();
            var stillWrong = await verifyCtx.StoredFiles.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(FC.Pdf, stillWrong.ContentTypeCode);   // left untouched while throttled
        }
    }

    // ── TC-GAL-05 ─────────────────────────────────────────────────────────────
    // Once the throttle window has elapsed, the next Gallery visit repairs a mid-session mismatch
    // and makes it visible again, without requiring a full app relock/restart.

    [Fact]
    public async Task LoadAsync_AfterThrottleWindowElapses_RepairsMismatchWithoutRestart()
    {
        var (db, session, vm, auditLog) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            int id;
            await using (var ctx = db.Factory.CreateDbContext())
            {
                var file = new StoredFile
                {
                    FileName       = EncodeIdentityFileName("photo.png"),
                    ContentTypeCode    = FC.ImagePng,
                    FileSize       = 0,
                    FileHash       = new byte[32],
                    FileModifiedAt = DateTime.UnixEpoch,
                };
                ctx.StoredFiles.Add(file);
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                id = file.Id;
            }

            await vm.LoadAsync();   // first load: nothing to fix
            Assert.Single(vm.FileItems);

            await using (var ctx = db.Factory.CreateDbContext())
            {
                var row = await ctx.StoredFiles.SingleAsync(f => f.Id == id, TestContext.Current.CancellationToken);
                row.ContentTypeCode = FC.Pdf;   // now mismatches the .png filename
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            vm.ResetContentTypeRepairThrottleForTests();   // simulate the throttle window having elapsed
            await vm.LoadAsync();   // next visit after the window -> rescans and repairs

            Assert.Single(vm.FileItems);           // repaired -> visible in the listing again, no restart needed
            Assert.Equal(0, vm.QuarantinedCount);
            Assert.Equal(1, auditLog.FileContentTypeRepairedCount);
            var payload = Assert.IsType<FileContentTypeRepairedPayload>(auditLog.LastPayload);
            Assert.Equal(id, payload.TargetId);
            Assert.Equal(FC.Pdf, payload.OldContentType);
            Assert.Equal(FC.ImagePng, payload.NewContentType);

            await using var verifyCtx = db.Factory.CreateDbContext();
            var fixedRow = await verifyCtx.StoredFiles.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(FC.ImagePng, fixedRow.ContentTypeCode);
        }
    }
}
