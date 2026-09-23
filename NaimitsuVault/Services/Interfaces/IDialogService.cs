// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Services.Interfaces;

/// <summary>Preview information for a TimeMachine generation swap</summary>
/// <param name="TargetGen">Generation to swap: 1=current<->Gen1, 2=promote Gen2 to the front</param>
public record TimeMachineSwapPreview(
    string CurrentHeader,
    string Gen1Header,
    string? Gen2Header,
    int TargetGen);

/// <summary>
/// Result of the master password confirmation dialog.
/// The caller must physically clear PasswordChars (SecureCharBuffer) via <see cref="Scrub"/> or
/// <see cref="IDisposable.Dispose"/>.
/// </summary>
public sealed class MasterAuthResult : IDisposable
{
    /// <summary>SecureCharBuffer (internally pinned in the POH). ZeroMemory'd on Dispose().</summary>
    public SecureCharBuffer? PasswordChars { get; private set; }

    /// <summary>True if identity was verified via Windows Hello. PasswordChars is always null in that case.</summary>
    public bool IsHelloVerified { get; private init; }

    private MasterAuthResult() { }

    public MasterAuthResult(SecureCharBuffer passwordBuffer)
    {
        PasswordChars = passwordBuffer;
    }

    /// <summary>Factory for a Windows Hello authentication result. PasswordChars stays null.</summary>
    public static MasterAuthResult FromHello() => new() { IsHelloVerified = true };

    /// <summary>Physically clears PasswordChars and sets it to null (idempotent).</summary>
    public void Scrub()
    {
        PasswordChars?.Dispose();
        PasswordChars = null;
    }

    public void Dispose() => Scrub();
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, bool defaultToCancel = false);
    /// <summary>
    /// Confirm dialog whose title and action button both carry an icon + red styling matching the
    /// destructive toolbar action it confirms (e.g. trash glyph + "Common.Delete", or EraseTool
    /// glyph + "Common.PurgePermanently"), instead of a generic "OK". <paramref name="glyph"/>
    /// is a Segoe MDL2 glyph string (a backslash-u-escaped code point literal, matching the app's
    /// other C#-side glyph usages); <paramref name="actionText"/> is already-resolved display
    /// text (the caller resolves the locale key first, matching title/message).
    /// </summary>
    Task<bool> ConfirmDestructiveAsync(string title, string message, string glyph, string actionText, bool defaultToCancel = false);
    /// <summary>
    /// Confirm dialog whose title and action button both carry the same icon (e.g. the upload
    /// glyph for an import action, or the download glyph for an export warning). Standard accent
    /// styling by default; pass <paramref name="destructive"/> true for the same red/critical
    /// styling and title+button icon treatment as <see cref="ConfirmDestructiveAsync"/>.
    /// <paramref name="glyph"/> is a Segoe MDL2 glyph string (a backslash-u-escaped code point
    /// literal); <paramref name="actionText"/> is already-resolved display text (the caller
    /// resolves the locale key first, matching title/message).
    /// </summary>
    Task<bool> ConfirmWithIconAsync(string title, string message, string glyph, string actionText, bool destructive = false, bool defaultToCancel = false);
    Task<bool> ConfirmSwapAsync(string title, TimeMachineSwapPreview preview);
    /// <summary>
    /// Displays a password entry dialog.
    /// The caller must physically clear the returned SecureCharBuffer via Dispose(). Returns null on cancel.
    /// </summary>
    Task<SecureCharBuffer?> ConfirmPasswordAsync(string message);
    Task ShowInfoAsync(string title, string message);
    /// <summary>
    /// Displays a buttonless busy/processing dialog. Closed via Dispose(). Awaits the previous
    /// dialog's close animation before showing, so callers must not skip the await.
    /// </summary>
    Task<IDisposable> ShowBusyAsync(string message);

    /// <summary>
    /// Displays a dialog that verifies identity using the master password.
    /// The returned <see cref="MasterAuthResult"/> must be physically cleared after use, via a
    /// <c>using</c> declaration (<see cref="IDisposable.Dispose"/>) or an explicit call to
    /// <see cref="MasterAuthResult.Scrub"/>. Returns null on cancel.
    /// </summary>
    Task<MasterAuthResult?> ConfirmMasterAuthAsync(string message);
}
