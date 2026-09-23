// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml.Data;

namespace NaimitsuVault.Converters;

/// <summary>Dims an element to signal it's disabled when the bound field has no value (e.g. an editable label whose target field is empty).</summary>
public class EmptyToDisabledOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string s && !string.IsNullOrEmpty(s) ? 1.0 : 0.4;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
