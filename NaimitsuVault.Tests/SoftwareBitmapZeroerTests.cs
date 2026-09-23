// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices.WindowsRuntime;
using NaimitsuVault.Helpers;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Physical zeroing of SoftwareBitmap pixels" TC-BMZ-01–08.
/// TC-BMZ-05–08 pin the observable output of the <see cref="ImageHelper"/> avatar pipeline (dimensions,
/// EXIF orientation, crop position) so that switching its encoder input from SoftwareBitmap to
/// SetPixelData(byte[]) can be shown not to change what the user sees.
/// </summary>
public sealed class SoftwareBitmapZeroerTests
{
    // ── Test image helpers ─────────────────────────────────────────────────────

    /// <summary>Left half red, right half blue (BGRA, opaque).</summary>
    private static byte[] RedBluePixels(int width, int height)
    {
        var px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (x < width / 2) { px[i] = 0;   px[i + 1] = 0; px[i + 2] = 255; } // red
                else               { px[i] = 255; px[i + 1] = 0; px[i + 2] = 0;   } // blue
                px[i + 3] = 255;
            }
        }
        return px;
    }

    private static async Task<byte[]> EncodeJpegAsync(int width, int height, byte[] bgra, ushort? exifOrientation = null)
    {
        using var ms = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        if (exifOrientation.HasValue)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                { "System.Photo.Orientation", new BitmapTypedValue(exifOrientation.Value, PropertyType.UInt16) }
            });
        }
        await encoder.FlushAsync();

        ms.Seek(0);
        var reader = new DataReader(ms);
        await reader.LoadAsync((uint)ms.Size);
        var bytes = new byte[ms.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<BitmapDecoder> DecodeAsync(byte[] jpeg)
    {
        var ms = new InMemoryRandomAccessStream();
        var writer = new DataWriter(ms);
        writer.WriteBytes(jpeg);
        await writer.StoreAsync();
        ms.Seek(0);
        return await BitmapDecoder.CreateAsync(ms);
    }

    /// <summary>Returns (B, G, R) of the pixel at (x, y) in the EXIF-oriented decode.</summary>
    private static async Task<(byte b, byte g, byte r)> PixelAtAsync(BitmapDecoder decoder, int x, int y)
    {
        var provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var data = provider.DetachPixelData();
        int i = (y * (int)decoder.OrientedPixelWidth + x) * 4;
        return (data[i], data[i + 1], data[i + 2]);
    }

    private static void AssertRedish((byte b, byte g, byte r) p)
        => Assert.True(p.r > 200 && p.b < 60, $"expected red-ish, got BGR=({p.b},{p.g},{p.r})");

    private static void AssertBluish((byte b, byte g, byte r) p)
        => Assert.True(p.b > 200 && p.r < 60, $"expected blue-ish, got BGR=({p.b},{p.g},{p.r})");

    /// <summary>A 64x64 Bgra8 bitmap whose every byte is 0xAB.</summary>
    private static SoftwareBitmap FilledBitmap()
    {
        var writer = new DataWriter();
        writer.WriteBytes(Enumerable.Repeat((byte)0xAB, 64 * 64 * 4).ToArray());
        return SoftwareBitmap.CreateCopyFromBuffer(
            writer.DetachBuffer(), BitmapPixelFormat.Bgra8, 64, 64, BitmapAlphaMode.Premultiplied);
    }

    private static int NonZeroByteCount(SoftwareBitmap bitmap)
    {
        var bytes = new byte[64 * 64 * 4];
        bitmap.CopyToBuffer(bytes.AsBuffer());
        return bytes.Count(b => b != 0);
    }

    // ── TC-BMZ-01 ──────────────────────────────────────────────────────────────

    /// <summary>TC-BMZ-01: TryZero returns true and every pixel byte of a writable bitmap reads back as 0.</summary>
    [Fact]
    public void TryZero_WritableBitmap_ZeroesEveryByte()
    {
        using var bitmap = FilledBitmap();
        Assert.Equal(64 * 64 * 4, NonZeroByteCount(bitmap));

        bool ok = SoftwareBitmapZeroer.TryZero(bitmap);

        Assert.True(ok);
        Assert.Equal(0, NonZeroByteCount(bitmap));
    }

    // ── TC-BMZ-02 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-BMZ-02: Repeating the zeroing 200 times always leaves 0 non-zero bytes. Guards against a zero source
    /// built from an uninitialized buffer (new Windows.Storage.Streams.Buffer(capacity) is NOT zero-filled;
    /// measured leftover garbage in ~every allocation), which would randomly leave non-zero bytes.
    /// </summary>
    [Fact]
    public void TryZero_Repeated_AlwaysLeavesNoNonZeroBytes()
    {
        for (int i = 0; i < 200; i++)
        {
            using var bitmap = FilledBitmap();
            Assert.True(SoftwareBitmapZeroer.TryZero(bitmap));
            Assert.Equal(0, NonZeroByteCount(bitmap));
        }
    }

    // ── TC-BMZ-03 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-BMZ-03: A bitmap already handed to a BitmapEncoder rejects all writes (even after FlushAsync), so
    /// TryZero returns false without throwing. Records why the avatar pipeline must not feed encoders with a
    /// SoftwareBitmap (it uses SetPixelData(byte[]) and zeroes that managed array instead).
    /// </summary>
    [Fact]
    public async Task TryZero_BitmapHandedToEncoder_ReturnsFalseWithoutThrowing()
    {
        using var bitmap = FilledBitmap();
        using (var ms = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
        }

        bool ok = SoftwareBitmapZeroer.TryZero(bitmap);

        Assert.False(ok);
        Assert.Equal(64 * 64 * 4, NonZeroByteCount(bitmap)); // still intact: nothing could be written
    }

    // ── TC-BMZ-04 ──────────────────────────────────────────────────────────────

    /// <summary>TC-BMZ-04: A non-Bgra8 bitmap is left untouched: TryZero returns false without throwing.</summary>
    [Fact]
    public void TryZero_NonBgra8Bitmap_ReturnsFalseWithoutThrowing()
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Gray8, 8, 8, BitmapAlphaMode.Ignore);

        bool ok = SoftwareBitmapZeroer.TryZero(bitmap);

        Assert.False(ok);
    }

    // ── TC-BMZ-05 ──────────────────────────────────────────────────────────────

    /// <summary>TC-BMZ-05: NormalizeExifAsync on an un-rotated JPEG returns a JPEG of the same dimensions.</summary>
    [Fact]
    public async Task NormalizeExif_NoRotation_KeepsDimensions()
    {
        var jpeg = await EncodeJpegAsync(64, 32, RedBluePixels(64, 32));

        var normalized = await ImageHelper.NormalizeExifAsync(jpeg);

        var decoder = await DecodeAsync(normalized);
        Assert.Equal(64u, decoder.PixelWidth);
        Assert.Equal(32u, decoder.PixelHeight);
        Assert.Equal(decoder.PixelWidth, decoder.OrientedPixelWidth);
        Assert.Equal(decoder.PixelHeight, decoder.OrientedPixelHeight);
    }

    // ── TC-BMZ-06 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-BMZ-06: NormalizeExifAsync bakes EXIF Orientation=6 (rotate 90° clockwise) into the pixels.
    /// A 64x32 image (left red, right blue) becomes 32x64 with red on top and blue on the bottom,
    /// and no rotation remains in the output (PixelWidth == OrientedPixelWidth).
    /// </summary>
    [Fact]
    public async Task NormalizeExif_Orientation6_BakesRotationIntoPixels()
    {
        var jpeg = await EncodeJpegAsync(64, 32, RedBluePixels(64, 32), exifOrientation: 6);
        var source = await DecodeAsync(jpeg);
        Assert.Equal(32u, source.OrientedPixelWidth);  // the test input really carries the rotation
        Assert.Equal(64u, source.OrientedPixelHeight);

        var normalized = await ImageHelper.NormalizeExifAsync(jpeg);

        var decoder = await DecodeAsync(normalized);
        Assert.Equal(32u, decoder.PixelWidth);
        Assert.Equal(64u, decoder.PixelHeight);
        Assert.Equal(decoder.PixelWidth, decoder.OrientedPixelWidth);
        Assert.Equal(decoder.PixelHeight, decoder.OrientedPixelHeight);
        AssertRedish(await PixelAtAsync(decoder, 16, 8));
        AssertBluish(await PixelAtAsync(decoder, 16, 56));
    }

    // ── TC-BMZ-07 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-BMZ-07: CreateAvatarWithCropAsync cuts the requested square out of the original pixels and
    /// scales it to 256x256. Cropping the right half (blue) of a left-red/right-blue image gives a blue avatar.
    /// </summary>
    [Fact]
    public async Task CreateAvatarWithCrop_RightHalf_Returns256x256Blue()
    {
        var jpeg = await EncodeJpegAsync(64, 32, RedBluePixels(64, 32));

        var avatar = await ImageHelper.CreateAvatarWithCropAsync(jpeg, cropX: 32, cropY: 0, cropSize: 32);

        var decoder = await DecodeAsync(avatar);
        Assert.Equal(256u, decoder.PixelWidth);
        Assert.Equal(256u, decoder.PixelHeight);
        AssertBluish(await PixelAtAsync(decoder, 128, 128));
        AssertBluish(await PixelAtAsync(decoder, 32, 128));
    }

    // ── TC-BMZ-08 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-BMZ-08: CreateAvatarAsync center-crops a non-square image to a square and scales it to 256x256.
    /// The centered 32x32 of a 64x32 left-red/right-blue image is half red, half blue.
    /// </summary>
    [Fact]
    public async Task CreateAvatar_NonSquare_CenterCropsTo256x256()
    {
        var jpeg = await EncodeJpegAsync(64, 32, RedBluePixels(64, 32));

        var avatar = await ImageHelper.CreateAvatarAsync(jpeg);

        var decoder = await DecodeAsync(avatar);
        Assert.Equal(256u, decoder.PixelWidth);
        Assert.Equal(256u, decoder.PixelHeight);
        AssertRedish(await PixelAtAsync(decoder, 32, 128));
        AssertBluish(await PixelAtAsync(decoder, 224, 128));
    }
}
