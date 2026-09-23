// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NaimitsuVault.Common;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace NaimitsuVault.Pages;

public sealed partial class GalleryPage : Page, ISearchFocusable, IPageActivationAware
{
    private static readonly ILogger<GalleryPage> Logger = AppLog.For<GalleryPage>();

    public GalleryViewModel ViewModel { get; }

    public void FocusSearchBox() => SearchBox.Focus(FocusState.Programmatic);

    public GalleryPage()
    {
        // AddScoped VMs are obtained from the session scope (disposed together with the scope on lock)
        ViewModel = ((App)Application.Current).ShellScopeServices!.GetRequiredService<GalleryViewModel>();
        InitializeComponent();
        ToolTipService.SetToolTip(GalleryGrid,
            LocalizationManager.Get("Common.DropTooltip", AppConstants.MaxFilesPerDrop, AppConstants.MaxFileSizeMb));
        Loaded += GalleryPage_Loaded;
        // PreviewKeyDown (tunneling) is received by the root first, so it fires reliably regardless
        // of whether GalleryGrid or any of its item containers currently holds keyboard focus - see
        // design/appendix/A-winui3-implementation-patterns.md section 40 for why a bubbling KeyDown
        // (which depends on that focus) is not reliable here. Same pattern as SecretsPage/TimeMachinePage.
        // CONFIRMED VIA LOG (2026-08-29): when a click leaves nothing focused anywhere in the page,
        // PreviewKeyDown/KeyDown never fire AT ALL for subsequent key presses - not even here. WinUI's
        // routed keyboard events require an existing focus target to route through; there is no way
        // to intercept a keystroke while focus is at zero. So this alone is not sufficient - something
        // must always stay focused (see GalleryGrid_Tapped/RootGrid_Tapped below).
        PreviewKeyDown += Page_KeyDown;
        // GridView's internal click-resolution (whichever stage it actually runs at - not observable
        // from outside) reliably wins any focus race fought within the same PointerPressed pass, and
        // even a DispatcherQueue-deferred retry from PointerPressed lost that race in practice. Tapped
        // fires only after the full press-release gesture (and GridView's own Tapped-based ItemClick
        // processing) has resolved, so hooking it - deferred one more pass via TryEnqueue for good
        // measure - is the last point at which our own focus call cannot be raced. handledEventsToo is
        // required: GridView marks its own Tapped Handled=true for presses that don't resolve to an
        // item, so a plain XAML Tapped="..." wiring would never fire here (same reasoning as the
        // AddHandler(KeyDownEvent, ...) call this project already uses elsewhere for Enter).
        GalleryGrid.AddHandler(UIElement.TappedEvent, new TappedEventHandler(GalleryGrid_Tapped), true);
    }

