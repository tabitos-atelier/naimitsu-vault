// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NaimitsuVault.Common;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Views.Controls;

// Shared tail section (custom fields / attached files / notes / created-updated timestamps) used
// by both SecretsPage and ProfilePage. Everything below the record's own core fields (Secrets:
// title through expiry; Profile: basic info through identity documents) has an identical layout,
// so it lives here once instead of being duplicated per page.
public sealed partial class RecordExtrasControl : UserControl
{
    private readonly IWindowService _windowService;
    private bool _suppressCustomFieldChanged;

    // Self-reference for the custom field ListView's Tag (used by DataTemplate buttons to reach
    // this control's command DPs via ElementName; a bare "{x:Bind}" self-binding is not used here).
    public RecordExtrasControl ThisControl => this;

    public RecordExtrasControl()
    {
        InitializeComponent();
        _windowService = ((App)Application.Current).Services.GetRequiredService<IWindowService>();
        ToolTipService.SetToolTip(AttachedFilesSection,
            LocalizationManager.Get("Common.DropTooltip", AppConstants.MaxFilesPerDrop, AppConstants.MaxFileSizeMb));
    }

    public static readonly DependencyProperty CustomFieldsProperty =
        DependencyProperty.Register(nameof(CustomFields), typeof(ObservableCollection<CustomFieldModel>),
            typeof(RecordExtrasControl), new PropertyMetadata(null));

    public ObservableCollection<CustomFieldModel>? CustomFields
    {
        get => (ObservableCollection<CustomFieldModel>?)GetValue(CustomFieldsProperty);
        set => SetValue(CustomFieldsProperty, value);
    }

    public static readonly DependencyProperty AddCustomFieldCommandProperty =
        DependencyProperty.Register(nameof(AddCustomFieldCommand), typeof(ICommand),
            typeof(RecordExtrasControl), new PropertyMetadata(null));

    public ICommand? AddCustomFieldCommand
    {
        get => (ICommand?)GetValue(AddCustomFieldCommandProperty);
        set => SetValue(AddCustomFieldCommandProperty, value);
    }

    public static readonly DependencyProperty RemoveCustomFieldCommandProperty =
        DependencyProperty.Register(nameof(RemoveCustomFieldCommand), typeof(ICommand),
            typeof(RecordExtrasControl), new PropertyMetadata(null));

    public ICommand? RemoveCustomFieldCommand
    {
        get => (ICommand?)GetValue(RemoveCustomFieldCommandProperty);
        set => SetValue(RemoveCustomFieldCommandProperty, value);
    }

    public static readonly DependencyProperty CopyCustomFieldCommandProperty =
        DependencyProperty.Register(nameof(CopyCustomFieldCommand), typeof(ICommand),
            typeof(RecordExtrasControl), new PropertyMetadata(null));

    public ICommand? CopyCustomFieldCommand
    {
        get => (ICommand?)GetValue(CopyCustomFieldCommandProperty);
        set => SetValue(CopyCustomFieldCommandProperty, value);
    }

    public static readonly DependencyProperty OpenCustomFieldUrlCommandProperty =
        DependencyProperty.Register(nameof(OpenCustomFieldUrlCommand), typeof(ICommand),
            typeof(RecordExtrasControl), new PropertyMetadata(null));

    public ICommand? OpenCustomFieldUrlCommand
    {
        get => (ICommand?)GetValue(OpenCustomFieldUrlCommandProperty);
        set => SetValue(OpenCustomFieldUrlCommandProperty, value);
    }

    public static readonly DependencyProperty AttachedFilesProperty =
        DependencyProperty.Register(nameof(AttachedFiles), typeof(ObservableCollection<FileItem>),
            typeof(RecordExtrasControl), new PropertyMetadata(null));

    public ObservableCollection<FileItem>? AttachedFiles
    {
        get => (ObservableCollection<FileItem>?)GetValue(AttachedFilesProperty);
        set => SetValue(AttachedFilesProperty, value);
    }

