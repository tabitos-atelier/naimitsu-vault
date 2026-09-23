// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace NaimitsuVault.Converters;

public class BoolToPasswordRevealModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is PasswordRevealMode m && m == PasswordRevealMode.Visible;
}
