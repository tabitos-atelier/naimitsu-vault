// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace NaimitsuVault.ViewModels;

public partial class EmergencyAccessViewModel : ObservableObject, IDisposable
{
    private readonly IAuthService _auth;
    private readonly IFilePickerService _filePicker;

    private readonly SecureCharBuffer _qrBuf = new();
    private readonly ILogger<EmergencyAccessViewModel> _logger;

    [ObservableProperty] public partial bool HasEmergencyCodeQr { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool IsSuccess { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteRecoveryCommand))]
    public partial int PinLength { get; set; }

    public bool IsNotBusy => !IsBusy;

    // Delegate that retrieves PasswordBox characters from the UI
    internal Func<char[]>? GetPinChars { get; set; }

    /// <summary>Event requesting a transition to ShellWindow after a successful recovery (subscribed in App.xaml.cs).</summary>
    public event EventHandler? RecoverySucceeded;

    public EmergencyAccessViewModel(IAuthService auth, IFilePickerService filePicker, ILogger<EmergencyAccessViewModel> logger)
    {
        _logger = logger;
        _auth = auth;
        _filePicker = filePicker;
    }

    /// <summary>
    /// The alternative to dropping the QR PNG onto the dialog: pick it with the file picker. Both routes
    /// end in <see cref="LoadQrFromPngAsync"/>. A cancelled pick changes nothing.
    /// </summary>
    [RelayCommand]
    private async Task SelectQrFileAsync()
    {
        if (IsBusy) return;
        SecureCharBuffer? pathBuf = null;
        try
        {
            pathBuf = await _filePicker.OpenAsync(
                [(string.Format(LocalizationManager.Get("Common.FileFilter"), "PNG", "png"), ".png")]);
            if (pathBuf == null) return;
            await LoadQrFromPngAsync(new string(pathBuf.Span));
        }
        catch (Exception ex)
        {
            _logger.LogError("[EmergencyAccessViewModel] SelectQrFileAsync failed. [{ExType}]", ex.GetType().Name);
            ErrorMessage = LocalizationManager.Get("Common.GeneralError");
        }
        finally
        {
            pathBuf?.Dispose();
        }
    }

    public async Task LoadQrFromPngAsync(string path)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            _qrBuf.Dispose();
            await ReadQrFromPngAsync(path, _qrBuf);
            HasEmergencyCodeQr = !_qrBuf.IsEmpty;
            ExecuteRecoveryCommand.NotifyCanExecuteChanged();
            if (_qrBuf.IsEmpty)
                ErrorMessage = LocalizationManager.Get("Emergency.ErrorQrReadFailed");
        }
        catch (Exception ex)
        {
            _logger.LogError("[EmergencyAccessViewModel] LoadQrFromPngAsync failed. [{ExType}]", ex.GetType().Name);
            ErrorMessage = LocalizationManager.Get("Common.GeneralError");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExecuteEmergencyUnlock => HasEmergencyCodeQr && PinLength == 4;

    [RelayCommand(CanExecute = nameof(CanExecuteEmergencyUnlock))]
    private async Task ExecuteRecoveryAsync()
    {
        ErrorMessage = null;

        if (_qrBuf.IsEmpty) { ErrorMessage = LocalizationManager.Get("Emergency.ErrorQrReadFailed"); return; }

        IsBusy = true;
        char[]? pinChars = null;
        try
        {
            pinChars = GetPinChars?.Invoke() ?? [];

            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

            var result = await _auth.EmergencyAccessUnlockAsync(_qrBuf.Span, pinChars.AsSpan(), dataDir, _cts.Token);

            // Wipe the recovery code immediately regardless of authentication success or failure (security first).
            // On WrongPin, HasEmergencyCodeQr = false disables the retry button, so the user must
            // reselect the PNG and re-read the QR code (re-entering only the PIN is not possible).
            _qrBuf.Dispose();

            switch (result)
            {
                case EmergencyAccessResult.Success:
                    IsSuccess = true;
                    RecoverySucceeded?.Invoke(this, EventArgs.Empty);
                    break;
                case EmergencyAccessResult.WrongPin:
                    HasEmergencyCodeQr = false;
                    ErrorMessage = LocalizationManager.Get("Emergency.ErrorQrOrPinInvalid");
                    break;
                case EmergencyAccessResult.NoMatchingVault:
                    HasEmergencyCodeQr = false;
                    ErrorMessage = LocalizationManager.Get("Emergency.ErrorNoVaultMatchingQr");
                    break;
                default:
                    HasEmergencyCodeQr = false;
                    ErrorMessage = LocalizationManager.Get("Emergency.ErrorQrOrPinInvalid");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed (Dispose) during the PIN derivation: not an error. The service has
            // already wiped the derived keys and did not touch the session.
        }
        catch (Exception ex)
        {
            _logger.LogError("[EmergencyAccessViewModel] ExecuteRecoveryAsync failed. [{ExType}]", ex.GetType().Name);
            ErrorMessage = LocalizationManager.Get("Common.GeneralError");
        }
        finally
        {
            if (pinChars?.Length > 0) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(pinChars.AsSpan()));
            IsBusy = false;
        }
    }

    // The PIN derivation runs off the UI thread, so the dialog can be closed while it is in flight.
    // Dispose (called on every close path via EmergencyAccessControl.Scrub) cancels it so a result that
    // completes afterwards is discarded instead of unlocking a session nobody is looking at.
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        _qrBuf.Dispose();
        GetPinChars = null;
    }

    private static async Task ReadQrFromPngAsync(string path, SecureCharBuffer buf)
    {
        using var fileStream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(fileStream.AsRandomAccessStream());
        var pixelProvider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var pixels = pixelProvider.DetachPixelData();
        try
        {
            var source = new RGBLuminanceSource(
                pixels, (int)decoder.PixelWidth, (int)decoder.PixelHeight,
                RGBLuminanceSource.BitmapFormat.BGRA32);
            var binarizer = new HybridBinarizer(source);
            var bitmap = new BinaryBitmap(binarizer);
            try
            {
                var result = new QRCodeReader().decode(bitmap);
                if (result?.Text != null)
                {
                    buf.SetFromSpan(result.Text.AsSpan());
                    // result.Text is a fresh, non-interned string owned solely by this decode call
                    // (never a literal) - wipe its backing buffer now that the EAC payload has been
                    // copied into the SecureCharBuffer, matching SecurePasswordHelper's own callers.
                    SecurePasswordHelper.ZeroStringInternals(result.Text);
                }
            }
            catch { /* buf remains empty; caller checks buf.IsEmpty */ }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pixels.AsSpan());
        }
    }
}
