// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

public interface IWindowService
{
    void OpenViewer(int fileId);
    void CloseViewer(int fileId);
    void CloseAllViewers();

    /// <summary>
    /// Registers a page-owned ContentDialog for guaranteed teardown during lock. <paramref name="closer"/>
    /// must both request the dialog's close (e.g. dialog.Hide()) and return a Task that completes only
    /// once the caller's own cleanup (SecureCharBuffer ZeroMemory, etc.) has actually run - not just once
    /// Hide() returns, since ContentDialog.Hide() resolves the awaiting ShowAsync() asynchronously (a
    /// later dispatcher tick), so awaiting Hide() itself proves nothing about whether cleanup has completed.
    /// </summary>
    void RegisterActiveDialogCloser(Func<Task> closer);
    void UnregisterActiveDialogCloser(Func<Task> closer);

    /// <summary>
    /// Called from the lock sequence, before the owning window is scrubbed/closed. Bounded by an
    /// internal timeout so a stuck dialog can never block Lock() itself.
    /// </summary>
    Task CloseActiveDialogsAsync();
}
