// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace NaimitsuVault.Views.Controls;

public sealed partial class AppCommonSettingsControl : UserControl
{
    public AppSettingsViewModel AppSettingsVm { get; }
    public VaultOperationsViewModel VaultOpsVm { get; }

    public AppCommonSettingsControl(AppSettingsViewModel appSettingsVm, VaultOperationsViewModel vaultOpsVm)
    {
        AppSettingsVm = appSettingsVm;
        VaultOpsVm    = vaultOpsVm;
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VaultOpsVm.AskNewVaultPasswordAsync =  ShowNewVaultPasswordDialogAsync;
        VaultOpsVm.PropertyChanged          += VaultOpsVm_PropertyChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        VaultOpsVm.AskNewVaultPasswordAsync =  null;
        VaultOpsVm.PropertyChanged          -= VaultOpsVm_PropertyChanged;
    }

    // Once the 3rd vault is added, CanAddVault flips to false and the Expander is disabled via
    // x:Bind, but a disabled Expander stays visually expanded with an empty ComboBox inside -
    // collapse it here so the "add a vault" section disappears instead of lingering empty.
    private void VaultOpsVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VaultOperationsViewModel.CanAddVault)) return;
        if (!VaultOpsVm.CanAddVault) AddVaultExpander.IsExpanded = false;
    }

    /// <summary>Delegate for x:Bind. The actual decision logic is centralized in <see cref="SettingsUiHelper.BothIdle"/>.</summary>
    public bool BothIdle(bool vaultBusy, bool appBusy) => SettingsUiHelper.BothIdle(vaultBusy, appBusy);

    private async void ChangeFontFamilyButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NaimitsuVault.Views.FontFamilyPickerDialog(AppSettingsVm.FontFamilyOptions, AppSettingsVm.FontFamily)
        {
            XamlRoot       = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        await dialog.ShowAsync();
        if (dialog.IsConfirmed)
            AppSettingsVm.FontFamily = dialog.SelectedFontFamily;
    }

    private void LanguageArea_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
    }

    private async void LanguageArea_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<Windows.Storage.StorageFile>()
                .FirstOrDefault(f => f.FileType.Equals(".json", StringComparison.OrdinalIgnoreCase));
            if (file == null) return;
            var bytes = await System.IO.File.ReadAllBytesAsync(file.Path);
            await AppSettingsVm.ImportLocaleFromBytesAsync(bytes);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<(char[] pw, char[] confirm)?> ShowNewVaultPasswordDialogAsync()
    {
        var pwBox      = new PasswordBox { PlaceholderText = LocalizationManager.Get("VaultSettings.NewMasterPassword") };
        var confirmBox = new PasswordBox { PlaceholderText = LocalizationManager.Get("VaultSettings.ConfirmMasterPassword") };

        // ContentDialog.PrimaryButtonText only accepts a plain string and can't include an icon,
        // so place a custom button in Content with an icon + text instead (same layout as the Restore dialogs).
        var addButton = new Button
        {
            IsEnabled           = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style               = (Style)Application.Current.Resources["AccentButtonStyle"],
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 6,
                Children =
                {
                    new FontIcon { FontSize = 13, Glyph = "\uE710" }, // Add
                    new TextBlock { Text = LocalizationManager.Get("Common.Add") },
                },
            },
        };
        var cancelButton = new Button { Content = LocalizationManager.Get("Common.Cancel") };
        var buttonRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Children            = { cancelButton, addButton },
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(pwBox);
        panel.Children.Add(confirmBox);
        panel.Children.Add(buttonRow);

        // PreviewKeyDown (tunneling) on the panel root, rather than on pwBox/confirmBox directly, so
        // the toggle still fires regardless of which element inside this ContentDialog popup has focus.
        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.H || !IsCtrlDown()) return;
            var mode = pwBox.PasswordRevealMode == PasswordRevealMode.Visible
                ? PasswordRevealMode.Hidden
                : PasswordRevealMode.Visible;
            pwBox.PasswordRevealMode      = mode;
            confirmBox.PasswordRevealMode = mode;
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
                    new FontIcon { Glyph = "\uF540", VerticalAlignment = VerticalAlignment.Center }, // Vault
                    new TextBlock
                    {
                        Text = LocalizationManager.Get("VaultSettings.AddNewVault"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
            Content        = panel,
            XamlRoot       = XamlRoot,
            RequestedTheme = ActualTheme,
        };

        bool confirmed = false;
        void Confirm()
        {
            if (!addButton.IsEnabled) return;
            confirmed = true;
            dialog.Hide();
        }

        void UpdateAddButtonEnabled()
            => addButton.IsEnabled = SecurePasswordHelper.BorrowLength(pwBox) >= 8
                                      && SecurePasswordHelper.BorrowLength(confirmBox) >= 8;

        pwBox.PasswordChanged      += (_, _) => UpdateAddButtonEnabled();
        confirmBox.PasswordChanged += (_, _) => UpdateAddButtonEnabled();
        pwBox.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true;
            Confirm();
        };
        confirmBox.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true;
            Confirm();
        };
        addButton.Click    += (_, _) => Confirm();
        cancelButton.Click += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
        if (!confirmed)
        {
            // Even on cancel, transcribe the entered plaintext into a pinned buffer and immediately zero-clear it.
            // ExtractPinned wipes the original PasswordBox.Password string side, but wiping the
            // returned pinned char[] is the caller's responsibility, so receive it and wipe it right away here.
            var discardedPw      = SecurePasswordHelper.ExtractPinned(pwBox);
            var discardedConfirm = SecurePasswordHelper.ExtractPinned(confirmBox);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(discardedPw.AsSpan()));
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(discardedConfirm.AsSpan()));
            return null;
        }

        return (SecurePasswordHelper.ExtractPinned(pwBox), SecurePasswordHelper.ExtractPinned(confirmBox));
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
}
