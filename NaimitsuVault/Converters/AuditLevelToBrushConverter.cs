// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NaimitsuVault.Converters;

public class AuditLevelToBrushConverter : IValueConverter
{
    // Mirrors AuditLog.EventLevel (see Models/AuditLog.cs): 0=info, 1=warning, 2=critical.
    private const int LevelInfo     = 0;
    private const int LevelWarning  = 1;
    private const int LevelCritical = 2;

    // Application.Current.RequestedTheme does not reflect the app's actual displayed theme (the one
    // ThemeService sets on ShellWindow.RequestedTheme) — it stays at the OS default and never changes.
    // Application.Current.Resources.TryGetValue(themedKey) also always returns the "Default" dictionary
    // (dark-equivalent values), so displaying in light mode would result in near-white colors, a
    // washed-out red, and an overly bright yellow, breaking visibility. Therefore all levels use fixed
    // colors with values whose contrast was measured and confirmed for both light and dark themes.
    // Warning (#B3850F, gold) is kept more than 90° apart in hue from Error (#E81123, red) to prevent misreading.
    private static readonly SolidColorBrush InfoBrush =
        new(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush WarningBrush =
        new(Color.FromArgb(0xFF, 0xB3, 0x85, 0x0F));
    private static readonly SolidColorBrush CriticalBrush =
        new(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var level = value is int i ? Math.Clamp(i, LevelInfo, LevelCritical) : LevelInfo;
        return level switch
        {
            LevelWarning  => WarningBrush,
            LevelCritical => CriticalBrush,
            _             => InfoBrush,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
