// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using Windows.Storage.Streams;
using Windows.System;
using WinUIEx;
using ZXing;
using ZXing.Common;

namespace NaimitsuVault.Views;

/// <summary>
/// TOTP setup dialog.
/// Accepts QR capture, manual entry, and recovery key drops.
/// </summary>
public sealed partial class TotpSetupDialog : ContentDialog
{
    private readonly WindowEx _parentWindow;
    // Manages file-path PII in a POH-pinned SecureCharBuffer.
    // Physically erased with ZeroMemory in OnUnloaded. ObservableCollection<string> has been removed.
    private readonly List<SecureCharBuffer> _droppedPathBufs = [];
    private DispatcherTimer? _totpTimer;
    private readonly ClipboardAutoEraser _clipboardEraser = new();
    // The raw string field has been removed and replaced with a POH-pinned SecureCharBuffer.
    // The timer Tick passes the Span directly to TotpCalculator.Generate(ReadOnlySpan<char>),
    // eliminating per-tick string bursts (which mass-produce ghosts).
    private readonly SecureCharBuffer _previewSecretBuf = new();
    // Raw (unspaced) code behind TotpCodeNormal/TotpCodeExpiring.Text, which display the
    // space-grouped form (TotpCalculator.FormatCodeForDisplay) - CopyTotpCode_Click must copy this,
    // not the displayed text, since some 2FA input fields don't tolerate the inserted space.
    private string _lastPreviewCode = string.Empty;
    private InMemoryRandomAccessStream? _thumbnailStream;
    // Tracks the (normalized) secret last written into SecretTextBox by QR decode, so
    // SecretTextBox_PasswordChanged can tell a subsequent hand-edit apart and revert
    // TotpDigits/Period/Algorithm to RFC 6238 defaults (a stale 8-digit/SHA256 QR config must not
    // silently apply to a hand-typed secret). POH-pinned buffer, not a string, for the same reason
    // as _previewSecretBuf: it holds live TOTP key material.
    private readonly SecureCharBuffer _lastQrSecretBuf = new();

    /// <summary>The confirmed TOTP secret (Base32). Null when cancelled.</summary>
    public string? TotpSecret { get; private set; }

    /// <summary>Digits/Period/Algorithm parsed from the otpauth:// URI (manual entry uses RFC 6238 defaults: 6/30/SHA1).</summary>
    public int TotpDigits { get; private set; } = 6;
    public int TotpPeriod { get; private set; } = 30;
    public string TotpAlgorithm { get; private set; } = TotpCalculator.DefaultAlgorithm;

    // Snapshotted by SaveSetupButton_Click right before Hide(). Hide() drives this ContentDialog's
    // Unloaded, which clears _droppedPathBufs (ZeroMemory'ing the POH buffers) - and that teardown can
    // complete before the caller's `await dialog.ShowAsync()` resumes, so DroppedFilePaths must not
    // depend on _droppedPathBufs still being populated by the time the caller reads it.
    private List<string>? _savedDroppedFilePaths;

    /// <summary>Paths of dropped recovery key files. Consumed by the caller after ShowAsync returns.</summary>
    public IReadOnlyList<string> DroppedFilePaths =>
        _savedDroppedFilePaths ?? _droppedPathBufs.Select(b => new string(b.Span)).ToList();

    /// <summary>Current attached image count (for upper-bound checks).</summary>
    public int CurrentImageCount { get; set; }

