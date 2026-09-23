// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NaimitsuVault.Common;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Views.Controls;
using NaimitsuVault.ViewModels;
using NaimitsuVault.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NaimitsuVault.Pages;

public sealed partial class ProfilePage : Page, IRevealToggleable, IPageActivationAware
{
    private static readonly ILogger<ProfilePage> Logger = AppLog.For<ProfilePage>();

    public ProfileViewModel ViewModel { get; }

    // ProfilePage has NavigationCacheMode="Required", so the same page instance (and the same
    // CalendarDatePicker/TextBox visual tree) survives across navigations and Loaded refires on
    // every revisit. Without this guard, AttachLostFocusRecursive would run again on each revisit
    // and stack a fresh set of handlers on top of the ones from every earlier visit.
    private bool _lostFocusAttached;

    public ProfilePage()
    {
        var app = (App)Application.Current;
        // Registered as AddScoped, so obtain it from the session scope (disposed together with the scope on lock)
        ViewModel = app.ShellScopeServices!.GetRequiredService<ProfileViewModel>();
        InitializeComponent();
        Loaded  += ProfilePage_Loaded;
        KeyDown += Page_KeyDown;
        // Unlike SecretsPage/EditingSecret, ProfileViewModel is never swapped out for a different
        // instance mid-session - it's one Scoped ViewModel for the page's whole lifetime - so a
        // single subscription for the page's lifetime is enough; no per-load attach/detach needed.
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    // Called by ShellWindow.NavigateToTag around the NavFrame.Content swap (Page.OnNavigatedTo/From never fire here).
    void IPageActivationAware.Activated()   => ViewModel.Resume();
    void IPageActivationAware.Deactivated() => ViewModel.Pause();

    private async void ProfilePage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        // Attach LostFocus to all elements once the visual tree is settled (first visit only - see _lostFocusAttached)
        if (!_lostFocusAttached)
        {
            _lostFocusAttached = true;
            AttachLostFocusRecursive(this);
        }
        // NavigationCacheMode="Required" means this same page instance (and its enlarge toggles) can
        // still be showing a previous visit's open state on revisit - reset them, same as SecretsPage
        // does for its Password/UserId enlarge toggles.
        Id1EnlargeToggle.IsChecked = false;
        Id2EnlargeToggle.IsChecked = false;
        Id3EnlargeToggle.IsChecked = false;
        Id1LargePreview.IsVisible  = false;
        Id2LargePreview.IsVisible  = false;
        Id3LargePreview.IsVisible  = false;
    }

