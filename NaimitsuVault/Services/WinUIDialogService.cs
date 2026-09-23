// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Services.Interfaces;
using Windows.Security.Credentials.UI;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace NaimitsuVault.Services;

public class WinUIDialogService : IDialogService
{
    private readonly IAuthService _authService;
    private readonly IUserConsentVerifierAdapter _consentVerifier;
    private readonly ISecurityContext _session;
    private readonly ILogger<WinUIDialogService> _logger;

    // Same minimum as the master password creation/restore dialogs.
    private const int MinMasterPasswordLength = 8;

    private FrameworkElement? _root;

    public WinUIDialogService(
        IAuthService authService,
        IUserConsentVerifierAdapter consentVerifier,
        ISecurityContext appSession,
        ILogger<WinUIDialogService> logger)
    {
        _authService = authService;
        _consentVerifier = consentVerifier;
        _session = appSession;
        _logger = logger;
    }

    // Only one ContentDialog can be open at a time (WinUI 3 constraint).
    // Furthermore, even after ShowAsync() returns, the next ShowAsync() is rejected while the close animation is running.
    // _lastClosedTask waits for the previous dialog's Closed event (animation completion).
    private readonly SemaphoreSlim _dialogLock = new(1, 1);
    private Task _lastClosedTask = Task.CompletedTask;

    private XamlRoot? XamlRoot => _root?.XamlRoot;
    private ElementTheme CurrentTheme => _root?.RequestedTheme ?? ElementTheme.Default;

    public void SetXamlRoot(FrameworkElement root) => _root = root;

    /// <summary>
    /// Displays ContentDialogs serialized one at a time.
    /// Waits for the previous dialog's Closed event (close animation completion) before calling ShowAsync.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogCoreAsync(ContentDialog dialog)
    {
        await _dialogLock.WaitAsync();
        try
        {
            await _lastClosedTask;
            var closedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _lastClosedTask = closedTcs.Task;
            dialog.Closed += (_, _) => closedTcs.TrySetResult();
            return await dialog.ShowAsync();
        }
        catch
        {
            _lastClosedTask = Task.CompletedTask;
            throw;
        }
        finally
        {
            _dialogLock.Release();
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, bool defaultToCancel = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = LocalizationManager.Get("Common.Ok"),
            CloseButtonText = LocalizationManager.Get("Common.Cancel"),
            DefaultButton = defaultToCancel ? ContentDialogButton.Close : ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        var result = await ShowDialogCoreAsync(dialog);
        return result == ContentDialogResult.Primary;
    }

    public async Task<bool> ConfirmDestructiveAsync(string title, string message, string glyph, string actionText, bool defaultToCancel = false)
    {
        // Same icon on the title and the action button (e.g. the trash glyph for Common.Delete, or
        // the EraseTool glyph for Common.PurgePermanently), reinforcing exactly what the user is
        // about to do instead of a generic "OK". Custom footer instead of PrimaryButtonText/
        // CloseButtonText, so the confirm button can carry the icon + red styling.
        var titleContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleContent.Children.Add(new FontIcon { Glyph = glyph, VerticalAlignment = VerticalAlignment.Center });
        titleContent.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });

