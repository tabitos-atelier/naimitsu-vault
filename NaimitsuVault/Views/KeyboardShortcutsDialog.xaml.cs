// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NaimitsuVault.Localization;

namespace NaimitsuVault.Views;

public sealed partial class KeyboardShortcutsDialog : ContentDialog
{
    public KeyboardShortcutsDialog()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;

        Title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { Glyph = "\uE92E", VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = LocalizationManager.Get("Common.KeyboardShortcuts"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
