// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NaimitsuVault.Helpers;

namespace NaimitsuVault.Services;

/// <summary>
/// GC-pinned char array (Pinned Object Heap) buffer.
/// Calling Dispose/Zero after SetFromSpan physically wipes the plaintext at its fixed address via
/// CryptographicOperations.ZeroMemory. No old-address residue (ghost) from GC compaction occurs.
/// The only thing exposed externally is ReadOnlySpan&lt;char&gt; Span. If the caller needs a string, write new string(Span).
/// </summary>
public sealed class SecureCharBuffer : IDisposable
{
    private char[]? _pinned; // GC.AllocateArray(pinned: true) — Pinned Object Heap, never moves
    private int _length;
    private string? _displayCache;

    public bool IsEmpty => _length == 0;

    public ReadOnlySpan<char> Span
        => _pinned is null ? ReadOnlySpan<char>.Empty : _pinned.AsSpan(0, _length);

    /// <summary>
    /// Returns a string for UI display binding, reusing the same instance across repeated calls as
    /// long as the content is unchanged. Without this, every XAML re-render would mint a fresh,
    /// unprotected managed string via `new string(Span)` that is never zeroed.
    /// The cache is invalidated (not zeroed) by SetFromSpan/Zero — a control may still be displaying
    /// that exact reference, so zeroing it there would blank the visible text (same hazard class as
    /// zeroing a live TextBox.Text). It is only zeroed in Dispose(), by which point the owning
    /// window has already been torn down (see LockAsync's ordered shutdown).
    /// </summary>
    /// <remarks>
    /// Known, unfixable limitation: unlike the pinned char[] this reads from, the returned string is
    /// an ordinary movable-heap object (System.String has no public pinned-allocation API), so it is
    /// fully subject to GC compaction. If even one compacting collection runs between this call and
    /// the eventual Dispose()/ZeroStringInternals() call, the object is copied to a new address and
    /// the plaintext bytes at its old address are left untouched — there is no way for managed code to
    /// reach or zero that abandoned copy. This method only reduces how often such a copy is minted; it
    /// cannot make the residual window zero. The same limitation applies to any other plaintext that
    /// is ever materialized as a plain System.String for display purposes.
    ///
    /// A second, more direct known limitation: SetFromSpan() unconditionally calls Zero() before
    /// installing the new value, and Zero() only drops the _displayCache reference — it never calls
    /// ZeroStringInternals() on it (see Zero()'s own comment for why). So for any field whose value
    /// can change more than once (every two-way-bound edit field: ProfileViewModel's Name/Address1/etc,
    /// SecretsViewModel's Title/Password/etc), each prior ToDisplayString() result is discarded without
    /// ever receiving a zero pass at all — not even the "best effort, defeated by compaction" attempt
    /// Dispose() makes. Routing through Dispose() instead of Zero() does not avoid this: it only helps
    /// for a field whose value is set once and never changed again. Fixing Zero() to zero _displayCache
    /// was considered and rejected (Rev.1.8.0): Zero()/SetFromSpan() fire on the hot edit path, so
    /// zeroing a string a TextBox may still be rendering risks reproducing the same "visible text
    /// vanishes mid-edit" bug class already hit once. Recorded as an accepted limitation, same class as
    /// the compaction-ghost one above, not a bug to fix.
    /// </remarks>
    public string ToDisplayString() => IsEmpty ? "" : _displayCache ??= new string(Span);

    public void SetFromSpan(ReadOnlySpan<char> value)
    {
        Zero();
        if (value.IsEmpty) return;
        _pinned = GC.AllocateArray<char>(value.Length, pinned: true);
        _length = value.Length;
        value.CopyTo(_pinned.AsSpan());
    }

    public void Zero()
    {
        // Deliberately not zeroed here (see ToDisplayString()'s <remarks> for why) - dropped only.
        _displayCache = null;
        if (_pinned is null) return;
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_pinned.AsSpan(0, _length)));
        _pinned = null; // Release the reference to the POH array. Contents are already zeroed and await GC collection
        _length = 0;
    }

    public void Dispose()
    {
        if (_displayCache is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(_displayCache);
        _displayCache = null;
        Zero();
        GC.SuppressFinalize(this);
    }

    ~SecureCharBuffer() => Zero();
}
