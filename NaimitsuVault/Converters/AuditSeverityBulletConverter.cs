// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml.Data;

namespace NaimitsuVault.Converters;

public class AuditSeverityBulletConverter : IValueConverter
{
    // All three severity levels share the same filled-circle glyph; AuditLevelToBrushConverter's
    // color (blue/gold/red) is what distinguishes them, not the shape.
    private const string CircleFillGlyph = "\uEA3B";

    public object Convert(object value, Type targetType, object parameter, string language)
        => CircleFillGlyph;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
