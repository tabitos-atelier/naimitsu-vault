// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;

namespace NaimitsuVault.Tests;

/// <summary>
/// Unit tests for AuthService.IsWindowsHelloConfiguredForUnlockAsync().
///
/// The key design point of this method is that it must be callable even before unlock
/// (i.e., before a vault DB path is configured). By passing FaultInjectionDbContextFactory
/// as the vault DB factory, a passing test structurally guarantees that "the vault DB is
/// never touched."
/// </summary>
public sealed class IsWindowsHelloConfiguredForUnlockTests
{
    private static AuthService CreateAuth(IDbContextFactory<UnifiedDbContext> unifiedFactory)
        => new(
            new FaultInjectionDbContextFactory(
                new InvalidOperationException("the vault DB must not be touched pre-unlock")),
            unifiedFactory,
            new NullConnectionProvider(),
            new IdentityCryptoService(),
            new AppSession(),
            new StubAuditLogService(),
            null!,
            NullLogger<AuthService>.Instance);

    [Fact]
    public async Task WhenKSharedHelloPresentInUnifiedDb_ReturnsTrue()
    {
        using var db = TestUnifiedDb.Create();
        await using (var ctx = db.Factory.CreateDbContext())
        {
            ctx.Metadata.Add(new UnifiedMetadata
            {
                ConfigKey   = UnifiedMetadataKey.KSharedHello,
                ConfigValue = [1, 2, 3],  // only checks presence; the value itself doesn't matter
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(await CreateAuth(db.Factory).IsWindowsHelloConfiguredForUnlockAsync());
    }

    [Fact]
    public async Task WhenKSharedHelloAbsent_ReturnsFalse()
    {
        using var db = TestUnifiedDb.Create();
        Assert.False(await CreateAuth(db.Factory).IsWindowsHelloConfiguredForUnlockAsync());
    }
}
