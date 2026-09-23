// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests.Stubs;

/// <summary>
/// A test-only dispatcher stub.
/// Executes immediately and synchronously in headless test environments where
/// App.UiDispatcherQueue is null.
/// Because EnqueueAsync runs directly on the calling thread, do not use lambdas that
/// contain WinRT async calls (e.g. SetSourceAsync) with this stub in tests.
/// </summary>
internal sealed class ImmediateDispatcherService : IDispatcherService
{
    public void Enqueue(Action action) => action();
    public Task EnqueueAsync(Func<Task> asyncAction) => asyncAction();
}
