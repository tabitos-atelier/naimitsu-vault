// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Localization;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression coverage (2026-09-18) for a bug in LocalizationManager.Initialize()'s _byCode
/// population: it computed the value via two separate Dictionary.TryGetValue calls
/// (_fallback.TryGetValue then _messages.TryGetValue "primary overrides"), but TryGetValue assigns
/// its out parameter to default(T) on a miss even when it doesn't find the key. A key present only
/// in the fallback locale had its value silently wiped to null by the second, failing lookup, so it
/// was never registered in _byCode at all - GetById(code) for such a key always returned the
/// "[0x...]" not-found placeholder. Fixed to the same short-circuit || pattern Get(string) already used.
/// LocalizationManager holds static state, so tests are serialized via the SequentialLocale collection.
/// </summary>
[Collection("SequentialLocale")]
public sealed class LocalizationManagerGetByIdTests
{
    [Fact]
    public void GetById_KeyOnlyInFallback_ResolvesToFallbackValue()
    {
        LocalizationManager.Initialize(
            new Dictionary<string, string> { ["Common.Ok"] = "OK" },
            new Dictionary<string, string> { ["Common.Ok"] = "OK", ["Common.Cancel"] = "Cancel" });

        var code = LocalizationManager.GetCode("Common.Cancel");

        Assert.Equal("Cancel", LocalizationManager.GetById(code));
    }

    [Fact]
    public void GetById_KeyInBoth_PrimaryOverridesFallback()
    {
        LocalizationManager.Initialize(
            new Dictionary<string, string> { ["Common.Ok"] = "Custom OK" },
            new Dictionary<string, string> { ["Common.Ok"] = "OK" });

        var code = LocalizationManager.GetCode("Common.Ok");

        Assert.Equal("Custom OK", LocalizationManager.GetById(code));
    }

    [Fact]
    public void GetById_KeyOnlyInPrimary_ResolvesToPrimaryValue()
    {
        LocalizationManager.Initialize(
            new Dictionary<string, string> { ["Common.Ok"] = "OK", ["Common.Retry"] = "Retry" },
            new Dictionary<string, string> { ["Common.Ok"] = "OK" });

        var code = LocalizationManager.GetCode("Common.Retry");

        Assert.Equal("Retry", LocalizationManager.GetById(code));
    }
}
