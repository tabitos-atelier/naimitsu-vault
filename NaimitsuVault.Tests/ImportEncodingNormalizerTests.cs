// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using NaimitsuVault.Helpers;

namespace NaimitsuVault.Tests;

/// <summary>
/// Unit tests for ImportEncodingNormalizer.NormalizeToUtf8.
/// Covers every encoding branch (UTF-8 BOM / UTF-32 LE/BE / UTF-16 LE / UTF-16 BE / ANSI / plain UTF-8).
/// </summary>
public sealed class ImportEncodingNormalizerTests
{
    // ── Plain UTF-8 (no BOM) ──────────────────────────────────────────────────

    [Fact]
    public void NormalizeToUtf8_PlainUtf8_ReturnsSameBytes()
    {
        var input = Encoding.UTF8.GetBytes("{\"key\":\"値\"}");
        var result = ImportEncodingNormalizer.NormalizeToUtf8(input);
        Assert.Equal(input, result.ToArray());
    }

    [Fact]
    public void NormalizeToUtf8_AsciiOnly_ReturnsSameBytes()
    {
        var input = Encoding.UTF8.GetBytes("hello,world\n");
        var result = ImportEncodingNormalizer.NormalizeToUtf8(input);
        Assert.Equal(input, result.ToArray());
    }

    // ── UTF-8 BOM stripping ────────────────────────────────────────────────────

    [Fact]
    public void NormalizeToUtf8_Utf8Bom_StripsAndReturnsContent()
    {
        var content = Encoding.UTF8.GetBytes("{\"key\":\"val\"}");
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. content];

        var result = ImportEncodingNormalizer.NormalizeToUtf8(withBom);

        // The BOM is removed, leaving only the content
        Assert.Equal(content, result.ToArray());
    }

    [Fact]
    public void NormalizeToUtf8_Utf8BomOnly_ReturnsEmpty()
    {
        byte[] bomOnly = [0xEF, 0xBB, 0xBF];
        var result = ImportEncodingNormalizer.NormalizeToUtf8(bomOnly);
        Assert.Empty(result.ToArray());
    }

    // ── UTF-16 LE rescue ──────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeToUtf8_Utf16Le_TranspilesToUtf8()
    {
        const string text = "hello,world";
        // Generate a byte sequence with a UTF-16 LE BOM
        var utf16Bytes = Encoding.Unicode.GetBytes(text);
        byte[] withBom = [0xFF, 0xFE, .. utf16Bytes];

        var result = ImportEncodingNormalizer.NormalizeToUtf8(withBom);

        // The same text must be output as UTF-8
        var decoded = Encoding.UTF8.GetString(result.ToArray());
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void NormalizeToUtf8_Utf16Le_WithJapanese_TranspilesToUtf8()
    {
        const string text = "タイトル,パスワード";
        var utf16Bytes = Encoding.Unicode.GetBytes(text);
        byte[] withBom = [0xFF, 0xFE, .. utf16Bytes];

        var result = ImportEncodingNormalizer.NormalizeToUtf8(withBom);

        var decoded = Encoding.UTF8.GetString(result.ToArray());
        Assert.Equal(text, decoded);
    }

    // ── UTF-16 BE rescue ──────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeToUtf8_Utf16Be_TranspilesToUtf8()
    {
        const string text = "test";
        var utf16Bytes = Encoding.BigEndianUnicode.GetBytes(text);
        byte[] withBom = [0xFE, 0xFF, .. utf16Bytes];

        var result = ImportEncodingNormalizer.NormalizeToUtf8(withBom);

        var decoded = Encoding.UTF8.GetString(result.ToArray());
        Assert.Equal(text, decoded);
    }

    // ── Reject UTF-32 (its LE BOM shares its first 2 bytes with UTF-16 LE) ────────

    [Fact]
    public void NormalizeToUtf8_Utf32Le_ThrowsDecoderFallbackException()
    {
        // Without the guard, this would be mis-converted as a UTF-16 LE file
        byte[] withBom = [0xFF, 0xFE, 0x00, 0x00, .. Encoding.UTF32.GetBytes("test")];

        Assert.Throws<DecoderFallbackException>(
            () => ImportEncodingNormalizer.NormalizeToUtf8(withBom));
    }

    [Fact]
    public void NormalizeToUtf8_Utf32Be_ThrowsDecoderFallbackException()
    {
        byte[] withBom = [0x00, 0x00, 0xFE, 0xFF, .. new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetBytes("test")];

        Assert.Throws<DecoderFallbackException>(
            () => ImportEncodingNormalizer.NormalizeToUtf8(withBom));
    }

    // ── Reject all ANSI ─────────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeToUtf8_AnsiWindows1252_ThrowsDecoderFallbackException()
    {
        // Windows-1252's "é" (0xE9) is not a valid UTF-8 sequence
        byte[] ansi = [0x74, 0x65, 0x73, 0x74, 0xE9]; // "testÃ©" in ANSI

        Assert.Throws<DecoderFallbackException>(
            () => ImportEncodingNormalizer.NormalizeToUtf8(ansi));
    }

    [Fact]
    public void NormalizeToUtf8_ShiftJis_ThrowsDecoderFallbackException()
    {
        // CP932 (Shift_JIS)'s "あ" (0x82 0xA0) is not valid UTF-8
        byte[] sjis = [0x82, 0xA0];

        Assert.Throws<DecoderFallbackException>(
            () => ImportEncodingNormalizer.NormalizeToUtf8(sjis));
    }

    [Fact]
    public void NormalizeToUtf8_InvalidUtf8Continuation_ThrowsDecoderFallbackException()
    {
        // An invalid continuation byte (0x80 appears standalone)
        byte[] invalid = [0x41, 0x80, 0x42]; // A<invalid>B

        Assert.Throws<DecoderFallbackException>(
            () => ImportEncodingNormalizer.NormalizeToUtf8(invalid));
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeToUtf8_EmptyArray_ReturnsEmpty()
    {
        // An empty array is valid UTF-8 (Utf8.IsValid(empty) = true)
        var result = ImportEncodingNormalizer.NormalizeToUtf8([]);
        Assert.Empty(result.ToArray());
    }

    [Fact]
    public void NormalizeToUtf8_SingleNullByte_ReturnsAsIs()
    {
        // A standalone NUL byte is valid UTF-8
        byte[] nul = [0x00];
        var result = ImportEncodingNormalizer.NormalizeToUtf8(nul);
        Assert.Equal(nul, result.ToArray());
    }

    [Fact]
    public void NormalizeToUtf8_Utf16LeResultIsValidUtf8()
    {
        // The byte sequence after UTF-16 rescue must actually be valid UTF-8
        byte[] withBom = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("テスト")];
        var result = ImportEncodingNormalizer.NormalizeToUtf8(withBom);
        Assert.True(System.Text.Unicode.Utf8.IsValid(result.Span));
    }
}
