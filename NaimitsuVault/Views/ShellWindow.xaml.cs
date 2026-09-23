// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using NaimitsuVault.Common;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Pages;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using Windows.Storage.Streams;
using Windows.UI;
using WinRT.Interop;
using WinUIEx;

namespace NaimitsuVault.Views;

public sealed partial class ShellWindow : WindowEx
{
    private static readonly ILogger<ShellWindow> Logger = AppLog.For<ShellWindow>();

    private readonly IAppNotificationService _notification;
    private readonly IThemeService _themeService;
    private readonly WinUIFilePicker _filePicker;
    private readonly WinUIDialogService _dialogService;
    private readonly IdleTimeoutService _autoLock;
    private readonly AppSession _session;
    private readonly IVaultConnectionProvider _connectionProvider;
    private readonly AvatarService _avatarService;
    private readonly ProfileService _profileService;
    private readonly StoredFileRepository _storedFiles;
    private readonly SecretRepository _secrets;
    private readonly IAuditLogService _auditLog;
    private readonly GalleryViewModel   _galleryVm;
    private readonly DashboardViewModel _dashboardVm;
    private readonly ProfileViewModel   _profileVm;
    private DateTime _lastActivityReset = DateTime.MinValue;
    private bool _shellInitialized;
    private bool _hasProfileExpiryAlert;
    // Unlike _hasProfileExpiryAlert (expired + expiring-soon combined), this is true only when at
    // least one profile identity item is already past due - drives the title bar's red-vs-gold.
    private bool _hasProfileExpiredPastDue;
    private int  _cachedSecretExpiryCount;
    // Companion to _cachedSecretExpiryCount (expired + expiring-soon combined), used the same way
    // before SecretsViewModel has loaded: only true once at least one secret is already past due.
    private bool _cachedSecretHasExpiredPastDue;
    // Tracks the RefreshTimeMachineNavEnabledAsync/RefreshAuditLogNavEnabledAsync "already confirmed
    // enabled, stop querying" guard. Deliberately NOT read back from NavItemTimeMachine.IsEnabled /
    // NavItemAuditLog.IsEnabled themselves - NavigationViewItem.IsEnabled defaults to true in XAML,
    // so reading it before either method's first real query ever ran would look "already enabled"
    // and skip the query forever, leaving both nav items permanently enabled regardless of count.
    private bool _timeMachineNavConfirmedHasData;
    private bool _auditLogNavConfirmedHasData;
    // Application.Current.Resources[key] ignores the theme context and always returns the "Default"
    // dictionary, so a brush retrieved from C# doesn't reflect App.xaml's unified color in light
    // mode. Dark mode matches Default so there's no practical harm, but light mode uses a fixed
    // value directly.
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush _cautionBrushLight  = new(Color.FromArgb(0xFF, 0xB3, 0x85, 0x0F));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush _criticalBrushLight = new(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));
    // Manages display-name PII in a POH-pinned SecureCharBuffer and ZeroMemory's it on lock.
    private readonly SecureCharBuffer _displayNameBuf = new();

    public SecretsViewModel SecretsVm { get; }

    public ShellWindow()
    {
        // x:Bind resolves its references inside InitializeComponent, so resolve these before that
        // AddScoped VMs are obtained from the session scope (disposed together with the scope on lock)
        var app          = (App)Application.Current;
        var scopeServices = app.ShellScopeServices!;
        SecretsVm  = scopeServices.GetRequiredService<SecretsViewModel>();
        _galleryVm    = scopeServices.GetRequiredService<GalleryViewModel>();
        _dashboardVm  = scopeServices.GetRequiredService<DashboardViewModel>();
        _profileVm    = scopeServices.GetRequiredService<ProfileViewModel>();

        InitializeComponent();

        // Window size, minimum size, center on screen (WinUIEx auto-corrects for DPI)
        this.SetWindowSize(1100, 700);
        this.MinWidth  = 900;
        this.MinHeight = 600;
        this.CenterOnScreen();

        // Title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Taskbar/Alt+Tab icon (ICO). The title bar icon is already set in XAML via TitleBar.IconSource + ImageIconSource(PNG)
        AppWindow.SetIcon("Assets/NaimitsuVault.ico");

        // 48px title bar (also aligns the OS caption buttons, via AppWindowTitleBar)
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        // Mica backdrop (WinUIEx.WindowEx.SystemBackdrop: CS0612 obsolete, but suppressed in csproj)
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        _notification  = app.Services.GetRequiredService<IAppNotificationService>();
        _themeService  = app.Services.GetRequiredService<IThemeService>();
        _filePicker    = (WinUIFilePicker)app.Services.GetRequiredService<IFilePickerService>();
        _dialogService = (WinUIDialogService)app.Services.GetRequiredService<IDialogService>();
        _autoLock            = app.Services.GetRequiredService<IdleTimeoutService>();
        _session             = app.Services.GetRequiredService<AppSession>();
        _connectionProvider  = app.Services.GetRequiredService<IVaultConnectionProvider>();
        _avatarService   = app.Services.GetRequiredService<AvatarService>();
        _profileService  = app.Services.GetRequiredService<ProfileService>();
        _storedFiles     = scopeServices.GetRequiredService<StoredFileRepository>();  // changed to Scoped
        _secrets         = scopeServices.GetRequiredService<SecretRepository>();
        _auditLog        = app.Services.GetRequiredService<IAuditLogService>();

        // hwnd is valid immediately after window creation; resolve it here without waiting for Activated
        var hwnd = WindowNative.GetWindowHandle(this);
        _filePicker.SetHwnd(hwnd);

        AppWindow.Closing += ShellWindow_AppWindowClosing;
        AppWindow.Changed += ShellWindow_AppWindowChanged;

        // Theme: SetRoot then Apply, in that order. Waiting for Activated would misfire the first time on Deactivated
        var themeRoot = (FrameworkElement)Content;
        _themeService.SetRoot(themeRoot);
        _themeService.Apply(GetCurrentThemeSetting());
        // Fix the caption button colors to the initial theme
        UpdateTitleBarButtonColors(themeRoot.ActualTheme == ElementTheme.Light);
        // InfoBar doesn't automatically detect theme changes while IsOpen=false, so reset to Default to trigger inheritance from its parent.
        // Show() likewise resets to Default before setting IsOpen=true (on the AppNotificationService side).
        themeRoot.ActualThemeChanged += (_, _) =>
        {
            NotificationBar.RequestedTheme = ElementTheme.Default;
            UpdateTitleBarButtonColors(themeRoot.ActualTheme == ElementTheme.Light);
            // Without this, the expiry icon/count's light-vs-dark brush (isLight branch in
            // RefreshExpiryWarningIcon) stays stale until something unrelated (nav selection, etc.)
            // happens to re-trigger it - the icon itself doesn't listen for theme changes on its own.
            RefreshExpiryWarningIcon();
        };

        // Font family: apply the current setting immediately, then track live changes
        var fontSettingsVm = app.Services.GetRequiredService<AppSettingsViewModel>();
        ApplyFontFamily(themeRoot, fontSettingsVm.FontFamily);
        WeakReferenceMessenger.Default.Register<FontFamilyChangedMessage>(this, (_, msg) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Content is FrameworkElement root) ApplyFontFamily(root, msg.FontFamily);
            }));

        // Finish Register before the page's LoadAsync. Waiting for Activated would drop the
        // notification fired by NavigateToTag -> Loaded -> LoadAsync inside the constructor.
        var queue = DispatcherQueue.GetForCurrentThread();
        ((AppNotificationService)_notification).Register(NotificationBar, queue);

        // Read-only restricted mode (Route A): turns the title bar lock button yellow and
        // physically blocks write-capable pages from the side menu
        ApplyReadOnlyRestrictedLockdown();
        _ = RefreshTimeMachineNavEnabledAsync();
        _ = RefreshAuditLogNavEnabledAsync();

        // Finalize navigation before Activate(), to prevent NavEmptyHint from being visible until
        // ShellWindow_Activated fires (there can be a 2-3 second gap between Activate and Activated)
        var initialTag = _session.LastActivePageTag ?? "dashboard";
        if (_session.IsReadOnlyRestricted && IsReadOnlyRestrictedLockedTag(initialTag))
            initialTag = "dashboard";
        NavigateToTag(initialTag);

        // Update the title bar whenever the secret list count/expiry alert changes
        SecretsVm.PropertyChanged += SecretsVm_PropertyChanged;
        _galleryVm.PropertyChanged   += GalleryVm_PropertyChanged;
        // SecretExpiryAlertCount updates once Dashboard finishes LoadBasicStatsAsync
        _dashboardVm.PropertyChanged += DashboardVm_PropertyChanged;

        WeakReferenceMessenger.Default.Register<StorageChangedMessage>(this, (_, _) =>
        {
            RefreshTitleLabel();
            _ = RefreshCachedSecretExpiryCountAsync();
        });
        WeakReferenceMessenger.Default.Register<SecretsImportedMessage>(this, (_, _) =>
            RefreshTitleLabel());
        WeakReferenceMessenger.Default.Register<DbIntegrityWarningMessage>(this,
            (_, msg) => DispatcherQueue.TryEnqueue(() => OnDbIntegrityWarning(msg.Delta)));
        WeakReferenceMessenger.Default.Register<NavigateToTagMessage>(this, (_, msg) =>
            DispatcherQueue.TryEnqueue(async () => await TryNavigateToTagAsync(msg.Tag, msg.JumpSecretId)));
        // AuditLogService broadcasts this from every write path (LogAsync, LogAuthFailedAsync, etc.) -
        // the one choke point common to over a dozen scattered call sites - so the nav item flips to
        // enabled the moment the first entry lands, not just when a secret is added/removed.
        WeakReferenceMessenger.Default.Register<AuditLogWrittenMessage>(this, (_, msg) =>
            DispatcherQueue.TryEnqueue(async () =>
            {
                await RefreshAuditLogNavEnabledAsync();
                // SecretPermanentlyDeleted (TimeMachineViewModel.PermanentDeleteAsync) is the only
                // event that can shrink TimeMachine's "has any data" state back toward zero - a soft
                // delete keeps the secret's history, only a hard delete removes it. Force a genuine
                // recheck by clearing the "confirmed" flag, rather than reacting to every frequent
                // audit event (SecretViewed on every list click, etc.) the way AuditLog's own check
                // deliberately doesn't need to.
                if (msg.Code == AuditEventCode.SecretPermanentlyDeleted)
                {
                    _timeMachineNavConfirmedHasData = false;
                    await RefreshTimeMachineNavEnabledAsync();
                }
            }));

        // Resolve XamlRoot before Activated fires.
        // Activated can lag Activate() by 2-3 seconds, and opening a dialog during that window
        // throws "This element does not have a XamlRoot".
        // Loaded fires once the content is attached to the visual tree (reliably before any user action).
        ((FrameworkElement)Content).Loaded += async (_, _) =>
        {
            _dialogService.SetXamlRoot((FrameworkElement)Content);
            await LoadNavPaneAvatarAsync();
            RefreshTitleLabel();
        };

        NavView.PaneClosed += (_, _) => SyncAvatarItemVisibility();
        NavView.PaneOpened += (_, _) => SyncAvatarItemVisibility();

        WeakReferenceMessenger.Default.Register<AvatarChangedMessage>(this, (_, msg) =>
            DispatcherQueue.TryEnqueue(async () => await UpdateNavAvatarAsync(msg.AvatarBytes)));
        WeakReferenceMessenger.Default.Register<DisplayNameChangedMessage>(this, (_, msg) =>
            DispatcherQueue.TryEnqueue(async () =>
            {
                SetDisplayName(msg.DisplayName);
                await RefreshProfileExpiryAlertAsync();
            }));

        Activated += ShellWindow_Activated;

        ((FrameworkElement)Content).PreviewKeyDown += ShellWindow_KeyDown;
    }

    private void ShellWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_closed) return;

        // Accessing an already-closed WinRT object from the Activated event that fires right
        // after Exit() throws COMException, so catch it defensively and return early.
        try
        {
            if (Content is FrameworkElement activeRoot && activeRoot.XamlRoot != null)
                _dialogService.SetXamlRoot(activeRoot);
        }
        catch (System.Runtime.InteropServices.COMException) { return; }

        // Suppresses the issue where, after an external app like Snipping Tool takes focus, WinUI 3
        // restores focus to the first TextBox and the ScrollViewer jumps back to the top.
        // Record the offset on deactivation and restore it at Low priority on activation
        // (runs after BringIntoView, so it can override it).
        if (NavFrame.Content is Pages.ProfilePage profilePage)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                profilePage.SaveScrollPosition();
            else
                profilePage.RestoreScrollPosition();
        }

        if (_shellInitialized)
        {
            // Check immediately on every reactivation, such as resuming from sleep.
            // DispatcherQueueTimer freezes during suspend, so force the decision from the absolute time difference.
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                _autoLock.CheckNow();
            return;
        }
        _shellInitialized = true;
        _ = ShellInitializeAsync();
    }

    private async Task ShellInitializeAsync()
    {
        var content = (FrameworkElement)Content;
        var autoLockQueue = DispatcherQueue.GetForCurrentThread();

        content.PointerMoved += OnUserActivity;
        content.KeyDown += OnUserActivity;
        var appService = ((App)Application.Current).Services.GetRequiredService<ILockService>();
        // AutoLock hookup - physically severs NavPane PII right before the lock callback.
        // DispatcherQueueTimer.Tick and CheckNow are called on the UI thread, so
        // PurgeNavContext (which accesses WinUI controls) is safe here.
        _autoLock.Initialize(autoLockQueue, () => { PurgeNavContext(); appService.Lock(); });

        // Capture protection: apply it once, consolidated, at the very end of ShellInitializeAsync() after the HWND is resolved.
        // LoadAsync already completes inside App.OnLaunched before ShellWindow is created, so the setting can be read safely here.
        var captureProtection = ((App)Application.Current).Services.GetRequiredService<IWindowCaptureProtectionService>();
        var captureSettingsVm = ((App)Application.Current).Services.GetRequiredService<AppSettingsViewModel>();
        captureProtection.Apply(WindowNative.GetWindowHandle(this), captureSettingsVm.WindowCaptureProtectionEnabled);
        await Task.CompletedTask;
    }

    private bool _suppressCloseConfirm;
    private bool _closed;

    private void ShellWindow_AppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_suppressCloseConfirm || !AppWindow.IsVisible) return;
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
        var settingsVm = ((App)Application.Current).Services
            .GetRequiredService<ViewModels.AppSettingsViewModel>();
        if (settingsVm.IsHideFromTaskbarWhenMinimized)
            AppWindow.Hide();
    }

    public void SuppressCloseConfirm()
    {
        _suppressCloseConfirm = true;
        _closed = true;
        SecretsVm.PropertyChanged -= SecretsVm_PropertyChanged;
        _galleryVm.PropertyChanged   -= GalleryVm_PropertyChanged;
        _dashboardVm.PropertyChanged -= DashboardVm_PropertyChanged;
        WeakReferenceMessenger.Default.Unregister<AvatarChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<DisplayNameChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<StorageChangedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<SecretsImportedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<NavigateToTagMessage>(this);
        WeakReferenceMessenger.Default.Unregister<AuditLogWrittenMessage>(this);
        WeakReferenceMessenger.Default.Unregister<DbIntegrityWarningMessage>(this);
        WeakReferenceMessenger.Default.Unregister<FontFamilyChangedMessage>(this);
    }

    // FontFamily is only a first-class property on Control/TextBlock, not on FrameworkElement (Content
    // here is a plain Grid). Set the underlying inheritable DependencyProperty directly via SetValue so
    // it still cascades down to descendant Controls that don't already set FontFamily locally/via Style.
    private static void ApplyFontFamily(FrameworkElement root, string? fontFamily)
    {
        if (string.IsNullOrEmpty(fontFamily))
            root.ClearValue(Control.FontFamilyProperty);
        else
            root.SetValue(Control.FontFamilyProperty, new Microsoft.UI.Xaml.Media.FontFamily(fontFamily));

        // Controls whose default Style sets FontFamily from {ThemeResource ContentControlThemeFontFamily}
        // (already updated by FontResourceService) won't re-evaluate from a raw dictionary edit alone -
        // ThemeResource only re-resolves on an actual theme change. Toggle-and-restore synchronously
        // (collapses into a single composited frame, no visible flicker) to force that re-evaluation.
        var current = root.RequestedTheme;
        root.RequestedTheme = current == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = current;
    }

    /// <summary>
    /// Synchronously tears down the XAML binding reference graph before locking.
    /// Must be called before Close(), while the Window is still fully valid.
    /// Clears _pageCache to immediately release Page instance references, so the
    /// VM -> SecureCharBuffer ZeroMemory chain doesn't get blocked.
    /// </summary>
    internal void ScrubViewReferences()
    {
        // LockButton_Click/ShellWindow_KeyDown(Ctrl+L) already call PurgeNavContext() before reaching
        // here (PurgeNavContext() is idempotent, so that's harmless double-purge, not redundant dead
        // code removal material), but every OTHER trigger of the shared LockAsync -> LockCycleOrchestrator
        // -> ScrubViewReferences pipeline - IdleTimeoutService's auto-lock, App.LockAfterDataWrite() -
        // reaches this method directly with no such call, which used to leave the NavPane's cached
        // avatar image and display-name string un-purged on those paths.
        PurgeNavContext();
        NavFrame.Content = null;
        _pageCache.Clear();  // immediately release references to Page instances
    }

    public void DetachActivityHandlers()
    {
        if (Content is FrameworkElement content)
        {
            content.PointerMoved -= OnUserActivity;
            content.KeyDown -= OnUserActivity;
            content.PreviewKeyDown -= ShellWindow_KeyDown;
        }
    }

    /// <summary>
    /// Modal exclusion control that physically blocks all input to the main screen during a bulk
    /// backup/plaintext export. Set to false only while BackupProgressWindow is shown.
    /// </summary>
    public void SetInputEnabled(bool enabled) => RootGrid.IsHitTestVisible = enabled;

    private void SecretsVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SecretsViewModel.TotalCountLabel))
        {
            RefreshTitleLabel();
            // TotalCountLabel changes on every add/delete/restore/import - the same moments TimeMachine's
            // "has anything to show" state can flip, so piggyback on this instead of a separate signal.
            // (AuditLog's own nav item is instead driven by AuditLogWrittenMessage - see constructor -
            // since audit entries are written from many more places than just secret count changes.)
            _ = RefreshTimeMachineNavEnabledAsync();
        }
        if (e.PropertyName is nameof(SecretsViewModel.HasExpiryAlert) or nameof(SecretsViewModel.ExpiryAlertCount))
        {
            _cachedSecretExpiryCount = 0; // SecretsVm is already loaded, so the cache isn't needed
            RefreshExpiryWarningIcon();
        }
    }

    private void GalleryVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GalleryViewModel.HasCertExpiryAlert) or nameof(GalleryViewModel.HasCertExpiredAny))
            RefreshExpiryWarningIcon();
    }

    private void DashboardVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.SecretExpiryAlertCount))
        {
            _cachedSecretExpiryCount = _dashboardVm.SecretExpiryAlertCount;
            RefreshExpiryWarningIcon();
        }
    }

    private async void ShellWindow_AppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_suppressCloseConfirm) return;

        // Cancel closing once with Cancel=true, then terminate via Exit() after cleanup completes.
        // Because this is async void, an exception inside the handler would crash the process,
        // so wrap it in try-catch-finally to guarantee termination even if cleanup fails.
        args.Cancel = true;

        try
        {
            await _profileVm.AutoSaveDraftAsync("WindowClosing");
            await ValidateSettingsBeforeLeaveAsync(showDialog: false);

            if (NavFrame.Content is SecretsPage slPageClose && slPageClose.ViewModel.HasUnsavedChanges)
                await slPageClose.ViewModel.AutoSaveOnNavigateAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError("ShellWindow_AppWindowClosing: pre-exit cleanup failed. [{ExType}]", ex.GetType().Name);
        }
        finally
        {
            _closed = true;
            Application.Current.Exit();
        }
    }

    private async Task ValidateSettingsBeforeLeaveAsync(bool showDialog)
    {
        if (_session.LastActivePageTag != "settings") return;
        var settingsVm = ((App)Application.Current).Services
            .GetRequiredService<ViewModels.AppSettingsViewModel>();
        await settingsVm.ValidateAutoBackupAsync(showDialog);
    }

    private void UpdateTitleBarButtonColors(bool isLight)
    {
        var tb = AppWindow.TitleBar;
        if (isLight)
        {
            tb.ButtonForegroundColor        = Color.FromArgb(255,   0,   0,   0);
            tb.ButtonHoverBackgroundColor   = Color.FromArgb(255, 210, 210, 210);
            tb.ButtonHoverForegroundColor   = Color.FromArgb(255,   0,   0,   0);
            tb.ButtonPressedBackgroundColor = Color.FromArgb(255, 180, 180, 180);
            tb.ButtonPressedForegroundColor = Color.FromArgb(255,   0,   0,   0);
            tb.ButtonInactiveForegroundColor= Color.FromArgb(255, 120, 120, 120);
        }
        else
        {
            tb.ButtonForegroundColor        = null;
            tb.ButtonHoverBackgroundColor   = null;
            tb.ButtonHoverForegroundColor   = null;
            tb.ButtonPressedBackgroundColor = null;
            tb.ButtonPressedForegroundColor = null;
            tb.ButtonInactiveForegroundColor= null;
        }
    }

    private static string GetCurrentThemeSetting()
    {
        var app = (App)Application.Current;
        return app.Services.GetRequiredService<ViewModels.AppSettingsViewModel>().ThemeMode;
    }

    private async void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
            await TryNavigateToTagAsync(tag);
    }

    // J/K vi-style nav across MenuItems + FooterMenuItems as a single ordered list (same key
    // convention as the Secrets/TimeMachine/Gallery/Categories item lists). Mirrors arrow-key
    // behavior: moves keyboard focus only, without navigating. Navigation happens on Enter/Space
    // (NavView_ItemInvoked) or click, same as cursor-key movement.
    private void NavView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsCtrlDown()) return;
        if (e.Key != Windows.System.VirtualKey.J && e.Key != Windows.System.VirtualKey.K) return;

        // NavView.Content hosts NavFrame, so KeyDown from any TextBox/PasswordBox inside a page
        // (e.g. ProfilePage's fields) bubbles all the way up here too. Don't hijack J/K as nav
        // shortcuts while the user is actually typing into a text field.
        var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot != null && FocusManager.GetFocusedElement(xamlRoot) is TextBox or PasswordBox or RichEditBox or NumberBox)
            return;

        var allItems = NavView.MenuItems.OfType<NavigationViewItem>()
            .Concat(NavView.FooterMenuItems.OfType<NavigationViewItem>())
            .Where(i => i.Visibility == Visibility.Visible)
            .ToList();
        if (allItems.Count == 0) return;

        var focused = xamlRoot != null ? FocusManager.GetFocusedElement(xamlRoot) as NavigationViewItem : null;
        var currentIdx = focused != null ? allItems.IndexOf(focused)
            : NavView.SelectedItem is NavigationViewItem sel ? allItems.IndexOf(sel) : -1;
        var nextIdx = e.Key == Windows.System.VirtualKey.J
            ? Math.Min(currentIdx + 1, allItems.Count - 1)
            : Math.Max(currentIdx - 1, 0);
        e.Handled = true;
        if (nextIdx == currentIdx) return;

        allItems[nextIdx].Focus(FocusState.Keyboard);
    }

    private async void LockButton_Click(object sender, RoutedEventArgs e)
    {
        var vaultOpsVm = ((App)Application.Current).ShellScopeServices
            ?.GetRequiredService<ViewModels.VaultOperationsViewModel>();
        if (vaultOpsVm?.IsBusy == true)
        {
            _notification.Show(LK.Shell_LockNow, LK.Common_ErrorLockBlockedRunningTask, NotificationSeverity.Warning, TimeSpan.FromSeconds(3));
            return;
        }
        await ValidateSettingsBeforeLeaveAsync(showDialog: true);
        var guard = GetCurrentGuard();
        if (guard?.HasUnsavedChanges == true)
            await guard.GuardedSaveAsync();
        if (Application.Current is App app)
        {
            PurgeNavContext(); // physically sever NavPane's PII graphics cache before locking
            app.Lock();
        }
    }

    private bool _shortcutsDialogOpen;

    private async void ShortcutsButton_Click(object sender, RoutedEventArgs e)
        => await ShowShortcutsDialogAsync();

    private async void ShellWindow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsCtrlDown()) return;

        // Fail-safe default: every case below is skipped without action while a ContentDialog/Popup is
        // open, unless the key is explicitly exempted here. Deliberately NOT setting e.Handled - this
        // fires during the tunneling PreviewKeyDown pass, so leaving it unhandled lets the key keep
        // tunneling down to a dialog's own handler (e.g. SecretDraftCompareContent's own Ctrl+H reveal
        // toggle). A new case added below never needs its own popup check - it's already covered here.
        // Ctrl+L is the sole exemption: locking must never be suppressed by an open dialog.
        if (e.Key != Windows.System.VirtualKey.L && IsAnyPopupOpen())
            return;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.F:
                e.Handled = true;
                (NavFrame.Content as ISearchFocusable)?.FocusSearchBox();
                break;
            case Windows.System.VirtualKey.K:
                e.Handled = true;
                await ShowShortcutsDialogAsync();
                break;
            case Windows.System.VirtualKey.H:
                // Only claim the key when the current page actually implements the toggle - otherwise
                // e.Handled=true here (set during the tunneling PreviewKeyDown pass) would suppress the
                // bubbling KeyDown of a nested control's own Ctrl+H handler (e.g. VaultSettingsControl's
                // password fields inside SettingsPage) before it ever gets a chance to run.
                if (NavFrame.Content is IRevealToggleable revealable)
                {
                    e.Handled = true;
                    revealable.ToggleAllRevealed();
                }
                break;
            case Windows.System.VirtualKey.L:
                e.Handled = true;
                PurgeNavContext(); // physically sever NavPane's PII graphics cache before locking
                ((App)Application.Current).Lock();
                break;
        }
    }

    private async Task ShowShortcutsDialogAsync()
    {
        if (_shortcutsDialogOpen) return;
        _shortcutsDialogOpen = true;
        try
        {
            var dialog = new KeyboardShortcutsDialog
            {
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
                RequestedTheme = ((FrameworkElement)Content).RequestedTheme,
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _shortcutsDialogOpen = false;
        }
    }

    private static bool IsCtrlDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    // ContentDialog (and flyouts/teaching tips) render in a separate Popup layer above NavFrame's
    // page content. GetOpenPopupsForXamlRoot is the supported way to detect that layer is active,
    // so window-level shortcuts can defer to whatever's currently on top instead of reaching past it.
    private bool IsAnyPopupOpen()
    {
        var xamlRoot = ((FrameworkElement)Content).XamlRoot;
        return xamlRoot != null && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Count > 0;
    }

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
        => await TryNavigateToTagAsync("about");

    private async Task TryNavigateToTagAsync(string tag, int? jumpSecretId = null)
    {
        try
        {
            if (_session.LastActivePageTag == tag) return;
            // While in read-only restricted mode, structurally block entry via any route other
            // than the side menu (e.g. a jump via Messenger) as well
            if (_session.IsReadOnlyRestricted && IsReadOnlyRestrictedLockedTag(tag)) return;

            await ValidateSettingsBeforeLeaveAsync(showDialog: true);
            if (NavFrame.Content is SecretsPage slPage)
                await slPage.ViewModel.AutoSaveOnNavigateAsync();
            NavigateToTag(tag, jumpSecretId);
        }
        catch (Exception ex)
        {
            // An exception leaking out via NavigateToTagMessage (inside DispatcherQueue.TryEnqueue)
            // would become an unhandled exception on the DispatcherQueue thread and crash silently, so catch it here.
            Logger.LogError("[TryNavigateToTag] Navigation error. [{ExType}]", ex.GetType().Name);
        }
    }

    private static bool IsReadOnlyRestrictedLockedTag(string tag) =>
        tag is "timemachine" or "auditlog" or "settings";

    private void ApplyReadOnlyRestrictedLockdown()
    {
        if (!_session.IsReadOnlyRestricted) return;

        // Since navigation restrictions already make read-only restricted mode obvious, the lock
        // icon's yellow-tinted color change (an interim measure from before restricted mode was
        // implemented) has been removed. It stays the default danger color (red).
        NavItemTimeMachine.IsEnabled = false;
        NavItemAuditLog.IsEnabled    = false;
        NavItemSettings.IsEnabled    = false;
    }

    private IUnsavedChangesGuard? GetCurrentGuard() =>
        NavFrame.Content switch
        {
            SecretsPage p => p.ViewModel as IUnsavedChangesGuard,
            _                => null,
        };

    private readonly Dictionary<string, Page> _pageCache = new();

    private void NavigateToTag(string tag, int? jumpSecretId = null)
    {
        Logger.LogInformation("[NavigateToTag] tag={Tag}", tag);
        _session.LastActivePageTag = tag;
        _session.PendingJumpSecretId = jumpSecretId; // null clears it, non-null sets the jump target

        if (!_pageCache.TryGetValue(tag, out var page))
        {
            var app = (App)Application.Current;
            var rootSvc  = app.Services;
            var scopeSvc = app.ShellScopeServices!;
            // Secret-bearing pages resolve from the scope (prevents a Captive Dependency)
            // Settings / About stay on ROOT (no Scoped dependencies)
            page = tag switch
            {
                "dashboard"   => scopeSvc.GetRequiredService<DashboardPage>(),
                "secrets"     => scopeSvc.GetRequiredService<SecretsPage>(),
                "gallery"     => scopeSvc.GetRequiredService<GalleryPage>(),
                "timemachine" => scopeSvc.GetRequiredService<TimeMachinePage>(),
                "auditlog"    => scopeSvc.GetRequiredService<AuditLogPage>(),
                "profile"     => scopeSvc.GetRequiredService<ProfilePage>(),
                "settings"    => rootSvc.GetRequiredService<SettingsPage>(),
                "about"       => rootSvc.GetRequiredService<AboutPage>(),
                _             => throw new InvalidOperationException($"Unknown tag: {tag}")
            };
            _pageCache[tag] = page;
        }

        // NavFrame.Content is assigned directly rather than via Frame.Navigate (required by the
        // scoped-DI _pageCache pattern above), so Page.OnNavigatedTo/OnNavigatedFrom never fire.
        // IPageActivationAware.Activated()/Deactivated() stand in for them here instead.
        if (NavFrame.Content is IPageActivationAware oldPage) oldPage.Deactivated();
        NavFrame.Content = page;
        if (page is IPageActivationAware newPage) newPage.Activated();
        NavEmptyHint.Visibility = Visibility.Collapsed;

        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag) { NavView.SelectedItem = item; return; }
        }
        foreach (var item in NavView.FooterMenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag) { NavView.SelectedItem = item; return; }
        }
        NavView.SelectedItem = null;
    }

    private void OnUserActivity(object sender, object e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastActivityReset).TotalSeconds < 1) return;
        _lastActivityReset = now;
        _autoLock.ResetTimer();
    }

    private void SyncAvatarItemVisibility()
    {
        bool collapsed = !NavView.IsPaneOpen;
        NavPaneExpandedHeader.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        NavPaneCollapsedHeader.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TitleBar_PaneToggleRequested(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
        => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void RefreshTitleLabel() => _ = RefreshTitleLabelAsync();

    private async Task RefreshTitleLabelAsync()
    {
        try
        {
            var vaultNo      = _session.DisplayedVaultNumber;
            var secretCount  = await _secrets.CountActiveAsync();
            var galleryCount = await _storedFiles.CountAllAsync();
            var size         = FormatDbSizeCompact(_connectionProvider.ActiveVaultDbPath);
            DispatcherQueue.TryEnqueue(() =>
            {
                TitleCountText.Text = $"{secretCount} / {galleryCount} / {size}";
                if (vaultNo.HasValue)
                {
                    TitleVaultText.Text = $"{vaultNo}";
                    TitleVaultPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    TitleVaultPanel.Visibility = Visibility.Collapsed;
                }
            });
        }
        catch { }
    }

    private static string FormatDbSizeCompact(string? path)
    {
        if (path == null || !File.Exists(path)) return "0.0MB";
        var bytes = new FileInfo(path).Length;
        var walPath = path + "-wal";
        if (File.Exists(walPath)) bytes += new FileInfo(walPath).Length;
        var mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024.0 ? $"{mb / 1024.0:F2}GB" : $"{mb:F1}MB";
    }

    private async Task LoadNavPaneAvatarAsync()
    {
        var bytes = _session.AvatarBytes ?? await _avatarService.LoadAsync(_session.GetKey());
        var displayName = await _profileService.LoadDisplayNameAsync();
        await UpdateNavAvatarAsync(bytes);
        SetDisplayName(displayName);
        await RefreshProfileExpiryAlertAsync();
    }

    private void SetDisplayName(string? name)
    {
        // Confine the plaintext PII to a SecureCharBuffer. ToDisplayString() (not `new string(Span)`)
        // caches the string in _displayNameBuf's _displayCache, which is what lets Dispose() below
        // reach it with ZeroStringInternals - bypassing the cache would leave this string's plaintext
        // un-zeroed on the heap even after _displayNameBuf.Dispose() runs.
        // On lock, PurgeNavContext() sets Text = "" before _displayNameBuf.Dispose() performs the ZeroMemory.
        _displayNameBuf.SetFromSpan((name ?? string.Empty).AsSpan());
        var display = _displayNameBuf.ToDisplayString();
        NavDisplayNameText.Text = display;
        ToolTipService.SetToolTip(TitleBarAvatarPicture, _displayNameBuf.IsEmpty ? null : (object)display);
        ToolTipService.SetToolTip(NavAvatarPictureSmall, _displayNameBuf.IsEmpty ? null : (object)display);
    }

    // Physically severs the NavPane's PII display right before locking.
    // Setting PersonPicture.ProfilePicture = null releases the C# reference to the decoded pixel cache,
    // and Text = string.Empty releases the reference to the display name string, before _displayNameBuf is ZeroMemory'd.
    private void PurgeNavContext()
    {
        NavAvatarPicture.ProfilePicture = null;
        TitleBarAvatarPicture.ProfilePicture = null;
        NavAvatarPictureSmall.ProfilePicture = null;
        NavDisplayNameText.Text = string.Empty;
        ToolTipService.SetToolTip(TitleBarAvatarPicture, null);
        ToolTipService.SetToolTip(NavAvatarPictureSmall, null);
        _displayNameBuf.Dispose();
    }

    private async Task RefreshProfileExpiryAlertAsync()
    {
        try
        {
            // Receive the date-only ProfileExpiryInfo instead of the PII-bearing ProfileEditModel
            var dek = _session.GetKey();
            var info = await _profileService.GetExpiryInfoAsync(dek);
            var today = DateTime.Today;
            _hasProfileExpiryAlert =
                (info.Id1Expiry.HasValue && info.Id1Expiry.Value.ToLocalTime().Date < today.AddDays(AppConstants.ProfileIdLicenseWarnDays)) ||
                (info.Id2Expiry.HasValue && info.Id2Expiry.Value.ToLocalTime().Date < today.AddDays(AppConstants.ProfileIdLicenseWarnDays)) ||
                (info.Id3Expiry.HasValue && info.Id3Expiry.Value.ToLocalTime().Date < today.AddDays(AppConstants.ProfilePassportWarnDays));
            _hasProfileExpiredPastDue =
                (info.Id1Expiry.HasValue && info.Id1Expiry.Value.ToLocalTime().Date < today) ||
                (info.Id2Expiry.HasValue && info.Id2Expiry.Value.ToLocalTime().Date < today) ||
                (info.Id3Expiry.HasValue && info.Id3Expiry.Value.ToLocalTime().Date < today);
        }
        catch { }
        RefreshExpiryWarningIcon();
    }

    private void OnDbIntegrityWarning(int delta)
    {
        // Safely parse the existing count (treat unset or non-numeric values as 0)
        int current = int.TryParse(TitleBarWarningCountBadge.Text, out var n) ? n : 0;
        int updated = current + delta;
        TitleBarTitleWarningIcon.Visibility  = updated > 0 ? Visibility.Visible : Visibility.Collapsed;
        TitleBarWarningCountBadge.Text       = updated.ToString();
        TitleBarWarningCountBadge.Visibility = updated > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshCachedSecretExpiryCountAsync()
    {
        try
        {
            _cachedSecretExpiryCount       = await _secrets.CountExpiryAlertsAsync(AppConstants.SecretPasswordWarnDays);
            _cachedSecretHasExpiredPastDue = await _secrets.CountExpiredAsync() > 0;
        }
        catch { }
        RefreshExpiryWarningIcon();
    }

    // TimeMachine has nothing to show until at least one secret has ever existed in this vault
    // (active or deleted - its history covers both). Disabling the nav item in that state avoids
    // sending a first-time user into a screen that's empty no matter what they click.
    // ApplyReadOnlyRestrictedLockdown() already force-disables this nav item under the emergency
    // access code's restricted mode; never override that decision here.
    private async Task RefreshTimeMachineNavEnabledAsync()
    {
        if (_session.IsReadOnlyRestricted) return;
        // Once true it never needs to go back to false (a secret's history survives its own
        // deletion - see CountAllIncludingDeletedAsync), so skip the query once already confirmed.
        if (_timeMachineNavConfirmedHasData) return;
        bool hasAny;
        try { hasAny = await _secrets.CountAllIncludingDeletedAsync() > 0; }
        catch { hasAny = true; } // fail open - never let a transient query error hide TimeMachine's recovery data behind a disabled nav item
        NavItemTimeMachine.IsEnabled = hasAny;
        if (hasAny) _timeMachineNavConfirmedHasData = true;
    }

    // Same reasoning as RefreshTimeMachineNavEnabledAsync: nothing to show while the audit log is
    // empty (fresh vault), so disable the nav item until at least one entry exists. The dashboard's
    // own jump-link is gated separately in DashboardViewModel, via HasRecentAuditLogs.
    private async Task RefreshAuditLogNavEnabledAsync()
    {
        if (_session.IsReadOnlyRestricted) return;
        // Once true it never needs to go back to false (log entries only ever accumulate until the
        // 180-day rotation, which self-logs its own AuditLogsPurged entry right after). Skipping the
        // query once already confirmed also matters here specifically: AuditLogWrittenMessage can fire
        // very frequently (SecretViewed on every list click, clipboard copies, etc.), and without this
        // guard every one of those would otherwise re-issue a DB round trip for no reason.
        if (_auditLogNavConfirmedHasData) return;
        bool hasAny;
        try { hasAny = await _auditLog.HasAnyLogsAsync(); }
        catch { hasAny = true; } // fail open - same rationale as RefreshTimeMachineNavEnabledAsync
        NavItemAuditLog.IsEnabled = hasAny;
        if (hasAny) _auditLogNavConfirmedHasData = true;
    }

    private void RefreshExpiryWarningIcon()
    {
        // SecretsVm is only settled after the page loads; use the cached values before that.
        int  secretExpiryCount       = SecretsVm.ExpiryAlertCount > 0 ? SecretsVm.ExpiryAlertCount  : _cachedSecretExpiryCount;
        bool secretHasExpiredPastDue = SecretsVm.ExpiryAlertCount > 0 ? SecretsVm.HasExpiredPastDue  : _cachedSecretHasExpiredPastDue;
        // hasExpired must reflect items that are genuinely past due, not merely "within the warning
        // window" - _hasProfileExpiryAlert/secretExpiryCount/HasCertExpiryAlert are combined
        // (expired + expiring-soon) counts used for hasWarning/visible/total below, not for this.
        bool hasExpired  = _hasProfileExpiredPastDue || secretHasExpiredPastDue || _galleryVm.HasCertExpiredAny;
        bool hasWarning  = _hasProfileExpiryAlert || secretExpiryCount > 0 || _galleryVm.HasCertExpiryAlert;
        bool visible     = hasExpired || hasWarning;
        int  total       = secretExpiryCount + _galleryVm.CertExpiryAlertCount + (_hasProfileExpiryAlert ? 1 : 0);
        bool isLight     = ((FrameworkElement)Content).ActualTheme == ElementTheme.Light;
        DispatcherQueue.TryEnqueue(() =>
        {
            // Red once anything is actually expired (not just approaching), gold while it's only "expiring soon".
            var brush = hasExpired
                ? (isLight ? _criticalBrushLight : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"])
                : (isLight ? _cautionBrushLight  : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"]);
            ExpiryWarningIcon.Glyph      = "\uEC92"; // Calendar (kept consistent with the expiry icon on DashboardPage, etc.)
            ExpiryWarningIcon.Foreground = brush;
            ExpiryWarningIcon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ExpiryCountText.Text         = total.ToString();
            ExpiryCountText.Foreground   = brush;
            ExpiryCountText.Visibility   = visible ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private async Task UpdateNavAvatarAsync(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            NavAvatarPicture.ProfilePicture = null;
            TitleBarAvatarPicture.ProfilePicture = null;
            NavAvatarPictureSmall.ProfilePicture = null;
            return;
        }
        // Copy the buffer that spans the await into a GC.AllocateArray<byte>(pinned: true) POH-pinned
        // region, eliminating stale-address ghosts left behind by GC compaction.
        // ZeroMemory'ing pinned after SetSourceAsync completes ensures the wipe hits the correct address.
        var pinned = GC.AllocateArray<byte>(bytes.Length, pinned: true);
        bytes.AsSpan().CopyTo(pinned.AsSpan());
        try
        {
            using var ms = new MemoryStream(pinned, 0, pinned.Length, writable: false);
            var ras = ms.AsRandomAccessStream();
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ras);
            NavAvatarPicture.ProfilePicture = bmp;
            TitleBarAvatarPicture.ProfilePicture = bmp;
            NavAvatarPictureSmall.ProfilePicture = bmp;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinned.AsSpan());
            // AppSession.AvatarBytes is documented as "cached in memory while unlocked" - owned and
            // lifecycle-managed by AppSession itself (zeroed on Lock()), not by this method. When
            // bytes is that same reference (LoadNavPaneAvatarAsync's `_session.AvatarBytes ??
            // _avatarService.LoadAsync(...)` fallback: LoadAsync's cache-miss path also assigns into
            // and returns session.AvatarBytes, so this is in fact the common case, not an edge case),
            // leave it alone so the session-wide cache survives this NavPane refresh. Nulling it here
            // used to discard that cache on every ShellWindow startup, forcing a redundant DB
            // re-decrypt on the very next read (e.g. ProfileViewModel.LoadAsync's own `_session.
            // AvatarBytes ?? ...` fallback).
            // A genuinely different reference (e.g. AvatarChangedMessage's disposable .ToArray() copy)
            // is this method's own temporary and must still be wiped.
            if (!ReferenceEquals(_session.AvatarBytes, bytes))
                CryptographicOperations.ZeroMemory(bytes.AsSpan());
        }
    }
}
