// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NaimitsuVault.Localization;
using NaimitsuVault.Services.Interfaces;
using Windows.UI;

namespace NaimitsuVault.Services;

/// <summary>
/// Builds the preview content for the TimeMachine generation swap dialog.
/// </summary>
internal static class TimeMachineSwapContentBuilder
{
    internal static UIElement Build(TimeMachineSwapPreview p, bool isDark)
    {
        // Flag icon (E7C1), UpdateRestore icon (E777)
        const string flagGlyph = "\uE7C1";
        const string histGlyph = "\uE777";

        // Explicit colors per theme (not dependent on ThemeResource)
        // Light mode: fixed royal blue. AccentAAFillColorDefaultBrush's ThemeDictionary lookup gets
        // dragged along by the app theme at dialog-creation time, so a literal value is used instead.
        Brush accentBrush = isDark
            ? (Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var ab) && ab is Brush abr
                ? abr : new SolidColorBrush(Color.FromArgb(255, 0, 102, 204)))
            : new SolidColorBrush(Color.FromArgb(255, 0, 102, 204)); // #0066CC
        Brush primaryText = new SolidColorBrush(isDark ? Color.FromArgb(255, 230, 230, 230) : Color.FromArgb(255,  30,  30,  30));
        Brush dimText     = new SolidColorBrush(isDark ? Color.FromArgb(170, 200, 200, 200) : Color.FromArgb(170,  80,  80,  80));
        Brush cardBg      = new SolidColorBrush(isDark ? Color.FromArgb(255,  48,  48,  48) : Color.FromArgb(255, 246, 246, 246));
        Brush neutralBdr  = new SolidColorBrush(isDark ? Color.FromArgb( 50, 210, 210, 210) : Color.FromArgb( 50,   0,   0,   0));

        var gen2Header = p.Gen2Header ?? "−";
        var currentLabel = LocalizationManager.Get("Common.Current");

        var beforeRow = MakeCardRow(
            (flagGlyph, null, currentLabel, p.CurrentHeader, false),
            (histGlyph, "1",  "G1",         p.Gen1Header,    false),
            (histGlyph, "2",  "G2",         gen2Header,      false),
            accentBrush, primaryText, dimText, cardBg, neutralBdr);

        Grid afterRow;
        if (p.TargetGen == 1)
        {
            afterRow = MakeCardRow(
                (histGlyph, "1",  "G1",         p.Gen1Header,    true),
                (flagGlyph, null, currentLabel, p.CurrentHeader, true),
                (histGlyph, "2",  "G2",         gen2Header,      false),
                accentBrush, primaryText, dimText, cardBg, neutralBdr);
        }
        else
        {
            afterRow = MakeCardRow(
                (histGlyph, "2",  "G2",         gen2Header,      true),
                (flagGlyph, null, currentLabel, p.CurrentHeader, true),
                (histGlyph, "1",  "G1",         p.Gen1Header,    true),
                accentBrush, primaryText, dimText, cardBg, neutralBdr);
        }

        var root = new StackPanel { Spacing = 6, MinWidth = 340 };
        root.Children.Add(MakeLabel(LocalizationManager.Get("TimeMachine.Dialog.BeforeSwap"), dimText));
        root.Children.Add(beforeRow);
        root.Children.Add(MakeSeparator(dimText));
        root.Children.Add(MakeLabel(LocalizationManager.Get("TimeMachine.Dialog.AfterSwap"), dimText));
        root.Children.Add(afterRow);
        return root;
    }

    private static Grid MakeCardRow(
        (string glyph, string? badge, string label, string header, bool accent) c0,
        (string glyph, string? badge, string label, string header, bool accent) c1,
        (string glyph, string? badge, string label, string header, bool accent) c2,
        Brush accentBrush, Brush primaryText, Brush dimText, Brush cardBg, Brush neutralBdr)
    {
        var cards = new[] { c0, c1, c2 };
        var grid = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++)
        {
            var card = MakeCard(cards[i], accentBrush, primaryText, dimText, cardBg, neutralBdr);
            Grid.SetColumn(card, i);
            grid.Children.Add(card);
        }
        return grid;
    }

    private static Border MakeCard(
        (string glyph, string? badge, string label, string header, bool accent) c,
        Brush accentBrush, Brush primaryText, Brush dimText, Brush cardBg, Brush neutralBdr)
    {
        Brush iconFg  = c.accent ? accentBrush : dimText;
        Brush labelFg = c.accent ? accentBrush : primaryText;

        var iconGrid = new Grid { Width = 24, Height = 18 };
        iconGrid.Children.Add(new FontIcon
        {
            Glyph = c.glyph, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Center,
            Foreground = iconFg,
        });
        if (c.badge != null)
        {
            var badgeBorder = new Border
            {
                Background = c.accent ? accentBrush : dimText,
                CornerRadius = new CornerRadius(5),
                Height = 12, MinWidth = 12, Padding = new Thickness(2, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Bottom,
            };
            badgeBorder.Child = new TextBlock
            {
                Text = c.badge, FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            iconGrid.Children.Add(badgeBorder);
        }

        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(iconGrid);
        stack.Children.Add(new TextBlock { Text = c.label,  FontSize = 12, Foreground = labelFg });
        stack.Children.Add(new TextBlock { Text = c.header, FontSize = 10, Foreground = dimText, TextTrimming = TextTrimming.CharacterEllipsis });

        return new Border
        {
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(8, 6, 8, 6),
            Background      = cardBg,
            BorderThickness = c.accent ? new Thickness(1.5) : new Thickness(1),
            BorderBrush     = c.accent ? accentBrush : neutralBdr,
            Child           = stack,
        };
    }

    private static TextBlock MakeLabel(string text, Brush brush) =>
        new() { Text = text, FontSize = 11, Foreground = brush, Margin = new Thickness(0, 2, 0, 0) };

    private static UIElement MakeSeparator(Brush brush)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var line1 = new Border { Height = 1, Background = brush, Opacity = 0.4, VerticalAlignment = VerticalAlignment.Center };
        var arrow = new FontIcon { Glyph = "\uE70D", FontSize = 13, Foreground = brush, Margin = new Thickness(6, 0, 6, 0) }; // ChevronDown
        var line2 = new Border { Height = 1, Background = brush, Opacity = 0.4, VerticalAlignment = VerticalAlignment.Center };

        Grid.SetColumn(line1, 0); Grid.SetColumn(arrow, 1); Grid.SetColumn(line2, 2);
        grid.Children.Add(line1); grid.Children.Add(arrow); grid.Children.Add(line2);
        return grid;
    }
}
