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
/// Tests focused on the password generator (TC-PGS-01, TC-PGS-03).
/// Verifies the EditingSecret strength property and call count after pressing the generate button.
/// </summary>
[Collection("SequentialMessenger")]
public sealed class PasswordGeneratorStrengthTests
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

    private static (TestDb db, SecretsViewModel vm, StubPasswordEvaluationService evaluator)
        BuildVm()
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

        var vm = new SecretsViewModel(
            secrets, files, NullCrypto, session, profile,
            notif, dialog, favicon, history, drafts, guard, registry, disp, evaluator,
            new StubAuditLogService(), new TestableAutoBackupService("unused", "unused"), NullLogger<SecretsViewModel>.Instance);

        return (db, vm, evaluator);
    }

    // ── TC-PGS-01 ──────────────────────────────────────────────────────────────
    // After executing GeneratePasswordCommand, EditingSecret.PasswordStrengthLevel becomes 0-4

    [Fact]
    public void TC_PGS_01_AfterGenerate_EditingSecretStrengthLevelIsInRange()
    {
        var (db, vm, _) = BuildVm();
        using (db)
        using (vm)
        {
            vm.EditingSecret = new SecretEditModel();

            vm.GeneratePasswordCommand.Execute(null);

            Assert.NotNull(vm.EditingSecret);
            Assert.InRange(vm.EditingSecret.PasswordStrengthLevel, 0, 4);
        }
    }

    // ── TC-PGS-03 ──────────────────────────────────────────────────────────────
    // After executing GeneratePasswordCommand, EvaluateStrength is called exactly once

    [Fact]
    public void TC_PGS_03_GeneratePassword_CallsEvaluateStrength_ExactlyOnce()
    {
        var (db, vm, evaluator) = BuildVm();
        using (db)
        using (vm)
        {
            vm.EditingSecret = new SecretEditModel();

            vm.GeneratePasswordCommand.Execute(null);

            Assert.Equal(1, evaluator.CallCount);
        }
    }
}
