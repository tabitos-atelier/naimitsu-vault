// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml.Data;

namespace NaimitsuVault.Converters;

// Drives the enlarge-preview toggle buttons' glyph (UserIdEnlargeToggle, the custom text field's
// enlarge toggle in RecordExtrasControl, ProfilePage's IdNEnlargeToggle): ZoomIn while the preview is
// closed, ZoomOut once it's open. Accepts both bool (classic {Binding} to CustomFieldModel.IsRevealed)
// and bool? (x:Bind to a ToggleButton's own IsChecked) - a null/unset value falls back to ZoomIn.
public class BoolToZoomGlyphConverter : IValueConverter
{
    private const string ZoomInGlyph  = "\uE8A3";
    private const string ZoomOutGlyph = "\uE71F";

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? ZoomOutGlyph : ZoomInGlyph;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
