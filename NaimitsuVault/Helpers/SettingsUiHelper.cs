// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Helpers;

/// <summary>
/// UI decision logic shared by the settings screen's two tabs (AppCommonSettingsControl/VaultSettingsControl).
/// Since both controls reference the same AppSettingsViewModel/VaultOperationsViewModel instance,
/// the decision logic is centralized here, and each control's code-behind only holds delegation for x:Bind.
/// </summary>
internal static class SettingsUiHelper
{
    /// <summary>
    /// Determines whether to disable both tabs while either VM is IsBusy (prevents a concurrent-operation race).
    /// Symmetric because it's an AND condition (true only when both are false), so argument order doesn't affect the result.
    /// </summary>
    internal static bool BothIdle(bool vaultBusy, bool appBusy) => !vaultBusy && !appBusy;
}
