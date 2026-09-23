// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using NaimitsuVault.Common;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using NaimitsuVault.Views;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;

namespace NaimitsuVault.Pages;

public sealed partial class SecretsPage : Page, ISearchFocusable, IRevealToggleable, IPageActivationAware
{
    private static readonly ILogger<SecretsPage> Logger = AppLog.For<SecretsPage>();
    public SecretsViewModel ViewModel { get; }
    private readonly IAutoTypeService _autoTypeService;
    private bool _totpToggleBusy;
    private bool _isFirstLoad = true;
    private bool _isLeftPaneVisible = true;
    private bool _dialogOpen;
    private SecretEditModel? _observedSecret;
    // Guards PasswordField.Password/PasswordFieldRevealed.Text assignments made BY this code (secret
    // switch, reveal toggle, password generation, echoing a model change from Secret_PasswordChanged)
    // so PasswordField_Changed/PasswordFieldRevealed_Changed don't treat them as a fresh user edit.
    // Without this, a programmatic assignment fires PasswordChanged/TextChanged synchronously, which
    // re-reads the box's own value (a fresh, un-zeroed heap string) and writes it straight back into
    // EditingSecret.Password - the setter's same-value guard then returns before its own
    // ZeroStringInternals call, so that fresh string is never wiped. During live typing this also
    // free-runs a full extra round trip per keystroke (model -> Secret_PasswordChanged -> box ->
    // PasswordField_Changed -> model), each leaving another un-zeroed copy behind. Same idiom as
    // RecordExtrasControl._suppressCustomFieldChanged.
    private bool _suppressPasswordSync;

