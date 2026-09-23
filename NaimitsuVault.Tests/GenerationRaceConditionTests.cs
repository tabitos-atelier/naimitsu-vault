// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;

namespace NaimitsuVault.Tests;

/// <summary>
/// Generation race conditions, long-lived staging, GC ghost leaks, and SQLite VACUUM tests
/// (TC-GR-01 .. TC-GR-07).
/// TC-GR-01/02/03/05 overlapped with what TC-DBX-13/14/12/11 in AppDbContextGenerationTests
/// verified, so for these 4 cases this file was consolidated as the single source of truth.
/// When changing SessionGenerationGuard semantics, only this class needs to be checked.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class GenerationRaceConditionTests
{
    private static string GetConnStr(TestDb db)
    {
        using var ctx = db.Factory.CreateDbContext();
        return ctx.Database.GetConnectionString()!;
    }

    private static Secret MinimalSecret() => new()
    {
        Title    = new byte[28],
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── TC-GR-01 ──────────────────────────────────────────────────────────────
    // After creating a context, calling NextSession -> Save gets rejected as belonging to an old generation

    [Fact]
    public async Task Race_ContextCaptured_ThenSessionAdvances_SaveRejected()
    {
        using var db    = TestDb.Create();
        var sec         = new ControlledSessionGenerationGuard();
        var connStr     = GetConnStr(db);

        // Create the context at generation 1
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options, sec);
        ctx.Secrets.Add(MinimalSecret());

        // Advance the generation right before Save
        sec.NextSession();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ctx.SaveChangesAsync(TestContext.Current.CancellationToken));

        // No physical write occurred
        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(0, await verify.Secrets.CountAsync(TestContext.Current.CancellationToken));
    }

    // ── TC-GR-02 ──────────────────────────────────────────────────────────────
    // Multiple contexts of the same generation Saving in parallel all succeed

    [Fact]
    public async Task Race_SameGeneration_MultipleContexts_AllSucceed()
    {
        using var db    = TestDb.Create();
        var sec         = new ControlledSessionGenerationGuard();
        var connStr     = GetConnStr(db);

        // 20 contexts Save in parallel
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var ctx = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options, sec);
            ctx.Secrets.Add(MinimalSecret());
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        await Task.WhenAll(tasks.Select(async t =>
        {
            try { await t; }
            catch (Exception e) { exceptions.Add(e); }
        }));

        Assert.Empty(exceptions);
        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(20, await verify.Secrets.CountAsync(TestContext.Current.CancellationToken));
    }

    // ── TC-GR-03 ──────────────────────────────────────────────────────────────
    // Every context created after Barricade is rejected

    [Fact]
    public async Task Race_Barricade_AllPostBarricadeContextsRejected()
    {
        using var db    = TestDb.Create();
        var sec         = new ControlledSessionGenerationGuard();
        sec.Barricade();  // create contexts while in the barricaded state

        var connStr = GetConnStr(db);

        int oce = 0, pass = 0;
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var ctx = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options, sec);
            ctx.Secrets.Add(MinimalSecret());
            try
            {
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                Interlocked.Increment(ref pass);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref oce);
            }
        });
        await Task.WhenAll(tasks);

        Assert.Equal(0, pass);
        Assert.Equal(10, oce);
    }

    // ── TC-GR-04 ──────────────────────────────────────────────────────────────
    // Rapid NextSession cycle: each context snapshots its own generation ID and is
    // invalidated by the next NextSession

    [Fact]
    public async Task Race_RapidNextSessionCycle_EachContextRejectedByNext()
    {
        using var db    = TestDb.Create();
        var sec         = new ControlledSessionGenerationGuard();
        var connStr     = GetConnStr(db);

        int oce = 0;
        for (int i = 0; i < 10; i++)
        {
            await using var ctx = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options, sec);
            ctx.Secrets.Add(MinimalSecret());
            sec.NextSession();  // advance the generation immediately after creating the context
            try { await ctx.SaveChangesAsync(TestContext.Current.CancellationToken); }
            catch (OperationCanceledException) { oce++; }
        }

        Assert.Equal(10, oce);
    }

    // ── TC-GR-05 ──────────────────────────────────────────────────────────────
    // Cross-thread: create the context on thread A, call NextSession on thread B,
    // and thread A's Save fails with OCE

    [Fact]
    public async Task Race_CrossThread_ContextCreatedInA_NextSessionInB_SaveFails()
    {
        using var db    = TestDb.Create();
        var sec         = new ControlledSessionGenerationGuard();
        var connStr     = GetConnStr(db);

        var barrier = new TaskCompletionSource<bool>();
        Exception? saveEx = null;

        var contextTask = Task.Run(async () =>
        {
            await using var ctx = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options, sec);
            ctx.Secrets.Add(MinimalSecret());

            barrier.SetResult(true);  // signal thread B to run NextSession
            await Task.Delay(50, TestContext.Current.CancellationToken); // give B's NextSession time to run

            try { await ctx.SaveChangesAsync(TestContext.Current.CancellationToken); }
            catch (Exception e) { saveEx = e; }
        }, TestContext.Current.CancellationToken);

        // Thread B: call NextSession after the barrier
        await barrier.Task;
        sec.NextSession();

        await contextTask;
        Assert.IsType<OperationCanceledException>(saveEx);
    }

    // ── TC-GR-06 ────────────────────────────────────────────────────────────
    // Long-lived staging: ghost-leak check across 100 SaveDraftAsync calls.
    // A plaintext buffer allocated via GC.AllocateArray(pinned: true) must be reclaimed
    // by the GC after ZeroMemory + releasing the reference (tracked via WeakReference)

    [Fact]
    public void LongLivedStaging_PinnedBuffer_IsCollectedAfterZeroMemory()
    {
        var references = new List<WeakReference>(100);

        void AllocateAndFree()
        {
            // Simulates a POH (Pinned Object Heap) buffer: a 32-byte pinned array
            var buf = GC.AllocateArray<byte>(32, pinned: true);
            references.Add(new WeakReference(buf, trackResurrection: false));

            // Write sensitive data -> ZeroMemory -> release the reference
            Array.Fill(buf, (byte)0xAB);
            CryptographicOperations.ZeroMemory(buf.AsSpan());
            buf = null!;   // explicitly release the reference
        }

        // Repeat 100 times to confirm there's no residual accumulation across every allocation
        for (int i = 0; i < 100; i++)
        {
            AllocateAndFree();
        }

        // 2 gen-2 GC cycles can reclaim the POH too (once there's no reference)
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Core assertion: every one of the 100 weak references is gone (the GC reclaimed all of them)
        Assert.All(references, r => Assert.False(r.IsAlive,
            "The pinned array was not reclaimed by the GC even after ZeroMemory + assigning null (suspected ghost leak). " +
            "A POH array can be reclaimed by a normal GC once its reference is gone."));
    }

    // ── TC-GR-07 ────────────────────────────────────────────────────────────
    // INSERT 100 SecretDrafts rows (each with a 4KB Content blob), then DELETE them -> VACUUM must
    // reclaim at least 100KB. SaveDraftAsync uses ExecuteUpdate (UPDATE) on existing rows, which
    // produces no free pages, so testing VACUUM requires a pattern that frees pages via DELETE.
    // In-memory SQLite cannot exercise WAL/VACUUM, so file-mode SQLite is used instead.

    [Fact]
    public async Task LongLivedStaging_100Drafts_Vacuum_ShrinksBelowThreshold()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vacuum_test_{Guid.NewGuid():N}.nkdb");
        try
        {
            // ── Setup: file-mode SQLite + WAL ──
            // Pooling=False: actually closes the underlying socket the moment each connection is Close()'d.
            // This setting is essential because an idle connection left in the pool holding a WAL read
            // lock would prevent VACUUM from acquiring its exclusive lock, causing it to silently no-op.
            var connStr = $"Data Source={dbPath};Pooling=False";
            await using var setup = new SqliteConnection(connStr);
            setup.Open();
            await setup.ExecuteNonQueryAsync("PRAGMA journal_mode=WAL");

            // Establish the schema via EF Core
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connStr)
                .Options;
            using var setupCtx = new AppDbContext(options, new SessionGenerationGuardStub());
            await setupCtx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            setup.Close();

            var factory = new SharedOptionsAppDbContextFactory(options);

            // ── Insert 100 Secret and SecretDrafts rows (each with a 4KB Content blob) to consume pages ──
            var bigBlob = new byte[4096];
            RandomNumberGenerator.Fill(bigBlob);

            await using (var ctx = factory.CreateDbContext())
            {
                var secrets = Enumerable.Range(0, 100).Select(_ => MinimalSecret()).ToList();
                foreach (var s in secrets) ctx.Secrets.Add(s);
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
                foreach (var s in secrets)
                    ctx.SecretDrafts.Add(new SecretDraft { SecretId = s.Id, SnapshotBlob = bigBlob, SavedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            // ── Delete all SecretDrafts rows to generate free pages ──
            await using (var ctx = factory.CreateDbContext())
            {
                await ctx.SecretDrafts.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            }

            // ── Checkpoint the WAL so the free pages are reflected in the main DB file ──
            await using (var checkpointConn = new SqliteConnection(connStr))
            {
                checkpointConn.Open();
                await checkpointConn.ExecuteNonQueryAsync("PRAGMA wal_checkpoint(TRUNCATE)");
            }
            long sizeBeforeVacuum = new FileInfo(dbPath).Length;

            // ── Reclaim unused pages via VACUUM ──
            await using var vacuumConn = new SqliteConnection(connStr);
            vacuumConn.Open();
            await vacuumConn.ExecuteNonQueryAsync("VACUUM");
            vacuumConn.Close();

            // ── The reclaimed amount must exceed 100KB ──
            // Deleting 100 x 4KB BLOBs produces at least 400KB of free pages, so confirm that
            // VACUUM reclaims at least 100KB.
            var fileInfo = new FileInfo(dbPath);
            Assert.True(fileInfo.Exists);
            long reclaimedBytes = sizeBeforeVacuum - fileInfo.Length;
            Assert.True(reclaimedBytes > 100 * 1024,
                $"VACUUM reclaimed too little: reclaimed {reclaimedBytes} bytes, " +
                $"file size {sizeBeforeVacuum} -> {fileInfo.Length}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            var wal = dbPath + "-wal";
            var shm = dbPath + "-shm";
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);
        }
    }
}

/// <summary>Extension for SqliteConnection: a synchronous ExecuteSql helper (test-only).</summary>
file static class SqliteExtensions
{
    public static async Task ExecuteNonQueryAsync(this SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
