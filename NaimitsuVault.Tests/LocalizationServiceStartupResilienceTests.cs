// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Localization;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression coverage for a real-machine crash: LocalizationService.LoadForStartupAsync queried the
/// unified DB with no try/catch at all. When the unified DB is corrupted at startup with no usable
/// shadow, App.xaml.cs already detects this and sets IsUnifiedDbCorrupted to show a frozen UnlockWindow
/// with recovery guidance - but this call happens unconditionally right before that window is shown,
/// so the unhandled SqliteException propagated to OnLaunched's outermost catch, which logs Fatal and
/// calls Environment.Exit(1). Because NLog's targets are async, the Fatal line frequently never made
/// it to disk before the process died, making the app appear to silently vanish with no window and no
/// log trace at all - completely defeating the frozen-window design.
///
/// LocalizationManager holds static state, so tests are serialized via the SequentialLocale
/// collection.
/// </summary>
[Collection("SequentialLocale")]
public sealed class LocalizationServiceStartupResilienceTests
{
    [Fact]
    public async Task LoadForStartupAsync_UnifiedDbUnreachable_FallsBackWithoutThrowing()
    {
        var service = new LocalizationService(new ExplodingUnifiedDbContextFactory(), NullLogger<LocalizationService>.Instance);

        var ex = await Record.ExceptionAsync(() => service.LoadForStartupAsync());

        Assert.Null(ex);
        Assert.False(string.IsNullOrEmpty(service.CurrentLocale));
        Assert.False(service.HasCustomLocale, "No DB was reachable, so no custom locale can have been found");
    }

    [Fact]
    public async Task LoadForStartupAsync_UnifiedDbUnreachable_LocalizationManagerStillInitialized()
    {
        var service = new LocalizationService(new ExplodingUnifiedDbContextFactory(), NullLogger<LocalizationService>.Instance);

        await service.LoadForStartupAsync();

        // A key that must exist in the built-in locale (used by the frozen UnlockWindow itself) must
        // resolve to real text, not an uninitialized/empty LocalizationManager falling through to the
        // raw key name.
        var text = LocalizationManager.Get("Unlock.ErrorUnifiedDbCorrupted");
        Assert.False(string.IsNullOrEmpty(text));
        Assert.NotEqual("Unlock.ErrorUnifiedDbCorrupted", text);
    }
}
