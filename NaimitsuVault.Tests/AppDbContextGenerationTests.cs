// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;

namespace NaimitsuVault.Tests;

/// <summary>
/// AppDbContext generation checks (the SaveChangesAsync override) (TC-DBX-01..10, 15).
/// ThrowIfInvalid is controlled externally via ControlledSessionGenerationGuard.
/// TC-DBX-11..14 overlapped with GenerationRaceConditionTests, so they were removed and consolidated there.
/// </summary>
public sealed class AppDbContextGenerationTests
{
    private static Secret MinimalSecret() => new()
    {
        Title    = new byte[28],
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── TC-DBX-01 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_MatchingGeneration_CommitsSuccessfully()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        ctx.Secrets.Add(MinimalSecret());
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        var count = await ctx.Secrets.CountAsync(TestContext.Current.CancellationToken);
        Assert.True(count >= 1);
    }

    // ── TC-DBX-02 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_SessionAdvanced_ThrowsOCE()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(GetConnStr(db))
            .Options;
        await using var ctx = new AppDbContext(opts, sec);
        ctx.Secrets.Add(MinimalSecret());

        sec.NextSession(); // advance the generation -> this ctx is now an old generation

        await Assert.ThrowsAsync<OperationCanceledException>(() => ctx.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // ── TC-DBX-03 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_Barricaded_ThrowsOCE()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        ctx.Secrets.Add(MinimalSecret());
        sec.Barricade();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ctx.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // ── TC-DBX-04 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_SessionMismatch_NoPhysicalWrite()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        ctx.Secrets.Add(MinimalSecret());
        sec.NextSession(); // becomes an old generation

        try { await ctx.SaveChangesAsync(TestContext.Current.CancellationToken); } catch (OperationCanceledException) { }

        // No physical write occurred
        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(0, await verify.Secrets.CountAsync(TestContext.Current.CancellationToken));
    }

    // ── TC-DBX-05 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_TwoArg_AcceptAllChanges_AlsoChecksGeneration()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        ctx.Secrets.Add(MinimalSecret());
        sec.NextSession();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ctx.SaveChangesAsync(acceptAllChangesOnSuccess: true, TestContext.Current.CancellationToken));
    }

    // ── TC-DBX-06 ─────────────────────────────────────────────────────────────

    [Fact]
    public void SaveChanges_Sync_AlwaysThrowsNotSupportedException()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        Assert.Throws<NotSupportedException>(() => ctx.SaveChanges());
    }

    // ── TC-DBX-07 ─────────────────────────────────────────────────────────────

    [Fact]
    public void SaveChanges_Sync_2Arg_AlwaysThrowsNotSupportedException()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        Assert.Throws<NotSupportedException>(() => ctx.SaveChanges(true));
    }

    // ── TC-DBX-08 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_PreCanceledToken_ThrowsOCE()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        ctx.Secrets.Add(MinimalSecret());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ctx.SaveChangesAsync(cts.Token));
    }

    // ── TC-DBX-09 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_AfterMultipleNextSessions_OldContextRejected()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        ctx.Secrets.Add(MinimalSecret());

        for (int i = 0; i < 5; i++) sec.NextSession();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ctx.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // ── TC-DBX-10 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveChangesAsync_NewContextAfterNextSession_Succeeds()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        sec.NextSession();

        // Create a new context in the new generation -> should succeed
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        ctx.Secrets.Add(MinimalSecret());
        var ex = await Record.ExceptionAsync(() => ctx.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Null(ex);
    }

    // TC-DBX-11..14 overlapped with what TC-GR-01/02/03/05 in GenerationRaceConditionTests
    // verified, so they were removed and consolidated there.

    // ── TC-DBX-15 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AfterBarricade_Then_NextSession_OldContextStillRejected()
    {
        using var db  = TestDb.Create();
        var sec = new ControlledSessionGenerationGuard();
        await using var ctx = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(GetConnStr(db)).Options, sec);
        ctx.Secrets.Add(MinimalSecret());
        sec.Barricade();
        sec.NextSession(); // barricade released, generation becomes 2
        // The old-generation context (snapshotted at ID=1) is rejected due to the generation mismatch
        await Assert.ThrowsAsync<OperationCanceledException>(() => ctx.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string GetConnStr(TestDb db)
    {
        using var ctx = db.Factory.CreateDbContext();
        return ctx.Database.GetConnectionString()!;
    }
}
