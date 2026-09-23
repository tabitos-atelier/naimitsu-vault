// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;

namespace NaimitsuVault.Tests;

/// <summary>
/// Deniability defense: the "instant reject on fewer than 8 characters" gate at the
/// unlock front line (TC-MVG-01..07).
///
/// Verifies that input shorter than 8 characters never triggers coarse_hash computation,
/// DB access, or Argon2id, and instead returns the same result as an ordinary auth failure.
/// Test method: inject an "exploding factory" and prove the gate's existence from both
/// sides — no exception is raised for < 8 characters, while DB access is attempted at
/// exactly 8 characters.
/// </summary>
public sealed class MultiVaultAuthGateTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────

    private static AuthService MakeAuth()
        => new(
            new FaultInjectionDbContextFactory(
                new InvalidOperationException("vault DB accessed — gate should have blocked this")),
            new ExplodingUnifiedDbContextFactory(),
            new NullConnectionProvider(),
            new IdentityCryptoService(),
            new AppSession(),
            new StubAuditLogService(),
            null!,
            NullLogger<AuthService>.Instance);

    // ── TC-MVG-01 ─────────────────────────────────────────────────────────────
    // AcquireKSharedAsync: 7 characters → returns an empty list, never touches the unified DB

    [Fact]
    public async Task AcquireKShared_7Chars_ReturnsEmptyListWithoutDbAccess()
    {
        var auth   = MakeAuth();
        var result = await auth.AcquireKSharedAsync("1234567", TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    // ── TC-MVG-02 ─────────────────────────────────────────────────────────────
    // AcquireKSharedAsync: 1 character → empty list

    [Fact]
    public async Task AcquireKShared_1Char_ReturnsEmptyListWithoutDbAccess()
    {
        var auth   = MakeAuth();
        var result = await auth.AcquireKSharedAsync("x", TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    // ── TC-MVG-03 ─────────────────────────────────────────────────────────────
    // AcquireKSharedAsync: empty string → empty list

    [Fact]
    public async Task AcquireKShared_Empty_ReturnsEmptyListWithoutDbAccess()
    {
        var auth   = MakeAuth();
        var result = await auth.AcquireKSharedAsync(string.Empty, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    // ── TC-MVG-04 ─────────────────────────────────────────────────────────────
    // AcquireKSharedAsync: 8 characters → passes the gate → attempts unified DB access
    // (proven by the exploding factory)

    [Fact]
    public async Task AcquireKShared_8Chars_PassesGateAndAttemptsDbAccess()
    {
        var auth = MakeAuth();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => auth.AcquireKSharedAsync("12345678", TestContext.Current.CancellationToken));
    }

    // ── TC-MVG-05 ─────────────────────────────────────────────────────────────
    // UnlockAsync: 7 characters → returns false, never touches the secrets DB

    [Fact]
    public async Task UnlockAsync_7Chars_ReturnsFalseWithoutDbAccess()
    {
        var auth   = MakeAuth();
        var result = await auth.UnlockAsync("1234567");
        Assert.False(result);
    }

    // ── TC-MVG-06 ─────────────────────────────────────────────────────────────
    // UnlockAsync: empty string → false

    [Fact]
    public async Task UnlockAsync_Empty_ReturnsFalseWithoutDbAccess()
    {
        var auth   = MakeAuth();
        var result = await auth.UnlockAsync(string.Empty);
        Assert.False(result);
    }

    // ── TC-MVG-07 ─────────────────────────────────────────────────────────────
    // UnlockAsync: 8 characters → passes the gate → attempts secrets DB access
    // (proven by the exploding factory)

    [Fact]
    public async Task UnlockAsync_8Chars_PassesGateAndAttemptsDbAccess()
    {
        var auth = MakeAuth();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => auth.UnlockAsync("12345678"));
    }

}
