// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// A test-only IVaultConnectionProvider stub.
/// The active vault path always returns null. For tests that don't use multi-vault features.
/// </summary>
internal sealed class NullConnectionProvider : IVaultConnectionProvider
{
    public string? ActiveVaultDbPath => null;
    public void SetActiveVault(string path) { }
    public void ClearActiveVault() { }
}
