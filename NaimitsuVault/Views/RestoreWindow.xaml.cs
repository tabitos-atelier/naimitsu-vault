// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using NaimitsuVault.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinUIEx;

namespace NaimitsuVault.Views;

public sealed partial class RestoreWindow : WindowEx
{
    private static readonly ILogger<RestoreWindow> Logger = AppLog.For<RestoreWindow>();

    public RestoreViewModel ViewModel { get; }

    public RestoreWindow(RestoreViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ApplyCurrentTheme();
        ApplyCurrentFont();
        // For Alt+Tab/taskbar display. The XAML Title="{loc:Msg Key=Restore.MainPage}" alone
        // doesn't clearly connect to Naimitsu-kun, so append the product name at the end.
        Title = $"{LocalizationManager.Get("Restore.MainPage")} - {LocalizationManager.Get("System.Product.Name")}";

        AppWindow.SetIcon("Assets/NaimitsuVault.ico");
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        // Rev.22 removed the restore-mode radio buttons and vault matrix list, shrinking the content
        // to header + drop zone + status InfoBar + footer button. The window size was left at its
        // pre-Rev.22 height, leaving a large empty gap in the "*"-sized InfoBar row.
        this.SetWindowSize(480, 320); // Width matches UnlockWindow
        this.CenterOnScreen();
        ExtendsContentIntoTitleBar = true;

        ViewModel.PropertyChanged  += ViewModel_PropertyChanged;
        ViewModel.RestoreSucceeded += (_, _) => Close();

        Closed += (_, _) =>
        {
            WeakReferenceMessenger.Default.Unregister<FontFamilyChangedMessage>(this);
        };
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
        // resolves correctly, including OS-follow behavior) - the same proven pattern used in ShowBackupAuthDialogAsync.
        bool isLight;
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
            isLight = root.ActualTheme == ElementTheme.Light;
        }
        else
        {
            isLight = theme == ElementTheme.Light;
        }
        UpdateTitleBarButtonColors(isLight);
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

