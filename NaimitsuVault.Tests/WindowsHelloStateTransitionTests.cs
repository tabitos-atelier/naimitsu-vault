// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;
using Windows.Security.Credentials.UI;

namespace NaimitsuVault.Tests;

/// <summary>
/// Exhaustive test of all UserConsentVerificationResult patterns (TC-WH-01..10), against
/// AcquireKSharedWithHelloAsync — the method the Unlock screen actually calls (stage 1 of the
/// two-stage multi-vault Hello flow). Uses FakeUserConsentVerifierAdapter / FakeProtectedDataService
/// to fully eliminate WinRT dependencies.
/// </summary>
public sealed class WindowsHelloStateTransitionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// K_shared is restored before any per-vault DB access, so tests that fail at or before the
    /// biometric step never touch the vault DB. Pass a real vaultFactory only for tests that need
    /// to reach the per-vault registry loop (TC-WH-01); everything else defaults to
    /// FaultInjectionDbContextFactory, which structurally proves "the vault DB was never touched."
    /// </summary>
    private static (TestUnifiedDb unifiedDb, AppSession session, AuthService auth,
                    FakeUserConsentVerifierAdapter adapter, FakeProtectedDataService dpapi)
        CreateAuth(IDbContextFactory<AppDbContext>? vaultFactory = null)
    {
        var unifiedDb = TestUnifiedDb.Create();
        var session   = new AppSession();
        var crypto    = new IdentityCryptoService();
        var adapter   = new FakeUserConsentVerifierAdapter();
        var dpapi     = new FakeProtectedDataService();
        var auth = new AuthService(
            vaultFactory ?? new FaultInjectionDbContextFactory(
                new InvalidOperationException("the vault DB must not be touched before K_shared is restored")),
            unifiedDb.Factory,
            new NullConnectionProvider(), crypto, session,
            new StubAuditLogService(),
            null!,
            NullLogger<AuthService>.Instance);
        auth.SetHelloAdapter(adapter);
        auth.SetProtectedData(dpapi);
        return (unifiedDb, session, auth, adapter, dpapi);
    }

    // ── TC-WH-01 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_Verified_Returns_True_Session_Is_Unlocked()
    {
        using var vaultDb = TestDb.Create();
        var (unifiedDb, session, auth, adapter, _) = CreateAuth(vaultDb.Factory);
        using (unifiedDb)
        {
            // Pre-seed a valid KSharedHello + VaultRegistry, and a valid VaultDEKHello in the vault DB
            await using (var uctx = unifiedDb.Factory.CreateDbContext())
            {
                var kShared = await WinHelloDbSeeder.SeedUnifiedDbForHelloAsync(uctx);
                CryptographicOperations.ZeroMemory(kShared);
            }
            await using (var vctx = vaultDb.Factory.CreateDbContext())
            {
                var dek = await WinHelloDbSeeder.SeedWrappedDekAsync(vctx);
                CryptographicOperations.ZeroMemory(dek);
            }

            adapter.AvailabilityResult  = UserConsentVerifierAvailability.Available;
            adapter.VerificationResult  = UserConsentVerificationResult.Verified;

            Assert.False(session.HasKShared);

            var result = await auth.AcquireKSharedWithHelloAsync();

            Assert.NotEmpty(result);
            Assert.True(session.HasKShared);
        }
    }

    // ── TC-WH-02 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_UserCanceled_Returns_False_NoThrow()
    {
        var (unifiedDb, session, auth, adapter, _) = CreateAuth();
        using (unifiedDb)
        {
            adapter.VerificationResult = UserConsentVerificationResult.Canceled;

            var result = await auth.AcquireKSharedWithHelloAsync();

            Assert.Empty(result);
            Assert.False(session.HasKShared);
        }
    }

    // ── TC-WH-03 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_RetriesExceeded_Returns_False_NoThrow()
    {
        var (unifiedDb, _, auth, adapter, _) = CreateAuth();
        using (unifiedDb)
        {
            adapter.VerificationResult = UserConsentVerificationResult.RetriesExhausted;
            Assert.Empty(await auth.AcquireKSharedWithHelloAsync());
        }
    }

    // ── TC-WH-04 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_DisabledByPolicy_Returns_False_NoThrow()
    {
        var (unifiedDb, _, auth, adapter, _) = CreateAuth();
        using (unifiedDb)
        {
            adapter.VerificationResult = UserConsentVerificationResult.DisabledByPolicy;
            Assert.Empty(await auth.AcquireKSharedWithHelloAsync());
        }
    }

    // ── TC-WH-05 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_NotConfiguredForUser_Returns_False_NoThrow()
    {
        var (unifiedDb, _, auth, adapter, _) = CreateAuth();
        using (unifiedDb)
        {
            adapter.VerificationResult = UserConsentVerificationResult.NotConfiguredForUser;
            Assert.Empty(await auth.AcquireKSharedWithHelloAsync());
        }
    }

    // ── TC-WH-06 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_DeviceNotPresent_Returns_False_NoThrow()
    {
        var (unifiedDb, _, auth, adapter, _) = CreateAuth();
        using (unifiedDb)
        {
            adapter.VerificationResult = UserConsentVerificationResult.DeviceNotPresent;
            Assert.Empty(await auth.AcquireKSharedWithHelloAsync());
        }
    }

    // ── TC-WH-07 ─────────────────────────────────────────────────────────

    public static IEnumerable<object[]> NonVerifiedVariants()
    {
        yield return [UserConsentVerificationResult.Canceled];
        yield return [UserConsentVerificationResult.RetriesExhausted];
        yield return [UserConsentVerificationResult.DisabledByPolicy];
        yield return [UserConsentVerificationResult.NotConfiguredForUser];
        yield return [UserConsentVerificationResult.DeviceNotPresent];
        yield return [UserConsentVerificationResult.DeviceBusy];
    }

    [Theory]
    [MemberData(nameof(NonVerifiedVariants))]
    public async Task UnlockHello_AllNonVerifiedVariants_Parameterized_ReturnFalse(
        UserConsentVerificationResult variant)
    {
        var (unifiedDb, session, auth, adapter, _) = CreateAuth();
        using (unifiedDb)
        {
            adapter.VerificationResult = variant;
            var result = await auth.AcquireKSharedWithHelloAsync();
            Assert.Empty(result);
            Assert.False(session.HasKShared);
        }
    }

    // ── TC-WH-08 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_CheckAvailability_NotAvailable_Returns_False_Prompt_Never_Shown()
    {
        var (unifiedDb, session, auth, adapter, _) = CreateAuth();
        using (unifiedDb)
        {
            adapter.AvailabilityResult = UserConsentVerifierAvailability.DeviceNotPresent;
            adapter.VerificationException =
                new InvalidOperationException("Prompt must not be called");
            // Setting VerificationException causes an exception to be thrown if it is called

            // However, since AvailabilityResult != Available, RequestVerificationAsync should not be called
            // (no exception = proof it was not called)
            var ex = await Record.ExceptionAsync(() => auth.AcquireKSharedWithHelloAsync());

            Assert.Null(ex);
            Assert.False(session.HasKShared);
        }
    }

    // ── TC-WH-09 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_RequestVerification_Throws_COMException_Returns_False_NoLeak()
    {
        var (unifiedDb, session, auth, adapter, _) = CreateAuth();
        using (unifiedDb)
        {
            adapter.VerificationException = new COMException("WinRT COM boundary failure", unchecked((int)0x80004001));

            var ex = await Record.ExceptionAsync(() => auth.AcquireKSharedWithHelloAsync());

            Assert.Null(ex); // no exception leak
            Assert.False(session.HasKShared);
        }
    }

    // ── TC-WH-10 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_RequestVerification_Throws_TaskCanceledException_Returns_False_NoLeak()
    {
        var (unifiedDb, session, auth, adapter, _) = CreateAuth();
        using (unifiedDb)
        {
            adapter.VerificationException = new TaskCanceledException("Simulated session timeout");

            var ex = await Record.ExceptionAsync(() => auth.AcquireKSharedWithHelloAsync());

            Assert.Null(ex); // no exception leak
            Assert.False(session.HasKShared);
        }
    }

    // ── TC-WH-21 ─────────────────────────────────────────────────────────
    // Regression coverage for the 2026-08-28 fix: a single broken/corrupted vault DB used to abort the
    // entire per-vault Hello scan (one shared try/catch around the whole loop), silently reporting
    // "zero Hello-registered vaults" even when other, unrelated vaults were perfectly fine. The scan
    // must now isolate the fault to just that one vault and still return the healthy ones.

    [Fact]
    public async Task UnlockHello_OneVaultCorrupted_OtherVaultStillReturned()
    {
        using var healthyVaultDb = TestDb.Create();
        var unifiedDb = TestUnifiedDb.Create();
        using (unifiedDb)
        {
            var session         = new AppSession();
            var crypto          = new IdentityCryptoService();
            var adapter         = new FakeUserConsentVerifierAdapter();
            var dpapi           = new FakeProtectedDataService();
            var connProvider    = new RecordingConnectionProvider();
            var dataDir         = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

            // Vault 1 (all-zero FileHash, seeded by SeedUnifiedDbForHelloAsync) is healthy and
            // Hello-registered. Vault 2 (all-0x01 FileHash) resolves to a path the routing factory
            // treats as broken (simulates a corrupted/unreadable vault DB file).
            byte[] kShared;
            await using (var uctx = unifiedDb.Factory.CreateDbContext())
            {
                kShared = await WinHelloDbSeeder.SeedUnifiedDbForHelloAsync(uctx);
                await WinHelloDbSeeder.SeedAdditionalVaultRegistryAsync(uctx, dbNumber: 2, fileHashFill: 0x01);
            }
            CryptographicOperations.ZeroMemory(kShared);

            await using (var vctx = healthyVaultDb.Factory.CreateDbContext())
            {
                var dek = await WinHelloDbSeeder.SeedWrappedDekAsync(vctx);
                CryptographicOperations.ZeroMemory(dek);
            }

            var healthyPath = AuthService.ComputeVaultDbPath(new byte[32], dataDir);
            var routingFactory = new RoutingDbContextFactory(
                connProvider, healthyPath, healthyVaultDb.Factory,
                () => new InvalidOperationException("simulated corrupted vault DB"));

            var auth = new AuthService(
                routingFactory, unifiedDb.Factory, connProvider, crypto, session,
                new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);
            auth.SetHelloAdapter(adapter);
            auth.SetProtectedData(dpapi);

            adapter.AvailabilityResult = UserConsentVerifierAvailability.Available;
            adapter.VerificationResult = UserConsentVerificationResult.Verified;

            List<int> result = [];
            var ex = await Record.ExceptionAsync(async () => result = await auth.AcquireKSharedWithHelloAsync());

            Assert.Null(ex); // the fault in vault 2 must not propagate out of the method
            Assert.Contains(1, result); // vault 1 (healthy) must still be reported
            Assert.DoesNotContain(2, result); // vault 2 (broken) must be excluded, not crash the scan
        }
    }
}

// ── TC-WH-21 support: routes CreateDbContextAsync by the connection provider's currently-active
// vault path, so a single AuthService instance can behave differently per vault in the loop ──
internal sealed class RoutingDbContextFactory(
    RecordingConnectionProvider connectionProvider,
    string healthyPath,
    IDbContextFactory<AppDbContext> healthyFactory,
    Func<Exception> faultFactory) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext()
        => connectionProvider.ActiveVaultDbPath == healthyPath
            ? healthyFactory.CreateDbContext()
            : throw faultFactory();

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => connectionProvider.ActiveVaultDbPath == healthyPath
            ? healthyFactory.CreateDbContextAsync(cancellationToken)
            : Task.FromException<AppDbContext>(faultFactory());
}
