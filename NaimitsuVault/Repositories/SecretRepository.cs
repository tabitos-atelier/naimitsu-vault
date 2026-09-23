// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;

namespace NaimitsuVault.Repositories;

public class SecretRepository(IDbContextFactory<AppDbContext> factory)
{
    public async Task<int> CountActiveAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Secrets.CountAsync(s => s.DeletedAt == null);
    }

    // Includes soft-deleted rows - TimeMachine's history covers both active and deleted secrets,
    // so its nav item's IsEnabled must not go by CountActiveAsync alone (that would hide a deleted
    // secret's recovery history behind a disabled nav item once no active secrets remain).
    public async Task<int> CountAllIncludingDeletedAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Secrets.CountAsync();
    }

    public async Task<List<Secret>> GetExpiringAsync(DateTime limitUtc)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Secrets
            .AsNoTracking()
            .Where(s => s.DeletedAt == null && s.ExpiresAt.HasValue && s.ExpiresAt < limitUtc)
            .ToListAsync();
    }

    public async Task<int> CountExpiryAlertsAsync(int warnDays)
    {
        // Converts the local calendar-day boundary to a UTC instant before passing it to EF Core's
        // DateTimeOffset value converter. Since display-side code (SecretsViewModel, TimeMachineViewModel,
        // etc.) all determines expiration based on the local calendar day (ToLocalTime().Date), the DB
        // aggregation side must align to the same basis here (otherwise, e.g. at UTC+9, the warning
        // count can drift by up to a day between screens near the date boundary).
        var limit = DateTime.Today.AddDays(warnDays).ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Secrets.CountAsync(s =>
            s.DeletedAt == null && s.ExpiresAt.HasValue && s.ExpiresAt < limit);
    }

    // Unlike CountExpiryAlertsAsync (expired + expiring-soon combined), this counts only secrets
    // that are already past due - used to decide red-vs-gold before SecretsViewModel has loaded
    // (see ShellWindow.RefreshCachedSecretExpiryCountAsync).
    public async Task<int> CountExpiredAsync()
    {
        var todayUtc = DateTime.Today.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Secrets.CountAsync(s =>
            s.DeletedAt == null && s.ExpiresAt.HasValue && s.ExpiresAt < todayUtc);
    }

    public async Task<List<Secret>> GetAllAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Secrets
            .AsNoTracking()
            .Where(s => s.DeletedAt == null)
            .ToListAsync();
    }

    public async Task<Secret?> GetByIdAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
    }

    /// <summary>For audit log display: bulk-fetches the encrypted Title BLOB for a list of IDs. Includes deleted items.</summary>
    public async Task<Dictionary<int, byte[]>> GetTitleBlobsByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return [];
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Secrets
            .AsNoTracking()
            .Where(s => idList.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Title, ct);
    }

    /// <summary>For TimeMachine: returns all rows including deleted, ordered by UpdatedAt descending.</summary>
    public async Task<List<Secret>> GetAllIncludingDeletedAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Secrets
            .AsNoTracking()
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync();
    }

    public async Task<int> AddAsync(Secret secret)
    {
        await using var db = await factory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        secret.CreatedAt = now;
        secret.UpdatedAt = now;
        db.Secrets.Add(secret);
        await db.SaveChangesAsync();
        return secret.Id;
    }

    public async Task UpdateAsync(Secret secret)
    {
        await using var db = await factory.CreateDbContextAsync();
        secret.UpdatedAt = DateTime.UtcNow;
        db.Secrets.Update(secret);
        await db.SaveChangesAsync();
    }

    /// <summary>Saves without changing UpdatedAt. For TimeMachine generation rotation only.</summary>
    public async Task UpdatePreservingTimestampAsync(Secret secret)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Secrets.Update(secret);
        await db.SaveChangesAsync();
    }

    public async Task SoftDeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Secrets
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.DeletedAt, DateTime.UtcNow));
    }

    public async Task HardDeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        // SecretFileLinks has no FK cascade configured, so delete it explicitly first
        await db.SecretFileLinks.Where(l => l.SecretId == id).ExecuteDeleteAsync();
        // Delete Secrets (SecretHistory is cascade-deleted automatically)
        await db.Secrets.Where(s => s.Id == id).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    public async Task UndeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Secrets
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.DeletedAt, (DateTime?)null));
    }

    public async Task SetFavoriteAsync(int id, bool isFavorite)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Secrets
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.IsFavorite, isFavorite));
    }

    public async Task<List<int>> GetFileLinksAsync(int secretId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.SecretFileLinks
            .AsNoTracking()
            .Where(l => l.SecretId == secretId)
            .Select(l => l.FileId)
            .ToListAsync();
    }

    public async Task UpdateFileLinksAsync(int secretId, IEnumerable<int> fileIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        // ExecuteDeleteAsync completely bypasses the change tracker and issues a raw SQL DELETE (zero allocation)
        await db.SecretFileLinks
            .Where(l => l.SecretId == secretId)
            .ExecuteDeleteAsync();

        db.SecretFileLinks.AddRange(
            fileIds.Distinct().Select(id => new SecretFileLink { SecretId = secretId, FileId = id }));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
