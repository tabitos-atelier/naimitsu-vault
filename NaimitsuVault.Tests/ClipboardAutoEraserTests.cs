// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NaimitsuVault.Helpers;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Clipboard auto-erase (30-second timer)" - all 4 cases for ClipboardAutoEraser.
///
/// Temporarily borrows the real OS clipboard space. <see cref="IDisposable.Dispose"/> always
/// calls <see cref="ClipboardHelper.Clear"/> to reset the clipboard to a clean slate.
///
/// Injecting <c>action => action()</c> into <see cref="ClipboardAutoEraser"/>'s constructor makes it
/// run synchronously without <c>App.UiDispatcherQueue</c>, enabling headless testing.
/// </summary>
[Collection("SequentialClipboard")]
public sealed class ClipboardAutoEraserTests : IDisposable
{
    // ── P/Invoke (guard-only; independent of ClipboardHelper's private declarations) ────────
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    /// <summary>
    /// If OpenClipboard fails, skip the test as an environment limitation (fail-safe).
    /// The Windows Clipboard History service can temporarily hold the clipboard while
    /// processing the immediately preceding change, so retry 5 times at 50ms intervals before deciding.
    /// </summary>
    private static void SkipIfClipboardUnavailable()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) { CloseClipboard(); return; }
            if (attempt < 4) Thread.Sleep(50);
        }
        Assert.Skip("OpenClipboard failed (held by another process or an environment limitation) - skipping this test");
    }

    public void Dispose() => ClipboardHelper.Clear();

    // ── TC-CAE-01 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IsClipboardTextHashMatch_ExactSameText_ReturnsTrue()
    {
        SkipIfClipboardUnavailable();

        // Arrange
        const string text = "SecretPassword123";
        ClipboardHelper.SetText(text);
        var hash = SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan()));

        // Act
        var result = ClipboardHelper.IsClipboardTextHashMatch(hash);

        // Assert - the hash computed directly from the Win32 pointer matches exactly
        Assert.True(result);
    }

    // ── TC-CAE-02 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IsClipboardTextHashMatch_DifferentText_ReturnsFalse()
    {
        SkipIfClipboardUnavailable();

        // Arrange
        ClipboardHelper.SetText("SecretPassword123");
        var wrongHash = SHA256.HashData(MemoryMarshal.AsBytes("HackedPassword".AsSpan()));

        // Act
        var result = ClipboardHelper.IsClipboardTextHashMatch(wrongHash);

        // Assert
        Assert.False(result);
    }

    // ── TC-CAE-03 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleClear_ConsecutiveCalls_CancelsPreviousAndClearsLast()
    {
        SkipIfClipboardUnavailable();

        // Arrange - set "LastPassword" on the clipboard and use a 50ms timer with synchronous dispatch
        const string lastPassword = "LastPassword";
        ClipboardHelper.SetText(lastPassword);

        using var eraser = new ClipboardAutoEraser(dispatch: action => action());
        eraser.DelayDuration = TimeSpan.FromMilliseconds(50);

        // Act - the first timer is immediately cancelled by the second call
        eraser.ScheduleClear("FirstPassword");
        eraser.ScheduleClear(lastPassword);

        // Poll for up to 1000ms (50ms x 20 times) to absorb the 50ms timer plus thread-pool
        // scheduling lag while waiting for the erase to complete
        var text = ClipboardHelper.GetText();
        for (int attempt = 0; text is not (null or "") && attempt < 20; attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            text = ClipboardHelper.GetText();
        }

        // Assert - "LastPassword"'s erase timer fires and the clipboard becomes empty
        Assert.True(text is null or "");
    }

    // ── TC-CAE-04 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SetText_SetsThreeSecurityFormatFlags_AvailableInOSClipboard()
    {
        SkipIfClipboardUnavailable();

        // Arrange
        ClipboardHelper.SetText("TopSecretPassword");

        // [Fail-safe guard] Before asserting, verify RegisterClipboardFormat's P/Invoke return value.
        // A return of 0 means an environment limitation (sandboxed, locked, etc.), so protect with a skip rather than a hard failure.
        uint fMonitor = RegisterClipboardFormat(ClipboardHelper.FormatMonitor);
        uint fHistory = RegisterClipboardFormat(ClipboardHelper.FormatHistory);
        uint fCloud   = RegisterClipboardFormat(ClipboardHelper.FormatCloud);
        if (fMonitor == 0 || fHistory == 0 || fCloud == 0)
            Assert.Skip("RegisterClipboardFormat failed (an environment limitation) - skipping this test");

        // Act - atomically confirm all 3 flags within a single OpenClipboard session.
        // Retry inside GetSecurityFlagsPresent, since the Windows Clipboard History service can
        // temporarily hold the clipboard upon receiving the change notification.
        var (monitor, history, cloud) = ClipboardHelper.GetSecurityFlagsPresent();

        // Assert - SetText has set all 3 security flags on the OS clipboard
        Assert.True(monitor, "The history-monitor exclusion flag is not set");
        Assert.True(history, "The Win+V history exclusion flag is not set");
        Assert.True(cloud,   "The cloud-sync exclusion flag is not set");
    }
}
