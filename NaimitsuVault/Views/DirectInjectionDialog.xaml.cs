// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;

namespace NaimitsuVault.Views;

public sealed partial class DirectInjectionDialog : ContentDialog
{
    private static readonly ILogger<DirectInjectionDialog> Logger = AppLog.For<DirectInjectionDialog>();

    private enum DialogState { Idle, Running, Phase1Done, Complete, Error }

    private readonly IAutoTypeService _autoTypeService;
    private readonly IAuditLogService _auditLog;
    private readonly AppSession _session;
    private readonly nint _hwndTarget;
    private readonly SecureCharBuffer _usernameBuffer;
    private readonly SecureCharBuffer _passwordBuffer;
    private readonly int _targetSecretId;
    private readonly string? _targetSecretName;
    private readonly string _userIdLabel;
    private readonly string _passwordLabel;
    private DialogState _state = DialogState.Idle;

    // Kept consistent with the app's common info/success/danger colors.
    private static readonly SolidColorBrush BlueBrush  = new(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F));
    private static readonly SolidColorBrush RedBrush   = new(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));

    public DirectInjectionDialog(
        IAutoTypeService autoTypeService,
        nint hwndTarget,
        SecureCharBuffer usernameBuffer,
        SecureCharBuffer passwordBuffer,
        int targetSecretId,
        string? targetSecretName,
        string userIdLabel,
        string passwordLabel)
    {
        _autoTypeService  = autoTypeService;
        _hwndTarget       = hwndTarget;
        _usernameBuffer   = usernameBuffer;
        _passwordBuffer   = passwordBuffer;
        _targetSecretId   = targetSecretId;
        _targetSecretName = targetSecretName;
        _userIdLabel      = userIdLabel;
        _passwordLabel    = passwordLabel;
        _auditLog = ((App)Application.Current).Services.GetRequiredService<IAuditLogService>();
        _session  = ((App)Application.Current).Services.GetRequiredService<AppSession>();

        InitializeComponent();

        Title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { Glyph = "\uE8F0", VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = LocalizationManager.Get("DirectInjection.Title"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        SetStatus(LocalizationManager.Get("DirectInjection.InfoMonitoringForeground"), BlueBrush);

        // Fully disable Escape (empty CloseButtonText + suppress it in KeyDown)
        // handledEventsToo: true - still receives the event even after a Button consumes arrow keys for focus movement
        CloseButtonText = string.Empty;
        RootPanel.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Dialog_KeyDown), handledEventsToo: true);
    }

    private void Dialog_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            return;
        }

        bool isDown = e.Key == VirtualKey.Down || e.Key == VirtualKey.J;
        bool isUp   = e.Key == VirtualKey.Up   || e.Key == VirtualKey.K;
        if (!isDown && !isUp) return;

        Button[] all     = [InjectUsernameButton, InjectPasswordButton, OkButton];
        Button[] enabled = [.. all.Where(b => b.IsEnabled)];
        if (enabled.Length == 0) return;

        var focused = FocusManager.GetFocusedElement(XamlRoot) as Button;
        int idx = Array.IndexOf(enabled, focused);
        // idx == -1 (nothing focused yet, or the focused button just got disabled) isn't a real
        // index to wrap around from: feeding it into the (idx - 1 + length) % length formula below
        // shifts Up/K one slot short of the intended last button, so it's handled explicitly instead.
        int next;
        if (idx < 0)
            next = isDown ? 0 : enabled.Length - 1;
        else
            next = isDown
                ? (idx + 1) % enabled.Length
                : (idx - 1 + enabled.Length) % enabled.Length;

        enabled[next].Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    // ── Button handlers ─────────────────────────────────────────────────────────

    private async void InjectUsernameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state != DialogState.Idle && _state != DialogState.Error) return;
        if (_hwndTarget == nint.Zero)
        {
            SetStatus(LocalizationManager.Get("DirectInjection.ErrorTargetWindowNotFound"), RedBrush);
            SetState(DialogState.Error);
            return;
        }

        SetState(DialogState.Running);

        Win32InputSender.SetForegroundWindow(_hwndTarget);
        await Task.Delay(300);

        var result = await _autoTypeService.InjectAsync(_usernameBuffer, _hwndTarget, appendEnter: false, CancellationToken.None);

        if (result.IsSuccess)
        {
            SetStatus(LocalizationManager.Get("DirectInjection.SuccessUsernameInjected"), GreenBrush);
            SetState(DialogState.Phase1Done);
            InjectPasswordButton.Focus(FocusState.Programmatic);
            _ = LogAutoTypeAsync(_userIdLabel);
        }
        else
        {
            SetStatus(GetErrorMessage(result), RedBrush);
            SetState(DialogState.Error);
            InjectUsernameButton.Focus(FocusState.Programmatic);
        }
    }

    private async void InjectPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state != DialogState.Idle && _state != DialogState.Phase1Done && _state != DialogState.Error) return;
        if (_hwndTarget == nint.Zero)
        {
            SetStatus(LocalizationManager.Get("DirectInjection.ErrorTargetWindowNotFound"), RedBrush);
            SetState(DialogState.Error);
            return;
        }

        SetState(DialogState.Running);

        Win32InputSender.SetForegroundWindow(_hwndTarget);
        await Task.Delay(300);

        var result = await _autoTypeService.InjectAsync(_passwordBuffer, _hwndTarget, appendEnter: true, CancellationToken.None);

        if (result.IsSuccess)
        {
            SetStatus(LocalizationManager.Get("DirectInjection.SuccessPasswordInjected"), GreenBrush);
            SetState(DialogState.Complete);
            OkButton.Focus(FocusState.Programmatic);
            _ = LogAutoTypeAsync(_passwordLabel);
        }
        else
        {
            SetStatus(GetErrorMessage(result), RedBrush);
            SetState(DialogState.Error);
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Hide();

    // ── State management ───────────────────────────────────────────────────────────────

    private void SetState(DialogState state)
    {
        _state = state;
        switch (state)
        {
            case DialogState.Idle:
                InjectUsernameButton.IsEnabled = true;
                InjectPasswordButton.IsEnabled = true;
                OkButton.IsEnabled             = true;
                break;
            case DialogState.Running:
                InjectUsernameButton.IsEnabled = false;
                InjectPasswordButton.IsEnabled = false;
                OkButton.IsEnabled             = false;
                break;
            case DialogState.Phase1Done:
                InjectUsernameButton.IsEnabled = false;
                InjectPasswordButton.IsEnabled = true;
                OkButton.IsEnabled             = true;
                break;
            case DialogState.Complete:
                InjectUsernameButton.IsEnabled = false;
                InjectPasswordButton.IsEnabled = false;
                OkButton.IsEnabled             = true;
                break;
            case DialogState.Error:
                InjectUsernameButton.IsEnabled = true;
                InjectPasswordButton.IsEnabled = true;
                OkButton.IsEnabled             = true;
                break;
        }
    }

    private async Task LogAutoTypeAsync(string fieldLabel)
    {
        try
        {
            await _auditLog.LogAsync(
                AuditEventCode.SecretAutoTypeExecuted,
                new SecretAutoTypeExecutedPayload(_targetSecretId, fieldLabel, _targetSecretName),
                _session.GetKey());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("[DirectInjectionDialog] Failed to log SecretAutoTypeExecuted. [{ExType}]", ex.GetType().Name);
        }
    }

    private void SetStatus(string message, SolidColorBrush brush)
    {
        StatusTextBlock.Text       = message;
        StatusTextBlock.Foreground = brush;
    }

    private string GetErrorMessage(DirectInjectionResult result)
    {
        if (result.IsHwndMismatch)
        {
            return _hwndTarget == nint.Zero
                ? LocalizationManager.Get("DirectInjection.ErrorTargetWindowNotFound")
                : LocalizationManager.Get("DirectInjection.ErrorHwndMismatchInterrupted");
        }
        if (result.IsSendInputFailed)
            return string.Format(LocalizationManager.Get("DirectInjection.ErrorSendInputWin32Failed"), result.Win32ErrorCode);
        return LocalizationManager.Get("DirectInjection.ErrorHwndMismatchInterrupted");
    }

}
