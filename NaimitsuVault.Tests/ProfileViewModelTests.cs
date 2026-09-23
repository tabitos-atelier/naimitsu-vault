// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// ProfileViewModel draft-entry trigger tests.
/// TC-CFD-01: adding a custom field (the "+" button) alone must not enter draft mode - the new
///            field is still an empty placeholder indistinguishable from "nothing changed".
/// TC-CFD-02: editing the newly-added field's Value does enter draft mode.
/// TC-CFD-03: editing the newly-added field's Label alone (value left blank) does enter draft mode -
///            the regression this suite guards against (label-only edits were previously
///            indistinguishable from an untouched placeholder).
/// TC-CFD-04: removing an existing (already-loaded) custom field enters draft mode immediately,
///            proving the Add-only skip in the CollectionChanged handler doesn't also swallow Remove.
/// TC-CFD-05: reordering custom fields (Move) while the model is fully clean commits the new
///            order directly to TwinA and never enters draft mode.
/// TC-CFD-06: reordering while an unsaved draft edit is in progress does not touch TwinA at all.
/// TC-CFD-07: reordering a collection that still contains an unedited placeholder field (added via
///            the "+" button but never edited) does not touch TwinA either.
/// TC-CFD-08: same as TC-CFD-05 but reproduces WinUI 3's actual ListView.CanReorderItems mechanics
///            (Remove then Insert, never ObservableCollection.Move) - regression coverage for a
///            real bug where the bare Remove half alone was treated as a genuine deletion.
/// TC-CFD-09: HasUnsavedChanges (the flag ProfilePage.xaml.cs's blur handlers gate on before
///            calling AutoSaveDraftAsync) stays false for the same unedited placeholder as
///            TC-CFD-01.
/// TC-CFD-10: HasUnsavedChanges flips true on an edit to an unrelated field (Name), proving
///            TC-CFD-09 isn't just permanently false.
/// TC-CFD-11: AutoSaveDraftAsync called after the session lock barricade completes normally
///            (no exception) and writes nothing, so a fire-and-forget caller never produces an
///            unobserved OperationCanceledException.
/// TC-PLC-01: a paused (IsActive = false) ProfileViewModel ignores StorageChangedMessage - no DB read
///            and no ProfileFiles reconciliation while another page is in front.
/// TC-PLC-02: after Resume() the same message is processed again (positive control for TC-PLC-01,
///            proving the guard is what suppressed it and not a broken setup).
/// TC-PLC-03: a Dispose()d ProfileViewModel no longer receives StorageChangedMessage at all
///            (second line of defense: UnregisterAll).
/// </summary>
[Collection("SequentialMessenger")]
public sealed class ProfileViewModelTests
{
    private static readonly ICryptoService NullCrypto = new IdentityCryptoService();

    private static (TestDb db, AppSession session, ProfileViewModel vm) BuildVm()
        => BuildVm(new SessionLockGuard());

    private static (TestDb db, AppSession session, ProfileViewModel vm) BuildVm(SessionLockGuard guard)
    {
        var db      = TestDb.Create();
        var session = new AppSession();
        var files   = new StoredFileRepository(db.Factory, NullCrypto, NullLogger<StoredFileRepository>.Instance);
        var profile = new ProfileService(db.Factory, NullCrypto, session, NullLogger<ProfileService>.Instance);
        var avatar  = new AvatarService(db.Factory, NullCrypto, session);
        var registry = new SessionTaskRegistry();

        var vm = new ProfileViewModel(
            profile, new NullNotificationService(), files, NullCrypto, session,
            new NullWindowService(), avatar, new StubAuditLogService(),
            NullLogger<ProfileViewModel>.Instance, guard, registry,
            new TestableAutoBackupService("unused", "unused"));

        return (db, session, vm);
    }

    private sealed class NullNotificationService : IAppNotificationService
    {
        public void Show(int _, int __, NotificationSeverity ___ = default, TimeSpan? ____ = null) { }
        public void Show(int _, string __, NotificationSeverity ___ = default, TimeSpan? ____ = null) { }
    }

    // ── TC-CFD-01 ─────────────────────────────────────────────────────────────
    // AddCustomField alone (no value/label edit) must not enter draft mode

