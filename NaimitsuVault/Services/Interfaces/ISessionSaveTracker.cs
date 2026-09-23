// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

/// <summary>
/// Interface implemented by Scoped ViewModels that have a long-running async save task.
/// The contract that lets LockAsync bulk-scan CurrentSaveTask via SessionTaskRegistry.
/// </summary>
public interface ISessionSaveTracker
{
    Task? CurrentSaveTask { get; }
}
