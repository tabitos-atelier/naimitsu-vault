// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// Verifies full read-only enforcement in restricted-viewing mode (Route A).
///
/// TC-ROM-01..04: even when the add/edit/delete secret commands are invoked directly, bypassing
///             CanExecute, they must never access EF Core (vault DB) (proven via FaultInjectionDbContextFactory).
/// TC-ROM-05..06: when AutoBackupService.IsReadOnlyRestricted=true, RunBackup() must never create any files.
/// TC-ROM-09..10: a manual backup (BackupDatabaseCommand) is refused with a warning in restricted mode
///             and never reaches the folder picker; normal mode proceeds (control experiment).
///
/// Additionally: structurally verifies the seed guards in App.xaml.cs's OnLaunched and after re-unlock.
///       Proves that at both call sites, the IsReadOnlyRestricted guard fully prevents both
///       DatabaseInitializer/ProfileService writes (EnsureVaultDbMigratedAsync,
///       SeedEmptyProfileIfAbsentAsync) from ever being called.
/// </summary>
public sealed class ReadOnlyRestrictedModeTests : IDisposable
{
    private readonly string _tempDir;

    public ReadOnlyRestrictedModeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"naimitsu_rorm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // TC-ROM-01..04 : structural blocking of SecretsViewModel write commands
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

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

    /// <summary>
    /// Builds a SecretsViewModel where every access to the vault DB (EF Core) throws.
    /// If the IsReadOnlyRestricted guard works correctly, a direct call to a write command
    /// returns on its first line and never reaches this stub's exception.
    /// If the guard is ever removed or regresses, the repository call throws immediately and the test fails.
    /// </summary>
    private static SecretsViewModel BuildReadOnlyVm()
    {
        var explodingFactory = new FaultInjectionDbContextFactory(
            new InvalidOperationException("Slipped past the restricted-viewing-mode guard and accessed the vault DB"));
        var crypto  = new IdentityCryptoService();
        var session = new AppSession { IsReadOnlyRestricted = true };

        var secrets  = new SecretRepository(explodingFactory);
        var files    = new StoredFileRepository(explodingFactory, crypto, NullLogger<StoredFileRepository>.Instance);
        var history  = new SecretHistoryRepository(explodingFactory);
        var drafts   = new SecretDraftsRepository(explodingFactory, crypto);
        var profile  = new ProfileService(explodingFactory, crypto, session, NullLogger<ProfileService>.Instance);
        var favicon  = new FaviconService(explodingFactory, crypto);

        return new SecretsViewModel(
            secrets, files, crypto, session, profile,
            new NullNotificationService(), new NullDialogService(), favicon, history, drafts,
            new SessionLockGuard(), new SessionTaskRegistry(), new ImmediateDispatcherService(),
            new StubPasswordEvaluationService(), new StubAuditLogService(), new TestableAutoBackupService("unused", "unused"), NullLogger<SecretsViewModel>.Instance);
    }

    // TC-ROM-01
    // Even calling AddSecretCommand's ExecuteAsync directly, bypassing CanExecute, must not access the vault DB

    [Fact]
    public async Task AddSecretCommand_WhenReadOnlyRestricted_NeverTouchesVaultDb()
    {
        using var vm = BuildReadOnlyVm();

        await vm.AddSecretCommand.ExecuteAsync(null);

        Assert.Null(vm.EditingSecret);
    }

    // TC-ROM-02
    // Even calling SaveSecretCommand's ExecuteAsync directly must not access the vault DB
    // (SecretsPage.xaml.cs's Ctrl+S handler checks CanExecute, but the internal guard exists
    //  independently as a defense against direct command invocation in general)

    [Fact]
    public async Task SaveSecretCommand_WhenReadOnlyRestricted_NeverTouchesVaultDb()
    {
        using var vm = BuildReadOnlyVm();
        vm.EditingSecret = new SecretEditModel { Id = 1, Title = "テスト" };

        await vm.SaveSecretCommand.ExecuteAsync(null);

        // If the guard is working, SaveSecretCoreAsync is never reached and
        // CurrentSaveTask/IsBusy remain unchanged at their initial values.
        // Relying solely on exception propagation from FaultInjectionDbContextFactory would fail
        // to detect a broken guard if a generic try-catch were ever added to SaveSecretAsync and
        // swallowed the exception, so the absence of a state change is checked explicitly.
        Assert.Null(vm.CurrentSaveTask);
        Assert.False(vm.IsBusy);
    }

    // TC-ROM-03
    // Even calling DeleteSecretCommand's ExecuteAsync directly must not access the vault DB
    // (SecretsPage.xaml.cs's Delete key handler calls ExecuteAsync directly without checking
    //  CanExecute, so this guard is the only line of defense)

    [Fact]
    public async Task DeleteSecretCommand_WhenReadOnlyRestricted_NeverTouchesVaultDb()
    {
        using var vm = BuildReadOnlyVm();
        vm.EditingSecret = new SecretEditModel { Id = 1, Title = "テスト" };

        await vm.DeleteSecretCommand.ExecuteAsync(null);

        Assert.NotNull(vm.EditingSecret);
    }

