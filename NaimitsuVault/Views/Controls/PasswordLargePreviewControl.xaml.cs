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

public sealed partial class PasswordLargePreviewControl : UserControl
{
    // Completely removed leaking the plaintext password into the property system via typeof(string).
    // Changed to a contract that accepts a PasswordToken (a reference wrapper around a POH-pinned buffer).
    // The SecureCharBuffer itself is never stored in the DependencyProperty; it's only referenced via the Token.
    public static readonly DependencyProperty PasswordTokenProperty =
        DependencyProperty.Register(nameof(PasswordToken), typeof(PasswordToken),
            typeof(PasswordLargePreviewControl),
            new PropertyMetadata(null, OnPasswordTokenChanged));

    public static readonly DependencyProperty IsVisibleProperty =
        DependencyProperty.Register(nameof(IsVisible), typeof(bool),
            typeof(PasswordLargePreviewControl),
            new PropertyMetadata(false, OnIsVisibleChanged));

    public PasswordToken? PasswordToken
    {
        get => (PasswordToken?)GetValue(PasswordTokenProperty);
        set => SetValue(PasswordTokenProperty, value);
    }

    public bool IsVisible
    {
        get => (bool)GetValue(IsVisibleProperty);
        set => SetValue(IsVisibleProperty, value);
    }

    public PasswordLargePreviewControl()
    {
        InitializeComponent();
        // ZeroMemory Run.Text on Unloaded, then Blocks.Clear().
        Unloaded += (_, _) => ZeroAndClearBlocks();
    }

    private static void OnPasswordTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordLargePreviewControl ctrl) return;
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
        if (d is not PasswordLargePreviewControl ctrl) return;
        if ((bool)e.NewValue)
        {
            var token = ctrl.PasswordToken;
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
    private void Render(ReadOnlySpan<char> password)
    {
        var res         = Application.Current.Resources;
        var digitBrush  = res["SystemControlHighlightAccentBrush"] as Brush;
        var isDark      = ActualTheme == ElementTheme.Dark;
        var symbolBrush = isDark ? res["SystemFillColorCautionBrush"] as Brush : _symbolBrushLight;
        var paragraph   = new Paragraph();
        for (int i = 0; i < password.Length; i++)
        {
            char c = password[i];
            var run = new Run { Text = new string(password.Slice(i, 1)) };
            if      (char.IsDigit(c))   run.Foreground = digitBrush;
            else if (!char.IsLetter(c)) run.Foreground = symbolBrush;
            paragraph.Inlines.Add(run);
        }
        ZeroAndClearBlocks(); // physically erase the old block's Run.Text before replacing it
        PreviewBlock.Blocks.Add(paragraph);
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
