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
/// Regression coverage for the reported bug: unlinking a profile-attached file from ViewerWindow's
/// right pane doesn't flip to "unpinned" - the pin snaps back to "linked" almost immediately.
/// Root cause: LoadLinksAsync computed LinkIsProfileLinked as a union of TwinA (confirmed) and
/// TwinB/PendingProfileImageIds (draft), so once a TwinB draft removed the file, TwinA still having
/// it made the union true again. ToggleProfileLinkAsync's own StorageChangedMessage send triggers
/// the viewer's own StorageChangedMessage listener to re-run LoadLinksAsync, which is what made the
/// bug show up immediately after toggling (not just on a later reopen).
/// </summary>
[Collection("SequentialMessenger")]
public sealed class ViewerProfileLinkSyncTests
{
    private sealed class NullNotificationService : IAppNotificationService
    {
        public void Show(int _, int __, NotificationSeverity ___ = default, TimeSpan? ____ = null) { }
        public void Show(int _, string __, NotificationSeverity ___ = default, TimeSpan? ____ = null) { }
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
    public async Task UnlinkingProfileFileInViewer_StaysUnlinked_AcrossSelfTriggeredReloadAndReopen()
    {
        using var db = TestDb.Create();
        var session = new AppSession();
        session.SetKey(new byte[32]);

        var secrets     = new SecretRepository(db.Factory);
        var storedFiles = new StoredFileRepository(db.Factory, IdentityCrypto, NullLogger<StoredFileRepository>.Instance);
        var drafts      = new SecretDraftsRepository(db.Factory, IdentityCrypto);
        var profile     = new ProfileService(db.Factory, IdentityCrypto, session, NullLogger<ProfileService>.Instance);
        var favicon     = new FaviconService(db.Factory, IdentityCrypto);
        var notif       = new NullNotificationService();

        int fileId;
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var file = new StoredFile
            {
                FileName       = EncodeIdentity("avatar.png"),
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

        // Commit the profile with the file attached (TwinA confirmed), as if the user had already
        // saved the profile with this file attached - mirrors ProfileViewModel.SaveAsync's two writes.
        var key = session.GetKey();
        await profile.CommitProfileAsync(new ProfileEditModel { FileIds = [fileId] }, key);
        await storedFiles.UpdateProfileFileLinksAsync([fileId]);

        var viewerVm = new ViewerViewModel(
            storedFiles, secrets, drafts, IdentityCrypto, session, profile,
            notif, new StubAuditLogService(), NullLogger<ViewerViewModel>.Instance,
            favicon, new AvatarService(db.Factory, IdentityCrypto, session), new ImmediateDispatcherService());
        using var _vvmScope = viewerVm;
        viewerVm.Resume();

        await viewerVm.LoadFileAsync(fileId, TestContext.Current.CancellationToken);
        Assert.True(viewerVm.LinkIsProfileLinked, "Precondition: the profile should show as linked before toggling.");

        await viewerVm.ToggleProfileLinkCommand.ExecuteAsync(null);
        Assert.False(viewerVm.LinkIsProfileLinked, "The viewer's own pin should flip to unlinked immediately.");

        // ToggleProfileLinkAsync sends StorageChangedMessage, which this same (still-active) viewer
        // also listens to and reacts to by re-running LoadLinksAsync - fire-and-forget from the
        // handler's point of view, but WaitForPendingSelfReloadAsync exposes the started Task
        // directly so the test can await it deterministically instead of polling. This is exactly
        // the reload that snapped the pin back to "linked" before the fix.
        await viewerVm.WaitForPendingSelfReloadAsync();

        Assert.False(viewerVm.LinkIsProfileLinked,
            "The pin must stay unlinked even after the viewer's own StorageChangedMessage-triggered reload.");

        // Reopen (fresh ViewerViewModel, fresh LoadLinksAsync from DB) - the TwinB removal must
        // survive independently of in-memory VM state.
        using var viewerVm2 = new ViewerViewModel(
            storedFiles, secrets, drafts, IdentityCrypto, session, profile,
            notif, new StubAuditLogService(), NullLogger<ViewerViewModel>.Instance,
            favicon, new AvatarService(db.Factory, IdentityCrypto, session), new ImmediateDispatcherService());
        viewerVm2.Resume();
        await viewerVm2.LoadFileAsync(fileId, TestContext.Current.CancellationToken);
        Assert.False(viewerVm2.LinkIsProfileLinked,
            "Reopening the viewer must read back the persisted TwinB removal, not the stale TwinA link.");
    }
}