    // App.xaml only overrides SystemFillColorCriticalBrush/SystemFillColorCautionBrush in the Light
    // theme dictionary (dark mode keeps WinUI's own softer built-in reds/golds, #FF99A4/#FCE100,
    // which already read fine against a dark background - see App.xaml Technique 4/5). Mirrors
    // ProfilePage.GetAlertForeground / SecretsPage.GetExpiryAlertForeground (see design/03-05-01-00
    // §24 for why the light-mode value can't be used unconditionally). No "normal, no alert" branch
    // is needed here, unlike those two: this filter ToggleButton is only ever visible (bound to
    // ViewModel.HasCertExpiryAlert) while at least one certificate is alerting.
    private static readonly SolidColorBrush _certAlertForegroundLight   = new(Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));
    private static readonly SolidColorBrush _certCautionForegroundLight = new(Windows.UI.Color.FromArgb(0xFF, 0xB3, 0x85, 0x0F));

    internal Brush GetCertExpiryAlertForeground(bool isExpired)
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        return isExpired
            ? (isDark ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : _certAlertForegroundLight)
            : (isDark ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]  : _certCautionForegroundLight);
    }

    private async void GalleryPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        await Task.Yield();
        if (ViewModel.SelectedFile != null)
        {
            GalleryGrid.ScrollIntoView(ViewModel.SelectedFile);
            // Restoring a prior selection here doesn't change GalleryGrid's SelectedItem (it was
            // already set via the TwoWay x:Bind before Loaded fires), so SelectionChanged never
            // raises and LoadLinksAsync - normally sourced solely from that event - would never run,
            // leaving the right pane blank until the user picks a different file.
            await ViewModel.LoadLinksAsync(ViewModel.SelectedFile.Id);
        }
        else if (ViewModel.FileItems.Count > 0)
        {
            // Setting SelectedIndex here does not reliably raise SelectionChanged this early in the
            // page lifecycle (a first-launch-only WinUI3 timing quirk - the containers aren't fully
            // realized yet), so LoadLinksAsync must be called explicitly instead of relying on the
            // event. Read FileItems[0] directly rather than ViewModel.SelectedFile, since the TwoWay
            // x:Bind write-back for SelectedItem may not have completed synchronously either.
            GalleryGrid.SelectedIndex = 0;
            await ViewModel.LoadLinksAsync(ViewModel.FileItems[0].Id);
        }
        // Don't steal focus from the nav menu: with J/K keyboard browsing there, landing on this
        // page after each keystroke would otherwise yank focus into the grid after a single step.
        if (ViewModel.FileItems.Count > 0 && FocusManager.GetFocusedElement(XamlRoot) is not NavigationViewItem)
            GalleryGrid.Focus(FocusState.Programmatic);
    }

    // ShellWindow.NavigateToTag assigns NavFrame.Content directly (not via Frame.Navigate), which
    // never raises OnNavigatedTo/OnNavigatedFrom - it calls these explicitly at the swap point instead.
    // GalleryPage_Loaded already reloads unconditionally on every visit (NavFrame.Content = page
    // re-fires Loaded each time this cached instance is reattached), so Activated() only needs to
    // flip the IsActive flag - no separate NeedsReload-driven reload path is needed here.
    void IPageActivationAware.Activated() => ViewModel.Resume();
    void IPageActivationAware.Deactivated() => ViewModel.Pause();

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = LocalizationManager.Get("Common.DropToAttach");
        e.DragUIOverride.IsGlyphVisible = true;
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
                try { return new System.IO.FileInfo(p).Length <= AppConstants.MaxFileSizeBytes; }
                catch { return false; }
            }
            var oversizeCount = allPaths.Count(p => !SizeOk(p));
            var paths = allPaths
                .Where(SizeOk)
                .Take(AppConstants.MaxFilesPerDrop)
                .ToArray();
            if (paths.Length > 0)
                await ViewModel.AddFilesAsync(paths);
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
                "GalleryPage.Page_Drop failed. [{ExType}]", ex.GetType().Name);
        }
    }

    // A tap on genuinely blank chrome outside GalleryGrid (grid gaps, empty rows) - see RootGrid's
    // XAML: only un-backgrounded ancestors fall through to RootGrid, so a real control reports itself
    // as OriginalSource instead and is left alone here.
    private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, RootGrid))
            DispatcherQueue.TryEnqueue(RestoreGalleryFocus);
    }

    // A tap on the blank area inside GalleryGrid's own bounds (below/right of the thumbnails).
    // A tap that resolved to an actual item is deliberately excluded here: GridView already gives
    // that item's container pointer focus on its own, and ItemClick (fired for the same gesture)
    // opens the ViewerWindow. Re-running RestoreGalleryFocus afterward calls container.Focus() on
    // this page a moment later - a TryEnqueue callback that lands after the viewer's own Activate()/
    // SetForegroundWindow - which re-activates ShellWindow and pulls it back in front of the viewer
    // (same class of problem as the DoubleTapped-subwindow pitfall: a second-stage gesture callback
    // reactivating the shell after a subwindow already took the foreground). It also confused
    // WindowService.OpenViewer's open-viewer lookup into spawning a second window when the user,
    // seeing the viewer hidden behind Gallery, clicked the item again before the first one finished loading.
    private void GalleryGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsWithinItemContainer(e.OriginalSource as DependencyObject)) return;
        DispatcherQueue.TryEnqueue(RestoreGalleryFocus);
    }

    private bool IsWithinItemContainer(DependencyObject? node)
    {
        while (node != null && !ReferenceEquals(node, GalleryGrid))
        {
            if (node is GridViewItem) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // Try to focus the selected item's container first (falls back to the first item when nothing
    // is selected, without changing the selection) - this is what makes GridView's native arrow-key
    // navigation and the blue focus rectangle work again. But GridView's own internal focus/selection
    // bookkeeping for "this gesture didn't resolve to an item" cannot reliably be raced (confirmed:
    // neither a synchronous nor a DispatcherQueue-deferred container Focus() from PointerPressed
    // stuck), so verify afterward and, if the container focus didn't actually take, fall back to
    // RootGrid - a plain Grid, not a GridView descendant, so nothing internal to GridView contests it.
    // RootGrid can't be stolen from GridView's perspective; it just isn't the object we'd prefer.
    private void RestoreGalleryFocus()
    {
        var item = ViewModel.SelectedFile ?? (GalleryGrid.Items.Count > 0 ? GalleryGrid.Items[0] : null);
        if (item == null) return;
        // Explicitly (re-)select, not just focus: when nothing has been clicked yet (e.g. right
        // after a drop, with only one file in the gallery), SelectedFile can still be null here,
        // and focusing an unselected container alone leaves it with neither a visible selection
        // nor a keyboard focus rectangle.
        if (!ReferenceEquals(GalleryGrid.SelectedItem, item))
            GalleryGrid.SelectedItem = item;
        if (GalleryGrid.ContainerFromItem(item) is Control container)
            container.Focus(FocusState.Pointer);
        if (FocusManager.GetFocusedElement(XamlRoot) is not GridViewItem)
            RootGrid.Focus(FocusState.Pointer);
    }

    // All Gallery keyboard shortcuts live here (tunneling), not as a bubbling KeyDown on GalleryGrid:
    // GridView only ever gives keyboard focus to a realized item container, never to itself, and a
    // click on blank space (inside or outside the grid's own bounds) can leave nothing focused at
    // all - see design/appendix/A-winui3-implementation-patterns.md section 40 for the failed
    // attempts at chasing that focus. Acting on ViewModel.SelectedFile/GalleryGrid.SelectedIndex
    // directly sidesteps the whole problem: these shortcuts work no matter what currently has focus.
    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();
        bool textFocused = FocusManager.GetFocusedElement(XamlRoot) is TextBox;

        // Suppress single keys (Enter/Delete/J/K) while editing SearchBox, but let Ctrl-combined
        // shortcuts work immediately even while searching (a symmetric rule shared across the app).
        if (textFocused && !ctrl) return;

        switch (e.Key)
        {
            case VirtualKey.S when ctrl:
                if (ViewModel.SaveSelectedCommand.CanExecute(null))
                {
                    ViewModel.SaveSelectedCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case VirtualKey.B when ctrl:
                ToggleRightPane();
                e.Handled = true;
                break;
            case VirtualKey.Number8 when ctrl:
                if (ViewModel.HasAnyExpiryAlert)
                {
                    ViewModel.FilterExpiryOnly = !ViewModel.FilterExpiryOnly;
                    e.Handled = true;
                }
                break;
            case VirtualKey.Enter when !textFocused:
                if (ViewModel.SelectedFile != null)
                {
                    ViewModel.OpenViewerCommand.Execute(ViewModel.SelectedFile);
                    e.Handled = true;
                }
                break;
            case VirtualKey.Delete when !textFocused:
                if (ViewModel.SelectedFile != null)
                {
                    // Already-soft-deleted items (trash filter view) get purged permanently instead
                    // of re-running the soft-delete flow, which would be a no-op confirm dialog.
                    if (ViewModel.SelectedFile.IsDeleted)
                    {
                        if (ViewModel.PurgeSelectedCommand.CanExecute(null))
                            ViewModel.PurgeSelectedCommand.Execute(null);
                    }
                    else if (ViewModel.DeleteSelectedCommand.CanExecute(null))
                    {
                        ViewModel.DeleteSelectedCommand.Execute(null);
                    }
                    e.Handled = true;
                }
                break;
            case VirtualKey.J when !textFocused:
                if (GalleryGrid.SelectedIndex < GalleryGrid.Items.Count - 1)
                {
                    GalleryGrid.SelectedIndex++;
                    GalleryGrid.ScrollIntoView(GalleryGrid.SelectedItem);
                }
                e.Handled = true;
                break;
            case VirtualKey.K when !textFocused:
                if (GalleryGrid.SelectedIndex > 0)
                {
                    GalleryGrid.SelectedIndex--;
                    GalleryGrid.ScrollIntoView(GalleryGrid.SelectedItem);
                }
                e.Handled = true;
                break;
        }
    }

    // GalleryGrid's SelectionChanged is the sole trigger for LoadLinksAsync (eliminates double firing).
    // OnSelectedFileChanged does not call LoadLinksAsync.
    private async void GalleryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GalleryGrid.SelectedItem is FileItem selectedFile)
            await ViewModel.LoadLinksAsync(selectedFile.Id);
        else
            ViewModel.ClearLinks();
    }

    // A safety net for when focus returns to Gallery right after linking/pinning in the Viewer
    // (a separate window), and the real-time update via StorageChangedMessage hasn't caught up yet.
    // Since the selection hasn't changed, SelectionChanged doesn't fire, so re-scan explicitly on focus return.
    private async void GalleryGrid_GotFocus(object sender, RoutedEventArgs e)
    {
        if (GalleryGrid.SelectedItem is FileItem selectedFile)
            await ViewModel.LoadLinksAsync(selectedFile.Id);
    }

    // The DataTemplate uses x:DataType="vm:GalleryLinkItem" + x:Bind, so x:Bind handles data
    // assignment; only clear the plaintext on container reuse here.
    private void LinkedSecretsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue) return;
        if (args.ItemContainer.ContentTemplateRoot is Grid g &&
            g.Children.Count > 0 && g.Children[0] is HyperlinkButton hb &&
            hb.Content is TextBlock tb)
            tb.Text = string.Empty;
    }

    private void GalleryGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileItem item)
            ViewModel.OpenViewerCommand.Execute(item);
    }

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e) => ToggleRightPane();

    private void ToggleRightPane()
    {
        var col = RootGrid.ColumnDefinitions[2];
        bool willBeVisible = col.Width.Value <= 0;
        col.Width = willBeVisible ? new GridLength(260) : new GridLength(0);
        PaneDivider.Visibility = willBeVisible ? Visibility.Visible : Visibility.Collapsed;
        PaneToggleButton.IsChecked = !willBeVisible;
        PaneToggleGlyph.Glyph = willBeVisible ? ((char)0xE740).ToString() : ((char)0xE73F).ToString();
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
}
