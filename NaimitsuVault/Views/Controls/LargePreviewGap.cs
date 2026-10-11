// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace NaimitsuVault.Views.Controls;

/// <summary>
/// The group separator shared by the enlarged previews (<see cref="PasswordLargePreviewControl"/> and
/// <see cref="UserIdLargePreviewControl"/>), so both look the same.
///
/// It is an empty element about one character wide, not a space character: a text separator would be
/// included when the preview text is selected and copied, handing the user a value with extra spaces
/// in it. No string is created for it, so there is nothing to zero afterwards.
/// </summary>
internal static class LargePreviewGap
{
    // Advance width of one glyph in the preview's monospace font (Cascadia Code), relative to the font size.
    private const double CharWidthRatio = 0.6;

    internal static InlineUIContainer Create(double fontSize)
        => new() { Child = new Border { Width = fontSize * CharWidthRatio, Height = 1 } };
}