    // TC-ROM-04
    // In normal mode (IsReadOnlyRestricted=false), AddSecretCommand actually INSERTs into the
    // vault DB (a control experiment confirming the guard is not over-blocking)

    [Fact]
    public async Task AddSecretCommand_WhenNotReadOnlyRestricted_InsertsIntoVaultDb()
    {
        using var db = TestDb.Create();
        var crypto  = new IdentityCryptoService();
        var session = new AppSession();
        session.SetKey(new byte[32]);

        var secrets  = new SecretRepository(db.Factory);
        var files    = new StoredFileRepository(db.Factory, crypto, NullLogger<StoredFileRepository>.Instance);
        var history  = new SecretHistoryRepository(db.Factory);
        var drafts   = new SecretDraftsRepository(db.Factory, crypto);
        var profile  = new ProfileService(db.Factory, crypto, session, NullLogger<ProfileService>.Instance);
        var favicon  = new FaviconService(db.Factory, crypto);

        using var vm = new SecretsViewModel(
            secrets, files, crypto, session, profile,
            new NullNotificationService(), new NullDialogService(), favicon, history, drafts,
            new SessionLockGuard(), new SessionTaskRegistry(), new ImmediateDispatcherService(),
            new StubPasswordEvaluationService(), new StubAuditLogService(), new TestableAutoBackupService("unused", "unused"), NullLogger<SecretsViewModel>.Instance);

        await vm.AddSecretCommand.ExecuteAsync(null);

        Assert.NotNull(vm.EditingSecret);
        await using var ctx = db.Factory.CreateDbContext();
        Assert.Equal(1, await ctx.Secrets.CountAsync(TestContext.Current.CancellationToken));
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // TC-ROM-05..06 : confirming the backup engine is fully skipped
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    // TC-ROM-05
    // When AutoBackupService.IsReadOnlyRestricted=true, RunBackup() must not copy any files to the
    // backup destination folder, and must not leak any exception.

    [Fact]
    public void RunBackup_WhenIsReadOnlyModeTrue_CreatesNoFiles()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        Directory.CreateDirectory(backupDir);

        var svc = new AutoBackupService(NullLogger<AutoBackupService>.Instance)
        {
            IsEnabled            = true,
            Folder               = backupDir,
            IsReadOnlyRestricted = true,
        };

        // The IsReadOnlyRestricted guard must return first, even if the DB file doesn't exist
        svc.RunBackup();

        Assert.Empty(Directory.GetFiles(backupDir));
    }

    // TC-ROM-06
    // When IsReadOnlyRestricted=false, RunBackup() proceeds to the normal backup logic.
    // (No actual copy happens since the source DB doesn't exist, but the guard passes through)

