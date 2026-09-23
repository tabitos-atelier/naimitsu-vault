// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NaimitsuVault.Services;

/// <summary>
/// GC-pinned byte array (Pinned Object Heap) buffer.
/// Calling Dispose after SetFromSpan physically wipes the plaintext bytes at their fixed address.
/// No old-address residue (ghost) from GC compaction occurs.
/// </summary>
public sealed class SecureByteBuffer : IDisposable
{
    private byte[]? _pinned; // GC.AllocateArray(pinned: true) — POH allocation, never moves
    private int _length;

    public bool IsEmpty => _length == 0;
    public int Length => _length;

    public ReadOnlySpan<byte> Span
        => _pinned is null ? ReadOnlySpan<byte>.Empty : _pinned.AsSpan(0, _length);

    /// <summary>
    /// Direct reference to the POH-pinned array. For wrapping a WinRT IBuffer across an async boundary only.
    /// Becomes null after Dispose. Use it together with Length.
    /// </summary>
    internal byte[]? PinnedArray => _pinned;

    public void SetFromSpan(ReadOnlySpan<byte> value)
    {
        Zero();
        if (value.IsEmpty) return;
        _pinned = GC.AllocateArray<byte>(value.Length, pinned: true);
        _length = value.Length;
        value.CopyTo(_pinned.AsSpan());
    }

    public void Zero()
    {
        if (_pinned is null) return;
        CryptographicOperations.ZeroMemory(_pinned.AsSpan(0, _length));
        _pinned = null;
        _length = 0;
    }

    public void Dispose()
    {
        Zero();
        GC.SuppressFinalize(this);
    }

    ~SecureByteBuffer() => Zero();
}
