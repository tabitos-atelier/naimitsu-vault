// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NaimitsuVault.Helpers;
using NaimitsuVault.ViewModels;
using Windows.Foundation;
using Windows.Graphics;
using WinUIEx;

namespace NaimitsuVault.Views;

/// <summary>
/// Full-screen still-image overlay plus drag selection that returns a BGRA sub-image of the QR code.
/// </summary>
public sealed partial class SnippingWindow : WindowEx
{
    private readonly ScreenCaptureHelper.CaptureResult _capture;
    private readonly double _dpiScale; // DIPs → physical px

    private bool _isDragging;
    private Point _startDip;
    private Rect _selectedDip; // selection rectangle in DIPs

    /// <summary>The BGRA byte sequence of the region the user selected (null = cancelled).</summary>
    public byte[]? ResultBgra { get; private set; }
    public int ResultWidth  { get; private set; }
    public int ResultHeight { get; private set; }

    private readonly TaskCompletionSource<bool> _tcs = new();

    public SnippingWindow(ScreenCaptureHelper.CaptureResult capture, double dpiScale,
        ElementTheme theme = ElementTheme.Default)
    {
        _capture  = capture;
        _dpiScale = dpiScale;

        InitializeComponent();

        // Apply the same theme as the caller's dialog
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
            var settingsVm = ((App)Application.Current).Services.GetRequiredService<AppSettingsViewModel>();
            ApplyFontFamily(root, settingsVm.FontFamily);
        }
        WeakReferenceMessenger.Default.Register<FontFamilyChangedMessage>(this, (_, msg) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Content is FrameworkElement r) ApplyFontFamily(r, msg.FontFamily);
            }));

        // No title bar, no border
        ExtendsContentIntoTitleBar = true;

        // Position to cover the entire virtual screen
        var appWin = AppWindow;
        appWin.Move(new PointInt32(_capture.Left, _capture.Top));
        appWin.Resize(new SizeInt32(_capture.Width, _capture.Height));

        // After closing, the FullScreen presenter never re-associates the IME context with the
        // WinUI 3 XAML Island, leaving Japanese input impossible in every text box.
        // Using a borderless, always-on-top OverlappedPresenter achieves a full-screen-equivalent
        // appearance while keeping the IME intact.
        var overlapped = OverlappedPresenter.Create();
        overlapped.IsResizable   = false;
        overlapped.IsMinimizable = false;
        overlapped.IsMaximizable = false;
        overlapped.IsAlwaysOnTop = true;
        overlapped.SetBorderAndTitleBar(false, false);
        appWin.SetPresenter(overlapped);

        // Load the screenshot image
        LoadScreenshot();

        // Cancel with Esc
        RootCanvas.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
                Close();
        };
        RootCanvas.IsTabStop = true;

        Closed += (_, _) =>
        {
            _tcs.TrySetResult(true);

            // Zero-clear WriteableBitmap's pixel buffer before dropping the strong reference
            if (ScreenImage.Source is WriteableBitmap wb)
            {
                try
                {
                    using var pixStream = wb.PixelBuffer.AsStream();
                    long remaining = pixStream.Length;
                    // Avoids a temporary allocation on the GC heap.
                    // stackalloc reserves a zero chunk on the stack (already zero-initialized in a safe context).
                    // Since the written content is all zeros (no secret data), ZeroMemory isn't needed.
                    Span<byte> zeroChunk = stackalloc byte[4096];
                    pixStream.Position = 0;
                    while (remaining > 0)
                    {
                        int count = (int)Math.Min(zeroChunk.Length, remaining);
                        pixStream.Write(zeroChunk[..count]); // Stream.Write(ReadOnlySpan<byte>)
                        remaining -= count;
                    }
                }
                catch { }
                ScreenImage.Source = null;
            }

            // Physically erase the full-screen capture buffer (the caller is responsible for wiping ResultBgra)
            CryptographicOperations.ZeroMemory(_capture.Bgra.AsSpan());
            WeakReferenceMessenger.Default.Unregister<FontFamilyChangedMessage>(this);
        };
    }

    // FontFamily is only a first-class property on Control/TextBlock, not on FrameworkElement (Content
    // here is a plain Canvas). Set the underlying inheritable DependencyProperty directly via SetValue so
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

    /// <summary>
    /// Shows the SnippingWindow and waits until the user selects a region.
    /// </summary>
    public Task<bool> WaitForResultAsync()
    {
        Activate();
        return _tcs.Task;
    }

    private void LoadScreenshot()
    {
        var wb = new WriteableBitmap(_capture.Width, _capture.Height);
        using var stream = wb.PixelBuffer.AsStream();
        stream.Write(_capture.Bgra, 0, _capture.Bgra.Length);

        ScreenImage.Source = wb;
        ScreenImage.Width  = _capture.Width  / _dpiScale;
        ScreenImage.Height = _capture.Height / _dpiScale;

        // Position the hint at the top-center of the screen
        RootCanvas.Loaded += (_, _) =>
        {
            double hintW = HintBorder.ActualWidth;
            double canvasW = _capture.Width / _dpiScale;
            Canvas.SetLeft(HintBorder, (canvasW - hintW) / 2);
            Canvas.SetTop(HintBorder, 24);
            RootCanvas.Focus(FocusState.Programmatic);
        };
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _startDip  = e.GetCurrentPoint(RootCanvas).Position;
        _isDragging = true;
        SelectionRect.Visibility = Visibility.Visible;
        UpdateSelectionRect(_startDip, _startDip);
        RootCanvas.CapturePointer(e.Pointer);
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        UpdateSelectionRect(_startDip, e.GetCurrentPoint(RootCanvas).Position);
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        RootCanvas.ReleasePointerCapture(e.Pointer);

        var end = e.GetCurrentPoint(RootCanvas).Position;
        UpdateSelectionRect(_startDip, end);

        // Ignore if the selection is too small
        if (_selectedDip.Width < 10 || _selectedDip.Height < 10) return;

        CropAndClose();
    }

    private void UpdateSelectionRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);

        _selectedDip = new Rect(x, y, w, h);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect,  y);
        SelectionRect.Width  = w;
        SelectionRect.Height = h;
    }

    private void CropAndClose()
    {
        // Convert DIP -> physical pixels
        int px = (int)(_selectedDip.X      * _dpiScale);
        int py = (int)(_selectedDip.Y      * _dpiScale);
        int pw = (int)(_selectedDip.Width  * _dpiScale);
        int ph = (int)(_selectedDip.Height * _dpiScale);

        // Normalize since the virtual screen's top-left corresponds to _capture.Left / .Top
        px = Math.Clamp(px, 0, _capture.Width  - 1);
        py = Math.Clamp(py, 0, _capture.Height - 1);
        pw = Math.Clamp(pw, 1, _capture.Width  - px);
        ph = Math.Clamp(ph, 1, _capture.Height - py);

        // Crop the BGRA
        // Allocate it POH-pinned to eliminate stale-address ghosts from GC compaction.
        // This guarantees ZeroMemory always hits the correct address (the caller, TotpSetupDialog, is responsible for wiping it).
        var cropped = GC.AllocateArray<byte>(pw * ph * 4, pinned: true);
        for (int row = 0; row < ph; row++)
        {
            int srcOff = ((py + row) * _capture.Width + px) * 4;
            int dstOff = row * pw * 4;
            Buffer.BlockCopy(_capture.Bgra, srcOff, cropped, dstOff, pw * 4);
        }

        ResultBgra   = cropped;
        ResultWidth  = pw;
        ResultHeight = ph;

        Close();
    }
}
