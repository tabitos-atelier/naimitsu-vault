// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// An in-memory SQLite database for testing.
/// Generates a GUID-unique DB name to structurally prevent interference between parallel
/// tests. The named in-memory DB persists only while the keepAlive connection is alive.
/// </summary>
internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    public IDbContextFactory<AppDbContext> Factory { get; }

    private TestDb(IDbContextFactory<AppDbContext> factory, SqliteConnection keepAlive)
    {
        Factory    = factory;
        _keepAlive = keepAlive;
    }

    /// <summary>
    /// Generates a unique in-memory DB, establishes its schema, and returns it.
    /// Always use it as <c>using var db = TestDb.Create();</c>.
    /// </summary>
    internal static TestDb Create()
    {
        var dbName  = $"naimitsu_test_{Guid.NewGuid():N}";
        var connStr = $"Data Source={dbName};Mode=Memory;Cache=Shared;Foreign Keys=True";

        // The named in-memory DB persists within the process as long as keepAlive stays open
        var keepAlive = new SqliteConnection(connStr);
        keepAlive.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connStr)
            .Options;

        // Establish the schema via EnsureCreated (avoids NotSupportedException since SaveChanges isn't used)
        using (var ctx = new AppDbContext(options, new SessionGenerationGuardStub()))
            ctx.Database.EnsureCreated();

        return new TestDb(new SharedOptionsAppDbContextFactory(options), keepAlive);
    }

    public void Dispose() => _keepAlive.Dispose();
}

/// <summary>
/// A test-only IDbContextFactory implementation.
/// Reuses the same DbContextOptions to connect to the same in-memory DB.
/// </summary>
internal sealed class SharedOptionsAppDbContextFactory(DbContextOptions<AppDbContext> options)
    : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext()
        => new(options, new SessionGenerationGuardStub());

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

/// <summary>
/// A test-only stub that always lets the session-generation check pass.
/// </summary>
internal sealed class SessionGenerationGuardStub : ISessionGenerationGuard
{
    public long CurrentSessionId       => 1L;
    public bool IsBarricaded           => false;
    public void Barricade()            { }
    public void NextSession()          { }
    public void ThrowIfInvalid(long _) { }
}
