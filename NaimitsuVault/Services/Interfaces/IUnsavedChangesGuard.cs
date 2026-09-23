// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

/// <summary>
/// In-memory dirty-tracking contract used by ShellWindow (see GetCurrentGuard/ValidateSettingsBeforeLeaveAsync)
/// to force a silent save before leaving a page. This has no UI dialog of its own and is unrelated to the
/// "confirm save?" dialog removed by the dialog-free draft-autosave architecture - that removal only deleted
/// dead branches inside consumers (e.g. the `if (IsNew) { ... }` block once found in
/// SecretsViewModel.AutoSaveOnNavigateAsync). Implemented today by SecretsViewModel; still actively referenced
/// by ShellWindow.xaml.cs, so do not remove as "dead code" without re-checking both call sites.
/// </summary>
public interface IUnsavedChangesGuard
{
    bool HasUnsavedChanges { get; }
    Task GuardedSaveAsync();
    void DiscardChanges();
}