    [Fact]
    public async Task TC_CFD_01_AddCustomField_Alone_DoesNotEnterDraftMode()
    {
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            vm.AddCustomFieldCommand.Execute("Text");
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            Assert.False(vm.HasDraft);
        }
    }

    // ── TC-CFD-02 ─────────────────────────────────────────────────────────────
    // Editing the newly-added field's Value does enter draft mode

    [Fact]
    public async Task TC_CFD_02_AddCustomField_ThenEditValue_EntersDraftMode()
    {
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            vm.AddCustomFieldCommand.Execute("Text");
            vm.CustomFields[0].Value = "some value";
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            Assert.True(vm.HasDraft);
        }
    }

    // ── TC-CFD-03 ─────────────────────────────────────────────────────────────
    // Editing the newly-added field's Label alone (value left blank) does enter draft mode

    [Fact]
    public async Task TC_CFD_03_AddCustomField_ThenEditLabelOnly_EntersDraftMode()
    {
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            vm.AddCustomFieldCommand.Execute("Text");
            vm.CustomFields[0].Label = "My Custom Label";
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            Assert.True(vm.HasDraft);
        }
    }

    // ── TC-CFD-04 ─────────────────────────────────────────────────────────────
    // Removing an existing custom field via RemoveCustomFieldCommand enters draft mode immediately
    // (Add-only skip doesn't swallow Remove). Must go through the command (not a raw collection
    // RemoveAt) since only the command flags _explicitCustomFieldRemove - an un-flagged Remove is
    // treated as the first half of a WinUI 3 ListView drag-and-drop reorder (see TC-CFD-05/06/07).

    [Fact]
    public async Task TC_CFD_04_RemoveExistingCustomField_EntersDraftModeImmediately()
    {
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            vm.AddCustomFieldCommand.Execute("Text");
            vm.CustomFields[0].Value = "some value";
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;
            Assert.True(vm.HasDraft); // sanity check: draft exists from the newly-edited field

            // Commit to TwinA so the field becomes part of the confirmed profile, not just a draft
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.False(vm.HasDraft);
            Assert.Single(vm.CustomFields);

            vm.RemoveCustomFieldCommand.Execute(vm.CustomFields[0]);
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            Assert.True(vm.HasDraft);
        }
    }

    private static async Task<int> AddAndCommitFieldAsync(ProfileViewModel vm, string value)
    {
        int index = vm.CustomFields.Count;
        vm.AddCustomFieldCommand.Execute("Text");
        vm.CustomFields[index].Value = value;
        if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;
        await vm.SaveCommand.ExecuteAsync(null);
        return index;
    }

    // ── TC-CFD-05 ─────────────────────────────────────────────────────────────
    // Reordering custom fields via Move, while the model is fully clean, commits directly to
    // TwinA and never enters draft mode

    [Fact]
    public async Task TC_CFD_05_ReorderCustomFields_WhenClean_CommitsDirectlyToTwinA()
    {
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            await AddAndCommitFieldAsync(vm, "a");
            await AddAndCommitFieldAsync(vm, "b");
            Assert.False(vm.HasDraft);
            Assert.Equal(2, vm.CustomFields.Count);

            vm.CustomFields.Move(0, 1); // [a, b] -> [b, a]
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            Assert.False(vm.HasDraft);

            var profile = new ProfileService(db.Factory, NullCrypto, session, NullLogger<ProfileService>.Instance);
            using var twinA = await profile.GetTwinASnapshotAsync(session.GetKey());
            var twinAModel = profile.BuildEditModelFromSnapshot(twinA!);
            try
            {
                Assert.Equal(["b", "a"], twinAModel.CustomFields.Select(f => f.Value));
            }
            finally
            {
                twinAModel.ZeroPii();
            }
        }
    }

    // ── TC-CFD-06 ─────────────────────────────────────────────────────────────
    // Reordering while an unsaved draft edit is in progress must not touch TwinA

    [Fact]
    public async Task TC_CFD_06_ReorderCustomFields_WhileDraftExists_DoesNotCommit()
    {
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            await AddAndCommitFieldAsync(vm, "a");
            await AddAndCommitFieldAsync(vm, "b");

            // Dirty the model via a custom-field value edit (a ViewModel-only draft trigger,
            // unlike the main fields which rely on the page's focus-out handler)
            vm.CustomFields[0].Value = "a-edited";
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;
            Assert.True(vm.HasDraft);

            vm.CustomFields.Move(0, 1);
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            var profile = new ProfileService(db.Factory, NullCrypto, session, NullLogger<ProfileService>.Instance);
            using var twinA = await profile.GetTwinASnapshotAsync(session.GetKey());
            var twinAModel = profile.BuildEditModelFromSnapshot(twinA!);
            try
            {
                Assert.Equal(["a", "b"], twinAModel.CustomFields.Select(f => f.Value)); // unchanged
            }
            finally
            {
                twinAModel.ZeroPii();
            }
        }
    }

    // ── TC-CFD-07 ─────────────────────────────────────────────────────────────
    // Reordering a collection that still carries an unedited "+"-button placeholder field must
    // not touch TwinA either

    [Fact]
    public async Task TC_CFD_07_ReorderCustomFields_WithUneditedPlaceholder_DoesNotCommit()
    {
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            await AddAndCommitFieldAsync(vm, "a");
            await AddAndCommitFieldAsync(vm, "b");

            vm.AddCustomFieldCommand.Execute("Text"); // unedited placeholder, FieldId not in TwinA
            Assert.False(vm.HasDraft); // sanity check (TC-CFD-01)

            vm.CustomFields.Move(0, 1);
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            var profile = new ProfileService(db.Factory, NullCrypto, session, NullLogger<ProfileService>.Instance);
            using var twinA = await profile.GetTwinASnapshotAsync(session.GetKey());
            var twinAModel = profile.BuildEditModelFromSnapshot(twinA!);
            try
            {
                Assert.Equal(["a", "b"], twinAModel.CustomFields.Select(f => f.Value)); // unchanged
            }
            finally
            {
                twinAModel.ZeroPii();
            }
        }
    }

    // ── TC-CFD-08 ─────────────────────────────────────────────────────────────
    // WinUI 3's ListView.CanReorderItems never calls ObservableCollection.Move - it removes the
    // dragged item and re-inserts it at the drop position, which is exactly what this test
    // reproduces (RemoveAt then Insert) instead of taking the Move() shortcut TC-CFD-05–07 use.
    // Regression coverage for a real bug: the Remove half alone used to be treated as a genuine
    // deletion and autosaved a draft missing the still-in-flight field before the Insert half
    // landed, silently losing the reordered field on the next reload.

    [Fact]
    public async Task TC_CFD_08_ReorderViaRemoveThenInsert_WhenClean_CommitsDirectlyToTwinA()
    {
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            await AddAndCommitFieldAsync(vm, "a");
            await AddAndCommitFieldAsync(vm, "b");
            await AddAndCommitFieldAsync(vm, "c");
            Assert.Equal(3, vm.CustomFields.Count);

            // Drag "c" (index 2) to the front (index 0), WinUI3-style: Remove then Insert
            var dragged = vm.CustomFields[2];
            vm.CustomFields.RemoveAt(2);
            vm.CustomFields.Insert(0, dragged);
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            Assert.False(vm.HasDraft);

            var profile = new ProfileService(db.Factory, NullCrypto, session, NullLogger<ProfileService>.Instance);
            using var twinA = await profile.GetTwinASnapshotAsync(session.GetKey());
            var twinAModel = profile.BuildEditModelFromSnapshot(twinA!);
            try
            {
                Assert.Equal(["c", "a", "b"], twinAModel.CustomFields.Select(f => f.Value));
            }
            finally
            {
                twinAModel.ZeroPii();
            }
        }
    }

    // ── TC-CFD-09 ─────────────────────────────────────────────────────────────
    // HasUnsavedChanges (the gate ProfilePage.xaml.cs's blur handlers check before calling
    // AutoSaveDraftAsync) stays false for an unedited placeholder field, exactly mirroring
    // HasDraft in TC-CFD-01 - regression coverage for a bug where the page's TextBox LostFocus
    // handler called AutoSaveDraftAsync unconditionally on every blur, so blurring ANY unrelated
    // field after adding (but not editing) a custom field recomputed the TwinA comparison and
    // falsely entered draft mode from the field-count difference alone.

    [Fact]
    public async Task TC_CFD_09_AddCustomField_Alone_DoesNotSetHasUnsavedChanges()
    {
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            vm.AddCustomFieldCommand.Execute("Text");

            Assert.False(vm.HasUnsavedChanges);
        }
    }

    // ── TC-CFD-10 ─────────────────────────────────────────────────────────────
    // Editing an unrelated field (Name) does set HasUnsavedChanges, so the page's blur handlers
    // still autosave a draft for genuine edits - proves TC-CFD-09 isn't just a permanently-false flag

    [Fact]
    public async Task TC_CFD_10_EditingUnrelatedField_SetsHasUnsavedChanges()
    {
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            vm.AddCustomFieldCommand.Execute("Text"); // still pristine, must not count as an edit
            Assert.False(vm.HasUnsavedChanges);

            vm.Name = "Someone"; // a real, unrelated edit
            Assert.True(vm.HasUnsavedChanges);
        }
    }

    // ── TC-CFD-11 ─────────────────────────────────────────────────────────────
    // AutoSaveDraftAsync is routinely fire-and-forget. Once the session lock has barricaded writes it
    // must return normally (the block is logged) rather than fault the returned Task, and must not
    // have written a draft

    [Fact]
    public async Task TC_CFD_11_AutoSaveDraftAsync_AfterBarricade_CompletesWithoutThrowing()
    {
        var guard = new SessionLockGuard();
        var (db, session, vm) = BuildVm(guard);
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            guard.Barricade();
            await vm.AutoSaveDraftAsync("Test");

            Assert.False(vm.HasDraft);
            Assert.Null(vm.CurrentSaveTask);
        }
    }

    // ── TC-PLC-01 / 02 / 03 ───────────────────────────────────────────────────
    // StorageChangedMessage handling follows the IsActive lifecycle discipline. The observable effect
    // of RefreshFilesFromPendingAsync is ProfileFiles being reconciled with
    // AppSession.PendingProfileImageIds, so each test seeds a stored file, adds its id to the
    // pending set (as ViewerWindow's profile-link toggle does) and sends the message.

    private static byte[] EncodeIdentity(string plaintext)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[28 + utf8.Length];
        utf8.CopyTo(blob.AsSpan(28));
        return blob;
    }

    private static async Task<int> SeedStoredFileAsync(TestDb db, CancellationToken ct)
    {
        await using var ctx = db.Factory.CreateDbContext();
        var file = new NaimitsuVault.Models.StoredFile
        {
            FileName        = EncodeIdentity("avatar.png"),
            ContentTypeCode = NaimitsuVault.Common.FileTypeCode.ImagePng,
            FileSize        = 123,
            FileHash        = new byte[32],
            FileModifiedAt  = DateTime.UtcNow,
            CreatedAt       = DateTime.UtcNow,
            UpdatedAt       = DateTime.UtcNow,
        };
        ctx.StoredFiles.Add(file);
        await ctx.SaveChangesAsync(ct);
        return file.Id;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20, ct);
        }
        return condition();
    }

    [Fact]
    public async Task TC_PLC_01_PausedVm_IgnoresStorageChangedMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();
            Assert.False(vm.IsActive); // inactive until the page is activated

            int fileId = await SeedStoredFileAsync(db, ct);
            session.PendingProfileImageIds.Add(fileId);

            vm.Resume();
            vm.Pause();
            WeakReferenceMessenger.Default.Send(new StorageChangedMessage());

            // The guard returns synchronously, so nothing is ever started; the delay only gives an
            // unguarded handler's fire-and-forget refresh time to show up as a failure.
            await Task.Delay(300, ct);
            Assert.Empty(vm.ProfileFiles);
        }
    }

    [Fact]
    public async Task TC_PLC_02_ResumedVm_ProcessesStorageChangedMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, session, vm) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            int fileId = await SeedStoredFileAsync(db, ct);
            session.PendingProfileImageIds.Add(fileId);

            vm.Resume();
            Assert.True(vm.IsActive);
            WeakReferenceMessenger.Default.Send(new StorageChangedMessage());

            Assert.True(await WaitUntilAsync(() => vm.ProfileFiles.Count == 1, ct),
                "An active ProfileViewModel must reconcile ProfileFiles with PendingProfileImageIds.");
            Assert.Equal(fileId, vm.ProfileFiles[0].Id);
        }
    }

    [Fact]
    public async Task TC_PLC_03_DisposedVm_DoesNotReceiveStorageChangedMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, session, vm) = BuildVm();
        using (db)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();
            vm.Resume();

            int fileId = await SeedStoredFileAsync(db, ct);
            vm.Dispose();
            session.PendingProfileImageIds.Add(fileId);

            WeakReferenceMessenger.Default.Send(new StorageChangedMessage());

            await Task.Delay(300, ct);
            Assert.Empty(vm.ProfileFiles);
        }
    }
}
