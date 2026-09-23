// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// SecretDraftsRepository scenarios (TC-SDR-01..05).
/// Moved from SecretHistoryRepositoryTests.cs (formerly TC-SHR-12..16) and rewritten against the
/// now-independent SecretDrafts table, split out of SecretHistory on 2026-08-21. A draft row's
/// existence now means "a draft exists"; its absence means "no draft" - there is no longer a
/// nullable BLOB column to check.
/// </summary>
public sealed class SecretDraftsRepositoryTests
{
    // ── Common helpers ─────────────────────────────────────────────────────────

    private static readonly ICryptoService NullCrypto = new NullCryptoService();

    private sealed class NullCryptoService : ICryptoService
    {
        public void DeriveKey(ReadOnlySpan<char> _, ReadOnlySpan<byte> __, Span<byte> ___) { }
        public byte[] GenerateSalt(int size = 32) => new byte[size];
        public byte[] Encrypt(ReadOnlySpan<byte> _, ReadOnlySpan<byte> __, ReadOnlySpan<byte> ___ = default) => [];
        public void Decrypt(ReadOnlySpan<byte> _, ReadOnlySpan<byte> __, Span<byte> ___, ReadOnlySpan<byte> ____ = default) { }
    }

    /// <summary>Returns a random 64-byte dummy snapshot.</summary>
    private static byte[] Snap()
    {
        var b = new byte[64];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    /// <summary>Inserts and returns a minimal Secret row.</summary>
    private static async Task<Secret> InsertSecretAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var ctx = factory.CreateDbContext();
        var s = new Secret
        {
            Title    = new byte[28], // minimal dummy BLOB (nonce12+tag16+empty ciphertext)
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Secrets.Add(s);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        return s;
    }

    /// <summary>Returns the number of SecretDrafts rows for the given secretId (0 or 1).</summary>
    private static async Task<int> CountDraftsAsync(IDbContextFactory<AppDbContext> factory, int secretId)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.SecretDrafts.CountAsync(d => d.SecretId == secretId, TestContext.Current.CancellationToken);
    }

