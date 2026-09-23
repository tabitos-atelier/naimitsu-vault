// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Production implementation that dispatches work to the UI thread using WinUI 3's DispatcherQueue.
/// App.UiDispatcherQueue is set in the App constructor.
/// </summary>
public sealed class WinUiDispatcherService : IDispatcherService
{
    public void Enqueue(Action action)
        => App.UiDispatcherQueue?.TryEnqueue(() => action());

    public Task EnqueueAsync(Func<Task> asyncAction)
    {
        var tcs = new TaskCompletionSource();
        if (App.UiDispatcherQueue is not { } queue || !queue.TryEnqueue(async () =>
        {
            try   { await asyncAction(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
            tcs.SetException(new InvalidOperationException("Failed to enqueue to the UI dispatcher queue."));
        return tcs.Task;
    }
}
