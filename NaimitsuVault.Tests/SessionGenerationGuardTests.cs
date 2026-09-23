// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Direct tests for SessionGenerationGuard (TC-SC-01 .. TC-SC-12)
/// </summary>
public sealed class SessionGenerationGuardTests
{
    // ── TC-SC-01 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Initial_SessionId_Is_One()
    {
        var ctx = new SessionGenerationGuard();
        Assert.Equal(1L, ctx.CurrentSessionId);
    }

    // ── TC-SC-02 ──────────────────────────────────────────────────────────────

    [Fact]
    public void NextSession_Increments_SessionId()
    {
        var ctx = new SessionGenerationGuard();
        ctx.NextSession();
        ctx.NextSession();
        ctx.NextSession();
        Assert.Equal(4L, ctx.CurrentSessionId);
    }

    // ── TC-SC-03 ──────────────────────────────────────────────────────────────

    [Fact]
    public void NextSession_IncrementIsStrictlyMonotonic()
    {
        var ctx = new SessionGenerationGuard();
        long prev = ctx.CurrentSessionId;
        for (int i = 0; i < 10; i++)
        {
            ctx.NextSession();
            long current = ctx.CurrentSessionId;
            Assert.Equal(prev + 1, current);
            prev = current;
        }
    }

    // ── TC-SC-04 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Barricade_Sets_IsBarricaded_True()
    {
        var ctx = new SessionGenerationGuard();
        Assert.False(ctx.IsBarricaded);
        ctx.Barricade();
        Assert.True(ctx.IsBarricaded);
    }

    // ── TC-SC-05 ──────────────────────────────────────────────────────────────

    [Fact]
    public void NextSession_Resets_IsBarricaded_To_False()
    {
        var ctx = new SessionGenerationGuard();
        ctx.Barricade();
        Assert.True(ctx.IsBarricaded);
        ctx.NextSession();
        Assert.False(ctx.IsBarricaded);
    }

    // ── TC-SC-06 ──────────────────────────────────────────────────────────────

    [Fact]
    public void ThrowIfInvalid_MatchingId_DoesNotThrow()
    {
        var ctx = new SessionGenerationGuard();
        var ex = Record.Exception(() => ctx.ThrowIfInvalid(1L));
        Assert.Null(ex);
    }

    // ── TC-SC-07 ──────────────────────────────────────────────────────────────

    [Fact]
    public void ThrowIfInvalid_StaleId_ThrowsOCE()
    {
        var ctx = new SessionGenerationGuard();
        Assert.Throws<OperationCanceledException>(() => ctx.ThrowIfInvalid(0L));
    }

    // ── TC-SC-08 ──────────────────────────────────────────────────────────────

    [Fact]
    public void ThrowIfInvalid_FutureId_ThrowsOCE()
    {
        var ctx = new SessionGenerationGuard();
        Assert.Throws<OperationCanceledException>(() => ctx.ThrowIfInvalid(99L));
    }

    // ── TC-SC-09 ──────────────────────────────────────────────────────────────

    [Fact]
    public void ThrowIfInvalid_Barricaded_ThrowsEvenIfIdMatches()
    {
        var ctx = new SessionGenerationGuard();
        ctx.Barricade();
        Assert.Throws<OperationCanceledException>(() => ctx.ThrowIfInvalid(1L));
    }

    // ── TC-SC-10 ──────────────────────────────────────────────────────────────

    [Fact]
    public void AfterNextSession_NewIdPasses_OldIdRejected()
    {
        var ctx = new SessionGenerationGuard();
        ctx.Barricade();
        ctx.NextSession();
        long newId = ctx.CurrentSessionId; // == 2

        var ex1 = Record.Exception(() => ctx.ThrowIfInvalid(newId));
        Assert.Null(ex1);

        Assert.Throws<OperationCanceledException>(() => ctx.ThrowIfInvalid(1L));
    }

    // ── TC-SC-11 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentNextSession_50Threads_IsAtomicallyIncremented()
    {
        var ctx = new SessionGenerationGuard();
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => ctx.NextSession(), TestContext.Current.CancellationToken))
            .ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(51L, ctx.CurrentSessionId);
    }

    // ── TC-SC-12 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentBarricadeAndThrowIfInvalid_BarricadeAlwaysWins()
    {
        var ctx = new SessionGenerationGuard();
        long id = ctx.CurrentSessionId;

        // Issue Barricade right before 100 threads call ThrowIfInvalid.
        // Confirm that every ThrowIfInvalid executed after Barricade() returns OCE.
        var barricadeDone = new TaskCompletionSource<bool>();
        int oceCount = 0;
        int passCount = 0;

        var readers = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            await barricadeDone.Task; // wait until Barricade completes
            try { ctx.ThrowIfInvalid(id); Interlocked.Increment(ref passCount); }
            catch (OperationCanceledException) { Interlocked.Increment(ref oceCount); }
        })).ToArray();

        ctx.Barricade();
        barricadeDone.SetResult(true);
        await Task.WhenAll(readers);

        // Every thread executed after Barricade receives OCE (pass count is 0)
        Assert.Equal(0, passCount);
        Assert.Equal(100, oceCount);
    }
}