    public SecretsPage()
    {
        var app = (App)Application.Current;
        // AddScoped VMs are obtained from the session scope (disposed together with the scope on lock)
        ViewModel = app.ShellScopeServices!.GetRequiredService<SecretsViewModel>();
        _autoTypeService  = app.Services.GetRequiredService<IAutoTypeService>();
        InitializeComponent();
        // With KeyDown (bubbling), when focus is on a child control inside the detail form,
        // e.Handled=true can get set first and shortcuts like Ctrl+B never arrive.
        // PreviewKeyDown (tunneling) is received by the root first, so it fires reliably.
        PreviewKeyDown += Page_KeyDown;
        Loaded += SecretsPage_Loaded;
        // Auto-save the draft on focus-out (LosingFocus is routed against the whole detail form)
        // Note: ViewModel.PropertyChanged is subscribed in SecretsPage_Loaded (unsubscribed on Unloaded -> resubscribed on each reload)
        DetailFormPanel.AddHandler(
            UIElement.LosingFocusEvent,
            new TypedEventHandler<UIElement, LosingFocusEventArgs>(OnDetailFormLosingFocus),
            handledEventsToo: false);
        // Break the chain from the Singleton VM when ShellWindow is disposed, preventing zombification
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            DetachObservedSecret();
        };
    }

    private void OnDetailFormLosingFocus(UIElement sender, LosingFocusEventArgs e)
    {
        if (ViewModel.EditingSecret == null) return;
        // LosingFocus is a preview event that fires before the outgoing control's own LostFocus,
        // so the Notes field's x:Bind TwoWay write-back (NotesTextBox.Text -> RecordExtrasControl.Notes
        // -> EditingSecret.Notes) may not have landed yet on the very first Tab out of it. Read the
        // live text directly instead of trusting the bound value to already be current.
        // This must run BEFORE the HasUnsavedChanges check below: on a fast single edit (e.g. right
        // after a save resets the dirty flag), that same lagging x:Bind chain is also what marks the
        // model dirty (EditingSecret.Notes's setter fires the PropertyChanged that flips
        // HasUnsavedChanges), so checking it first could still read stale/false here.
        ViewModel.EditingSecret.Notes = RecordExtras.CurrentNotesText;

        // Same LosingFocus-vs-LostFocus race applies to custom fields: their {Binding Value/Label}
        // write-back (classic Binding, default LostFocus trigger) also lands on the control's own
        // LostFocus, which fires after this preview event. Read the live control value directly and
        // push it into the model before the dirty check below (mirrors the Notes handling above).
        if (e.OldFocusedElement is TextBox tb && tb.DataContext is CustomFieldModel tbField)
        {
            // A cleared label must fall back to the type-based default here too (mirrors
            // RecordExtrasControl.xaml.cs's LabelEditor_LostFocus): this LosingFocus preview fires
            // - and can trigger SaveDraftAsync below - before that control's own LostFocus runs, so
            // without this the empty text would get autosaved as the draft's label first.
            if ((string?)tb.Tag == "Label")
                tbField.Label = string.IsNullOrEmpty(tb.Text) ? tbField.FieldType.GetDefaultLabel() : tb.Text;
            else if ((string?)tb.Tag == "Value") tbField.Value = tb.Text;
        }
        else if (e.OldFocusedElement is PasswordBox pb && pb.DataContext is CustomFieldModel pwField)
        {
            SecurePasswordHelper.Borrow(pb, pwField.SetValueDirect);
        }

        if (!ViewModel.HasUnsavedChanges) return;
        _ = ViewModel.SaveDraftAsync("LosingFocus");
    }

    // CalendarDatePicker's flyout is a separate popup, so picking a date doesn't bubble a
    // LosingFocus through DetailFormPanel the way losing focus to a sibling control does -
    // the value change would otherwise sit dirty until some unrelated focus change happened to fire.
    // Also assign from args.NewDate directly rather than relying on the x:Bind TwoWay write-back
    // having already landed: on a picker's very first use, DateChanged can fire before that
    // write-back completes, so SaveDraftAsync would otherwise read the pre-change value.
    private void ExpiresAtPicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (ViewModel.EditingSecret == null) return;
        // x:Bind TwoWay pushes the loaded ExpiresAt into the picker on every LoadSecretAsync, which
        // fires this same DateChanged event with args.NewDate already equal to the current value
        // (a binding echo, not a user pick). Same guard idiom as ToggleSwitch's x:Bind OneWay pitfall:
        // bail out before the unconditional SaveDraftAsync below, or a secret with ExpiresAt set
        // spuriously re-saves a draft on every open.
        if (args.NewDate == ViewModel.EditingSecret.ExpiresAt) return;
        ViewModel.EditingSecret.ExpiresAt = args.NewDate;
        _ = ViewModel.SaveDraftAsync("ExpiresAtDateChanged");
    }

    private async void CompareDraft_Click(object sender, RoutedEventArgs e) => await ShowCompareDraftDialogAsync();

    private async Task ShowCompareDraftDialogAsync()
    {
        // Commit the latest state first, to avoid racing with LosingFocus's async save
        await ViewModel.SaveDraftAsync("CompareDraftPreSync");

        var (draft, draftAtDisplay, gen0, gen0AtDisplay, draftFiles, gen0Files) = await ViewModel.GetDraftCompareDataAsync();
        if (draft == null) return;

        var shellTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;

        // A code with no matching preset (e.g. left over from before the 2026-08-17 preset
        // renumbering) falls back to the same Uncategorized (0) label, rather than rendering blank.
        string? ResolveCategoryName(int? code) => code == null
            ? null
            : ViewModel.CategoryItems.FirstOrDefault(c => c.Code == code)?.Name
              ?? ViewModel.CategoryItems.FirstOrDefault(c => c.Code == 0)?.Name;

        var content = new SecretDraftCompareContent
        {
            Draft             = draft,
            Gen0              = gen0,
            DraftAtDisplay    = draftAtDisplay,
            Gen0AtDisplay     = gen0AtDisplay,
            DraftCategoryName = ResolveCategoryName(draft.CategoryNum),
            Gen0CategoryName  = gen0 != null ? ResolveCategoryName(gen0.CategoryNum) : null,
            DraftFiles        = draftFiles,
            Gen0Files         = gen0Files,
        };

        var dialog = new ContentDialog
        {
            XamlRoot       = XamlRoot,
            RequestedTheme = shellTheme,
            Content        = content,
        };
        // Override WinUI 3 ContentDialog's default max width (~548px) to secure enough width
        dialog.Resources["ContentDialogMinWidth"] = 860d;
        dialog.Resources["ContentDialogMaxWidth"] = 900d;

        content.CloseRequested   += (_, _) => dialog.Hide();
        content.DiscardRequested += async (_, _) =>
        {
            dialog.Hide();
            await ViewModel.DiscardCurrentDraftCommand.ExecuteAsync(null);
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
            content.DisposeAll(); // Ensure the slot buffer is ZeroMemory'd even when closed by something other than a button, e.g. Escape
            dialogClosedTcs.TrySetResult();
        }

        // Return focus to the selected item in the left pane after the dialog closes
        DispatcherQueue.TryEnqueue(() =>
        {
            var container = SecretListView.ContainerFromItem(SecretListView.SelectedItem) as ListViewItem;
            container?.Focus(FocusState.Programmatic);
        });
    }

    private async void SecretsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Reconnect the subscription that was unsubscribed on Unloaded, on every load
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // On returning via navigation: re-sync _observedSecret and PasswordBox with the current EditingSecret
        DetachObservedSecret();
        if (ViewModel.EditingSecret is { } existing)
        {
            _observedSecret = existing;
            _observedSecret.PropertyChanged += Secret_PasswordChanged;
            var pwd = existing.Password ?? string.Empty;
            _suppressPasswordSync = true;
            PasswordField.Password               = pwd;
            PasswordFieldRevealed.Text           = pwd;
            _suppressPasswordSync = false;
            PasswordRevealToggle.IsChecked       = false;
            PasswordField.Visibility             = Visibility.Visible;
            PasswordFieldRevealed.Visibility     = Visibility.Collapsed;
            PasswordLargePreview.IsVisible = false;
            UserIdEnlargeToggle.IsChecked  = false;
            UserIdLargePreview.IsVisible   = false;
            PasswordGeneratorToggle.IsChecked = false;
            PasswordGeneratorPanel.Visibility = Visibility.Collapsed;
        }

        var app     = (App)Application.Current;
        var session = app.Services.GetRequiredService<AppSession>();

        bool isPendingJump = session.PendingJumpSecretId.HasValue;
        int? jumpId = session.PendingJumpSecretId;
        session.PendingJumpSecretId = null;

        // The screen is narrow, so the left pane's default state depends on how we got here:
        // jumping in from TimeMachinePage (a specific secret) collapses it to give the detail
        // pane more room; arriving from the nav menu always shows it, resetting any previous
        // Ctrl+B collapse from an earlier visit (the page instance is cached and
        // _isLeftPaneVisible would otherwise carry over unchanged).
        SetLeftPaneVisible(!isPendingJump);

        if (!_isFirstLoad && !ViewModel.NeedsReload && !isPendingJump)
        {
            FocusFirstOrSelected();
            return;
        }

        if (_isFirstLoad)
        {
            _isFirstLoad = false;

            int? restoreId = jumpId ?? session.LastSelectedSecretId;
            Logger.LogInformation("[Restore] isPendingJump={IsPendingJump}, LastSelectedId={LastSelectedId}, restoreId={RestoreId}",
                isPendingJump, session.LastSelectedSecretId, restoreId);

            ViewModel.NeedsReload = false;
            await ViewModel.LoadAsync();
            // x:Bind SelectedValue only resolves against FilterComboItems at binding-establishment time
            // (or when FilterCategoryCode itself raises PropertyChanged) - it never automatically
            // re-resolves just because LoadAsync clears and rebuilds FilterComboItems. Force it once
            // here, now that the items actually exist (same fix TimeMachinePage uses).
            SyncCategoryFilterSelection();
            Logger.LogInformation("[Restore] FilteredSecrets.Count={Count}", ViewModel.FilteredSecrets.Count);

            if (restoreId.HasValue)
            {
                if (isPendingJump) ViewModel.FilterFavoritesOnly = false;
                await ScrollToSecretAsync(restoreId.Value);
            }
        }
        else if (ViewModel.NeedsReload)
        {
            // When changes were received while inactive (deferred reload via the NeedsReload flag).
            // Must run even when a jump is also pending - e.g. TimeMachine restores a deleted secret
            // then jumps straight to it here, so the reload has to land before ScrollToSecretAsync
            // looks the (still-stale) item up, or it can't find it and the detail pane renders blank.
            Logger.LogInformation("[NeedsReload] Reloading due to changes while inactive. isPendingJump={IsPendingJump}, jumpId={JumpId}",
                isPendingJump, jumpId);
            ViewModel.NeedsReload = false;
            await ViewModel.LoadAsync();
            // Same rebuild-loses-selection pitfall as the first-load branch above, except here
            // FilterCategoryCode may already hold a real (non-null) filter the user had picked before
            // navigating away - re-sync to whatever it currently is instead of forcing back to "All".
            SyncCategoryFilterSelection();
            if (isPendingJump)
            {
                ViewModel.FilterFavoritesOnly = false;
                await ScrollToSecretAsync(jumpId!.Value);
            }
        }
        else
        {
            // Revisiting the page, jump only (already loaded)
            Logger.LogInformation("[Jump] isPendingJump=true, id={JumpId}", jumpId);
            ViewModel.FilterFavoritesOnly = false;
            await ScrollToSecretAsync(jumpId!.Value);
        }

        FocusFirstOrSelected();
    }

    // ShellWindow.NavigateToTag assigns NavFrame.Content directly (not via Frame.Navigate), which
    // never raises OnNavigatedTo/OnNavigatedFrom - it calls these explicitly at the swap point instead.
    void IPageActivationAware.Activated() => ViewModel.Resume();
    void IPageActivationAware.Deactivated() => ViewModel.Pause();

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

    private void FocusFirstOrSelected()
    {
        // Don't steal focus from the nav menu: with J/K keyboard browsing there, landing on this
        // page after each keystroke would otherwise yank focus into the list after a single step.
        if (FocusManager.GetFocusedElement(XamlRoot) is NavigationViewItem) return;

        // No secrets at all yet (e.g. first launch): the list has nothing to focus and Search/
        // CategoryFilter are disabled (see SecretsPage.xaml), so land on the one control that's
        // actually actionable instead of parking focus in an empty ListView.
        if (!ViewModel.HasAnySecrets)
        {
            AddSecretButton.Focus(FocusState.Programmatic);
            return;
        }

        if (ViewModel.SelectedListItem != null)
            SecretListView.ScrollIntoView(ViewModel.SelectedListItem);
        SecretListView.Focus(FocusState.Programmatic);
    }

    private async Task ScrollToSecretAsync(int id)
    {
        await Task.Yield();
        var node = ViewModel.FilteredSecrets.FirstOrDefault(s => s.Id == id);
        Logger.LogInformation("[ScrollTo] id={Id}, found={Found}", id, node != null);
        if (node == null) return;
        await ViewModel.LoadSecretAsync(id);
        Logger.LogInformation("[ScrollTo] EditingSecret.Id={Id}", ViewModel.EditingSecret?.Id.ToString() ?? "null");
        SecretListView.SelectedItem = node;
        SecretListView.ScrollIntoView(node);
    }

    // When EditingSecret switches, sync PasswordBox / TextBox and reset the eye toggle and the
    // UserId enlarge toggle (so a large-preview panel left open on the previous secret doesn't
    // keep showing that secret's now-stale value).
    // When SelectedListItem changes from the VM side, make the ListView's visual selection follow it
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SelectedListItem) &&
            ViewModel.SelectedListItem is SecretListItemViewModel selNode)
        {
            SecretListView.SelectedItem = selNode;
            SecretListView.ScrollIntoView(selNode);
            // After a selection change from the VM side, such as a filter toggle, return focus to the list item
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var container = SecretListView.ContainerFromItem(selNode) as ListViewItem;
                if (container != null)
                    container.Focus(FocusState.Programmatic);
                else
                    SecretListView.Focus(FocusState.Programmatic);
            });
            return;
        }
        if (e.PropertyName != nameof(ViewModel.EditingSecret)) return;

        var pwd = ViewModel.EditingSecret?.Password ?? string.Empty;
        _suppressPasswordSync = true;
        PasswordField.Password         = pwd;
        PasswordFieldRevealed.Text     = pwd;
        _suppressPasswordSync = false;
        PasswordRevealToggle.IsChecked = false;
        PasswordField.Visibility             = Visibility.Visible;
        PasswordFieldRevealed.Visibility     = Visibility.Collapsed;
        PasswordLargePreview.IsVisible = false;
        UserIdEnlargeToggle.IsChecked  = false;
        UserIdLargePreview.IsVisible   = false;
        // A generator panel left open on the previously selected secret must not keep showing on the
        // newly selected one - otherwise switching secrets while the generator is open leaves it
        // visually "stuck open" against a secret the user never opened it for.
        PasswordGeneratorToggle.IsChecked = false;
        PasswordGeneratorPanel.Visibility = Visibility.Collapsed;

        // Detach from the old secret before attaching to the new one (prevents accumulating multiple subscriptions)
        DetachObservedSecret();
        if (ViewModel.EditingSecret is { } secret)
        {
            _observedSecret = secret;
            _observedSecret.PropertyChanged += Secret_PasswordChanged;
        }
    }

    // Reflect Password changes (typed via PasswordFieldRevealed, or external e.g. from password
    // generation) into PasswordBox / TextBox, and UserId changes (typed via the x:Bind TwoWay
    // TextBox, or external) into the enlarge-preview panel while it's open.
    private void Secret_PasswordChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SecretEditModel.Password))
        {
            var p = _observedSecret?.Password ?? string.Empty;
            _suppressPasswordSync = true;
            if (PasswordRevealToggle.IsChecked == true)
                PasswordFieldRevealed.Text = p;
            else
                PasswordField.Password = p;
            _suppressPasswordSync = false;
            return;
        }
        if (e.PropertyName == nameof(SecretEditModel.UserId) && UserIdLargePreview.IsVisible)
            UserIdLargePreview.UserIdToken = _observedSecret?.UserIdToken;
    }

    private void DetachObservedSecret()
    {
        if (_observedSecret == null) return;
        _observedSecret.PropertyChanged -= Secret_PasswordChanged;
        _observedSecret = null;
    }

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e) => ToggleLeftPane();

    private void ToggleLeftPane()
    {
        SetLeftPaneVisible(!_isLeftPaneVisible);
        (_isLeftPaneVisible ? (Control)SecretListView : PaneToggleButton).Focus(FocusState.Programmatic);
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

    public void FocusSearchBox() => SearchBox.Focus(FocusState.Programmatic);

    private async void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();
        if (e.Key == VirtualKey.S && ctrl && ViewModel.SaveSecretCommand.CanExecute(null))
        {
            ViewModel.SaveSecretCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.N && ctrl && ViewModel.AddSecretCommand.CanExecute(null))
        {
            await ViewModel.AddSecretCommand.ExecuteAsync(null);
            // Queue at Low priority so focus is applied after the Collapsed->Visible layout pass (Normal priority) completes
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => TitleField.Focus(FocusState.Programmatic));
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.J && ctrl && ViewModel.EditingSecret != null)
        {
            JumpToTimeMachine();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.E && ctrl && ViewModel.EditingSecretHasDraft)
        {
            _ = ShowCompareDraftDialogAsync();
            e.Handled = true;
        }
        else if ((e.Key == VirtualKey.Number6 || e.Key == VirtualKey.NumberPad6) && ctrl
                 && ViewModel.HasFilteredFavorites)
        {
            ViewModel.FilterFavoritesOnly = !ViewModel.FilterFavoritesOnly;
            e.Handled = true;
        }
        else if ((e.Key == VirtualKey.Number7 || e.Key == VirtualKey.NumberPad7) && ctrl
                 && ViewModel.HasFilteredDraft)
        {
            ViewModel.FilterDraftOnly = !ViewModel.FilterDraftOnly;
            e.Handled = true;
        }
        else if ((e.Key == VirtualKey.Number8 || e.Key == VirtualKey.NumberPad8) && ctrl
                 && ViewModel.HasFilteredExpired)
        {
            ViewModel.FilterExpiredOnly = !ViewModel.FilterExpiredOnly;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.B && ctrl)
        {
            ToggleLeftPane();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.D && ctrl && ViewModel.HasEditingSecret)
        {
            LaunchDirectInjectionDialog();
            e.Handled = true;
        }
        // Space: toggle favorite for the selected left-pane item. Handled here (PreviewKeyDown,
        // tunneling) rather than in SecretListView_KeyDown (bubbling): a focused ListViewItem consumes
        // Space for its own default handling first, which stops WinUI from ever invoking a normal
        // (handledEventsToo: false) bubbling KeyDown subscriber - matches TimeMachinePage's Space handling.
        else if (e.Key == VirtualKey.Space && !ctrl
                 && FocusManager.GetFocusedElement(XamlRoot) is not TextBox and not PasswordBox)
        {
            if (ViewModel.SelectedListItem is SecretListItemViewModel node)
                await ViewModel.ToggleFavoriteCommand.ExecuteAsync(node);
            e.Handled = true;
        }
    }

    private void InjectButton_Click(object sender, RoutedEventArgs e)
        => LaunchDirectInjectionDialog();

    private async void LaunchDirectInjectionDialog()
    {
        if (_dialogOpen) return;
        var editing = ViewModel.EditingSecret;
        if (editing is null) return;
        _dialogOpen = true;
        try
        {
            var shellTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
            var hwndTarget = _autoTypeService.GetPreviousTargetHwnd();
            var dlg = new Views.DirectInjectionDialog(
                _autoTypeService, hwndTarget,
                editing.UserIdBuf, editing.PasswordBuf,
                editing.Id, editing.Title,
                editing.LabelUserId, editing.LabelPassword)
            {
                XamlRoot = XamlRoot,
                RequestedTheme = shellTheme,
            };
            await dlg.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    public void ToggleAllRevealed()
    {
        var editing = ViewModel.EditingSecret;
        if (editing == null) return;
        // All fields that carry a reveal/enlarge toggle (password fields mask/unmask; text-plain
        // fields only enlarge - see CustomFieldModel.ShowAsTextPlain). Date/Url fields have neither.
        var toggleItems = editing.CustomFields.Where(cf => cf.IsPassword || cf.ShowAsTextPlain).ToList();
        bool allRevealed = PasswordRevealToggle.IsChecked == true
            && UserIdEnlargeToggle.IsChecked == true
            && toggleItems.All(cf => cf.IsRevealed);
        bool next = !allRevealed;
        PasswordRevealToggle.IsChecked = next;
        ApplyPasswordReveal(next);
        UserIdEnlargeToggle.IsChecked = next;
        ApplyUserIdEnlarge(next);
        foreach (var cf in toggleItems)
            cf.IsRevealed = next;
    }

    private async void SecretListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();
        switch (e.Key)
        {
            case VirtualKey.Delete:
                await ViewModel.DeleteSecretCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            // Space is handled in Page_KeyDown (PreviewKeyDown/tunneling) instead of here: a focused
            // ListViewItem consumes it for its own default handling before this bubbling handler runs.
            case VirtualKey.Number5 when ctrl:
            case VirtualKey.NumberPad5 when ctrl:
                if (ViewModel.SelectedListItem != null)
                {
                    await ViewModel.ToggleFavoriteCommand.ExecuteAsync(ViewModel.SelectedListItem);
                    e.Handled = true;
                }
                break;
            case VirtualKey.J when !ctrl:
                if (SecretListView.SelectedIndex < SecretListView.Items.Count - 1)
                {
                    SecretListView.SelectedIndex++;
                    SecretListView.ScrollIntoView(SecretListView.SelectedItem);
                }
                e.Handled = true;
                break;
            case VirtualKey.K when !ctrl:
                if (SecretListView.SelectedIndex > 0)
                {
                    SecretListView.SelectedIndex--;
                    SecretListView.ScrollIntoView(SecretListView.SelectedItem);
                }
                e.Handled = true;
                break;
        }
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    private void SecretList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is SecretListItemViewModel secret)
            ViewModel.SelectedListItem = secret;
        else if (e.AddedItems.Count == 0 && e.RemovedItems.Count > 0)
            ViewModel.SelectedListItem = null;
    }

    private void PasswordField_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPasswordSync || ViewModel.EditingSecret == null) return;
        // The setter itself guards against redundant same-value writes (and runs ZeroStringInternals only on an actual change).
        ViewModel.EditingSecret.Password = PasswordField.Password;
    }

    private void PasswordRevealToggle_Click(object sender, RoutedEventArgs e)
        => ApplyPasswordReveal(PasswordRevealToggle.IsChecked == true);

    private void ApplyPasswordReveal(bool reveal)
    {
        if (reveal)
        {
            var pwd = ViewModel.EditingSecret?.Password ?? string.Empty;
            _suppressPasswordSync = true;
            PasswordFieldRevealed.Text       = pwd;
            _suppressPasswordSync = false;
            PasswordField.Visibility         = Visibility.Collapsed;
            PasswordFieldRevealed.Visibility = Visibility.Visible;
            PasswordFieldRevealed.Focus(FocusState.Programmatic);
            PasswordLargePreview.PasswordToken = ViewModel.EditingSecret?.PasswordToken;
            PasswordLargePreview.IsVisible = true;
        }
        else
        {
            _suppressPasswordSync = true;
            PasswordField.Password           = ViewModel.EditingSecret?.Password ?? string.Empty;
            _suppressPasswordSync = false;
            PasswordField.Visibility         = Visibility.Visible;
            PasswordFieldRevealed.Visibility = Visibility.Collapsed;
            PasswordField.Focus(FocusState.Programmatic);
            PasswordLargePreview.IsVisible = false;
        }
    }

    private void PasswordFieldRevealed_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressPasswordSync || ViewModel.EditingSecret == null) return;
        // The setter itself guards against redundant same-value writes (and runs ZeroStringInternals only on an actual change).
        ViewModel.EditingSecret.Password = PasswordFieldRevealed.Text;

        // Regenerate the token directly from PasswordBuf (already updated by the setter). Never through a string.
        if (PasswordLargePreview.IsVisible)
            PasswordLargePreview.PasswordToken = ViewModel.EditingSecret.PasswordToken;
    }

    // UserId has no masked/revealed pair (it's always plain text), so the toggle only opens/closes
    // the large preview panel - unlike the password toggle it never swaps which TextBox is visible.
    // Live updates while typing are handled via Secret_PasswordChanged (below), not a TextChanged
    // handler here: the UserId TextBox's Text is already x:Bind TwoWay, and racing an explicit
    // TextChanged handler against x:Bind's own push-to-source on the same event has no guaranteed
    // ordering, which could read UserIdBuf one keystroke stale.
    private void UserIdEnlargeToggle_Click(object sender, RoutedEventArgs e)
        => ApplyUserIdEnlarge(UserIdEnlargeToggle.IsChecked == true);

    private void ApplyUserIdEnlarge(bool show)
    {
        if (show)
            UserIdLargePreview.UserIdToken = ViewModel.EditingSecret?.UserIdToken;
        UserIdLargePreview.IsVisible = show;
    }

    private void ShowPasswordGenerator(object sender, RoutedEventArgs e)
    {
        if (PasswordGeneratorToggle.IsChecked == true)
        {
            ViewModel.ResetGeneratorDefaults();
            PasswordGeneratorPanel.Visibility = Visibility.Visible;
        }
        else
        {
            PasswordGeneratorPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void GeneratePassword_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.GeneratePasswordCommand.Execute(null);
        RevealGeneratedPassword();
    }

    private void PwdLengthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (PasswordGeneratorPanel.Visibility != Visibility.Visible || ViewModel.EditingSecret == null) return;
        if (!ViewModel.CanGeneratePassword) return;
        ViewModel.GeneratePasswordCommand.Execute(null);
        RevealGeneratedPassword();
    }

    private void RevealGeneratedPassword()
    {
        if (!PasswordRevealToggle.IsChecked.GetValueOrDefault())
        {
            PasswordRevealToggle.IsChecked = true;
            ApplyPasswordReveal(true);
        }
        else
        {
            // Even when the eye toggle is already ON, explicitly update the TextBox (complements the update via _observedSecret)
            var pwd = ViewModel.EditingSecret?.Password ?? string.Empty;
            _suppressPasswordSync = true;
            PasswordFieldRevealed.Text = pwd;
            _suppressPasswordSync = false;
            PasswordLargePreview.PasswordToken = ViewModel.EditingSecret?.PasswordToken;
        }
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        var session = ((App)Application.Current).Services.GetRequiredService<AppSession>();
        if (ViewModel.EditingSecret == null || session.IsReadOnlyRestricted)
        {
            // Nothing to attach to (no secret selected) or writes are disabled (emergency access
            // code's restricted mode) - show the "not allowed" cursor instead of "drop to attach",
            // since Page_Drop would otherwise silently swallow the drop with no visible feedback.
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = LocalizationManager.Get("Common.DropToAttach");
    }

    private async void StarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SecretListItemViewModel node })
            await ViewModel.ToggleFavoriteCommand.ExecuteAsync(node);
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
                "SecretsPage.Page_Drop failed. [{ExType}]", ex.GetType().Name);
        }
    }

    private void JumpToTimeMachine_Click(object sender, RoutedEventArgs e) => JumpToTimeMachine();

    private void JumpToTimeMachine()
    {
        if (ViewModel.EditingSecret == null) return;
        WeakReferenceMessenger.Default.Send(new NavigateToTagMessage("timemachine", ViewModel.EditingSecret.Id));
    }

    private async void TotpToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn == ViewModel.HasTotp) return; // prevent re-entry caused by a binding update
        if (ViewModel.EditingSecret == null) return;
        if (_totpToggleBusy) return; // prevent concurrent saves from rapid ON->OFF toggling
        _totpToggleBusy = true;

        var shell = ((App)Application.Current).ActiveShellWindow;
        var shellTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;

        try
        {
            if (toggle.IsOn)
            {
                var dialog = new TotpSetupDialog(shell!)
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = shellTheme,
                    CurrentImageCount = ViewModel.EditingSecret.AttachedFiles.Count,
                };
                // TotpSetupDialog uses a custom footer (Discard/Save) instead of the standard Primary/Close
                // buttons, so Hide() always yields ContentDialogResult.None regardless of which was clicked.
                // TotpSecret itself (set only by the Save-side handler) is the reliable success signal.
                await dialog.ShowAsync();
                if (dialog.TotpSecret != null)
                {
                    ViewModel.EditingSecret.SetTotpConfig(new TotpCalculator.TotpConfig(
                        dialog.TotpSecret, dialog.TotpDigits, dialog.TotpPeriod, dialog.TotpAlgorithm));
                    // SetTotpConfig packs config.Secret into TotpSecretBuf's own pinned buffer - it
                    // never touches the original string. TotpSetupDialog documents this handoff as
                    // "isn't zeroed because it's the value the caller is about to consume", so wiping
                    // it is this call site's responsibility.
                    SecurePasswordHelper.ZeroStringInternals(dialog.TotpSecret);
                    if (dialog.DroppedFilePaths.Count > 0)
                        await ViewModel.AddFilesCommand.ExecuteAsync(dialog.DroppedFilePaths.ToArray());
                    await ViewModel.GuardedSaveAsync(); // save to DB immediately
                }
                else
                {
                    toggle.IsOn = false; // cancelled: revert the switch
                }
            }
            else
            {
                // TOTP is a login-critical credential, so this uses the same red/destructive
                // confirm styling as an actual delete, not a neutral OK/Cancel confirm.
                bool confirmed = await ViewModel.ConfirmDisableTotpAsync();
                if (confirmed)
                {
                    ViewModel.EditingSecret.TotpSecret = null;
                    await ViewModel.GuardedSaveAsync(); // save to DB immediately
                }
                else
                {
                    toggle.IsOn = true; // cancelled: revert the switch
                }
            }
        }
        finally
        {
            _totpToggleBusy = false;
        }
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

    // App.xaml only overrides SystemFillColorCriticalBrush/SystemFillColorCautionBrush in the Light
    // theme dictionary (dark mode keeps WinUI's own softer built-in reds/golds, #FF99A4/#FCE100,
    // which already read fine against a dark background - see App.xaml Technique 4/5). Using the
    // fixed light-mode value unconditionally in dark mode made these alerts look jarringly saturated
    // next to the title bar's expiry icon (ShellWindow._criticalBrushLight/_cautionBrushLight), which
    // already branched on theme. Branch here too instead of hardcoding the light value.
    private static readonly SolidColorBrush _expiryAlertForegroundLight   = new(Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));
    private static readonly SolidColorBrush _expiryCautionForegroundLight = new(Windows.UI.Color.FromArgb(0xFF, 0xB3, 0x85, 0x0F));
    // Equivalent to WinUI 3's TextFillColorPrimaryBrush (chosen based on ActualTheme)
    private static readonly SolidColorBrush _expiryNormalFgLight = new(Windows.UI.Color.FromArgb(0xE4,   0,   0,   0));
    private static readonly SolidColorBrush _expiryNormalFgDark  = new(Windows.UI.Color.FromArgb(0xFF, 255, 255, 255));

    // isExpired (already past due, critical/red) takes precedence over hasAlert (still within the
    // warning window, caution/amber) - both can be true at once since hasAlert's window includes the
    // already-expired case. Mirrors ProfilePage's GetAlertForeground for the identity item expiry labels.
    internal Brush GetExpiryAlertForeground(bool hasAlert, bool isExpired)
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        if (isExpired) return isDark ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : _expiryAlertForegroundLight;
        if (hasAlert)  return isDark ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]  : _expiryCautionForegroundLight;
        return isDark ? _expiryNormalFgDark : _expiryNormalFgLight;
    }

    internal Visibility ExpiryAlertVisibility(bool hasAlert)
        => hasAlert ? Visibility.Visible : Visibility.Collapsed;
}
