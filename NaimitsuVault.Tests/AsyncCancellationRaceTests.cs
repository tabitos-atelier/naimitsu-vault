// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Async cancellation race tests (TC-ACR-01 .. TC-ACR-12).
/// Verifies CancellationToken handling in the repository layer and AppDbContext's generation checks.
/// </summary>
public sealed class AsyncCancellationRaceTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SecretDraftsRepository MakeDraftsRepo(TestDb db)
        => new(db.Factory, new IdentityCryptoService());

    private static DekScope MakeDek() => new(new byte[32], 32);

    private static async Task InsertDraftRows(TestDb db, int count)
    {
        await using var ctx = db.Factory.CreateDbContext();
        for (int i = 1; i <= count; i++)
        {
            ctx.Secrets.Add(new Secret
            {
                Id       = i,
                Title    = new byte[28],
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Draft content uses IdentityCryptoService: [28 zeros] + plaintext JSON
        // ContainsFileId parses the part remaining after removing the leading 28 bytes as a header,
        // so it fails with a JsonReaderException unless the content is valid JSON.
        ReadOnlySpan<byte> jsonTemplate = "{\"FileIds\":[]}"u8;
        var draftContent = new byte[28 + jsonTemplate.Length];
        jsonTemplate.CopyTo(draftContent.AsSpan(28));

        for (int i = 1; i <= count; i++)
        {
            ctx.SecretDrafts.Add(new SecretDraft
            {
                SecretId     = i,
                SnapshotBlob = (byte[])draftContent.Clone(),
                SavedAt      = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string GetConnStr(TestDb db)
    {
        using var ctx = db.Factory.CreateDbContext();
        return ctx.Database.GetConnectionString()!;
    }

    // ── TC-ACR-01 ─────────────────────────────────────────────────────────────
    // Pre-canceled token -> GetSecretIdsWithDraftContainingFileAsync throws OCE immediately

    [Fact]
    public async Task GetSecretIdsWithDraftContaining_PreCanceled_ThrowsOCE()
    {
        using var db   = TestDb.Create();
        var repo       = MakeDraftsRepo(db);
        var dek        = MakeDek();
        using var cts  = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repo.GetSecretIdsWithDraftContainingFileAsync(1, dek, cts.Token));
    }

    // TC-ACR-02 through TC-ACR-04 are retired/skipped numbers. They covered the GC LiveSet helpers
    // GetFileIdsFromDraftAsync / IsFileInAnyDraftAsync / IsFileInAnySnapshotAsync, which were removed
    // because the orphan-file GC was withdrawn and nothing else called them.

    // ── TC-ACR-05 ─────────────────────────────────────────────────────────────
    // CancellationToken.None completes normally (baseline)

    [Fact]
    public async Task AllTokenMethods_TokenNone_CompleteSuccessfully()
    {
        using var db     = TestDb.Create();
        var draftsRepo   = MakeDraftsRepo(db);
        var dek          = MakeDek();

        var ex = await Record.ExceptionAsync(async () =>
        {
            await draftsRepo.GetSecretIdsWithDraftContainingFileAsync(1, dek, TestContext.Current.CancellationToken);
        });
        Assert.Null(ex);
    }

    // ── TC-ACR-06 ─────────────────────────────────────────────────────────────
    // GetSecretIdsWithDraftContainingFileAsync - verifies ThrowIfCancellationRequested mid-loop.
    // After inserting multiple draft rows, call it with an already-cancelled token -> OCE

    [Fact]
    public async Task GetSecretIdsWithDraft_CancelledAfterDataLoaded_ThrowsOCE()
    {
        using var db  = TestDb.Create();
        await InsertDraftRows(db, 20);

        var repo      = MakeDraftsRepo(db);
        var dek       = MakeDek();
        using var cts = new CancellationTokenSource();
        cts.Cancel();   // cancel while data is already loaded

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repo.GetSecretIdsWithDraftContainingFileAsync(1, dek, cts.Token));
    }

    // ── TC-ACR-07 ─────────────────────────────────────────────────────────────
    // AppDbContext.SaveChangesAsync + pre-canceled token -> OCE (originates from the EF Core base implementation)

    [Fact]
    public async Task AppDbContextSaveChanges_PreCanceledToken_ThrowsOCE()
    {
        using var db   = TestDb.Create();
        var sec        = new SessionGenerationGuardStub();
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        ctx.Secrets.Add(new Secret { Title = new byte[28], CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ctx.SaveChangesAsync(cts.Token));
    }

    // ── TC-ACR-08 ─────────────────────────────────────────────────────────────
    // 50 concurrent SaveDraftAsync calls - cancelling the shared token all at once -> OCE propagates to every thread

    [Fact]
    public async Task SaveDraftAsync_50Concurrent_SharedCancel_AllThrowOCE()
    {
        using var cts = new CancellationTokenSource();
        var factory   = new FaultInjectionDbContextFactory(
            new OperationCanceledException(cts.Token));

        var repo  = new SecretDraftsRepository(factory, new IdentityCryptoService());
        int oce   = 0;
        int other = 0;

        var tasks = Enumerable.Range(1, 50).Select(i => Task.Run(async () =>
        {
            try
            {
                await repo.SaveDraftAsync(i, new byte[64], DateTime.UtcNow);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref oce);
            }
            catch
            {
                Interlocked.Increment(ref other);
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        Assert.Equal(50, oce);
        Assert.Equal(0, other);
    }

    // ── TC-ACR-09 ─────────────────────────────────────────────────────────────
    // Two independent tasks: one uses CancellationToken.None (succeeds), the other an already-cancelled token (OCE)

    [Fact]
    public async Task TwoTasks_IndependentTokens_OnlyCancelledOneThrows()
    {
        using var db  = TestDb.Create();
        await InsertDraftRows(db, 5);

        var repo      = MakeDraftsRepo(db);
        var dek       = MakeDek();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Exception? normalEx = null;
        Exception? cancelEx = null;

        await Task.WhenAll(
            Task.Run(async () =>
            {
                try { await repo.GetSecretIdsWithDraftContainingFileAsync(99, dek, TestContext.Current.CancellationToken); }
                catch (Exception e) { normalEx = e; }
            }, TestContext.Current.CancellationToken),
            Task.Run(async () =>
            {
                try { await repo.GetSecretIdsWithDraftContainingFileAsync(99, dek, cts.Token); }
                catch (Exception e) { cancelEx = e; }
            }, TestContext.Current.CancellationToken));

        Assert.Null(normalEx);
        Assert.IsAssignableFrom<OperationCanceledException>(cancelEx);
    }

    // ── TC-ACR-10 ─────────────────────────────────────────────────────────────
    // The OCE from a cancelled token retains that cancellation token's information

    [Fact]
    public async Task CancellationOCE_ContainsCancellationToken()
    {
        using var db  = TestDb.Create();
        var repo      = MakeDraftsRepo(db);
        var dek       = MakeDek();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repo.GetSecretIdsWithDraftContainingFileAsync(1, dek, cts.Token));
        Assert.True(ex.CancellationToken.IsCancellationRequested);
    }

    // ── TC-ACR-11 ─────────────────────────────────────────────────────────────
    // Passing SessionLockGuard.Token as the CancellationToken for an async operation.
    // Barricade + Cancel -> the in-progress Task.Delay is interrupted with OCE

    [Fact]
    public async Task SessionLockGuard_CancelPropagatesIntoAwait()
    {
        using var guard = new SessionLockGuard();

        var task = Task.Run(async () =>
        {
            // A long wait on the guard's token (expected to be cancelled during the test)
            await Task.Delay(10_000, guard.Token);
        }, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        guard.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    // ── TC-ACR-12 ─────────────────────────────────────────────────────────────
    // AppDbContext: no data has been written even after throwing OCE due to cancellation

    [Fact]
    public async Task AppDbContextSaveChanges_CancelledToken_NoPhysicalWrite()
    {
        using var db   = TestDb.Create();
        var sec        = new SessionGenerationGuardStub();
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);

        ctx.Secrets.Add(new Secret { Title = new byte[28], CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try { await ctx.SaveChangesAsync(cts.Token); } catch { /* OCE expected */ }

        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(0, await verify.Secrets.CountAsync(TestContext.Current.CancellationToken));
    }
}
