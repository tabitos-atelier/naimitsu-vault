// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NaimitsuVault.Helpers;

/// <summary>
/// Automatically clears the clipboard 30 seconds after a copy.
/// Every call to <see cref="ScheduleClear"/> cancels the previous timer, keeping only the latest one active.
/// <see cref="Dispose"/> never interferes with a pending timer (see below).
/// </summary>
internal sealed class ClipboardAutoEraser : IDisposable
{
    private static readonly ILogger<ClipboardAutoEraser> Logger = AppLog.For<ClipboardAutoEraser>();

    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _cts;

    /// <summary>Wait duration, overridable from tests. 30 seconds in production.</summary>
    internal TimeSpan DelayDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <param name="dispatch">
    /// Dispatch function to the UI thread. Defaults to <c>App.UiDispatcherQueue.TryEnqueue</c> if omitted.
    /// In tests, pass <c>action => action()</c> to run it synchronously in place.
    /// </param>
    internal ClipboardAutoEraser(Action<Action>? dispatch = null)
    {
        _dispatch = dispatch ?? (a => App.UiDispatcherQueue?.TryEnqueue(() => a()));
    }

    public void ScheduleClear(ReadOnlySpan<char> text)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var hash = SHA256.HashData(MemoryMarshal.AsBytes(text));
        _ = RunAsync(hash, token, _dispatch, DelayDuration);
    }

    private static async Task RunAsync(byte[] hash, CancellationToken token, Action<Action> dispatch, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, token);
            dispatch(() =>
            {
                try
                {
                    if (ClipboardHelper.IsClipboardTextHashMatch(hash))
                        ClipboardHelper.Clear();
                }
                catch (Exception)
                {
                    Logger.LogWarning("Clipboard auto-clear failed");
                }
            });
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Never cancels a pending clear timer, even after the caller (a dialog, etc.) is disposed.
    /// </summary>
    /// <remarks>
    /// Previously this performed an immediate flush-clear on Dispose, but that emptied the clipboard
    /// the instant the dialog was closed right after a copy, wiping it out before the user could paste
    /// into another app (breaking availability). Because <see cref="RunAsync"/> does not reference
    /// <c>this</c> and can run to completion using only local variables, it keeps running independently
    /// in the background even after Dispose, using up the full DelayDuration grace period before clearing naturally.
    /// </remarks>
    public void Dispose()
    {
    }
}
