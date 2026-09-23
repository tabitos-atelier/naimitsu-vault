// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Localization;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// Tests for SecretEditModel.CategoryComboSelection - the display-only fallback that shows
/// "Uncategorized" for a CategoryNum that doesn't match any currently defined preset (e.g. a code
/// left over from before the 2026-08-17 preset renumbering, like the old 4-digit 1100), without
/// ever rewriting the underlying CategoryNum.
/// LocalizationManager holds static state, so tests are serialized via the SequentialLocale collection.
/// </summary>
[Collection("SequentialLocale")]
public sealed class SecretEditModelCategoryFallbackTests
{
    private static void InitializePresets() =>
        LocalizationManager.Initialize(
            new Dictionary<string, string>
            {
                ["Categories.01"] = "Login",
                ["Categories.10"] = "Finance",
                ["Categories.Uncategorized"] = "Uncategorized",
            }, []);

    [Fact]
    public void CategoryComboSelection_KnownCode_ReturnsThatCode()
    {
        InitializePresets();
        using var model = new SecretEditModel { CategoryNum = 10 };

        Assert.Equal(10, model.CategoryComboSelection);
    }

    [Fact]
    public void CategoryComboSelection_UnknownLegacyCode_FallsBackToZero()
    {
        InitializePresets();
        using var model = new SecretEditModel { CategoryNum = 1100 };

        Assert.Equal(0, model.CategoryComboSelection);
        // The underlying value must not be mutated by merely reading the fallback.
        Assert.Equal(1100, model.CategoryNum);
    }

    [Fact]
    public void CategoryComboSelection_NullCategoryNum_FallsBackToZero()
    {
        InitializePresets();
        using var model = new SecretEditModel { CategoryNum = null };

        Assert.Equal(0, model.CategoryComboSelection);
    }

    [Fact]
    public void CategoryComboSelection_ZeroIsAlwaysValid_EvenWithoutPresets()
    {
        LocalizationManager.Initialize([], []);
        using var model = new SecretEditModel { CategoryNum = 0 };

        Assert.Equal(0, model.CategoryComboSelection);
    }

    [Fact]
    public void CategoryComboSelection_Setter_WritesThroughToCategoryNum()
    {
        InitializePresets();
        using var model = new SecretEditModel { CategoryNum = 1100 };

        model.CategoryComboSelection = 10;

        Assert.Equal(10, model.CategoryNum);
        Assert.Equal(10, model.CategoryComboSelection);
    }
}
