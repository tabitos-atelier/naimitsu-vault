// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using NaimitsuVault.Helpers;
using NaimitsuVault.Services;
using Windows.UI;

namespace NaimitsuVault.Views.Controls;

// Deliberately not shared with PasswordLargePreviewControl despite the near-identical secure-buffer
// rendering scaffolding: the digit-grouping display transform below is specific to UserId and would
// only add branching to the password path, which has no use for it.
public sealed partial class UserIdLargePreviewControl : UserControl
{
    // Same contract as PasswordLargePreviewControl.PasswordToken: never store the SecureCharBuffer
    // itself in the DependencyProperty, only a reference to it via the token.
    public static readonly DependencyProperty UserIdTokenProperty =
        DependencyProperty.Register(nameof(UserIdToken), typeof(PasswordToken),
            typeof(UserIdLargePreviewControl),
            new PropertyMetadata(null, OnUserIdTokenChanged));

    public static readonly DependencyProperty IsVisibleProperty =
        DependencyProperty.Register(nameof(IsVisible), typeof(bool),
            typeof(UserIdLargePreviewControl),
            new PropertyMetadata(false, OnIsVisibleChanged));

    public PasswordToken? UserIdToken
    {
        get => (PasswordToken?)GetValue(UserIdTokenProperty);
        set => SetValue(UserIdTokenProperty, value);
    }

    public bool IsVisible
    {
        get => (bool)GetValue(IsVisibleProperty);
        set => SetValue(IsVisibleProperty, value);
    }

    public UserIdLargePreviewControl()
    {
        InitializeComponent();
        // ZeroMemory Run.Text on Unloaded, then Blocks.Clear().
        Unloaded += (_, _) => ZeroAndClearBlocks();
    }

