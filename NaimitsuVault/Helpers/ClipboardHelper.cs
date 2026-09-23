// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NaimitsuVault.Helpers;

/// <summary>
/// Win32 clipboard API wrapper.
/// Windows.ApplicationModel.DataTransfer.Clipboard is UWP (CoreWindow) only, so it throws
/// CO_E_NOTINITIALIZED in a WinUI 3 desktop app.
/// Calls user32.dll / kernel32.dll directly to avoid the COM dependency.
/// </summary>
public static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE  = 0x0002;

    internal const string FormatMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string FormatHistory = "CanIncludeInClipboardHistory";
    internal const string FormatCloud   = "CanUploadToCloudClipboard";

    public static void SetText(string text) => SetText(text.AsSpan());

    /// <summary>
    /// Writes directly in-place from a pinned span to the Win32 clipboard.
    /// Never allocates a string instance.
    /// </summary>
    public static void SetText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        if (!OpenClipboard(nint.Zero)) return;
        try
        {
            EmptyClipboard();

            int byteCount = (text.Length + 1) * sizeof(char);
            var hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)byteCount);
            if (hMem == nint.Zero) return;

            var ptr = GlobalLock(hMem);
            if (ptr == nint.Zero) { GlobalFree(hMem); return; }
            try
            {
                try
                {
                    unsafe
                    {
                        fixed (char* pText = text) // fixed on ReadOnlySpan<char> is supported in C# 7.3+
                        {
                            Buffer.MemoryCopy(pText, (void*)ptr, byteCount, text.Length * sizeof(char));
                        }
                    }
                    Marshal.WriteInt16(ptr + text.Length * sizeof(char), 0); // null terminator
                }
                finally
                {
                    GlobalUnlock(hMem);
                }

                if (SetClipboardData(CF_UNICODETEXT, hMem) == nint.Zero)
                { GlobalFree(hMem); return; }
            }
            catch
            {
                // If an exception occurs during copy/write, execution never reaches SetClipboardData
                // and ownership of hMem never transfers to the clipboard, so free it here to be safe.
                GlobalFree(hMem);
                throw;
            }

            SetDword(FormatMonitor, 0);
            SetDword(FormatHistory, 0);
            SetDword(FormatCloud,   0);
        }
        finally
        {
            CloseClipboard();
        }
    }

    // Unit test use only. Must not be used in production — the string persists as unmanaged plaintext outside GC control, so never use it to read sensitive data.
    internal static string? GetText()
    {
        if (!OpenClipboard(nint.Zero)) return null;
        try
        {
            var hMem = GetClipboardData(CF_UNICODETEXT);
            if (hMem == nint.Zero) return null;
            var ptr = GlobalLock(hMem);
            if (ptr == nint.Zero) return null;
            try   { return Marshal.PtrToStringUni(ptr); }
            finally { GlobalUnlock(hMem); }
        }
        finally { CloseClipboard(); }
    }

    public static void Clear()
    {
        if (!OpenClipboard(nint.Zero)) return;
        try   { EmptyClipboard(); }
        finally { CloseClipboard(); }
    }

    /// <summary>
    /// Checks whether the SHA-256 of the clipboard's current text matches expectedHash.
    /// Never creates a plaintext string object on the heap (for the auto-clear flow only).
    /// </summary>
    public static bool IsClipboardTextHashMatch(ReadOnlySpan<byte> expectedHash)
    {
        if (!OpenClipboard(nint.Zero)) return false;
        try
        {
            var hMem = GetClipboardData(CF_UNICODETEXT);
            if (hMem == nint.Zero) return false;
            var ptr = GlobalLock(hMem);
            if (ptr == nint.Zero) return false;
            try
            {
                // Check the actual allocated size with GlobalSize before scanning. This ensures we
                // never read past the allocated block, even if another process places a non-NUL-terminated CF_UNICODETEXT on the clipboard.
                nuint byteCapacity = GlobalSize(hMem);
                if (byteCapacity < sizeof(char)) return false;
                int maxChars = (int)(byteCapacity / sizeof(char));

                int byteLen;
                unsafe
                {
                    var span = new ReadOnlySpan<char>((void*)ptr, maxChars);
                    int nulIndex = span.IndexOf('\0');
                    int charLen = nulIndex >= 0 ? nulIndex : maxChars;
                    byteLen = charLen * sizeof(char);
                }
                if (byteLen == 0) return false;

                Span<byte> hash = stackalloc byte[32];
                unsafe
                {
                    SHA256.HashData(new ReadOnlySpan<byte>((void*)ptr, byteLen), hash);
                }
                return CryptographicOperations.FixedTimeEquals(hash, expectedHash);
            }
            finally { GlobalUnlock(hMem); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>
    /// Atomically checks whether the three security format flags are present on the OS clipboard,
    /// within a single <see cref="OpenClipboard"/> session.
    /// Retries up to 5 times at 50ms intervals to handle the race where the clipboard history
    /// service temporarily takes ownership of the clipboard after receiving a change notification.
    /// </summary>
    internal static (bool Monitor, bool History, bool Cloud) GetSecurityFlagsPresent()
    {
        uint fMonitor = RegisterClipboardFormat(FormatMonitor);
        uint fHistory = RegisterClipboardFormat(FormatHistory);
        uint fCloud   = RegisterClipboardFormat(FormatCloud);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(nint.Zero))
            {
                try
                {
                    return (
                        GetClipboardData(fMonitor) != nint.Zero,
                        GetClipboardData(fHistory) != nint.Zero,
                        GetClipboardData(fCloud)   != nint.Zero
                    );
                }
                finally { CloseClipboard(); }
            }
            if (attempt < 4) Thread.Sleep(50);
        }
        return (false, false, false);
    }

    private static void SetDword(string formatName, uint value)
    {
        uint format = RegisterClipboardFormat(formatName);
        if (format == 0) return;
        var hMem = GlobalAlloc(GMEM_MOVEABLE, sizeof(int));
        if (hMem == nint.Zero) return;
        var ptr = GlobalLock(hMem);
        if (ptr == nint.Zero) { GlobalFree(hMem); return; }
        Marshal.WriteInt32(ptr, unchecked((int)value));
        GlobalUnlock(hMem);
        if (SetClipboardData(format, hMem) == nint.Zero)
            GlobalFree(hMem); // Free it ourselves only if SetClipboardData fails
    }

    [DllImport("user32.dll")] private static extern bool    OpenClipboard(nint hWnd);
    [DllImport("user32.dll")] private static extern bool    EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint    GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern nint    SetClipboardData(uint uFormat, nint hMem);
    [DllImport("user32.dll")] private static extern bool    CloseClipboard();
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("kernel32.dll")] private static extern nint   GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll")] private static extern nint   GlobalLock(nint hMem);
    [DllImport("kernel32.dll")] private static extern bool   GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll")] private static extern nint   GlobalFree(nint hMem);
    [DllImport("kernel32.dll")] private static extern nuint  GlobalSize(nint hMem);
}
