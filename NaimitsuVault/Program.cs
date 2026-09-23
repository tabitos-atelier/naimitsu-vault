// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace NaimitsuVault;

static class Program
{
    // Held for the process lifetime so Setup (NaimitsuVault.iss's AppMutex directive) can detect a
    // running instance and tell the user to close it before an upgrade install overwrites files.
    // Never disposed explicitly; the OS releases it automatically on process exit.
    private static Mutex? _installerDetectionMutex;

    // WARNING: async Task Main must never be used
    // Making Main async causes the compiler to generate a synchronous wrapper, which double-pumps
    // alongside Application.Start's STA message pump and breaks TSF (Text Services Framework) initialization.
    // Result: IME on/off becomes uncontrollable right from startup, and every IME-based language (e.g. Japanese) stops working entirely.
    // Direct Latin input still works fine, so testing in an English environment will never catch this.
    [STAThread]
    static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        var instance = AppInstance.FindOrRegisterForKey("NaimitsuVault");
        if (!instance.IsCurrent)
        {
            // Second launch: activate the existing instance and quietly exit
            // No risk of an STA thread deadlock since this exits immediately
            instance.RedirectActivationToAsync(
                AppInstance.GetCurrent().GetActivatedEventArgs())
                .AsTask().Wait();
            return;
        }

        _installerDetectionMutex = new Mutex(initiallyOwned: true, name: @"Global\NaimitsuVaultAppMutex");

        // First launch: register a handler that brings the window to the front on subsequent launches
        instance.Activated += OnRedirectedActivation;

        Application.Start(p =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            new App(); // no explicit capture needed, since WinRT holds the reference
        });
    }

    // AppInstance.Activated fires on a background thread, so marshal to the UI thread to bring it to the front
    private static void OnRedirectedActivation(object? sender, AppActivationArguments _)
        => App.UiDispatcherQueue?.TryEnqueue(() => (Application.Current as App)?.BringToFront());
}
