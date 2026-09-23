// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NaimitsuVault.Localization;
using Windows.System;

namespace NaimitsuVault.Views;

public sealed partial class FontFamilyPickerDialog : ContentDialog
{
    private readonly string _systemDefaultLabel = LocalizationManager.Get("AppSettings.FontFamilySystemDefault");

    /// <summary>Set only after the OK button is pressed. Null means "system default".</summary>
    public string? SelectedFontFamily { get; private set; }

    /// <summary>True only when closed via the OK button. Needed because SelectedFontFamily is
    /// legitimately null for the "system default" choice, so it can't disambiguate confirm vs cancel by itself.</summary>
    public bool IsConfirmed { get; private set; }

    public FontFamilyPickerDialog(string[] fontFamilyOptions, string? currentFontFamily)
    {
        InitializeComponent();

        var items = new List<string> { _systemDefaultLabel };
        items.AddRange(fontFamilyOptions);
        FontListView.ItemsSource = items;
        var selected = currentFontFamily != null && fontFamilyOptions.Contains(currentFontFamily)
            ? currentFontFamily
            : _systemDefaultLabel;
        FontListView.SelectedItem = selected;
        FontListView.Loaded += (_, _) => FontListView.ScrollIntoView(selected);
    }

    private void OkButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var chosen = FontListView.SelectedItem as string;
        SelectedFontFamily = chosen == _systemDefaultLabel ? null : chosen;
        IsConfirmed = true;
        Hide();
    }

    private void CancelButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Hide();

    // ListView's built-in type-ahead would otherwise consume J/K to jump to items starting with
    // those letters (there are real font names starting with both). Handling them here first and
    // marking e.Handled repurposes J/K as vi-style up/down, matching the app's other list dialogs
    // (see UnlockWindow's vault selector).
    private void FontListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        int step = e.Key switch
        {
            VirtualKey.J => 1,
            VirtualKey.K => -1,
            _ => 0,
        };
        if (step == 0) return;

        var items = FontListView.Items;
        int next = FontListView.SelectedIndex + step;
        if (next < 0 || next >= items.Count) { e.Handled = true; return; }

        FontListView.SelectedIndex = next;
        FontListView.ScrollIntoView(items[next]);
        e.Handled = true;
    }
}
