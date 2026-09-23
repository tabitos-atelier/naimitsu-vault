// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression coverage for a bug found in real usage: creating a 3rd vault (and importing secrets
/// from plaintext) never made the shutdown-time automatic backup fire, because
/// VaultOperationsViewModel.AddVaultAsync/ImportSecretsAsync never called
/// AutoBackupService.MarkContentChanged() - the flag AutoBackupService's RunBackup() checks before
/// deciding whether there is anything worth protecting since the last backup.
///
/// TC-VOB-01: AddVaultAsync success marks the pending-backup flag.
/// TC-VOB-02: AddVaultAsync failure (CreateNewVaultAsync returns false) does not mark it.
/// TC-VOB-03: ImportSecretsAsync success (>=1 record imported) marks the pending-backup flag.
/// </summary>
public sealed class VaultOperationsAutoBackupTests
{
    private sealed class NullNotificationService : IAppNotificationService
    {
        public void Show(int _, int __, NotificationSeverity ___ = default, TimeSpan? ____ = null) { }
        public void Show(int _, string __, NotificationSeverity ___ = default, TimeSpan? ____ = null) { }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class StubDialogService : IDialogService
    {
        public bool ConfirmAsyncResult { get; init; } = true;
        public MasterAuthResult? ConfirmMasterAuthResult { get; init; }

        public Task<bool> ConfirmAsync(string _, string __, bool ___ = false)
            => Task.FromResult(ConfirmAsyncResult);
        public Task<bool> ConfirmDestructiveAsync(string _, string __, string ____, string _____, bool ___ = false)
            => Task.FromResult(ConfirmAsyncResult);
        public Task<bool> ConfirmWithIconAsync(string _, string __, string ____, string _____, bool ______ = false, bool ___ = false)
            => Task.FromResult(ConfirmAsyncResult);
        public Task<bool> ConfirmSwapAsync(string _, TimeMachineSwapPreview __)
            => Task.FromResult(false);
        public Task<SecureCharBuffer?> ConfirmPasswordAsync(string _)
            => Task.FromResult<SecureCharBuffer?>(null);
        public Task ShowInfoAsync(string _, string __) => Task.CompletedTask;
        public Task<IDisposable> ShowBusyAsync(string _) => Task.FromResult<IDisposable>(NullDisposable.Instance);
        public Task<MasterAuthResult?> ConfirmMasterAuthAsync(string _)
            => Task.FromResult(ConfirmMasterAuthResult);
    }

    private sealed class StubFilePicker : IFilePickerService
    {
        public string? OpenAsyncPath { get; init; }

        public Task<SecureCharBuffer?> SaveAsync(string _, IReadOnlyList<(string, string)> __)
            => Task.FromResult<SecureCharBuffer?>(null);

        public Task<SecureCharBuffer?> OpenAsync(IReadOnlyList<(string, string)> _)
        {
            if (OpenAsyncPath == null) return Task.FromResult<SecureCharBuffer?>(null);
            var buf = new SecureCharBuffer();
            buf.SetFromSpan(OpenAsyncPath.AsSpan());
            return Task.FromResult<SecureCharBuffer?>(buf);
        }

        public Task<IReadOnlyList<SecureCharBuffer>> OpenMultipleAsync(IReadOnlyList<(string, string)> _)
            => Task.FromResult<IReadOnlyList<SecureCharBuffer>>([]);
        public Task<SecureCharBuffer?> OpenFolderAsync()
            => Task.FromResult<SecureCharBuffer?>(null);
    }

    private static readonly ICryptoService NullCrypto = new IdentityCryptoService();

    private static string NewPendingFlagPath()
        => Path.Combine(Path.GetTempPath(), $"naimitsu_test_pending_backup_{Guid.NewGuid():N}.flag");

