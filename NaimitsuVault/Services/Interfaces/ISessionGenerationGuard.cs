// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

/// <summary>
/// Singleton service that centrally manages the app-wide lock/barricade state and session generation.
/// The session generation counter invalidates zombie tasks left over from a previous session by
/// making their captured generation ID mismatch the current one.
/// </summary>
public interface ISessionGenerationGuard
{
    /// <summary>The current session generation ID. VaultDbContext snapshots this in its constructor.</summary>
    long CurrentSessionId { get; }

    bool IsBarricaded { get; }

    /// <summary>Called when LockAsync Step 1's timeout is exceeded. Sets <see cref="IsBarricaded"/> to true.</summary>
    void Barricade();

    /// <summary>
    /// Called after successful re-authentication, before DatabaseInitializer.InitializeAsync().
    /// Clears the barricade and advances to a new session generation.
    /// </summary>
    void NextSession();

    /// <summary>
    /// Called from the VaultDbContext.SaveChangesAsync() override.
    /// Throws OperationCanceledException if barricaded or if the session generation mismatches.
    /// </summary>
    void ThrowIfInvalid(long capturedSessionId);
}
