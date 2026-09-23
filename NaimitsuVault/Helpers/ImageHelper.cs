// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using System.Security.Cryptography;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NaimitsuVault.Helpers;

public static class ImageHelper
{
    /// <summary>
    /// Generates a thumbnail for a general (non-avatar) image.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for zeroing the originalData argument's memory (this method does not call ZeroMemory).
    /// </remarks>
    public static async Task<byte[]> CreateThumbnailAsync(byte[] originalData, uint maxWidth = 200, bool isPdf = false)
    {
        if (originalData == null || originalData.Length == 0)
            throw new ArgumentException("Image data is empty.", nameof(originalData));

        if (isPdf)
            return await CreatePdfThumbnailAsync(originalData, maxWidth);

        try
        {
            using var ms = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(ms);
            writer.WriteBytes(originalData);
            await writer.StoreAsync();
            ms.Seek(0);
            return await ResizeToJpegAsync(ms, maxWidth);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to generate the thumbnail image.", ex);
        }
    }

    private static async Task<byte[]> CreatePdfThumbnailAsync(byte[] pdfData, uint maxWidth)
    {
        try
        {
            using var ms = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(ms);
            writer.WriteBytes(pdfData);
            await writer.StoreAsync();
            ms.Seek(0);

            var pdfDoc = await PdfDocument.LoadFromStreamAsync(ms);
            if (pdfDoc.PageCount == 0) throw new InvalidOperationException("The PDF has no pages.");

            using var page = pdfDoc.GetPage(0);
            using var rendered = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions { DestinationWidth = maxWidth });
            rendered.Seek(0);
            return await ReadStreamAsync(rendered);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to generate the PDF thumbnail.", ex);
        }
    }

    /// <summary>
    /// Applies EXIF orientation (rotation and/or flip) to the pixels and returns a JPEG with it removed.
    /// Always re-encodes and returns a new array, even when no correction was needed.
    /// Used to align the crop control's display coordinates with the decoder's pixel coordinates.
    /// </summary>
    /// <remarks>
    /// The return value is always a new array dedicated to the caller (does not share a reference with
    /// the <paramref name="data"/> argument). The caller can simply ZeroMemory the return value without a ReferenceEquals check.
    /// </remarks>
    public static async Task<byte[]> NormalizeExifAsync(byte[] data)
    {
        if (data == null || data.Length == 0) return data?.ToArray() ?? [];

        using var ms = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(ms);
        writer.WriteBytes(data);
        await writer.StoreAsync();
        ms.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ms);

        // Always re-encode with EXIF orientation applied to the pixels. A width/height comparison
        // (decoder.PixelWidth/Height vs. OrientedPixelWidth/Height) cannot be used to skip this: EXIF
        // orientations 2 (horizontal flip), 3 (180° rotation), and 4 (vertical flip) all leave the pixel
        // dimensions unchanged, so that comparison alone would wrongly treat them as already normalized
        // and skip the correction, leaving the image flipped/upside-down.
        // Full-size decode with EXIF rotation applied to the pixels (no Bounds → avoids E_INVALIDARG).
        // The result has OrientedPixelWidth × OrientedPixelHeight pixels.
        var pixelProvider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var pixels = pixelProvider.DetachPixelData();

        try
        {
            // SetPixelData(byte[]) instead of SetSoftwareBitmap: a SoftwareBitmap handed to an encoder rejects
            // every write afterwards, so its unmanaged pixel copy could never be zeroed. The managed array can.
            using var outStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                decoder.OrientedPixelWidth, decoder.OrientedPixelHeight, decoder.DpiX, decoder.DpiY, pixels);
            await encoder.FlushAsync();
            return await ReadStreamAsync(outStream);
        }
        finally
        {
            // Zero only after FlushAsync: the encoder may still read the array until encoding completes.
            CryptographicOperations.ZeroMemory(pixels.AsSpan());
        }
    }

    /// <summary>
    /// Crops to a square using the user-specified crop rectangle (in original image pixel space),
    /// resizes to the given size, and returns a JPEG.
    /// BitmapTransform.Bounds causes E_INVALIDARG / WINCODEC_ERR_UNSUPPORTEDOPERATION with certain
    /// codecs for both GetSoftwareBitmapAsync and CreateForTranscodingAsync, so full pixels are
    /// fetched via GetPixelDataAsync and cropped manually.
    /// Zeroing <paramref name="originalData"/> is the caller's responsibility.
    /// </summary>
    public static async Task<byte[]> CreateAvatarWithCropAsync(byte[] originalData, int cropX, int cropY, uint cropSize, uint size = 256)
    {
        if (originalData == null || originalData.Length == 0)
            throw new ArgumentException("Image data is empty.", nameof(originalData));

        using var ms = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(ms);
        writer.WriteBytes(originalData);
        await writer.StoreAsync();
        ms.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ms);
        uint imageWidth  = decoder.PixelWidth;
        uint imageHeight = decoder.PixelHeight;

        uint clampedX = (uint)Math.Max(0, cropX);
        uint clampedY = (uint)Math.Max(0, cropY);
        clampedX = Math.Min(clampedX, imageWidth - 1);
        clampedY = Math.Min(clampedY, imageHeight - 1);
        uint clampedWidth  = Math.Min(cropSize, imageWidth - clampedX);
        uint clampedHeight = Math.Min(cropSize, imageHeight - clampedY);
        uint cropSquareSize = Math.Max(1u, Math.Min(clampedWidth, clampedHeight));

        // Fetch pixels at full resolution (no Bounds specified → avoids an API compatibility issue)
        var pixelProvider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var srcPixels = pixelProvider.DetachPixelData();

        // Manual crop (BGRA8 = 4 bytes/pixel) + zero the pixel arrays in try-finally
        byte[]? dstPixels = null;
        try
        {
            uint srcStride = imageWidth * 4;
            dstPixels = new byte[cropSquareSize * cropSquareSize * 4];
            for (uint row = 0; row < cropSquareSize; row++)
            {
                uint srcOffset = (clampedY + row) * srcStride + clampedX * 4;
                System.Buffer.BlockCopy(srcPixels, (int)srcOffset, dstPixels, (int)(row * cropSquareSize * 4), (int)(cropSquareSize * 4));
            }

            // SetPixelData(byte[]) instead of SetSoftwareBitmap: a SoftwareBitmap handed to an encoder rejects
            // every write afterwards, so its unmanaged pixel copy could never be zeroed. Feeding the managed
            // array directly also avoids the two extra unmanaged copies (DataWriter buffer, SoftwareBitmap).
            // Set only ScaledWidth/Height (do not use Bounds)
            using var outStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, cropSquareSize, cropSquareSize, 96, 96, dstPixels);
            encoder.BitmapTransform.ScaledWidth       = size;
            encoder.BitmapTransform.ScaledHeight      = size;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();
            return await ReadStreamAsync(outStream);
        }
        finally
        {
            // Zero only after FlushAsync: the encoder may still read dstPixels until encoding completes.
            CryptographicOperations.ZeroMemory(srcPixels.AsSpan());
            if (dstPixels != null) CryptographicOperations.ZeroMemory(dstPixels.AsSpan());
        }
    }

    /// <summary>
    /// Center-crops an image to a square, downscales to the given size, and returns a JPEG.
    /// For avatar use (to keep it stable under PersonPicture's circular clip).
    /// BitmapTransform.Bounds must not be used, as it causes E_INVALIDARG with certain codecs.
    /// Fetches full pixels via GetPixelDataAsync and crops manually.
    /// Zeroing <paramref name="originalData"/> is the caller's responsibility.
    /// </summary>
    public static async Task<byte[]> CreateAvatarAsync(byte[] originalData, uint size = 256)
    {
        if (originalData == null || originalData.Length == 0)
            throw new ArgumentException("Image data is empty.", nameof(originalData));

        using var ms = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(ms);
        writer.WriteBytes(originalData);
        await writer.StoreAsync();
        ms.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ms);

        // After RespectExifOrientation is applied, the pixel array is OrientedPixelWidth × OrientedPixelHeight
        uint imageWidth  = decoder.OrientedPixelWidth;
        uint imageHeight = decoder.OrientedPixelHeight;
        uint cropSize    = Math.Min(imageWidth, imageHeight);
        uint cropX       = (imageWidth - cropSize) / 2;
        uint cropY       = (imageHeight - cropSize) / 2;

        // Fetch pixels at full resolution (BitmapTransform.Bounds must not be used → avoids E_INVALIDARG)
        var pixelProvider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var srcPixels = pixelProvider.DetachPixelData();

        // Manual center crop (BGRA8 = 4 bytes/pixel) + zero the pixel arrays in try-finally
        byte[]? dstPixels = null;
        try
        {
            uint srcStride = imageWidth * 4;
            dstPixels = new byte[cropSize * cropSize * 4];
            for (uint row = 0; row < cropSize; row++)
            {
                uint srcOffset = (cropY + row) * srcStride + cropX * 4;
                System.Buffer.BlockCopy(srcPixels, (int)srcOffset, dstPixels, (int)(row * cropSize * 4), (int)(cropSize * 4));
            }

            // SetPixelData(byte[]) instead of SetSoftwareBitmap: see CreateAvatarWithCropAsync.
            using var outStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, cropSize, cropSize, 96, 96, dstPixels);
            encoder.BitmapTransform.ScaledWidth       = size;
            encoder.BitmapTransform.ScaledHeight      = size;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();
            return await ReadStreamAsync(outStream);
        }
        finally
        {
            // Zero only after FlushAsync: the encoder may still read dstPixels until encoding completes.
            CryptographicOperations.ZeroMemory(srcPixels.AsSpan());
            if (dstPixels != null) CryptographicOperations.ZeroMemory(dstPixels.AsSpan());
        }
    }

    private static async Task<byte[]> ResizeToJpegAsync(IRandomAccessStream inputStream, uint maxWidth)
    {
        var decoder = await BitmapDecoder.CreateAsync(inputStream);

        uint newWidth = decoder.PixelWidth <= maxWidth ? decoder.PixelWidth : maxWidth;
        uint newHeight = (uint)((double)decoder.PixelHeight * newWidth / decoder.PixelWidth);

        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateForTranscodingAsync(outStream, decoder);
        encoder.BitmapTransform.ScaledWidth = newWidth;
        encoder.BitmapTransform.ScaledHeight = newHeight;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();

        return await ReadStreamAsync(outStream);
    }

    private static async Task<byte[]> ReadStreamAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        reader.DetachStream(); // Detach beforehand so Dispose doesn't close the caller's stream
        return bytes;
    }
}
