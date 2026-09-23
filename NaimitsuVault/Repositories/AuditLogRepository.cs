// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Services;

namespace NaimitsuVault.Repositories;

public class AuditLogRepository(IDbContextFactory<AppDbContext> factory)
{
    public async Task InsertAsync(AuditLog entity, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AuditLogs.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// For AuthFailed only, where the DEK has not been unwrapped. Bypasses the EF Core guard (SaveChangesAsync).
    /// </summary>
    public async Task InsertAuthFailedRawAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Open via EF Core's connection management (not conn.OpenAsync() directly) so
        // SqliteSynchronousInterceptor fires and PRAGMA synchronous=NORMAL still applies here.
        await db.Database.OpenConnectionAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO AuditLogs (CreatedAt, EventCode, EventLevel, Payload) " +
            "VALUES (@ts, @code, @level, NULL)";
        cmd.Parameters.AddWithValue("@ts",    DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@code",  (int)AuditEventCode.AuthFailed);
        cmd.Parameters.AddWithValue("@level", 1);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // No decryption needed (unlike GetRecentAsync/GetAllAfterAsync) - just whether any row exists,
    // used to decide whether the AuditLog nav item / dashboard jump-link have anything to show.
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AuditLogs.CountAsync(ct);
    }

    public async Task<List<AuditLog>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AuditLogs
            .AsNoTracking()
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<List<AuditLog>> GetAllAfterAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var sinceUnix = since.ToUnixTimeSeconds();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AuditLogs
            .AsNoTracking()
            .Where(a => a.CreatedAt >= sinceUnix)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .ToListAsync(ct);
    }

    /// <summary>Return value: number of rows deleted</summary>
    public async Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var cutoffUnix = cutoff.ToUnixTimeSeconds();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AuditLogs
            .Where(a => a.CreatedAt < cutoffUnix)
            .ExecuteDeleteAsync(ct);
    }
}
