// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using NaimitsuVault.ViewModels;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinUIEx;

namespace NaimitsuVault.Views;

public sealed partial class UnlockWindow : WindowEx
{
    public UnlockViewModel ViewModel { get; }

    public bool ShowCloseConfirm { get; set; }

    public string? DbBackupFileName { get; set; }

    /// <summary>
    /// Guidance text shown in ErrorBorder when locking after a backup/export completes.
    /// Set via LockAfterDataWrite() and displayed exactly once, on Loaded.
    /// </summary>
    public string? InfoMessage { get; set; }

    public UnlockWindow(UnlockViewModel viewModel, bool recoveryAllowed = true)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ApplyCurrentTheme();
        ApplyCurrentFont();
        // Hide the wrench once a session has been unlocked at all (prevents restoring into an already-unlocked DB)
        if (!recoveryAllowed)
            RestoreGateButton.Visibility = Visibility.Collapsed;
        AppWindow.Closing += UnlockWindow_AppWindowClosing;
        Closed += UnlockWindow_Closed;

        if (Content is FrameworkElement root)
            root.Loaded += async (_, _) =>
            {
                var appSession = ((App)Application.Current).Services.GetRequiredService<AppSession>();
                if (appSession.IsUnifiedDbCorrupted)
                {
                    ApplyCorruptedState();
                    return;
                }
                await ShowDbWarningIfNeededAsync();
                ShowInfoMessageIfNeeded();
                MainPasswordBox.Focus(FocusState.Keyboard);
            };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Taskbar/Alt+Tab icon (ICO)
        AppWindow.SetIcon("Assets/NaimitsuVault.ico");
        // Mica backdrop (WinUIEx.WindowEx.SystemBackdrop: CS0612 obsolete, but suppressed in csproj)
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable   = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        this.SetWindowSize(480, 660);
        this.CenterOnScreen();

        if (ViewModel.IsSetupMode)
        {
            var placeholder = LocalizationManager.Get("Common.PasswordLengthHint");
            MainPasswordBox.PlaceholderText = placeholder;
            AutomationProperties.SetName(MainPasswordBox, placeholder);
            SetupPanel.Visibility = Visibility.Visible;
            ButtonText.Text = LocalizationManager.Get("Unlock.SetupStart");
        }
        else
        {
            var placeholder = LocalizationManager.Get("Common.EnterMasterPassword");
            MainPasswordBox.PlaceholderText = placeholder;
            AutomationProperties.SetName(MainPasswordBox, placeholder);
        }

        WindowsHelloButton.Visibility = ViewModel.IsWindowsHelloAvailable
            ? Visibility.Visible : Visibility.Collapsed;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Succeeded       += OnViewModelSucceeded;

