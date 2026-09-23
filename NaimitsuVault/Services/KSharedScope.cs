// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

/// <summary>
/// Type-safe access wrapper for K_shared held in a GC-pinned buffer.
/// <para>
/// Same structure as DekScope. Design principles:
/// - <see cref="Span"/> should only be expanded immediately before a crypto computation.
/// - <see cref="UnsafeArray"/> is for byte[]-required APIs such as DPAPI only (assembly-internal only).
/// - Storing it in a field is forbidden (the reference would outlive LockAsync() and break the lifecycle).
/// - Duplicating it via Clone() etc. is forbidden (an unpinned copy would create ghost memory).
/// </para>
/// </summary>
public readonly struct KSharedScope
{
    private readonly byte[] _pinned;
    private readonly int    _length;

    internal KSharedScope(byte[] pinned, int length)
    {
        _pinned = pinned;
        _length = length;
    }

    public ReadOnlySpan<byte> Span
    {
        get
        {
            if (_pinned is null)
                throw new InvalidOperationException("KSharedScope is invalid (after Lock or uninitialized).");
            return new ReadOnlySpan<byte>(_pinned, 0, _length);
        }
    }

    internal byte[] UnsafeArray
    {
        get
        {
            if (_pinned is null)
                throw new InvalidOperationException("KSharedScope is invalid (after Lock or uninitialized).");
            return _pinned;
        }
    }
}
