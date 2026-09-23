// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Time Machine slot boundary-value scenarios" for SecretHistoryRepository (finalized history only;
/// draft-focused cases live in SecretDraftsRepositoryTests.cs since the 2026-08-21 table split).
///
/// SlotOrder internal representation (packed int):
///   bits[11:8]=Gen0 slot ID, bits[7:4]=Gen1, bits[3:0]=Gen2
///   0=none, 1=A, 2=B, 3=C
///   e.g.: "C,B,A" -> 0x321   "B,C,A" -> 0x231   "A,C,B" -> 0x132   "A,B" -> 0x120
/// </summary>
public sealed class SecretHistoryRepositoryTests
{
    // ── Common helpers ─────────────────────────────────────────────────────────

    // Tests other than GetSecretIdsWithDraftContainingFileAsync don't use encryption, so inject
    // a stub for ICryptoService (a null implementation that merely satisfies the constructor argument).
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

    /// <summary>Returns the number of SecretHistory rows for the given secretId.</summary>
    private static async Task<int> CountHistoryAsync(IDbContextFactory<AppDbContext> factory, int secretId)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.SecretHistory.CountAsync(h => h.SecretId == secretId, TestContext.Current.CancellationToken);
    }

    /// <summary>Returns the SecretHistory row for the given secretId via AsNoTracking (nullable).</summary>
    private static async Task<SecretHistory?> LoadRowAsync(IDbContextFactory<AppDbContext> factory, int secretId)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.SecretHistory.AsNoTracking()
                        .FirstOrDefaultAsync(h => h.SecretId == secretId, TestContext.Current.CancellationToken);
    }

    // ── TC-SHR-01 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_NoRow_InsertsExactly1Row_SlotOrderEqualsPackedA()
    {
        // Arrange
        using var db   = TestDb.Create();
        var repo       = new SecretHistoryRepository(db.Factory);
        var secret     = await InsertSecretAsync(db.Factory);

        // Act
        var result = await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Assert
        var row   = await LoadRowAsync(db.Factory, secret.Id);
        Assert.Equal(1,     await CountHistoryAsync(db.Factory, secret.Id));
        Assert.Equal(0x100, row!.SlotOrder); // Gen0=A only

        // Regression coverage: the very first push displaces nothing, so the caller must not raise
        // a TimeMachineGenRotated audit entry for it.
        Assert.False(result.Rotated);
        Assert.Null(result.SacrificedSlotLabel);
    }

    // ── TC-SHR-02 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_1stPushThen2nd_SlotOrderEqualsBA()
    {
        // Arrange
        using var db = TestDb.Create();
        var repo     = new SecretHistoryRepository(db.Factory);
        var secret   = await InsertSecretAsync(db.Factory);

        // Act
        await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);
        await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Assert
        var row = await LoadRowAsync(db.Factory, secret.Id);
        Assert.Equal(1,     await CountHistoryAsync(db.Factory, secret.Id));
        Assert.Equal(0x210, row!.SlotOrder); // Gen0=B, Gen1=A
    }

    // ── TC-SHR-03 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_2PushesThen3rd_SlotOrderEqualsCBA()
    {
        // Arrange
        using var db = TestDb.Create();
        var repo     = new SecretHistoryRepository(db.Factory);
        var secret   = await InsertSecretAsync(db.Factory);

        // Act
        for (int i = 0; i < 3; i++)
            await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Assert
        var row = await LoadRowAsync(db.Factory, secret.Id);
        Assert.Equal(1,     await CountHistoryAsync(db.Factory, secret.Id));
        Assert.Equal(0x321, row!.SlotOrder); // Gen0=C, Gen1=B, Gen2=A
    }

    // ── TC-SHR-04 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_3SlotsFull_4thPush_SlotOrderRotatesToACB()
    {
        // Arrange
        using var db = TestDb.Create();
        var repo     = new SecretHistoryRepository(db.Factory);
        var secret   = await InsertSecretAsync(db.Factory);

        // Act - on the 4th push, Gen2(A) is sacrificed and SlotOrder rotates
        (bool Rotated, string? SacrificedSlotLabel) result = default;
        for (int i = 0; i < 4; i++)
            result = await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Assert
        var row = await LoadRowAsync(db.Factory, secret.Id);
        Assert.Equal(1,     await CountHistoryAsync(db.Factory, secret.Id));
        Assert.Equal(0x132, row!.SlotOrder); // Gen0=A, Gen1=C, Gen2=B

        // Regression coverage: only the 4th push (all 3 slots already full) reports a sacrificed
        // slot - this return value is what the caller uses to raise the TimeMachineSlotDeleted
        // audit entry, distinct from the routine TimeMachineGenRotated raised on every push after the first.
        Assert.True(result.Rotated);
        Assert.Equal("A", result.SacrificedSlotLabel);
    }

    // ── TC-SHR-05 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_100Consecutive_TotalRowsAlwaysExactly1()
    {
        // Arrange
        using var db = TestDb.Create();
        var repo     = new SecretHistoryRepository(db.Factory);
        var secret   = await InsertSecretAsync(db.Factory);

        // Act
        for (int i = 0; i < 100; i++)
            await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Assert - INSERT only happens on the first call; every subsequent call is UPDATE only. Row count must never grow.
        Assert.Equal(1, await CountHistoryAsync(db.Factory, secret.Id));
    }

    // ── TC-SHR-06 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwapSlotOrderAsync_Gen1Restore_FromCBA_BecomesBCA()
    {
        // Arrange - 3 Pushes give SlotOrder = 0x321 (C,B,A)
        using var db = TestDb.Create();
        var repo     = new SecretHistoryRepository(db.Factory);
        var secret   = await InsertSecretAsync(db.Factory);
        for (int i = 0; i < 3; i++)
            await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Act - genIndex=0 promotes Gen1(B) to Gen0
        await repo.SwapSlotOrderAsync(secret.Id, targetGenerationIndex: 0);

        // Assert - Pack(B=2, C=3, A=1) = (2<<8)|(3<<4)|1 = 0x231
        var row = await LoadRowAsync(db.Factory, secret.Id);
        Assert.Equal(0x231, row!.SlotOrder); // B,C,A
    }

    // ── TC-SHR-07 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwapSlotOrderAsync_Gen2Restore_FromCBA_BecomesACB()
    {
        // Arrange - 3 Pushes give SlotOrder = 0x321 (C,B,A)
        using var db = TestDb.Create();
        var repo     = new SecretHistoryRepository(db.Factory);
        var secret   = await InsertSecretAsync(db.Factory);
        for (int i = 0; i < 3; i++)
            await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Act - genIndex=1 promotes Gen2(A) to Gen0
        await repo.SwapSlotOrderAsync(secret.Id, targetGenerationIndex: 1);

        // Assert - Pack(A=1, C=3, B=2) = (1<<8)|(3<<4)|2 = 0x132
        var row = await LoadRowAsync(db.Factory, secret.Id);
        Assert.Equal(0x132, row!.SlotOrder); // A,C,B
    }

    // ── TC-SHR-08 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwapSlotOrderAsync_Gen1Restore_FromBA_BecomesAB()
    {
        // Arrange - 2 Pushes give SlotOrder = 0x210 (B,A)
        using var db = TestDb.Create();
        var repo     = new SecretHistoryRepository(db.Factory);
        var secret   = await InsertSecretAsync(db.Factory);
        for (int i = 0; i < 2; i++)
            await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Act - genIndex=0 promotes Gen1(A) to Gen0
        await repo.SwapSlotOrderAsync(secret.Id, targetGenerationIndex: 0);

        // Assert - Pack(A=1, B=2, 0) = (1<<8)|(2<<4)|0 = 0x120
        var row = await LoadRowAsync(db.Factory, secret.Id);
        Assert.Equal(0x120, row!.SlotOrder); // A,B
    }

    // ── TC-SHR-09 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwapSlotOrderAsync_OnlyGen0_Gen1RestoreRequest_IsNoOp()
    {
        // Arrange - 1 Push gives SlotOrder = 0x100 (A only, no Gen1)
        using var db = TestDb.Create();
        var repo     = new SecretHistoryRepository(db.Factory);
        var secret   = await InsertSecretAsync(db.Factory);
        await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Act - GenCount(1) <= 0+1 -> the boundary guard returns immediately
        await repo.SwapSlotOrderAsync(secret.Id, targetGenerationIndex: 0);

        // Assert - SlotOrder is unchanged
        var row = await LoadRowAsync(db.Factory, secret.Id);
        Assert.Equal(0x100, row!.SlotOrder);
    }

    // ── TC-SHR-10 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSnapshotsAsync_NullSlotBlobWhilePointerExists_ReturnsNullWithoutCrash()
    {
        // Arrange - SlotOrder=0x321(C,B,A), but Gen1=SlotB and Gen2=SlotA are NULL
        using var db   = TestDb.Create();
        var repo       = new SecretHistoryRepository(db.Factory);
        var secret     = await InsertSecretAsync(db.Factory);

        await using (var ctx = db.Factory.CreateDbContext())
        {
            ctx.SecretHistory.Add(new SecretHistory
            {
                SecretId  = secret.Id,
                SlotOrder = 0x321, // Gen0=C, Gen1=B, Gen2=A
                SlotC     = Snap(), // only Gen0 is non-NULL
                SlotB     = null,   // SlotB, pointed to by Gen1, is NULL
                SlotA     = null,   // SlotA, pointed to by Gen2, is NULL
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var (gen1Enc, _, gen2Enc, _) = await repo.GetSnapshotsAsync(secret.Id);

        // Assert - hitting a NULL slot returns null without crashing
        Assert.Null(gen1Enc);
        Assert.Null(gen2Enc);
    }

    // ── TC-SHR-11 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_AfterSecretDeleted_HistoryRowAlsoDeletedViaCascade()
    {
        // Arrange
        using var db = TestDb.Create();
        var repo     = new SecretHistoryRepository(db.Factory);
        var secret   = await InsertSecretAsync(db.Factory);
        await repo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);
        Assert.Equal(1, await CountHistoryAsync(db.Factory, secret.Id));

        // Act - delete the Secret (SecretHistory disappears too via ON DELETE CASCADE)
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var toDelete = await ctx.Secrets.FindAsync([secret.Id], TestContext.Current.CancellationToken);
            ctx.Secrets.Remove(toDelete!);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.Equal(0, await CountHistoryAsync(db.Factory, secret.Id));
    }

    // TC-SHR-12..16 (draft-focused: SaveDraftAsync/DiscardDraftAsync/GetAllDraftIdsAsync) moved to
    // SecretDraftsRepositoryTests.cs, rewritten against the now-independent SecretDrafts table
    // (2026-08-21 SecretHistory/SecretDrafts split).

    // ── TC-SHR-17 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-SHR-11 verifies cascading via EF tracking (Remove + SaveChangesAsync).
    /// TC-SHR-17 targets the ExecuteDeleteAsync path used by SecretRepository.HardDeleteAsync,
    /// confirming that the DB-level ON DELETE CASCADE constraint actually works.
    /// </summary>
    [Fact]
    public async Task HardDeleteAsync_WithHistory_AlsoDeletesSecretHistoryViaCascade()
    {
        // Arrange
        using var db    = TestDb.Create();
        var secretRepo  = new SecretRepository(db.Factory);
        var historyRepo = new SecretHistoryRepository(db.Factory);
        var secret      = await InsertSecretAsync(db.Factory);
        await historyRepo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);
        Assert.Equal(1, await CountHistoryAsync(db.Factory, secret.Id));

        // Act - the ExecuteDeleteAsync path (bypasses the EF change tracker)
        await secretRepo.HardDeleteAsync(secret.Id);

        // Assert - the SecretHistory row is also gone, via the DB-level ON DELETE CASCADE
        Assert.Equal(0, await CountHistoryAsync(db.Factory, secret.Id));
    }

    // ── TC-SHR-18 ────────────────────────────────────────────────────────────────
    // Core atomicity guarantee of this refactor: PushAsync's internal transaction clears any pending
    // SecretDrafts row for the same secret in the same commit as the slot rotation (no separate,
    // non-atomic DiscardDraftAsync call is needed from the caller).

    [Fact]
    public async Task PushAsync_WithExistingDraft_ClearsDraftInSameCall()
    {
        // Arrange
        using var db     = TestDb.Create();
        var historyRepo  = new SecretHistoryRepository(db.Factory);
        var draftsRepo   = new SecretDraftsRepository(db.Factory, NullCrypto);
        var secret       = await InsertSecretAsync(db.Factory);
        await draftsRepo.SaveDraftAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Act
        await historyRepo.PushAsync(secret.Id, Snap(), DateTime.UtcNow);

        // Assert - the draft row is gone as a side effect of the same PushAsync call
        var (draftEnc, draftAt) = await draftsRepo.GetDraftAsync(secret.Id);
        Assert.Null(draftEnc);
        Assert.Null(draftAt);
    }

    // ── TC-SHR-19 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PushAsync_NoExistingDraft_LeavesDraftTableEmptyWithoutError()
    {
        // Arrange
        using var db     = TestDb.Create();
        var historyRepo  = new SecretHistoryRepository(db.Factory);
        var draftsRepo   = new SecretDraftsRepository(db.Factory, NullCrypto);
        var secret       = await InsertSecretAsync(db.Factory);

        // Act - the 0-row DELETE inside PushAsync's transaction must be harmless when there's no draft
        var ex = await Record.ExceptionAsync(() => historyRepo.PushAsync(secret.Id, Snap(), DateTime.UtcNow));

        // Assert
        Assert.Null(ex);
        var (draftEnc, _) = await draftsRepo.GetDraftAsync(secret.Id);
        Assert.Null(draftEnc);
    }
}
