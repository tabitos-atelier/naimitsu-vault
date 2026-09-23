// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Security.Cryptography;

namespace NaimitsuVault.Helpers;

/// <summary>
/// A growable, POH-pinned byte buffer implementing <see cref="IBufferWriter{T}"/>, intended as the
/// output target for <see cref="System.Text.Json.Utf8JsonWriter"/> when serializing plaintext JSON
/// that is about to be encrypted (or was just decrypted). Unlike <see cref="MemoryStream"/>, growth
/// zero-clears the discarded old array before replacing it, so no unzeroed plaintext generation
/// survives a capacity reallocation. <see cref="Dispose"/> zero-clears the final buffer.
/// </summary>
internal sealed class PinnedBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buf = GC.AllocateArray<byte>(512, pinned: true);
    private int _pos;

    public ReadOnlySpan<byte> WrittenSpan => _buf.AsSpan(0, _pos);

    public void Advance(int count) => _pos += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint <= 0 ? 256 : sizeHint);
        return _buf.AsMemory(_pos);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint <= 0 ? 256 : sizeHint);
        return _buf.AsSpan(_pos);
    }

    private void EnsureCapacity(int needed)
    {
        if (_buf.Length - _pos >= needed) return;
        int newSize = Math.Max(_buf.Length * 2, _pos + needed);
        var next = GC.AllocateArray<byte>(newSize, pinned: true);
        _buf.AsSpan(0, _pos).CopyTo(next);
        CryptographicOperations.ZeroMemory(_buf); // Zero the old pinned array before discarding it
        _buf = next;
    }

    // Zero the whole buffer, not just [0, _pos): a caller that took GetSpan()/GetMemory() and wrote
    // plaintext into it but threw before calling Advance() would otherwise leave that plaintext
    // unzeroed in the discarded tail.
    public void Dispose() => CryptographicOperations.ZeroMemory(_buf);
}
