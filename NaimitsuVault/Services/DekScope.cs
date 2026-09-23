// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

/// <summary>
/// Type-safe access wrapper for the DEK held in a GC-pinned buffer.
/// <para>
/// Design principles:
/// - <see cref="Span"/> should only be expanded immediately before a crypto computation (since
///   <c>ReadOnlySpan</c> is a ref struct, it cannot be held as a local variable across an await).
///   Some read-only async methods call <c>dek.Span</c> after an await, but during Lock() this
///   becomes a decryption-failure fallback (fail-safe).
/// - <see cref="UnsafeArray"/> is for byte[]-required APIs such as ProtectedData.Protect only (assembly-internal only).
/// - Storing it in a class/struct **field** is forbidden (the reference would outlive Lock() and break the lifecycle).
///   Holding it in an async method's state machine (passed as a parameter) is acceptable.
/// - Duplicating it via Clone() etc. is forbidden (an unpinned copy would create ghost memory).
/// </para>
/// </summary>
public readonly struct DekScope
{
    private readonly byte[] _pinned;
    private readonly int    _length;

    internal DekScope(byte[] pinned, int length)
    {
        _pinned = pinned;
        _length = length;
    }

    /// <summary>
    /// Span passed to ICryptoService's synchronous methods.
    /// Valid only within the caller's stack frame. Do not use it across an await.
    /// </summary>
    public ReadOnlySpan<byte> Span
    {
        get
        {
            if (_pinned is null)
                throw new InvalidOperationException("DekScope is invalid (after Lock or uninitialized).");
            return new ReadOnlySpan<byte>(_pinned, 0, _length);
        }
    }

    /// <summary>
    /// For byte[]-required APIs such as ProtectedData.Protect only.
    /// Use only at synchronous call sites that do not cross an await.
    /// Clone() forbidden, field storage forbidden.
    /// </summary>
    internal byte[] UnsafeArray
    {
        get
        {
            if (_pinned is null)
                throw new InvalidOperationException("DekScope is invalid (after Lock or uninitialized).");
            return _pinned;
        }
    }
}
