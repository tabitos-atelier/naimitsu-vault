// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Protects a window from capture/screen sharing using Win32 SetWindowDisplayAffinity.
/// WDA_EXCLUDEFROMCAPTURE (0x11) makes it appear blacked out in Snipping Tool, OBS, etc.
/// If the OS doesn't support it (below Win10 2004), returns false without SetLastError, but no exception is thrown.
/// </summary>
public sealed class Win32WindowCaptureProtectionService : IWindowCaptureProtectionService
{
    private const uint WDA_NONE               = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public void Apply(nint hwnd, bool enabled)
    {
        if (hwnd == nint.Zero) return;
        SetWindowDisplayAffinity(hwnd, enabled ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
}
