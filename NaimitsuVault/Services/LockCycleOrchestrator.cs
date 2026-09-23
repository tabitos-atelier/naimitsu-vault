// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

/// <summary>
/// Abstracts the ScrubViewReferences / Close operations against ShellWindow.
/// In tests, swap in RecordingShellWindowProxy to verify call order.
/// </summary>
public interface IShellWindowProxy
{
    void ScrubViewReferences();
    void Close();
}

/// <summary>
/// Pure orchestrator that extracts App.LockAsync's lock sequence.
/// The _isRunning flag prevents double execution.
/// <para>
/// Step 1: wait for in-flight save tasks to land (up to InFlightTimeoutMs) + barricade on timeout<br/>
/// Step 2: SessionLockGuard.Cancel() (send an interrupt signal to remaining tasks)<br/>
/// Step 3: ScrubViewReferences() -> Close() (tear down XAML bindings -> close the window)<br/>
/// Step 4: shellScope.Dispose() (Scoped VM ZeroMemory cascade)<br/>
/// Step 5: session.Lock() (DEK ZeroMemory)
/// </para>
/// </summary>
public sealed class LockCycleOrchestrator
{
    private bool _isRunning;

    internal int InFlightTimeoutMs { get; set; } = 800;

    /// <summary>
    /// Executes the lock sequence. Returns immediately if already running (prevents double execution).
    /// </summary>
    public async Task ExecuteAsync(
        Func<IReadOnlyList<Task>>? getInFlightTasks = null,
        Action? barricadeScope                      = null,
        Action? barricadeApp                        = null,
        Action? cancelScope                         = null,
        IShellWindowProxy? shellProxy               = null,
        Action? disposeScope                        = null,
        Action? lockSession                         = null)
    {
        if (_isRunning) return;
        _isRunning = true;
        try
        {
            // ══ Step 1: wait for in-flight save tasks to land ═══════════════════════════
            var inFlight = getInFlightTasks?.Invoke() ?? Array.Empty<Task>();
            if (inFlight.Count > 0)
            {
                var allDone = Task.WhenAll(inFlight);
                var timeout = Task.Delay(InFlightTimeoutMs);
                var winner  = await Task.WhenAny(allDone, timeout);
                if (winner == timeout && inFlight.Any(t => !t.IsCompleted))
                {
                    barricadeScope?.Invoke();
                    barricadeApp?.Invoke();
                }
            }

            // ══ Step 2: cancellation notification ═══════════════════════════════════════════
            cancelScope?.Invoke();

            // ══ Step 3: ScrubViewReferences -> Close (fixed order) ══════════════════
            shellProxy?.ScrubViewReferences();
            shellProxy?.Close();

            // ══ Step 4: scope Dispose ══════════════════════════════════════════
            disposeScope?.Invoke();

            // ══ Step 5: DEK ZeroMemory ════════════════════════════════════════════
            lockSession?.Invoke();
        }
        finally
        {
            _isRunning = false;
        }
    }
}
