// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;
using Windows.Security.Credentials.UI;

namespace NaimitsuVault.Services;

/// <summary>
/// Production implementation that wraps the WinRT static calls of UserConsentVerifier.
/// RequestVerificationAsync internally guarantees the UI-thread execution WinUI 3 requires.
/// </summary>
public sealed class WinRTUserConsentVerifierAdapter : IUserConsentVerifierAdapter
{
    public Task<UserConsentVerifierAvailability> CheckAvailabilityAsync()
        => UserConsentVerifier.CheckAvailabilityAsync().AsTask();

    public Task<UserConsentVerificationResult> RequestVerificationAsync(string message)
    {
        var tcs = new TaskCompletionSource<UserConsentVerificationResult>();
        if (App.UiDispatcherQueue is not { } queue || !queue.TryEnqueue(async () =>
        {
            try   { tcs.SetResult(await UserConsentVerifier.RequestVerificationAsync(message)); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
            tcs.SetException(new InvalidOperationException("Failed to enqueue to the UI dispatcher queue."));
        return tcs.Task;
    }
}
