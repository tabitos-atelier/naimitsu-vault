// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace NaimitsuVault.Pages;

public sealed partial class TimeMachinePage : Page, ISearchFocusable, IRevealToggleable, IPageActivationAware
{
    public TimeMachineViewModel ViewModel { get; }
    private bool _isFirstLoad = true;
    private bool _isLeftPaneVisible = true;

    public TimeMachinePage()
    {
        var app = (App)Application.Current;
        // Registered as AddScoped, so obtain it from the session scope (disposed together with the scope on lock)
        ViewModel = app.ShellScopeServices!.GetRequiredService<TimeMachineViewModel>();
        InitializeComponent();
        Loaded += TimeMachinePage_Loaded;
        // With KeyDown (bubbling), when focus is in the right pane (RowsList), a child control can
        // set e.Handled=true first and shortcuts like Ctrl+B never arrive.
        // PreviewKeyDown (tunneling) is received by the root first, so it fires reliably.
        PreviewKeyDown += Page_KeyDown;
    }

    public void FocusSearchBox() => SearchBox.Focus(FocusState.Programmatic);

    public void ToggleAllRevealed()
    {
        if (ViewModel.SelectedSecret == null) return;
        ViewModel.IsRevealed = !ViewModel.IsRevealed;
    }

    private async void TimeMachinePage_Loaded(object sender, RoutedEventArgs e)
    {
        var session = ((App)Application.Current).Services.GetRequiredService<AppSession>();

        bool isPendingJump = session.PendingJumpSecretId.HasValue;
        int? jumpId = session.PendingJumpSecretId;
        session.PendingJumpSecretId = null;

        // The screen is narrow, so the left pane's default state depends on how we got here:
        // jumping in from SecretsPage (a specific secret's history) collapses it to give the
        // detail pane more room; arriving from the nav menu always shows it, resetting any
        // previous Ctrl+B collapse from an earlier visit (the page instance is cached and
        // _isLeftPaneVisible would otherwise carry over unchanged).
        SetLeftPaneVisible(!isPendingJump);

        // WARNING: keep isPendingJump included in needsLoad (a recurring known issue)
        // ────────────────────────────────────────────────────────────────
        // [Problem] A jump right after a save repeatedly caused a symptom where "selecting a
        //           different item and coming back shows the update."
        // [Cause] Save -> StorageChangedMessage -> NeedsReload=true is synchronous, but
        //         Jump -> NavigateToTagMessage -> DispatcherQueue.TryEnqueue is scheduled
        //         asynchronously, so depending on timing, Loaded can fire while NeedsReload is
        //         still false. In that case it enters the else branch (jump into an already-loaded
        //         list) and sets SelectedSecret to the existing object (same reference) from
        //         FilteredSecrets, so OnSelectedSecretChanged never fires, LoadSecretHistoryAsync
        //         is never called, and the history rows stay stale.
        // [Fix] Always run LoadAsync when isPendingJump=true.
        //       LoadAsync rebuilds the list (new objects), so setting SelectedSecret always
        //       triggers a reference change, guaranteeing OnSelectedSecretChanged fires.
        // [Do not remove] Excluding isPendingJump from needsLoad reintroduces the same bug.
        // ────────────────────────────────────────────────────────────────
        bool needsLoad = _isFirstLoad || ViewModel.NeedsReload || isPendingJump;
        if (!needsLoad) return;

        if (_isFirstLoad) _isFirstLoad = false;
        ViewModel.NeedsReload = false;

        await ViewModel.LoadAsync();
        // x:Bind SelectedValue only resolves against FilterComboItems at binding-establishment time (or
        // when FilterCategoryCode itself raises PropertyChanged) - it never automatically re-resolves
        // just because LoadAsync clears and rebuilds FilterComboItems. Re-sync to whatever
        // FilterCategoryCode currently is (falls back to "All" only when it's null/not found - e.g. a
        // true first load) instead of hard-resetting to index 0 on every reload, which used to wipe out
        // a user-selected category filter whenever NeedsReload/isPendingJump fired LoadAsync again
        // (same fix SecretsPage.SyncCategoryFilterSelection uses).
        SyncCategoryFilterSelection();

        if (jumpId.HasValue)
        {
            ViewModel.FilterFavoritesOnly = false;
            ViewModel.FilterDeletedOnly   = false;
            await Task.Yield();
            var target = ViewModel.FilteredSecrets.FirstOrDefault(s => s.Id == jumpId.Value);
            if (target != null)
            {
                ViewModel.SelectedSecret = target;
                SecretListView.ScrollIntoView(target);
            }
        }
        else if (ViewModel.SelectedSecret != null)
        {
            await Task.Yield();
            SecretListView.ScrollIntoView(ViewModel.SelectedSecret);
        }

        SecretListView.Focus(FocusState.Programmatic);
    }

