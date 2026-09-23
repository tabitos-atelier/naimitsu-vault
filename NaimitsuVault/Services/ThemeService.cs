// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public class ThemeService : IThemeService
{
    private FrameworkElement? _root;

    public void SetRoot(FrameworkElement root) => _root = root;

    public void Apply(string themeMode)
    {
        var theme = themeMode switch
        {
            "Dark"  => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _       => ElementTheme.Default
        };
        var root = _root;
        if (root == null) return;
        if (root.DispatcherQueue?.HasThreadAccess == true)
            root.RequestedTheme = theme;
        else
            root.DispatcherQueue?.TryEnqueue(() => root.RequestedTheme = theme);
    }
}
