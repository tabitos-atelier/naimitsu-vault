// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Common;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.ViewModels;

public partial class AuditLogViewModel : ObservableObject, IDisposable
{
    private readonly IAuditLogService _auditLog;
    private readonly AppSession       _session;
    private readonly ILogger<AuditLogViewModel> _logger;

    private IReadOnlyList<AuditLogDisplayItem> _allLogs = [];

    // The currently running (or most recently started) load. A new LoadAsync replaces it and cancels the old one.
    private CancellationTokenSource? _loadCts;

    public AuditLogViewModel(IAuditLogService auditLog, AppSession session, ILogger<AuditLogViewModel> logger)
    {
        _logger = logger;
        _auditLog = auditLog;
        _session  = session;

        // Keeps the list live while this page is open - e.g. an export or setting change made from
        // another window (Viewer, a dialog) appears at the top without needing to revisit the page.
        // While another page is in front, only the NeedsReload flag is recorded: a load decrypts up to
        // AppConstants.AuditLogRetentionDays of rows, and this message fires on every secret view or
        // clipboard copy, so querying while hidden would be a wasted burst of I/O.
        WeakReferenceMessenger.Default.Register<AuditLogWrittenMessage>(this, (_, _) =>
        {
            NeedsReload = true;
            if (!IsActive) return;
            _ = LoadAsync();
        });
    }

    // When the AddScoped Scope is Disposed on lock -> immediately release all string references
    // held by FilteredLogs. Strings are immutable and cannot be zero-cleared, but aligning the
    // timing they become GC-eligible with the SecureCharBuffer family minimizes the residual
    // window (same rationale as DashboardViewModel.Dispose / GalleryViewModel.Dispose).
    public void Dispose()
    {
        WeakReferenceMessenger.Default.Unregister<AuditLogWrittenMessage>(this);
        var cts = Interlocked.Exchange(ref _loadCts, null);
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }
        FilteredLogs.Clear();
    }

    // IsActive = true: the page is in front and processes messages immediately.
    // IsActive = false: another page is shown. DB queries are forbidden; record the change via NeedsReload.
    public bool IsActive { get; private set; }
    public void Resume() => IsActive = true;
    public void Pause()  => IsActive = false;

    // True when the list is stale (or was never loaded) and must be reloaded the next time the page is shown.
    // Starts true so the first Page.Loaded loads; LoadAsync clears it, a failed load sets it again.
    public bool NeedsReload { get; set; } = true;

    [ObservableProperty] public partial bool   IsLoading            { get; set; }
    [ObservableProperty] public partial string SearchText           { get; set; } = string.Empty;
    [ObservableProperty] public partial int    SelectedLevelIndex   { get; set; }
    [ObservableProperty] public partial int    SelectedCodeIndex    { get; set; }

    // Collection displayed after filtering (ListView binds to this)
    public ObservableCollection<AuditLogDisplayItem> FilteredLogs { get; } = [];

    public bool HasLogs => FilteredLogs.Count > 0;

    // True when any of the three filters (search text, severity, code group) narrows the list.
    public bool HasActiveFilters =>
        SelectedLevelIndex > 0 || SelectedCodeIndex > 0 || !string.IsNullOrWhiteSpace(SearchText);

    // Empty-state placeholders. "No activity" also covers the case where filters are active but there is
    // no audit log at all (saying "no match" there would be misleading); "no match" is only for filters
    // that hid an existing, non-empty log.
    public bool ShowNoActivityPlaceholder => !HasLogs && (!HasActiveFilters || _allLogs.Count == 0);
    public bool ShowNoMatchPlaceholder    => !HasLogs && HasActiveFilters && _allLogs.Count > 0;

    partial void OnSearchTextChanged(string value)         => ApplyFilter();
    partial void OnSelectedLevelIndexChanged(int value)    => ApplyFilter();
    partial void OnSelectedCodeIndexChanged(int value)     => ApplyFilter();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        // Latest request wins: cancel whatever is still running and take its place. (An "already loading,
        // so skip" guard would drop a notification that arrived after the running query started, leaving
        // the list without the newest entry.)
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var previous = Interlocked.Exchange(ref _loadCts, cts);
        if (previous != null)
        {
            previous.Cancel();
            previous.Dispose();
        }
        var token = cts.Token;

        NeedsReload = false;
        IsLoading = true;
        try
        {
            var since  = DateTimeOffset.UtcNow.AddDays(-AppConstants.AuditLogRetentionDays);
            var dek    = _session.GetKey();
            var logs   = await _auditLog.GetAllAfterAsync(since, dek, token);
            // A newer LoadAsync replaced this one while it was awaiting: discard the stale result even if
            // the service did not honor the token.
            token.ThrowIfCancellationRequested();
            _allLogs   = logs;
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // AuditLogPage_Loaded is async void, so an unhandled exception would crash the process.
            // Absorb all exceptions here, including CryptographicException from a lock event, and just log.
            // Re-arm NeedsReload so the next display retries instead of showing a stale list.
            NeedsReload = true;
            _logger.LogWarning("[AuditLogViewModel] LoadAsync failed. [{ExType}]", ex.GetType().Name);
        }
        finally
        {
            // Only the latest load clears the spinner; a superseded one must not hide a newer load's progress.
            if (ReferenceEquals(Volatile.Read(ref _loadCts), cts))
                IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<AuditLogDisplayItem> filtered = _allLogs;

        // Severity filter (index 0 = all, 1 = normal(0), 2 = warning(1), 3 = critical(2))
        if (SelectedLevelIndex > 0)
        {
            int targetLevel = SelectedLevelIndex - 1;
            filtered = filtered.Where(x => x.EventLevel == targetLevel);
        }

        // Code group filter (index 0 = all, 1-7 = each group)
        if (SelectedCodeIndex > 0)
            filtered = filtered.Where(x => MatchesCodeGroup(x.EventCode, SelectedCodeIndex));

        // Text search (partial match against Summary)
        var kw = SearchText?.Trim();
        if (!string.IsNullOrEmpty(kw))
            filtered = filtered.Where(x =>
                x.Summary.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                x.EventCodeDisplay.Contains(kw, StringComparison.OrdinalIgnoreCase));

        FilteredLogs.Clear();
        foreach (var item in filtered)
            FilteredLogs.Add(item);

        OnPropertyChanged(nameof(HasLogs));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(ShowNoActivityPlaceholder));
        OnPropertyChanged(nameof(ShowNoMatchPlaceholder));
    }

    // 1=view(0x1000), 2=configuration/settings change(0x3000), 3=authentication(0x4000), 4=save(0x5000),
    // 5=delete/purge(0x6000), 6=critical operation(0x7000), 7=system(0xF000)
    private static bool MatchesCodeGroup(AuditEventCode code, int index)
    {
        int cat = (int)code & 0xF000;
        return index switch
        {
            1 => cat == 0x1000,
            2 => cat == 0x3000,
            3 => cat == 0x4000,
            4 => cat == 0x5000,
            5 => cat == 0x6000,
            6 => cat == 0x7000,
            7 => cat == 0xF000,
            _ => true,
        };
    }
}
