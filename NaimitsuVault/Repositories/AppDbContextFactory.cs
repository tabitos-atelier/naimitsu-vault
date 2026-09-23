// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Repositories;

/// <summary>
/// Custom implementation of IDbContextFactory&lt;AppDbContext&gt;.
/// A singleton factory that dynamically retrieves the active vault DB path from
/// IVaultConnectionProvider and creates AppDbContext instances.
/// </summary>
public sealed class AppDbContextFactory(
    IVaultConnectionProvider connectionProvider,
    ISessionGenerationGuard sessionGuard)
    : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext()
    {
        var path = connectionProvider.ActiveVaultDbPath
            ?? throw new InvalidOperationException(
                "No active vault DB is set. Call this only after unlocking.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .AddInterceptors(new SqliteSynchronousInterceptor())
            .Options;

        return new AppDbContext(options, sessionGuard);
    }

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
