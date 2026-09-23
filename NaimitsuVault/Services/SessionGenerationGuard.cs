// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public sealed class SessionGenerationGuard : ISessionGenerationGuard
{
    private volatile bool _barricaded;
    // Session generation counter. Starts at 1 when the app launches.
    // Operated on via Interlocked to guarantee long's atomicity even on 32-bit environments.
    private long _sessionId = 1L;

    public long CurrentSessionId => Interlocked.Read(ref _sessionId);
    public bool IsBarricaded     => _barricaded;

    public void Barricade() => _barricaded = true;

    public void NextSession()
    {
        // Advance the session generation before releasing the barricade.
        // Interlocked.Increment issues a full memory barrier, so _barricaded = false
        // is always made visible after the Increment.
        Interlocked.Increment(ref _sessionId);
        _barricaded = false;
    }

    public void ThrowIfInvalid(long capturedSessionId)
    {
        // Dies immediately if either barricaded OR the generation mismatches (a zombie AppDbContext from an old session).
        if (_barricaded || Interlocked.Read(ref _sessionId) != capturedSessionId)
            throw new OperationCanceledException(
                "Rejected due to a session generation mismatch or a write barricade (AppDbContext core guard).");
    }
}
