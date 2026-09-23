// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// A test-only IVaultConnectionProvider stub.
/// Actually retains the path passed to SetActiveVault (NullConnectionProvider is always null).
/// Used by tests that need a real ActiveVaultDbPath value.
/// </summary>
internal sealed class RecordingConnectionProvider : IVaultConnectionProvider
{
    public string? ActiveVaultDbPath { get; private set; }
    public void SetActiveVault(string path) => ActiveVaultDbPath = path;
    public void ClearActiveVault() => ActiveVaultDbPath = null;
}
