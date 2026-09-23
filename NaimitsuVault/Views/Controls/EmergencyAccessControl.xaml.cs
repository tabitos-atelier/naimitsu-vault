// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
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

public sealed partial class EmergencyAccessControl : UserControl
{
    private static readonly ILogger<EmergencyAccessControl> Logger = AppLog.For<EmergencyAccessControl>();

    public EmergencyAccessViewModel ViewModel { get; }

    /// <summary>Raised when the user clicks the Close button. The host dialog should Hide() in response.</summary>
    public event EventHandler? CloseRequested;

    public EmergencyAccessControl(EmergencyAccessViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.GetPinChars = () => SecurePasswordHelper.ExtractPinned(PinBox);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // Unloaded scrubs only once the control is finalizing (the Close button, or the host's own
        // Scrub() in the finally around ShowAsync - the real guarantee). A ContentDialog raises a transient
        // Loaded -> Unloaded -> Loaded cycle right after it opens (seen in the log: two Scrub() calls within
        // 14 ms of opening); scrubbing on that one unsubscribed PropertyChanged, so the dialog stayed on
        // screen but never updated (a dropped QR was read and then nothing happened). Same rule as
        // SecretDraftCompareContent / ProfileDraftCompareContent (_isFinalizing).
        Unloaded += (_, _) =>
        {
            if (!_isFinalizing) return;
            Scrub();
        };

        // PreviewKeyDown (tunneling) on the control root, rather than PinBox.KeyDown, so the toggle
        // still fires regardless of which element inside this ContentDialog popup currently has focus.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.H || !IsCtrlDown()) return;
            PinBox.PasswordRevealMode = PinBox.PasswordRevealMode == PasswordRevealMode.Visible
                ? PasswordRevealMode.Hidden
                : PasswordRevealMode.Visible;
            e.Handled = true;
        };
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    private bool _isFinalizing;

    /// <summary>Final teardown (idempotent). Called by the host after the dialog closes, and by Unloaded once finalizing.</summary>
    public void Scrub()
    {
        _isFinalizing = true;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        // ExtractPinned reads out whatever PIN is still live in PinBox (e.g. on cancel, where no
        // PasswordChanged fired after the last keystroke to zero it via BorrowLength) and clears
        // PinBox.Password as a side effect; the returned pinned buffer must still be zeroed here.
        var discardedPin = SecurePasswordHelper.ExtractPinned(PinBox);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(discardedPin.AsSpan()));
        ViewModel.Dispose();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.ErrorMessage):
                var msg = ViewModel.ErrorMessage;
                ErrorText.Text         = msg ?? string.Empty;
                ErrorBorder.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
                break;

            case nameof(ViewModel.IsBusy):
                BusyRing.IsActive   = ViewModel.IsBusy;
                BusyRing.Visibility = ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                break;

            case nameof(ViewModel.HasEmergencyCodeQr):
                var hasQr = ViewModel.HasEmergencyCodeQr;
                QrPlaceholderPanel.Visibility = hasQr ? Visibility.Collapsed : Visibility.Visible;
                QrSuccessPanel.Visibility     = hasQr ? Visibility.Visible   : Visibility.Collapsed;
                break;
        }
    }

    private void QrDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void QrDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var pngFiles = items
                .OfType<Windows.Storage.StorageFile>()
                .Where(f => f.FileType.Equals(".png", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (pngFiles.Count == 0)
            {
                // Do not fail silently: a drop that carried no PNG file looked exactly like "nothing happened".
                ViewModel.ErrorMessage = LocalizationManager.Get("Emergency.ErrorQrReadFailed");
                return;
            }
            if (pngFiles.Count > 1)
            {
                ViewModel.ErrorMessage = LocalizationManager.Get("Emergency.ErrorMultipleFilesDropped");
                return;
            }
            await ViewModel.LoadQrFromPngAsync(pngFiles[0].Path);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                "EmergencyAccessControl: QrDropZone_Drop failed. [{ExType}]", ex.GetType().Name);
            ViewModel.ErrorMessage = LocalizationManager.Get("Common.GeneralError");
        }
    }

    private void PinBox_PasswordChanged(object sender, RoutedEventArgs e)
        => ViewModel.PinLength = SecurePasswordHelper.BorrowLength(PinBox);

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _isFinalizing = true; // the Unloaded that follows Hide() is the final one
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
