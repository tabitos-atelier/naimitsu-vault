// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Localization;

namespace NaimitsuVault.Tests;

/// <summary>
/// Tests for LocalizationManager.GetCategoryPresets() - categories have no DB table, so this is the
/// sole source of truth for "which category codes exist and what they're named."
/// LocalizationManager holds static state, so tests are serialized via the SequentialLocale collection.
/// </summary>
[Collection("SequentialLocale")]
public sealed class LocalizationManagerCategoryPresetsTests
{
    [Fact]
    public void GetCategoryPresets_ReturnsCodesSortedAscending()
    {
        LocalizationManager.Initialize(
            new Dictionary<string, string>
            {
                ["Categories.99"] = "Other",
                ["Categories.01"] = "Login",
                ["Categories.10"] = "Finance",
            }, []);

        var presets = LocalizationManager.GetCategoryPresets();

        Assert.Equal([1, 10, 99], presets.Select(p => p.Code));
        Assert.Equal(["Login", "Finance", "Other"], presets.Select(p => p.Name));
    }

    [Fact]
    public void GetCategoryPresets_PrimaryLocale_OverridesFallbackForSameCode()
    {
        LocalizationManager.Initialize(
            new Dictionary<string, string> { ["Categories.01"] = "Custom Login" },
            new Dictionary<string, string> { ["Categories.01"] = "Login" });

        var presets = LocalizationManager.GetCategoryPresets();

        Assert.Equal([(1, "Custom Login")], presets);
    }

    [Fact]
    public void GetCategoryPresets_UnionsCodesFromBothPrimaryAndFallback()
    {
        // A custom (primary) locale that only defines a subset still sees the fallback's
        // remaining codes - presets are a union, not a strict override of the whole set.
        LocalizationManager.Initialize(
            new Dictionary<string, string> { ["Categories.01"] = "Custom Login" },
            new Dictionary<string, string> { ["Categories.01"] = "Login", ["Categories.10"] = "Finance" });

        var presets = LocalizationManager.GetCategoryPresets();

        Assert.Equal([(1, "Custom Login"), (10, "Finance")], presets);
    }

    [Theory]
    [InlineData("Categories.Uncategorized")] // not a 2-digit suffix
    [InlineData("Categories.1")]              // 1 digit
    [InlineData("Categories.100")]            // 3 digits
    [InlineData("Categories.AB")]             // not numeric
    public void GetCategoryPresets_IgnoresNonTwoDigitNumericKeys(string key)
    {
        LocalizationManager.Initialize(
            new Dictionary<string, string> { [key] = "Should not appear", ["Categories.01"] = "Login" }, []);

        var presets = LocalizationManager.GetCategoryPresets();

        Assert.Equal([(1, "Login")], presets);
    }

    [Fact]
    public void GetCategoryPresets_NoDefinedKeys_ReturnsEmpty()
    {
        LocalizationManager.Initialize(new Dictionary<string, string> { ["Common.Ok"] = "OK" }, []);

        Assert.Empty(LocalizationManager.GetCategoryPresets());
    }
}