        // ExtractPinned transcribes directly into a POH-pinned char[] and immediately ZeroMemory's the source string.
        // This lets the ViewModel's finally-block ZeroMemory, which spans an async await, hit the correct address without stale-address ghosts.
        // This only reaches the managed string the .Password getter hands back, though. PasswordBox
        // also keeps its own native (WinRT) internal text buffer for display/undo/IME composition,
        // and there is no managed API to reach or zero that buffer. A fresh PasswordBox is created on
        // every unlock attempt (this window is re-created each time), so each attempt's native buffer
        // is a separate, unreachable plaintext copy this app cannot clear. This is a known limitation
        // of WinUI 3, not a gap in this app's own cleanup code.
        ViewModel.GetPasswordChars = () => SecurePasswordHelper.ExtractPinned(MainPasswordBox);
        ViewModel.GetConfirmChars  = () => SecurePasswordHelper.ExtractPinned(ConfirmBox);
        ViewModel.SelectVaultAsync = ShowVaultSelectorDialogAsync;
        ViewModel.ConfirmInvalidateHelloAsync = ShowConfirmInvalidateHelloDialogAsync;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.ErrorMessage):
                var msg = ViewModel.ErrorMessage;
                if (string.IsNullOrEmpty(msg))
                {
                    ErrorBorder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ErrorText.Text = msg;
                    ErrorBorder.Visibility = Visibility.Visible;
                }
                break;

            case nameof(ViewModel.IsBusy):
                BusyRing.IsActive = ViewModel.IsBusy;
                BusyRing.Visibility = ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                UpdateSecondaryControlsEnabled();
                break;

            case nameof(ViewModel.IsWindowsHelloAvailable):
                DispatcherQueue.TryEnqueue(() =>
                    WindowsHelloButton.Visibility = ViewModel.IsWindowsHelloAvailable
                        ? Visibility.Visible : Visibility.Collapsed);
                break;

        }
    }

    private void UnlockWindow_AppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Argon2id no longer blocks the UI thread, so the window can now be closed mid-unlock. Cancel
        // before the ShowCloseConfirm early return below: that flag is only set for the re-lock flow, and
        // the initial-launch window must cancel too, so a derivation that completes afterwards is wiped
        // rather than written to the session.
        ViewModel.CancelPendingAuth();

        if (!ShowCloseConfirm) return;

        // Setting ShowCloseConfirm to false first guards against double-close: when Close() re-fires
        // Closing, it returns early. The handler is also already unsubscribed inside ScrubAndUnsubscribe().
        ShowCloseConfirm = false;
        ScrubAndUnsubscribe();
        Close();
    }

    private bool _isClosed;

    /// <summary>
    /// The window is gone: stop the ViewModel from delivering anything to it. An unlock that was already
    /// past its cancellation check when the window closed still completes and raises Succeeded / flips
    /// IsBusy, and those handlers touch controls (and AppWindow, which is null by now) of a destroyed window.
    /// Also the guaranteed place to cancel, whichever way the window was closed.
    /// </summary>
    private void UnlockWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        ViewModel.CancelPendingAuth();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Succeeded       -= OnViewModelSucceeded;
    }

    private void OnViewModelSucceeded(object? sender, EventArgs e)
        => ScrubAndUnsubscribe();

    private void ScrubAndUnsubscribe()
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Succeeded       -= OnViewModelSucceeded;
        // AppWindow is null once the window has closed (see UnlockWindow_Closed); there is nothing left to unhook then.
        if (!_isClosed) AppWindow.Closing -= UnlockWindow_AppWindowClosing;
        WeakReferenceMessenger.Default.Unregister<FontFamilyChangedMessage>(this);
        // ExtractPinned reads out whatever password/confirm text is still live (the success path
        // already wiped it via ViewModel.GetPasswordChars/GetConfirmChars inside ExecuteAsync, but a
        // cancel/close without ever authenticating reaches this method with the boxes still holding
        // plaintext) and clears each Password as a side effect; the returned pinned buffers must still
        // be zeroed here (same pattern as EmergencyAccessControl.Scrub/TotpSetupDialog.OnUnloaded).
        var discardedPw = SecurePasswordHelper.ExtractPinned(MainPasswordBox);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(discardedPw.AsSpan()));
        var discardedConfirm = SecurePasswordHelper.ExtractPinned(ConfirmBox);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(discardedConfirm.AsSpan()));
        ViewModel.GetPasswordChars = null;
        ViewModel.GetConfirmChars  = null;
        ViewModel.SelectVaultAsync = null;
        ViewModel.ConfirmInvalidateHelloAsync = null;
    }

    private void UnlockInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && ViewModel.ExecuteCommand.CanExecute(null))
            ViewModel.ExecuteCommand.Execute(null);
        else if (e.Key == VirtualKey.H && IsCtrlDown())
        {
            var mode = MainPasswordBox.PasswordRevealMode == PasswordRevealMode.Visible
                ? PasswordRevealMode.Hidden
                : PasswordRevealMode.Visible;
            MainPasswordBox.PasswordRevealMode = mode;
            ConfirmBox.PasswordRevealMode  = mode;
            e.Handled = true;
        }
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    // .Password.Length is a self-defeating structure that mass-produces immutable plaintext strings on the heap for every keystroke.
    // BorrowLength temporarily gets the string, immediately wipes it with ZeroStringInternals, and returns only the int.
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        => ViewModel.PasswordLength = SecurePasswordHelper.BorrowLength(MainPasswordBox);

    private void ConfirmBox_PasswordChanged(object sender, RoutedEventArgs e)
        => ViewModel.ConfirmLength = SecurePasswordHelper.BorrowLength(ConfirmBox);

    private void ApplyCorruptedState()
    {
        MainPasswordBox.IsEnabled    = false;
        ConfirmBox.IsEnabled     = false;
        UnlockButton.IsEnabled   = false;
        WindowsHelloButton.Visibility = Visibility.Collapsed;
        ErrorText.Text          = LocalizationManager.Get("Unlock.ErrorUnifiedDbCorrupted");
        ErrorBorder.Visibility  = Visibility.Visible;
    }

    private bool _recovering;

    public void SetRestoreMode(bool recovering)
    {
        _recovering = recovering;
        MainPasswordBox.IsEnabled        = !recovering;
        UnlockButton.IsEnabled       = !recovering;
        WindowsHelloButton.IsEnabled = !recovering;
        UpdateSecondaryControlsEnabled();
    }

    /// <summary>
    /// Controls that are not covered by the ViewModel's IsBusy bindings/CanExecute (the confirm box and
    /// the restore/emergency-access entry points) are disabled while an unlock is in flight, and while
    /// restore mode is active. Previously the frozen UI thread made a second concurrent action impossible;
    /// with Argon2id off the UI thread it has to be blocked explicitly.
    /// </summary>
    private void UpdateSecondaryControlsEnabled()
    {
        bool free = !_recovering && !ViewModel.IsBusy;
        ConfirmBox.IsEnabled        = free;
        RestoreGateButton.IsEnabled = free;
        EmergencyQrButton.IsEnabled = free;
    }

    private void RestoreGateButton_Click(object sender, RoutedEventArgs e)
    {
        SetRestoreMode(true);
        ((App)Application.Current).OpenRestoreWindow(this);
    }

    private void EmergencyQrButton_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).OpenEmergencyAccessControl(this);
    }

    private async Task<int?> ShowVaultSelectorDialogAsync(IReadOnlyList<int> vaultNumbers)
    {
        int? selected = null;
        var root = (FrameworkElement)Content;

        var buttons = new List<Button>();
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dialog = new ContentDialog
        {
            Title = LocalizationManager.Get("Unlock.SelectVault"),
            Content = buttonPanel,
            CloseButtonText = LocalizationManager.Get("Common.Cancel"),
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.RequestedTheme,
        };

        foreach (var num in vaultNumbers)
        {
            var n = num;
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            btnPanel.Children.Add(new FontIcon { Glyph = "\uF540", FontSize = 16 });
            btnPanel.Children.Add(new TextBlock { Text = $"{n}", VerticalAlignment = VerticalAlignment.Center });
            var btn = new Button { Content = btnPanel, MinWidth = 80 };
            btn.Click += (_, _) => { selected = n; dialog.Hide(); };

            // ContentDialog has its own independent visual tree as a popup, so events don't bubble
            // up to dialog.KeyDown. Set the handler directly on each button instead.
            // Left / Up / H / K -> previous vault (wraps from the first to the last)
            // Right / Down / L / J -> next vault (wraps from the last to the first)
            btn.KeyDown += (s, e) =>
            {
                int idx = buttons.IndexOf((Button)s);
                int next = -1;
                switch (e.Key)
                {
                    case VirtualKey.Left:
                    case VirtualKey.Up:
                    case VirtualKey.H:
                    case VirtualKey.K:
                        next = idx <= 0 ? buttons.Count - 1 : idx - 1;
                        break;
                    case VirtualKey.Right:
                    case VirtualKey.Down:
                    case VirtualKey.L:
                    case VirtualKey.J:
                        next = idx >= buttons.Count - 1 ? 0 : idx + 1;
                        break;
                }
                if (next >= 0)
                {
                    buttons[next].Focus(FocusState.Keyboard);
                    e.Handled = true;
                }
            };

            buttons.Add(btn);
            buttonPanel.Children.Add(btn);
        }

        // Loaded inside a popup is guaranteed to fire; focus the first button.
        buttonPanel.Loaded += (_, _) =>
        {
            if (buttons.Count > 0) buttons[0].Focus(FocusState.Keyboard);
        };

        await dialog.ShowAsync();
        return selected;
    }

    private async Task<bool> ShowConfirmInvalidateHelloDialogAsync()
    {
        var root = (FrameworkElement)Content;

        // ContentDialog.PrimaryButtonText only accepts a plain string and can't include an icon or a
        // custom background, so place a custom delete button in Content instead (same pattern as
        // RestoreWindow.xaml.cs). This also means Enter never triggers it (no PrimaryButtonText / no
        // DefaultButton wired to it), which is the same "no accidental Enter-confirm" protection the
        // old DefaultButton=Close setting was providing.
        //
        // Application.Current.Resources.TryGetValue ignores the theme context, so in light mode it
        // doesn't go through the unified color (App.xaml Technique 5). Branch on ActualTheme instead
        // (same workaround as ShowAuthDialogAsync in RestoreWindow.xaml.cs).
        bool isLight = root.ActualTheme == ElementTheme.Light;
        Brush criticalBrush = isLight
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23))
            : (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var critical) && critical is Brush criticalBrushValue
                ? criticalBrushValue
                : new SolidColorBrush(Microsoft.UI.Colors.Red));

        var deleteButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Background          = criticalBrush,
            Foreground          = new SolidColorBrush(Microsoft.UI.Colors.White),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 6,
                Children =
                {
                    new FontIcon { FontSize = 13, Glyph = "\uF19E" }, // ToggleLeft
                    new TextBlock { Text = LocalizationManager.Get("Unlock.Dialog.HelloProfileMismatchDisableButton") },
                },
            },
        };
        var cancelButton = new Button { Content = LocalizationManager.Get("Common.Cancel") };
        var buttonRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Children            = { cancelButton, deleteButton },
        };

        var content = new StackPanel { Spacing = 8, MinWidth = 300 };
        content.Children.Add(new TextBlock
        {
            Text         = LocalizationManager.Get("Unlock.Dialog.HelloProfileMismatchBody"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(buttonRow);

        var dialog = new ContentDialog
        {
            Title = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uF140", VerticalAlignment = VerticalAlignment.Center }, // StatusCircleBlock
                    new TextBlock
                    {
                        Text = LocalizationManager.Get("Unlock.Dialog.HelloProfileMismatchTitle"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
            Content        = content,
            // The default ContentDialog template has a MinHeight sized for PrimaryButtonText usage,
            // so a short Content with only custom buttons leaves extra space below it. Shrink it to
            // fit the actual content so the buttons sit near the bottom, like a normal dialog.
            MinHeight      = 0,
            XamlRoot       = root.XamlRoot,
            RequestedTheme = root.RequestedTheme,
        };

        bool confirmed = false;
        deleteButton.Click += (_, _) => { confirmed = true; dialog.Hide(); };
        cancelButton.Click += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
        return confirmed;
    }

    private void ShowInfoMessageIfNeeded()
    {
        if (string.IsNullOrEmpty(InfoMessage)) return;
        ErrorText.Text         = InfoMessage;
        ErrorBorder.Visibility = Visibility.Visible;
    }

    private async Task ShowDbWarningIfNeededAsync()
    {
        if (DbBackupFileName == null) return;
        var root = (FrameworkElement)Content;
        var dialog = new ContentDialog
        {
            Title = LocalizationManager.Get("Common.SuccessSaveComplete"),
            Content = new TextBlock
            {
                Text = string.Format(LocalizationManager.Get("Unlock.WarningDbRecreatedBackup"), DbBackupFileName),
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = LocalizationManager.Get("Common.Ok"),
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.RequestedTheme,
        };
        await dialog.ShowAsync();
    }

    private void ApplyCurrentTheme()
    {
        var settingsVm = ((App)Application.Current).Services.GetRequiredService<AppSettingsViewModel>();
        var theme = settingsVm.ThemeMode switch
        {
            "Dark"  => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _       => ElementTheme.Default,
        };
        // Application.Current.RequestedTheme stays at the OS default and never changes, so it's
        // inaccurate for determining the actual theme when ThemeMode=Default (follow system).
        // Instead, check ActualTheme after setting root.RequestedTheme (the effective theme WinUI
        // resolves correctly, including OS-follow behavior) - the same proven pattern used in
        // RestoreWindow.ApplyCurrentTheme/ShowBackupAuthDialogAsync.
        if (Content is not FrameworkElement root) return;
        root.RequestedTheme = theme;
        UpdateTitleBarButtonColors(root.ActualTheme == ElementTheme.Light);

        // Native title-bar button colors aren't {ThemeResource}-driven, so they need an explicit
        // re-application on a live OS theme change while ThemeMode=Default (follow system) - same
        // pattern as ShellWindow/ViewerWindow's own ActualThemeChanged hook.
        root.ActualThemeChanged += (_, _) =>
            UpdateTitleBarButtonColors(root.ActualTheme == ElementTheme.Light);
    }

    private void ApplyCurrentFont()
    {
        if (Content is not FrameworkElement root) return;
        var settingsVm = ((App)Application.Current).Services.GetRequiredService<AppSettingsViewModel>();
        ApplyFontFamily(root, settingsVm.FontFamily);
        WeakReferenceMessenger.Default.Register<FontFamilyChangedMessage>(this, (_, msg) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Content is FrameworkElement r) ApplyFontFamily(r, msg.FontFamily);
            }));
    }

    // FontFamily is only a first-class property on Control/TextBlock, not on FrameworkElement (Content
    // here is a plain Grid). Set the underlying inheritable DependencyProperty directly via SetValue so
    // it still cascades down to descendant Controls that don't already set FontFamily locally/via Style.
    private static void ApplyFontFamily(FrameworkElement root, string? fontFamily)
    {
        if (string.IsNullOrEmpty(fontFamily))
            root.ClearValue(Control.FontFamilyProperty);
        else
            root.SetValue(Control.FontFamilyProperty, new FontFamily(fontFamily));

        // Controls whose default Style sets FontFamily from {ThemeResource ContentControlThemeFontFamily}
        // (already updated by FontResourceService) won't re-evaluate from a raw dictionary edit alone -
        // ThemeResource only re-resolves on an actual theme change. Toggle-and-restore synchronously
        // (collapses into a single composited frame, no visible flicker) to force that re-evaluation.
        var current = root.RequestedTheme;
        root.RequestedTheme = current == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = current;
    }

    private void UpdateTitleBarButtonColors(bool isLight)
    {
        var tb = AppWindow.TitleBar;
        if (isLight)
        {
            tb.ButtonForegroundColor         = Color.FromArgb(255,   0,   0,   0);
            tb.ButtonHoverBackgroundColor    = Color.FromArgb(255, 210, 210, 210);
            tb.ButtonHoverForegroundColor    = Color.FromArgb(255,   0,   0,   0);
            tb.ButtonPressedBackgroundColor  = Color.FromArgb(255, 180, 180, 180);
            tb.ButtonPressedForegroundColor  = Color.FromArgb(255,   0,   0,   0);
            tb.ButtonInactiveForegroundColor = Color.FromArgb(255, 120, 120, 120);
        }
        else
        {
            tb.ButtonForegroundColor         = null;
            tb.ButtonHoverBackgroundColor    = null;
            tb.ButtonHoverForegroundColor    = null;
            tb.ButtonPressedBackgroundColor  = null;
            tb.ButtonPressedForegroundColor  = null;
            tb.ButtonInactiveForegroundColor = null;
        }
    }

    private void TitleMinButton_Click(object sender, RoutedEventArgs e)
        => (AppWindow.Presenter as OverlappedPresenter)?.Minimize();


}
