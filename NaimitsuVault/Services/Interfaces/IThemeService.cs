// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml;

namespace NaimitsuVault.Services.Interfaces;

public interface IThemeService
{
    /// <summary>Sets the root element the theme is applied to. Call once before Apply().</summary>
    void SetRoot(FrameworkElement root);

    /// <summary>Applies the specified theme to the root element set via <see cref="SetRoot"/>.</summary>
    /// <param name="themeMode">
    /// <c>"Dark"</c> or <c>"Light"</c>; any other value (including <c>"Default"</c>) falls back to
    /// <see cref="ElementTheme.Default"/> (follow the OS theme).
    /// </param>
    void Apply(string themeMode);
}
