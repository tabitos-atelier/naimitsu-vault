// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;

namespace NaimitsuVault.Services;

/// <summary>
/// Holds decrypted UTF-8 plaintext bytes on the Pinned Object Heap and calls ZeroMemory on Dispose.
/// GC.AllocateArray(pinned: true) prevents GC compaction ghosts.
/// The only thing exposed externally is ReadOnlySpan&lt;byte&gt; Utf8.
/// If the caller needs a string, they must do so explicitly via Encoding.UTF8.GetString(sp.Utf8)
/// (making string creation intentionally visible at the call site).
/// If a sensitive char buffer is needed, transfer directly into a SecureCharBuffer via FieldCrypto.FillBufferFromUtf8.
/// </summary>
internal sealed class SecurePlaintext : IDisposable
{
    private byte[]? _pinned; // GC.AllocateArray(pinned: true) — Pinned Object Heap, never moves

    private SecurePlaintext(byte[] pinned) => _pinned = pinned;

    /// <summary>
    /// Internally allocates a pinned array of the given length and returns a Span&lt;byte&gt; the caller can write to directly.
    /// The caller must write the decryption result into the returned Span, then manage the instance with using.
    /// </summary>
    internal static SecurePlaintext Allocate(int length, out Span<byte> writableSpan)
    {
        var pinned = GC.AllocateArray<byte>(length, pinned: true);
        var sp = new SecurePlaintext(pinned);
        writableSpan = pinned.AsSpan();
        return sp;
    }

    /// <summary>Read-only span over the raw UTF-8 bytes. An empty span after Dispose.</summary>
    public ReadOnlySpan<byte> Utf8 => _pinned ?? ReadOnlySpan<byte>.Empty;

    public void Dispose()
    {
        if (_pinned is not null)
        {
            CryptographicOperations.ZeroMemory(_pinned);
            _pinned = null;
        }
        GC.SuppressFinalize(this);
    }

    ~SecurePlaintext() => Dispose();
}
