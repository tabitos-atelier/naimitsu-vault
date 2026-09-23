// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

/// <summary>
/// Abstracts safe dispatch to the UI thread.
/// Replace with ImmediateDispatcherService (immediate synchronous execution) for testing.
/// </summary>
public interface IDispatcherService
{
    /// <summary>
    /// Executes an action on the UI thread. The calling thread doesn't matter.
    /// <para>
    /// If the DispatcherQueue has already shut down (app is exiting), the action is dropped silently (no exception).
    /// Use <see cref="EnqueueAsync"/> if you need to check the result or catch exceptions.
    /// </para>
    /// </summary>
    void Enqueue(Action action);

    /// <summary>Executes an action on the UI thread and awaits its completion.</summary>
    Task EnqueueAsync(Func<Task> asyncAction);
}