    /// <summary>Returns the SecretDrafts row for the given secretId via AsNoTracking (nullable).</summary>
    private static async Task<SecretDraft?> LoadDraftRowAsync(IDbContextFactory<AppDbContext> factory, int secretId)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.SecretDrafts.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.SecretId == secretId, TestContext.Current.CancellationToken);
    }

    /// <summary>Returns the number of SecretHistory rows for the given secretId.</summary>
    private static async Task<int> CountHistoryAsync(IDbContextFactory<AppDbContext> factory, int secretId)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.SecretHistory.CountAsync(h => h.SecretId == secretId, TestContext.Current.CancellationToken);
    }

    // ── TC-SDR-01 (formerly TC-SHR-12) ───────────────────────────────────────────

    [Fact]
    public async Task SaveDraftAsync_NoExistingRow_InsertsRow()
    {
        // Arrange
        using var db = TestDb.Create();
        var repo     = new SecretDraftsRepository(db.Factory, NullCrypto);
        var secret   = await InsertSecretAsync(db.Factory);
        var draft    = Snap();

        // Act
        await repo.SaveDraftAsync(secret.Id, draft, DateTime.UtcNow);

        // Assert
        var row = await LoadDraftRowAsync(db.Factory, secret.Id);
        Assert.Equal(1, await CountDraftsAsync(db.Factory, secret.Id));
        Assert.Equal(draft, row!.SnapshotBlob);
        // No SecretHistory row is created as a side effect - the two tables are fully independent
        Assert.Equal(0, await CountHistoryAsync(db.Factory, secret.Id));
    }

    // ── TC-SDR-02 (formerly TC-SHR-13) ───────────────────────────────────────────

    [Fact]
    public async Task SaveDraftAsync_ExistingRow_UpdatesContentInPlace()
    {
        // Arrange - insert an initial draft row
        using var db = TestDb.Create();
        var repo     = new SecretDraftsRepository(db.Factory, NullCrypto);
        var secret   = await InsertSecretAsync(db.Factory);
        await repo.SaveDraftAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Act
        var draft2 = Snap();
        await repo.SaveDraftAsync(secret.Id, draft2, DateTime.UtcNow);

        // Assert - the row is updated in place (UPSERT), never duplicated
        var row = await LoadDraftRowAsync(db.Factory, secret.Id);
        Assert.Equal(1,      await CountDraftsAsync(db.Factory, secret.Id));
        Assert.Equal(draft2, row!.SnapshotBlob);
    }

    // ── TC-SDR-03 (formerly TC-SHR-14) ───────────────────────────────────────────

    [Fact]
    public async Task DiscardDraftAsync_DeletesTheRow()
    {
        // Arrange
        using var db = TestDb.Create();
        var repo     = new SecretDraftsRepository(db.Factory, NullCrypto);
        var secret   = await InsertSecretAsync(db.Factory);
        await repo.SaveDraftAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Act
        await repo.DiscardDraftAsync(secret.Id);

        // Assert - the row itself is gone (existence, not a nullable column, marks draft state)
        Assert.Equal(0, await CountDraftsAsync(db.Factory, secret.Id));
        var (draftEnc, draftAt) = await repo.GetDraftAsync(secret.Id);
        Assert.Null(draftEnc);
        Assert.Null(draftAt);
    }

    // ── TC-SDR-04 (formerly TC-SHR-15) ───────────────────────────────────────────

    [Fact]
    public async Task DiscardDraftAsync_NoRow_IsNoOpAndDoesNotThrow()
    {
        // Arrange - no SecretDrafts row
        using var db = TestDb.Create();
        var repo     = new SecretDraftsRepository(db.Factory, NullCrypto);
        var secret   = await InsertSecretAsync(db.Factory);

        // Act - ExecuteDeleteAsync simply returns 0 rows deleted; no exception is thrown
        var ex = await Record.ExceptionAsync(() => repo.DiscardDraftAsync(secret.Id));

        // Assert
        Assert.Null(ex);
        Assert.Equal(0, await CountDraftsAsync(db.Factory, secret.Id));
    }

    // ── TC-SDR-05 (formerly TC-SHR-16) ───────────────────────────────────────────

    [Fact]
    public async Task GetAllDraftIdsAsync_ReturnsOnlyIdsWithADraftRow()
    {
        // Arrange
        using var db        = TestDb.Create();
        var draftsRepo      = new SecretDraftsRepository(db.Factory, NullCrypto);
        var historyRepo     = new SecretHistoryRepository(db.Factory);
        var secretA         = await InsertSecretAsync(db.Factory);
        var secretB         = await InsertSecretAsync(db.Factory);

        await draftsRepo.SaveDraftAsync(secretA.Id, Snap(), DateTime.UtcNow); // A: has a draft
        await historyRepo.PushAsync(secretB.Id, Snap(), DateTime.UtcNow);     // B: finalized history only, no draft

        // Act
        var ids = await draftsRepo.GetAllDraftIdsAsync();

        // Assert
        Assert.Contains(secretA.Id, ids);
        Assert.DoesNotContain(secretB.Id, ids);
    }

    // ── TC-SDR-06 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAllForSecretAsync_RemovesOnlyThatSecretsDraft()
    {
        // Arrange
        using var db = TestDb.Create();
        var repo     = new SecretDraftsRepository(db.Factory, NullCrypto);
        var secretA  = await InsertSecretAsync(db.Factory);
        var secretB  = await InsertSecretAsync(db.Factory);
        await repo.SaveDraftAsync(secretA.Id, Snap(), DateTime.UtcNow);
        await repo.SaveDraftAsync(secretB.Id, Snap(), DateTime.UtcNow);

        // Act
        await repo.DeleteAllForSecretAsync(secretA.Id);

        // Assert
        Assert.Equal(0, await CountDraftsAsync(db.Factory, secretA.Id));
        Assert.Equal(1, await CountDraftsAsync(db.Factory, secretB.Id));
    }

    // ── TC-SDR-07 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecretDeleted_DraftRowAlsoDeletedViaCascade()
    {
        // Arrange
        using var db = TestDb.Create();
        var repo     = new SecretDraftsRepository(db.Factory, NullCrypto);
        var secret   = await InsertSecretAsync(db.Factory);
        await repo.SaveDraftAsync(secret.Id, Snap(), DateTime.UtcNow);
        Assert.Equal(1, await CountDraftsAsync(db.Factory, secret.Id));

        // Act - delete the Secret (SecretDrafts disappears too via ON DELETE CASCADE)
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var toDelete = await ctx.Secrets.FindAsync([secret.Id], TestContext.Current.CancellationToken);
            ctx.Secrets.Remove(toDelete!);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal(0, await CountDraftsAsync(db.Factory, secret.Id));
    }
}
