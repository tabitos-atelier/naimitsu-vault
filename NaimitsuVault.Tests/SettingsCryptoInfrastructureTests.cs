// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;

namespace NaimitsuVault.Tests;

/// <summary>
/// Verifies the infrastructure for adding a new vault (TC-SCI-01 .. TC-SCI-04).
///
/// Verifies that AuthService.CreateNewVaultAsync retrieves K_shared from the session.
/// Confirms that when K_shared is unset, it returns false and never triggers Argon2id or DB writes.
/// </summary>
public sealed class SettingsCryptoInfrastructureTests
{
    private static AuthService MakeAuth(AppSession? session = null)
        => new(
            new FaultInjectionDbContextFactory(
                new InvalidOperationException("vault DB accessed — K_shared gate should have blocked this")),
            new ExplodingUnifiedDbContextFactory(),
            new NullConnectionProvider(),
            new IdentityCryptoService(),
            session ?? new AppSession(),
            new StubAuditLogService(),
            null!,
            NullLogger<AuthService>.Instance);

    // ── TC-SCI-01 ────────────────────────────────────────────────────────────
    // CreateNewVaultAsync: K_shared unset -> returns false, no DB access

    [Fact]
    public async Task CreateNewVault_WithoutKShared_ReturnsFalseWithoutDbAccess()
    {
        var session = new AppSession();   // HasKShared == false
        var auth = MakeAuth(session);

        var result = await auth.CreateNewVaultAsync(1, "mypassword".AsSpan(), "/tmp/data");

        Assert.False(result, "Must return false when K_shared is unset.");
    }

    // ── TC-SCI-02 ────────────────────────────────────────────────────────────
    // CreateNewVaultAsync: K_shared set, vault number 0 -> ArgumentOutOfRangeException after passing the K_shared gate

    [Fact]
    public async Task CreateNewVault_InvalidVaultNumber_WithKShared_ThrowsArgumentOutOfRange()
    {
        var session = new AppSession();
        session.SetKShared(new byte[32]);
        var auth = MakeAuth(session);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => auth.CreateNewVaultAsync(0, "mypassword".AsSpan(), "/tmp/data"));
    }

    // ── TC-SCI-03 ────────────────────────────────────────────────────────────
    // CreateNewVaultAsync: K_shared set, vault number 10 -> ArgumentOutOfRangeException after passing the K_shared gate

    [Fact]
    public async Task CreateNewVault_VaultNumberAbove9_WithKShared_ThrowsArgumentOutOfRange()
    {
        var session = new AppSession();
        session.SetKShared(new byte[32]);
        var auth = MakeAuth(session);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => auth.CreateNewVaultAsync(10, "mypassword".AsSpan(), "/tmp/data"));
    }

    // ── TC-SCI-04 ────────────────────────────────────────────────────────────
    // Whether K_shared is set can be checked via the HasKShared property (precondition check for AddVaultCommand)

    [Fact]
    public void AppSession_HasKShared_IsTrueAfterSetKShared()
    {
        var session = new AppSession();
        Assert.False(session.HasKShared, "K_shared is unset in the initial state.");

        session.SetKShared(new byte[32]);
        Assert.True(session.HasKShared, "HasKShared is true after SetKShared.");
    }
}
