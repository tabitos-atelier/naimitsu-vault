// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;

namespace NaimitsuVault.Helpers;

/// <summary>
/// Physically overwrites the unmanaged pixel buffer of a <see cref="SoftwareBitmap"/> with zeros.
/// </summary>
/// <remarks>
/// The previous approach (LockBuffer -> IMemoryBufferByteAccess COM cast) never worked in this app's runtime:
/// the cast always throws InvalidCastException. This uses <see cref="SoftwareBitmap.CopyFromBuffer"/> with an
/// explicitly zero-filled buffer instead.
/// </remarks>
internal static class SoftwareBitmapZeroer
{
    private static readonly ILogger Logger = AppLog.For(typeof(SoftwareBitmapZeroer));

    /// <summary>
    /// Overwrites the bitmap's pixels with zeros. Returns false (never throws) if it could not be zeroed;
    /// the caller should carry on with its own cleanup (Dispose etc.) either way.
    /// </summary>
    /// <remarks>
    /// Only works on a bitmap that has NOT been handed to a <see cref="BitmapEncoder"/>: after
    /// SetSoftwareBitmap the bitmap rejects every write (UnauthorizedAccessException), even after FlushAsync.
    /// Callers must feed encoders with SetPixelData(byte[]) and zero that managed array instead.
    /// Also call it BEFORE disposing the SoftwareBitmapSource the bitmap was given to: disposing the source
    /// closes the bitmap too, after which it reads Format=Unknown / Size=0x0 and can no longer be zeroed.
    /// Only Bgra8 is supported (the only format this app uses).
    /// </remarks>
    public static bool TryZero(SoftwareBitmap bitmap)
    {
        try
        {
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                Logger.LogWarning("SoftwareBitmapZeroer: Unsupported pixel format; pixels were not zeroed. [{Format}]", bitmap.BitmapPixelFormat);
                return false;
            }

            // The zero source MUST be a managed array wrapped with AsBuffer(): a managed array is guaranteed to be
            // zero-initialized. Do NOT use new Windows.Storage.Streams.Buffer(capacity) - its contents are NOT
            // zeroed (measured: leftover heap garbage), so "zeroing" with it would write garbage into the bitmap.
            var zeros = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            bitmap.CopyFromBuffer(zeros.AsBuffer());
            return true;
        }
        catch (Exception ex)
        {
            // Not propagated (zeroing is best-effort cleanup), but always recorded. The exception object itself is not logged.
            Logger.LogWarning("SoftwareBitmapZeroer: Failed to zero the pixel buffer. [{ExType}]", ex.GetType().Name);
            return false;
        }
    }
}
