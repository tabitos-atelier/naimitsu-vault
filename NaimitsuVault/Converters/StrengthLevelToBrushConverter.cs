// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NaimitsuVault.Converters;

public class StrengthLevelToBrushConverter : IValueConverter
{
    // Matches the zxcvbn score (0-4) terminology used by SecretsViewModel.PasswordStrengthText
    // (Common.StrengthVeryWeak/Weak/Fair/Strong/VeryStrong). VeryWeak and Weak share the same
    // red - only Fair/Strong/VeryStrong get a distinct color.
    private static readonly SolidColorBrush WeakBrush       = new(Color.FromArgb(0xFF, 0xE7, 0x48, 0x56)); // red
    private static readonly SolidColorBrush FairBrush       = new(Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00)); // orange
    private static readonly SolidColorBrush StrongBrush     = new(Color.FromArgb(0xFF, 0x90, 0xB9, 0x00)); // yellow-green
    private static readonly SolidColorBrush VeryStrongBrush = new(Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F)); // green (same value as SystemFillColorSuccessBrush)

    private static readonly SolidColorBrush[] _brushes =
    [
        WeakBrush,       // 0: very weak
        WeakBrush,       // 1: weak
        FairBrush,       // 2: fair
        StrongBrush,     // 3: strong
        VeryStrongBrush, // 4: very strong
    ];

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is int i && i >= 0 && i < _brushes.Length ? _brushes[i] : _brushes[0];

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
