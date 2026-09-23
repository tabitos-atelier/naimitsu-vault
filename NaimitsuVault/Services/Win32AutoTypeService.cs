// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public sealed class Win32AutoTypeService : IAutoTypeService, IDisposable
{
    private const uint WINEVENT_SYSTEM_FOREGROUND = 0x0003u;
    private const uint WINEVENT_OUTOFCONTEXT      = 0x0000u;

    private nint _hHook = nint.Zero;
    private nint _hwndPrevious = nint.Zero;
    private readonly uint _currentProcessId;
    private Win32InputSender.WinEventProc? _hookProc;

    public Win32AutoTypeService()
    {
        _currentProcessId = (uint)Environment.ProcessId;
        _hookProc = OnForegroundChanged;
        _hHook = Win32InputSender.SetWinEventHook(
            WINEVENT_SYSTEM_FOREGROUND, WINEVENT_SYSTEM_FOREGROUND,
            nint.Zero, _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public nint GetPreviousTargetHwnd() => _hwndPrevious;

    public Task<DirectInjectionResult> InjectAsync(
        SecureCharBuffer buffer, nint hwndTarget, bool appendEnter, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            // Random.Shared (thread-safe) instead of a per-instance Random, in case InjectAsync is
            // ever invoked concurrently (e.g. the AutoType shortcut fired twice in quick succession).
            return Win32InputSender.Inject(buffer.Span, hwndTarget, appendEnter, Random.Shared, ct);
        }, ct);

    private void OnForegroundChanged(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == nint.Zero) return;
        Win32InputSender.GetWindowThreadProcessId(hwnd, out uint foregroundPid);
        if (foregroundPid == _currentProcessId) return;
        _hwndPrevious = hwnd;
    }

    public void Dispose()
    {
        if (_hHook != nint.Zero)
        {
            Win32InputSender.UnhookWinEvent(_hHook);
            _hHook = nint.Zero;
        }
        _hookProc = null;
    }
}
