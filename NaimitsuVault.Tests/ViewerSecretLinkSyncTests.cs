// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression coverage for the reported bug: unlinking a file's owning secret from
/// ViewerWindow's right pane ("pin" toggle) must be reflected in SecretsViewModel.EditingSecret
/// when that secret happens to be open for editing at the same time. Exercises the real
/// FileLinkChangedMessage / StorageChangedMessage wiring end-to-end rather than the private
/// methods directly, since the bug was specifically in that wiring being missing/stale.
/// </summary>
[Collection("SequentialMessenger")]
public sealed class ViewerSecretLinkSyncTests
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

    private static readonly ICryptoService IdentityCrypto = new IdentityCryptoService();

    private static byte[] EncodeIdentity(string plaintext)
    {
        var utf8 = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[28 + utf8.Length];
        utf8.CopyTo(blob.AsSpan(28));
        return blob;
    }

    [Fact]
    public async Task UnlinkingInViewer_RefreshesAttachedFilesAndDraftFlag_OnOpenSecret()
    {
        using var db = TestDb.Create();
        var session = new AppSession();
        session.SetKey(new byte[32]);

        var secrets     = new SecretRepository(db.Factory);
        var storedFiles = new StoredFileRepository(db.Factory, IdentityCrypto, NullLogger<StoredFileRepository>.Instance);
        var history     = new SecretHistoryRepository(db.Factory);
        var drafts      = new SecretDraftsRepository(db.Factory, IdentityCrypto);
        var profile     = new ProfileService(db.Factory, IdentityCrypto, session, NullLogger<ProfileService>.Instance);
        var favicon     = new FaviconService(db.Factory, IdentityCrypto);
        var notif       = new NullNotificationService();
        var dialog      = new NullDialogService();
        var guard       = new SessionLockGuard();
        var registry    = new SessionTaskRegistry();
        var disp        = new ImmediateDispatcherService();
        var evaluator   = new StubPasswordEvaluationService();
        var auditLog    = new StubAuditLogService();

        // Arrange: a secret with a file already attached and confirmed (Gen0), as if
        // "attach file + Save" had already happened.
        int secretId, fileId;
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var secret = new Secret
            {
                Title    = EncodeIdentity("Test Entry"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.Secrets.Add(secret);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            secretId = secret.Id;

            var file = new StoredFile
            {
                FileName       = EncodeIdentity("photo.png"),
                ContentTypeCode    = NaimitsuVault.Common.FileTypeCode.ImagePng,
                FileSize       = 123,
                FileHash       = new byte[32],
                FileModifiedAt = DateTime.UtcNow,
                CreatedAt       = DateTime.UtcNow,
                UpdatedAt       = DateTime.UtcNow,
            };
            ctx.StoredFiles.Add(file);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            fileId = file.Id;
        }
        await secrets.UpdateFileLinksAsync(secretId, [fileId]);

        var secretsVm = new SecretsViewModel(
            secrets, storedFiles, IdentityCrypto, session, profile,
            notif, dialog, favicon, history, drafts, guard, registry, disp, evaluator, auditLog,
            new TestableAutoBackupService("unused", "unused"), NullLogger<SecretsViewModel>.Instance);
        using var _svmScope = secretsVm;
        secretsVm.Resume(); // as if SecretsPage is the currently-navigated page

        var viewerVm = new ViewerViewModel(
            storedFiles, secrets, drafts, IdentityCrypto, session, profile,
            notif, new StubAuditLogService(), NullLogger<ViewerViewModel>.Instance,
            favicon, new AvatarService(db.Factory, IdentityCrypto, session), new ImmediateDispatcherService());
        using var _vvmScope = viewerVm;
        viewerVm.Resume();

        // Act 1: the secret is open for editing (as if the user has it selected in the list)
        await secretsVm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
        Assert.NotNull(secretsVm.EditingSecret);
        Assert.Contains(secretsVm.EditingSecret!.AttachedFiles, f => f.Id == fileId);

        // Act 2: open the file in the viewer, then unlink its owning secret from the right pane
        await viewerVm.LoadFileAsync(fileId, TestContext.Current.CancellationToken);
        var linkItem = Assert.Single(viewerVm.LinkFilteredItems, i => i.Id == secretId);
        Assert.True(linkItem.IsLinked, "Precondition: the secret should show as linked before toggling.");

        await viewerVm.ToggleLinkCommand.ExecuteAsync(linkItem);
        Assert.False(linkItem.IsLinked, "The viewer's own row should flip to unlinked immediately.");

        // Assert: SecretsViewModel picks up the removal via FileLinkChangedMessage (attached file
        // list) and StorageChangedMessage (draft/pencil flag). Both handlers dispatch their refresh
        // fire-and-forget, so await the tracked tasks directly instead of polling.
        await secretsVm.WaitForPendingLinkRefreshesAsync();

        Assert.DoesNotContain(secretsVm.EditingSecret!.AttachedFiles, f => f.Id == fileId);
        Assert.True(secretsVm.EditingSecretHasDraft, "The pencil (draft) indicator should turn on for the open secret.");

        // Act 3: close and reopen the viewer (fresh ViewerViewModel, fresh LoadLinksAsync from DB) -
        // the draft write from the toggle must survive independently of in-memory VM state.
        using var viewerVm2 = new ViewerViewModel(
            storedFiles, secrets, drafts, IdentityCrypto, session, profile,
            notif, new StubAuditLogService(), NullLogger<ViewerViewModel>.Instance,
            favicon, new AvatarService(db.Factory, IdentityCrypto, session), new ImmediateDispatcherService());
        viewerVm2.Resume();
        await viewerVm2.LoadFileAsync(fileId, TestContext.Current.CancellationToken);
        var linkItemReopened = Assert.Single(viewerVm2.LinkFilteredItems, i => i.Id == secretId);
        Assert.False(linkItemReopened.IsLinked,
            "Reopening the viewer must read back the persisted draft removal, not the stale Gen0 link.");
    }

    [Fact]
    public async Task UnlinkingInViewer_ThenSwitchingSecrets_SurvivesAutoSaveOnNavigate()
    {
        using var db = TestDb.Create();
        var session = new AppSession();
        session.SetKey(new byte[32]);

        var secrets     = new SecretRepository(db.Factory);
        var storedFiles = new StoredFileRepository(db.Factory, IdentityCrypto, NullLogger<StoredFileRepository>.Instance);
        var history     = new SecretHistoryRepository(db.Factory);
        var drafts      = new SecretDraftsRepository(db.Factory, IdentityCrypto);
        var profile     = new ProfileService(db.Factory, IdentityCrypto, session, NullLogger<ProfileService>.Instance);
        var favicon     = new FaviconService(db.Factory, IdentityCrypto);
        var notif       = new NullNotificationService();
        var dialog      = new NullDialogService();
        var guard       = new SessionLockGuard();
        var registry    = new SessionTaskRegistry();
        var disp        = new ImmediateDispatcherService();
        var evaluator   = new StubPasswordEvaluationService();
        var auditLog    = new StubAuditLogService();

        int secretId, otherSecretId, fileId;
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var secret = new Secret
            {
                Title    = EncodeIdentity("Test Entry"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.Secrets.Add(secret);
            var other = new Secret
            {
                Title    = EncodeIdentity("Other Entry"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.Secrets.Add(other);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            secretId      = secret.Id;
            otherSecretId = other.Id;

            var file = new StoredFile
            {
                FileName       = EncodeIdentity("photo.png"),
                ContentTypeCode    = NaimitsuVault.Common.FileTypeCode.ImagePng,
                FileSize       = 123,
                FileHash       = new byte[32],
                FileModifiedAt = DateTime.UtcNow,
                CreatedAt       = DateTime.UtcNow,
                UpdatedAt       = DateTime.UtcNow,
            };
            ctx.StoredFiles.Add(file);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            fileId = file.Id;
        }
        await secrets.UpdateFileLinksAsync(secretId, [fileId]);

        var secretsVm = new SecretsViewModel(
            secrets, storedFiles, IdentityCrypto, session, profile,
            notif, dialog, favicon, history, drafts, guard, registry, disp, evaluator, auditLog,
            new TestableAutoBackupService("unused", "unused"), NullLogger<SecretsViewModel>.Instance);
        using var _svmScope = secretsVm;
        secretsVm.Resume();

        var viewerVm = new ViewerViewModel(
            storedFiles, secrets, drafts, IdentityCrypto, session, profile,
            notif, new StubAuditLogService(), NullLogger<ViewerViewModel>.Instance,
            favicon, new AvatarService(db.Factory, IdentityCrypto, session), new ImmediateDispatcherService());
        using var _vvmScope = viewerVm;
        viewerVm.Resume();

        await secretsVm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
        await viewerVm.LoadFileAsync(fileId, TestContext.Current.CancellationToken);
        var linkItem = Assert.Single(viewerVm.LinkFilteredItems, i => i.Id == secretId);

        await viewerVm.ToggleLinkCommand.ExecuteAsync(linkItem);
        Assert.False(linkItem.IsLinked);

        // Wait for FileLinkChangedMessage's fire-and-forget refresh to land on EditingSecret
        await secretsVm.WaitForPendingLinkRefreshesAsync();
        Assert.DoesNotContain(secretsVm.EditingSecret!.AttachedFiles, f => f.Id == fileId);

        // Reproduces the real user flow: immediately switch to a different secret in the list.
        // SecretsPage calls AutoSaveOnNavigateAsync() for the secret being navigated away from
        // before switching EditingSecret - verify this doesn't silently discard the toggle's draft.
        await secretsVm.AutoSaveOnNavigateAsync();
        await secretsVm.LoadSecretAsync(otherSecretId, TestContext.Current.CancellationToken);

        using var viewerVm2 = new ViewerViewModel(
            storedFiles, secrets, drafts, IdentityCrypto, session, profile,
            notif, new StubAuditLogService(), NullLogger<ViewerViewModel>.Instance,
            favicon, new AvatarService(db.Factory, IdentityCrypto, session), new ImmediateDispatcherService());
        viewerVm2.Resume();
        await viewerVm2.LoadFileAsync(fileId, TestContext.Current.CancellationToken);
        var linkItemReopened = Assert.Single(viewerVm2.LinkFilteredItems, i => i.Id == secretId);
        Assert.False(linkItemReopened.IsLinked,
            "AutoSaveOnNavigateAsync must not silently discard the draft removal written by the viewer toggle.");
    }

    // Reproduces the confirmed root cause from production logs: the user unlinks a file from the
    // viewer while the Secrets page is NOT the active tab (e.g. they navigated to Gallery first,
    // or simply have another window focused). EditingSecret is still loaded and still auto-save
    // eligible even though the page is inactive. If FileLinkChangedMessage's handler skips the
    // refresh while inactive, EditingSecret.AttachedFiles goes stale (still shows the removed
    // file), and the next AutoSaveOnNavigateAsync compares that stale snapshot against Gen0, finds
    // no difference, and silently discards the draft removal the viewer toggle just wrote.
    [Fact]
    public async Task UnlinkingInViewer_WhileSecretsPageInactive_StillSyncsAndSurvivesAutoSave()
    {
        using var db = TestDb.Create();
        var session = new AppSession();
        session.SetKey(new byte[32]);

        var secrets     = new SecretRepository(db.Factory);
        var storedFiles = new StoredFileRepository(db.Factory, IdentityCrypto, NullLogger<StoredFileRepository>.Instance);
        var history     = new SecretHistoryRepository(db.Factory);
        var drafts      = new SecretDraftsRepository(db.Factory, IdentityCrypto);
        var profile     = new ProfileService(db.Factory, IdentityCrypto, session, NullLogger<ProfileService>.Instance);
        var favicon     = new FaviconService(db.Factory, IdentityCrypto);
        var notif       = new NullNotificationService();
        var dialog      = new NullDialogService();
        var guard       = new SessionLockGuard();
        var registry    = new SessionTaskRegistry();
        var disp        = new ImmediateDispatcherService();
        var evaluator   = new StubPasswordEvaluationService();
        var auditLog    = new StubAuditLogService();

        int secretId, fileId;
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var secret = new Secret
            {
                Title    = EncodeIdentity("Test Entry"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.Secrets.Add(secret);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            secretId = secret.Id;

            var file = new StoredFile
            {
                FileName       = EncodeIdentity("photo.png"),
                ContentTypeCode    = NaimitsuVault.Common.FileTypeCode.ImagePng,
                FileSize       = 123,
                FileHash       = new byte[32],
                FileModifiedAt = DateTime.UtcNow,
                CreatedAt       = DateTime.UtcNow,
                UpdatedAt       = DateTime.UtcNow,
            };
            ctx.StoredFiles.Add(file);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            fileId = file.Id;
        }
        await secrets.UpdateFileLinksAsync(secretId, [fileId]);

        var secretsVm = new SecretsViewModel(
            secrets, storedFiles, IdentityCrypto, session, profile,
            notif, dialog, favicon, history, drafts, guard, registry, disp, evaluator, auditLog,
            new TestableAutoBackupService("unused", "unused"), NullLogger<SecretsViewModel>.Instance);
        using var _svmScope = secretsVm;
        secretsVm.Resume();
        await secretsVm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);

        // The Secrets page is no longer the visible tab (e.g. user navigated to Gallery), but
        // EditingSecret is still loaded in memory and still subject to auto-save.
        secretsVm.Pause();

        var viewerVm = new ViewerViewModel(
            storedFiles, secrets, drafts, IdentityCrypto, session, profile,
            notif, new StubAuditLogService(), NullLogger<ViewerViewModel>.Instance,
            favicon, new AvatarService(db.Factory, IdentityCrypto, session), new ImmediateDispatcherService());
        using var _vvmScope = viewerVm;
        viewerVm.Resume();
        await viewerVm.LoadFileAsync(fileId, TestContext.Current.CancellationToken);
        var linkItem = Assert.Single(viewerVm.LinkFilteredItems, i => i.Id == secretId);

        await viewerVm.ToggleLinkCommand.ExecuteAsync(linkItem);
        Assert.False(linkItem.IsLinked);

        // EditingSecret.AttachedFiles must be refreshed even though the Secrets page is inactive.
        await secretsVm.WaitForPendingLinkRefreshesAsync();
        Assert.DoesNotContain(secretsVm.EditingSecret!.AttachedFiles, f => f.Id == fileId);

        // A later auto-save (e.g. triggered while still on Gallery, or upon returning) must not
        // discard the draft change because EditingSecret.AttachedFiles is now correctly in sync.
        await secretsVm.AutoSaveOnNavigateAsync();

        using var viewerVm2 = new ViewerViewModel(
            storedFiles, secrets, drafts, IdentityCrypto, session, profile,
            notif, new StubAuditLogService(), NullLogger<ViewerViewModel>.Instance,
            favicon, new AvatarService(db.Factory, IdentityCrypto, session), new ImmediateDispatcherService());
        viewerVm2.Resume();
        await viewerVm2.LoadFileAsync(fileId, TestContext.Current.CancellationToken);
        var linkItemReopened = Assert.Single(viewerVm2.LinkFilteredItems, i => i.Id == secretId);
        Assert.False(linkItemReopened.IsLinked,
            "The pin must stay unlinked even when the toggle happened while the Secrets page was inactive.");
    }
}
