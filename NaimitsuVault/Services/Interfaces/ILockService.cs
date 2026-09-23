// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

public interface ILockService
{
    void Lock();

    /// <summary>
    /// Forced lock after a plaintext export completes or a new vault is created (the session has
    /// switched to the new vault while the UI still shows the old vault's cached data).
    /// Same behavior as Lock(), plus displays guidance text in UnlockWindow's ErrorBorder.
    /// </summary>
    void LockAfterDataWrite();
}
