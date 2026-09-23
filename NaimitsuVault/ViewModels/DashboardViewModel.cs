// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Common;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.ViewModels;

public partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IPasswordEvaluationService  _evaluator;
    private readonly SecretRepository            _secrets;
    private readonly StoredFileRepository        _files;
    private readonly ProfileService              _profileService;
    private readonly GalleryViewModel            _galleryVm;
    private readonly SecretsViewModel            _secretsVm;
    private readonly ProfileViewModel            _profileVm;
    private readonly AppSession                  _session;
    private readonly IAuditLogService            _auditLog;
    private readonly IVaultConnectionProvider    _connectionProvider;
    private readonly ICryptoService              _crypto;
    private readonly IWindowService              _windowService;
    private readonly ILogger<DashboardViewModel> _logger;

    // Flag indicating the initial automatic scan after app launch has completed
    private bool _hasRunInitialScan;

    public DashboardViewModel(
        IPasswordEvaluationService evaluator,
        SecretRepository secrets,
        StoredFileRepository files,
        ProfileService profileService,
        GalleryViewModel galleryVm,
        SecretsViewModel secretsVm,
        ProfileViewModel profileVm,
        AppSession session,
        IAuditLogService auditLog,
        IVaultConnectionProvider connectionProvider,
        ICryptoService crypto,
        IWindowService windowService,
        ILogger<DashboardViewModel> logger)
    {
        _evaluator          = evaluator;
        _secrets            = secrets;
        _files              = files;
        _profileService     = profileService;
        _galleryVm          = galleryVm;
        _secretsVm          = secretsVm;
        _profileVm          = profileVm;
        _session            = session;
        _auditLog           = auditLog;
        _connectionProvider = connectionProvider;
        _crypto             = crypto;
        _windowService      = windowService;
        _logger             = logger;

        // Keeps "recent activity" live while Dashboard is the active page, without waiting for a
        // revisit - e.g. an export or setting change from another page appears here immediately.
        // While another page is in front the message is ignored: this message fires on every secret view
        // or clipboard copy, so querying while hidden is wasted I/O. No NeedsReload flag is needed
        // because DashboardPage.Loaded runs LoadBasicStatsAsync (which reloads the audit list) on every display.
        WeakReferenceMessenger.Default.Register<AuditLogWrittenMessage>(this, (_, _) =>
        {
            if (!IsActive) return;
            _ = LoadRecentAuditLogsAsync();
        });
    }

    // IsActive = true: the page is in front and processes messages immediately.
    // IsActive = false: another page is shown. DB queries are forbidden.
    public bool IsActive { get; private set; }
    public void Resume() => IsActive = true;
    public void Pause()  => IsActive = false;

    // When the AddScoped Scope is Disposed on lock → immediately release all string references
    // held by display collections. Strings are immutable and cannot be zero-cleared, but aligning
    // the timing they become GC-eligible with the SecureCharBuffer family minimizes the residual window.
    public void Dispose()
    {
        WeakReferenceMessenger.Default.Unregister<AuditLogWrittenMessage>(this);
        RecentAuditLogs.Clear();
        ExpiryAlertRows.Clear();
        SecurityScanRows.Clear();
    }

    // ── Basic status ─────────────────────────────────────────────
    [ObservableProperty] public partial int    TotalSecrets           { get; set; }
    [ObservableProperty] public partial int    TotalFiles             { get; set; }
    [ObservableProperty] public partial string DbSizeText             { get; set; } = string.Empty;
    [ObservableProperty] public partial int    SecretExpiryAlertCount { get; set; }

    // ── Expiry alerts (unified keyboard-navigable list) ──────────────────
    // Grouped by nav-menu order (Secrets -> Gallery -> Profile); within each group, expired items
    // come before expiring-soon items, matching the Security scan section's error-before-warning order.
    [ObservableProperty] public partial ObservableCollection<ExpiryAlertItem> ExpiryAlertRows { get; set; } = [];

    public bool HasAnyExpiryLine => ExpiryAlertRows.Count > 0;

    partial void OnExpiryAlertRowsChanged(ObservableCollection<ExpiryAlertItem> value)
        => OnPropertyChanged(nameof(HasAnyExpiryLine));

    [RelayCommand]
    private void NavigateToExpiryItem(ExpiryAlertItem? item)
    {
        if (item == null) return;
        if (item.FileId.HasValue) { _windowService.OpenViewer(item.FileId.Value); return; }
        WeakReferenceMessenger.Default.Send(new NavigateToTagMessage(item.Tag, item.SecretId));
    }

    [RelayCommand]
    private void NavigateToSecurityRow(SecurityScanRowItem? item)
    {
        if (item == null) return;
        WeakReferenceMessenger.Default.Send(new NavigateToTagMessage("secrets", item.JumpSecretId));
    }

    // ── Security activity (Section 4) ───────────────────
    [ObservableProperty] public partial ObservableCollection<AuditLogDisplayItem> RecentAuditLogs { get; set; } = [];
    [ObservableProperty] public partial bool IsAuditLoading { get; set; }

    public bool HasRecentAuditLogs => RecentAuditLogs.Count > 0;

    partial void OnRecentAuditLogsChanged(ObservableCollection<AuditLogDisplayItem> value)
    {
        OnPropertyChanged(nameof(HasRecentAuditLogs));
        NavigateToAuditLogCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToAuditLog))]
    private void NavigateToAuditLog()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToTagMessage("auditlog"));
    }

    // Nothing to jump to while the audit log is empty (e.g. a fresh vault) - disables the
    // dashboard's jump-link the same way ShellWindow disables the AuditLog nav item itself.
    private bool CanNavigateToAuditLog() => !_session.IsReadOnlyRestricted && HasRecentAuditLogs;

    // ── Security diagnostics ───────────────────────────────────────────
    [ObservableProperty] public partial bool IsScanRunning     { get; set; }
    [ObservableProperty] public partial bool HasScanResult     { get; set; }
    [ObservableProperty] public partial int  WeakCount         { get; set; }
    [ObservableProperty] public partial int  ReusedGroupCount  { get; set; }

    // Weak and reused items merged into one keyboard-navigable list; a reused-password group renders
    // as a single row (member titles joined) rather than one row per member. See SecurityScanRowItem.
    [ObservableProperty] public partial ObservableCollection<SecurityScanRowItem> SecurityScanRows { get; set; } = [];

    public bool   HasWeakItems        => WeakCount > 0;
    public bool   HasReusedItems      => ReusedGroupCount > 0;
    public bool   HasSecurityScanRows => SecurityScanRows.Count > 0;
    public bool   HasNoIssues         => HasScanResult && !HasWeakItems && !HasReusedItems;
    public bool UnifiedDbIntegrityWarning => _session.UnifiedDbIntegrityWarning;
    public bool VaultDbIntegrityWarning   => _session.VaultDbIntegrityWarning;
    public bool HasAutoRecoveryWarning    => _session.UnifiedDbAutoRecovered || _session.VaultDbAutoRecovered;
    public string AutoRecoveryMessage     => _session.UnifiedDbAutoRecovered
        ? LocalizationManager.Get("Common.InfoUnifiedDbAutoRecovered")
        : string.Format(LocalizationManager.Get("Common.InfoVaultDbAutoRecovered"), _session.VaultDbAutoRecoveredNumber);
    public bool   HasQuarantineWarning => _galleryVm.QuarantinedCount > 0;
    public string QuarantineMessage    => string.Format(LocalizationManager.Get("Common.InfoFilesQuarantined"), _galleryVm.QuarantinedCount);
    public int?   VaultNumber    => _session.DisplayedVaultNumber;

    partial void OnWeakCountChanged(int value)        { OnPropertyChanged(nameof(HasWeakItems));   OnPropertyChanged(nameof(HasNoIssues)); }
    partial void OnReusedGroupCountChanged(int value) { OnPropertyChanged(nameof(HasReusedItems)); OnPropertyChanged(nameof(HasNoIssues)); }
    partial void OnHasScanResultChanged(bool value)   => OnPropertyChanged(nameof(HasNoIssues));
    partial void OnSecurityScanRowsChanged(ObservableCollection<SecurityScanRowItem> value) => OnPropertyChanged(nameof(HasSecurityScanRows));

    // ─────────────────────────────────────────────────────────────

    /// <summary>Updates only basic status and expiry alerts. Runs automatically when the page is displayed.</summary>
    public async Task LoadBasicStatsAsync()
    {
        TotalSecrets           = await _secrets.CountActiveAsync();
        TotalFiles             = await _files.CountAllAsync();
        DbSizeText             = FormatDbSize();
        SecretExpiryAlertCount = await _secrets.CountExpiryAlertsAsync(AppConstants.SecretPasswordWarnDays);
        OnPropertyChanged(nameof(VaultNumber));
        OnPropertyChanged(nameof(HasAutoRecoveryWarning));
        OnPropertyChanged(nameof(AutoRecoveryMessage));

        var (secretExp, secretSoon)   = await LoadSecretExpiryItemsAsync();
        var (galleryExp, gallerySoon) = await LoadCertExpiryLinesAsync();
        OnPropertyChanged(nameof(HasQuarantineWarning));
        OnPropertyChanged(nameof(QuarantineMessage));
        var (profileExp, profileSoon) = await LoadExpiryAlertsAsync();
        var rows = new List<ExpiryAlertItem>(secretExp.Count + secretSoon.Count + galleryExp.Count + gallerySoon.Count + profileExp.Count + profileSoon.Count);
        rows.AddRange(secretExp);  rows.AddRange(secretSoon);
        rows.AddRange(galleryExp); rows.AddRange(gallerySoon);
        rows.AddRange(profileExp); rows.AddRange(profileSoon);
        ExpiryAlertRows = new ObservableCollection<ExpiryAlertItem>(rows);

        await LoadRecentAuditLogsAsync();
    }

    private async Task<(List<ExpiryAlertItem> Expired, List<ExpiryAlertItem> Soon)> LoadSecretExpiryItemsAsync()
    {
        var expLines  = new List<ExpiryAlertItem>();
        var soonLines = new List<ExpiryAlertItem>();
        try
        {
            var dek       = _session.GetKey();
            // Based on the local calendar date (consistent with other expiry checks). Passed to the repository as a UTC instant.
            var limitUtc  = DateTime.Today.AddDays(AppConstants.SecretPasswordWarnDays).ToUniversalTime();
            var secrets   = await _secrets.GetExpiringAsync(limitUtc);
            var today     = DateTime.Today;

            foreach (var s in secrets)
            {
                string title;
                using (var buf = FieldCrypto.Open(s.Title, _crypto, dek))
                    title = buf != null ? Encoding.UTF8.GetString(buf.Utf8) : "?";

                int days = (s.ExpiresAt!.Value.ToLocalTime().Date - today).Days;
                var text = days < 0
                    ? $"{title}: {LocalizationManager.Get("Common.Expired")}"
                    : $"{title}: {string.Format(LocalizationManager.Get("Common.ExpiresInDays"), days)}";
                var item = new ExpiryAlertItem(text, "secrets", IsExpired: days < 0, SecretId: s.Id);

                if (days < 0) expLines.Add(item);
                else          soonLines.Add(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Dashboard] LoadSecretExpiryItemsAsync failed. [{ExType}]", ex.GetType().Name);
        }
        return (expLines, soonLines);
    }

    private async Task LoadRecentAuditLogsAsync()
    {
        IsAuditLoading = true;
        try
        {
            var dek   = _session.GetKey();
            var items = await _auditLog.GetRecentAsync(15, dek);
            RecentAuditLogs = new ObservableCollection<AuditLogDisplayItem>(items);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Dashboard] GetRecentAsync failed. [{ExType}]", ex.GetType().Name);
        }
        finally
        {
            IsAuditLoading = false;
        }
    }

    /// <summary>Runs the diagnostic scan automatically only on the first display. Subsequent displays show cached results immediately.</summary>
    public async Task TryRunInitialScanAsync()
    {
        if (_hasRunInitialScan) return;
        _hasRunInitialScan = true;
        // Vault-wide self-heal for stray NoOp drafts (see SecretsViewModel.CleanUpNoOpDraftsAsync and
        // ProfileViewModel.CleanUpNoOpDraftAsync), piggybacked on the same "once per session, right
        // after unlock" timing as the security scan below - both already pay the cost of iterating
        // every secret, so this adds no new full-vault pass.
        try { await _secretsVm.CleanUpNoOpDraftsAsync(); }
        catch (Exception ex) { _logger.LogWarning("[Dashboard] CleanUpNoOpDraftsAsync failed. [{ExType}]", ex.GetType().Name); }
        try { await _profileVm.CleanUpNoOpDraftAsync(); }
        catch (Exception ex) { _logger.LogWarning("[Dashboard] CleanUpNoOpDraftAsync failed. [{ExType}]", ex.GetType().Name); }
        if (CanRunScan())
            await RunScanAsync();
    }

    private async Task<(List<ExpiryAlertItem> Expired, List<ExpiryAlertItem> Soon)> LoadExpiryAlertsAsync()
    {
        var expLines  = new List<ExpiryAlertItem>();
        var soonLines = new List<ExpiryAlertItem>();
        try
        {
            var dek    = _session.GetKey();
            var expiry = await _profileService.GetExpiryInfoAsync(dek);
            var today  = DateTime.Today;

            AppendExpiryLine(expLines, soonLines, expiry.Id1Expiry, AppConstants.ProfileIdLicenseWarnDays, expiry.IdentityItem1Label, today);
            AppendExpiryLine(expLines, soonLines, expiry.Id2Expiry, AppConstants.ProfileIdLicenseWarnDays, expiry.IdentityItem2Label, today);
            AppendExpiryLine(expLines, soonLines, expiry.Id3Expiry, AppConstants.ProfilePassportWarnDays,  expiry.IdentityItem3Label, today);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Dashboard] LoadExpiryAlertsAsync failed. [{ExType}]", ex.GetType().Name);
        }
        return (expLines, soonLines);
    }

    private static void AppendExpiryLine(List<ExpiryAlertItem> expLines, List<ExpiryAlertItem> soonLines, DateTime? expiry, int warnDays, string label, DateTime today)
    {
        if (!expiry.HasValue) return;
        int days = (int)(expiry.Value.Date - today).TotalDays;
        if (days < 0)
            expLines.Add(new ExpiryAlertItem($"{label}: {LocalizationManager.Get("Common.Expired")}", "profile", IsExpired: true));
        else if (days <= warnDays)
            soonLines.Add(new ExpiryAlertItem($"{label}: {string.Format(LocalizationManager.Get("Common.ExpiresInDays"), days)}", "profile", IsExpired: false));
    }

    private async Task<(List<ExpiryAlertItem> Expired, List<ExpiryAlertItem> Soon)> LoadCertExpiryLinesAsync()
    {
        await _galleryVm.EnsureLoadedAsync();
        var expLines  = new List<ExpiryAlertItem>();
        var soonLines = new List<ExpiryAlertItem>();
        foreach (var item in _galleryVm.GetCertAlertItems())
        {
            int days = item.DaysUntilExpiry!.Value;
            var text = days < 0
                ? $"{item.FileName}: {LocalizationManager.Get("Common.Expired")}"
                : $"{item.FileName}: {string.Format(LocalizationManager.Get("Common.ExpiresInDays"), days)}";
            var line = new ExpiryAlertItem(text, "gallery", IsExpired: days < 0, FileId: item.Id);
            if (days < 0) expLines.Add(line);
            else          soonLines.Add(line);
        }
        return (expLines, soonLines);
    }

    [RelayCommand(CanExecute = nameof(CanRunScan))]
    private async Task RunScanAsync(CancellationToken ct = default)
    {
        IsScanRunning = true;
        HasScanResult = false;
        RunScanCommand.NotifyCanExecuteChanged();
        try
        {
            var dek    = _session.GetKey();
            var result = await _evaluator.ScanAllSecretsAsync(dek, ct);
            // DekScope is a reference wrapper around AppSession._pinnedKey (not a copy).
            // ZeroMemory is AppSession.Lock()'s responsibility; not needed here.
            ApplyResult(result);
            HasScanResult = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Dashboard] Scan cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError("[Dashboard] ScanAllSecretsAsync failed. [{ExType}]", ex.GetType().Name);
        }
        finally
        {
            IsScanRunning = false;
            RunScanCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRunScan() => !IsScanRunning && TotalSecrets > 0 && !_session.IsReadOnlyRestricted;

    partial void OnTotalSecretsChanged(int value) => RunScanCommand.NotifyCanExecuteChanged();

    private void ApplyResult(DashboardScanResult r)
    {
        WeakCount        = r.WeakPasswordCount;
        ReusedGroupCount = r.ReusedPasswordCount;

        var rows = new List<SecurityScanRowItem>(r.WeakPasswordCount + r.ReusedPasswordCount);
        foreach (var w in r.WeakItems)
            rows.Add(new SecurityScanRowItem(w.Title, w.SecretId, IsReused: false));

        // Each DuplicateGroupId becomes exactly one row, its title joining every member of that
        // group - this is what keeps "a, b" and "c, d" as two separate rows instead of one blob.
        foreach (var group in r.ReusedItems.GroupBy(i => i.DuplicateGroupId).OrderBy(g => g.Key))
        {
            var members = group.ToList();
            var text    = string.Join(", ", members.Select(m => m.Title));
            rows.Add(new SecurityScanRowItem(text, members[0].SecretId, IsReused: true));
        }

        SecurityScanRows = new ObservableCollection<SecurityScanRowItem>(rows);
    }

    private string FormatDbSize()
    {
        var path = _connectionProvider.ActiveVaultDbPath;
        if (path == null || !File.Exists(path)) return string.Empty;
        var bytes = new FileInfo(path).Length;
        var walPath = path + "-wal";
        if (File.Exists(walPath)) bytes += new FileInfo(walPath).Length;
        var mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024.0 ? $"{mb / 1024.0:F2} GB" : $"{mb:F1} MB";
    }
}
