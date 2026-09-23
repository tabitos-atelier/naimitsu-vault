// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Direct use of VaultDbContext, and AuditLog.EventLevel column consistency (TC-VDS-01..05).
///
/// Verifies that DatabaseInitializer.InitializeVaultDbAsync generates the schema using
/// VaultDbContext rather than AppDbContext, and that AuditLog.EventLevel's EF Core mapping is
/// fully consistent with the RAW INSERT (InsertAuthFailedRawAsync-style) form.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class VaultDbSchemaTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(Path.GetTempPath(), $"naimitsu_vds_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var f = _tempPath + suffix;
            if (File.Exists(f))
                try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private static DatabaseInitializer MakeInitializer()
        => new(
            new FaultInjectionDbContextFactory(new InvalidOperationException("vault factory not needed")),
            new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(),
            NullLogger<DatabaseInitializer>.Instance);

    private VaultDbContext OpenVaultDb()
    {
        var options = new DbContextOptionsBuilder<VaultDbContext>()
            .UseSqlite($"Data Source={_tempPath};Foreign Keys=True")
            .Options;
        return new VaultDbContext(options, new SessionGenerationGuardStub());
    }

    // ── TC-VDS-01 ─────────────────────────────────────────────────────────────
    // InitializeVaultDbAsync creates the file, and VaultDbContext can connect to and query it

    [Fact]
    public async Task InitializeVaultDbAsync_CreatesFileAndVaultDbContextIsUsable()
    {
        await MakeInitializer().InitializeVaultDbAsync(_tempPath);

        Assert.True(File.Exists(_tempPath), "The DB file must have been created");

        await using var db = OpenVaultDb();
        var count = await db.AuditLogs.CountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, count);
    }

    // ── TC-VDS-02 ─────────────────────────────────────────────────────────────
    // AuditLog.EventLevel round-trips correctly through EF Core's mapping

    [Fact]
    public async Task AuditLog_EventLevel_RoundtripsThroughEfCore()
    {
        await MakeInitializer().InitializeVaultDbAsync(_tempPath);

        await using (var db = OpenVaultDb())
        {
            db.AuditLogs.Add(new AuditLog
            {
                CreatedAt  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                EventCode  = 1,
                EventLevel = 2,
                Payload    = null,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();
        await using var verify = OpenVaultDb();
        var log = await verify.AuditLogs.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, log.EventLevel);
    }

    // ── TC-VDS-03 ─────────────────────────────────────────────────────────────
    // Each of EventLevel = 0 / 1 / 2 is stored and read back distinctly

    [Fact]
    public async Task AuditLog_EventLevel_DistinguishesAllThreeSeverities()
    {
        await MakeInitializer().InitializeVaultDbAsync(_tempPath);

        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using (var db = OpenVaultDb())
        {
            db.AuditLogs.AddRange(
                new AuditLog { CreatedAt = ts,     EventCode = 10, EventLevel = 0 },
                new AuditLog { CreatedAt = ts + 1, EventCode = 20, EventLevel = 1 },
                new AuditLog { CreatedAt = ts + 2, EventCode = 30, EventLevel = 2 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();
        await using var verify = OpenVaultDb();
        var logs = await verify.AuditLogs.OrderBy(a => a.CreatedAt).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, logs.Count);
        Assert.Equal(0, logs[0].EventLevel);
        Assert.Equal(1, logs[1].EventLevel);
        Assert.Equal(2, logs[2].EventLevel);
    }

    // ── TC-VDS-04 ─────────────────────────────────────────────────────────────
    // Confirms via SQLite PRAGMA that the "EventLevel" column exists in the AuditLogs table
    // (physically proves the RAW INSERT column name matches the EF Core mapping)

    [Fact]
    public async Task AuditLogs_EventLevelColumnExists_MatchingRawInsertName()
    {
        await MakeInitializer().InitializeVaultDbAsync(_tempPath);

        await using var db   = OpenVaultDb();
        var             conn = (SqliteConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(AuditLogs)";
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            columns.Add(reader.GetString(1)); // col 1 = name

        Assert.Contains("EventLevel", columns);
    }

    // ── TC-VDS-05 ─────────────────────────────────────────────────────────────
    // An InsertAuthFailedRawAsync-style RAW INSERT succeeds and can be read back via EF Core

    [Fact]
    public async Task AuditLogs_RawInsertWithEventLevel_IsReadableViaEfCore()
    {
        await MakeInitializer().InitializeVaultDbAsync(_tempPath);

        await using (var db = OpenVaultDb())
        {
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO AuditLogs (CreatedAt, EventCode, EventLevel, Payload) " +
                "VALUES (@ts, @code, @level, NULL)";
            cmd.Parameters.AddWithValue("@ts",    DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("@code",  999);
            cmd.Parameters.AddWithValue("@level", 1);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();
        await using var verify = OpenVaultDb();
        var log = await verify.AuditLogs.FirstAsync(a => a.EventCode == 999, TestContext.Current.CancellationToken);
        Assert.Equal(1, log.EventLevel);
    }
}