    private static void OnUserIdTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UserIdLargePreviewControl ctrl) return;
        if (!ctrl.IsVisible) return;
        // Pass the POH-pinned buffer's span directly to Render (never through a string)
        var token = (PasswordToken?)e.NewValue;
        if (token is not null)
            ctrl.Render(token.Buffer.Span);
        else
            ctrl.ZeroAndClearBlocks();
    }

    private static void OnIsVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UserIdLargePreviewControl ctrl) return;
        if ((bool)e.NewValue)
        {
            var token = ctrl.UserIdToken;
            if (token is not null)
                ctrl.Render(token.Buffer.Span);
            ctrl.PreviewBorder.Visibility = Visibility.Visible;
        }
        else
        {
            ctrl.PreviewBorder.Visibility = Visibility.Collapsed;
            // Physically erase Run.Text with ZeroMemory before Blocks.Clear().
            ctrl.ZeroAndClearBlocks();
        }
    }

    private static readonly SolidColorBrush _symbolBrushLight =
        new(Color.FromArgb(255, 234, 88, 12));

    // Argument changed to ReadOnlySpan<char>, eliminating string-burst production via c.ToString().
    // Because Run.Text requires a string due to the WinUI 3 API's constraints,
    // generate a tiny single-character string from a slice via new string(span.Slice(i, 1)).
    // These single-character strings are physically erased with ZeroMemory in ZeroAndClearBlocks().
    //
    // When the value is entirely digits (e.g. a credit card number), spaces are inserted for
    // readability using the digit-grouping pattern GetDigitGroups resolves for it (brand-specific for
    // a recognized Amex/Diners Club number, 4-4-4-... otherwise). This is a display-only transform:
    // the underlying buffer is untouched, and the inserted separator is a freshly allocated
    // (non-interned) string so it is safe to ZeroMemory alongside the real digit runs.
    private void Render(ReadOnlySpan<char> userId)
    {
        var res         = Application.Current.Resources;
        var digitBrush  = res["SystemControlHighlightAccentBrush"] as Brush;
        var isDark      = ActualTheme == ElementTheme.Dark;
        var symbolBrush = isDark ? res["SystemFillColorCautionBrush"] as Brush : _symbolBrushLight;
        var paragraph   = new Paragraph();

        bool isAllDigits = userId.Length > 0;
        for (int i = 0; i < userId.Length && isAllDigits; i++)
            if (!char.IsDigit(userId[i])) isAllDigits = false;

        var groups = isAllDigits ? GetDigitGroups(userId) : null;

        for (int i = 0; i < userId.Length; i++)
        {
            if (groups is not null && i > 0 && IsGroupBoundary(i, groups))
                paragraph.Inlines.Add(new Run { Text = new string(' ', 1) });

            char c = userId[i];
            var run = new Run { Text = new string(userId.Slice(i, 1)) };
            if      (char.IsDigit(c))   run.Foreground = digitBrush;
            else if (!char.IsLetter(c)) run.Foreground = symbolBrush;
            paragraph.Inlines.Add(run);
        }
        ZeroAndClearBlocks(); // physically erase the old block's Run.Text before replacing it
        PreviewBlock.Blocks.Add(paragraph);
    }

    // Card-brand digit-grouping: only kicks in once the digit count matches a known brand's total
    // length exactly, per the brand's IIN (prefix) ranges - a partial, still-being-typed number just
    // keeps the plain 4-digit grouping until it resolves to a recognized length+prefix pair, then the
    // display reformats itself on the next render (there's no separate "typing" mode to maintain).
    private static readonly int[] _amexGroups   = [4, 6, 5]; // 15 digits: 3xxx xxxxxx xxxxx
    private static readonly int[] _dinersGroups = [4, 6, 4]; // 14 digits: 3xxx xxxxxx xxxx
    private static readonly int[] _uniformGroup = [4];       // everything else: xxxx xxxx xxxx ...

    private static int[] GetDigitGroups(ReadOnlySpan<char> digits)
    {
        if (digits.Length == 15 && (digits.StartsWith("34") || digits.StartsWith("37")))
            return _amexGroups;
        if (digits.Length == 14 && IsDinersClubPrefix(digits))
            return _dinersGroups;
        return _uniformGroup;
    }

    // Diners Club International IIN ranges: 36, 38-39, 300-305, 3095.
    private static bool IsDinersClubPrefix(ReadOnlySpan<char> digits)
    {
        if (digits.StartsWith("36") || digits.StartsWith("38") || digits.StartsWith("39")) return true;
        if (digits.StartsWith("3095")) return true;
        if (digits.StartsWith("30") && digits[2] is >= '0' and <= '5') return true;
        return false;
    }

    // groups.Length == 1 repeats that single group size for the whole value (the uniform fallback);
    // otherwise it's a fixed brand-specific sequence (e.g. Amex's 4-6-5) with no repeat past the end.
    private static bool IsGroupBoundary(int index, int[] groups)
    {
        if (groups.Length == 1) return index % groups[0] == 0;
        int cumulative = 0;
        foreach (var size in groups)
        {
            cumulative += size;
            if (index == cumulative) return true;
            if (index < cumulative) return false;
        }
        return false;
    }

    /// <summary>
    /// Physically erases each Run.Text inside the RichTextBlock with ZeroMemory, then calls Blocks.Clear().
    /// A plain Blocks.Clear() only severs the "visual pointer" - it does nothing to stop the
    /// single-character string objects held by Run.Text from drifting on the heap.
    /// </summary>
    private void ZeroAndClearBlocks()
    {
        foreach (var block in PreviewBlock.Blocks)
        {
            if (block is not Paragraph p) continue;
            foreach (var inline in p.Inlines)
            {
                if (inline is not Run r) continue;
                var s = r.Text;
                r.Text = string.Empty; // break Run's strong reference first
                if (!string.IsNullOrEmpty(s))
                    SecurePasswordHelper.ZeroStringInternals(s);
            }
        }
        PreviewBlock.Blocks.Clear();
    }
}
