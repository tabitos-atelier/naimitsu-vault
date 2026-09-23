// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NaimitsuVault.Services;

internal static partial class Win32InputSender
{
    private const int    INPUT_KEYBOARD    = 1;
    private const uint   KEYEVENTF_UNICODE = 0x0004u;
    private const uint   KEYEVENTF_KEYUP   = 0x0002u;
    private const ushort VK_RETURN         = 0x0D;

    // x64: type(4) + implicit padding(4) + union(32) = 40 bytes
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public int        type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;   // UTF-16 code unit of the password character
        public uint   dwFlags;
        public uint   time;
        public nint   dwExtraInfo;
    }

    // LibraryImport + ReadOnlySpan<INPUT>:
    // Pins the stackalloc buffer on the stack directly and passes a pointer.
    // The old DllImport(INPUT[] pInputs) allocated an INPUT array containing the password
    // character in wScan on the heap and left it to the GC without zeroing it (violates the zero-knowledge principle).
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint nInputs, ReadOnlySpan<INPUT> pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    internal static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint hWnd);

    internal delegate void WinEventProc(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    internal static DirectInjectionResult Inject(
        ReadOnlySpan<char> chars, nint hwndTarget, bool appendEnter, Random rng,
        CancellationToken ct = default)
    {
        Span<INPUT> inputs = stackalloc INPUT[2];
        try
        {
            for (int i = 0; i < chars.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (GetForegroundWindow() != hwndTarget)
                    return DirectInjectionResult.HwndMismatch(i);

                char c = chars[i];
                if (char.IsHighSurrogate(c) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
                {
                    if (!SendChar(inputs, c))
                        return DirectInjectionResult.SendInputFailed(i, Marshal.GetLastWin32Error());
                    i++;
                    if (GetForegroundWindow() != hwndTarget)
                        return DirectInjectionResult.HwndMismatch(i);
                    if (!SendChar(inputs, chars[i]))
                        return DirectInjectionResult.SendInputFailed(i, Marshal.GetLastWin32Error());
                }
                else
                {
                    if (!SendChar(inputs, c))
                        return DirectInjectionResult.SendInputFailed(i, Marshal.GetLastWin32Error());
                }
                Thread.Sleep(rng.Next(10, 71));
            }

            if (appendEnter)
            {
                ct.ThrowIfCancellationRequested();
                if (GetForegroundWindow() != hwndTarget)
                    return DirectInjectionResult.HwndMismatch(chars.Length);
                if (!SendEnter(inputs))
                    return DirectInjectionResult.SendInputFailed(chars.Length, Marshal.GetLastWin32Error());
                Thread.Sleep(rng.Next(10, 71));
            }

            return DirectInjectionResult.Success();
        }
        finally
        {
            // Zero-clear because the password character (UTF-16 code unit) remains in ki.wScan
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(inputs));
        }
    }

    private static bool SendChar(Span<INPUT> buf, char c)
    {
        buf[0] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE } };
        buf[1] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } };
        return SendInput(2, buf, Marshal.SizeOf<INPUT>()) == 2;
    }

    private static bool SendEnter(Span<INPUT> buf)
    {
        buf[0] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_RETURN, wScan = 0, dwFlags = 0 } };
        buf[1] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_RETURN, wScan = 0, dwFlags = KEYEVENTF_KEYUP } };
        return SendInput(2, buf, Marshal.SizeOf<INPUT>()) == 2;
    }
}
