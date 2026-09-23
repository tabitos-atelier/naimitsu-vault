// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using NaimitsuVault.Helpers;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Utility for handling encrypted fields (BLOBs) on EF Core entities.
/// All plaintext buffers are allocated on the stack or in pinned arrays, so no plaintext
/// ghosts from GC compaction remain on the heap.
/// </summary>
internal static class FieldCrypto
{
    // The JSON produced here is encrypted immediately after serialization and is never embedded
    // in HTML/JS, so there's no need for the default encoder's \uXXXX-escaping of non-ASCII (CJK,
    // full-width space, etc.) - it only bloats the plaintext-before-encryption and, when this JSON
    // resurfaces verbatim in a plaintext export (VaultImportExportHelper's CSV column), reads as
    // unreadable \uXXXX noise instead of the original text.
    internal static readonly JavaScriptEncoder FullUnicodeEncoder = JavaScriptEncoder.Create(UnicodeRanges.All);
    private static readonly JsonWriterOptions SealJsonWriterOptions = new() { Encoder = FullUnicodeEncoder };

    /// <summary>
    /// UTF-8-encodes a char span and returns it encrypted with AES-256-GCM.
    /// Allocates on the stack for ≤512 bytes; anything larger uses a pinned array to eliminate GC compaction ghosts.
    /// </summary>
    internal static byte[] Seal(ReadOnlySpan<char> plaintext, ICryptoService crypto, DekScope dek)
    {
        int byteCount = Encoding.UTF8.GetByteCount(plaintext);
        if (byteCount <= 512)
        {
            Span<byte> utf8 = stackalloc byte[512];
            Encoding.UTF8.GetBytes(plaintext, utf8);
            try
            {
                return crypto.Encrypt(utf8[..byteCount], dek.Span);
            }
            finally
            {
                // Physically wipe before the stack pointer unwinds. Scope exit alone does not zero it
                CryptographicOperations.ZeroMemory(utf8[..byteCount]);
            }
        }
        var pinnedUtf8 = GC.AllocateArray<byte>(byteCount, pinned: true);
        try
        {
            Encoding.UTF8.GetBytes(plaintext, pinnedUtf8.AsSpan());
            return crypto.Encrypt(pinnedUtf8, dek.Span);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinnedUtf8);
        }
    }

    /// <summary>Encrypts a nullable string. Returns null for null.</summary>
    internal static byte[]? SealNullable(string? plaintext, ICryptoService crypto, DekScope dek)
        => plaintext is null ? null : Seal(plaintext.AsSpan(), crypto, dek);

    /// <summary>Encrypts a nullable char array. Returns null for null.</summary>
    internal static byte[]? SealNullable(char[]? chars, ICryptoService crypto, DekScope dek)
        => chars is null ? null : Seal(chars.AsSpan(), crypto, dek);

    /// <summary>
    /// Decrypts a BLOB with AES-256-GCM and returns a <see cref="SecurePlaintext"/>.
    /// Decrypts directly into a pinned array, so no GC compaction ghost occurs.
    /// Returns null for null/empty. The return value must always be wrapped in using and disposed.
    /// </summary>
    internal static SecurePlaintext? Open(byte[]? blob, ICryptoService crypto, DekScope dek)
    {
        if (blob is null || blob.Length == 0) return null;
        int plaintextLen = blob.Length - ICryptoService.AeadOverhead;
        if (plaintextLen <= 0) return null;
        var sp = SecurePlaintext.Allocate(plaintextLen, out var buf);
        try
        {
            crypto.Decrypt(blob, dek.Span, buf); // span-output: writes directly into SecurePlaintext's pinned array
            return sp;
        }
        catch
        {
            // Catches any failure, not just CryptographicException (e.g. AesGcm's constructor
            // rejecting a malformed key) - a SecurePlaintext left undisposed still gets zeroed
            // eventually by its finalizer, but only this catch-all gives the immediate wipe.
            sp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Converts a UTF-8 byte span to chars and transfers it directly into a SecureCharBuffer.
    /// Allocates on the stack for ≤512 chars; anything larger uses a pinned array to eliminate GC compaction ghosts.
    /// No intermediate string is created.
    /// </summary>
    internal static void FillBufferFromUtf8(SecureCharBuffer buffer, ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return;
        int charCount = Encoding.UTF8.GetCharCount(utf8);
        if (charCount == 0) return;
        if (charCount <= 512)
        {
            Span<char> chars = stackalloc char[charCount];
            Encoding.UTF8.GetChars(utf8, chars);
            try
            {
                buffer.SetFromSpan(chars);
            }
            finally
            {
                // Physically wipe before the stack pointer unwinds. Scope exit alone does not zero it
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars));
            }
        }
        else
        {
            var pinnedChars = GC.AllocateArray<char>(charCount, pinned: true);
            try
            {
                Encoding.UTF8.GetChars(utf8, pinnedChars.AsSpan());
                buffer.SetFromSpan(pinnedChars.AsSpan());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(pinnedChars.AsSpan()));
            }
        }
    }

    /// <summary>
    /// Serializes T to JSON, then encrypts it with AES-256-GCM.
    /// Serializes directly into a pinned <see cref="PinnedBufferWriter"/>, so no JSON plaintext
    /// ghost remains on the heap.
    /// </summary>
    internal static byte[] SealJson<T>(T value, JsonTypeInfo<T> typeInfo, ICryptoService crypto, DekScope dek)
    {
        using var pinnedBuf = new PinnedBufferWriter();
        using (var jsonWriter = new Utf8JsonWriter(pinnedBuf, SealJsonWriterOptions))
        {
            JsonSerializer.Serialize(jsonWriter, value, typeInfo);
        } // Dispose -> Flush: pinnedBuf finalizes with all the data
        return crypto.Encrypt(pinnedBuf.WrittenSpan, dek.Span);
        // The using block makes pinnedBuf.Dispose() zero WrittenSpan
    }

    /// <summary>Serializes nullable T to JSON and encrypts it. Returns null for null.</summary>
    internal static byte[]? SealJsonNullable<T>(T? value, JsonTypeInfo<T> typeInfo, ICryptoService crypto, DekScope dek)
        where T : class
        => value is null ? null : SealJson(value, typeInfo, crypto, dek);

    /// <summary>
    /// Decrypts a BLOB, deserializes it as JSON, and returns T?.
    /// Decrypts directly into a pinned array, so no JSON plaintext ghost remains on the heap.
    /// Returns default for null/empty.
    /// </summary>
    internal static T? OpenJson<T>(byte[]? blob, JsonTypeInfo<T> typeInfo, ICryptoService crypto, DekScope dek)
    {
        if (blob is null || blob.Length == 0) return default;
        int plaintextLen = blob.Length - ICryptoService.AeadOverhead;
        if (plaintextLen <= 0) return default;
        var pinnedJson = GC.AllocateArray<byte>(plaintextLen, pinned: true);
        try
        {
            crypto.Decrypt(blob, dek.Span, pinnedJson); // span-output: writes directly into the pinned buffer
            return JsonSerializer.Deserialize(pinnedJson.AsSpan(), typeInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinnedJson);
        }
    }

    /// <summary>
    /// Safely reads a string from <see cref="Utf8JsonReader"/> and returns it as a <see cref="SecureCharBuffer"/>.
    /// Avoids exposing a raw char[] to upper layers, letting the caller reliably dispose it via using.
    /// </summary>
    internal static SecureCharBuffer ReadCharsSecure(ref Utf8JsonReader reader)
    {
        int maxLen = reader.ValueSpan.Length;
        var temp = ArrayPool<char>.Shared.Rent(maxLen > 0 ? maxLen : 1);
        var buf = new SecureCharBuffer();
        try
        {
            int n = reader.CopyString(temp.AsSpan());
            buf.SetFromSpan(temp.AsSpan(0, n));
            return buf;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(temp.AsSpan()));
            ArrayPool<char>.Shared.Return(temp, clearArray: false);
        }
    }

}