    // Reflects IdentityItem1-3 changes (typed via the x:Bind TwoWay TextBox, or external) into each
    // field's enlarge-preview panel while it's open. Mirrors SecretsPage's Secret_PasswordChanged.
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IdentityItem1) && Id1LargePreview.IsVisible)
            Id1LargePreview.UserIdToken = ViewModel.IdentityItem1Token;
        else if (e.PropertyName == nameof(ViewModel.IdentityItem2) && Id2LargePreview.IsVisible)
            Id2LargePreview.UserIdToken = ViewModel.IdentityItem2Token;
        else if (e.PropertyName == nameof(ViewModel.IdentityItem3) && Id3LargePreview.IsVisible)
            Id3LargePreview.UserIdToken = ViewModel.IdentityItem3Token;
    }

    private void Id1EnlargeToggle_Click(object sender, RoutedEventArgs e)
        => ApplyId1Enlarge(Id1EnlargeToggle.IsChecked == true);

    private void Id2EnlargeToggle_Click(object sender, RoutedEventArgs e)
        => ApplyId2Enlarge(Id2EnlargeToggle.IsChecked == true);

    private void Id3EnlargeToggle_Click(object sender, RoutedEventArgs e)
        => ApplyId3Enlarge(Id3EnlargeToggle.IsChecked == true);

    private void ApplyId1Enlarge(bool show)
    {
        if (show) Id1LargePreview.UserIdToken = ViewModel.IdentityItem1Token;
        Id1LargePreview.IsVisible = show;
    }

    private void ApplyId2Enlarge(bool show)
    {
        if (show) Id2LargePreview.UserIdToken = ViewModel.IdentityItem2Token;
        Id2LargePreview.IsVisible = show;
    }

    private void ApplyId3Enlarge(bool show)
    {
        if (show) Id3LargePreview.UserIdToken = ViewModel.IdentityItem3Token;
        Id3LargePreview.IsVisible = show;
    }

    private void AttachLostFocusRecursive(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox tb)
                tb.LostFocus += (_, _) =>
                {
                    // Same fix as SecretsPage: the Notes TextBox's x:Bind TwoWay write-back
                    // (NotesTextBox.Text -> RecordExtrasControl.Notes -> ViewModel.Notes) may not
                    // have landed yet when LostFocus fires, so AutoSaveDraftCoreAsync's BuildEditModel()
                    // could read a stale value. Sync it directly first (harmless no-op for any other TextBox).
                    ViewModel.Notes = RecordExtras.CurrentNotesText;
                    // Gate on HasUnsavedChanges (mirrors SecretsPage's LosingFocus handler): without
                    // this, a blur on ANY field - even one nobody touched - unconditionally re-ran the
                    // full TwinA comparison, which falsely reported a mismatch whenever an unedited
                    // AddCustomField placeholder was sitting in CustomFields (its mere presence changes
                    // the field count vs TwinA even though it hasn't been edited yet).
                    if (!ViewModel.HasUnsavedChanges) return;
                    _ = ViewModel.AutoSaveDraftAsync("LosingFocus");
                };
            else if (child is CalendarDatePicker cdp)
            {
                // The flyout is a separate popup, so picking a date doesn't reliably raise LostFocus on
                // the picker itself - without this, the new date sits dirty until some unrelated focus
                // change happens to fire LostFocus afterward.
                cdp.LostFocus += (_, _) =>
                {
                    if (!ViewModel.HasUnsavedChanges) return;
                    _ = ViewModel.AutoSaveDraftAsync("DatePickerLostFocus");
                };
                // DateChanged itself is wired from XAML (IdExpiryPicker_DateChanged below), not here -
                // see that method's comment for why registration order matters for this control.
            }
            AttachLostFocusRecursive(child);
        }
    }

    // Wired via XAML (DateChanged="IdExpiryPicker_DateChanged" on Id1/2/3ExpiryPicker), not attached
    // dynamically like AttachLostFocusRecursive's other handlers above. This one has to win the race
    // against x:Bind's own TwoWay write-back: a handler added here during InitializeComponent() (i.e.
    // via the XAML attribute) is registered on CalendarDatePicker.DateChanged BEFORE x:Bind's generated
    // Bindings.Update() wires its own write-back handler on the same event, so ViewModel.IdNExpiry
    // below still holds the PRE-change value when a genuine pick fires this. A handler attached later
    // (as this used to be, from Page_Loaded via AttachLostFocusRecursive) runs AFTER that write-back has
    // already landed, so the comparison below always saw NewDate == the (already-updated) ViewModel
    // value and bailed out unconditionally - the date changed correctly in the model, but no draft was
    // ever written from this handler, leaving it to whatever unrelated LostFocus fired next.
    private void IdExpiryPicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        // x:Bind TwoWay re-pushes the bound value into these pickers on every LoadAsync (e.g. a
        // revisit that refreshes ViewModel.IdNExpiry from a value changed elsewhere), which fires this
        // same event even though nothing was actually picked here - same binding-echo pitfall as
        // ExpiresAtPicker_DateChanged in SecretsPage.xaml.cs.
        var current = sender.Name switch
        {
            nameof(Id1ExpiryPicker) => ViewModel.Id1Expiry,
            nameof(Id2ExpiryPicker) => ViewModel.Id2Expiry,
            nameof(Id3ExpiryPicker) => ViewModel.Id3Expiry,
            _ => (DateTimeOffset?)null,
        };
        if (args.NewDate == current) return;
        _ = ViewModel.AutoSaveDraftAsync("DateChanged");
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        if (e.Key == Windows.System.VirtualKey.S && ctrl && ViewModel.SaveCommand.CanExecute(null))
        {
            ViewModel.SaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.E && ctrl && ViewModel.HasDraft)
        {
            _ = ShowCompareDialogAsync();
            e.Handled = true;
        }
    }

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
        => await ShowCompareDialogAsync();

    private async Task ShowCompareDialogAsync()
    {
        await ViewModel.AutoSaveDraftAsync("CompareDraftPreSync");
        var (items, twinBSavedAt, twinAUpdatedAt, gen0Avatar, draftAvatar, avatarChanged, gen0Files, draftFiles) = await ViewModel.BuildCompareItemsAsync();
        var shellTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;

        var content = new ProfileDraftCompareContent
        {
            CompareItems     = items,
            TwinBSavedAt     = twinBSavedAt,
            TwinAUpdatedAt    = twinAUpdatedAt,
            Gen0AvatarBytes  = gen0Avatar,
            DraftAvatarBytes = draftAvatar,
            AvatarChanged    = avatarChanged,
            Gen0Files        = gen0Files,
            DraftFiles       = draftFiles,
        };

        var dialog = new ContentDialog
        {
            Content        = content,
            XamlRoot       = XamlRoot,
            RequestedTheme = shellTheme,
        };
        dialog.Resources["ContentDialogMinWidth"] = 860d;
        dialog.Resources["ContentDialogMaxWidth"] = 900d;

        content.CloseRequested   += (_, _) => dialog.Hide();
        content.DiscardRequested += async (_, _) =>
        {
            dialog.Hide();
            await DiscardDraftAsync();
        };

        // Register with IWindowService so a Ctrl+L (or auto-lock) mid-compare deliberately tears this
        // dialog down before ShellWindow is scrubbed/closed, instead of leaving DisposeAll()'s
        // ZeroMemory dependent on however Window.Close() happens to unwind an open ContentDialog.
        var windowService = ((App)Application.Current).Services.GetRequiredService<IWindowService>();
        var dialogClosedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> closer = () =>
        {
            dialog.Hide();
            return dialogClosedTcs.Task; // Hide()'s return doesn't mean DisposeAll() below has run yet
        };
        windowService.RegisterActiveDialogCloser(closer);
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            windowService.UnregisterActiveDialogCloser(closer); // must run on every exit path, or the closure leaks in the singleton
            content.DisposeAll(); // Ensure CompareRowItem is ZeroMemory'd even when closed by something other than a button, e.g. Escape
            dialogClosedTcs.TrySetResult();
        }
    }

    private async Task DiscardDraftAsync()
    {
        // No confirmation dialog: discarding a draft only reverts unsaved edits (same as SecretsPage),
        // so it doesn't warrant the same protection as an irreversible delete.
        await ViewModel.DiscardDraftAndReloadAsync();
        var app = (App)Application.Current;
        var notification = app.Services.GetRequiredService<IAppNotificationService>();
        notification.Show(LK.Common_SuccessSaveComplete, LK.Common_InfoDeleteComplete, NotificationSeverity.Info, TimeSpan.FromSeconds(3));
    }

    public void ToggleAllRevealed()
    {
        // All fields that carry a reveal/enlarge toggle (password fields mask/unmask; text-plain
        // fields only enlarge - see CustomFieldModel.ShowAsTextPlain). Date/Url fields have neither.
        var toggleItems = ViewModel.CustomFields.Where(cf => cf.IsPassword || cf.ShowAsTextPlain).ToList();
        bool allRevealed = Id1EnlargeToggle.IsChecked == true
            && Id2EnlargeToggle.IsChecked == true
            && Id3EnlargeToggle.IsChecked == true
            && toggleItems.All(cf => cf.IsRevealed);
        bool next = !allRevealed;
        Id1EnlargeToggle.IsChecked = next;
        ApplyId1Enlarge(next);
        Id2EnlargeToggle.IsChecked = next;
        ApplyId2Enlarge(next);
        Id3EnlargeToggle.IsChecked = next;
        ApplyId3Enlarge(next);
        foreach (var cf in toggleItems)
            cf.IsRevealed = next;
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = LocalizationManager.Get("Common.DropToAttach");
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var allPaths = items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).ToArray();
            bool SizeOk(string p)
            {
                try { return new FileInfo(p).Length <= AppConstants.MaxFileSizeBytes; }
                catch { return false; }
            }
            var oversizeCount = allPaths.Count(p => !SizeOk(p));
            var paths = allPaths
                .Where(SizeOk)
                .Take(AppConstants.MaxFilesPerDrop)
                .ToArray();
            if (paths.Length > 0)
                await ViewModel.AddFilesCommand.ExecuteAsync(paths);
            if (oversizeCount > 0)
            {
                var notification = ((App)Application.Current).Services.GetRequiredService<IAppNotificationService>();
                notification.Show(LK.Common_Warning,
                    LocalizationManager.Get("Common.InfoOversizeFilesSkipped", oversizeCount, AppConstants.MaxFileSizeMb),
                    NotificationSeverity.Warning, TimeSpan.FromSeconds(4));
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                "ProfilePage.Page_Drop failed. [{ExType}]", ex.GetType().Name);
        }
    }

    private async void PickAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).ActiveShellWindow!);
            InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            byte[]? rawData = null;
            byte[] normalizedData;
            try
            {
                rawData = await File.ReadAllBytesAsync(file.Path);
                // Bake the EXIF rotation into the pixels, so the displayed coordinates match the crop coordinates.
                normalizedData = await ImageHelper.NormalizeExifAsync(rawData);
            }
            finally
            {
                // NormalizeExifAsync always returns a new array, so rawData can be zero-cleared
                // unconditionally once we're past it - including on exception (a corrupt/malformed
                // image), so the picked face photo's raw bytes (biometric PII) never linger un-zeroed.
                if (rawData != null) CryptographicOperations.ZeroMemory(rawData.AsSpan());
            }

            // Passing normalizedData (movable heap) across control boundaries is forbidden.
            // Immediately wipe the movable-heap copy right after transcribing it into a POH-pinned SecureByteBuffer.
            // To avoid letting raw pixels drift on the movable heap while the dialog is open (the
            // seconds-to-minutes the user spends adjusting the crop position), all subsequent crop
            // processing also references secureImageBuf.PinnedArray (the same POH allocation).
            using var secureImageBuf = new SecureByteBuffer();
            try
            {
                secureImageBuf.SetFromSpan(normalizedData.AsSpan());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(normalizedData.AsSpan());
            }

            var cropControl = new AvatarCropControl();
            var shellTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;

            // Custom footer (Cancel left, OK right) instead of PrimaryButtonText/CloseButtonText, so the
            // confirm action is consistently on the right like the app's other non-destructive dialogs.
            var okButton = new Button
            {
                Content   = LocalizationManager.Get("Common.Ok"),
                Style     = (Style)Application.Current.Resources["AccentButtonStyle"],
                IsEnabled = false,
            };
            var cancelButton = new Button { Content = LocalizationManager.Get("Common.Cancel") };
            var footer = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing             = 8,
                Children            = { cancelButton, okButton },
            };
            var contentPanel = new StackPanel { Spacing = 12, Children = { cropControl, footer } };

            var dialog = new ContentDialog
            {
                Title          = LocalizationManager.Get("Profile.Dialog.AvatarCrop"),
                Content        = contentPanel,
                XamlRoot       = XamlRoot,
                RequestedTheme = shellTheme,
            };
            bool confirmed = false;
            okButton.Click    += (_, _) => { confirmed = true; dialog.Hide(); };
            cancelButton.Click += (_, _) => dialog.Hide();

            // Call SetImageAsync after the visual tree is connected (after Loaded).
            // Calling it before ContentDialog.ShowAsync() leaves Image disconnected and it won't render.
            // Loaded can fire more than once around ContentDialog's transient Unloaded; SetImageAsync serializes
            // the calls and re-applies the image from the original buffer each time.
            // async void lambda: an uncaught exception would reach the global handler as a FATAL and leave the
            // crop image blank with OK disabled, so catch it here, log it, and close the dialog with an error.
            cropControl.Loaded += async (_, _) =>
            {
                try
                {
                    await cropControl.SetImageAsync(secureImageBuf);
                    okButton.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("ProfilePage: failed to load the image into the crop dialog. [{ExType}]", ex.GetType().Name);
                    dialog.Hide();
                    var notification = ((App)Application.Current).Services.GetRequiredService<IAppNotificationService>();
                    notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
                }
            };

            try   { await dialog.ShowAsync(); }
            finally { cropControl.Scrub(); } // the real teardown; the control's Unloaded ignores the transient one at open
            if (!confirmed)
            {
                // secureImageBuf is auto-ZeroMemory'd when the using scope ends
                return;
            }

            var (cropX, cropY, cropSize) = cropControl.GetCropInfo();
            var croppedBytes = await ImageHelper.CreateAvatarWithCropAsync(secureImageBuf.PinnedArray!, cropX, cropY, (uint)cropSize);
            // secureImageBuf is auto-ZeroMemory'd when the using scope ends
            try   { await ViewModel.SetAvatarFromBytesAsync(croppedBytes); }
            finally { CryptographicOperations.ZeroMemory(croppedBytes.AsSpan()); } // the VM already wiped it first - a double wipe is harmless
        }
        catch (Exception ex)
        {
            // async void: an uncaught exception here would tear down the whole process (e.g. the
            // picked file gets locked/deleted mid-read, or EXIF decoding fails on a corrupt image).
            Logger.LogWarning("ProfilePage.PickAvatarButton_Click failed. [{ExType}]", ex.GetType().Name);
            var notification = ((App)Application.Current).Services.GetRequiredService<IAppNotificationService>();
            notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
    }

    // No confirmation dialog: the draft compare/discard flow can restore the avatar afterward,
    // so this doesn't warrant the same protection as an irreversible delete.
    private async void ClearAvatarButton_Click(object sender, RoutedEventArgs e)
        => await ViewModel.ClearAvatarCommand.ExecuteAsync(null);

    // App.xaml only overrides SystemFillColorCriticalBrush/SystemFillColorCautionBrush in the Light
    // theme dictionary (dark mode keeps WinUI's own softer built-in reds/golds, #FF99A4/#FCE100,
    // which already read fine against a dark background - see App.xaml Technique 4/5). Using the
    // fixed light-mode value unconditionally in dark mode made these alerts look jarringly saturated
    // next to the title bar's expiry icon (ShellWindow._criticalBrushLight/_cautionBrushLight), which
    // already branched on theme. Branch here too instead of hardcoding the light value.
    private static readonly SolidColorBrush _alertForegroundLight   = new(Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));
    private static readonly SolidColorBrush _cautionForegroundLight = new(Windows.UI.Color.FromArgb(0xFF, 0xB3, 0x85, 0x0F));
    // Equivalent to WinUI 3's TextFillColorPrimaryBrush (chosen based on ActualTheme)
    // Looking it up via Application.Current.Resources resolves based on RequestedTheme, which can
    // diverge from the actual theme, so reference ActualTheme directly instead.
    private static readonly SolidColorBrush _normalFgLight = new(Windows.UI.Color.FromArgb(0xE4,   0,   0,   0));
    private static readonly SolidColorBrush _normalFgDark  = new(Windows.UI.Color.FromArgb(0xFF, 255, 255, 255));

    // isExpired (already past due, critical/red) takes precedence over hasAlert (still within the
    // warning window, caution/amber) - both can be true at once since hasAlert's window includes the
    // already-expired case.
    internal Brush GetAlertForeground(bool hasAlert, bool isExpired)
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        if (isExpired) return isDark ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : _alertForegroundLight;
        if (hasAlert)  return isDark ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]  : _cautionForegroundLight;
        return isDark ? _normalFgDark : _normalFgLight;
    }

    internal Visibility AlertVisibility(bool hasAlert)
        => hasAlert ? Visibility.Visible : Visibility.Collapsed;

    // Tap-to-edit label pattern for the identity item rows (IdentityItem1-3), mirroring
    // SecretsPage's LabelUserId/LabelPassword/etc. editable labels.
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
        editor.SelectAll();
    }

    private void LabelEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox editor || editor.Parent is not Grid parent) return;
        // Clearing the label (e.g. via the TextBox's built-in delete button) must not leave the row
        // with no visible label at all, so fall back to the field's default name (stashed in Tag).
        if (string.IsNullOrEmpty(editor.Text) && editor.Tag is string defaultLabel)
            editor.Text = defaultLabel;
        editor.Visibility = Visibility.Collapsed;
        var display = parent.Children.OfType<TextBlock>().FirstOrDefault();
        if (display is not null)
            display.Visibility = Visibility.Visible;
        // Also reset the hover display on focus loss (guards against a leftover image after pointer capture is released)
        var border = parent.Children.OfType<Border>().FirstOrDefault();
        if (border is not null) border.Background = null;
    }

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

    // Countermeasure for the issue where, after an external app like Snipping Tool takes focus,
    // WinUI 3 restores focus to the first TextBox and the ScrollViewer jumps back to the top.
    // Called from ShellWindow_Activated.
    private double _savedScrollOffset;

    internal void SaveScrollPosition()
        => _savedScrollOffset = MainScrollViewer.VerticalOffset;

    internal void RestoreScrollPosition()
    {
        var offset = _savedScrollOffset;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
            () => MainScrollViewer.ChangeView(null, offset, null, disableAnimation: true));
    }
}
