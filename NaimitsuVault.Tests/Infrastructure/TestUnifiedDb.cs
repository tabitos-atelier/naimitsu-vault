// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Repositories;

namespace NaimitsuVault.Tests;

/// <summary>
/// An in-memory unified DB for testing (UnifiedDbContext version).
/// Generates a GUID-unique DB name to prevent interference between parallel tests.
/// </summary>
internal sealed class TestUnifiedDb : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    public IDbContextFactory<UnifiedDbContext> Factory { get; }

    private TestUnifiedDb(IDbContextFactory<UnifiedDbContext> factory, SqliteConnection keepAlive)
    {
        Factory    = factory;
        _keepAlive = keepAlive;
    }

    internal static TestUnifiedDb Create()
    {
        var dbName  = $"naimitsu_unified_test_{Guid.NewGuid():N}";
        var connStr = $"Data Source={dbName};Mode=Memory;Cache=Shared;Foreign Keys=True";

        var keepAlive = new SqliteConnection(connStr);
        keepAlive.Open();

        var options = new DbContextOptionsBuilder<UnifiedDbContext>()
            .UseSqlite(connStr)
            .Options;

        using (var ctx = new UnifiedDbContext(options))
            ctx.Database.EnsureCreated();

        return new TestUnifiedDb(new SharedOptionsUnifiedDbContextFactory(options), keepAlive);
    }

    public void Dispose() => _keepAlive.Dispose();
}

internal sealed class SharedOptionsUnifiedDbContextFactory(DbContextOptions<UnifiedDbContext> options)
    : IDbContextFactory<UnifiedDbContext>
{
    public UnifiedDbContext CreateDbContext()
        => new(options);

    public Task<UnifiedDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
