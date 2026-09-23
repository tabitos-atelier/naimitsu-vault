// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// SessionLockGuard barricade/cancel tests (TC-SLG-01 .. TC-SLG-14)
/// </summary>
public sealed class SessionLockGuardTests
{
    // ── TC-SLG-01 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Fresh_Guard_ThrowIfLocked_DoesNotThrow()
    {
        using var guard = new SessionLockGuard();
        var ex = Record.Exception(() => guard.ThrowIfLocked());
        Assert.Null(ex);
    }

    // ── TC-SLG-02 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Barricade_Then_ThrowIfLocked_ThrowsOCE()
    {
        using var guard = new SessionLockGuard();
        guard.Barricade();
        Assert.Throws<OperationCanceledException>(() => guard.ThrowIfLocked());
    }

    // ── TC-SLG-03 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Cancel_Then_ThrowIfLocked_ThrowsOCE()
    {
        using var guard = new SessionLockGuard();
        guard.Cancel();
        Assert.Throws<OperationCanceledException>(() => guard.ThrowIfLocked());
    }

    // ── TC-SLG-04 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Barricade_And_Cancel_BothSet_ThrowsOCE()
    {
        using var guard = new SessionLockGuard();
        guard.Barricade();
        guard.Cancel();
        Assert.Throws<OperationCanceledException>(() => guard.ThrowIfLocked());
    }

    // ── TC-SLG-05 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Barricade_Only_Without_Cancel_StillBlocks()
    {
        using var guard = new SessionLockGuard();
        guard.Barricade();
        // Even without calling Cancel(), the barricade alone blocks ThrowIfLocked
        Assert.Throws<OperationCanceledException>(() => guard.ThrowIfLocked());
    }

    // ── TC-SLG-06 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Token_BeforeCancel_IsNotCanceled()
    {
        using var guard = new SessionLockGuard();
        Assert.False(guard.Token.IsCancellationRequested);
    }

    // ── TC-SLG-07 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Token_AfterCancel_IsCanceled()
    {
        using var guard = new SessionLockGuard();
        guard.Cancel();
        Assert.True(guard.Token.IsCancellationRequested);
    }

    // ── TC-SLG-08 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Token_AfterBarricadeOnly_IsNotCanceled()
    {
        using var guard = new SessionLockGuard();
        guard.Barricade();
        // The barricade does not cancel the CTS (it is an independent layer of defense)
        Assert.False(guard.Token.IsCancellationRequested);
    }

    // ── TC-SLG-09 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_50Threads_BarricadeAndThrowIfLocked_AllSubsequentBlock()
    {
        using var guard = new SessionLockGuard();
        var barricadeDone = new TaskCompletionSource<bool>();
        int oce = 0;
        int pass = 0;

        var threads = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            await barricadeDone.Task;
            try { guard.ThrowIfLocked(); Interlocked.Increment(ref pass); }
            catch (OperationCanceledException) { Interlocked.Increment(ref oce); }
        })).ToArray();

        guard.Barricade();
        barricadeDone.SetResult(true);
        await Task.WhenAll(threads);

        Assert.Equal(0, pass);
        Assert.Equal(50, oce);
    }

    // ── TC-SLG-10 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_CancelAndThrowIfLocked_AllSubsequentBlock()
    {
        using var guard = new SessionLockGuard();
        var cancelDone = new TaskCompletionSource<bool>();
        int oce = 0;

        var threads = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            await cancelDone.Task;
            try { guard.ThrowIfLocked(); }
            catch (OperationCanceledException) { Interlocked.Increment(ref oce); }
        })).ToArray();

        guard.Cancel();
        cancelDone.SetResult(true);
        await Task.WhenAll(threads);

        Assert.Equal(50, oce);
    }

    // ── TC-SLG-11 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_After_Cancel_DoesNotThrow()
    {
        var guard = new SessionLockGuard();
        guard.Cancel();
        var ex = Record.Exception(() => guard.Dispose());
        Assert.Null(ex);
    }

    // ── TC-SLG-12 ─────────────────────────────────────────────────────────────

    [Fact]
    public void ThrowIfLocked_OceMessage_Contains_LockKeyword()
    {
        using var guard = new SessionLockGuard();
        guard.Barricade();
        var ex = Assert.Throws<OperationCanceledException>(() => guard.ThrowIfLocked());
        Assert.Contains("lock", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── TC-SLG-13 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task InFlightAsync_CancelledByBarricadeAndCancel_CompletesWithOCE()
    {
        using var guard = new SessionLockGuard();

        var task = Task.Run(async () =>
        {
            guard.ThrowIfLocked(); // passes at the start
            await Task.Delay(200, guard.Token); // receives the cancellation while waiting
        }, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        guard.Barricade();
        guard.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.NotNull(ex);
    }

    // ── TC-SLG-14 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TwoGuards_Independent_OneBarricadedOtherNot()
    {
        using var guardA = new SessionLockGuard();
        using var guardB = new SessionLockGuard();

        guardA.Barricade();

        Assert.Throws<OperationCanceledException>(() => guardA.ThrowIfLocked());
        var ex = Record.Exception(() => guardB.ThrowIfLocked());
        Assert.Null(ex);
    }
}
