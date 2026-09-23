// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Xaml.Controls;

namespace NaimitsuVault.Helpers;

/// <summary>
/// Helper that extracts a password from a WinUI 3 PasswordBox without leaving a plaintext string behind.
/// WinUI 3 has no PasswordBox.SecurePassword; the Password property is the only way to read it.
/// Exploits the fact that it returns a new string on every call: immediately after transcribing it to
/// a POH-pinned char[], the original string's internal buffer is ZeroMemory'd, bringing the plaintext's
/// lifetime as close to zero as possible.
/// </summary>
/// <remarks>
/// Known, unfixable limitation: this only reaches the managed-side string copy that the .Password
/// getter hands back. PasswordBox itself keeps its own native (WinRT) internal text buffer for
/// display/undo/IME composition, and no managed API exists to reach or zero it. Because a new
/// PasswordBox instance is created on every unlock attempt, each attempt's native buffer is a fresh,
/// separately-leaked plaintext copy that this class cannot touch. There is no known way to close this
/// gap short of replacing PasswordBox with a fully custom input control (own keystroke handling, own
/// masked rendering, IME disabled) — a change large enough that it hasn't been made.
/// </remarks>
internal static class SecurePasswordHelper
{
    /// <summary>
    /// Safely extracts the password from a PasswordBox and passes it to action as a ReadOnlySpan&lt;char&gt;.
    /// Physically erases the internal char[] buffer after action completes.
    /// Because the transcription target is allocated as immovable POH via GC.AllocateArray(pinned:true),
    /// ZeroMemory still reaches the correct address even if GC compaction occurs during a synchronous action.
    /// </summary>
    internal static void Borrow(PasswordBox pb, Action<ReadOnlySpan<char>> action)
    {
        var s = pb.Password;                        // A new string (not interned) — the only way to get it
        if (string.IsNullOrEmpty(s)) { action(ReadOnlySpan<char>.Empty); return; }

        // Allocate as POH-pinned via GC.AllocateArray(pinned:true). Eliminates GC-compaction ghosts.
        // try/finally around the allocation+copy so an exception there (e.g. OutOfMemoryException)
        // still reaches ZeroStringInternals instead of leaving s's plaintext on the heap.
        char[] chars;
        try
        {
            chars = GC.AllocateArray<char>(s.Length, pinned: true);
            s.CopyTo(0, chars, 0, s.Length);
        }
        finally
        {
            ZeroStringInternals(s);
        }

        try   { action(chars.AsSpan()); }
        finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan())); }
    }

    /// <summary>
    /// Retrieves only the character count. Never allocates a char[].
    /// Immediately ZeroMemory's the string's internal buffer.
    /// </summary>
    internal static int BorrowLength(PasswordBox pb)
    {
        var s = pb.Password;
        var len = s.Length;
        if (len > 0) ZeroStringInternals(s);
        return len;
    }

    /// <summary>
    /// Transcribes into a POH-pinned char[] and returns it. The caller must erase it with
    /// CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan())).
    /// No stale-address ghosting from GC compaction occurs even across an async await.
    /// </summary>
    /// <remarks>
    /// Named Extract, not Borrow, because it doesn't share this class's Borrow / BorrowLength read-only,
    /// auto-erasing-on-return contract: it also has the side effect of immediately setting
    /// <paramref name="pb"/>.Password to an empty string on call, and it hands full ownership of the
    /// returned array (including the ZeroMemory responsibility) to the caller. For use only when you
    /// want to extract the value once and also clear the on-screen display, e.g. right after submitting
    /// for authentication. Do not use this method just to peek at the value.
    /// </remarks>
    internal static char[] ExtractPinned(PasswordBox pb)
    {
        var s = pb.Password;
        pb.Password = string.Empty;
        if (string.IsNullOrEmpty(s)) return [];

        // try/finally around the allocation+copy so an exception there (e.g. OutOfMemoryException)
        // still reaches ZeroStringInternals instead of leaving s's plaintext on the heap.
        char[] pinned;
        try
        {
            pinned = GC.AllocateArray<char>(s.Length, pinned: true);
            s.CopyTo(0, pinned, 0, s.Length);
        }
        finally
        {
            ZeroStringInternals(s);
        }
        return pinned;
    }

    // PasswordBox.Password returns a new (non-interned) string on every call. TextBox.Text does NOT: it is
    // an ordinary dependency property getter that returns the stored string reference itself.
    // Unsafe.AsRef forcibly lifts the ReadOnly constraint so the internal char buffer can be ZeroMemory'd.
    // Promoted to internal because it's also called from ProfileViewModel's PII setter.
    //
    // CALLER CONTRACT: only pass a string instance that nothing else still uses. Zeroing writes directly
    // into the string's backing buffer, so every other holder of the same reference sees zeros:
    //  - A string returned by PasswordBox.Password is safe (a fresh copy per read).
    //  - A string read from TextBox.Text is NOT safe to pass as-is: it is the very instance the TextBox
    //    is still displaying, so zeroing it blanks the text on screen. Pass it only once the control no
    //    longer uses it (e.g. after the dialog closed), or guard the call so it runs only when the value
    //    actually changed (see the PII property setters in ProfileViewModel).
    //  - A string literal or any interned/shared string is NOT safe either: it corrupts the CLR's
    //    process-wide intern pool in place, so any other code that later reads that same literal
    //    observes zeroed-out characters instead of its original text.
    internal static void ZeroStringInternals(string s)
    {
        var span = MemoryMarshal.CreateSpan(
            ref Unsafe.AsRef(in s.GetPinnableReference()), s.Length);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(span));
    }
}
