// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests.Stubs;

/// <summary>No-op IWindowService stub for ViewModel tests that don't exercise the image viewer window.</summary>
internal sealed class NullWindowService : IWindowService
{
    public void OpenViewer(int fileId) { }
    public void CloseViewer(int fileId) { }
    public void CloseAllViewers() { }
    public void RegisterActiveDialogCloser(Func<Task> closer) { }
    public void UnregisterActiveDialogCloser(Func<Task> closer) { }
    public Task CloseActiveDialogsAsync() => Task.CompletedTask;
}