    // ─────────────────────────────────────────────────────────────────────────
    // TC-VOB-01/02: AddVaultAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddVaultAsync_Success_MarksAutoBackupContentChanged()
    {
        var flagPath   = NewPendingFlagPath();
        using var autoBackup = new TestableAutoBackupService("unused", "unused", pendingBackupFlagPath: flagPath);
        var auth = new StubAuthService
        {
            GetAvailableVaultNumbersAsyncResult = [1, 2, 3],
            CreateNewVaultAsyncResult           = true,
        };
        var dialog = new StubDialogService { ConfirmMasterAuthResult = MasterAuthResult.FromHello() };

        var vm = new VaultOperationsViewModel(
            vaultFactory:       null!,
            auth:               auth,
            connectionProvider: null!,
            secrets:            null!,
            secretHistory:      null!,
            crypto:             null!,
            session:            new AppSession(),
            notification:       new NullNotificationService(),
            dialog:             dialog,
            appService:         null!,
            autoBackup:         autoBackup,
            filePicker:         null!,
            auditLog:           new StubAuditLogService(),
            autoLock:           new IdleTimeoutService(), logger: NullLogger<VaultOperationsViewModel>.Instance);

        await vm.LoadAvailableVaultsAsync();
        vm.AskNewVaultPasswordAsync = () =>
            Task.FromResult<(char[], char[])?>(("password1".ToCharArray(), "password1".ToCharArray()));

        await vm.AddVaultCommand.ExecuteAsync(null);

        Assert.True(File.Exists(flagPath));
    }

    [Fact]
    public async Task AddVaultAsync_CreateNewVaultFails_DoesNotMarkAutoBackupContentChanged()
    {
        var flagPath   = NewPendingFlagPath();
        using var autoBackup = new TestableAutoBackupService("unused", "unused", pendingBackupFlagPath: flagPath);
        var auth = new StubAuthService
        {
            GetAvailableVaultNumbersAsyncResult = [1, 2, 3],
            CreateNewVaultAsyncResult           = false,
        };
        var dialog = new StubDialogService { ConfirmMasterAuthResult = MasterAuthResult.FromHello() };

        var vm = new VaultOperationsViewModel(
            vaultFactory:       null!,
            auth:               auth,
            connectionProvider: null!,
            secrets:            null!,
            secretHistory:      null!,
            crypto:             null!,
            session:            new AppSession(),
            notification:       new NullNotificationService(),
            dialog:             dialog,
            appService:         null!,
            autoBackup:         autoBackup,
            filePicker:         null!,
            auditLog:           new StubAuditLogService(),
            autoLock:           new IdleTimeoutService(), logger: NullLogger<VaultOperationsViewModel>.Instance);

        await vm.LoadAvailableVaultsAsync();
        vm.AskNewVaultPasswordAsync = () =>
            Task.FromResult<(char[], char[])?>(("password1".ToCharArray(), "password1".ToCharArray()));

        await vm.AddVaultCommand.ExecuteAsync(null);

        Assert.False(File.Exists(flagPath));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-VOB-03: ImportSecretsAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportSecretsAsync_OneRecordImported_MarksAutoBackupContentChanged()
    {
        var flagPath   = NewPendingFlagPath();
        using var autoBackup = new TestableAutoBackupService("unused", "unused", pendingBackupFlagPath: flagPath);
        var importPath = Path.Combine(Path.GetTempPath(), $"naimitsu_test_import_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(importPath, """[{"Title":"Imported Secret"}]""", TestContext.Current.CancellationToken);

        try
        {
            using var db = TestDb.Create();
            var secrets       = new SecretRepository(db.Factory);
            var secretHistory = new SecretHistoryRepository(db.Factory);
            var session       = new AppSession();
            session.SetKey(new byte[32]);
            var dialog = new StubDialogService { ConfirmMasterAuthResult = MasterAuthResult.FromHello() };

            var vm = new VaultOperationsViewModel(
                vaultFactory:       db.Factory,
                auth:               new StubAuthService(),
                connectionProvider: null!,
                secrets:            secrets,
                secretHistory:      secretHistory,
                crypto:             NullCrypto,
                session:            session,
                notification:       new NullNotificationService(),
                dialog:             dialog,
                appService:         null!,
                autoBackup:         autoBackup,
                filePicker:         new StubFilePicker { OpenAsyncPath = importPath },
                auditLog:           new StubAuditLogService(),
                autoLock:           new IdleTimeoutService(), logger: NullLogger<VaultOperationsViewModel>.Instance);

            await vm.ImportSecretsCommand.ExecuteAsync(null);

            Assert.True(File.Exists(flagPath));
        }
        finally
        {
            File.Delete(importPath);
        }
    }
}
