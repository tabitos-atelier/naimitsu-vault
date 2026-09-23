// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;
using Windows.Security.Credentials.UI;

namespace NaimitsuVault.Tests;

/// <summary>
/// K_shared memory contamination prevention and ZeroMemory guarantee tests (TC-WH-11..14), against
/// AcquireKSharedWithHelloAsync — the method the Unlock screen actually calls.
/// Uses FakeProtectedDataService's LastUnprotectedBytes to verify ZeroMemory of the buffer
/// returned by Unprotect (i.e. the actual kSharedRaw).
/// </summary>
public sealed class WindowsHelloDekMemoryTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

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

    // ── TC-WH-11 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_Failure_Session_Remains_Locked_DEK_Not_Loaded()
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

    // ── TC-WH-12 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_DPAPIThrows_CryptographicException_ThrowsProfileMismatch()
    {
        // Unprotect() throws, so kSharedRaw is never allocated.
        // finally { if (kSharedRaw != null) ZeroMemory(kSharedRaw) } is a no-op since it's null.
        // The test confirms no memory residue (stays null) via the thrown exception plus the
        // session remaining without K_shared.
        var (unifiedDb, session, auth, adapter, dpapi) = CreateAuth();
        using (unifiedDb)
        {
            await using (var uctx = unifiedDb.Factory.CreateDbContext())
            {
                var kShared = await WinHelloDbSeeder.SeedUnifiedDbForHelloAsync(uctx);
                CryptographicOperations.ZeroMemory(kShared);
            }

            adapter.AvailabilityResult  = UserConsentVerifierAvailability.Available;
            adapter.VerificationResult  = UserConsentVerificationResult.Verified;
            dpapi.UnprotectException    = new CryptographicException("DPAPI failure");

            await Assert.ThrowsAsync<WindowsHelloProfileMismatchException>(
                () => auth.AcquireKSharedWithHelloAsync());

            Assert.False(session.HasKShared);
            // dpapi.LastUnprotectedBytes is null (was never returned before the exception)
            Assert.Null(dpapi.LastUnprotectedBytes);
        }
    }

    // ── TC-WH-13 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_Success_ProtectedKeyBuf_IsZeroed_After_Unprotect()
    {
        // FakeProtectedDataService.LastUnprotectedBytes is the byte[] returned by Unprotect (i.e. the actual kSharedRaw).
        // Production code ZeroMemory's kSharedRaw and then sets kSharedRaw = null.
        // Since LastUnprotectedBytes holds a strong reference to that same array, it should be all-zero after ZeroMemory.
        using var vaultDb = TestDb.Create();
        var (unifiedDb, session, auth, adapter, dpapi) = CreateAuth(vaultDb.Factory);
        using (unifiedDb)
        {
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

            adapter.AvailabilityResult = UserConsentVerifierAvailability.Available;
            adapter.VerificationResult = UserConsentVerificationResult.Verified;

            var result = await auth.AcquireKSharedWithHelloAsync();

            Assert.NotEmpty(result);
            Assert.True(session.HasKShared);

            var returnedBuf = dpapi.LastUnprotectedBytes;
            Assert.NotNull(returnedBuf);
            // kSharedRaw should already be ZeroMemory'd
            Assert.True(returnedBuf.All(b => b == 0),
                "The kSharedRaw buffer returned by Unprotect was not ZeroMemory'd");
        }
    }

    // ── TC-WH-14 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockHello_Success_SessionUnlockedAndRelockable()
    {
        // ZeroMemory of kSharedPinned itself (a pinned local variable inside
        // AuthService.AcquireKSharedWithHelloAsync that re-copies kSharedRaw into GC.AllocateArray)
        // cannot be verified mechanically. TC-WH-13's LastUnprotectedBytes trick captures
        // IProtectedDataService.Unprotect's return value (kSharedRaw); kSharedPinned is a separate
        // buffer that internally copies that return value and never passes through any interface
        // visible outside AuthService, so there is no path for a test double to capture it. The
        // policy here is that ZeroMemory's placement inside finally { ... } has already been
        // confirmed via code audit, and this test verifies only the side effect of the SetKShared
        // call (the session state transition).
        using var vaultDb = TestDb.Create();
        var (unifiedDb, session, auth, adapter, dpapi) = CreateAuth(vaultDb.Factory);
        using (unifiedDb)
        {
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

            adapter.AvailabilityResult = UserConsentVerifierAvailability.Available;
            adapter.VerificationResult = UserConsentVerificationResult.Verified;

            var result = await auth.AcquireKSharedWithHelloAsync();

            Assert.NotEmpty(result);
            Assert.True(session.HasKShared, "SetKShared should have been called, unlocking K_shared");

            // Re-lock the session to confirm the K_shared lifecycle's consistency
            session.Lock();
            Assert.False(session.HasKShared);
        }
    }
}
