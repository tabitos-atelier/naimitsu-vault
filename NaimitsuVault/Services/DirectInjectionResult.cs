// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

public readonly struct DirectInjectionResult
{
    public bool IsSuccess         { get; }
    public bool IsHwndMismatch    { get; }
    public bool IsSendInputFailed { get; }
    public int  AbortedAtIndex    { get; }
    public int  Win32ErrorCode    { get; }

    private DirectInjectionResult(bool success, bool hwndMismatch, bool sendInputFailed, int abortedAt, int errorCode)
    {
        IsSuccess         = success;
        IsHwndMismatch    = hwndMismatch;
        IsSendInputFailed = sendInputFailed;
        AbortedAtIndex    = abortedAt;
        Win32ErrorCode    = errorCode;
    }

    public static DirectInjectionResult Success()
        => new(true, false, false, -1, 0);

    public static DirectInjectionResult HwndMismatch(int abortedAt)
        => new(false, true, false, abortedAt, 0);

    public static DirectInjectionResult SendInputFailed(int abortedAt, int errorCode)
        => new(false, false, true, abortedAt, errorCode);
}
