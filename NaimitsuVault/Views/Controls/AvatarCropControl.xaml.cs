// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using NaimitsuVault.Helpers;
using NaimitsuVault.Services;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NaimitsuVault.Views.Controls;

public sealed partial class AvatarCropControl : UserControl
{
    private static readonly ILogger<AvatarCropControl> Logger = AppLog.For<AvatarCropControl>();

    private const double ViewportSize = 320;
    private const double CircleRadius = 120;

    private void SetViewportCursor(InputCursor? cursor)
        => Viewport.ViewportCursor = cursor;

    private uint _imgW, _imgH;
    private double _fitScale;
    private double _displayScale;
    private double _tx, _ty;
    private bool _isDragging;
    private Point _lastPointerPos;
    private bool _suppressSlider;
    private SoftwareBitmap? _softBitmap;
    private SoftwareBitmapSource? _bitmapSource;

    // Set by the host's Scrub() after the dialog closes. volatile: read from a thread-pool continuation in SetImageAsync.
    private volatile bool _isFinalizing;

    // Serializes SetImageAsync. The host calls it from Loaded; a ContentDialog can raise Loaded again after a
    // transient Unloaded right after it opens, so a second call could start while the first is still
    // decoding / inside SoftwareBitmapSource.SetBitmapAsync.
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    public AvatarCropControl()
    {
        InitializeComponent();

        // Unloaded tears down only once the control is finalizing (the host's Scrub() in the finally around
        // ShowAsync - the real guarantee). A ContentDialog sends Unloaded (while IsLoaded is still true) right
        // after it opens. Observed in this control's log: one Loaded, then two Unloaded ~16 ms later, and NO
        // second Loaded - so nothing re-applies the image if Unloaded has torn it down.
        // Disposing the image source on that Unloaded pulled it out from under an in-flight SetImageAsync
        // (a COMException surfaced as an unhandled exception) or cleared an already-shown image (blank crop view).
        // Same rule as EmergencyAccessControl / SecretDraftCompareContent / ProfileDraftCompareContent (_isFinalizing).
        Unloaded += (_, _) =>
        {
            if (!_isFinalizing)
            {
                Logger.LogInformation("AvatarCropControl.Unloaded ignored (not finalizing). [IsLoaded={IsLoaded}]", IsLoaded);
                return;
            }
            Scrub();
        };
        Viewport.PointerEntered += (_, _) => SetViewportCursor(InputSystemCursor.Create(InputSystemCursorShape.Hand));
        Viewport.PointerExited  += (_, _) => SetViewportCursor(null);
    }

    /// <summary>
    /// Final teardown (idempotent). Called by the host after the dialog closes, and by Unloaded once finalizing.
    /// Zero-clears the decoded bitmap. An in-flight SetImageAsync sees <see cref="_isFinalizing"/> and discards its result.
    /// </summary>
    public void Scrub()
    {
        _isFinalizing = true;
        CropImage.Source = null;

        // Order matters: zero the bitmap BEFORE disposing the source. Disposing a SoftwareBitmapSource also closes
        // the SoftwareBitmap it was given (measured 2026-09-20: the bitmap read Size=0x0 / ObjectDisposedException
        // right after the source's Dispose), so zeroing afterwards silently did nothing.
        if (_softBitmap != null) SoftwareBitmapZeroer.TryZero(_softBitmap);

        _bitmapSource?.Dispose();
        _bitmapSource = null;

        _softBitmap?.Dispose();
        _softBitmap = null;
    }

