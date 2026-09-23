// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

/// <summary>
/// Singleton that holds the file path of the currently active vault DB.
/// AppDbContextFactory dynamically reads the path from this interface to
/// implement IDbContextFactory&lt;AppDbContext&gt;.
/// </summary>
public interface IVaultConnectionProvider
{
    /// <summary>
    /// The full path of the currently active vault DB.
    /// Null before unlock. Reset to null after LockAsync().
    /// </summary>
    string? ActiveVaultDbPath { get; }

    /// <summary>Sets the specified path as the active vault DB (called on vault entry).</summary>
    void SetActiveVault(string path);

    /// <summary>Clears the active vault DB path (called on LockAsync()).</summary>
    void ClearActiveVault();
}
