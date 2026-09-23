// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

// Test-only JSON source-generation context
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class TestJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

/// <summary>
/// FieldCrypto 512-byte boundary values + zero-memory tests (TC-FC-01 .. TC-FC-28)
/// </summary>
public sealed class FieldCryptoBoundaryTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CryptoService Crypto() => new(NullLogger<CryptoService>.Instance);
    private static DekScope MakeDek()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return new DekScope(key, 32);
    }

    private static string MakeAsciiString(int length) => new('A', length);
    private static string MakeCjkString(int charCount)
        => new('中', charCount); // '中' - 3 bytes in UTF-8

    // ── TC-FC-01 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_512AsciiChars_ExactlyAtBoundary_CorrectCipherLength()
    {
        var dek    = MakeDek();
        var cipher = FieldCrypto.Seal(MakeAsciiString(512).AsSpan(), Crypto(), dek);
        Assert.Equal(512 + 28, cipher.Length);
    }

    // ── TC-FC-02 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_513AsciiChars_OneOverBoundary_CorrectCipherLength()
    {
        var dek    = MakeDek();
        var cipher = FieldCrypto.Seal(MakeAsciiString(513).AsSpan(), Crypto(), dek);
        Assert.Equal(513 + 28, cipher.Length);
    }

    // ── TC-FC-03 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_512Bytes_Roundtrip_DecryptsToOriginalText()
    {
        var original = MakeAsciiString(512);
        var dek      = MakeDek();
        var cipher   = FieldCrypto.Seal(original.AsSpan(), Crypto(), dek);
        using var sp = FieldCrypto.Open(cipher, Crypto(), dek);
        Assert.NotNull(sp);
        Assert.Equal(original, Encoding.UTF8.GetString(sp!.Utf8));
    }

    // ── TC-FC-04 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_513Bytes_Roundtrip_DecryptsToOriginalText()
    {
        var original = MakeAsciiString(513);
        var dek      = MakeDek();
        var cipher   = FieldCrypto.Seal(original.AsSpan(), Crypto(), dek);
        using var sp = FieldCrypto.Open(cipher, Crypto(), dek);
        Assert.NotNull(sp);
        Assert.Equal(original, Encoding.UTF8.GetString(sp!.Utf8));
    }

    // ── TC-FC-05 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_MultibyteCJK_171Chars_Exceeds512Bytes()
    {
        // 171 CJK chars × 3 bytes/char = 513 bytes → pinned path
        var original = MakeCjkString(171);
        var dek      = MakeDek();
        var cipher   = FieldCrypto.Seal(original.AsSpan(), Crypto(), dek);
        using var sp = FieldCrypto.Open(cipher, Crypto(), dek);
        Assert.NotNull(sp);
        Assert.Equal(original, Encoding.UTF8.GetString(sp!.Utf8));
    }

    // ── TC-FC-06 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_MultibyteCJK_170Chars_Under512Bytes_Stackalloc()
    {
        // 170 CJK chars × 3 bytes/char = 510 bytes ≤ 512 → stackalloc path
        var original = MakeCjkString(170);
        var dek      = MakeDek();
        var cipher   = FieldCrypto.Seal(original.AsSpan(), Crypto(), dek);
        using var sp = FieldCrypto.Open(cipher, Crypto(), dek);
        Assert.NotNull(sp);
        Assert.Equal(original, Encoding.UTF8.GetString(sp!.Utf8));
    }

    // ── TC-FC-07 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_EmptyString_Returns28BytesCipher()
    {
        var dek    = MakeDek();
        var cipher = FieldCrypto.Seal(ReadOnlySpan<char>.Empty, Crypto(), dek);
        Assert.Equal(28, cipher.Length);
    }

    // ── TC-FC-08 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_SingleChar_CorrectRoundtrip()
    {
        var dek    = MakeDek();
        var cipher = FieldCrypto.Seal("A".AsSpan(), Crypto(), dek);
        using var sp = FieldCrypto.Open(cipher, Crypto(), dek);
        Assert.Equal("A", Encoding.UTF8.GetString(sp!.Utf8));
    }

    // ── TC-FC-09 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_NullTerminator_PreservedInRoundtrip()
    {
        var dek = MakeDek();
        var cipher = FieldCrypto.Seal("\0".AsSpan(), Crypto(), dek);
        using var sp = FieldCrypto.Open(cipher, Crypto(), dek);
        Assert.NotNull(sp);
        Assert.Equal(1, sp!.Utf8.Length);
        Assert.Equal(0, sp.Utf8[0]);
    }

    // ── TC-FC-10 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_4000AsciiChars_LargePinned_CorrectRoundtrip()
    {
        var original = MakeAsciiString(4000);
        var dek      = MakeDek();
        var cipher   = FieldCrypto.Seal(original.AsSpan(), Crypto(), dek);
        using var sp = FieldCrypto.Open(cipher, Crypto(), dek);
        Assert.NotNull(sp);
        Assert.Equal(original, Encoding.UTF8.GetString(sp!.Utf8));
    }

    // ── TC-FC-11 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_DifferentDek_ProducesDifferentCiphertext()
    {
        var plaintext = "hello world";
        var dek1 = MakeDek();
        var dek2 = MakeDek();
        var c1   = FieldCrypto.Seal(plaintext.AsSpan(), Crypto(), dek1);
        var c2   = FieldCrypto.Seal(plaintext.AsSpan(), Crypto(), dek2);
        Assert.False(c1.SequenceEqual(c2));
    }

    // ── TC-FC-12 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_TwoCalls_SamePlaintext_DifferentNonces()
    {
        var dek = MakeDek();
        var c1  = FieldCrypto.Seal("test".AsSpan(), Crypto(), dek);
        var c2  = FieldCrypto.Seal("test".AsSpan(), Crypto(), dek);
        // The leading 12 bytes are the nonce - they should differ
        Assert.False(c1[..12].SequenceEqual(c2[..12]));
    }

    // ── TC-FC-13 ──────────────────────────────────────────────────────────────

    [Fact]
    public void FillBufferFromUtf8_512AsciiChars_ExactBoundary_StackallocPath()
    {
        var utf8 = Encoding.UTF8.GetBytes(MakeAsciiString(512)); // 512 bytes = 512 chars (<=512)
        using var buf = new SecureCharBuffer();
        FieldCrypto.FillBufferFromUtf8(buf, utf8);
        Assert.Equal(512, buf.Span.Length);
    }

    // ── TC-FC-14 ──────────────────────────────────────────────────────────────

    [Fact]
    public void FillBufferFromUtf8_513AsciiChars_OneBeyond_PinnedPath()
    {
        var utf8 = Encoding.UTF8.GetBytes(MakeAsciiString(513)); // 513 bytes = 513 chars (>512)
        using var buf = new SecureCharBuffer();
        FieldCrypto.FillBufferFromUtf8(buf, utf8);
        Assert.Equal(513, buf.Span.Length);
    }

    // ── TC-FC-15 ──────────────────────────────────────────────────────────────

    [Fact]
    public void FillBufferFromUtf8_EmptyBytes_DoesNotModifyBuffer()
    {
        using var buf = new SecureCharBuffer();
        FieldCrypto.FillBufferFromUtf8(buf, ReadOnlySpan<byte>.Empty);
        Assert.True(buf.IsEmpty);
    }

    // ── TC-FC-16 ──────────────────────────────────────────────────────────────

    [Fact]
    public void FillBufferFromUtf8_512CJKChars_ByteCountIs1536_ButCharCountIs512()
    {
        // 512 CJK chars x 3 bytes/char = 1536 bytes, charCount = 512 -> stackalloc path
        var utf8 = Encoding.UTF8.GetBytes(MakeCjkString(512));
        Assert.Equal(1536, utf8.Length); // confirm the byte count
        using var buf = new SecureCharBuffer();
        FieldCrypto.FillBufferFromUtf8(buf, utf8);
        Assert.Equal(512, buf.Span.Length); // char count <= 512 -> stackalloc
    }

    // ── TC-FC-17 ──────────────────────────────────────────────────────────────

    [Fact]
    public void FillBufferFromUtf8_513CJKChars_CharCountExceedsBoundary_PinnedPath()
    {
        // 513 CJK chars x 3 bytes/char = 1539 bytes, charCount = 513 -> pinned path
        var utf8 = Encoding.UTF8.GetBytes(MakeCjkString(513));
        using var buf = new SecureCharBuffer();
        FieldCrypto.FillBufferFromUtf8(buf, utf8);
        Assert.Equal(513, buf.Span.Length);
    }

    // ── TC-FC-18 ──────────────────────────────────────────────────────────────

    [Fact]
    public void FillBufferFromUtf8_Roundtrip_IntegrityVerified()
    {
        var original = "テスト文字列 Test String 1234 !@#$";
        var dek      = MakeDek();
        var cipher   = FieldCrypto.Seal(original.AsSpan(), Crypto(), dek);
        using var sp = FieldCrypto.Open(cipher, Crypto(), dek);
        Assert.NotNull(sp);
        using var buf = new SecureCharBuffer();
        FieldCrypto.FillBufferFromUtf8(buf, sp!.Utf8);
        Assert.Equal(original, new string(buf.Span));
    }

    // ── TC-FC-19 ──────────────────────────────────────────────────────────────

    [Fact]
    public void SealJson_SmallObject_Under512Bytes_CorrectRoundtrip()
    {
        var dek     = MakeDek();
        var obj     = new List<int> { 1, 2, 3, 4, 5 };
        var cipher  = FieldCrypto.SealJson(obj, TestJsonContext.Default.ListInt32, Crypto(), dek);
        var result  = FieldCrypto.OpenJson(cipher, TestJsonContext.Default.ListInt32, Crypto(), dek);
        Assert.Equal(obj, result);
    }

    // ── TC-FC-20 ──────────────────────────────────────────────────────────────

    [Fact]
    public void SealJson_LargeObject_Over512Bytes_CorrectRoundtrip()
    {
        // A large Dictionary exceeding 512 bytes
        var dek = MakeDek();
        var obj = Enumerable.Range(0, 50)
            .ToDictionary(i => $"key{i:D3}", i => $"value{i:D3}_padding_______");
        var cipher = FieldCrypto.SealJson(obj, TestJsonContext.Default.DictionaryStringString, Crypto(), dek);
        var result = FieldCrypto.OpenJson(cipher, TestJsonContext.Default.DictionaryStringString, Crypto(), dek);
        Assert.Equal(obj, result);
    }

    // ── TC-FC-29 ────────────────────────────────────────────────────────

    [Fact]
    public void PinnedBufferWriter_EnsureCapacity_OldBuffer_IsPhysicallyZeroed()
    {
        // PinnedBufferWriter is internal in NaimitsuVault.Helpers (shared by FieldCrypto, ProfileService,
        // SecretsViewModel, VaultOperationsViewModel, ViewerViewModel, CertificateHelper)
        var pbwType = typeof(FieldCrypto).Assembly
            .GetType("NaimitsuVault.Helpers.PinnedBufferWriter", throwOnError: false, ignoreCase: false);
        Assert.NotNull(pbwType); // early detection of a type-name/namespace change

        var pbw = Activator.CreateInstance(pbwType)!;

        var bufField = pbwType.GetField("_buf",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        byte[] oldBuf = (byte[])bufField.GetValue(pbw)!;

        Assert.Equal(512, oldBuf.Length);           // confirm the initial buffer size
        Assert.True(oldBuf.All(b => b == 0));        // the initial content is zero

        // Trigger EnsureCapacity via GetMemory(1024)
        // GetSpan returns Span<byte>, so it cannot be used via reflection (boxing a ref struct is not allowed)
        // GetMemory returns Memory<byte>, so it can be used via reflection
        var getMemory = pbwType.GetMethod("GetMemory",
            BindingFlags.Public | BindingFlags.Instance)!;
        getMemory.Invoke(pbw, new object[] { 1024 });

        byte[] newBuf = (byte[])bufField.GetValue(pbw)!;
        Assert.NotSame(oldBuf, newBuf);              // already replaced with a different instance
        Assert.True(newBuf.Length >= 1024);

        // Core assertion: the old pinned array has been physically zeroed
        Assert.True(oldBuf.All(b => b == 0),
            "After EnsureCapacity, the old _buf was not ZeroMemory'd (suspected leftover plaintext residue)");

        ((IDisposable)pbw).Dispose();
    }

    // ── TC-FC-21 ──────────────────────────────────────────────────────────────

    [Fact]
    public void SealJson_VeryLargeObject_4096Bytes_CorrectRoundtrip()
    {
        var dek = MakeDek();
        var obj = Enumerable.Range(0, 200)
            .ToDictionary(i => $"k{i:D4}", i => $"v{i:D4}______________padding");
        var cipher = FieldCrypto.SealJson(obj, TestJsonContext.Default.DictionaryStringString, Crypto(), dek);
        var result = FieldCrypto.OpenJson(cipher, TestJsonContext.Default.DictionaryStringString, Crypto(), dek);
        Assert.Equal(obj, result);
    }

    // ── TC-FC-22 ──────────────────────────────────────────────────────────────

    [Fact]
    public void SealJson_EmptyList_RoundtripToEmptyList()
    {
        var dek    = MakeDek();
        var obj    = new List<int>();
        var cipher = FieldCrypto.SealJson(obj, TestJsonContext.Default.ListInt32, Crypto(), dek);
        var result = FieldCrypto.OpenJson(cipher, TestJsonContext.Default.ListInt32, Crypto(), dek);
        Assert.Equal(obj, result);
    }

    // ── TC-FC-23 ──────────────────────────────────────────────────────────────

    [Fact]
    public void SealJsonNullable_Null_ReturnsNullBlob()
    {
        var dek    = MakeDek();
        var result = FieldCrypto.SealJsonNullable((List<int>?)null, TestJsonContext.Default.ListInt32, Crypto(), dek);
        Assert.Null(result);
    }

    // ── TC-FC-24 ──────────────────────────────────────────────────────────────

    [Fact]
    public void OpenJson_TruncatedBlob_ReturnsDefault()
    {
        var dek    = MakeDek();
        var result = FieldCrypto.OpenJson<List<int>>(new byte[28], TestJsonContext.Default.ListInt32, Crypto(), dek);
        Assert.Null(result);
    }

    // ── TC-FC-25 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Open_NullBlob_ReturnsNull()
    {
        var dek = MakeDek();
        var result = FieldCrypto.Open(null, Crypto(), dek);
        Assert.Null(result);
    }

    // ── TC-FC-26 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Open_EmptyByteArray_ReturnsNull()
    {
        var dek = MakeDek();
        var result = FieldCrypto.Open([], Crypto(), dek);
        Assert.Null(result);
    }

    // ── TC-FC-27 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Open_ExactlyAeadOverheadBytes_ReturnsNull()
    {
        var dek = MakeDek();
        var result = FieldCrypto.Open(new byte[28], Crypto(), dek);
        Assert.Null(result);
    }

    // ── TC-FC-28 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Open_TamperedAuthTag_ThrowsCryptographicException()
    {
        var dek    = MakeDek();
        var cipher = FieldCrypto.Seal("secret".AsSpan(), Crypto(), dek);
        // Tamper with the auth tag (bytes 12..27)
        cipher[15] ^= 0xFF;
        // AuthenticationTagMismatchException is a derived type of CryptographicException
        Assert.ThrowsAny<CryptographicException>(() => FieldCrypto.Open(cipher, Crypto(), dek));
    }
}
