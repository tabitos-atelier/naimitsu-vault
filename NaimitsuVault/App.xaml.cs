// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Dispatching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Pages;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using NaimitsuVault.Views;
using Microsoft.Data.Sqlite;
using NaimitsuVault.Helpers;

namespace NaimitsuVault;

public partial class App : Application
{
    private static readonly ILogger<App> Logger = AppLog.For<App>();

    public IServiceProvider Services { get; private set; } = null!;

    // A per-session scope. On lock, Dispose is called, which in turn calls each AddScoped VM's Dispose() to ZeroMemory secrets.
    private IServiceScope? _shellScope;
    public IServiceProvider? ShellScopeServices => _shellScope?.ServiceProvider;

    // Set to true once ShellWindow has ever been opened.
    // A flag that prevents restoring (replacing files) from a session that has already been unlocked.
    private bool _hasBeenUnlockedThisSession = false;

    // Guards ShellWindow_Closed's backup/log/shadow-write body against running more than once. The
    // Closed event has been observed firing twice for a single real app exit (ShellWindow_AppWindowClosing
    // sets args.Cancel=true and calls Application.Current.Exit() itself, which appears to re-enter the
    // closing sequence). Without this guard, a second RunBackup() call sees the first run's own
    // AutoBackupExecuted audit-log write as a content change, creating a second generation and a second
    // audit entry for what was really one exit.
    private bool _exitCleanupDone;

    // Guards LockAsync's whole body (not just LockCycleOrchestrator's internal _isRunning, which
    // only covers Steps 1-5) against re-entry. IdleTimeoutService can fire its lock callback twice
    // for the same idle-expiry window (its own periodic Tick racing a CheckNow() from window
    // reactivation) - without this flag, a second concurrent LockAsync() call sees the orchestrator
    // no-op (still correct) but then falls through to ShowUnlockWindowAsync() anyway, activating a
    // second UnlockWindow while the first invocation's ShellWindow.Close() is still pending,
    // producing a brief frame where UnlockWindow and ShellWindow are both visible.
    private bool _lockInFlight;

    private Window? _shellWindow;
    private Window? _unlockWindow;
    // The tcs that resolves ShowUnlockWindowAsync's await. Shared so that recovery-related flows
    // can TrySetResult(true) before closing _unlockWindow (this prevents a race where the Closed
    // handler's TrySetResult(false) fires first and causes OnLaunched to call Exit()).
    private TaskCompletionSource<bool>? _unlockCompletionSource;
    private bool    _wasCorruptedOnStartup;
    /// <summary>
    /// Set by LockAfterDataWrite() and consumed exactly once by the next ShowUnlockWindowAsync().
    /// Displays guidance text in UnlockWindow's ErrorBorder after a backup/export completes.
    /// </summary>
    private string? _pendingUnlockInfoMessage;
    public WinUIEx.WindowEx? ActiveShellWindow => _shellWindow as WinUIEx.WindowEx;
    private readonly LockCycleOrchestrator _lockOrchestrator = new();
    private WinUIEx.TrayIcon? _trayIcon;

    // Used by Program.cs's AppInstance.Activated handler to marshal onto the UI thread
    public static DispatcherQueue? UiDispatcherQueue { get; private set; }