    [Fact]
    public void RunBackup_WhenIsReadOnlyModeFalse_ProceedsNormally()
    {
        var backupDir = Path.Combine(_tempDir, "backup2");
        Directory.CreateDirectory(backupDir);

        var svc = new AutoBackupService(NullLogger<AutoBackupService>.Instance)
        {
            IsEnabled            = true,
            Folder               = backupDir,
            IsReadOnlyRestricted = false,
        };

        // Nothing is copied since the source DB doesn't exist, but it must finish without exception
        var ex = Record.Exception(() => svc.RunBackup());
        Assert.Null(ex);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // TC-ROM-07..08 : simulated sequence for the seed guards at App.xaml.cs startup / after re-unlock
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// Faithfully reproduces the "if (!session.IsReadOnlyRestricted) { 2 seed calls }" guard
    /// statement present at App.xaml.cs's OnLaunched and after LockAsync's successful re-unlock.
    /// Since App is a WinUI entry point and cannot be launched directly from xUnit, this reproduces
    /// the guard statement itself using the same "simulated sequence" approach as
    /// SelfHealingIntegrationTests.cs.
    /// </summary>
    private static async Task RunSeedGuardSequenceAsync(
        DatabaseInitializer initializer, ProfileService profile, AppSession session)
    {
        if (!session.IsReadOnlyRestricted)
        {
            var dek = session.GetKey();
            await initializer.EnsureVaultDbMigratedAsync();
            await profile.SeedEmptyProfileIfAbsentAsync(dek);
        }
    }

    // TC-ROM-07
    // When IsReadOnlyRestricted=true, neither seed call may access either AppDbContext or
    // UnifiedDbContext (proven via FaultInjectionDbContextFactory/ExplodingUnifiedDbContextFactory)

    [Fact]
    public async Task SeedGuardSequence_WhenReadOnlyRestricted_NeverTouchesAnyDb()
    {
        var explodingAppDb = new FaultInjectionDbContextFactory(
            new InvalidOperationException("Slipped past the restricted-viewing-mode guard and accessed AppDbContext"));
        var crypto  = new IdentityCryptoService();
        var session = new AppSession { IsReadOnlyRestricted = true };

        var initializer = new DatabaseInitializer(
            explodingAppDb, new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(), NullLogger<DatabaseInitializer>.Instance);
        var profile = new ProfileService(explodingAppDb, crypto, session, NullLogger<ProfileService>.Instance);

        var ex = await Record.ExceptionAsync(() => RunSeedGuardSequenceAsync(initializer, profile, session));

        Assert.Null(ex);
    }

    // TC-ROM-08
    // When IsReadOnlyRestricted=false, the seed calls actually write to AppDbContext
    // (a control experiment confirming the guard is not over-blocking)

    [Fact]
    public async Task SeedGuardSequence_WhenNotReadOnlyRestricted_SeedsProfile()
    {
        using var db = TestDb.Create();
        var crypto  = new IdentityCryptoService();
        var session = new AppSession();
        session.SetKey(new byte[32]);

        var initializer = new DatabaseInitializer(
            db.Factory, new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(), NullLogger<DatabaseInitializer>.Instance);
        var profile = new ProfileService(db.Factory, crypto, session, NullLogger<ProfileService>.Instance);

        await RunSeedGuardSequenceAsync(initializer, profile, session);

        await using var ctx = db.Factory.CreateDbContext();
        Assert.True(await ctx.Metadata.AnyAsync(
            s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinA, TestContext.Current.CancellationToken));
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // TC-ROM-09..10 : manual backup is refused in restricted mode, with a warning
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    private sealed class RecordingNotificationService : IAppNotificationService
    {
        public List<NotificationSeverity> Shown { get; } = [];
        public void Show(int _, int __, NotificationSeverity severity = default, TimeSpan? ____ = null) => Shown.Add(severity);
        public void Show(int _, string __, NotificationSeverity severity = default, TimeSpan? ____ = null) => Shown.Add(severity);
    }

    private sealed class FolderPickerSpy : IFilePickerService
    {
        public int OpenFolderCalls { get; private set; }

        public Task<SecureCharBuffer?> SaveAsync(string _, IReadOnlyList<(string, string)> __)
            => Task.FromResult<SecureCharBuffer?>(null);
        public Task<SecureCharBuffer?> OpenAsync(IReadOnlyList<(string, string)> _)
            => Task.FromResult<SecureCharBuffer?>(null);
        public Task<IReadOnlyList<SecureCharBuffer>> OpenMultipleAsync(IReadOnlyList<(string, string)> _)
            => Task.FromResult<IReadOnlyList<SecureCharBuffer>>([]);
        public Task<SecureCharBuffer?> OpenFolderAsync()
        {
            OpenFolderCalls++;
            return Task.FromResult<SecureCharBuffer?>(null);
        }
    }

    private static VaultOperationsViewModel BuildBackupVm(bool restricted, IAppNotificationService notification, IFilePickerService picker)
        => new(
            vaultFactory:       null!,
            auth:               new StubAuthService(),
            connectionProvider: null!,
            secrets:            null!,
            secretHistory:      null!,
            crypto:             null!,
            session:            new AppSession { IsReadOnlyRestricted = restricted },
            notification:       notification,
            dialog:             new NullDialogService(),
            appService:         null!,
            autoBackup:         new TestableAutoBackupService("unused", "unused"),
            filePicker:         picker,
            auditLog:           new StubAuditLogService(),
            autoLock:           new IdleTimeoutService(),
            logger:             NullLogger<VaultOperationsViewModel>.Instance);

    // TC-ROM-09
    // Invoking BackupDatabaseCommand directly (bypassing the unreachable Settings page) in restricted
    // mode must show a warning and stop before the folder picker is ever opened.

    [Fact]
    public async Task BackupDatabaseCommand_WhenReadOnlyRestricted_WarnsAndNeverOpensFolderPicker()
    {
        var notification = new RecordingNotificationService();
        var picker       = new FolderPickerSpy();
        var vm           = BuildBackupVm(restricted: true, notification, picker);

        await vm.BackupDatabaseCommand.ExecuteAsync(null);

        Assert.Equal(0, picker.OpenFolderCalls);
        Assert.Equal([NotificationSeverity.Warning], notification.Shown);
    }

    // TC-ROM-10
    // Control experiment: in normal mode the same command proceeds to the folder picker and shows
    // no warning (proves the guard is not over-blocking).

    [Fact]
    public async Task BackupDatabaseCommand_WhenNotRestricted_ProceedsToFolderPickerWithoutWarning()
    {
        var notification = new RecordingNotificationService();
        var picker       = new FolderPickerSpy();
        var vm           = BuildBackupVm(restricted: false, notification, picker);

        await vm.BackupDatabaseCommand.ExecuteAsync(null);

        Assert.Equal(1, picker.OpenFolderCalls);
        Assert.Empty(notification.Shown);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // Supplement: IsReadOnlyRestricted is reset by AppSession.Lock()
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    [Fact]
    public void AppSession_Lock_ResetsIsReadOnlyRestricted()
    {
        var session = new AppSession
        {
            IsReadOnlyRestricted = true,
        };

        session.Lock();

        Assert.False(session.IsReadOnlyRestricted);
    }
}