    /// <param name="buffer">
    /// A POH-pinned SecureByteBuffer. No GC-compaction ghosts occur even across an async await boundary.
    /// The stream write happens copy-free via PinnedArray.AsBuffer().
    /// </param>
    public async Task SetImageAsync(SecureByteBuffer buffer)
    {
        // Wait for any earlier call (see _applyLock) so two calls never interleave their Dispose/replace steps.
        await _applyLock.WaitAsync();
        // SoftwareBitmapSource keeps pixel data in memory, so it displays reliably regardless of
        // whether the visual tree is connected.
        // BitmapImage isn't used because it lazily decodes and renders blank when the visual tree isn't connected yet.
        SoftwareBitmap? softBitmap = null;
        try
        {
            // The dialog may have closed (Scrub) while this call was queued; the host's buffer is then already gone.
            if (_isFinalizing) return;

            Logger.LogInformation("AvatarCropControl.SetImageAsync start. [IsLoaded={IsLoaded}]", IsLoaded);
            using var stream = new InMemoryRandomAccessStream();
            // Wrap the POH-pinned array in an IBuffer and write it directly.
            // Removed the old flow of passing a movable-heap array via DataWriter.WriteBytes(byte[]).
            // AsBuffer() only creates an IBuffer that references the byte[]; it doesn't copy.
            // Since the array is pinned in the POH, its address is stable even across an await boundary.
            if (!buffer.IsEmpty)
                await stream.WriteAsync(buffer.PinnedArray!.AsBuffer(0, buffer.Length));
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            _imgW = decoder.OrientedPixelWidth;
            _imgH = decoder.OrientedPixelHeight;

            // Apply the EXIF rotation, same as BitmapImage
            softBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            // The Loaded event inside a ContentDialog changes the SynchronizationContext, so
            // execution can fall onto the thread pool after a WinRT await.
            // SoftwareBitmapSource.SetBitmapAsync throws E_ABORT when called off the UI thread,
            // so explicitly return to the UI thread via DispatcherQueue.
            var captured = softBitmap;
            softBitmap = null; // hand off ownership to the lambda (don't touch it in finally)
            await ApplyBitmapAsync(captured);
        }
        finally
        {
            // On exception interruption: zero-clear and release the decoded softBitmap
            // It's already null after handing off to captured, so no double release occurs
            if (softBitmap != null)
            {
                SoftwareBitmapZeroer.TryZero(softBitmap);
                softBitmap.Dispose();
            }
            // Wiping buffer is the caller's (ProfilePage's) responsibility, guaranteed via using
            _applyLock.Release();
        }
    }