        var okContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        okContent.Children.Add(new FontIcon
        {
            Glyph    = glyph,
            FontSize = (double)Application.Current.Resources["NvIconSizeDefault"],
        });
        okContent.Children.Add(new TextBlock { Text = actionText });
        var okButton = new Button { Content = okContent };
        ApplyDestructiveAccentStyle(okButton);
        var cancelButton = new Button { Content = LocalizationManager.Get("Common.Cancel") };
        var footer = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            // Cancel listed first so it's the first focusable control (default Tab-order focus target).
            Children            = { cancelButton, okButton },
        };
        // Providing a custom root Panel as ContentDialog.Content bypasses the default style's own
        // width propagation to its ContentPresenter, so a bare TextWrapping="Wrap" TextBlock never
        // receives a finite available width to wrap against and renders unwrapped. An explicit
        // MaxWidth (matching this app's other simple-message dialogs) forces wrapping regardless.
        // Explicit LineBreak inlines instead of relying on "\n" inside Text: a plain "\n" only
        // *looks* like it forces a break when the surrounding text happens to be long enough to
        // word-wrap near the same spot anyway (confirmed: short messages like Gallery's render as one
        // run with "\n" silently dropped, while long ones like TimeMachine's coincidentally wrap close
        // to where the "\n" is). LineBreak is unambiguous regardless of message length.
        var messageBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        var messageLines = message.Split('\n');
        for (int i = 0; i < messageLines.Length; i++)
        {
            if (i > 0) messageBlock.Inlines.Add(new LineBreak());
            if (messageLines[i].Length > 0)
                messageBlock.Inlines.Add(new Run { Text = messageLines[i] });
        }
        var content = new StackPanel
        {
            Spacing  = 12,
            Children = { messageBlock, footer },
        };

        var dialog = new ContentDialog
        {
            Title          = titleContent,
            Content        = content,
            XamlRoot       = XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        bool confirmed = false;
        okButton.Click     += (_, _) => { confirmed = true; dialog.Hide(); };
        cancelButton.Click += (_, _) => dialog.Hide();
        // No built-in Primary/Close buttons exist here for ContentDialog.DefaultButton to target, so
        // explicitly focus Cancel on open when the caller wants Enter to be safe by default.
        if (defaultToCancel)
            dialog.Opened += (_, _) => cancelButton.Focus(FocusState.Programmatic);

        await ShowDialogCoreAsync(dialog);
        return confirmed;
    }

    public async Task<bool> ConfirmWithIconAsync(string title, string message, string glyph, string actionText, bool destructive = false, bool defaultToCancel = false)
    {
        // Same icon on the title and the action button (e.g. the upload glyph for an import
        // confirmation, or the download glyph for an export warning). destructive swaps the
        // action button from accent styling to ConfirmDestructiveAsync's red/critical styling.
        var titleContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleContent.Children.Add(new FontIcon { Glyph = glyph, VerticalAlignment = VerticalAlignment.Center });
        titleContent.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });

        var okContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        okContent.Children.Add(new FontIcon
        {
            Glyph    = glyph,
            FontSize = (double)Application.Current.Resources["NvIconSizeDefault"],
        });
        okContent.Children.Add(new TextBlock { Text = actionText });
        var okButton = new Button { Content = okContent };
        if (destructive)
            ApplyDestructiveAccentStyle(okButton);
        else
            okButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var cancelButton = new Button { Content = LocalizationManager.Get("Common.Cancel") };
        var footer = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            // Cancel listed first so it's the first focusable control (default Tab-order focus target).
            Children            = { cancelButton, okButton },
        };
        // See ConfirmDestructiveAsync for why message wrapping needs an explicit MaxWidth + LineBreak
        // inlines instead of a bare TextWrapping="Wrap" TextBlock with "\n" in Text.
        var messageBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        var messageLines = message.Split('\n');
        for (int i = 0; i < messageLines.Length; i++)
        {
            if (i > 0) messageBlock.Inlines.Add(new LineBreak());
            if (messageLines[i].Length > 0)
                messageBlock.Inlines.Add(new Run { Text = messageLines[i] });
        }
        var content = new StackPanel
        {
            Spacing  = 12,
            Children = { messageBlock, footer },
        };

        var dialog = new ContentDialog
        {
            Title          = titleContent,
            Content        = content,
            XamlRoot       = XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        bool confirmed = false;
        okButton.Click     += (_, _) => { confirmed = true; dialog.Hide(); };
        cancelButton.Click += (_, _) => dialog.Hide();
        if (defaultToCancel)
            dialog.Opened += (_, _) => cancelButton.Focus(FocusState.Programmatic);

        await ShowDialogCoreAsync(dialog);
        return confirmed;
    }

    /// <summary>
    /// Re-skins an AccentButtonStyle button instance to a solid red "danger" button (matching the
    /// app's established danger red, App.xaml Technique 5's #E81123) instead of the system accent
    /// color. AccentButtonStyle's ControlTemplate resolves its Background/Foreground per-state via
    /// {ThemeResource AccentButton*} lookups, not TemplateBinding, so setting Button.Background
    /// directly has no visible effect — overriding those keys in this button's own Resources
    /// re-skins just this one instance without touching the shared AccentButtonStyle used
    /// elsewhere (e.g. this app's other non-destructive confirm buttons).
    /// </summary>
    private static void ApplyDestructiveAccentStyle(Button button)
    {
        button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var danger        = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));
        var dangerHover    = new SolidColorBrush(Color.FromArgb(0xFF, 0xC5, 0x0E, 0x1E));
        var dangerPressed  = new SolidColorBrush(Color.FromArgb(0xFF, 0xA4, 0x07, 0x0E));
        var white          = new SolidColorBrush(Microsoft.UI.Colors.White);
        button.Resources["AccentButtonBackground"]            = danger;
        button.Resources["AccentButtonBackgroundPointerOver"] = dangerHover;
        button.Resources["AccentButtonBackgroundPressed"]     = dangerPressed;
        button.Resources["AccentButtonForeground"]            = white;
        button.Resources["AccentButtonForegroundPointerOver"] = white;
        button.Resources["AccentButtonForegroundPressed"]     = white;
    }

    public async Task<bool> ConfirmSwapAsync(string title, TimeMachineSwapPreview preview)
    {
        // Use ActualTheme to determine explicit colors
        // Application.Current.Resources is fixed to the theme at app launch and doesn't follow OS theme switches
        bool isDark = _root?.ActualTheme == ElementTheme.Dark;

        // Custom footer (Cancel left, OK right) instead of PrimaryButtonText/CloseButtonText, so the
        // confirm action is consistently on the right like the app's other non-destructive dialogs.
        var okButton = new Button
        {
            Content = LocalizationManager.Get("Common.Ok"),
            Style   = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var cancelButton = new Button { Content = LocalizationManager.Get("Common.Cancel") };
        var footer = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Children            = { cancelButton, okButton },
        };
        var content = new StackPanel
        {
            Spacing  = 12,
            Children = { TimeMachineSwapContentBuilder.Build(preview, isDark), footer },
        };

        var dialog = new ContentDialog
        {
            Title          = title,
            Content        = content,
            XamlRoot       = XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        bool confirmed = false;
        okButton.Click     += (_, _) => { confirmed = true; dialog.Hide(); };
        cancelButton.Click += (_, _) => dialog.Hide();

        await ShowDialogCoreAsync(dialog);
        return confirmed;
    }


    public async Task<SecureCharBuffer?> ConfirmPasswordAsync(string message)
    {
        var passwordBox = new PasswordBox
        {
            PlaceholderText = message,
            Width = 300
        };

        // Custom footer (Cancel left, OK right) instead of PrimaryButtonText/CloseButtonText, so the
        // confirm action is consistently on the right like the app's other non-destructive dialogs.
        var okButton = new Button
        {
            Content = LocalizationManager.Get("Common.Ok"),
            Style   = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var cancelButton = new Button { Content = LocalizationManager.Get("Common.Cancel") };
        var footer = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Children            = { cancelButton, okButton },
        };
        var content = new StackPanel { Spacing = 12, Children = { passwordBox, footer } };
        // PreviewKeyDown (tunneling) on the panel root so the toggle fires regardless of which
        // element inside this ContentDialog popup currently has focus.
        content.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.H || !IsCtrlDown()) return;
            passwordBox.PasswordRevealMode = passwordBox.PasswordRevealMode == PasswordRevealMode.Visible
                ? PasswordRevealMode.Hidden
                : PasswordRevealMode.Visible;
            e.Handled = true;
        };

        var dialog = new ContentDialog
        {
            Title          = message,
            Content        = content,
            XamlRoot       = XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        bool confirmed = false;
        okButton.Click     += (_, _) => { confirmed = true; dialog.Hide(); };
        cancelButton.Click += (_, _) => dialog.Hide();

        // PasswordBox.Password returns a new (non-interned) string every time it's read, so it must
        // be read exactly once - including on the cancel path - and that same instance zero-cleared in finally.
        string? s = null;
        try
        {
            await ShowDialogCoreAsync(dialog);
            s = passwordBox.Password;
            if (!confirmed) return null;
            if (string.IsNullOrEmpty(s)) return null;
            var buf = new SecureCharBuffer();
            buf.SetFromSpan(s.AsSpan());
            return buf;
        }
        finally
        {
            if (!string.IsNullOrEmpty(s)) SecurePasswordHelper.ZeroStringInternals(s);
            passwordBox.Password = string.Empty;    // Purge the control
        }
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = LocalizationManager.Get("Common.Ok"),
            XamlRoot = XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        await ShowDialogCoreAsync(dialog);
    }

    public async Task<IDisposable> ShowBusyAsync(string message)
    {
        // Waits for the previous dialog's close animation the same way ShowDialogCoreAsync does -
        // otherwise showing this dialog too soon after a prior one closes (e.g. right after
        // ConfirmMasterAuthAsync's Windows Hello fast path, which has no Argon2id delay to mask the
        // race) can collide with WinUI 3's in-flight close animation.
        await _lastClosedTask;

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        panel.Children.Add(new TextBlock { Text = message, VerticalAlignment = VerticalAlignment.Center });
        var dialog = new ContentDialog
        {
            Content = panel,
            XamlRoot = XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        var closedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastClosedTask = closedTcs.Task;
        dialog.Closed += (_, _) => closedTcs.TrySetResult();
        _ = dialog.ShowAsync();
        return new BusyDialogHandle(dialog);
    }

    private sealed class BusyDialogHandle(ContentDialog dialog) : IDisposable
    {
        public void Dispose() => dialog.Hide();
    }

    public async Task<MasterAuthResult?> ConfirmMasterAuthAsync(string message)
    {
        // Hide the Hello button while logging in via EAC (defense in depth).
        // Even when IsReadOnlyRestricted = true, DEK_Hello remains in the vault DB, so
        // IsWindowsHelloEnabledAsync() returns true, but if Hello sets verified = true,
        // operations like export could be performed without the master password.
        bool helloEnabled = !_session.IsReadOnlyRestricted
                            && await _authService.IsWindowsHelloEnabledAsync();
        MasterAuthResult? helloResult = null;

        var content = new StackPanel { Spacing = 12, MinWidth = 340 };

        if (!string.IsNullOrEmpty(message))
            content.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });

        var pwBox = new PasswordBox { PlaceholderText = LocalizationManager.Get("VaultSettings.CurrentMasterPassword") };
        // PreviewKeyDown (tunneling) on the panel root, rather than pwBox.KeyDown, so the toggle still
        // fires regardless of which element inside this ContentDialog popup currently has focus
        // (e.g. the Verify/Cancel/Hello buttons - a focused Button consuming the keystroke before it
        // bubbles back up to pwBox is a known ContentDialog popup-layer pitfall).
        content.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.H || !IsCtrlDown()) return;
            pwBox.PasswordRevealMode = pwBox.PasswordRevealMode == PasswordRevealMode.Visible
                ? PasswordRevealMode.Hidden
                : PasswordRevealMode.Visible;
            e.Handled = true;
        };
        content.Children.Add(pwBox);

        var helloBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE928", FontSize = 16 },
                    new TextBlock { Text = LocalizationManager.Get("Common.AuthWithWindowsHello") },
                }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style               = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Visibility = helloEnabled ? Visibility.Visible : Visibility.Collapsed,
        };
        content.Children.Add(helloBtn);

        // Custom footer (Cancel left, Verify right) instead of PrimaryButtonText/CloseButtonText, so the
        // confirm action is consistently on the right like the app's other non-destructive dialogs.
        var verifyButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE8D7", FontSize = 16 },
                    new TextBlock { Text = LocalizationManager.Get("Common.Verify") },
                }
            },
            Style     = (Style)Application.Current.Resources["AccentButtonStyle"],
            // Disabled until the password reaches the minimum master password length.
            IsEnabled = false,
        };
        var cancelButton = new Button { Content = LocalizationManager.Get("Common.Cancel") };
        content.Children.Add(new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin              = new Thickness(0, 4, 0, 0),
            Children            = { cancelButton, verifyButton },
        });

        // BorrowLength reads pwBox.Password and immediately zero-clears that returned string's
        // internal buffer, so this length check leaves no un-zeroed plaintext copy per keystroke.
        pwBox.PasswordChanged += (_, _) =>
            verifyButton.IsEnabled = SecurePasswordHelper.BorrowLength(pwBox) >= MinMasterPasswordLength;

        var dialog = new ContentDialog
        {
            Title = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE8D4", VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = LocalizationManager.Get("Common.IdentityVerification"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
            Content = content,
            XamlRoot = XamlRoot,
            RequestedTheme = CurrentTheme,
        };
        // Cancel/Escape/Hello all close the dialog with ContentDialogResult.None, so the password box
        // content alone can't tell "Verify pressed" apart from "Cancel pressed with text typed in".
        bool verified = false;
        verifyButton.Click += (_, _) => { verified = true; dialog.Hide(); };
        cancelButton.Click += (_, _) => dialog.Hide();

        helloBtn.Click += async (_, _) =>
        {
            try
            {
                var result = await _consentVerifier.RequestVerificationAsync(
                    LocalizationManager.Get("Common.IdentityVerification"));
                if (result == UserConsentVerificationResult.Verified)
                {
                    helloResult = MasterAuthResult.FromHello();
                    dialog.Hide();
                }
                // Don't close the dialog on failure/cancel (falls back to password entry)
            }
            catch (Exception ex)
            {
                _logger.LogWarning("An exception occurred during Windows Hello verification. Falling back to password entry. [{ExType}]", ex.GetType().Name);
            }
        };

        // PasswordBox.Password returns a new (non-interned) string every time it's read, so it must
        // be read exactly once - including on the early-return path for Hello success -
        // and that same instance zero-cleared in finally.
        string? s = null;
        try
        {
            // Closing via dialog.Hide() returns ContentDialogResult.None (same as the Close button).
            // The return value is discarded because whether Hello closed it vs. cancel is distinguished by the helloResult local variable.
            _ = await ShowDialogCoreAsync(dialog);

            s = pwBox.Password;
            if (helloResult != null) return helloResult;

            if (!verified || string.IsNullOrEmpty(s)) return null;
            var pwBuf = new SecureCharBuffer();
            pwBuf.SetFromSpan(s.AsSpan());
            return new MasterAuthResult(pwBuf);
        }
        finally
        {
            if (!string.IsNullOrEmpty(s)) SecurePasswordHelper.ZeroStringInternals(s);
            pwBox.Password = string.Empty;
        }
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
}