    public static readonly DependencyProperty RemoveFileCommandProperty =
        DependencyProperty.Register(nameof(RemoveFileCommand), typeof(ICommand),
            typeof(RecordExtrasControl), new PropertyMetadata(null));

    public ICommand? RemoveFileCommand
    {
        get => (ICommand?)GetValue(RemoveFileCommandProperty);
        set => SetValue(RemoveFileCommandProperty, value);
    }

    public static readonly DependencyProperty FilesMaxHeightProperty =
        DependencyProperty.Register(nameof(FilesMaxHeight), typeof(double),
            typeof(RecordExtrasControl), new PropertyMetadata(160d));

    public double FilesMaxHeight
    {
        get => (double)GetValue(FilesMaxHeightProperty);
        set => SetValue(FilesMaxHeightProperty, value);
    }

    public static readonly DependencyProperty NotesProperty =
        DependencyProperty.Register(nameof(Notes), typeof(string),
            typeof(RecordExtrasControl), new PropertyMetadata(string.Empty));

    public string Notes
    {
        get => (string)GetValue(NotesProperty);
        set => SetValue(NotesProperty, value);
    }

    /// <summary>
    /// Reads the Notes TextBox's current text directly, bypassing the x:Bind TwoWay chain
    /// (NotesTextBox.Text -> Notes DP -> the host page's ViewModel property). On the very first
    /// Tab out of the field, the page's LosingFocus-driven autosave can run before that chain has
    /// finished propagating, so callers doing a focus-out autosave should read this instead of
    /// trusting the bound ViewModel value to already be current.
    /// </summary>
    public string CurrentNotesText => NotesTextBox.Text;

    public static readonly DependencyProperty CreatedAtTextProperty =
        DependencyProperty.Register(nameof(CreatedAtText), typeof(string),
            typeof(RecordExtrasControl), new PropertyMetadata(string.Empty));

    public string CreatedAtText
    {
        get => (string)GetValue(CreatedAtTextProperty);
        set => SetValue(CreatedAtTextProperty, value);
    }

    public static readonly DependencyProperty UpdatedAtTextProperty =
        DependencyProperty.Register(nameof(UpdatedAtText), typeof(string),
            typeof(RecordExtrasControl), new PropertyMetadata(string.Empty));

    public string UpdatedAtText
    {
        get => (string)GetValue(UpdatedAtTextProperty);
        set => SetValue(UpdatedAtTextProperty, value);
    }

    // Custom field label hover highlight (3-layer Border + TextBlock + collapsed TextBox; see
    // SecretsPage.xaml for why the simpler single-TextBox EditableLabelStyle can't be used here)
    private static readonly SolidColorBrush _labelHoverLight =
        new(Windows.UI.Color.FromArgb(0x2C, 0, 0, 0));
    private static readonly SolidColorBrush _labelHoverDark =
        new(Windows.UI.Color.FromArgb(0x2C, 255, 255, 255));