    // ShellWindow.NavigateToTag assigns NavFrame.Content directly (not via Frame.Navigate), which
    // never raises OnNavigatedTo/OnNavigatedFrom - it calls these explicitly at the swap point instead.
    void IPageActivationAware.Activated() => ViewModel.Resume();
    void IPageActivationAware.Deactivated() => ViewModel.Pause();

    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is FilterCategoryItem item)
            ViewModel.FilterCategoryCode = item.Code;
    }

    // Re-resolves CategoryFilter's visual selection to match ViewModel.FilterCategoryCode after
    // LoadAsync() has cleared and rebuilt FilterComboItems. Falls back to index 0 ("All") when the
    // current code isn't found (e.g. a fresh load where FilterCategoryCode is still null).
    private void SyncCategoryFilterSelection()
    {
        var items = ViewModel.FilterComboItems;
        int idx = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Code == ViewModel.FilterCategoryCode) { idx = i; break; }
        }
        CategoryFilter.SelectedIndex = idx;
    }

    private async void DeletedFilter_Click(object sender, RoutedEventArgs e)
    {
        // ApplyFilter() (triggered synchronously by the TwoWay x:Bind above) rebuilds
        // FilteredSecrets and re-selects the same item by id when it survives the filter, but the
        // ListView's scroll position doesn't follow - the selection can end up off-screen with no
        // visual cue. Yield once so the rebuilt ItemsSource is laid out before scrolling to it.
        await Task.Yield();
        if (ViewModel.SelectedSecret != null)
            SecretListView.ScrollIntoView(ViewModel.SelectedSecret);
    }

    private async void StarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TimeMachineSecretItem item })
            await ViewModel.ToggleFavoriteForItemAsync(item);
    }

    private async void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();
        bool textFocused = FocusManager.GetFocusedElement(XamlRoot) is TextBox;

        // Suppress single keys (Insert/Delete, etc.) while editing a TextBox, but
        // let Ctrl-combined shortcuts work immediately even while searching (a symmetric rule shared across the app)
        if (textFocused && !ctrl) return;

        switch (e.Key)
        {
            case VirtualKey.Number1 when ctrl:
            case VirtualKey.NumberPad1 when ctrl:
                if (ViewModel.RestoreGen1Command.CanExecute(null))
                {
                    await ViewModel.RestoreGen1Command.ExecuteAsync(null);
                    e.Handled = true;
                    RestoreFocus();
                }
                break;
            case VirtualKey.Number2 when ctrl:
            case VirtualKey.NumberPad2 when ctrl:
                if (ViewModel.RestoreGen2Command.CanExecute(null))
                {
                    await ViewModel.RestoreGen2Command.ExecuteAsync(null);
                    e.Handled = true;
                    RestoreFocus();
                }
                break;
            case VirtualKey.Insert when !textFocused:
                if (ViewModel.UndeleteCommand.CanExecute(null))
                {
                    await ViewModel.UndeleteCommand.ExecuteAsync(null);
                    e.Handled = true;
                    RestoreFocus();
                }
                break;
            case VirtualKey.Delete when !textFocused:
                if (ViewModel.PermanentDeleteCommand.CanExecute(null))
                {
                    await ViewModel.PermanentDeleteCommand.ExecuteAsync(null);
                    e.Handled = true;
                    RestoreFocus();
                }
                break;
            case VirtualKey.Number5 when ctrl:
            case VirtualKey.NumberPad5 when ctrl:
            case VirtualKey.Space when !textFocused:
                if (ViewModel.SelectedSecret != null)
                {
                    await ViewModel.ToggleFavoriteCommand.ExecuteAsync(null);
                    e.Handled = true;
                }
                break;
            case VirtualKey.Number6 when ctrl:
            case VirtualKey.NumberPad6 when ctrl:
                if (ViewModel.HasFilteredFavorites)
                {
                    ViewModel.FilterFavoritesOnly = !ViewModel.FilterFavoritesOnly;
                    e.Handled = true;
                }
                break;
            case VirtualKey.Number9 when ctrl:
            case VirtualKey.NumberPad9 when ctrl:
                if (ViewModel.HasFilteredDeleted)
                {
                    ViewModel.FilterDeletedOnly = !ViewModel.FilterDeletedOnly;
                    e.Handled = true;
                }
                break;
            case VirtualKey.B when ctrl:
                ToggleLeftPane();
                e.Handled = true;
                break;
            case VirtualKey.J when ctrl:
                if (ViewModel.CanJumpToSecretList)
                {
                    RestoreFocus();
                    JumpToSecretList();
                    e.Handled = true;
                }
                break;
        }
    }

    private void SecretListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.J when !IsCtrlDown():
                if (SecretListView.SelectedIndex < SecretListView.Items.Count - 1)
                {
                    SecretListView.SelectedIndex++;
                    SecretListView.ScrollIntoView(SecretListView.SelectedItem);
                }
                e.Handled = true;
                break;
            case VirtualKey.K when !IsCtrlDown():
                if (SecretListView.SelectedIndex > 0)
                {
                    SecretListView.SelectedIndex--;
                    SecretListView.ScrollIntoView(SecretListView.SelectedItem);
                }
                e.Handled = true;
                break;
        }
    }

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e) => ToggleLeftPane();

    private void ToggleLeftPane()
    {
        SetLeftPaneVisible(!_isLeftPaneVisible);
        RestoreFocus();
    }

    private void SetLeftPaneVisible(bool visible)
    {
        if (_isLeftPaneVisible == visible) return;
        _isLeftPaneVisible         = visible;
        LeftPane.Visibility        = visible ? Visibility.Visible : Visibility.Collapsed;
        LeftColumnDef.Width        = visible ? new GridLength(240) : new GridLength(0);
        PaneToggleButton.IsChecked = !visible;
        PaneToggleGlyph.Glyph      = visible ? ((char)0xE740).ToString() : ((char)0xE73F).ToString();
    }

    // Forces focus to land somewhere concrete, preventing it from escaping into the void.
    // When the left pane is visible: SecretListView (lets J/K navigation continue as-is)
    // When the left pane is hidden: PaneToggleButton (stays resident in the right pane, never disappears)
    private void RestoreFocus()
        => (_isLeftPaneVisible ? (Control)SecretListView : PaneToggleButton)
            .Focus(FocusState.Programmatic);

    private void JumpToSecretList_Click(object sender, RoutedEventArgs e) => JumpToSecretList();

    private void JumpToSecretList()
    {
        if (!ViewModel.CanJumpToSecretList) return;
        WeakReferenceMessenger.Default.Send(new NavigateToTagMessage("secrets", ViewModel.SelectedSecret!.Id));
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
}
