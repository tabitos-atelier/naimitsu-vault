// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NaimitsuVault.Helpers;

/// <summary>
/// Captures the virtual screen (composite of all monitors) via GDI P/Invoke.
/// Returns a raw BGRA 32-bit byte sequence plus the screen rectangle info.
/// </summary>
public static class ScreenCaptureHelper
{
    public sealed record CaptureResult(
        byte[] Bgra,
        int Left,
        int Top,
        int Width,
        int Height);

    /// <summary>
    /// Captures the entire virtual screen, including all monitors, via BitBlt.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="CaptureResult.Bgra"/> is the raw pixel sequence of the entire virtual
    /// screen (which may include the window contents of other apps), so zeroing it after use is the caller's responsibility.
    /// </remarks>
    public static CaptureResult Capture()
    {
        // Logical coordinates of the virtual screen (no DPI scaling; GDI works in physical pixels)
        int left   = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top    = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width  = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        // All displays off/disconnected etc. can make GetSystemMetrics report 0 or a negative value;
        // proceeding would allocate a negative-length array or fail deep inside GDI with a less
        // diagnosable error.
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Virtual screen dimension is invalid ({width}x{height}).");

        nint hdcScreen = nint.Zero, hdcMem = nint.Zero, hBitmap = nint.Zero, hOldBitmap = nint.Zero;
        byte[] bgra = [];
        try
        {
            hdcScreen = GetDC(nint.Zero);
            if (hdcScreen == nint.Zero) throw new InvalidOperationException("GetDC failed.");

            hdcMem = CreateCompatibleDC(hdcScreen);
            if (hdcMem == nint.Zero) throw new InvalidOperationException("CreateCompatibleDC failed.");

            hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == nint.Zero) throw new InvalidOperationException("CreateCompatibleBitmap failed.");

            hOldBitmap = SelectObject(hdcMem, hBitmap);
            if (!BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, SRCCOPY))
                throw new InvalidOperationException("BitBlt failed.");

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize        = Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth       = width;
            bmi.bmiHeader.biHeight      = -height; // top-down
            bmi.bmiHeader.biPlanes      = 1;
            bmi.bmiHeader.biBitCount    = 32;
            bmi.bmiHeader.biCompression = BI_RGB;

            bgra = new byte[width * height * 4];
            // Returns the number of scanlines copied, 0 on failure. An unchecked failure would silently
            // hand back a zero-filled (black) image instead of surfacing the error.
            int copiedLines = GetDIBits(hdcMem, hBitmap, 0, (uint)height, bgra, ref bmi, DIB_RGB_COLORS);
            if (copiedLines == 0)
                throw new InvalidOperationException("GetDIBits failed to copy scanlines.");

            return new CaptureResult(bgra, left, top, width, height);
        }
        catch (Exception)
        {
            // On abnormal abort: immediately purge the leftover raw desktop pixels before rethrowing
            if (bgra.Length > 0) CryptographicOperations.ZeroMemory(bgra.AsSpan());
            throw;
        }
        finally
        {
            if (hOldBitmap != nint.Zero) SelectObject(hdcMem, hOldBitmap);
            if (hBitmap    != nint.Zero) DeleteObject(hBitmap);
            if (hdcMem     != nint.Zero) DeleteDC(hdcMem);
            if (hdcScreen  != nint.Zero) ReleaseDC(nint.Zero, hdcScreen);
        }
    }

    // ─── P/Invoke ─────────────────────────────────────────────────────────────

    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY           = 0x00CC0020;
    private const uint BI_RGB            = 0;
    private const uint DIB_RGB_COLORS    = 0;

    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")]  private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")]  private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);
    [DllImport("gdi32.dll")]  private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")]  private static extern bool DeleteObject(nint ho);
    [DllImport("gdi32.dll")]  private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy,
        nint hdcSrc, int x1, int y1, uint rop);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint cLines,
        byte[] lpvBits, ref BITMAPINFO lpbmi, uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int  biSize;
        public int  biWidth;
        public int  biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int  biXPelsPerMeter;
        public int  biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public uint[] bmiColors;
    }
}