    // Rev7: used by LockAsync's HasThreadAccess guard.
    // Since the App constructor runs on the UI thread, GetForCurrentThread() returns the correct UI thread's DispatcherQueue.
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    public App()
    {
        UiDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();

        // Last-resort safety net for an exception that escapes every per-call try-catch (e.g. one
        // corrupted Secret's decrypt failure bubbling up through a LoadAsync loop). Without this,
        // WinUI 3 terminates the whole process on any unhandled exception reaching an async void
        // event handler (Page Loaded, Click, etc.), turning a single bad record into a full crash.
        UnhandledException += (_, e) =>
        {
            Logger.LogCritical("UnhandledException: {ExType}", e.Exception.GetType().Name);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // The runtime always wraps the fault in an AggregateException, so the outer type name alone
            // says nothing about the cause. Also log the flattened inner exception type names. Type
            // names only - never Message/StackTrace, which can carry file paths, column names, etc.
            var innerTypes = string.Join(", ", e.Exception.Flatten().InnerExceptions.Select(x => x.GetType().Name));
            Logger.LogError("UnobservedTaskException: {ExType} [{InnerTypes}]", e.Exception.GetType().Name, innerTypes);
            e.SetObserved();
        };
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null) return;
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "NaimitsuVault.ico");
        _trayIcon = new WinUIEx.TrayIcon(1u, iconPath, LocalizationManager.Get("System.Product.Name"));
        _trayIcon.IsVisible = true;
        _trayIcon.Selected += TrayIcon_Selected;
        _trayIcon.ContextMenu += TrayIcon_ContextMenu;
    }

    private void TrayIcon_ContextMenu(WinUIEx.TrayIcon sender, WinUIEx.TrayIconEventArgs args)
    {
        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        var exitItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
        {
            Text = LocalizationManager.Get("Shell.TrayExit"),
        };
        // Close whichever window is currently active so its existing Closed handler runs the
        // normal shutdown sequence (backup, shadow file write, Exit()) instead of duplicating it here.
        exitItem.Click += (_, _) =>
        {
            var active = _shellWindow ?? _unlockWindow;
            if (active != null)
                active.Close();
            else
                Exit();
        };
        menu.Items.Add(exitItem);
        args.Flyout = menu;
    }

    private void TrayIcon_Selected(WinUIEx.TrayIcon sender, WinUIEx.TrayIconEventArgs args)
    {
        UiDispatcherQueue?.TryEnqueue(() =>
        {
            // Locked state (UnlockWindow exists) - restore regardless of hidden/minimized
            if (_unlockWindow != null)
            {
                var unlockHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_unlockWindow);
                ShowWindow(unlockHwnd, 9 /* SW_RESTORE */);
                Win32InputSender.SetForegroundWindow(unlockHwnd);
                _unlockWindow.Activate();
                return;
            }

            if (_shellWindow == null) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_shellWindow);

            bool isActivelyShown = _shellWindow.AppWindow.IsVisible &&
                _shellWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
                {
                    State: Microsoft.UI.Windowing.OverlappedPresenterState.Restored
                        or Microsoft.UI.Windowing.OverlappedPresenterState.Maximized
                };

            if (isActivelyShown)
            {
                var settingsVm = Services.GetRequiredService<AppSettingsViewModel>();
                if (settingsVm.IsHideFromTaskbarWhenMinimized)
                    _shellWindow.AppWindow.Hide();
                else
                    ShowWindow(hwnd, 6 /* SW_MINIMIZE */);
            }
            else
            {
                ShowWindow(hwnd, 9 /* SW_RESTORE */);
                Win32InputSender.SetForegroundWindow(hwnd);
                _shellWindow.Activate();
            }
        });
    }

    public void BringToFront()
    {
        // Prefer the locked state (UnlockWindow exists); fall back to ShellWindow if not.
        // ShellWindow is restored via Activate() even while stowed in the tray (AppWindow.Hide),
        // so branching on IsVisible isn't needed (doing so would make target null while hidden and produce no response).
        Window? target = _unlockWindow ?? (Window?)_shellWindow;
        if (target == null) return;
        // Restore from minimized before bringing it to the foreground
        var targetHwnd = WinRT.Interop.WindowNative.GetWindowHandle(target);
        ShowWindow(targetHwnd, 9 /* SW_RESTORE */);
        Win32InputSender.SetForegroundWindow(targetHwnd);
        target.Activate();
    }

    // SetForegroundWindow's P/Invoke declaration is centralized in Win32InputSender (shared with
    // DirectInjectionDialog and WindowService's viewer-window foregrounding - see
    // design/03-06-06-01-direct-injection-design.md §3.1); this class only keeps ShowWindow, which
    // isn't part of that centralized surface.
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    // WinUI 3's OnLaunched must be async void (a constraint of the override signature).
    // Because it's async void, an exception thrown synchronously before the first await never
    // reaches UnhandledException, but wrapping the entire method in try-catch makes this a non-issue in practice.
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Logger.LogInformation("NaimitsuVault startup begin");

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Path.Combine(baseDir, "data");

            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            // Resolve the DB path: fixed name NaimitsuVault.nkdb. The shadow is found by reverse lookup via the NKDB_SHADOW magic.
            var dbPath = Path.Combine(dataDir, "NaimitsuVault.nkdb");

            var svc = new ServiceCollection();
            ConfigureServices(svc, dbPath);
            Services = svc.BuildServiceProvider();

            // nkdb corruption check + shadow auto-recovery
            var appSessionOnStartup = Services.GetRequiredService<AppSession>();
            bool nkdbExists  = File.Exists(dbPath);
            bool nkdbHealthy = nkdbExists && await ShadowFileService.IsFileHealthyAsync(dbPath);
            if (!nkdbHealthy)
            {
                var shadowCandidates = ShadowFileService.FindByMagic(dataDir, ShadowFileService.NKDB_SHADOW);
                if (shadowCandidates.Count == 1 &&
                    await ShadowFileService.IsShadowHealthyAsync(shadowCandidates[0], ShadowFileService.NKDB_SHADOW))
                {
                    bool restored = await ShadowFileService.TryRestoreFromShadowAsync(
                        shadowCandidates[0], dbPath, ShadowFileService.NKDB_ORIGIN);
                    if (restored)
                    {
                        Logger.LogInformation("Auto-recovered nkdb from the shadow.");
                        appSessionOnStartup.UnifiedDbAutoRecovered = true;
                    }
                    else
                    {
                        Logger.LogWarning("Failed to recover nkdb from the shadow. Freezing the unified DB.");
                        appSessionOnStartup.IsUnifiedDbCorrupted = true;
                        _wasCorruptedOnStartup = true;
                    }
                }
                else if (!nkdbExists && shadowCandidates.Count == 0)
                {
                    // A completely fresh install: don't freeze (DatabaseInitializer creates it new)
                }
                else
                {
                    // nkdb is corrupted + no shadow / multiple shadows / shadow also corrupted -> Fail-Fast freeze
                    Logger.LogWarning("nkdb is corrupted with no shadow, multiple shadows, or a corrupted shadow. Freezing the unified DB.");
                    appSessionOnStartup.IsUnifiedDbCorrupted = true;
                    _wasCorruptedOnStartup = true;
                }
            }

            var initializer = Services.GetRequiredService<DatabaseInitializer>();
            try
            {
                await initializer.InitializeAsync();
            }
            catch (Exception dbEx)
            {
                // Detected unified DB corruption: don't back up or recreate it. Keep the corrupted
                // file as-is and show UnlockWindow in a frozen state to guide the user to the recovery screen.
                Logger.LogWarning("DB initialization failed. Setting the corruption flag and freezing the unlock screen. [{ExType}]", dbEx.GetType().Name);
                Services.GetRequiredService<AppSession>().IsUnifiedDbCorrupted = true;
                _wasCorruptedOnStartup = true;
            }

            // Check the auto-backup failure flag from the previous shutdown (the flag is deleted immediately after checking)
            bool autoBackupFailed = false;
            if (File.Exists(AutoBackupService.FailureFlagPath))
            {
                autoBackupFailed = true;
                try { File.Delete(AutoBackupService.FailureFlagPath); } catch { }
            }

            var loc = Services.GetRequiredService<ILocalizationService>();
            await loc.LoadForStartupAsync();
            // Created before UnlockWindow is shown - UnlockWindow's own AppWindow.Changed handler
            // can already hide it to the tray on minimize, so the icon must exist by then or the
            // window becomes unreachable (no taskbar entry, no tray icon to click).
            EnsureTrayIcon();

            var appSettingsVm = Services.GetRequiredService<AppSettingsViewModel>();
            // Skip DB-dependent settings loading when the unified DB is corrupted (proceed with default values)
            if (!_wasCorruptedOnStartup)
                await appSettingsVm.LoadAsync();

            var auth = Services.GetRequiredService<IAuthService>();
            // Skip the setup-required check when the unified DB is corrupted (it's DB-dependent). Always use unlock mode instead.
            bool isSetup = !_wasCorruptedOnStartup && await auth.IsSetupRequiredAsync();

            bool unlocked = await ShowUnlockWindowAsync(auth, isSetup, dbBackupFileName: null);
            if (!unlocked)
            {
                Logger.LogInformation("Authentication cancelled -> exiting");
                Exit();
                return;
            }

            // After unlock: run post-unlock housekeeping (WAL migration check, profile seeding,
            // 30-day soft-deleted secret purge). Under the emergency access code's (Route A)
            // read-only restricted mode, there's a contract of "fully suppress physical writes to
            // the DB," so guard the whole thing - every step potentially writes.
            var appSession = Services.GetRequiredService<AppSession>();
            if (!appSession.IsReadOnlyRestricted)
            {
                var dek = appSession.GetKey();
                await Services.GetRequiredService<PostUnlockMaintenanceService>().RunAsync(dek);
            }

            Logger.LogInformation("Authentication complete. Showing the main screen.");
            await CheckDbIntegrityAsync(dbPath, Services.GetRequiredService<AppSession>(),
                Services.GetRequiredService<IVaultConnectionProvider>());
            // Hide UnlockWindow first (so it doesn't overlap with ShellWindow)
            _unlockWindow?.AppWindow.Hide();
            // Registered as AddTransient, so a new instance is created every call (intentional).
            _shellScope = Services.CreateScope();
            _shellWindow = _shellScope.ServiceProvider.GetRequiredService<ShellWindow>();
            _hasBeenUnlockedThisSession = true;
            _shellWindow.Closed += ShellWindow_Closed;
            _shellWindow.Activate();
            // Close UnlockWindow only after ShellWindow's XAML Island is registered with the TSF.
            // Closing it first on Succeeded leaves the TSF blank and breaks Japanese IME input.
            var unlockWinOnLaunch = _unlockWindow;
            _unlockWindow = null;
            unlockWinOnLaunch?.Close();

            if (autoBackupFailed)
            {
                var notification = Services.GetRequiredService<IAppNotificationService>();
                notification.Show(LK.VaultSettings_WarningAutoBackupContext, LK.VaultSettings_WarningLastExitBackupFailedAlert, NotificationSeverity.Warning, TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            Logger.LogCritical("A fatal error occurred during startup. [{ExType}]", ex.GetType().Name);
            // Application.Exit() only posts a shutdown request to the message pump. When the crash
            // happens before any window was ever shown (e.g. during ShellWindow construction), the
            // pump may never settle it, leaving a zombie process behind. Force a hard process
            // termination as a backstop so a fatal startup error never lingers in Task Manager.
            Exit();
            Environment.Exit(1);
        }
    }

    private async Task<bool> ShowUnlockWindowAsync(IAuthService auth, bool isSetup, bool showCloseConfirm = false, string? dbBackupFileName = null)
    {
        var unlockVm = new UnlockViewModel(
            auth, isSetup, Services.GetRequiredService<AppSession>(), Services.GetRequiredService<ILogger<UnlockViewModel>>());
        var tcs = new TaskCompletionSource<bool>();
        _unlockCompletionSource = tcs;

        // Shows guidance text in ErrorBorder when locking after a backup/export completes.
        var infoMessage = _pendingUnlockInfoMessage;
        _pendingUnlockInfoMessage = null;

        var unlockWindow = new UnlockWindow(unlockVm, recoveryAllowed: !_hasBeenUnlockedThisSession)
        {
            ShowCloseConfirm = showCloseConfirm,
            DbBackupFileName = dbBackupFileName,
            InfoMessage      = infoMessage,
        };

        // Point the file picker's hwnd at UnlockWindow (for selecting the recovery QR)
        if (Services.GetRequiredService<IFilePickerService>() is WinUIFilePicker fp)
            fp.SetHwnd(WinRT.Interop.WindowNative.GetWindowHandle(unlockWindow));

        _unlockWindow = unlockWindow;
        unlockWindow.AppWindow.Changed += (s, e) =>
        {
            if (!unlockWindow.AppWindow.IsVisible) return;
            if (unlockWindow.AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized }) return;
            var settingsVm = Services.GetRequiredService<AppSettingsViewModel>();
            if (settingsVm.IsHideFromTaskbarWhenMinimized)
                unlockWindow.AppWindow.Hide();
        };
        unlockVm.Succeeded += (_, _) =>
        {
            tcs.TrySetResult(true);
            // Don't call Close here. The caller closes it after ShellWindow.Activate(),
            // which prevents the TSF document manager from going blank and keeps Japanese IME input working.
        };
        unlockWindow.Closed += (_, _) =>
        {
            _unlockWindow = null;
            tcs.TrySetResult(false);
            if (_unlockCompletionSource == tcs) _unlockCompletionSource = null;
        };
        unlockWindow.Activate();

        return await tcs.Task;
    }

    private async void ShellWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_exitCleanupDone) return;
        _exitCleanupDone = true;

        // Close all viewers on exit (ZeroMemory's SoftwareBitmap, prevents zombie processes)
        Services.GetRequiredService<IWindowService>().CloseAllViewers();
        _trayIcon?.Dispose();
        var session = Services.GetRequiredService<AppSession>();
        var autoBackup = Services.GetRequiredService<AutoBackupService>();
        autoBackup.IsReadOnlyRestricted = session.IsReadOnlyRestricted;
        var backupResult = autoBackup.RunBackup();
        if (backupResult == AutoBackupService.AutoBackupResult.Created)
        {
            // The DEK is still live here (session.Lock() has not run on this exit path), so the entry can be sealed normally.
            try { await Services.GetRequiredService<IAuditLogService>().LogAsync(AuditEventCode.AutoBackupExecuted, null, session.GetKey()); }
            catch (Exception ex) { Logger.LogWarning("Failed to log the automatic backup. [{ExType}]", ex.GetType().Name); }
        }
        try
        {
            var currentVaultDbNumber = session.CurrentVaultDbNumber;
            Services.GetRequiredService<ShadowFileService>().WriteAll(currentVaultDbNumber);
        }
        catch (Exception ex) { Logger.LogWarning("Failed to write shadow files on exit. [{ExType}]", ex.GetType().Name); }
        Exit();
    }

    public void Lock() => _ = LockAsync();

    /// <summary>
    /// A forced lock after a plaintext export completes or a new vault is created.
    /// In addition to the same behavior as Lock(), it displays guidance text in UnlockWindow's ErrorBorder.
    /// </summary>
    public void LockAfterDataWrite()
    {
        _pendingUnlockInfoMessage = LocalizationManager.Get("Common.MemoryClearedLock");
        _ = LockAsync();
    }

    private async Task LockAsync()
    {
        // ══ Rev7: UI thread guarantee guard ═══════════════════════════════════════
        // When driven from a non-UI thread (e.g. IdleTimeoutService), forcibly marshal onto the
        // UI thread via DispatcherQueue and re-enter.
        // WinUI 3's Frame.Content / Window.Close() are COM operations that require the UI thread;
        // without this guard, RPC_E_WRONG_THREAD would force-terminate the app before reaching the
        // ZeroMemory calls in Steps 4/5, killing the process with secret data left unwiped.
        if (!_dispatcherQueue.HasThreadAccess)
        {
            if (!_dispatcherQueue.TryEnqueue(() => { _ = LockAsync(); }))
                Logger.LogError("LockAsync: failed to re-dispatch to the DispatcherQueue. The lock will not run.");
            return;
        }

        // A second call arriving while one is already mid-flight (e.g. IdleTimeoutService's Tick
        // racing a CheckNow()) must be a true no-op: LockCycleOrchestrator's own _isRunning guard
        // only covers Steps 1-5, so without this the fall-through to ShowUnlockWindowAsync() below
        // would still activate a second UnlockWindow while the first call's ShellWindow.Close() is
        // still pending.
        if (_lockInFlight)
        {
            Logger.LogInformation("LockAsync: already in progress, ignoring the duplicate call.");
            return;
        }
        _lockInFlight = true;
        try
        {
            await LockCycleBodyAsync();
        }
        finally
        {
            _lockInFlight = false;
        }
    }

    private async Task LockCycleBodyAsync()
    {
        Logger.LogInformation("Executing lock");

        Services.GetRequiredService<IdleTimeoutService>().Stop();
        Services.GetRequiredService<IWindowService>().CloseAllViewers();
        // Deliberately close any open page-owned ContentDialog (e.g. SecretDraftCompareContent/
        // ProfileDraftCompareContent) and wait for its ZeroMemory cleanup to finish, the same way
        // CloseAllViewers() above does for ViewerWindow, before the owning ShellWindow is scrubbed/closed.
        await Services.GetRequiredService<IWindowService>().CloseActiveDialogsAsync();

        var oldShell = _shellWindow as ShellWindow;
        _shellWindow = null;

        // A local proxy wrapping ShellWindow's ScrubViewReferences -> Close
        IShellWindowProxy? shellProxy = oldShell != null
            ? new InlineProxy(
                scrub: () =>
                {
                    oldShell.Closed -= ShellWindow_Closed;
                    oldShell.DetachActivityHandlers();
                    oldShell.SuppressCloseConfirm();
                    oldShell.ScrubViewReferences();
                },
                close: () => oldShell.Close())
            : null;

        // Delegate the lock sequence to LockCycleOrchestrator (prevents double execution via _isRunning)
        var capturedScope = _shellScope;
        await _lockOrchestrator.ExecuteAsync(
            getInFlightTasks: capturedScope == null ? null : () =>
            {
                var reg = capturedScope.ServiceProvider.GetRequiredService<SessionTaskRegistry>();
                return reg.Snapshot()
                          .Select(t => t.CurrentSaveTask)
                          .Where(t => t is { IsCompleted: false })
                          .Cast<Task>()
                          .ToList();
            },
            barricadeScope: () =>
            {
                capturedScope?.ServiceProvider.GetRequiredService<SessionLockGuard>().Barricade();
                Logger.LogWarning("LockAsync: exceeded the 800ms timeout. Barricading both SessionLockGuard and ISessionGenerationGuard.");
            },
            barricadeApp: () => Services.GetRequiredService<ISessionGenerationGuard>().Barricade(),
            cancelScope: () => capturedScope?.ServiceProvider.GetRequiredService<SessionLockGuard>().Cancel(),
            shellProxy: shellProxy,
            disposeScope: () => { _shellScope?.Dispose(); _shellScope = null; },
            lockSession: () =>
            {
                // VaultOperationsViewModel.ClearSensitiveInputBuffers() has already run,
                // via disposeScope's (Step 4's) _shellScope.Dispose().
                Services.GetRequiredService<AppSession>().Lock();
            }
        );

        // 3. Background GC sweep (evicts ghost copies)
        _ = Task.Run(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        });

        // After the lock completes: clear the active secret DB path and release the connection pool entirely.
        // ClearActiveVault() only removes the path reference while the SQLite pool keeps holding
        // connections, so also release the physical handles immediately via ClearAllPools(). This
        // makes file replacement safe on the recovery screen.
        Services.GetRequiredService<IVaultConnectionProvider>().ClearActiveVault();
        SqliteConnection.ClearAllPools();

        try
        {
            var auth = Services.GetRequiredService<IAuthService>();
            bool isSetup = await auth.IsSetupRequiredAsync();

            bool success = await ShowUnlockWindowAsync(auth, isSetup, showCloseConfirm: true);
            if (success)
            {
                Logger.LogInformation("Re-authentication complete. Checking schema migration.");

                // ── Rev7: advance the generation via NextSession() (Reset() has been completely removed) ──────────
                // NextSession() atomically performs the following:
                //   1. Reset _barricaded to false (so DatabaseInitializer can call SaveChangesAsync again)
                //   2. Increment _sessionId (a zombie AppDbContext from the old generation dies instantly via ThrowIfInvalid)
                Services.GetRequiredService<ISessionGenerationGuard>().NextSession();

                var initializerOnRelock = Services.GetRequiredService<DatabaseInitializer>();
                try
                {
                    await initializerOnRelock.InitializeAsync();
                }
                catch (Exception dbEx)
                {
                    Logger.LogError("DB initialization failed after unlocking. Exiting safely. [{ExType}]", dbEx.GetType().Name);
                    Services.GetRequiredService<IWindowService>().CloseAllViewers();
                    _trayIcon?.Dispose(); _trayIcon = null;
                    Services.GetRequiredService<AutoBackupService>().RunBackup();
                    Exit();
                    return;
                }

                // After re-unlocking: run post-unlock housekeeping (WAL migration check, profile
                // seeding, 30-day soft-deleted secret purge). Under the emergency access code's
                // (Route A) read-only restricted mode, there's a contract of "fully suppress
                // physical writes to the DB," so guard the whole thing, the same rule as OnLaunched.
                var sessionOnRelock = Services.GetRequiredService<AppSession>();
                if (!sessionOnRelock.IsReadOnlyRestricted)
                {
                    var dekOnRelock = sessionOnRelock.GetKey();
                    await Services.GetRequiredService<PostUnlockMaintenanceService>().RunAsync(dekOnRelock);
                }

                Logger.LogInformation("Schema check complete. Showing the new main screen.");
                // A new instance is created via AddTransient after re-login as well (the old ShellWindow was already closed in Step 3).
                _shellScope = Services.CreateScope();
                _unlockWindow?.AppWindow.Hide();
                var newShell = _shellScope.ServiceProvider.GetRequiredService<ShellWindow>();
                newShell.Closed += ShellWindow_Closed;
                newShell.Activate();
                _shellWindow = newShell;
                _hasBeenUnlockedThisSession = true;
                var unlockWinOnRelock = _unlockWindow;
                _unlockWindow = null;
                unlockWinOnRelock?.Close();
            }
            else
            {
                Logger.LogInformation("Re-authentication cancelled -> exiting");
                Services.GetRequiredService<IWindowService>().CloseAllViewers();
                _trayIcon?.Dispose();
                _trayIcon = null;
                Services.GetRequiredService<AutoBackupService>().RunBackup();
                Exit();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("An unexpected exception occurred in the latter half of LockAsync. Exiting the app. [{ExType}]", ex.GetType().Name);
            Exit();
        }
    }

    // ── DB integrity check (PRAGMA quick_check) ────────────────────────────────────────

    private static async Task CheckDbIntegrityAsync(
        string unifiedDbPath, AppSession session, IVaultConnectionProvider connProvider)
    {
        session.UnifiedDbIntegrityWarning = !await ShadowFileService.IsFileHealthyAsync(unifiedDbPath);
        if (session.UnifiedDbIntegrityWarning)
            Logger.LogWarning("The unified DB's PRAGMA quick_check returned something other than 'ok'. Path={Path}", unifiedDbPath);

        var vaultPath = connProvider.ActiveVaultDbPath;
        if (vaultPath != null)
        {
            session.VaultDbIntegrityWarning = !await ShadowFileService.IsFileHealthyAsync(vaultPath);
            if (session.VaultDbIntegrityWarning)
                Logger.LogWarning("The vault DB's PRAGMA quick_check returned something other than 'ok'. Path={Path}", vaultPath);
        }
    }

    // ── Launching the emergency access / data recovery window ────────────────────────────────────────

    private RestoreWindow? _restoreWindow;

    public void OpenRestoreWindow(UnlockWindow unlockWin)
    {
        if (_restoreWindow != null)
        {
            _restoreWindow.Activate();
            return;
        }
        // Reject recovery (overwriting files) from a session that's already been unlocked, since it isn't safe.
        // UnlockWindow also hides the wrench for this case, so reaching here should only happen in abnormal cases.
        if (_hasBeenUnlockedThisSession) return;

        var auth = Services.GetRequiredService<IRestoreAuthService>();
        var vm   = new RestoreViewModel(auth, Services.GetRequiredService<ILogger<RestoreViewModel>>());
        var dq   = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        vm.RestoreSucceeded += (_, _) =>
        {
            // After recovery, AppSession is unauthenticated (no DEK), so ShellWindow can't be opened directly.
            // Regardless of the case, restart and go through the re-authentication flow.
            if (!dq.TryEnqueue(() =>
            {
                // AppInstance.Restart() alone doesn't terminate this process, and this process still
                // holds the "NaimitsuVault" single-instance key (see Program.cs's
                // FindOrRegisterForKey), so the newly launched process would see IsCurrent==false,
                // redirect activation back to this still-running process, and exit immediately - the
                // app would never actually restart. Process.Start + UnregisterKey + Exit() is the
                // proven pattern already used for exactly this purpose in
                // AppSettingsViewModel.RestartApp.
                SqliteConnection.ClearAllPools();
                Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().UnregisterKey();
                Process.Start(Environment.ProcessPath!);
                Exit();
            }))
                Logger.LogError("RestoreSucceeded: failed to dispatch to the DispatcherQueue. The restart will not happen.");
        };
        _restoreWindow = new RestoreWindow(vm);
        _restoreWindow.Closed += (_, _) =>
        {
            _restoreWindow = null;
            unlockWin.SetRestoreMode(false);
        };
        _restoreWindow.Activate();
        // Leave UnlockWindow open behind it, without closing it (allows returning to it on cancel)
    }

    public async void OpenEmergencyAccessControl(UnlockWindow unlockWin)
    {
        try
        {
            var auth    = Services.GetRequiredService<IAuthService>();
            // The file picker singleton already points at UnlockWindow (see ShowUnlockWindowAsync), which
            // is the window hosting this dialog.
            using var vm = new EmergencyAccessViewModel(
                auth, Services.GetRequiredService<IFilePickerService>(), Services.GetRequiredService<ILogger<EmergencyAccessViewModel>>());
            var control = new NaimitsuVault.Views.Controls.EmergencyAccessControl(vm);
            var root    = (Microsoft.UI.Xaml.FrameworkElement)unlockWin.Content;

            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = new Microsoft.UI.Xaml.Controls.StackPanel
                {
                    Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uED14", VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center },
                        new Microsoft.UI.Xaml.Controls.TextBlock { Text = LocalizationManager.Get("Emergency.WindowTitle"), VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center },
                    },
                },
                Content         = control,
                XamlRoot        = root.XamlRoot,
                RequestedTheme  = root.RequestedTheme,
            };

            // No standard CloseButtonText: EmergencyAccessControl's own Close button matches the
            // Unlock button's size (Height=40, Stretch) instead of the default ContentDialog footer button.
            control.CloseRequested += (_, _) => dialog.Hide();

            bool succeeded = false;
            vm.RecoverySucceeded += (_, _) =>
            {
                succeeded = true;
                dialog.Hide();
            };

            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                control.Scrub();
            }

            if (succeeded)
                TransitionToShellFromRecovery();
        }
        catch (Exception ex)
        {
            Logger.LogError("An unexpected exception occurred in OpenEmergencyAccessControl. [{ExType}]", ex.GetType().Name);
        }
    }

    private BackupProgressWindow? _backupProgressWindow;

    /// <summary>
    /// Opens the progress window shown during a bulk backup/plaintext export.
    /// If it's already open, only updates the message (prevents creating duplicates).
    /// </summary>
    public BackupProgressWindow OpenBackupProgressWindow(string message)
    {
        if (_backupProgressWindow != null)
        {
            _backupProgressWindow.UpdateMessage(message);
            return _backupProgressWindow;
        }
        _backupProgressWindow = new BackupProgressWindow(message);
        _backupProgressWindow.Activate();
        return _backupProgressWindow;
    }

    /// <summary>The path for closing the progress window after a backup completes. Call this right after the atomic rename succeeds.</summary>
    public void CloseBackupProgressWindow()
    {
        _backupProgressWindow?.ForceClose();
        _backupProgressWindow = null;
    }

    // Fully delegate the main screen's startup/initialization to OnLaunched's continuation stream.
    // Creating ShellWindow ourselves here is strictly forbidden: it would race with the creation
    // logic on the OnLaunched side (woken up by tcs completion), causing a double-collision that
    // can leak scopes or trigger an ExecutionEngineException.
    private void TransitionToShellFromRecovery()
    {
        // If OnLaunched is waiting on ShowUnlockWindowAsync's tcs, a later Close() would settle
        // the tcs to false via the Closed handler and trigger Exit() (racing with
        // ShellWindow.Activate() and causing an ExecutionEngineException). Settle it to true first to neutralize this.
        _unlockCompletionSource?.TrySetResult(true);
        _unlockCompletionSource = null;
        _unlockWindow?.AppWindow.Hide();
    }

    private sealed class InlineProxy(Action scrub, Action close) : IShellWindowProxy
    {
        public void ScrubViewReferences() => scrub();
        public void Close() => close();
    }

    internal static void ConfigureServices(IServiceCollection services, string dbPath)
    {
        services.AddLogging(AppLog.Configure);

        // Unified DB (NaimitsuVault.nkdb): UnifiedMetadata + VaultRegistries
        services.AddDbContextFactory<UnifiedDbContext>(o =>
            o.UseSqlite($"Data Source={dbPath};Foreign Keys=True")
             .AddInterceptors(new SqliteSynchronousInterceptor()));

        // Vault DB (dynamic path): the Factory resolves the path via IVaultConnectionProvider
        services.AddSingleton<IVaultConnectionProvider, VaultConnectionProvider>();
        services.AddScoped<IDbContextFactory<AppDbContext>, AppDbContextFactory>();

        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<AppSession>();
        services.AddSingleton<ISecurityContext>(sp => sp.GetRequiredService<AppSession>());
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IRestoreAuthService>(sp =>
            (IRestoreAuthService)sp.GetRequiredService<IAuthService>());
        services.AddSingleton<ProfileService>();
        services.AddSingleton<PostUnlockMaintenanceService>();
        services.AddSingleton<AvatarService>();
        services.AddSingleton<AutoBackupService>();
        services.AddSingleton<FaviconService>();

        // Repository - AddScoped (purges leftover ChangeTracker entity cache entries on lock)
        services.AddScoped<SecretRepository>();
        services.AddScoped<StoredFileRepository>();
        services.AddScoped<SecretHistoryRepository>();  // changed from Singleton to Scoped to eliminate leftover cache entries
        services.AddScoped<SecretDraftsRepository>();   // same rationale as SecretHistoryRepository above

        services.AddSingleton<IdleTimeoutService>();

        services.AddSingleton<IAppNotificationService, AppNotificationService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IFontResourceService, FontResourceService>();
        services.AddSingleton<IFilePickerService, WinUIFilePicker>();
        services.AddSingleton<IUserConsentVerifierAdapter, WinRTUserConsentVerifierAdapter>();
        services.AddSingleton<IDialogService, WinUIDialogService>();
        services.AddSingleton<ILockService, LockService>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IDispatcherService, WinUiDispatcherService>();
        services.AddSingleton<IWindowCaptureProtectionService, Win32WindowCaptureProtectionService>();
        services.AddSingleton<IAutoTypeService, Win32AutoTypeService>();
        services.AddSingleton<IPasswordEvaluationService, PasswordEvaluationService>();
        services.AddSingleton<AuditLogRepository>();
        services.AddSingleton<IAuditLogService, AuditLogService>();
        services.AddSingleton<ShadowFileService>();

        // ── App-wide security context (Rev7: a generation-tracking Singleton) ──────────
        services.AddSingleton<ISessionGenerationGuard, SessionGenerationGuard>();

        // ── Session-scope common services (Rev7) ──────────────────────────────────
        services.AddScoped<SessionLockGuard>();
        services.AddScoped<SessionTaskRegistry>();

        services.AddScoped<DashboardViewModel>();
        services.AddScoped<SecretsViewModel>();
        services.AddSingleton<AppSettingsViewModel>();
        services.AddScoped<VaultOperationsViewModel>();
        services.AddScoped<GalleryViewModel>();
        // ViewerViewModel is intentionally not registered: ViewerWindow creates it with
        // ActivatorUtilities.CreateInstance(ShellScopeServices) so the container never tracks (and retains)
        // the disposable instance, while its scoped dependencies still come from the session scope.
        services.AddScoped<ProfileViewModel>();     // changed from AddTransient to AddScoped (guarantees ZeroMemory for its 12 PII fields)
        services.AddSingleton<AboutViewModel>();
        services.AddScoped<TimeMachineViewModel>();  // changed from AddTransient to AddScoped (ZeroMemory via IDisposable)
        services.AddScoped<AuditLogViewModel>();

        services.AddScoped<DashboardPage>();
        services.AddTransient<SecretsPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<GalleryPage>();
        services.AddTransient<ProfilePage>();
        services.AddTransient<AboutPage>();
        services.AddTransient<TimeMachinePage>();
        services.AddTransient<AuditLogPage>();

        services.AddTransient<ViewerWindow>();
        services.AddTransient<ShellWindow>();
    }
}
