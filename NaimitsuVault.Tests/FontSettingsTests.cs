// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using CommunityToolkit.Mvvm.Messaging;
using NaimitsuVault.Helpers;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// "App-wide font family setting" TC-FNT-01–06.
/// Covers <see cref="InstalledFontEnumerator"/> (real GDI enumeration), the portable-use fallback
/// decision (<see cref="AppSettingsViewModel.ResolveFontFamily"/>), and the WeakReferenceMessenger
/// broadcast fired when the setting changes.
/// </summary>
public sealed class FontSettingsTests : IDisposable
{
    private sealed class NullFontResourceService : IFontResourceService
    {
        public void Apply(string? fontFamily) { }
    }

    // Cleans up WeakReferenceMessenger's global state after the test (unregisters the subscription that used this as the recipient)
    public void Dispose()
        => WeakReferenceMessenger.Default.UnregisterAll(this);

    // ── TC-FNT-01 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-FNT-01: EnumFontFamiliesEx against the real OS returns at least one installed font.
    /// Every Windows install ships system fonts, so an empty result would indicate the P/Invoke is broken.
    /// </summary>
    [Fact]
    public void GetInstalledFontFamilies_ReturnsNonEmptyResult()
    {
        var fonts = InstalledFontEnumerator.GetInstalledFontFamilies();

        Assert.NotEmpty(fonts);
    }

    // ── TC-FNT-02 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-FNT-02: the result is deduplicated, ordinally sorted, and excludes the '@'-prefixed
    /// vertical-writing variants of CJK fonts.
    /// </summary>
    [Fact]
    public void GetInstalledFontFamilies_IsSortedDeduplicatedAndExcludesVerticalVariants()
    {
        var fonts = InstalledFontEnumerator.GetInstalledFontFamilies();

        Assert.Equal(fonts.Distinct(StringComparer.Ordinal).Count(), fonts.Length);
        Assert.Equal(fonts.OrderBy(f => f, StringComparer.Ordinal), fonts);
        Assert.DoesNotContain(fonts, f => f.StartsWith('@'));
    }

    // ── TC-FNT-03 ─────────────────────────────────────────────────────────────

    /// <summary>TC-FNT-03: a persisted name present in the enumerated options passes through unchanged.</summary>
    [Fact]
    public void ResolveFontFamily_PersistedNameInOptions_ReturnsSameName()
    {
        var options = new[] { "Yu Gothic UI", "Consolas", "Segoe UI" };

        var result = AppSettingsViewModel.ResolveFontFamily("Consolas", options);

        Assert.Equal("Consolas", result);
    }

    // ── TC-FNT-04 ─────────────────────────────────────────────────────────────

    /// <summary>TC-FNT-04: a null persisted value (never customized) stays null.</summary>
    [Fact]
    public void ResolveFontFamily_PersistedNull_ReturnsNull()
    {
        var options = new[] { "Yu Gothic UI", "Consolas" };

        var result = AppSettingsViewModel.ResolveFontFamily(null, options);

        Assert.Null(result);
    }

    // ── TC-FNT-05 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-FNT-05: portable-use fallback. A font name persisted on another PC (USB stick moved
    /// between machines, or a name that differs by OS display language) isn't in this machine's
    /// enumerated options, so it must silently fall back to null instead of being passed to WinUI as-is.
    /// </summary>
    [Fact]
    public void ResolveFontFamily_PersistedNameNotInOptions_FallsBackToNull()
    {
        var options = new[] { "Yu Gothic UI", "Consolas" };

        var result = AppSettingsViewModel.ResolveFontFamily("游ゴシック", options);

        Assert.Null(result);
    }

    // ── TC-FNT-06 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-FNT-06: setting FontFamily through the public property synchronously broadcasts
    /// FontFamilyChangedMessage via WeakReferenceMessenger, matching the CaptureProtectionChangedMessage
    /// pattern verified by WindowCaptureProtectionTests (TC-WCP-02).
    /// </summary>
    [Fact]
    public void FontFamily_SetToNewValue_BroadcastsFontFamilyChangedMessage()
    {
        var vm = BuildMinimalSettingsVm();
        var receivedMessages = new List<FontFamilyChangedMessage>();

        WeakReferenceMessenger.Default.Register<FontFamilyChangedMessage>(this, (_, msg) =>
            receivedMessages.Add(msg));

        try
        {
            // Act: synchronously fires partial void OnFontFamilyChanged("Consolas")
            vm.FontFamily = "Consolas";

            Assert.Single(receivedMessages);
            Assert.Equal("Consolas", receivedMessages[0].FontFamily);
        }
        finally
        {
            // Reliably cancels and releases the 300ms debounce timer (CancellationTokenSource)
            // that SchedulePersist() started, before the test ends.
            vm.Dispose();
        }
    }

    /// <summary>
    /// A minimal test-only AppSettingsViewModel. The only dependency OnFontFamilyChanged uses is
    /// WeakReferenceMessenger (a static). Other fields are initialized with null!, and the 300ms
    /// delayed DB write fired by SchedulePersist inside the handler is silently absorbed via
    /// null _factory -> NullRef -> catch (same pattern as WindowCaptureProtectionTests).
    /// </summary>
    private static AppSettingsViewModel BuildMinimalSettingsVm()
        => new(
            factory:           null!,
            auth:              null!,
            session:           null!,
            themeService:      null!,
            fontResource:      new NullFontResourceService(),
            localization:      null!,
            autoLock:          null!,
            favicon:           null!,
            captureProtection: null!,
            autoBackup:        null!,
            notification:      null!,
            dialog:            null!,
            filePicker:        null!,
            auditLog:          null!,
            logger:            NullLogger<AppSettingsViewModel>.Instance);
}
