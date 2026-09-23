// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Auto-lock boundary values" - all 6 cases for IdleTimeoutService (Phase 2).
///
/// Controls the clock without a DispatcherQueue by injecting <c>Func&lt;DateTime&gt;</c>.
/// Simulates elapsed time by rewriting the mutable array <c>fakeNow[0]</c> around calls
/// to <c>ResetTimer</c>.
///
/// The old IdleTimeoutServiceCycleTests (TC-TSF-01..05) overlapped with what this class's
/// TC-IDL-01/TC-IDL-02/TC-IDL-06/TC-IDL-03/TC-IDL-04 verified, so it was removed and consolidated here.
/// </summary>
public sealed class IdleTimeoutServiceTests
{
    /// <summary>
    /// Test factory.
    /// Manipulating fakeNow[0] allows injecting an arbitrary time.
    /// </summary>
    private static (IdleTimeoutService svc, List<int> callLog, DateTime[] fakeNow) Build(
        int timeoutMinutes = 5)
    {
        var origin  = DateTime.UtcNow;
        var fakeNow = new[] { origin }; // swap the time via a mutable reference
        var log     = new List<int>();
        var svc     = new IdleTimeoutService(
            nowFactory:   () => fakeNow[0],
            lockCallback: () => log.Add(1));
        svc.TimeoutMinutes = timeoutMinutes;
        // Right after construction, _lastInputTime == origin == fakeNow[0], so idle time = 0
        return (svc, log, fakeNow);
    }

    // ── TC-IDL-01 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckNow_AfterTimeout_InvokesLockCallback()
    {
        // Arrange - 5-minute timeout, simulate 6 minutes elapsed
        var (svc, log, fakeNow) = Build(timeoutMinutes: 5);
        fakeNow[0] = fakeNow[0].AddMinutes(6);

        // Act
        svc.CheckNow();

        // Assert
        Assert.Single(log);
    }

    // ── TC-IDL-02 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckNow_BeforeTimeout_DoesNotInvokeCallback()
    {
        // Arrange - 5-minute timeout, 4 minutes elapsed
        var (svc, log, fakeNow) = Build(timeoutMinutes: 5);
        fakeNow[0] = fakeNow[0].AddMinutes(4);

        // Act
        svc.CheckNow();

        // Assert
        Assert.Empty(log);
    }

    // ── TC-IDL-03 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ResetTimer_ResetsIdleTime_DefersTimeout()
    {
        // Arrange - ResetTimer after 4 minutes elapsed, then 2 more minutes elapse
        var (svc, log, fakeNow) = Build(timeoutMinutes: 5);
        fakeNow[0] = fakeNow[0].AddMinutes(4);

        // Act - after the reset, _lastInputTime is updated to the current time
        svc.ResetTimer();                         // _lastInputTime = origin + 4min
        fakeNow[0] = fakeNow[0].AddMinutes(2);   // elapsed time = 2min (< 5min)
        svc.CheckNow();

        // Assert - only 2 minutes since the reset point, so not timed out
        Assert.Empty(log);
    }

    // ── TC-IDL-04 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TimeoutMinutes_Zero_ClampedToMinimum1()
    {
        // Arrange - TimeoutMinutes=0 is clamped to Math.Max(1, 0)=1 minute
        var (svc, log, fakeNow) = Build(timeoutMinutes: 0);
        fakeNow[0] = fakeNow[0].AddSeconds(30); // 30 seconds elapsed -> under the 1-minute threshold

        // Act
        svc.CheckNow();

        // Assert - callback not invoked
        Assert.Empty(log);
    }

    // ── TC-IDL-05 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TimeoutMinutes_LargeValue_AcceptedWithoutCrash()
    {
        // Arrange - 9999 minutes is accepted (no upper bound)
        var (svc, _, _) = Build(timeoutMinutes: 9999);

        // Act & Assert - it can be set, and CheckNow does not crash
        Assert.Equal(9999, svc.TimeoutMinutes);
        svc.CheckNow(); // no exception
    }

    // ── TC-IDL-06 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_False_CheckNowDoesNotInvokeCallback()
    {
        // Arrange - the timeout is exceeded, but IsEnabled=false
        var (svc, log, fakeNow) = Build(timeoutMinutes: 5);
        svc.IsEnabled = false;
        fakeNow[0] = fakeNow[0].AddMinutes(10);

        // Act
        svc.CheckNow();

        // Assert
        Assert.Empty(log);
    }

    // ── TC-IDL-07 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckNow_CalledTwiceAfterTimeout_InvokesCallbackOnlyOnce()
    {
        // Arrange - simulates the periodic Tick and a reactivation-driven CheckNow() both
        // observing "expired" for the same idle window (e.g. ShellWindow_Activated racing
        // IdleTimeoutService's own timer). Only the first should fire the lock callback;
        // ResetTimer() (a real unlock/activity) is required before it can fire again.
        var (svc, log, fakeNow) = Build(timeoutMinutes: 5);
        fakeNow[0] = fakeNow[0].AddMinutes(6);

        // Act - two checks land back-to-back, neither preceded by a ResetTimer()
        svc.CheckNow();
        svc.CheckNow();

        // Assert
        Assert.Single(log);
    }
}
