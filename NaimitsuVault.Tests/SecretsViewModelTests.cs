// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Helpers;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// SecretsViewModel tests.
/// TC-PGS-02: on-demand evaluation after LoadSecretAsync.
/// TC-PGS-04: debounced evaluation after manually typing into the Password property
///            (debounceMs=0 makes it immediate).
/// TC-AL-16:  after assigning a SecretListItemViewModel to SelectedListItem, SecretViewed is
///            recorded exactly once.
/// TC-AL-17:  even after ApplySearch, SelectedListItem stays null → 0 IAuditLogService calls.
/// TC-SCF-01: adding a custom field (the "+" button) alone must not dirty the model - the new
///            field is still an empty placeholder indistinguishable from "nothing changed".
/// TC-SCF-02: editing the newly-added field's Value does dirty the model.
/// TC-SCF-03: editing the newly-added field's Label alone (value left blank) does dirty the model -
///            the regression this suite guards against (label-only edits were previously
///            indistinguishable from an untouched placeholder).
/// TC-SCF-04: removing an existing custom field dirties the model immediately, proving the
///            Add-only skip in OnEditingModelCollectionChanged doesn't also swallow Remove.
/// TC-CFO-01: reordering custom fields (Move) while the model is fully clean commits the new
///            order directly to Secrets.CustomFields without pushing a new TimeMachine
///            generation, and never dirties the model.
/// TC-CFO-02: reordering while a draft already exists does not touch Secrets.CustomFields at all
///            (the reorder is left in-memory only).
/// TC-CFO-03: reordering a collection that still contains an unedited placeholder field (added via
///            the "+" button but never edited) does not touch Secrets.CustomFields either.
/// TC-CFO-04: same as TC-CFO-01 but reproduces WinUI 3's actual ListView.CanReorderItems mechanics
///            (Remove then Insert, never ObservableCollection.Move) - regression coverage for a
///            real bug where the bare Remove half alone was treated as a genuine deletion.
/// TC-LST-01: LoadAsync sorts FilteredSecrets by Title (CurrentCultureIgnoreCase), covering the
///            full-reload path.
/// TC-LST-02: repeated Add+rename cycles (the mandatory pre-insert placeholder flow) keep
///            FilteredSecrets sorted and never corrupt a Title into null characters - regression
///            coverage for two real bugs found together: (1) GetOrCreatePoolItem/RefreshListItem
///            assigned m.Title itself (SecretEditModel.TitleBuf's cached display string) into the
///            long-lived list item, so the next Dispose() of that SecretEditModel (any selection
///            change) zeroed the string in place and blanked the already-displayed title; (2) since
///            CategoryItems' tree nodes and FilteredSecrets share the same pooled
///            SecretListItemViewModel instance per Id, RefreshListItem's category-tree branch (which
///            always runs first) overwrote .Title in place before the flat-list branch read its own
///            "did the title change" comparison, so that comparison always saw "no change" and
///            silently skipped re-sorting FilteredSecrets - the collection the left-pane ListView is
///            actually bound to.
/// TC-GSN-01: editing a field, saving a draft, then reverting that field to its original value
///            clears the draft (FindFirstMismatch NoOp check) - regression coverage for a real bug
///            where GenSymbols' display-only default-symbols fallback (never applied to the raw Gen0
///            entity) made the NoOp comparison permanently report a mismatch once any draft save had
///            run, even after every user-visible field was reverted.
/// TC-EAT-01: SnapshotSerializer.WriteEntity normalizes a Secret's ExpiresAt to local midnight the
///            same way WriteEditModel's edit-model reconstruction does, even when the stored value
///            itself carries a different (drifted) time-of-day component.
/// TC-EAT-02: adding a custom field then immediately removing it again clears the draft even when the
///            secret's ExpiresAt is drifted (not exactly local-midnight-in-UTC) - regression coverage
///            for a real bug where the mismatch above alone made FindFirstMismatch never report a
///            true no-op for such a secret, no matter what else was reverted.
/// TC-EAT-03: same add-then-remove sequence with no ExpiresAt set at all still clears the draft
///            (control case, isolating TC-EAT-02 from unrelated custom-field regressions).
/// </summary>
[Collection("SequentialMessenger")]
public sealed class SecretsViewModelTests
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

    private static readonly ICryptoService NullCrypto = new IdentityCryptoService();

    // IdentityCryptoService's encryption format: 28-byte dummy header + UTF-8 plaintext
    internal static byte[] EncryptUtf8(string plaintext)
    {
        var utf8  = Encoding.UTF8.GetBytes(plaintext);
        var blob  = new byte[28 + utf8.Length];
        utf8.CopyTo(blob.AsSpan(28));
        return blob;
    }

    private static string DecryptUtf8(byte[] blob) => Encoding.UTF8.GetString(blob.AsSpan(28));

    internal static (TestDb db, AppSession session, SecretsViewModel vm, StubPasswordEvaluationService evaluator, StubAuditLogService auditLog)
        BuildVm(int strengthDebounceMs = 300)
    {
        var db        = TestDb.Create();
        var session   = new AppSession();
        var secrets   = new SecretRepository(db.Factory);
        var files     = new StoredFileRepository(db.Factory, NullCrypto, NullLogger<StoredFileRepository>.Instance);
        var history   = new SecretHistoryRepository(db.Factory);
        var drafts    = new SecretDraftsRepository(db.Factory, NullCrypto);
        var profile   = new ProfileService(db.Factory, NullCrypto, session, NullLogger<ProfileService>.Instance);
        var favicon   = new FaviconService(db.Factory, NullCrypto);
        var notif     = new NullNotificationService();
        var dialog    = new NullDialogService();
        var guard     = new SessionLockGuard();
        var registry  = new SessionTaskRegistry();
        var disp      = new ImmediateDispatcherService();
        var evaluator = new StubPasswordEvaluationService();
        var auditLog  = new StubAuditLogService();

        var vm = new SecretsViewModel(
            secrets, files, NullCrypto, session, profile,
            notif, dialog, favicon, history, drafts, guard, registry, disp, evaluator, auditLog,
            new TestableAutoBackupService("unused", "unused"), NullLogger<SecretsViewModel>.Instance, strengthDebounceMs);

        return (db, session, vm, evaluator, auditLog);
    }

    // ── TC-PGS-02 ──────────────────────────────────────────────────────────────
    // On-demand strength evaluation runs after LoadSecretAsync, setting PasswordStrengthLevel to 0-4

    [Fact]
    public async Task TC_PGS_02_LoadSecretAsync_OnDemandEvaluationSetsStrengthLevel()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);

            // IdentityCryptoService's encryption format: 28-byte dummy header + UTF-8 plaintext
            var pwdUtf8    = Encoding.UTF8.GetBytes("Password123!");
            var encPwd     = new byte[28 + pwdUtf8.Length];
            pwdUtf8.CopyTo(encPwd.AsSpan(28));

            var titleUtf8  = Encoding.UTF8.GetBytes("Test Entry");
            var encTitle   = new byte[28 + titleUtf8.Length];
            titleUtf8.CopyTo(encTitle.AsSpan(28));

            int secretId;
            await using (var ctx = db.Factory.CreateDbContext())
            {
                var secret = new Secret
                {
                    Title    = encTitle,
                    Password = encPwd,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                ctx.Secrets.Add(secret);
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                secretId = secret.Id;
            }

            await vm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);

            Assert.NotNull(vm.EditingSecret);
            Assert.InRange(vm.EditingSecret.PasswordStrengthLevel, 0, 4);
        }
    }

    // ── TC-PGS-04 ──────────────────────────────────────────────────────────────
    // Debounce runs after manually typing into Password (debounceMs=0 makes it immediate),
    // setting PasswordStrengthLevel to 0-4

    [Fact]
    public async Task TC_PGS_04_ManualPasswordInput_DebounceEvaluatesStrength()
    {
        var (db, _, vm, _, _) = BuildVm(strengthDebounceMs: 0);
        using (db)
        using (vm)
        {
            vm.EditingSecret = new SecretEditModel();

            vm.EditingSecret.Password = "Password123!";

            Assert.NotNull(vm.LastStrengthTask);
            await vm.LastStrengthTask;

            Assert.NotNull(vm.EditingSecret);
            Assert.InRange(vm.EditingSecret.PasswordStrengthLevel, 0, 4);
        }
    }

    // ── TC-AL-16 ──────────────────────────────────────────────────────────────
    // Assign a SecretListItemViewModel to SelectedListItem → after the selection task completes,
    // SecretViewed is recorded exactly once

    [Fact]
    public async Task SelectingSecretNode_LogsSecretViewedExactlyOnce()
    {
        var (db, session, vm, _, auditLog) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);

            vm.SelectedListItem = new SecretListItemViewModel { Id = 999, Title = "テスト機密" };
            await vm.WaitForSelectionTaskAsync();

            Assert.Equal(1, auditLog.SecretViewedCount);
        }
    }

    // ── TC-AL-17 ──────────────────────────────────────────────────────────────
    // Even after ApplySearch, SelectedListItem stays null → 0 IAuditLogService calls

    [Fact]
    public async Task NullSelectionAfterApplySearch_NoAuditLogCalls()
    {
        var (db, session, vm, _, auditLog) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);

            // SearchText change → ApplySearch → SelectedListItem = null is confirmed
            vm.SearchText = "nonexistent_query_xyz";
            await vm.WaitForSelectionTaskAsync();

            Assert.Null(vm.SelectedListItem);
            Assert.Equal(0, auditLog.TotalLogCount);
        }
    }

    // ── TC-GSN-01 ─────────────────────────────────────────────────────────────
    // Regression: FindFirstMismatch's NoOp check compared GenSymbols' raw spans, but
    // SecretEditModel.GenSymbols falls back to PasswordGenerator.DefaultSymbols for display whenever
    // the stored value is null (LoadSecretAsync), while the Gen0 entity keeps the raw null. That made
    // GenSymbols permanently "different" the moment any draft save ran (even with the generator never
    // opened), so reverting an edited field back to its original value never cleared the draft.

    [Fact]
    public async Task TC_GSN_01_EditThenRevert_ClearsDraft_DespiteNeverCustomizedGenSymbols()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            int secretId;
            await using (var ctx = db.Factory.CreateDbContext())
            {
                var secret = new Secret
                {
                    Title    = EncryptUtf8("Original"),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                ctx.Secrets.Add(secret);
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                secretId = secret.Id;
            }

            await vm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
            Assert.NotNull(vm.EditingSecret);

            vm.EditingSecret.Title = "Changed";
            await vm.SaveDraftAsync("LosingFocus");
            Assert.True(vm.EditingSecretHasDraft); // sanity check

            vm.EditingSecret.Title = "Original";
            await vm.SaveDraftAsync("LosingFocus");

            Assert.False(vm.EditingSecretHasDraft);
            var drafts = new SecretDraftsRepository(db.Factory, NullCrypto);
            var (draftEnc, _) = await drafts.GetDraftAsync(secretId);
            Assert.Null(draftEnc);
        }
    }

    // ── TC-EAT-01–03 ─────────────────────────────────────────────────────────
    // Regression: SnapshotSerializer.WriteEntity wrote a Secret's raw stored ExpiresAt instant
    // verbatim, while WriteEditModel (used for both the draft and, via BuildEditModelFromEntityAsync,
    // any reload) always reconstructs ExpiresAt as local midnight, since ExpiresAt is a date-only
    // concept throughout the app (CalendarDatePicker has no time-of-day input). The two only produced
    // the same "o"-formatted string by coincidence, when the stored value already happened to be
    // exactly local-midnight-in-UTC. Any Secret whose ExpiresAt carries a different time-of-day
    // component (e.g. data written before that normalization existed) permanently failed the no-op
    // self-heal comparison (FindFirstMismatch), so touching an unrelated field and reverting it - such
    // as adding a custom field via the "+" button and immediately removing it again - left a phantom
    // draft behind forever, even though nothing meaningful changed.

    [Fact]
    public void TC_EAT_01_WriteEntity_NormalizesDriftedExpiresAt_ToMatchEditModelReconstruction()
    {
        var session = new AppSession();
        session.SetKey(new byte[32]);
        var key = session.GetKey();

        // A "drifted" ExpiresAt: raw UTC midnight, NOT local-midnight-in-UTC. Simulates legacy data
        // saved before ExpiresAt normalization was applied consistently everywhere.
        var entity = new Secret
        {
            Title     = EncryptUtf8("x"),
            ExpiresAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        };

        using var gen0Buf = new PinnedBufferWriter();
        using (var w = new Utf8JsonWriter(gen0Buf))
            SnapshotSerializer.WriteEntity(w, entity, NullCrypto, key, []);
        using var gen0Doc = JsonDocument.Parse(gen0Buf.WrittenSpan.ToArray());
        var gen0ExpiresAt = gen0Doc.RootElement.GetProperty("ExpiresAt").GetString();

        // Mirrors BuildEditModelFromEntityAsync's reconstruction exactly.
        var em = new SecretEditModel { ExpiresAt = new DateTimeOffset(entity.ExpiresAt.Value.ToLocalTime().Date) };
        using var draftBuf = new PinnedBufferWriter();
        using (var w = new Utf8JsonWriter(draftBuf))
            SnapshotSerializer.WriteEditModel(w, em, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        using var draftDoc = JsonDocument.Parse(draftBuf.WrittenSpan.ToArray());
        var draftExpiresAt = draftDoc.RootElement.GetProperty("ExpiresAt").GetString();

        Assert.Equal(draftExpiresAt, gen0ExpiresAt);
    }

    [Fact]
    public async Task TC_EAT_02_AddThenRemoveCustomField_WithDriftedExpiresAt_ClearsDraft()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            int secretId;
            await using (var ctx = db.Factory.CreateDbContext())
            {
                var secret = new Secret
                {
                    Title     = EncryptUtf8("Original"),
                    ExpiresAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), // drifted, see TC-EAT-01
                    CreatedAt  = DateTime.UtcNow,
                    UpdatedAt  = DateTime.UtcNow,
                };
                ctx.Secrets.Add(secret);
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                secretId = secret.Id;
            }

            await vm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
            Assert.NotNull(vm.EditingSecret);

            vm.AddCustomFieldCommand.Execute("Text");
            vm.RemoveCustomFieldCommand.Execute(vm.EditingSecret.CustomFields[0]);
            await vm.SaveDraftAsync("LosingFocus");

            Assert.False(vm.EditingSecretHasDraft);
        }
    }

    [Fact]
    public async Task TC_EAT_03_AddThenRemoveCustomField_WithoutExpiresAt_ClearsDraft()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            int secretId;
            await using (var ctx = db.Factory.CreateDbContext())
            {
                var secret = new Secret
                {
                    Title    = EncryptUtf8("Original"),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                ctx.Secrets.Add(secret);
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                secretId = secret.Id;
            }

            await vm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
            Assert.NotNull(vm.EditingSecret);

            vm.AddCustomFieldCommand.Execute("Text");
            vm.RemoveCustomFieldCommand.Execute(vm.EditingSecret.CustomFields[0]);
            await vm.SaveDraftAsync("LosingFocus");

            Assert.False(vm.EditingSecretHasDraft);
        }
    }

    // ── TC-LBL-01–02 ─────────────────────────────────────────────────────────
    // Regression: FindFirstMismatch didn't compare LabelOverridesBuf at all, so renaming a standard
    // field's label (Username/Password/Website/Email/Title/Notes) via the label-edit UI never entered
    // draft mode - SaveDraftAsync's own NoOp check saw every OTHER field as unchanged and silently
    // discarded the would-be draft as "identical to Gen0" before it could ever be saved. A custom
    // field's label edit worked correctly because it lives inside the CustomFields JSON, which IS
    // compared. The compare dialog already fully supported highlighting a changed label
    // (SecretDraftCompareContent.ResolveLabel/AddRow) - it just never got a draft to compare.

    [Fact]
    public async Task TC_LBL_01_RelabelStandardField_EntersDraftMode()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            int secretId;
            await using (var ctx = db.Factory.CreateDbContext())
            {
                var secret = new Secret
                {
                    Title    = EncryptUtf8("Original"),
                    UserId   = EncryptUtf8("someone@example.com"),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                ctx.Secrets.Add(secret);
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                secretId = secret.Id;
            }

            await vm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
            Assert.NotNull(vm.EditingSecret);
            Assert.False(vm.EditingSecretHasDraft); // sanity check

            vm.EditingSecret.LabelPassword = "PIN Code";
            await vm.SaveDraftAsync("LosingFocus");

            Assert.True(vm.EditingSecretHasDraft);
        }
    }

    [Fact]
    public async Task TC_LBL_02_RevertRelabeledStandardField_ClearsDraft()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            int secretId;
            await using (var ctx = db.Factory.CreateDbContext())
            {
                var secret = new Secret
                {
                    Title    = EncryptUtf8("Original"),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                ctx.Secrets.Add(secret);
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                secretId = secret.Id;
            }

            await vm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
            Assert.NotNull(vm.EditingSecret);

            vm.EditingSecret.LabelUserId = "Account ID";
            await vm.SaveDraftAsync("LosingFocus");
            Assert.True(vm.EditingSecretHasDraft); // sanity check

            vm.EditingSecret.LabelUserId = SecretEditModel.DefaultLabelUserId;
            await vm.SaveDraftAsync("LosingFocus");

            Assert.False(vm.EditingSecretHasDraft);
        }
    }

    // ── TC-LST-01 ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC_LST_01_LoadAsync_SortsFilteredSecretsByTitle()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            var titles = new[] { "Zebra", "apple", "Mango", "banana", "Cherry" };
            await using (var ctx = db.Factory.CreateDbContext())
            {
                foreach (var t in titles)
                {
                    ctx.Secrets.Add(new Secret
                    {
                        Title = EncryptUtf8(t),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                }
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await vm.LoadAsync();

            var actual = vm.FilteredSecrets.Select(x => x.Title).ToList();
            var expected = titles.OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();
            Assert.Equal(expected, actual);
        }
    }

    // ── TC-LST-02 ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task TC_LST_02_IncrementalAddThenRename_KeepsFilteredSecretsSorted()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            await vm.LoadAsync();

            var saveCore = typeof(SecretsViewModel).GetMethod("SaveSecretCoreAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            var finalTitles = new[] { "Zebra", "apple", "Mango", "banana", "Cherry" };
            foreach (var title in finalTitles)
            {
                await vm.AddSecretCommand.ExecuteAsync(null);
                Assert.NotNull(vm.EditingSecret);
                vm.EditingSecret.Title = title;
                await (Task)saveCore.Invoke(vm, null)!;
            }

            var actual = vm.FilteredSecrets.Select(x => x.Title).ToList();
            var expected = finalTitles.OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();
            Assert.Equal(string.Join(",", expected), string.Join(",", actual));
        }
    }

    // ── TC-SCF-01 ─────────────────────────────────────────────────────────────
    // AddCustomField alone (no value/label edit) must not dirty the model

    [Fact]
    public void TC_SCF_01_AddCustomField_Alone_DoesNotDirtyModel()
    {
        var (db, _, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            vm.EditingSecret = new SecretEditModel();

            vm.AddCustomFieldCommand.Execute("Text");

            Assert.False(vm.HasUnsavedChanges);
        }
    }

    // ── TC-SCF-02 ─────────────────────────────────────────────────────────────
    // Editing the newly-added field's Value does dirty the model

    [Fact]
    public void TC_SCF_02_AddCustomField_ThenEditValue_DirtiesModel()
    {
        var (db, _, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            vm.EditingSecret = new SecretEditModel();

            vm.AddCustomFieldCommand.Execute("Text");
            vm.EditingSecret.CustomFields[0].Value = "some value";

            Assert.True(vm.HasUnsavedChanges);
        }
    }

    // ── TC-SCF-03 ─────────────────────────────────────────────────────────────
    // Editing the newly-added field's Label alone (value left blank) does dirty the model

    [Fact]
    public void TC_SCF_03_AddCustomField_ThenEditLabelOnly_DirtiesModel()
    {
        var (db, _, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            vm.EditingSecret = new SecretEditModel();

            vm.AddCustomFieldCommand.Execute("Text");
            vm.EditingSecret.CustomFields[0].Label = "My Custom Label";

            Assert.True(vm.HasUnsavedChanges);
        }
    }

    // ── TC-SCF-04 ─────────────────────────────────────────────────────────────
    // Removing an existing custom field dirties the model immediately (Add-only skip doesn't swallow Remove)

    [Fact]
    public void TC_SCF_04_RemoveExistingCustomField_DirtiesModelImmediately()
    {
        var (db, _, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            vm.EditingSecret = new SecretEditModel();
            vm.AddCustomFieldCommand.Execute("Text");
            vm.EditingSecret.CustomFields[0].Value = "some value";
            Assert.True(vm.HasUnsavedChanges); // sanity check

            // Simulate a freshly-loaded secret where this field is already committed
            vm.DiscardChanges();
            Assert.False(vm.HasUnsavedChanges);

            vm.RemoveCustomFieldCommand.Execute(vm.EditingSecret.CustomFields[0]);

            Assert.True(vm.HasUnsavedChanges);
        }
    }

    private static async Task<int> CreateSecretWithCustomFieldsAsync(TestDb db, params (int FieldId, string Label, string Value)[] fields)
    {
        var cfsJson = JsonSerializer.Serialize(
            fields.Select(f => new CustomFieldModel { FieldId = f.FieldId, Label = f.Label, Value = f.Value }).ToList(),
            SecretsViewModel.SecretListJsonContext.Default.ListCustomFieldModel);

        await using var ctx = db.Factory.CreateDbContext();
        var secret = new Secret
        {
            Title        = EncryptUtf8("Test Entry"),
            CustomFields = EncryptUtf8(cfsJson),
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        };
        ctx.Secrets.Add(secret);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        return secret.Id;
    }

    // ── TC-CFO-01 ─────────────────────────────────────────────────────────────
    // Reordering custom fields via Move, while the model is fully clean, commits directly to
    // Secrets.CustomFields without pushing a new TimeMachine generation, and doesn't dirty the model

    [Fact]
    public async Task TC_CFO_01_ReorderCustomFields_WhenClean_CommitsDirectlyWithoutTimeMachinePush()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            var secrets = new SecretRepository(db.Factory);
            var history = new SecretHistoryRepository(db.Factory);

            int secretId = await CreateSecretWithCustomFieldsAsync(db, (1, "A", "a"), (2, "B", "b"));

            await vm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
            Assert.Equal(2, vm.EditingSecret!.CustomFields.Count);

            vm.EditingSecret.CustomFields.Move(0, 1); // [A, B] -> [B, A]
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            Assert.False(vm.HasUnsavedChanges);

            var reloaded = await secrets.GetByIdAsync(secretId);
            var reloadedCfs = JsonSerializer.Deserialize(DecryptUtf8(reloaded!.CustomFields!),
                SecretsViewModel.SecretListJsonContext.Default.ListCustomFieldModel);
            Assert.Equal(["B", "A"], reloadedCfs!.Select(f => f.Label));

            Assert.Null(await history.GetSlotOrderAsync(secretId)); // no TimeMachine generation pushed
        }
    }

    // ── TC-CFO-02 ─────────────────────────────────────────────────────────────
    // Reordering while an unsaved edit (draft) is in progress must not touch Secrets.CustomFields

    [Fact]
    public async Task TC_CFO_02_ReorderCustomFields_WhileDraftExists_DoesNotCommit()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            var secrets = new SecretRepository(db.Factory);

            int secretId = await CreateSecretWithCustomFieldsAsync(db, (1, "A", "a"), (2, "B", "b"));

            await vm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
            vm.EditingSecret!.Notes = "unsaved edit"; // dirties the model (HasUnsavedChanges=true)

            vm.EditingSecret.CustomFields.Move(0, 1);
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            var reloaded = await secrets.GetByIdAsync(secretId);
            var reloadedCfs = JsonSerializer.Deserialize(DecryptUtf8(reloaded!.CustomFields!),
                SecretsViewModel.SecretListJsonContext.Default.ListCustomFieldModel);
            Assert.Equal(["A", "B"], reloadedCfs!.Select(f => f.Label)); // unchanged in DB
        }
    }

    // ── TC-CFO-03 ─────────────────────────────────────────────────────────────
    // Reordering a collection that still carries an unedited "+"-button placeholder field must
    // not touch Secrets.CustomFields either

    [Fact]
    public async Task TC_CFO_03_ReorderCustomFields_WithUneditedPlaceholder_DoesNotCommit()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            var secrets = new SecretRepository(db.Factory);

            int secretId = await CreateSecretWithCustomFieldsAsync(db, (1, "A", "a"), (2, "B", "b"));

            await vm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
            vm.AddCustomFieldCommand.Execute("Text"); // unedited placeholder, FieldId not in Gen0
            Assert.False(vm.HasUnsavedChanges); // sanity check (TC-SCF-01)

            vm.EditingSecret!.CustomFields.Move(0, 1);
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            var reloaded = await secrets.GetByIdAsync(secretId);
            var reloadedCfs = JsonSerializer.Deserialize(DecryptUtf8(reloaded!.CustomFields!),
                SecretsViewModel.SecretListJsonContext.Default.ListCustomFieldModel);
            Assert.Equal(["A", "B"], reloadedCfs!.Select(f => f.Label)); // unchanged in DB
        }
    }

    // ── TC-CFO-04 ─────────────────────────────────────────────────────────────
    // WinUI 3's ListView.CanReorderItems never calls ObservableCollection.Move - it removes the
    // dragged item and re-inserts it at the drop position, which is exactly what this test
    // reproduces (RemoveAt then Insert) instead of taking the Move() shortcut TC-CFO-01–03 use.
    // Regression coverage for a real bug: the Remove half alone used to be treated as a genuine
    // deletion and autosaved a draft missing the still-in-flight field before the Insert half
    // landed, silently losing the reordered field on the next reload.

    [Fact]
    public async Task TC_CFO_04_ReorderViaRemoveThenInsert_WhenClean_CommitsDirectlyWithoutTimeMachinePush()
    {
        var (db, session, vm, _, _) = BuildVm();
        using (db)
        using (vm)
        {
            session.SetKey(new byte[32]);
            var secrets = new SecretRepository(db.Factory);
            var history = new SecretHistoryRepository(db.Factory);

            int secretId = await CreateSecretWithCustomFieldsAsync(db, (1, "A", "a"), (2, "B", "b"), (3, "C", "c"));

            await vm.LoadSecretAsync(secretId, TestContext.Current.CancellationToken);
            Assert.Equal(3, vm.EditingSecret!.CustomFields.Count);

            // Drag "C" (index 2) to the front (index 0), WinUI3-style: Remove then Insert
            var dragged = vm.EditingSecret.CustomFields[2];
            vm.EditingSecret.CustomFields.RemoveAt(2);
            vm.EditingSecret.CustomFields.Insert(0, dragged);
            if (vm.CurrentSaveTask != null) await vm.CurrentSaveTask;

            Assert.False(vm.HasUnsavedChanges);

            var reloaded = await secrets.GetByIdAsync(secretId);
            var reloadedCfs = JsonSerializer.Deserialize(DecryptUtf8(reloaded!.CustomFields!),
                SecretsViewModel.SecretListJsonContext.Default.ListCustomFieldModel);
            Assert.Equal(["C", "A", "B"], reloadedCfs!.Select(f => f.Label));

            Assert.Null(await history.GetSlotOrderAsync(secretId)); // no TimeMachine generation pushed
        }
    }
}
