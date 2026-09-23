// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// High-priority tests for the cross-screen / state-transition matrix.
///
/// Group A: CP-8 soft delete x draft (SecretDrafts) retention
/// Group B: hard delete x explicit SecretFileLinks deletion
/// (Group C, the GC LiveSet helper tests TC-STM-08 .. 17, is retired.)
/// </summary>
public sealed class StateTransitionMatrixTests
{
    // ── Crypto stubs ────────────────────────────────────────────────────────

    /// <summary>A stub that disables encryption (for tests that don't parse file IDs).</summary>
    private sealed class NullCryptoService : ICryptoService
    {
        public void DeriveKey(ReadOnlySpan<char> _, ReadOnlySpan<byte> __, Span<byte> ___) { }
        public byte[] GenerateSalt(int size = 32) => new byte[size];
        public byte[] Encrypt(ReadOnlySpan<byte> _, ReadOnlySpan<byte> __, ReadOnlySpan<byte> ___ = default) => [];
        public void Decrypt(ReadOnlySpan<byte> _, ReadOnlySpan<byte> __, Span<byte> ___, ReadOnlySpan<byte> ____ = default) { }
    }

    // ── Common helpers ──────────────────────────────────────────────────────────

    private static readonly ICryptoService NullCrypto     = new NullCryptoService();

    private static byte[] Snap()
    {
        var b = new byte[64];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static async Task<Secret> InsertSecretAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var ctx = factory.CreateDbContext();
        var s = new Secret
        {
            Title    = new byte[28],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Secrets.Add(s);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        return s;
    }

    private static async Task<StoredFile> InsertStoredFileAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var ctx = factory.CreateDbContext();
        var fileHash = new byte[32];
        RandomNumberGenerator.Fill(fileHash);  // random to satisfy the UNIQUE constraint
        var f = new StoredFile
        {
            FileName       = new byte[28],
            ContentTypeCode    = 0,
            FileSize       = 0,
            FileHash       = fileHash,
            FileModifiedAt = DateTime.UtcNow,
            CreatedAt       = DateTime.UtcNow,
            UpdatedAt       = DateTime.UtcNow,
        };
        ctx.StoredFiles.Add(f);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        return f;
    }

    private static async Task InsertSecretFileLinkAsync(IDbContextFactory<AppDbContext> factory, int secretId, int fileId)
    {
        await using var ctx = factory.CreateDbContext();
        ctx.SecretFileLinks.Add(new SecretFileLink { SecretId = secretId, FileId = fileId });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> CountSecretFileLinksAsync(IDbContextFactory<AppDbContext> factory, int secretId)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.SecretFileLinks.CountAsync(l => l.SecretId == secretId, TestContext.Current.CancellationToken);
    }

