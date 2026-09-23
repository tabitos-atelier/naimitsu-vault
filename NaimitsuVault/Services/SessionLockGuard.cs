// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

/// <summary>
/// Scoped service that encapsulates authenticated-session-scope cancellation and the write barricade in one place.
/// The time gap between Barricade() and Cancel() (Step 1 and Step 2) is strictly managed by LockAsync.
/// </summary>
public sealed class SessionLockGuard : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _barricaded;

    public CancellationToken Token => _cts.Token;

    /// <summary>LockAsync Step 1: called when the 800ms timeout is exceeded. Activates gates 1 and 2.</summary>
    internal void Barricade() => _barricaded = true;

    /// <summary>LockAsync Step 2: called after Barricade(). Sends an interrupt signal to remaining tasks.</summary>
    public void Cancel() => _cts.Cancel();

    /// <summary>
    /// The shared gate check for defense layers 1 and 2. Throws immediately if barricaded or canceled.
    /// </summary>
    public void ThrowIfLocked()
    {
        if (_barricaded)
            throw new OperationCanceledException("Writes were blocked by the session lock.");
        _cts.Token.ThrowIfCancellationRequested();
    }

    public void Dispose() => _cts.Dispose();
}