    public TotpSetupDialog(WindowEx parentWindow)
    {
        _parentWindow = parentWindow;
        InitializeComponent();

        Title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { Glyph = "\uE8D7", VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = LocalizationManager.Get("Common.TotpSecret"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        Unloaded += OnUnloaded;

        // PreviewKeyDown (tunneling) on the dialog root, rather than SecretTextBox.KeyDown, so the
        // toggle still fires regardless of which element inside this ContentDialog currently has focus.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.H || !IsCtrlDown()) return;
            SecretTextBox.PasswordRevealMode = SecretTextBox.PasswordRevealMode == PasswordRevealMode.Visible
                ? PasswordRevealMode.Hidden
                : PasswordRevealMode.Visible;
            e.Handled = true;
        };
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _clipboardEraser.Dispose();
        _thumbnailStream?.Dispose();
        _thumbnailStream = null;

        // Physically wipe the POH-pinned secret buffers with ZeroMemory.
        _previewSecretBuf.Dispose();
        _lastQrSecretBuf.Dispose();

        // Physically wipe the POH-pinned buffers for dropped file paths with ZeroMemory.
        foreach (var buf in _droppedPathBufs) buf.Dispose();
        _droppedPathBufs.Clear();

        // ExtractPinned reads out whatever secret is still live in SecretTextBox (e.g. if the dialog
        // is torn down without a Save/Discard click) and clears SecretTextBox.Password as a side
        // effect; the returned pinned buffer must still be zeroed here (same pattern as
        // EmergencyAccessControl.Scrub).
        var discardedSecret = SecurePasswordHelper.ExtractPinned(SecretTextBox);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(discardedSecret.AsSpan()));
        _lastPreviewCode      = string.Empty;
        TotpCodeNormal.Text   = string.Empty;
        TotpCodeExpiring.Text = string.Empty;
        TotpAdjacentText.Text = string.Empty;
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        StopTotpPreview();
        TotpSecret = null;
        Hide();
    }

    private void SaveSetupButton_Click(object sender, RoutedEventArgs e)
    {
        StopTotpPreview();
        // SaveSetupButton stays disabled until SecretTextBox_PasswordChanged confirms a valid secret,
        // so this re-check is defense against a stale enabled state rather than the normal path.
        // Reads SecretTextBox.Password only via SecurePasswordHelper.Borrow, same as every other
        // password-bearing control in this codebase, so the string the getter hands back is
        // ZeroMemory'd immediately instead of lingering unzeroed on the GC heap.
        string? normalized = null;
        SecurePasswordHelper.Borrow(SecretTextBox, raw =>
        {
            var scratch = GC.AllocateArray<char>(raw.Length, pinned: true);
            try
            {
                var span = scratch.AsSpan(0, NormalizeInto(raw, scratch));
                if (!IsValidSecret(span))
                {
                    ShowValidation(LocalizationManager.Get("Common.WarningInvalidBase32Secret"));
                    return;
                }
                // TotpSecret is a plain string because TotpCalculator.TotpConfig.Secret (the
                // boundary to SecretsViewModel.SetTotpConfig) is already string-typed app-wide; this
                // single allocation at the confirmed-save point mirrors the existing QR-decode path's
                // "PasswordBox needs a string" allocation and isn't zeroed because it's the value the
                // caller is about to consume.
                normalized = new string(span);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(scratch.AsSpan()));
            }
        });
        if (normalized == null) return;
        TotpSecret = normalized;
        // Snapshot before Hide(): Hide() drives Unloaded, which clears _droppedPathBufs, and that can
        // finish before the caller's `await dialog.ShowAsync()` resumes to read DroppedFilePaths.
        _savedDroppedFilePaths = _droppedPathBufs.Select(b => new string(b.Span)).ToList();
        Hide();
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureButton.IsEnabled = false;
        QrInfoBar.IsOpen = false;