    // Applies the SoftwareBitmap to a SoftwareBitmapSource on the UI thread and reflects it in Image.Source.
    // Runs directly if DispatcherQueue.HasThreadAccess is true; otherwise re-queues via TryEnqueue.
    private async Task ApplyBitmapAsync(SoftwareBitmap bitmap)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            await ApplyBitmapCoreAsync(bitmap);
            return;
        }

        var tcs = new TaskCompletionSource();
        bool enqueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, async () =>
        {
            try   { await ApplyBitmapCoreAsync(bitmap); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        if (!enqueued)
        {
            // When enqueueing is rejected (e.g. app shutting down, an auto-lock interruption), the
            // lambda never runs at all - not only does bitmap get orphaned without being
            // zero-cleared, but tcs also never completes, leaving the caller waiting forever.
            // Dispose it manually here and complete tcs.
            SoftwareBitmapZeroer.TryZero(bitmap);
            bitmap.Dispose();
            tcs.TrySetResult();
        }
        await tcs.Task;
    }

    private async Task ApplyBitmapCoreAsync(SoftwareBitmap bitmap)
    {
        SoftwareBitmap? b = bitmap;
        SoftwareBitmapSource? source = null;
        try
        {
            if (_isFinalizing) return; // the finally below zero-clears b

            // Build and fill the new source in locals first. The fields (_bitmapSource / _softBitmap) still hold
            // the previous image (if any) and stay untouched across the await, so nothing can dispose or null
            // the source that SetBitmapAsync is working on, and CropImage.Source is never left pointing at a
            // source that is not ready yet.
            source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(b);

            // Scrub() may have run while SetBitmapAsync was in flight; don't resurrect state after teardown.
            if (_isFinalizing) return; // the finally below disposes source and zero-clears b

            // Swap in the new image, then release the previous one (after it is no longer the Image's Source).
            var oldSource = _bitmapSource;
            var oldBitmap = _softBitmap;
            _bitmapSource = source;
            _softBitmap = b;
            source = null; // hand off ownership to _bitmapSource
            b = null;      // hand off ownership to _softBitmap

            CropImage.Width  = _imgW;
            CropImage.Height = _imgH;
            CropImage.Source = _bitmapSource;

            // Zero-clear the old bitmap once it has been replaced - BEFORE disposing the old source
            // (disposing a SoftwareBitmapSource also closes its SoftwareBitmap; see Scrub).
            if (oldBitmap != null) SoftwareBitmapZeroer.TryZero(oldBitmap);
            oldSource?.Dispose();
            oldBitmap?.Dispose();

            // Use the minimum scale that reliably covers the circle (diameter 240) as the initial value
            _fitScale = Math.Max(CircleRadius * 2 / _imgW, CircleRadius * 2 / _imgH);
            _displayScale = _fitScale;

            // Center the image within the viewport
            _tx = (ViewportSize - _imgW * _fitScale) / 2.0;
            _ty = (ViewportSize - _imgH * _fitScale) / 2.0;

            ApplyTransform();

            _suppressSlider = true;
            ZoomSlider.Value = 0;
            _suppressSlider = false;

            Logger.LogInformation("AvatarCropControl image applied. [IsLoaded={IsLoaded}]", IsLoaded);
        }
        finally
        {
            // On a SetBitmapAsync exception / early return: zero-clear the bitmap and release the unused source.
            // Zero BEFORE disposing the source: disposing a SoftwareBitmapSource also closes the SoftwareBitmap
            // it was given, after which the pixels can no longer be zeroed (measured 2026-09-20).
            if (b != null) SoftwareBitmapZeroer.TryZero(b);
            source?.Dispose();
            b?.Dispose();
        }
    }

    private void ApplyTransform()
    {
        ImgScale.ScaleX = _displayScale;
        ImgScale.ScaleY = _displayScale;
        ImgTranslate.X = _tx;
        ImgTranslate.Y = _ty;
    }

    // Slider value 0..100 -> zoom factor 1.0..5.0 (relative to fitScale)
    private void SetZoomFromSlider(double sliderValue)
    {
        double zoomFactor = 1.0 + sliderValue / 100.0 * 4.0;
        double newScale = _fitScale * zoomFactor;
        double ratio = newScale / _displayScale;

        // Zoom while keeping the circle's center (160, 160) fixed
        _tx = ViewportSize / 2.0 + (_tx - ViewportSize / 2.0) * ratio;
        _ty = ViewportSize / 2.0 + (_ty - ViewportSize / 2.0) * ratio;
        _displayScale = newScale;

        ClampTranslation();
        ApplyTransform();
    }

    // Clamp the pan amount so the image always covers the circle area
    private void ClampTranslation()
    {
        double scaledW = _imgW * _displayScale;
        double scaledH = _imgH * _displayScale;
        double left   = ViewportSize / 2.0 - CircleRadius;
        double right  = ViewportSize / 2.0 + CircleRadius;
        double top    = ViewportSize / 2.0 - CircleRadius;
        double bottom = ViewportSize / 2.0 + CircleRadius;

        _tx = Math.Min(_tx, left);
        _tx = Math.Max(_tx, right - scaledW);
        _ty = Math.Min(_ty, top);
        _ty = Math.Max(_ty, bottom - scaledH);
    }

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider || _imgW == 0) return;
        SetZoomFromSlider(e.NewValue);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
        => ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, ZoomSlider.Value - 10);

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
        => ZoomSlider.Value = Math.Min(ZoomSlider.Maximum, ZoomSlider.Value + 10);

    private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Viewport.CapturePointer(e.Pointer);
        _isDragging = true;
        _lastPointerPos = e.GetCurrentPoint(Viewport).Position;
        SetViewportCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeAll));
        e.Handled = true;
    }

    private void Viewport_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || _imgW == 0) return;
        var pos = e.GetCurrentPoint(Viewport).Position;
        _tx += pos.X - _lastPointerPos.X;
        _ty += pos.Y - _lastPointerPos.Y;
        _lastPointerPos = pos;
        ClampTranslation();
        ApplyTransform();
        e.Handled = true;
    }

    private void Viewport_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        Viewport.ReleasePointerCapture(e.Pointer);
        SetViewportCursor(InputSystemCursor.Create(InputSystemCursorShape.Hand));
    }

    private void Viewport_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        SetViewportCursor(InputSystemCursor.Create(InputSystemCursorShape.Hand));
    }

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_imgW == 0) return;
        var delta = e.GetCurrentPoint(Viewport).Properties.MouseWheelDelta;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + delta / 120.0 * 10, ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    /// <summary>
    /// Returns the crop rectangle (a square) in the original image's pixel space, computed from the current display position and zoom.
    /// </summary>
    public (int cropX, int cropY, int cropSize) GetCropInfo()
    {
        double center = ViewportSize / 2.0;
        double imgCx = (center - _tx) / _displayScale;
        double imgCy = (center - _ty) / _displayScale;
        double imgR  = CircleRadius / _displayScale;

        int cropSize = (int)(imgR * 2);
        int cropX    = (int)Math.Round(imgCx - imgR);
        int cropY    = (int)Math.Round(imgCy - imgR);

        cropX    = Math.Clamp(cropX,    0, (int)_imgW - 1);
        cropY    = Math.Clamp(cropY,    0, (int)_imgH - 1);
        cropSize = Math.Clamp(cropSize, 1, (int)Math.Min(_imgW - cropX, _imgH - cropY));

        return (cropX, cropY, cropSize);
    }
}
