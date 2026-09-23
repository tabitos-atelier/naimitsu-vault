// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public class FontResourceService : IFontResourceService
{
    private const string ResourceKey = "ContentControlThemeFontFamily";
    private const string SystemDefault = "XamlAutoFontFamily";
    private static readonly string[] ThemeKeys = ["Default", "Light", "Dark"];

    public void Apply(string? fontFamily)
    {
        var value = new FontFamily(string.IsNullOrEmpty(fontFamily) ? SystemDefault : fontFamily);
        var themeDictionaries = Application.Current.Resources.ThemeDictionaries;
        foreach (var key in ThemeKeys)
        {
            if (themeDictionaries.TryGetValue(key, out var dict) && dict is ResourceDictionary rd)
                rd[ResourceKey] = value;
        }
    }
}