    // ── ViewModel observation ────────────────────────────────────────────────────

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.IsBusy):
            case nameof(ViewModel.IsNotBusy):
                UpdateFooterButtons();
                break;

            case nameof(ViewModel.StatusMessage):
                if (!string.IsNullOrEmpty(ViewModel.StatusMessage))
                {
                    StatusInfoBar.Message  = ViewModel.StatusMessage;
                    StatusInfoBar.Severity = ViewModel.IsError
                        ? InfoBarSeverity.Error : InfoBarSeverity.Success;
                    StatusInfoBar.IsOpen   = true;
                }
                else
                {
                    StatusInfoBar.IsOpen = false;
                }
                break;
        }
    }

    private void UpdateFooterButtons()
    {
        bool busy = ViewModel.IsBusy;

        BusyRing.IsActive   = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        RestoreButton.IsEnabled = !busy && ViewModel.HasBackupNkdb;
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    // ── Restore button: validate → backup authentication → (conditional) overwrite confirmation → restore ──

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ViewModel.ValidateAsync()) return;

        if (!await ShowBackupAuthDialogAsync()) return;

        if (ViewModel.HasExistingLocalData())
        {
            if (!await ShowOverwriteConfirmDialogAsync()) return;
        }

        await ViewModel.RestoreAsync();
    }

    // ── Backup authentication dialog ──────────────────────────────────

    private async Task<bool> ShowBackupAuthDialogAsync()
    {
        var pwBox = new PasswordBox
        {
            PlaceholderText = LocalizationManager.Get("Restore.AuthPasswordPlaceholder"),
            MinWidth        = 300,
        };
        var errorText = new TextBlock
        {
            FontSize     = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility   = Visibility.Collapsed,
        };
        // Application.Current.Resources.TryGetValue ignores the theme context, so in light mode
        // it doesn't go through the unified color (App.xaml Technique 5). Branch on ActualTheme instead.
        bool isLight = Content is FrameworkElement root && root.ActualTheme == ElementTheme.Light;
        errorText.Foreground = isLight
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23))
            : (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var errFg) && errFg is Brush errFgBrush
                ? errFgBrush
                : errorText.Foreground);

        var busyRing = new ProgressRing { IsActive = false, Width = 20, Height = 20 };

        // Authenticate button: ContentDialog.PrimaryButtonText only accepts a plain string and
        // can't include an icon, so place a custom button in Content with an icon + text instead.
        var authButton = new Button
        {
            IsEnabled            = false,
            HorizontalAlignment  = HorizontalAlignment.Right,
            Style                = (Style)Application.Current.Resources["AccentButtonStyle"],
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 6,
                Children =
                {
                    new FontIcon { FontSize = 13, Glyph = "\uE785" }, // Unlock
                    new TextBlock { Text = LocalizationManager.Get("Restore.AuthButton") },
                },
            },
        };
        var cancelButton = new Button
        {
            Content = LocalizationManager.Get("Common.Cancel"),
        };
        var buttonRow = new StackPanel
        {
            Orientation          = Orientation.Horizontal,
            HorizontalAlignment  = HorizontalAlignment.Right,
            Spacing              = 8,
            Children             = { cancelButton, authButton },
        };

        var content = new StackPanel { Spacing = 8, MinWidth = 300 };
        content.Children.Add(new TextBlock
        {
            Text         = LocalizationManager.Get("Restore.AuthDialogMessage"),
            FontSize     = 12,
            Opacity      = 0.8,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(pwBox);
        content.Children.Add(errorText);
        content.Children.Add(busyRing);
        content.Children.Add(buttonRow);

        // PreviewKeyDown (tunneling) on the panel root so the toggle fires regardless of which
        // element inside this ContentDialog popup currently has focus.
        content.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.H || !IsCtrlDown()) return;
            pwBox.PasswordRevealMode = pwBox.PasswordRevealMode == PasswordRevealMode.Visible
                ? PasswordRevealMode.Hidden
                : PasswordRevealMode.Visible;
            e.Handled = true;
        };

        var dialog = new ContentDialog
        {
            Title = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE8D4", VerticalAlignment = VerticalAlignment.Center }, // Contact2
                    new TextBlock
                    {
                        Text = LocalizationManager.Get("Restore.AuthDialogTitle"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
            Content        = content,
            XamlRoot       = Content.XamlRoot,
            RequestedTheme = (Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
        };

        bool authenticated = false;

        async Task AuthenticateAsync()
        {
            if (!authButton.IsEnabled) return;
            authButton.IsEnabled = false;
            busyRing.IsActive    = true;
            errorText.Visibility = Visibility.Collapsed;

            var pw = SecurePasswordHelper.ExtractPinned(pwBox);
            try
            {
                bool ok = await ViewModel.AuthenticateAsync(pw);
                if (ok)
                {
                    authenticated = true;
                    dialog.Hide();
                }
                else
                {
                    errorText.Text        = ViewModel.StatusMessage;
                    errorText.Visibility  = Visibility.Visible;
                    authButton.IsEnabled  = SecurePasswordHelper.BorrowLength(pwBox) >= 8;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(pw.AsSpan()));
                busyRing.IsActive = false;
            }
        }

        // BorrowLength reads pwBox.Password and immediately zero-clears that returned string's
        // internal buffer, so this length check leaves no un-zeroed plaintext copy per keystroke.
        pwBox.PasswordChanged += (_, _) =>
            authButton.IsEnabled = SecurePasswordHelper.BorrowLength(pwBox) >= 8;
        pwBox.KeyDown += async (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true;
            await AuthenticateAsync();
        };
        authButton.Click   += async (_, _) => await AuthenticateAsync();
        cancelButton.Click += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
        if (!authenticated)
        {
            // Cancel/Escape bypasses AuthenticateAsync's own ExtractPinned+ZeroMemory, so an
            // untouched, unconfirmed password would otherwise sit in pwBox.Password until GC.
            var discardedPw = SecurePasswordHelper.ExtractPinned(pwBox);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(discardedPw.AsSpan()));
        }
        return authenticated;
    }

    // ── Overwrite confirmation dialog (first guard; shown only when local data already exists) ──────

    private async Task<bool> ShowOverwriteConfirmDialogAsync()
    {
        string createdAt;
        try { createdAt = File.GetLastWriteTime(ViewModel.BackupNkdbPath).ToString("yyyy/MM/dd HH:mm"); }
        catch { createdAt = "?"; }

        // To avoid confirming with the Enter key (Cancel is the default, preventing accidental
        // repeated-Enter triggers), don't use ContentDialog.PrimaryButtonText (no icon support);
        // instead place a custom restore button in Content (fires on click only; KeyDown isn't wired up).
        var restoreButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 6,
                Children =
                {
                    new FontIcon { FontSize = 13, Glyph = "\uE777" }, // UpdateRestore
                    new TextBlock { Text = LocalizationManager.Get("Restore.OverwriteConfirmButton") },
                },
            },
        };
        var cancelButton = new Button { Content = LocalizationManager.Get("Common.Cancel") };
        var buttonRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Children            = { cancelButton, restoreButton },
        };

        var content = new StackPanel { Spacing = 8, MinWidth = 300 };
        content.Children.Add(new TextBlock
        {
            Text         = string.Format(LocalizationManager.Get("Restore.OverwriteWarningMessage"), createdAt),
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
                    new FontIcon { Glyph = "\uE90F", VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock
                    {
                        Text = LocalizationManager.Get("Restore.OverwriteWarningTitle"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
            Content        = content,
            // The default ContentDialog template has a MinHeight sized for PrimaryButtonText usage,
            // so a short Content with only a custom button leaves extra space below it.
            // Shrink it to fit the actual content so the button sits near the bottom, like a normal dialog.
            MinHeight      = 0,
            XamlRoot       = Content.XamlRoot,
            RequestedTheme = (Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
        };

        bool confirmed = false;
        restoreButton.Click += (_, _) => { confirmed = true; dialog.Hide(); };
        cancelButton.Click  += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
        return confirmed;
    }

    // ── Backup nkdb file picker ────────────────────────────────

    private async void NkdbBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".nkdb");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            if (IsActiveNkdb(file.Path)) return;

            ApplyNkdbSelection(file.Path, file.Name);
            ViewModel.LoadBackupPath(file.Path);
            UpdateFooterButtons();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                "NkdbBrowseButton_Click failed. [{ExType}]", ex.GetType().Name);
        }
    }

    // ── Backup nkdb drag & drop ─────────────────────────────────────────────

    private void NkdbDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
        else
            e.AcceptedOperation = DataPackageOperation.None;
        e.Handled = true;
    }

    private async void NkdbDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file  = items
                .OfType<Windows.Storage.StorageFile>()
                .FirstOrDefault(f => f.FileType.Equals(".nkdb", StringComparison.OrdinalIgnoreCase));
            if (file == null) return;
            if (IsActiveNkdb(file.Path)) return;

            ApplyNkdbSelection(file.Path, file.Name);
            ViewModel.LoadBackupPath(file.Path);
            UpdateFooterButtons();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                "NkdbDropZone_Drop failed. [{ExType}]", ex.GetType().Name);
        }
    }

    // If the user tries to select the active unified DB as the restore target, show an error and return true.
    private bool IsActiveNkdb(string filePath)
    {
        var activeNkdbPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "data", "NaimitsuVault.nkdb");
        if (!string.Equals(filePath, activeNkdbPath, StringComparison.OrdinalIgnoreCase))
            return false;

        StatusInfoBar.Message  = LocalizationManager.Get("Restore.ErrorActiveDbSelected");
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.IsOpen   = true;
        return true;
    }

    private void ApplyNkdbSelection(string path, string fileName)
    {
        NkdbPlaceholderPanel.Visibility = Visibility.Collapsed;
        NkdbAcceptedPanel.Visibility    = Visibility.Visible;
        NkdbAcceptedNameText.Text       = fileName;
    }
}
