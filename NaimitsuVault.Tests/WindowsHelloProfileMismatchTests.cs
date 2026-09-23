// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;

namespace NaimitsuVault.Tests;

/// <summary>
/// Verifies that a DPAPI decrypt failure occurring *after* successful biometric verification
/// (e.g. the vault was moved to a different PC/Windows account) is surfaced as
/// WindowsHelloProfileMismatchException instead of being swallowed into a generic false/[] result.
/// This lets the UI distinguish "wrong device/account" from "biometric canceled/failed" and offer
/// to clear the stale Hello registration rather than just prompting the user to retry forever.
///
/// The K_shared-side equivalent of this scenario is covered by TC-WH-12
/// (WindowsHelloDekMemoryTests.UnlockHello_DPAPIThrows_CryptographicException_ThrowsProfileMismatch);
/// this file covers the vault_DEK side (EnterVaultWithHelloAsync, stage 2 of the two-stage flow),
/// which has no historical TC-WH coverage of its own.
/// </summary>
public sealed class WindowsHelloProfileMismatchTests
{
    // ── TC-WH-19 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task EnterVaultWithHello_DpapiThrowsCryptographicException_ThrowsProfileMismatch()
    {
        using var vaultDb   = TestDb.Create();
        using var unifiedDb = TestUnifiedDb.Create();
        var session = new AppSession();
        var dpapi   = new FakeProtectedDataService();

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

        // EnterVaultWithHelloAsync's precondition: K_shared must already be in the session
        // (stage 1 of the two-stage flow already succeeded).
        var dummyKShared = new byte[32];
        RandomNumberGenerator.Fill(dummyKShared);
        session.SetKShared(dummyKShared);
        CryptographicOperations.ZeroMemory(dummyKShared);

        var auth = new AuthService(
            vaultDb.Factory,
            unifiedDb.Factory,
            new NullConnectionProvider(),
            new IdentityCryptoService(),
            session,
            new StubAuditLogService(),
            null!,
            NullLogger<AuthService>.Instance);
        auth.SetProtectedData(dpapi);

        dpapi.UnprotectException = new CryptographicException("DPAPI failure: wrong device/account");

        await Assert.ThrowsAsync<WindowsHelloProfileMismatchException>(
            () => auth.EnterVaultWithHelloAsync(1, "unused"));

        Assert.False(session.IsUnlocked);
    }

    // ── TC-WH-20 ─────────────────────────────────────────────────────────
    // Regression coverage: AuthSucceeded (0x4001) was defined in AuditEventCode with a matching
    // "no payload" contract but never actually wired to LogAsync from any call site (neither the
    // password path nor this Hello path) - only AuthFailed was ever recorded.

    [Fact]
    public async Task EnterVaultWithHello_Success_LogsAuthSucceeded()
    {
        using var vaultDb   = TestDb.Create();
        using var unifiedDb = TestUnifiedDb.Create();
        var session = new AppSession();
        var dpapi   = new FakeProtectedDataService();

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

        var dummyKShared = new byte[32];
        RandomNumberGenerator.Fill(dummyKShared);
        session.SetKShared(dummyKShared);
        CryptographicOperations.ZeroMemory(dummyKShared);

        var auditLog = new StubAuditLogService();
        var auth = new AuthService(
            vaultDb.Factory,
            unifiedDb.Factory,
            new NullConnectionProvider(),
            new IdentityCryptoService(),
            session,
            auditLog,
            null!,
            NullLogger<AuthService>.Instance);
        auth.SetProtectedData(dpapi);

        var result = await auth.EnterVaultWithHelloAsync(1, "unused");

        Assert.True(result);
        Assert.True(session.IsUnlocked);
        Assert.Contains(auditLog.Calls, c => c.Code == AuditEventCode.AuthSucceeded);
    }
}