    private void LabelGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid g) return;
        var border = g.Children.OfType<Border>().FirstOrDefault();
        if (border is not null)
            border.Background = ActualTheme == ElementTheme.Dark ? _labelHoverDark : _labelHoverLight;
    }

    private void LabelGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid g) return;
        var border = g.Children.OfType<Border>().FirstOrDefault();
        if (border is not null)
            border.Background = null;
    }

    private void LabelDisplay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not TextBlock display || display.Parent is not Grid parent) return;
        var editor = parent.Children.OfType<TextBox>().FirstOrDefault();
        if (editor is null) return;
        // On tap, the pointer gets captured by the TextBox and PointerExited stops firing, so reset it here
        var border = parent.Children.OfType<Border>().FirstOrDefault();
        if (border is not null) border.Background = null;
        display.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        editor.Focus(FocusState.Programmatic);
    }

    private void LabelEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox editor || editor.Parent is not Grid parent) return;
        // Clearing the label (e.g. via the TextBox's built-in delete button) must not leave the row
        // with no visible label at all, so fall back to the field's type-based default name (mirrors
        // SecretsPage.xaml.cs's LabelEditor_LostFocus for the main fields, which falls back to a
        // Tag-stashed constant - a custom field has no single fixed default the way Title/UserId do,
        // so this derives it from FieldType instead). Setting cf.Label directly (not just
        // editor.Text) guards against the Text->Label TwoWay write-back not having landed yet.
        if (string.IsNullOrEmpty(editor.Text) && editor.DataContext is CustomFieldModel cf)
        {
            var defaultLabel = cf.FieldType.GetDefaultLabel();
            editor.Text = defaultLabel;
            cf.Label = defaultLabel;
        }
        editor.Visibility = Visibility.Collapsed;
        var display = parent.Children.OfType<TextBlock>().FirstOrDefault();
        if (display is not null)
            display.Visibility = Visibility.Visible;
        // Also reset the hover display on focus loss (guards against a leftover image after pointer capture is released)
        var border = parent.Children.OfType<Border>().FirstOrDefault();
        if (border is not null) border.Background = null;
    }

    // The overlaid copy button and the built-in TextBox delete ("x") button occupy the same
    // spot on the right edge. WinUI 3's delete button only appears while the box has focus and
    // contains text, so hide our copy button on focus and restore it on blur to avoid overlap.
    private void CopyOverlayTextBox_GotFocus(object sender, RoutedEventArgs e) =>
        SetSiblingCopyOverlayVisibility(sender, Visibility.Collapsed);

    private void CopyOverlayTextBox_LostFocus(object sender, RoutedEventArgs e) =>
        SetSiblingCopyOverlayVisibility(sender, Visibility.Visible);

    private static void SetSiblingCopyOverlayVisibility(object sender, Visibility visibility)
    {
        if (sender is not FrameworkElement fe || fe.Parent is not Grid grid) return;
        int column = Grid.GetColumn(fe);
        var button = grid.Children.OfType<Button>()
            .FirstOrDefault(b => Grid.GetColumn(b) == column && Equals(b.Tag, "CopyOverlay"));
        if (button is not null)
            button.Visibility = visibility;
    }

    private void CustomPasswordField_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            _suppressCustomFieldChanged = true;
            pb.Password = (e.NewValue as CustomFieldModel)?.Value ?? string.Empty;
            _suppressCustomFieldChanged = false;
        }
    }

    // Fires on every keystroke (not just LostFocus), mirroring the main Secret Password field's
    // PasswordField_Changed/PasswordFieldRevealed_Changed in SecretsPage.xaml.cs: while the reveal
    // toggle is on, PasswordLargePreviewControl binds to CustomFieldModel.PasswordToken, which
    // SetValueDirect only re-raises when called - so gating this to LostFocus (the field's earlier
    // behavior) left the large preview frozen at whatever was typed before the box last lost focus
    // instead of following the live input like the main Password field's preview does.
    private void CustomPasswordField_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressCustomFieldChanged) return;
        if (sender is PasswordBox pb && pb.DataContext is CustomFieldModel field)
            SecurePasswordHelper.Borrow(pb, field.SetValueDirect);
    }

    private void CustomDateField_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        if (sender is CalendarDatePicker picker)
        {
            _suppressCustomFieldChanged = true;
            picker.Date = (e.NewValue as CustomFieldModel)?.DateValue;
            _suppressCustomFieldChanged = false;
        }
    }

    private void CustomDateField_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs e)
    {
        if (_suppressCustomFieldChanged) return;
        if (sender.DataContext is CustomFieldModel field)
            field.DateValue = e.NewDate;
    }

    private void AttachedFile_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileItem item)
            _windowService.OpenViewer(item.Id);
    }

    private void RemoveAttachedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileItem item })
            RemoveFileCommand?.Execute(item);
    }
}