        try
        {
            _parentWindow.AppWindow.Hide();
            await Task.Delay(150);

            ScreenCaptureHelper.CaptureResult capture;
            try
            {
                capture = ScreenCaptureHelper.Capture();
            }
            catch
            {
                _parentWindow.Activate();
                ShowQrInfo(LocalizationManager.Get("Common.ErrorCaptureFailed"), InfoBarSeverity.Error);
                return;
            }

            _parentWindow.Activate();
            await Task.Delay(100);

            double dpiScale = _parentWindow.Content?.XamlRoot?.RasterizationScale ?? 1.0;
            var snipping = new SnippingWindow(capture, dpiScale, RequestedTheme);
            await snipping.WaitForResultAsync();

            if (snipping.ResultBgra == null) return; // cancelled

            // 5. Decode the QR code with ZXing (this call site owns the responsibility for wiping ResultBgra)
            byte[] resultBgra = snipping.ResultBgra;
            // Receive the decoded result into a SecureCharBuffer.
            // The raw string ZXing returns (result.Text / parsed.Secret) only lives within this
            // function's stack frame; after transcribing to the SecureCharBuffer, the reference
            // drops and it becomes eligible for GC.
            var secretBuf = new SecureCharBuffer();
            try
            {
                if (!DecodeQrToSecret(resultBgra, snipping.ResultWidth, snipping.ResultHeight, secretBuf, out var digits, out var period, out var algorithm))
                {
                    ShowQrInfo(LocalizationManager.Get("Totp.WarningQrRecognitionFailed"), InfoBarSeverity.Warning);
                    return;
                }
                ApplyDecodedSecret(secretBuf, digits, period, algorithm);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(resultBgra.AsSpan());
                secretBuf.Dispose(); // immediately ZeroMemory the POH-pinned buffer
            }
        }
        finally
        {
            CaptureButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Puts a decoded secret and its digits / period / algorithm into the dialog, as if the user had entered it:
    /// the secret goes into SecretTextBox (whose PasswordChanged validates it and starts the live preview).
    /// Shared by the QR capture path and the pasted otpauth:// URI path.
    /// </summary>
    private void ApplyDecodedSecret(SecureCharBuffer secretBuf, int digits, int period, string algorithm)
    {
        TotpDigits = digits;
        TotpPeriod = period;
        TotpAlgorithm = algorithm;
        // PasswordBox.Password requires a string, so allocate exactly one temporary string and pass it.
        // PasswordBox's internal retention can't be controlled, but secretBuf is disposed immediately after, ZeroMemory'ing the POH source.
        // No success message here: SecretTextBox_PasswordChanged validates the decoded secret and
        // switches to the post-verification phase (SetupPanel collapses, the live code appears),
        // which is itself the success feedback.
        var pwdStr = new string(secretBuf.Span);
        // _lastQrSecretBuf must be set BEFORE the Password assignment below: WinUI 3 fires
        // PasswordChanged synchronously from within the property setter, so setting it after
        // would let SecretTextBox_PasswordChanged see _lastQrSecretBuf still at its old value
        // and treat this same decoded value as a hand-edit, reverting TotpDigits/Period/
        // Algorithm back to RFC 6238 defaults before this method even returns.
        var normScratch = GC.AllocateArray<char>(secretBuf.Span.Length, pinned: true);
        try
        {
            _lastQrSecretBuf.SetFromSpan(normScratch.AsSpan(0, NormalizeInto(secretBuf.Span, normScratch)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(normScratch.AsSpan()));
        }
        SecretTextBox.Password = pwdStr;
    }

    // True while SecretTextBox shows a rejected otpauth:// URI unmasked (see RevealWhileUriRejected).
    private bool _revealedForRejectedUri;

    /// <summary>
    /// Parses SecretTextBox's content as an otpauth:// URI. Returns null when it isn't a usable one: HOTP, an
    /// unsupported digits / period / algorithm (TotpCalculator.ParseOtpAuth rejects those instead of falling
    /// back to a default), no secret, or a secret that isn't valid Base32 (the same rule as a hand-entered key).
    /// </summary>
    private static TotpCalculator.OtpAuthParams? ParseOtpAuthInput(ReadOnlySpan<char> uriSpan)
    {
        // ParseOtpAuth takes a string, so the URI is materialized once and zeroed right after. The substrings
        // ParseOtpAuth derives from it (query values) can't be zeroed from here - the same residue the QR
        // capture path already has, where ZXing hands back the URI as a plain string.
        var uri = new string(uriSpan);
        TotpCalculator.OtpAuthParams? parsed;
        try
        {
            parsed = TotpCalculator.ParseOtpAuth(uri);
        }
        finally
        {
            SecurePasswordHelper.ZeroStringInternals(uri);
        }
        if (parsed == null) return null;

        var scratch = GC.AllocateArray<char>(parsed.Secret.Length, pinned: true);
        try
        {
            if (IsValidSecret(scratch.AsSpan(0, NormalizeInto(parsed.Secret, scratch)))) return parsed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(scratch.AsSpan()));
        }
        // parsed.Secret was created by ParseOtpAuth and is referenced by nothing else, so it's safe to zero.
        SecurePasswordHelper.ZeroStringInternals(parsed.Secret);
        return null;
    }

    /// <summary>
    /// While a rejected otpauth:// URI is in SecretTextBox, show it unmasked so the user can see what is wrong
    /// and correct it in place: the box is a PasswordBox with the reveal button off, so a long masked URI can
    /// neither be read nor edited. The reveal is applied once on entering the rejected state (a Ctrl+H hide
    /// afterwards is respected) and undone when the content stops being a rejected URI.
    /// </summary>
    private void RevealWhileUriRejected(bool rejected)
    {
        if (rejected && !_revealedForRejectedUri)
        {
            SecretTextBox.PasswordRevealMode = PasswordRevealMode.Visible;
            _revealedForRejectedUri = true;
        }
        else if (!rejected && _revealedForRejectedUri)
        {
            SecretTextBox.PasswordRevealMode = PasswordRevealMode.Hidden;
            _revealedForRejectedUri = false;
        }
    }

    /// <summary>Replaces an otpauth:// URI in SecretTextBox with the secret it carries.</summary>
    private void ApplyPastedOtpAuth(TotpCalculator.OtpAuthParams parsed)
    {
        var secretBuf = new SecureCharBuffer();
        try
        {
            RevealWhileUriRejected(false); // the URI is accepted: back to a masked secret
            secretBuf.SetFromSpan(parsed.Secret.AsSpan());
            ApplyDecodedSecret(secretBuf, parsed.Digits, parsed.Period, parsed.Algorithm);
        }
        finally
        {
            secretBuf.Dispose(); // immediately ZeroMemory the POH-pinned buffer
            // parsed.Secret was created by ParseOtpAuth and is referenced by nothing else, so it's safe to zero.
            SecurePasswordHelper.ZeroStringInternals(parsed.Secret);
        }
    }

    private void SecretTextBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // Set inside the Borrow callback below and applied after it returns: applying assigns
        // SecretTextBox.Password, which re-enters this handler, and that must not happen while the outer
        // Borrow still holds the borrowed value.
        TotpCalculator.OtpAuthParams? pastedUri = null;

        // Fires on every keystroke, so this is the highest-volume read of this control: go through
        // SecurePasswordHelper.Borrow (same discipline as every other password control in this
        // codebase) rather than SecretTextBox.Password directly, and normalize into a scratch
        // POH-pinned buffer instead of string.ToUpperInvariant()/Replace(), so no unzeroed plaintext
        // string of the secret is minted on the GC heap per keystroke.
        SecurePasswordHelper.Borrow(SecretTextBox, raw =>
        {
            // An otpauth:// URI carries digits / period / algorithm as well as the secret. When it parses, skip
            // the normal handling here: the URI is replaced by its secret after Borrow returns, and that
            // assignment re-enters this handler with the plain secret. When it doesn't (HOTP, unsupported
            // parameter, no usable secret) it falls through and is rejected like any other non-Base32 input,
            // with the same format message, and is shown unmasked so it can be corrected in place.
            // Trim: a URI copied from a page or a terminal often carries a trailing newline or space.
            // Checked on every change rather than only on a paste, so a rejected URI that the user then
            // corrects in place is converted as soon as it becomes valid. A URI half-typed by hand can't be
            // converted early: it stays rejected until it carries a full, valid Base32 secret.
            var trimmed = raw.Trim();
            bool isUri = trimmed.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase);
            if (isUri)
            {
                pastedUri = ParseOtpAuthInput(trimmed);
                if (pastedUri != null) return;
            }
            bool rejectedUri = isUri; // an otpauth:// URI that was not accepted

            var scratch = GC.AllocateArray<char>(raw.Length, pinned: true);
            try
            {
                var normalized = scratch.AsSpan(0, NormalizeInto(raw, scratch));
                if (!normalized.SequenceEqual(_lastQrSecretBuf.Span))
                {
                    // Hand-edited away from the last QR-decoded value: revert to RFC 6238 defaults.
                    TotpDigits = 6;
                    TotpPeriod = 30;
                    TotpAlgorithm = TotpCalculator.DefaultAlgorithm;
                    _lastQrSecretBuf.Zero();
                }
                bool valid = IsValidSecret(normalized);
                SaveSetupButton.IsEnabled = valid;
                // Only a rejected URI gets a message while editing: the user entered something that looks right,
                // so a silently disabled Save button would give no hint why nothing happened. Ordinary partial
                // typing stays quiet.
                ValidationText.Text = rejectedUri
                    ? LocalizationManager.Get("Common.WarningInvalidBase32Secret")
                    : string.Empty;
                ValidationText.Visibility = (!valid && raw.Length > 0)
                    ? Visibility.Visible : Visibility.Collapsed;
                RevealWhileUriRejected(rejectedUri);

                if (valid)
                    StartTotpPreview(normalized);
                else
                    StopTotpPreview();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(scratch.AsSpan()));
            }
        });

        if (pastedUri != null) ApplyPastedOtpAuth(pastedUri);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.Handled = true;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();

        foreach (var item in items)
        {
            if (item is not Windows.Storage.StorageFile file) continue;
            var ext = Path.GetExtension(file.Path).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".pdf")) continue;

            // ZeroMemory the old path buffers before registering the new pinned buffer.
            foreach (var buf in _droppedPathBufs) buf.Dispose();
            _droppedPathBufs.Clear();
            var pathBuf = new SecureCharBuffer();
            pathBuf.SetFromSpan(file.Path.AsSpan());
            _droppedPathBufs.Add(pathBuf);
            await ShowThumbnailAsync(file.Path);
            break;
        }
    }

    private async Task ShowThumbnailAsync(string filePath)
    {
        byte[]? data      = null;
        byte[]? thumbData = null;
        try
        {
            // Replaced File.ReadAllBytesAsync (a movable array) with a direct FileStream read
            // into a GC.AllocateArray(pinned: true) immovable pinned array, eliminating GC-compaction ghosts.
            var fileInfo = new FileInfo(filePath);
            data = GC.AllocateArray<byte>((int)fileInfo.Length, pinned: true);
            await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4096, useAsync: true))
            {
                await fs.ReadExactlyAsync(data.AsMemory());
            }

            bool isPdf = filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            thumbData = await ImageHelper.CreateThumbnailAsync(data, 64, isPdf);

            _thumbnailStream?.Dispose();
            _thumbnailStream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(_thumbnailStream);
            writer.WriteBytes(thumbData);
            await writer.StoreAsync();
            writer.DetachStream();
            _thumbnailStream.Seek(0);

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(_thumbnailStream);
            ThumbnailImage.Source = bmp;
            ThumbnailBorder.Visibility = Visibility.Visible;
        }
        catch
        {
            ThumbnailBorder.Visibility = Visibility.Collapsed;
        }
        finally
        {
            if (data      != null) CryptographicOperations.ZeroMemory(data.AsSpan());
            if (thumbData != null) CryptographicOperations.ZeroMemory(thumbData.AsSpan());
        }
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    // Parameter changed to ReadOnlySpan<char>. The span is consumed synchronously, so this is safe across async/closures.
    // Transcribe into the POH-pinned region via SecureCharBuffer.SetFromSpan,
    // then subsequent timer Ticks pass _previewSecretBuf.Span directly to TotpCalculator.Generate.
    private void StartTotpPreview(ReadOnlySpan<char> secret)
    {
        _previewSecretBuf.SetFromSpan(secret);
        RefreshTotpCode();
        if (_totpTimer == null)
        {
            _totpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _totpTimer.Tick += (_, _) => RefreshTotpCode();
        }
        _totpTimer.Start();
        TotpPreviewBorder.Visibility = Visibility.Visible;
        TotpTimeSyncText.Visibility  = Visibility.Visible;
        // Phase switch: a confirmed secret needs no further capture/manual-entry UI, so hand the
        // freed space to the (now relevant) backup code section instead.
        SetupPanel.Visibility      = Visibility.Collapsed;
        BackupCodePanel.Visibility = Visibility.Visible;
    }

    private void StopTotpPreview()
    {
        _totpTimer?.Stop();
        TotpPreviewBorder.Visibility = Visibility.Collapsed;
        TotpTimeSyncText.Visibility  = Visibility.Collapsed;
        _previewSecretBuf.Dispose(); // ZeroMemory the POH-pinned buffer (idempotent: safe to call twice)
        SetupPanel.Visibility      = Visibility.Visible;
        BackupCodePanel.Visibility = Visibility.Collapsed;
    }

    private void RefreshTotpCode()
    {
        if (_previewSecretBuf.IsEmpty) return;
        try
        {
            // Pass _previewSecretBuf.Span (ReadOnlySpan<char>) directly.
            // TotpCalculator's span overload eliminates per-tick string bursts.
            var (code, rem) = TotpCalculator.Generate(_previewSecretBuf.Span, TotpDigits, TotpPeriod, TotpAlgorithm);
            bool expiring   = rem <= 5;

            _lastPreviewCode = code;
            var displayCode = TotpCalculator.FormatCodeForDisplay(code);
            TotpCodeNormal.Text      = displayCode;
            TotpCodeExpiring.Text    = displayCode;
            TotpCodeNormal.Visibility   = expiring ? Visibility.Collapsed : Visibility.Visible;
            TotpCodeExpiring.Visibility = expiring ? Visibility.Visible   : Visibility.Collapsed;
            TotpSecondsText.Text    = $"{rem}s";
            TotpProgressBar.Value   = rem;

            var prev = TotpCalculator.GenerateAtOffset(_previewSecretBuf.Span, -1, TotpDigits, TotpPeriod, TotpAlgorithm);
            var next = TotpCalculator.GenerateAtOffset(_previewSecretBuf.Span, +1, TotpDigits, TotpPeriod, TotpAlgorithm);
            TotpAdjacentText.Text = string.Format(
                LocalizationManager.Get("Totp.TimeDriftCodes"),
                TotpCalculator.FormatCodeForDisplay(prev),
                TotpCalculator.FormatCodeForDisplay(next),
                TotpPeriod);
        }
        catch
        {
            StopTotpPreview();
        }
    }

    // Removed the string return-value form and refactored to a contract signature that transcribes
    // in-place into a SecureCharBuffer supplied by the caller.
    // The raw strings ZXing returns (result.Text / parsed.Secret) only live within this function's
    // stack; after SetFromSpan into the SecureCharBuffer, the reference drops and it becomes eligible for GC.
    private static bool DecodeQrToSecret(byte[] bgra, int width, int height, SecureCharBuffer buf, out int digits, out int period, out string algorithm)
    {
        digits = 6;
        period = 30;
        algorithm = TotpCalculator.DefaultAlgorithm;
        var rgb = new byte[width * height * 3];
        try
        {
            for (int i = 0; i < width * height; i++)
            {
                rgb[i * 3 + 0] = bgra[i * 4 + 2]; // R
                rgb[i * 3 + 1] = bgra[i * 4 + 1]; // G
                rgb[i * 3 + 2] = bgra[i * 4 + 0]; // B
            }

            var src    = new RGBLuminanceSource(rgb, width, height);
            var bmp    = new BinaryBitmap(new HybridBinarizer(src));
            var reader = new ZXing.QrCode.QRCodeReader();
            try
            {
                var result = reader.decode(bmp);
                if (result == null) return false;

                ReadOnlySpan<char> textSpan = result.Text.AsSpan();
                if (textSpan.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
                {
                    var parsed = TotpCalculator.ParseOtpAuth(result.Text);
                    if (parsed?.Secret == null) return false;
                    // parsed.Secret is a temporary string internal to ParseOtpAuth; after transcribing, the reference drops and it's eligible for GC.
                    buf.SetFromSpan(parsed.Secret.AsSpan());
                    digits = parsed.Digits;
                    period = parsed.Period;
                    algorithm = parsed.Algorithm;
                    return true;
                }
                // If it's not a URI, treat it as a Base32 secret directly (RFC 6238 defaults apply)
                buf.SetFromSpan(textSpan);
                return true;
            }
            catch { return false; }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rgb.AsSpan());
        }
    }

    // Writes the normalized (uppercase, spaces/dashes stripped) form of raw into dest in place and
    // returns the written length. dest must be at least raw.Length long (the normalized form is
    // never longer than the input). Span-based so callers can normalize a borrowed PasswordBox value
    // without ever allocating an intermediate plaintext string.
    private static int NormalizeInto(ReadOnlySpan<char> raw, Span<char> dest)
    {
        int written = 0;
        foreach (var c in raw)
        {
            if (c is ' ' or '-') continue;
            dest[written++] = char.ToUpperInvariant(c);
        }
        return written;
    }

    private static bool IsValidSecret(ReadOnlySpan<char> secret)
    {
        if (secret.Length < 8) return false;
        const string base32Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567=";
        foreach (var c in secret)
            if (base32Chars.IndexOf(c) < 0) return false;
        return true;
    }

    private void ShowQrInfo(string message, InfoBarSeverity severity)
    {
        QrInfoBar.Severity = severity;
        QrInfoBar.Message  = message;
        QrInfoBar.IsOpen   = true;
    }

    private void CopyTotpCode_Click(object sender, RoutedEventArgs e)
    {
        var code = _lastPreviewCode;
        if (string.IsNullOrEmpty(code)) return;
        ClipboardHelper.SetText(code);
        _clipboardEraser.ScheduleClear(code.AsSpan());
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text       = message;
        ValidationText.Visibility = Visibility.Visible;
    }
}