    private static async Task<SecretHistory?> LoadHistoryAsync(IDbContextFactory<AppDbContext> factory, int secretId)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.SecretHistory.AsNoTracking().FirstOrDefaultAsync(h => h.SecretId == secretId, TestContext.Current.CancellationToken);
    }

    private static async Task<SecretDraft?> LoadDraftAsync(IDbContextFactory<AppDbContext> factory, int secretId)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.SecretDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.SecretId == secretId, TestContext.Current.CancellationToken);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Group A: CP-8 - soft delete x draft (SecretDrafts) retention
    // ════════════════════════════════════════════════════════════════════════════

    // ── TC-STM-01 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A1_SoftDelete_WithDraft_DraftIsPreserved()
    {
        // Arrange
        using var db = TestDb.Create();
        var secretRepo = new SecretRepository(db.Factory);
        var draftsRepo = new SecretDraftsRepository(db.Factory, NullCrypto);
        var secret     = await InsertSecretAsync(db.Factory);
        var draft      = Snap();
        await draftsRepo.SaveDraftAsync(secret.Id, draft, DateTime.UtcNow);

        // Act - soft delete
        await secretRepo.SoftDeleteAsync(secret.Id);

        // Assert: the draft row has not disappeared (per the v1.2 decision: DeletedAt!=null AND a draft
        // row existing is valid)
        var row = await LoadDraftAsync(db.Factory, secret.Id);
        Assert.NotNull(row);
        Assert.Equal(draft, row!.SnapshotBlob);
    }

    // ── TC-STM-02 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A2_SoftDelete_WithHistory_SlotOrderIsUnchanged()
    {
        // Arrange - 2 Pushes give SlotOrder = 0x210 (B,A)
        using var db = TestDb.Create();
        var secretRepo = new SecretRepository(db.Factory);
        var histRepo   = new SecretHistoryRepository(db.Factory);
        var secret     = await InsertSecretAsync(db.Factory);
        await histRepo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);
        await histRepo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        var before = await LoadHistoryAsync(db.Factory, secret.Id);
        var originalSlotOrder = before!.SlotOrder;

        // Act - soft delete (no generation push)
        await secretRepo.SoftDeleteAsync(secret.Id);

        // Assert: SlotOrder is unchanged (soft delete must never generate a new generation)
        var after = await LoadHistoryAsync(db.Factory, secret.Id);
        Assert.Equal(originalSlotOrder, after!.SlotOrder);
    }

    // ── TC-STM-03 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A3_SoftDelete_SetsDeletedAt()
    {
        // Arrange
        using var db   = TestDb.Create();
        var secretRepo = new SecretRepository(db.Factory);
        var secret     = await InsertSecretAsync(db.Factory);

        // Act
        await secretRepo.SoftDeleteAsync(secret.Id);

        // Assert
        await using var ctx = db.Factory.CreateDbContext();
        var row = await ctx.Secrets.AsNoTracking().FirstAsync(s => s.Id == secret.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(row.DeletedAt);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Group B: hard delete x explicit SecretFileLinks deletion
    // ════════════════════════════════════════════════════════════════════════════

    // ── TC-STM-04 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task B1_HardDelete_DeletesSecretFileLinksBeforeSecrets()
    {
        // Arrange
        using var db   = TestDb.Create();
        var secretRepo = new SecretRepository(db.Factory);
        var secret     = await InsertSecretAsync(db.Factory);
        var file       = await InsertStoredFileAsync(db.Factory);
        await InsertSecretFileLinkAsync(db.Factory, secret.Id, file.Id);
        Assert.Equal(1, await CountSecretFileLinksAsync(db.Factory, secret.Id));

        // Act
        await secretRepo.HardDeleteAsync(secret.Id);

        // Assert: SecretFileLinks has been explicitly deleted
        Assert.Equal(0, await CountSecretFileLinksAsync(db.Factory, secret.Id));
    }

    // ── TC-STM-05 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task B2_HardDelete_DeletesSecretRow()
    {
        // Arrange
        using var db   = TestDb.Create();
        var secretRepo = new SecretRepository(db.Factory);
        var secret     = await InsertSecretAsync(db.Factory);

        // Act
        await secretRepo.HardDeleteAsync(secret.Id);

        // Assert
        await using var ctx = db.Factory.CreateDbContext();
        var row = await ctx.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == secret.Id, TestContext.Current.CancellationToken);
        Assert.Null(row);
    }

    // ── TC-STM-06 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task B3_HardDelete_MultipleFileLinks_AllLinksDeleted()
    {
        // Arrange
        using var db   = TestDb.Create();
        var secretRepo = new SecretRepository(db.Factory);
        var secret     = await InsertSecretAsync(db.Factory);
        var file1      = await InsertStoredFileAsync(db.Factory);
        var file2      = await InsertStoredFileAsync(db.Factory);
        await InsertSecretFileLinkAsync(db.Factory, secret.Id, file1.Id);
        await InsertSecretFileLinkAsync(db.Factory, secret.Id, file2.Id);
        Assert.Equal(2, await CountSecretFileLinksAsync(db.Factory, secret.Id));

        // Act
        await secretRepo.HardDeleteAsync(secret.Id);

        // Assert
        Assert.Equal(0, await CountSecretFileLinksAsync(db.Factory, secret.Id));
    }

    // ── TC-STM-07 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task B4_HardDelete_DeletesSecretHistoryAndSecretDraftsViaCascade()
    {
        // Arrange
        using var db   = TestDb.Create();
        var secretRepo = new SecretRepository(db.Factory);
        var histRepo   = new SecretHistoryRepository(db.Factory);
        var draftsRepo = new SecretDraftsRepository(db.Factory, NullCrypto);
        var secret     = await InsertSecretAsync(db.Factory);

        // Create both a SecretHistory row (Gen1) and a SecretDrafts row
        await draftsRepo.SaveDraftAsync(secret.Id, Snap(), DateTime.UtcNow);
        await histRepo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);
        // PushAsync's own transaction clears the draft it just pushed - recreate it so both tables
        // have a row to verify the cascade against.
        await draftsRepo.SaveDraftAsync(secret.Id, Snap(), DateTime.UtcNow);

        var beforeHistory = await LoadHistoryAsync(db.Factory, secret.Id);
        var beforeDraft   = await LoadDraftAsync(db.Factory, secret.Id);
        Assert.NotNull(beforeHistory);   // Precondition: the SecretHistory row exists
        Assert.NotNull(beforeDraft);     // Precondition: the SecretDrafts row exists

        // Act
        await secretRepo.HardDeleteAsync(secret.Id);

        // Assert: deleting Secrets also physically removes both rows via ON DELETE CASCADE (CP-8c)
        Assert.Null(await LoadHistoryAsync(db.Factory, secret.Id));
        Assert.Null(await LoadDraftAsync(db.Factory, secret.Id));
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Group C (TC-STM-08 .. TC-STM-17) is retired.
    // ════════════════════════════════════════════════════════════════════════════
    //
    // Group C covered the GC LiveSet / orphan-file GC helpers:
    //   StoredFileRepository.GetFileIdsBySecretIdAsync / DeleteBatchAsync            (TC-STM-08 .. 11)
    //   SecretDraftsRepository.GetFileIdsFromDraftAsync / IsFileInAnyDraftAsync      (TC-STM-12 .. 15)
    //   SecretHistoryRepository.IsFileInAnySnapshotAsync                             (TC-STM-16 .. 17)
    // The orphan-file GC was withdrawn entirely (never physically delete data without an explicit
    // user action), so these methods had no production caller and were removed with their tests.
}
