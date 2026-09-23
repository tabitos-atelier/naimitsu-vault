// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Dispatching;

namespace NaimitsuVault.Services;

public sealed class IdleTimeoutService
{
    private DispatcherQueueTimer? _timer;
    private Action? _lockCallback;
    // Judging by absolute time rather than a relative timer lets this survive OS suspend
    private DateTime _lastInputTime;
    // Injection point for controlling time in tests. Returns DateTime.UtcNow in production.
    private readonly Func<DateTime> _nowFactory;
    // Guards against firing _lockCallback twice for the same idle-expiry window: the periodic
    // Tick and an externally-driven CheckNow() (e.g. ShellWindow_Activated on wake-from-sleep) can
    // both observe "expired" within the same moment before either side has a chance to stop the
    // timer. Cleared only by Initialize()/ResetTimer() (i.e. a genuine reset, not just a re-check).
    private bool _lockTriggered;

    public bool IsEnabled { get; set; } = true;
    public int TimeoutMinutes { get; set; } = 5;

    public IdleTimeoutService()
    {
        _nowFactory    = () => DateTime.UtcNow;
        _lastInputTime = _nowFactory();
    }

    /// <summary>
    /// Test-only constructor. Lets CheckNow() / ResetTimer() be tested directly without a DispatcherQueue.
    /// Wrapping nowFactory in a mutable array allows rewinding the clock.
    /// </summary>
    internal IdleTimeoutService(Func<DateTime> nowFactory, Action lockCallback)
    {
        _nowFactory    = nowFactory;
        _lockCallback  = lockCallback;
        _lastInputTime = nowFactory();
    }

    /// <summary>
    /// Initializes the timer on the UI thread's DispatcherQueue. Called from ShellWindow.Activated.
    /// </summary>
    public void Initialize(DispatcherQueue queue, Action lockCallback)
    {
        Stop();
        _lockCallback  = lockCallback;
        _lastInputTime = _nowFactory();
        _lockTriggered = false;
        _timer = queue.CreateTimer();
        _timer.IsRepeating = true;
        // Check the absolute time difference every 30 seconds; this also doubles as polling after OS sleep resume
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += (_, _) => CheckAndLockIfExpired();
        if (IsEnabled) _timer.Start();
    }

    private void CheckAndLockIfExpired()
    {
        if (!IsEnabled || _lockCallback == null || _lockTriggered) return;
        if ((_nowFactory() - _lastInputTime).TotalMinutes >= Math.Max(1, TimeoutMinutes))
        {
            // Set before Invoke() (and stop the timer immediately) so a second entry racing in
            // right behind this one - another Tick, or a CheckNow() from window reactivation -
            // can never invoke the callback a second time before the lock cycle it kicked off
            // gets around to tearing this service down itself.
            _lockTriggered = true;
            _timer?.Stop();
            _lockCallback.Invoke();
        }
    }

    /// <summary>
    /// Locks immediately if the timeout has already been exceeded at the time of the call.
    /// Called when ShellWindow is reactivated (including resuming from sleep).
    /// </summary>
    public void CheckNow() => CheckAndLockIfExpired();

    /// <summary>
    /// Called when user activity is detected. Updates the last-input time and resets the timeout count.
    /// Updates _lastInputTime even when _timer is null (test environment / Initialize not called).
    /// </summary>
    public void ResetTimer()
    {
        // Track the input time even while disabled, so that enabling auto-lock later doesn't see
        // a stale _lastInputTime from before the toggle and lock immediately on the next check.
        _lastInputTime = _nowFactory();
        if (!IsEnabled) return;
        _lockTriggered = false;
        _timer?.Start(); // Resume if it was stopped by Stop() (e.g. after IsBusy is cleared)
    }

    public void Stop() => _timer?.Stop();
}
