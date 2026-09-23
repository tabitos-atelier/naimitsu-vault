// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// A test-only stub that lets ThrowIfInvalid, NextSession, and Barricade be controlled
/// from the outside.
/// Used to inject SessionGenerationGuard's behavior into AppDbContext unit tests.
/// </summary>
internal sealed class ControlledSessionGenerationGuard : ISessionGenerationGuard
{
    private volatile bool _barricade;
    private long _currentId = 1L;

    public long CurrentSessionId => Interlocked.Read(ref _currentId);
    public bool IsBarricaded     => _barricade;

    public void Barricade() => _barricade = true;

    public void NextSession()
    {
        Interlocked.Increment(ref _currentId);
        _barricade = false;
    }

    public void ThrowIfInvalid(long capturedSessionId)
    {
        if (_barricade || Interlocked.Read(ref _currentId) != capturedSessionId)
            throw new OperationCanceledException(
                "ControlledSessionGenerationGuard: session invalid or barricaded.");
    }

    /// <summary>Force-sets an arbitrary generation ID (test-only).</summary>
    public void ForceSessionId(long id) => Interlocked.Exchange(ref _currentId, id);
}
