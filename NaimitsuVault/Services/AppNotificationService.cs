// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NaimitsuVault.Localization;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public class AppNotificationService : IAppNotificationService
{
    private InfoBar? _infoBar;
    private DispatcherQueue? _queue;

    // Generation counter incremented on every Show call. HideAfterAsync only closes the InfoBar if
    // its own generation is still the latest (i.e. no later Show has interrupted it). Without this,
    // a stale notification's hide timer could close a different, more recently shown notification early.
    private int _generation;

    public void Register(InfoBar infoBar, DispatcherQueue queue)
    {
        // Detach the handler from the previous instance before reattaching (prevents double registration on window recreation)
        if (_infoBar != null) _infoBar.Closed -= OnInfoBarClosed;
        _infoBar = infoBar;
        _queue   = queue;
        _infoBar.Closed += OnInfoBarClosed;
    }

    // Catch both manual close (x button) and programmatic close, and clear string references
    private static void OnInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs _)
    {
        sender.Title   = string.Empty;
        sender.Message = string.Empty;
    }

    public void Show(int titleKey, int messageKey,
        NotificationSeverity severity = NotificationSeverity.Info,
        TimeSpan? duration = null)
    {
        int generation = Interlocked.Increment(ref _generation);
        // The closure only captures a plain int (value type).
        // The string reference is only created when the lambda runs (on the UI thread).
        _queue?.TryEnqueue(() =>
        {
            if (_infoBar == null) return;
            _infoBar.Title   = LocalizationManager.GetById(titleKey);
            _infoBar.Message = LocalizationManager.GetById(messageKey);
            ApplySeverityAndOpen(_infoBar, _queue, severity, duration, generation);
        });
    }

    public void Show(int titleKey, string message,
        NotificationSeverity severity = NotificationSeverity.Info,
        TimeSpan? duration = null)
    {
        int generation = Interlocked.Increment(ref _generation);
        // Title is an int (resolved lazily). Message is a runtime string (already built dynamically).
        _queue?.TryEnqueue(() =>
        {
            if (_infoBar == null) return;
            _infoBar.Title   = LocalizationManager.GetById(titleKey);
            _infoBar.Message = message;
            ApplySeverityAndOpen(_infoBar, _queue, severity, duration, generation);
        });
    }

    private void ApplySeverityAndOpen(InfoBar bar, DispatcherQueue? queue,
        NotificationSeverity severity, TimeSpan? duration, int generation)
    {
        bar.Severity = severity switch
        {
            NotificationSeverity.Success => InfoBarSeverity.Success,
            NotificationSeverity.Warning => InfoBarSeverity.Warning,
            NotificationSeverity.Error   => InfoBarSeverity.Error,
            _                            => InfoBarSeverity.Informational
        };
        // If the theme changes while IsOpen=false, the InfoBar's VisualState freezes on the old theme.
        // Resetting to Default before opening lets it re-inherit the parent theme.
        bar.RequestedTheme = ElementTheme.Default;
        bar.IsOpen = true;
        if (duration.HasValue)
        {
            // Capture the field here so HideAfterAsync closes the correct instance
            // even if the _infoBar field has since been overwritten by another window.
            var capturedBar   = bar;
            var capturedQueue = queue;
            _ = HideAfterAsync(capturedBar, capturedQueue, duration.Value, generation);
        }
    }

    private async Task HideAfterAsync(InfoBar bar, DispatcherQueue? queue, TimeSpan delay, int generation)
    {
        await Task.Delay(delay);
        queue?.TryEnqueue(() =>
        {
            // If a later Show has interrupted this one, do nothing so we don't close it early
            // (stale generation = this timer is no longer valid).
            if (generation != _generation) return;
            if (bar.XamlRoot == null) return;
            bar.IsOpen  = false;        // Fires the Closed event, which runs OnInfoBarClosed
            bar.Title   = string.Empty; // Extra safety in case IsOpen=false doesn't fire synchronously
            bar.Message = string.Empty;
        });
    }
}
