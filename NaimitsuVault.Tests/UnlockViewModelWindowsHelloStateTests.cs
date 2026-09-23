// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;
using Windows.Security.Credentials.UI;

namespace NaimitsuVault.Tests;

/// <summary>
/// UnlockViewModel form state transition synchronization tests (TC-WH-15..18).
/// Verifies ViewModel-layer state changes through UnlockWithWindowsHelloCommand.
/// </summary>
public sealed class UnlockViewModelWindowsHelloStateTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static (TestDb db, AppSession session, UnlockViewModel vm,
                    FakeUserConsentVerifierAdapter adapter)
        CreateVm()
    {
        var db      = TestDb.Create();
        var session = new AppSession();
        var crypto  = new IdentityCryptoService();
        var adapter = new FakeUserConsentVerifierAdapter();
        var dpapi   = new FakeProtectedDataService();
        var auth    = new AuthService(db.Factory, new ExplodingUnifiedDbContextFactory(),
                                      new NullConnectionProvider(), crypto, session,
                                      new StubAuditLogService(),
                                      null!,
                                      NullLogger<AuthService>.Instance);
        auth.SetHelloAdapter(adapter);
        auth.SetProtectedData(dpapi);
        var vm = new UnlockViewModel(auth, isSetupMode: false, session, NullLogger<UnlockViewModel>.Instance);
        return (db, session, vm, adapter);
    }

    // ── TC-WH-15 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockViewModel_Hello_IsBusy_Reset_To_False_After_Failure()
    {
        var (db, _, vm, adapter) = CreateVm();
        using (db)
        {
            adapter.VerificationResult = UserConsentVerificationResult.Canceled;

            await ((IAsyncRelayCommand)vm.UnlockWithWindowsHelloCommand).ExecuteAsync(null);

            Assert.False(vm.IsBusy);
        }
    }

    // ── TC-WH-16 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockViewModel_Hello_ErrorMessage_Set_On_COMException()
    {
        var (db, _, vm, adapter) = CreateVm();
        using (db)
        {
            adapter.VerificationException = new COMException("WinRT COM error", unchecked((int)0x80004001));

            await ((IAsyncRelayCommand)vm.UnlockWithWindowsHelloCommand).ExecuteAsync(null);

            Assert.NotNull(vm.ErrorMessage);
        }
    }

    // ── TC-WH-17 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockViewModel_Hello_IsBusy_Reset_After_Any_Exception()
    {
        var (db, _, vm, adapter) = CreateVm();
        using (db)
        {
            adapter.VerificationException = new Exception("Unexpected exception");

            await ((IAsyncRelayCommand)vm.UnlockWithWindowsHelloCommand).ExecuteAsync(null);

            Assert.False(vm.IsBusy);
        }
    }

    // ── TC-WH-18 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockViewModel_Hello_Succeeded_Event_Fired_Exactly_Once_On_Verified()
    {
        var (db, session, _, adapter) = CreateVm();
        using (db)
        {
            using var unifiedDb = TestUnifiedDb.Create();
            await using var ctx  = db.Factory.CreateDbContext();
            await using var uctx = unifiedDb.Factory.CreateDbContext();

            var dpapi  = new FakeProtectedDataService();
            var crypto = new IdentityCryptoService();
            var auth2  = new AuthService(db.Factory, unifiedDb.Factory,
                                         new NullConnectionProvider(), crypto, session,
                                         new StubAuditLogService(),
                                         null!,
                                         NullLogger<AuthService>.Instance);
            auth2.SetHelloAdapter(adapter);
            auth2.SetProtectedData(dpapi);
            var vm2 = new UnlockViewModel(auth2, isSetupMode: false, session, NullLogger<UnlockViewModel>.Instance);

            // Seed the vault DB with VaultDEKHello(0x1002)
            var dek     = await WinHelloDbSeeder.SeedWrappedDekAsync(ctx);
            // Seed the unified DB with KSharedHello(0x000A) + VaultRegistry[1]
            var kShared = await WinHelloDbSeeder.SeedUnifiedDbForHelloAsync(uctx);
            session.Lock();

            adapter.AvailabilityResult = UserConsentVerifierAvailability.Available;
            adapter.VerificationResult = UserConsentVerificationResult.Verified;

            int succeededCount = 0;
            vm2.Succeeded += (_, _) => succeededCount++;

            await ((IAsyncRelayCommand)vm2.UnlockWithWindowsHelloCommand).ExecuteAsync(null);

            Assert.Equal(1, succeededCount);
            Assert.True(vm2.IsSuccess);
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(kShared);
        }
    }

    // ── TC-WH-24 ─────────────────────────────────────────────────────────
    // Regression coverage for the "silent exclusion" gap: when the Hello scan drops one
    // unrecoverable vault but other vaults are still usable, the picker/auto-select flow must
    // still surface a non-blocking notice about the excluded vault instead of proceeding silently.
    // The successful entry into the remaining vault must not be affected by the notice.

    [Fact]
    public async Task UnlockViewModel_Hello_OneVaultExcluded_ShowsNoticeButOtherVaultStillEnters()
    {
        var session = new AppSession
        {
            VaultDbUnrecoverable       = true,
            VaultDbUnrecoverableNumber = 1,
        };
        var auth = new StubAuthService
        {
            AcquireKSharedWithHelloAsyncResult = [2, 3], // vault 1 was silently dropped by the scan
            EnterVaultWithHelloAsyncResult     = true,
        };
        var vm = new UnlockViewModel(auth, isSetupMode: false, session, NullLogger<UnlockViewModel>.Instance)
        {
            SelectVaultAsync = _ => Task.FromResult<int?>(2), // user picks vault 2 from the picker
        };

        var expectedNotice = string.Format(LocalizationManager.Get("Unlock.ErrorVaultDbCorrupted"), 1);

        await ((IAsyncRelayCommand)vm.UnlockWithWindowsHelloCommand).ExecuteAsync(null);

        Assert.True(vm.IsSuccess, "Entering vault 2 must still succeed despite the excluded vault");
        Assert.Equal(expectedNotice, vm.ErrorMessage);
    }

    // ── TC-WH-25 ─────────────────────────────────────────────────────────
    // Regression coverage for a second silent-exclusion path: a vault can be auto-recovered from a
    // shadow that predates when Hello was enabled on it (or otherwise lacks VaultDEKHello), so the
    // restored file legitimately has no Hello registration and is excluded - without ever hitting the
    // VaultDbUnrecoverable branch. This must also surface a non-blocking notice.

    [Fact]
    public async Task UnlockViewModel_Hello_OneVaultAutoRecoveredButStillExcluded_ShowsInfoNotice()
    {
        var session = new AppSession
        {
            VaultDbAutoRecovered       = true,
            VaultDbAutoRecoveredNumber = 1,
        };
        var auth = new StubAuthService
        {
            AcquireKSharedWithHelloAsyncResult = [2, 3], // vault 1 recovered but no longer has Hello configured
            EnterVaultWithHelloAsyncResult     = true,
        };
        var vm = new UnlockViewModel(auth, isSetupMode: false, session, NullLogger<UnlockViewModel>.Instance)
        {
            SelectVaultAsync = _ => Task.FromResult<int?>(2),
        };

        var expectedNotice = string.Format(LocalizationManager.Get("Common.InfoVaultDbAutoRecovered"), 1);

        await ((IAsyncRelayCommand)vm.UnlockWithWindowsHelloCommand).ExecuteAsync(null);

        Assert.True(vm.IsSuccess, "Entering vault 2 must still succeed despite the excluded vault");
        Assert.Equal(expectedNotice, vm.ErrorMessage);
    }
}
