// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Singleton implementation of IVaultConnectionProvider.
/// AppDbContextFactory reads the path from this instance when calling CreateDbContext().
/// </summary>
public sealed class VaultConnectionProvider : IVaultConnectionProvider
{
    private volatile string? _activePath;

    public string? ActiveVaultDbPath => _activePath;

    public void SetActiveVault(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Vault DB path is empty.", nameof(path));
        _activePath = path;
    }

    public void ClearActiveVault() => _activePath = null;
}
