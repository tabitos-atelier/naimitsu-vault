// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Verifies the execution order of LockAsync's 5 steps (TC-TSF-06..12).
/// Calls LockCycleOrchestrator.ExecuteAsync() directly to verify the ordering invariant.
/// </summary>
public sealed class LockCycleStepOrderTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static LockCycleOrchestrator FastOrchestrator(int timeoutMs = 50)
        => new() { InFlightTimeoutMs = timeoutMs };

    // ── TC-TSF-06 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LockCycle_ScrubViewReferences_Called_Before_Close()
    {
        var orch  = FastOrchestrator();
        var proxy = new RecordingShellWindowProxy();

        await orch.ExecuteAsync(shellProxy: proxy);

        var scrubIdx = proxy.Calls.ToList().IndexOf("ScrubViewReferences");
        var closeIdx = proxy.Calls.ToList().IndexOf("Close");
        Assert.True(scrubIdx >= 0, "ScrubViewReferences was not called");
        Assert.True(closeIdx >= 0, "Close was not called");
        Assert.True(scrubIdx < closeIdx,
            $"ScrubViewReferences({scrubIdx}) must be called before Close({closeIdx})");
    }

    // ── TC-TSF-07 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LockCycle_Close_Called_Before_ScopeDispose()
    {
        var orch    = FastOrchestrator();
        var calls   = new List<string>();

        // Also add the proxy's Close to calls
        await orch.ExecuteAsync(
            shellProxy:   new DelegatingProxy(
                              scrub: () => calls.Add("ScrubViewReferences"),
                              close: () => calls.Add("Close")),
            disposeScope: () => calls.Add("ScopeDispose"));

        var closeIdx   = calls.IndexOf("Close");
        var disposeIdx = calls.IndexOf("ScopeDispose");
        Assert.True(closeIdx >= 0,   "Close was not called");
        Assert.True(disposeIdx >= 0, "ScopeDispose was not called");
        Assert.True(closeIdx < disposeIdx,
            $"Close({closeIdx}) must come before ScopeDispose({disposeIdx})");
    }

    // ── TC-TSF-08 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LockCycle_ScopeDispose_Called_Before_SessionLock()
    {
        var orch  = FastOrchestrator();
        var calls = new List<string>();

        await orch.ExecuteAsync(
            disposeScope:  () => calls.Add("ScopeDispose"),
            lockSession:   () => calls.Add("SessionLock"));

        var disposeIdx = calls.IndexOf("ScopeDispose");
        var lockIdx    = calls.IndexOf("SessionLock");
        Assert.True(disposeIdx >= 0, "ScopeDispose was not called");
        Assert.True(lockIdx    >= 0, "SessionLock was not called");
        Assert.True(disposeIdx < lockIdx,
            $"ScopeDispose({disposeIdx}) must come before SessionLock({lockIdx})");
    }

    // ── TC-TSF-09 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LockCycle_IsRunning_Guard_Prevents_Concurrent_Second_ExecuteAsync()
    {
        var orch = FastOrchestrator(timeoutMs: 10);

        // first: suspend ExecuteAsync on an in-flight task (releases control via await)
        var tcs          = new TaskCompletionSource<bool>();
        int lockCallCount = 0;

        var firstTask = orch.ExecuteAsync(
            getInFlightTasks: () => [tcs.Task],
            lockSession:      () => Interlocked.Increment(ref lockCallCount));

        // first has released control via await WhenAny. Call second while
        // _isRunning == true.
        // Note: in an environment without a SynchronizationContext, first's await may continue
        //     immediately, but as long as second is called before firstTask completes,
        //     the _isRunning guard is exercised.

        // second: should return immediately since _isRunning == true
        await orch.ExecuteAsync(lockSession: () => Interlocked.Increment(ref lockCallCount));

        // complete first
        tcs.TrySetResult(true);
        await firstTask;

        // lockSession should have been called exactly once (second was blocked)
        Assert.Equal(1, lockCallCount);
    }

    // ── TC-TSF-10 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LockCycle_InFlight_800ms_Timeout_Both_Barricades_Set_Before_Scrub()
    {
        var orch    = FastOrchestrator(timeoutMs: 50); // force a timeout at 50ms
        var calls   = new List<string>();

        // the in-flight task never completes (timeout is guaranteed)
        var neverComplete = new TaskCompletionSource<bool>();

        await orch.ExecuteAsync(
            getInFlightTasks: () => [neverComplete.Task],
            barricadeScope:   () => calls.Add("BarricadeScope"),
            barricadeApp:     () => calls.Add("BarricadeApp"),
            shellProxy:       new DelegatingProxy(
                                  scrub: () => calls.Add("ScrubViewReferences"),
                                  close: () => calls.Add("Close")),
            disposeScope:     () => calls.Add("ScopeDispose"),
            lockSession:      () => calls.Add("SessionLock"));

        // both barricades were called
        Assert.Contains("BarricadeScope", calls);
        Assert.Contains("BarricadeApp",   calls);

        // barricades come before ScrubViewReferences
        var barricadeScopeIdx = calls.IndexOf("BarricadeScope");
        var barricadeAppIdx   = calls.IndexOf("BarricadeApp");
        var scrubIdx          = calls.IndexOf("ScrubViewReferences");
        Assert.True(barricadeScopeIdx < scrubIdx);
        Assert.True(barricadeAppIdx   < scrubIdx);
    }

    // ── TC-TSF-11 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LockCycle_InFlight_Completes_Within_800ms_No_Barricade_Set()
    {
        var orch   = FastOrchestrator(timeoutMs: 200); // 200ms timeout
        bool barricadeCalled = false;

        // the in-flight task completes in 10ms (before the timeout)
        var quickTask = Task.Delay(10, TestContext.Current.CancellationToken);

        await orch.ExecuteAsync(
            getInFlightTasks: () => [quickTask],
            barricadeScope:   () => barricadeCalled = true,
            barricadeApp:     () => barricadeCalled = true);

        Assert.False(barricadeCalled, "barricades must not be called on normal completion");
    }

    // ── TC-TSF-12 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LockCycle_Step2_Cancel_Always_Called_Regardless_Of_Barricade_Status()
    {
        var orch    = FastOrchestrator();
        int cancelCount = 0;

        // no in-flight task (timeout never triggers)
        await orch.ExecuteAsync(
            cancelScope: () => Interlocked.Increment(ref cancelCount));

        Assert.Equal(1, cancelCount);
    }

    // ── Test-only helper ─────────────────────────────────────────────────

    private sealed class DelegatingProxy(Action scrub, Action close) : IShellWindowProxy
    {
        public void ScrubViewReferences() => scrub();
        public void Close()               => close();
    }
}
