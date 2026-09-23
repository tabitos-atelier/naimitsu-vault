// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Tests for DirectInjectionResult's factory methods and property invariants.
/// No P/Invoke dependency. Pure logic verification that never involves Win32InputSender
/// or AppWindow.
/// </summary>
public sealed class DirectInjectionResultTests
{
    // ── TC-DI-01: Success ─────────────────────────────────────────────────

    [Fact]
    public void Success_IsSuccess_IsTrue()
    {
        var r = DirectInjectionResult.Success();
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public void Success_OtherFlags_AreFalse()
    {
        var r = DirectInjectionResult.Success();
        Assert.False(r.IsHwndMismatch);
        Assert.False(r.IsSendInputFailed);
    }

    [Fact]
    public void Success_AbortedAtIndex_IsMinusOne()
    {
        var r = DirectInjectionResult.Success();
        Assert.Equal(-1, r.AbortedAtIndex);
    }

    [Fact]
    public void Success_Win32ErrorCode_IsZero()
    {
        var r = DirectInjectionResult.Success();
        Assert.Equal(0, r.Win32ErrorCode);
    }

    // ── TC-DI-02: HwndMismatch ────────────────────────────────────────────

    [Fact]
    public void HwndMismatch_IsHwndMismatch_IsTrue()
    {
        var r = DirectInjectionResult.HwndMismatch(abortedAt: 3);
        Assert.True(r.IsHwndMismatch);
    }

    [Fact]
    public void HwndMismatch_IsSuccess_IsFalse()
    {
        var r = DirectInjectionResult.HwndMismatch(abortedAt: 3);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void HwndMismatch_IsSendInputFailed_IsFalse()
    {
        var r = DirectInjectionResult.HwndMismatch(abortedAt: 3);
        Assert.False(r.IsSendInputFailed);
    }

    [Fact]
    public void HwndMismatch_AbortedAtIndex_MatchesArgument()
    {
        var r = DirectInjectionResult.HwndMismatch(abortedAt: 7);
        Assert.Equal(7, r.AbortedAtIndex);
    }

    [Fact]
    public void HwndMismatch_Win32ErrorCode_IsZero()
    {
        var r = DirectInjectionResult.HwndMismatch(abortedAt: 0);
        Assert.Equal(0, r.Win32ErrorCode);
    }

    [Fact]
    public void HwndMismatch_AbortedAtIndex_Zero_IsValid()
    {
        var r = DirectInjectionResult.HwndMismatch(abortedAt: 0);
        Assert.True(r.IsHwndMismatch);
        Assert.Equal(0, r.AbortedAtIndex);
    }

    // ── TC-DI-03: SendInputFailed ─────────────────────────────────────────

    [Fact]
    public void SendInputFailed_IsSendInputFailed_IsTrue()
    {
        var r = DirectInjectionResult.SendInputFailed(abortedAt: 5, errorCode: 87);
        Assert.True(r.IsSendInputFailed);
    }

    [Fact]
    public void SendInputFailed_IsSuccess_IsFalse()
    {
        var r = DirectInjectionResult.SendInputFailed(abortedAt: 5, errorCode: 87);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void SendInputFailed_IsHwndMismatch_IsFalse()
    {
        var r = DirectInjectionResult.SendInputFailed(abortedAt: 5, errorCode: 87);
        Assert.False(r.IsHwndMismatch);
    }

    [Fact]
    public void SendInputFailed_AbortedAtIndex_MatchesArgument()
    {
        var r = DirectInjectionResult.SendInputFailed(abortedAt: 12, errorCode: 0);
        Assert.Equal(12, r.AbortedAtIndex);
    }

    [Fact]
    public void SendInputFailed_Win32ErrorCode_MatchesArgument()
    {
        var r = DirectInjectionResult.SendInputFailed(abortedAt: 0, errorCode: 87);
        Assert.Equal(87, r.Win32ErrorCode);
    }

    [Fact]
    public void SendInputFailed_Win32ErrorCode_NegativeValue_IsPreserved()
    {
        // Values are mostly positive, e.g. ERROR_INVALID_PARAMETER = 87, ERROR_ACCESS_DENIED = 5,
        // but confirm negative values are also preserved to account for environments where
        // GetLastError is occasionally treated as signed
        var r = DirectInjectionResult.SendInputFailed(abortedAt: 0, errorCode: -1);
        Assert.Equal(-1, r.Win32ErrorCode);
    }

    // ── TC-DI-04: exclusivity (mutually exclusive flags) ────────────────────────────────────

    [Fact]
    public void Success_ExactlyOneFlag_IsTrue()
    {
        var r = DirectInjectionResult.Success();
        int trueCount = (r.IsSuccess ? 1 : 0) + (r.IsHwndMismatch ? 1 : 0) + (r.IsSendInputFailed ? 1 : 0);
        Assert.Equal(1, trueCount);
    }

    [Fact]
    public void HwndMismatch_ExactlyOneFlag_IsTrue()
    {
        var r = DirectInjectionResult.HwndMismatch(abortedAt: 0);
        int trueCount = (r.IsSuccess ? 1 : 0) + (r.IsHwndMismatch ? 1 : 0) + (r.IsSendInputFailed ? 1 : 0);
        Assert.Equal(1, trueCount);
    }

    [Fact]
    public void SendInputFailed_ExactlyOneFlag_IsTrue()
    {
        var r = DirectInjectionResult.SendInputFailed(abortedAt: 0, errorCode: 0);
        int trueCount = (r.IsSuccess ? 1 : 0) + (r.IsHwndMismatch ? 1 : 0) + (r.IsSendInputFailed ? 1 : 0);
        Assert.Equal(1, trueCount);
    }
}
