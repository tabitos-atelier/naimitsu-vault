// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// Routing tests for the master authentication gate.
///
/// TC-MA-01: On the Hello auth path (IsHelloVerified=true), UnlockAsync is not called and
///           the flow reaches FilePicker.
/// TC-MA-02: On the password path (IsHelloVerified=false), StubAuthService.UnlockAsync returns
///           false, so verified=false and FilePicker is never reached.
/// TC-MA-03: When ConfirmMasterAuthAsync returns null (user cancels), FilePicker is never reached.
/// TC-MA-04: On the Hello path, the EAC issuance gate is passed and GenerateEmergencyAccessCodeAsync
///           is reached.
/// TC-MA-05: When ConfirmMasterAuthAsync returns null, EAC issuance is not attempted.
/// </summary>
public sealed class MasterAuthTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Stubs
    // ─────────────────────────────────────────────────────────────────────────

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

    private sealed class TrackingFilePicker : IFilePickerService
    {
        public bool SaveAsyncWasCalled { get; private set; }

        public Task<SecureCharBuffer?> SaveAsync(string _, IReadOnlyList<(string, string)> __)
        {
            SaveAsyncWasCalled = true;
            return Task.FromResult<SecureCharBuffer?>(null);
        }

        public Task<SecureCharBuffer?> OpenAsync(IReadOnlyList<(string, string)> _)
            => Task.FromResult<SecureCharBuffer?>(null);
        public Task<IReadOnlyList<SecureCharBuffer>> OpenMultipleAsync(IReadOnlyList<(string, string)> _)
            => Task.FromResult<IReadOnlyList<SecureCharBuffer>>([]);
        public Task<SecureCharBuffer?> OpenFolderAsync()
            => Task.FromResult<SecureCharBuffer?>(null);
    }

    private static VaultOperationsViewModel BuildVm(
        IDialogService dialog,
        IFilePickerService? filePicker = null,
        IAppNotificationService? notification = null,
        IAuthService? auth = null) => new(
            vaultFactory:       null!,
            auth:               auth!,
            connectionProvider: null!,
            secrets:            null!,
            secretHistory:      null!,
            crypto:             null!,
            session:            new AppSession(),
            notification:       notification ?? new NullNotificationService(),
            dialog:             dialog,
            appService:         null!,
            autoBackup:         null!,
            filePicker:         filePicker!,
            auditLog:           new StubAuditLogService(),
            autoLock:           new IdleTimeoutService(),
            logger:             NullLogger<VaultOperationsViewModel>.Instance);

    // ─────────────────────────────────────────────────────────────────────────
    // TC-MA-01: ExportSecrets — the Hello path reaches FilePicker
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportSecrets_HelloVerified_AuthGatePassedAndFilePickerReached()
    {
        var filePicker = new TrackingFilePicker();
        var vm = BuildVm(
            dialog: new StubDialogService
            {
                ConfirmAsyncResult     = true,
                ConfirmMasterAuthResult = MasterAuthResult.FromHello(),
            },
            filePicker: filePicker,
            auth: new StubAuthService());

        await vm.ExportSecretsCommand.ExecuteAsync(null);

        // Hello path: UnlockAsync is not called, the auth gate is passed, and
        // _filePicker.SaveAsync() is reached (returns null → early return)
        Assert.True(filePicker.SaveAsyncWasCalled);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-MA-02: ExportSecrets — the password path never reaches FilePicker
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportSecrets_PasswordPath_UnlockReturnsFalse_FilePickerNotReached()
    {
        var filePicker = new TrackingFilePicker();
        var pwBuf = new SecureCharBuffer();
        pwBuf.SetFromSpan("testpw".AsSpan());

        var vm = BuildVm(
            dialog: new StubDialogService
            {
                ConfirmAsyncResult     = true,
                ConfirmMasterAuthResult = new MasterAuthResult(pwBuf),
            },
            filePicker: filePicker,
            auth: new StubAuthService { UnlockAsyncResult = false });

        await vm.ExportSecretsCommand.ExecuteAsync(null);

        // Password path: UnlockAsync returns false → verified=false →
        // _notification.Show(ErrorAuthFailed) → return (FilePicker is never reached)
        Assert.False(filePicker.SaveAsyncWasCalled);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-MA-03: ExportSecrets — when auth is cancelled, FilePicker is never reached
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportSecrets_AuthCancelledNull_FilePickerNotReached()
    {
        var filePicker = new TrackingFilePicker();
        var vm = BuildVm(
            dialog: new StubDialogService
            {
                ConfirmAsyncResult     = true,
                ConfirmMasterAuthResult = null, // user cancelled
            },
            filePicker: filePicker,
            auth: new StubAuthService());

        await vm.ExportSecretsCommand.ExecuteAsync(null);

        Assert.False(filePicker.SaveAsyncWasCalled);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-MA-04: CreateEac — the Hello path reaches EAC issuance processing
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateEac_HelloVerified_AuthGatePassedEacGenerationAttempted()
    {
        var vm = BuildVm(
            dialog: new StubDialogService
            {
                ConfirmMasterAuthResult = MasterAuthResult.FromHello(),
            },
            auth: new StubAuthService());
        // Set the PIN to 4 ASCII digits (a condition for passing validation).
        // Because ZeroStringInternals zeroes out the string's internal buffer,
        // EacPin and EacPinConfirm must be passed separate instances (using the same
        // literal would zero the second one out).
        vm.EacPin        = new string(new[] { '1', '2', '3', '4' });
        vm.EacPinConfirm = new string(new[] { '1', '2', '3', '4' });

        await vm.CreateEmergencyAccessCodeCommand.ExecuteAsync(null);

        // Hello path: the auth gate is passed and _auth.GenerateEmergencyAccessCodeAsync() is reached.
        // StubAuthService throws NotImplementedException → caught → EmergencyAccessCodeErrorMessage is set
        Assert.NotNull(vm.EmergencyAccessCodeErrorMessage);
        Assert.False(vm.HasEmergencyAccessCode); // issuance did not actually complete
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-MA-05: CreateEac — EAC issuance is not attempted when auth is cancelled
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateEac_AuthCancelledNull_EacGenerationNotAttempted()
    {
        var vm = BuildVm(
            dialog: new StubDialogService
            {
                ConfirmMasterAuthResult = null, // user cancelled
            },
            auth: new StubAuthService());
        vm.EacPin        = new string(new[] { '1', '2', '3', '4' });
        vm.EacPinConfirm = new string(new[] { '1', '2', '3', '4' });

        await vm.CreateEmergencyAccessCodeCommand.ExecuteAsync(null);

        // Returns before the auth gate → EmergencyAccessCodeErrorMessage stays null
        Assert.Null(vm.EmergencyAccessCodeErrorMessage);
        Assert.False(vm.HasEmergencyAccessCode);
    }
}
