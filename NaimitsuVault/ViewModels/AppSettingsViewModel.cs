// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using WinRT.Interop;
using Microsoft.Extensions.Logging;

namespace NaimitsuVault.ViewModels;

public partial class AppSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IDbContextFactory<UnifiedDbContext> _factory;
    private readonly IAuthService _auth;
    private readonly AppSession _session;
    private readonly IThemeService _themeService;
    private readonly IFontResourceService _fontResource;
    private readonly ILocalizationService _localization;
    private readonly IdleTimeoutService _autoLock;
    private readonly FaviconService _favicon;
    private readonly IWindowCaptureProtectionService _captureProtection;
    private readonly AutoBackupService _autoBackup;
    private readonly IAppNotificationService _notification;
    private readonly IDialogService _dialog;
    private readonly IFilePickerService _filePicker;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<AppSettingsViewModel> _logger;

    private bool _isLoading;
    private CancellationTokenSource? _persistCts;
    private string _loadedLocale = "ja";

    [ObservableProperty] public partial string ThemeMode { get; set; } = "System";
    [ObservableProperty] public partial bool IsWindowsHelloEnabled { get; set; }
    [ObservableProperty] public partial bool IsWindowsHelloSupported { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsAutoBackupEnabled { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoBackupFolder))]
    public partial string AutoBackupFolder { get; set; } = string.Empty;
    public bool HasAutoBackupFolder => !string.IsNullOrEmpty(AutoBackupFolder);
    [ObservableProperty] public partial string LocaleMode { get; set; } = "ja";
    [ObservableProperty] public partial bool HasCustomLocale { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomLocaleLabel))]
    public partial string CustomLocaleDisplayName { get; set; } = string.Empty;
    [ObservableProperty] public partial bool AutoLockEnabled { get; set; } = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoLockMinutesIndex))]
    public partial int AutoLockMinutes { get; set; } = 5;
    [ObservableProperty] public partial bool IsHideFromTaskbarWhenMinimized { get; set; }
    [ObservableProperty] public partial bool IsRestartRequired { get; set; }
    [ObservableProperty] public partial bool IsFaviconAutoFetchEnabled { get; set; } = false;
    [ObservableProperty] public partial bool WindowCaptureProtectionEnabled { get; set; } = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontFamilyDisplayName))]
    public partial string? FontFamily { get; set; }
    public string[] FontFamilyOptions { get; private set; } = Array.Empty<string>();
    public string FontFamilyDisplayName => FontFamily ?? LocalizationManager.Get("AppSettings.FontFamilySystemDefault");

    public AppSettingsViewModel(
        IDbContextFactory<UnifiedDbContext> factory,
        IAuthService auth,
        AppSession session,
        IThemeService themeService,
        IFontResourceService fontResource,
        ILocalizationService localization,
        IdleTimeoutService autoLock,
        FaviconService favicon,
        IWindowCaptureProtectionService captureProtection,
        AutoBackupService autoBackup,
        IAppNotificationService notification,
        IDialogService dialog,
        IFilePickerService filePicker,
        IAuditLogService auditLog,
        ILogger<AppSettingsViewModel> logger)
    {
        _logger = logger;
        _factory = factory;
        _auth = auth;
        _session = session;
        _themeService = themeService;
        _fontResource = fontResource;
        _localization = localization;
        _autoLock = autoLock;
        _favicon = favicon;
        _captureProtection = captureProtection;
        _autoBackup = autoBackup;
        _notification = notification;
        _dialog = dialog;
        _filePicker = filePicker;
        _auditLog = auditLog;
    }

    partial void OnThemeModeChanged(string value)
    {
        if (_isLoading) return;
        SchedulePersist();
        _themeService.Apply(value);
        OnPropertyChanged(nameof(ThemeModeIndex));
    }

    partial void OnIsAutoBackupEnabledChanged(bool value)
    {
        if (_isLoading) return;
        SchedulePersist();
        LogSettingChanged(AuditEventCode.AutoBackupSettingChanged,
            new AutoBackupSettingChangedPayload(value, AutoBackupFolder.Length > 0 ? AutoBackupFolder : null));
    }

    partial void OnAutoBackupFolderChanged(string value)
    {
        if (_isLoading) return;
        SchedulePersist();
        // Mirrors OnIsAutoBackupEnabledChanged: while auto backup is already enabled, changing or
        // clearing the destination folder is itself an audit-worthy setting change (previously only
        // the enabled/disabled toggle was logged, leaving a mid-session folder swap unrecorded).
        if (IsAutoBackupEnabled)
            LogSettingChanged(AuditEventCode.AutoBackupSettingChanged,
                new AutoBackupSettingChangedPayload(true, value.Length > 0 ? value : null));
    }

    partial void OnAutoLockEnabledChanged(bool value)
    {
        if (_isLoading) return;
        SchedulePersist();
        _autoLock.IsEnabled = value;
        if (value) _autoLock.ResetTimer();
        else _autoLock.Stop();
        LogSettingChanged(AuditEventCode.AutoLockSettingChanged, new AutoLockSettingChangedPayload(value, AutoLockMinutes));
    }

    partial void OnAutoLockMinutesChanged(int value)
    {
        if (_isLoading) return;
        SchedulePersist();
        _autoLock.TimeoutMinutes = value;
        if (_autoLock.IsEnabled) _autoLock.ResetTimer();
    }

    partial void OnIsHideFromTaskbarWhenMinimizedChanged(bool value)
    {
        if (_isLoading) return;
        SchedulePersist();
    }

    partial void OnIsFaviconAutoFetchEnabledChanged(bool value)
    {
        _favicon.IsEnabled = value;
        if (_isLoading) return;
        SchedulePersist();
        // Without this, an already-loaded SecretsViewModel never learns the toggle changed and won't
        // fetch until its next LoadAsync (app restart) - the setting takes effect for the service, but
        // looks like it does nothing to the user until then. Gated on _isLoading like SchedulePersist
        // above: during startup restore, SecretsViewModel's own LoadAsync already checks
        // FaviconService.IsEnabled, so firing here too could race a not-yet-populated _searchIndex.
        WeakReferenceMessenger.Default.Send(new FaviconAutoFetchToggledMessage(value));
        LogSettingChanged(AuditEventCode.FaviconAutoFetchChanged, new FaviconAutoFetchChangedPayload(value));
    }

    partial void OnWindowCaptureProtectionEnabledChanged(bool value)
    {
        if (_isLoading) return;
        SchedulePersist();
        _captureProtection.Apply(GetShellWindowHwnd(), value);
        WeakReferenceMessenger.Default.Send(new CaptureProtectionChangedMessage(value));
        LogSettingChanged(AuditEventCode.ScreenCaptureProtectionChanged, new ScreenCaptureProtectionChangedPayload(value));
    }

    partial void OnFontFamilyChanged(string? value)
    {
        _fontResource.Apply(value);
        if (_isLoading) return;
        SchedulePersist();
        WeakReferenceMessenger.Default.Send(new FontFamilyChangedMessage(value));
    }

    partial void OnLocaleModeChanged(string value)
    {
        if (_isLoading) return;
        _ = SaveLocaleAsync(value);
        IsRestartRequired = value != _loadedLocale;
        OnPropertyChanged(nameof(LocaleModeIndex));
    }

    public void Dispose()
    {
        _persistCts?.Cancel();
        _persistCts?.Dispose();
        _persistCts = null;
    }

    private void SchedulePersist()
    {
        // Swap in the new CTS before tearing down the old one, so _persistCts never briefly
        // points to an already-disposed instance if this were ever re-entered.
        var oldCts = _persistCts;
        _persistCts = new CancellationTokenSource();
        oldCts?.Cancel();
        oldCts?.Dispose();
        _ = PersistGeneralSettingsAsync(_persistCts.Token);
    }

    private async Task PersistGeneralSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(300, ct);
            var folder = AutoBackupFolder.Length > 0 ? AutoBackupFolder : null;
            var json = JsonSerializer.Serialize(
                new GeneralSettings(ThemeMode, IsAutoBackupEnabled, folder, AutoLockEnabled, AutoLockMinutes,
                    IsHideFromTaskbarWhenMinimized, IsFaviconAutoFetchEnabled, WindowCaptureProtectionEnabled, FontFamily),
                AppSettingsJsonContext.Default.GeneralSettings);
            await using var db = await _factory.CreateDbContextAsync();
            var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == UnifiedMetadataKey.General);
            if (existing == null)
                db.Metadata.Add(new UnifiedMetadata { ConfigKey = UnifiedMetadataKey.General, ConfigValue = Encoding.UTF8.GetBytes(json) });
            else
                existing.ConfigValue = Encoding.UTF8.GetBytes(json);
            await db.SaveChangesAsync();
            _autoBackup.IsEnabled = IsAutoBackupEnabled;
            _autoBackup.Folder = IsAutoBackupEnabled ? folder : null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError("PersistGeneralSettingsAsync failed. [{ExType}]", ex.GetType().Name);
        }
    }

    // Fire-and-forget wrapper for use from the synchronous OnXxxChanged partial methods generated
    // by [ObservableProperty] (which can't be made async). Swallows and logs any failure so a
    // transient audit-write error never surfaces to the UI over a settings toggle.
    private void LogSettingChanged(AuditEventCode code, AuditPayload payload)
        => _ = LogSettingChangedAsync(code, payload);

    private async Task LogSettingChangedAsync(AuditEventCode code, AuditPayload payload)
    {
        try { await _auditLog.LogAsync(code, payload, _session.GetKey()); }
        catch (Exception ex) { _logger.LogWarning("[AppSettings] Failed to log {Code}. [{ExType}]", code, ex.GetType().Name); }
    }

    private async Task SaveLocaleAsync(string locale)
    {
        try
        {
            await _localization.SetLocaleAsync(locale);

            // The new language only takes effect after a restart, so a "saved" (Success) toast would
            // overstate it. Warning tells the user something is still pending. Switching back to the
            // language already running needs no restart, so nothing is shown (the radio button itself
            // reflects the selection).
            if (locale != _loadedLocale)
                _notification.Show(LK.AppSettings_Language, LK.AppSettings_Dialog_RestartRequiredAlert, NotificationSeverity.Warning, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            _logger.LogError("SaveLocaleAsync failed. [{ExType}]", ex.GetType().Name);
        }
    }

    public async Task LoadAsync()
    {
        _isLoading = true;
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            var setting = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == UnifiedMetadataKey.General);

            IsWindowsHelloSupported = await _auth.IsWindowsHelloSupportedAsync();
            IsWindowsHelloEnabled = _session.IsUnlocked
                ? await _auth.IsWindowsHelloEnabledAsync()
                : false;

            HasCustomLocale = _localization.HasCustomLocale;
            CustomLocaleDisplayName = _localization.CustomLocaleDisplayName ?? string.Empty;

            FontFamilyOptions = InstalledFontEnumerator.GetInstalledFontFamilies();

            // Must run even when General settings have never been saved (true first launch),
            // otherwise LocaleMode keeps its "ja" declaration default while the UI text itself
            // (loaded independently at startup via LocalizationService) already reflects the
            // correctly-detected OS locale, so the settings radio button shows the wrong selection.
            LocaleMode = _localization.CurrentLocale;
            _loadedLocale = LocaleMode;
            IsRestartRequired = false;

            if (setting == null) return;

            var data = JsonSerializer.Deserialize(setting.ConfigValue.AsSpan(), AppSettingsJsonContext.Default.GeneralSettings);
            if (data == null) return;

            ThemeMode = data.ThemeMode ?? "System";
            IsAutoBackupEnabled = data.AutoBackupEnabled;
            AutoBackupFolder = data.AutoBackupFolder ?? string.Empty;
            AutoLockEnabled = data.AutoLockEnabled;
            AutoLockMinutes = data.AutoLockMinutes > 0 ? data.AutoLockMinutes : 5;
            IsHideFromTaskbarWhenMinimized = data.HideFromTaskbarWhenMinimized;
            IsFaviconAutoFetchEnabled = data.FaviconAutoFetchEnabled;
            _favicon.IsEnabled = data.FaviconAutoFetchEnabled;
            WindowCaptureProtectionEnabled = data.WindowCaptureProtectionEnabled ?? true;

            // Portable-use fallback: a font chosen on another PC may not be installed here
            // (USB stick moved between machines), or its enumerated name may differ by OS display
            // language. Silently fall back to the system default rather than passing an unknown
            // name to WinUI, which would otherwise substitute a fallback font with no explanation.
            FontFamily = ResolveFontFamily(data.FontFamily, FontFamilyOptions);
            if (FontFamily != data.FontFamily) SchedulePersist();

            _autoBackup.IsEnabled = data.AutoBackupEnabled;
            _autoBackup.Folder = data.AutoBackupFolder;
        }
        finally
        {
            _isLoading = false;
        }
        _autoLock.IsEnabled = AutoLockEnabled;
        _autoLock.TimeoutMinutes = AutoLockMinutes;
    }

    public int ThemeModeIndex
    {
        get => ThemeMode switch { "Light" => 1, "Dark" => 2, _ => 0 };
        set => ThemeMode = value switch { 1 => "Light", 2 => "Dark", _ => "System" };
    }

    public int LocaleModeIndex
    {
        get => LocaleMode switch { "ja" => 1, "custom" => 2, _ => 0 };
        set => LocaleMode = value switch { 1 => "ja", 2 => "custom", _ => "en" };
    }

    public string CustomLocaleLabel =>
        HasCustomLocale && !string.IsNullOrEmpty(CustomLocaleDisplayName)
            ? CustomLocaleDisplayName
            : LocalizationManager.Get("AppSettings.LangCustom");

    public string[] AutoLockMinutesLabels =>
        new[] { 5, 10, 15, 30, 60 }
        .Select(m => string.Format(LocalizationManager.Get("VaultSettings.MinutesCountFormat"), m))
        .ToArray();

    public int AutoLockMinutesIndex
    {
        get => AutoLockMinutes switch { 5 => 0, 10 => 1, 15 => 2, 30 => 3, 60 => 4, _ => 1 };
        set => AutoLockMinutes = value switch { 0 => 5, 1 => 10, 2 => 15, 3 => 30, 4 => 60, _ => 10 };
    }

    /// <summary>
    /// When auto backup is on but no folder is configured, shows a dialog and turns it back off.
    /// In contexts where a dialog cannot be shown (auto lock, exit), pass showDialog=false to fix it silently.
    /// </summary>
    public async Task ValidateAutoBackupAsync(bool showDialog = true)
    {
        if (!IsAutoBackupEnabled || !string.IsNullOrEmpty(AutoBackupFolder)) return;
        IsAutoBackupEnabled = false;
        if (showDialog)
            await _dialog.ShowInfoAsync(
                LocalizationManager.Get("VaultSettings.WarningAutoBackupContext"),
                LocalizationManager.Get("VaultSettings.WarningNoFolderAutoBackupDisabled"));
    }

    [RelayCommand]
    private async Task ChangeHelloSettingAsync()
    {
        if (_isLoading) return;

        bool wantEnable = IsWindowsHelloEnabled;
        IsBusy = true;
        try
        {
            // No success toast: the toggle itself shows the new state, and the change is audit-logged.
            // Only failures are surfaced (see catch below).
            if (wantEnable)
                await _auth.EnableWindowsHelloAsync();
            else
                await _auth.DisableWindowsHelloAsync();
            LogSettingChanged(AuditEventCode.WindowsHelloChanged, new WindowsHelloChangedPayload(wantEnable));
        }
        catch (Exception ex)
        {
            IsWindowsHelloEnabled = !wantEnable;
            _logger.LogError("ChangeHelloSettingAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportLocaleAsync(string locale)
    {
        IsBusy = true;
        try
        {
            using var pathBuf = await _filePicker.SaveAsync(
                $"custom-locale-{locale}",
                [(LocalizationManager.Get("AppSettings.JsonFilterName"), ".json")]);
            if (pathBuf == null) return;

            await using var fs = new FileStream(new string(pathBuf.Span), FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await _localization.ExportAsync(locale, fs);
            _notification.Show(LK.Common_SuccessSaveComplete, LK.Common_SuccessSaveComplete, NotificationSeverity.Success, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError("ExportLocaleAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportLocaleAsync()
    {
        using var pathBuf = await _filePicker.OpenAsync([(LocalizationManager.Get("AppSettings.JsonFilterName"), ".json")]);
        if (pathBuf == null) return;
        var bytes = await File.ReadAllBytesAsync(new string(pathBuf.Span));
        await ImportLocaleFromBytesAsync(bytes);
    }

    internal async Task ImportLocaleFromBytesAsync(byte[] bytes)
    {
        IsBusy = true;
        try
        {
            var result = await _localization.ImportCustomAsync(bytes);

            if (!result.Success)
            {
                _notification.Show(LK.Common_Error,
                    string.Format(LocalizationManager.GetById(LK.VaultSettings_ErrorImportExceptionReport), result.ErrorMessage),
                    NotificationSeverity.Error, TimeSpan.FromSeconds(5));
                try { await _auditLog.LogAsync(AuditEventCode.LocaleImportFailed, null, _session.GetKey()); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("[ImportLocale] Failed to log LocaleImportFailed audit entry. [{ExType}]", ex.GetType().Name);
                }
                return;
            }

            if (result.WarningMessage != null)
                _notification.Show(LK.Common_WarningVersionMismatch, result.WarningMessage, NotificationSeverity.Warning, TimeSpan.FromSeconds(4));

            HasCustomLocale = _localization.HasCustomLocale;
            CustomLocaleDisplayName = _localization.CustomLocaleDisplayName ?? string.Empty;

            _isLoading = true;
            LocaleMode = "custom";
            _isLoading = false;
            OnPropertyChanged(nameof(LocaleModeIndex));
            await _localization.SetLocaleAsync("custom");
            IsRestartRequired = true;

            var msg = string.Format(LocalizationManager.Get("AppSettings.Dialog.ImportPatchedCountReport"), result.PatchedCount);
            _notification.Show(LK.AppSettings_Dialog_ImportLanguageSuccess, msg, NotificationSeverity.Success, TimeSpan.FromSeconds(4));

            // One row for the import attempt itself, plus one row per key (expected to be rare) so the
            // exact missing/oversized/broken keys stay investigable.
            try
            {
                var key = _session.GetKey();
                await _auditLog.LogAsync(AuditEventCode.LocaleImportSucceeded, new LocaleImportSucceededPayload(result.PatchedCount, _localization.CustomLocaleDisplayName), key);
                foreach (var k in result.MissingKeys ?? [])
                    await _auditLog.LogAsync(AuditEventCode.LocaleImportKeyMissing, new LocaleImportKeyMissingPayload(k), key);
                foreach (var k in result.TooLongKeys ?? [])
                    await _auditLog.LogAsync(AuditEventCode.LocaleImportValueTooLong, new LocaleImportValueTooLongPayload(k), key);
                foreach (var k in result.PlaceholderBrokenKeys ?? [])
                    await _auditLog.LogAsync(AuditEventCode.LocaleImportPlaceholderBroken, new LocaleImportPlaceholderBrokenPayload(k), key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("[ImportLocale] Failed to log locale import audit entries. [{ExType}]", ex.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("ImportLocaleFromBytesAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RestartApp()
    {
        SqliteConnection.ClearAllPools();
        Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().UnregisterKey();
        Process.Start(Environment.ProcessPath!);
        if (Microsoft.UI.Xaml.Application.Current is App app)
            (app.ActiveShellWindow as Views.ShellWindow)?.SuppressCloseConfirm();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    [RelayCommand]
    private async Task ClearBackupFolderAsync()
    {
        IsBusy = true;
        try
        {
            bool confirmed = await _dialog.ConfirmAsync(
                LocalizationManager.Get("Common.DeleteConfirm"),
                LocalizationManager.Get("VaultSettings.Dialog.ClearBackupPathConfirmText"),
                defaultToCancel: true);
            if (!confirmed) return;
            AutoBackupFolder = string.Empty;
            await ValidateAutoBackupAsync(showDialog: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BrowseBackupFolderAsync()
    {
        using var folderBuf = await _filePicker.OpenFolderAsync();
        if (folderBuf == null) return;
        var folder = new string(folderBuf.Span);

        var testPath = Path.Combine(folder, ".naimitsu_write_test");
        try
        {
            await File.WriteAllTextAsync(testPath, "");
            File.Delete(testPath);
        }
        catch (UnauthorizedAccessException)
        {
            _notification.Show(LK.Common_Error, LK.Common_ErrorWriteAccessDenied, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
            return;
        }

        AutoBackupFolder = folder;
    }

    /// <summary>
    /// Portable-use fallback: a persisted FontFamily that isn't present in the current machine's
    /// installed-font enumeration (moved via USB to another PC, or a name that differs by OS
    /// display language) falls back to null (system default) instead of being passed to WinUI as-is.
    /// </summary>
    internal static string? ResolveFontFamily(string? persisted, IReadOnlyCollection<string> installedOptions)
        => persisted != null && installedOptions.Contains(persisted) ? persisted : null;

    private static nint GetShellWindowHwnd()
    {
        if (Microsoft.UI.Xaml.Application.Current is App app && app.ActiveShellWindow is { } w)
            return WindowNative.GetWindowHandle(w);
        return nint.Zero;
    }

    internal record GeneralSettings(
        string? ThemeMode = "System",
        bool AutoBackupEnabled = false,
        string? AutoBackupFolder = null,
        bool AutoLockEnabled = true,
        int AutoLockMinutes = 5,
        bool HideFromTaskbarWhenMinimized = false,
        bool FaviconAutoFetchEnabled = false,
        bool? WindowCaptureProtectionEnabled = null,
        string? FontFamily = null);

    [JsonSerializable(typeof(GeneralSettings))]
    internal partial class AppSettingsJsonContext : JsonSerializerContext { }
}
