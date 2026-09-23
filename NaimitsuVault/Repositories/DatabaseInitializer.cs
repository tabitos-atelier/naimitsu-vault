// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Common;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Repositories;

/// <summary>Handles DB creation and initial data seeding. Zero backward compatibility with old schemas, zero hand-written SQL migrations.</summary>
public class DatabaseInitializer(
    IDbContextFactory<AppDbContext> factory,
    IDbContextFactory<UnifiedDbContext> unifiedFactory,
    ISessionGenerationGuard sessionGuard,
    ILogger<DatabaseInitializer> logger)
{
    // ── Unified DB initialization ───────────────────────────────────────────

    /// <summary>
    /// Initializes the unified DB (NaimitsuVault.nkdb).
    /// Called at startup. The old NaimitsuVault.db is ignored (not converted).
    /// </summary>
    public async Task InitializeAsync()
    {
        logger.LogInformation("Starting unified DB initialization...");

        SqliteConnection.ClearAllPools();
        await using var db = await unifiedFactory.CreateDbContextAsync();

        await ApplyUnifiedPragmasAsync(db);
        await db.Database.EnsureCreatedAsync();

        logger.LogInformation("Unified DB initialization complete.");
    }

    /// <summary>
    /// Creates a new vault DB.
    /// path: full path of data/[48-character Base64Url].
    /// </summary>
    public async Task InitializeVaultDbAsync(string path)
    {
        logger.LogInformation("Starting vault DB creation: {Path}", System.IO.Path.GetFileName(path));

        SqliteConnection.ClearAllPools();
        // AppDbContext is a wrapper that resolves the path dynamically via IVaultConnectionProvider,
        // so it must not be used directly. Instantiate the base class VaultDbContext directly to
        // generate the schema at a fixed path.
        var options = new DbContextOptionsBuilder<VaultDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .AddInterceptors(new SqliteSynchronousInterceptor())
            .Options;

        await using var db = new VaultDbContext(options, sessionGuard);
        await ApplyVaultPragmasAsync(db);
        await db.Database.EnsureCreatedAsync();

        logger.LogInformation("Vault DB creation complete.");
    }

    // ── Second phase, after unlock ───────────────────────────────────────

    /// <summary>
    /// Called after every unlock to idempotently confirm the vault DB's journal_mode=WAL migration
    /// (to rescue existing vault DBs created before this fix).
    /// </summary>
    public async Task EnsureVaultDbMigratedAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        await SetWalAndAutoVacuumAsync(db.Database.GetDbConnection());
    }

    // ── Deleted secrets GC ───────────────────────────────────────────────────

    /// <summary>Returns the number of secrets physically deleted, so the caller can raise the SecretAutoPurgedByExpiry audit entry.</summary>
    internal async Task<int> CleanupDeletedSecretsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await CleanupDeletedSecretsAsync(db);
    }

    private async Task<int> CleanupDeletedSecretsAsync(AppDbContext db)
    {
        var cutoff = DateTime.UtcNow.AddDays(-AppConstants.DeletedSecretRetentionDays);
        var targetIds = await db.Secrets
            .Where(s => s.DeletedAt != null && s.DeletedAt < cutoff)
            .Select(s => s.Id)
            .ToListAsync();
        if (targetIds.Count == 0) return 0;

        await using var tx = await db.Database.BeginTransactionAsync();
        // SecretFileLinks has no FK cascade configured, so delete it explicitly first (same pattern as SecretRepository.HardDeleteAsync)
        await db.SecretFileLinks.Where(l => targetIds.Contains(l.SecretId)).ExecuteDeleteAsync();
        var deleted = await db.Secrets.Where(s => targetIds.Contains(s.Id)).ExecuteDeleteAsync();
        await tx.CommitAsync();

        if (deleted > 0)
            logger.LogInformation("Physically deleted {Count} soft-deleted secret(s) (past the {RetentionDays}-day retention period).", deleted, AppConstants.DeletedSecretRetentionDays);
        return deleted;
    }

    // ── Deleted files GC ───────────────────────────────────────────────────

    /// <summary>Returns the number of files physically deleted, so the caller can raise the FileAutoPurgedByExpiry audit entry.</summary>
    internal async Task<int> CleanupDeletedFilesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var cutoff = DateTime.UtcNow.AddDays(-AppConstants.DeletedSecretRetentionDays);

        // SecretFileLinks/ProfileFileLinks have no FK cascade configured. SoftDeleteAsync normally severs
        // the links already, but sweep any lingering ones defensively (same pattern as
        // StoredFileRepository.DeleteAsync) so no orphan link row outlives its file. The subquery keeps
        // the expiry condition evaluated inside the transaction, so a file restored concurrently is never purged.
        await using var tx = await db.Database.BeginTransactionAsync();
        var expiredIds = db.StoredFiles
            .Where(f => f.DeletedAt != null && f.DeletedAt < cutoff)
            .Select(f => f.Id);
        await db.SecretFileLinks.Where(l => expiredIds.Contains(l.FileId)).ExecuteDeleteAsync();
        await db.ProfileFileLinks.Where(l => expiredIds.Contains(l.FileId)).ExecuteDeleteAsync();
        var deleted = await db.StoredFiles
            .Where(f => f.DeletedAt != null && f.DeletedAt < cutoff)
            .ExecuteDeleteAsync();
        await tx.CommitAsync();

        if (deleted > 0)
            logger.LogInformation("Physically deleted {Count} soft-deleted file(s) (past the {RetentionDays}-day retention period).", deleted, AppConstants.DeletedSecretRetentionDays);
        return deleted;
    }

    // ── PRAGMA settings ──────────────────────────────────────────────────────

    private async Task ApplyUnifiedPragmasAsync(UnifiedDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        bool wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync();
        try { await SetWalAndAutoVacuumAsync(conn); }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    private async Task ApplyVaultPragmasAsync(VaultDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        bool wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync();
        try { await SetWalAndAutoVacuumAsync(conn); }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    private async Task SetWalAndAutoVacuumAsync(System.Data.Common.DbConnection conn)
    {
        // journal_mode is a setting persisted in the DB file, so no re-setting is needed if it's already wal (idempotent).
        await using (var journalCheckCmd = conn.CreateCommand())
        {
            journalCheckCmd.CommandText = "PRAGMA journal_mode";
            var currentJournal = Convert.ToString(await journalCheckCmd.ExecuteScalarAsync());
            if (!string.Equals(currentJournal, "wal", StringComparison.OrdinalIgnoreCase))
            {
                await using var setJournalCmd = conn.CreateCommand();
                setJournalCmd.CommandText = "PRAGMA journal_mode=WAL";
                // The journal_mode pragma returns a result row (the new mode name), so it must be consumed via ExecuteScalar.
                await setJournalCmd.ExecuteScalarAsync();
            }
        }

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "PRAGMA auto_vacuum";
        var current = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
        if (current != 2)
        {
            await using (var setCmd = conn.CreateCommand())
            {
                setCmd.CommandText = "PRAGMA auto_vacuum = INCREMENTAL";
                await setCmd.ExecuteNonQueryAsync();
            }
            await using (var vacuumCmd = conn.CreateCommand())
            {
                vacuumCmd.CommandText = "VACUUM";
                await vacuumCmd.ExecuteNonQueryAsync();
            }
        }
    }
}
