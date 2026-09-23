// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NaimitsuVault.Converters;

public class BoolToHighlightBrushConverter : IValueConverter
{
    // Highlighter-marker gold for a "changed" cell - the same #FFD700 used for the favorite star, at
    // an alpha bright enough in each theme to read as a proper highlight rather than a faint wash.
    // Light needs more alpha than dark to reach the same perceived vividness: mixing with a white
    // background dilutes saturation far more per unit alpha than mixing with a near-black one.
    private static readonly SolidColorBrush HighlightBrushLight = new(Color.FromArgb(0xB0, 255, 215, 0));
    private static readonly SolidColorBrush HighlightBrushDark  = new(Color.FromArgb(0x85, 255, 215, 0));
    private static readonly SolidColorBrush TransparentBrush = new(Color.FromArgb(0, 0, 0, 0));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not bool b || !b) return TransparentBrush;

        // Unlike AuditLevelToBrushConverter, this does not read Application.Current.RequestedTheme
        // (documented elsewhere as stuck at the OS default, never reflecting the app's actual displayed
        // theme) or Application.Current.Resources[key] (always resolves the wrong theme dictionary from
        // a converter). It instead reads ActualTheme off the live ShellWindow content, the same
        // technique this app's Window/Page code-behind already relies on for theme-correct brushes.
        var isDark = (((App)Application.Current).ActiveShellWindow?.Content as FrameworkElement)?.ActualTheme
            == ElementTheme.Dark;
        return isDark ? HighlightBrushDark : HighlightBrushLight;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
