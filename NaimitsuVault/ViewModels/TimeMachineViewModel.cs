// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using NaimitsuVault.Common;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace NaimitsuVault.ViewModels;

public partial class TimeMachineSecretItem : ObservableObject, IDisposable
{
    public int Id { get; init; }
    public bool IsDeleted { get; init; }
    public bool HasDraft { get; init; }
    public int? CategoryNum { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? WebsiteDomain { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string UpdatedAtDisplay => UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd");
    public bool IsExpired      => ExpiresAt.HasValue && ExpiresAt.Value.Date < DateTime.Today;
    public bool IsExpiringSoon => ExpiresAt.HasValue && !IsExpired && ExpiresAt.Value.Date < DateTime.Today.AddDays(AppConstants.SecretPasswordWarnDays);
    [ObservableProperty] public partial bool IsFavorite { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFavicon))]
    public partial Microsoft.UI.Xaml.Media.Imaging.BitmapImage? FaviconSource { get; set; }
    public bool HasFavicon => FaviconSource != null;

    // Holds the title (PII) in a POH-pinned buffer. Provides a temporary string / Span for XAML/search.
    private readonly SecureCharBuffer _titleBuf = new();
    public string DisplayTitle => _titleBuf.ToDisplayString();
    public ReadOnlySpan<char> TitleSpan => _titleBuf.Span;
    internal void SetTitle(ReadOnlySpan<byte> utf8) => FieldCrypto.FillBufferFromUtf8(_titleBuf, utf8);
    public void Dispose() => _titleBuf.Dispose();
}

public partial class TimeMachineRowViewModel : ObservableObject, IDisposable
{
    public string FieldLabel { get; init; } = string.Empty;
    public bool IsSensitive { get; init; }
    public bool Gen1HasValue { get; init; }
    public bool Gen2HasValue { get; init; }

    // Holds every column in a POH-pinned buffer. Clear()/Dispose() runs ZeroMemory on all of them at once.
    private readonly SecureCharBuffer _currentBuf = new();
    private readonly SecureCharBuffer _gen1Buf    = new();
    private readonly SecureCharBuffer _gen2Buf    = new();

    // For clipboard copy: creates a temporary string on each call, never held in a field
    public string? CurrentRaw => _currentBuf.IsEmpty ? null : new string(_currentBuf.Span);
    public string? Gen1Raw    => _gen1Buf.IsEmpty    ? null : new string(_gen1Buf.Span);
    public string? Gen2Raw    => _gen2Buf.IsEmpty    ? null : new string(_gen2Buf.Span);

    // Bundles the field label + which generation, alongside the value, into one CommandParameter -
    // so CopyValueCommand stays a single shared command (no per-slot command split) while still
    // knowing what to write to the audit log without re-deriving it from the raw copied string.
    public TimeMachineCopyParam? CurrentCopyParam => HasCurrentValue ? new(FieldLabel, "Current", CurrentRaw!) : null;
    public TimeMachineCopyParam? Gen1CopyParam    => Gen1CanCopy     ? new(FieldLabel, "Gen1", Gen1Raw!) : null;
    public TimeMachineCopyParam? Gen2CopyParam    => Gen2CanCopy     ? new(FieldLabel, "Gen2", Gen2Raw!) : null;

    // Forced flag for rows (e.g. file rows) whose diff must be determined by a set of IDs
    internal bool ForceGen1Changed { get; set; }
    internal bool ForceGen2Changed { get; set; }

    // Attached-files row: rendered as thumbnails instead of text (see TimeMachinePage.xaml's
    // IsAttachmentRow-toggled visibility). _currentBuf/_gen1Buf/_gen2Buf stay empty for this row -
    // ForceGen1Changed/ForceGen2Changed (set from an ID-set comparison, same as any other file row)
    // are what drive Gen1Differs/Gen2Differs here.
    public bool IsAttachmentRow { get; init; }
    public List<CompareFileThumbnail> CurrentFiles { get; init; } = [];
    public List<CompareFileThumbnail> Gen1Files    { get; init; } = [];
    public List<CompareFileThumbnail> Gen2Files    { get; init; } = [];

    // Allocation-free span comparison (unconditionally differs if the forced flag is set).
    // Must NOT require the gen buffer to be non-empty: an empty-to-set transition (e.g. ExpiresAt
    // was never set in gen1 but is set in the current value) is itself a difference worth flagging.
    public bool Gen1Differs => ForceGen1Changed || (Gen1HasValue &&
        !MemoryExtensions.SequenceEqual(_gen1Buf.Span, _currentBuf.Span));
    public bool Gen2Differs => ForceGen2Changed || (Gen2HasValue &&
        !MemoryExtensions.SequenceEqual(_gen2Buf.Span, _gen1Buf.Span));

    public bool HasCurrentValue => !_currentBuf.IsEmpty;
    public bool HasGen1Value    => !_gen1Buf.IsEmpty;
    public bool HasGen2Value    => !_gen2Buf.IsEmpty;

    // Copy button visibility: only when there's a diff AND that generation actually has a value to copy
    // (a diff can be "value -> empty", which has nothing to put on the clipboard).
    public bool Gen1CanCopy => Gen1Differs && HasGen1Value;
    public bool Gen2CanCopy => Gen2Differs && HasGen2Value;

    [ObservableProperty] public partial bool IsRevealed { get; set; }

    partial void OnIsRevealedChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentDisplay));
        OnPropertyChanged(nameof(Gen1Display));
        OnPropertyChanged(nameof(Gen2Display));
    }

    // For XAML binding: creates a temporary string from the span and passes it. Never held in a field.
    public string CurrentDisplay => MaskSpan(_currentBuf.Span, IsSensitive && !IsRevealed);
    public string Gen1Display    => Gen1HasValue ? MaskSpan(_gen1Buf.Span, IsSensitive && !IsRevealed) : "-";
    public string Gen2Display    => Gen2HasValue ? MaskSpan(_gen2Buf.Span, IsSensitive && !IsRevealed) : "-";

    private static string MaskSpan(ReadOnlySpan<char> span, bool mask) =>
        span.IsEmpty ? "-" : mask ? "••••••••" : new string(span);

    internal void SetCurrent(ReadOnlySpan<char> value) => _currentBuf.SetFromSpan(value);
    internal void SetGen1(ReadOnlySpan<char> value)    => _gen1Buf.SetFromSpan(value);
    internal void SetGen2(ReadOnlySpan<char> value)    => _gen2Buf.SetFromSpan(value);

    // Physically wipes all pinned buffers with ZeroMemory right before removal from the collection
    public void Clear()
    {
        _currentBuf.Dispose();
        _gen1Buf.Dispose();
        _gen2Buf.Dispose();
    }

    public void Dispose() => Clear();
}

// CommandParameter payload for TimeMachineViewModel.CopyValueCommand - see CurrentCopyParam/
// Gen1CopyParam/Gen2CopyParam above.
public sealed record TimeMachineCopyParam(string FieldLabel, string GenerationLabel, string Value);

public partial class TimeMachineViewModel : ObservableObject, IDisposable
{
    private readonly ClipboardAutoEraser _clipboardEraser = new();

    private readonly SecretRepository _secrets;
    private readonly StoredFileRepository _storedFiles;
    private readonly ICryptoService _crypto;
    private readonly AppSession _session;
    private readonly IAppNotificationService _notification;
    private readonly IDialogService _dialog;
    private readonly FaviconService _favicon;
    private readonly IAuditLogService _auditLog;
    private readonly AutoBackupService _autoBackup;
    private readonly SecretHistoryRepository _secretHistory;
    private readonly SecretDraftsRepository _secretDrafts;

    // EraseTool - matches the destructive toolbar buttons (permanent delete)
    private const string EraseToolGlyph = "\uE75C";
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger<TimeMachineViewModel> _logger;

    private CancellationTokenSource? _selectionDelayCts;

    // Codes currently defined by LocalizationManager.GetCategoryPresets() (+ 0 for Uncategorized),
    // captured once per LoadAsync(). A Secret.CategoryNum not in this set (e.g. a code left over
    // from before the 2026-08-17 preset renumbering) is treated as Uncategorized for filtering -
    // see EffectiveCategoryCode - without ever rewriting the stored value.
    private HashSet<int> _validCategoryCodes = [];

    private int EffectiveCategoryCode(int? categoryNum) =>
        categoryNum.HasValue && _validCategoryCodes.Contains(categoryNum.Value) ? categoryNum.Value : 0;

    // Collections: null-swap pattern (Clear() forbidden — CollectionChanged(Reset) races with the async Unloaded)
    private ObservableCollection<TimeMachineSecretItem> _allSecrets = [];
    private ObservableCollection<TimeMachineSecretItem> _filteredSecrets = [];
    private ObservableCollection<FilterCategoryItem> _filterComboItems = [];
    private ObservableCollection<TimeMachineRowViewModel> _rows = [];

    // Locked down with a getter-only arrow expression (blocks an accidental setter as a compile error)
    public ObservableCollection<TimeMachineSecretItem> AllSecrets     => _allSecrets;
    public ObservableCollection<TimeMachineSecretItem> FilteredSecrets => _filteredSecrets;
    public ObservableCollection<FilterCategoryItem>    FilterComboItems => _filterComboItems;
    public ObservableCollection<TimeMachineRowViewModel> Rows          => _rows;

    // SelectedSecret: changed from [ObservableProperty] to an explicit backing field
    // Dispose() can safely clear it via a direct backing-field assignment (zero notifications)
    private TimeMachineSecretItem? _selectedSecret;
    public TimeMachineSecretItem? SelectedSecret
    {
        get => _selectedSecret;
        set
        {
            if (_selectedSecret != value)
            {
                _selectedSecret = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanJumpToSecretList));
                OnPropertyChanged(nameof(CanRestoreGen1));
                OnPropertyChanged(nameof(CanRestoreGen2));
                OnPropertyChanged(nameof(IsRestoreBlockedByDraft));
                OnSelectedSecretChanged(value);
            }
        }
    }

    /// <summary>
    /// Called when the session scope is disposed.
    /// Discards collections via the null-swap pattern, avoiding the CollectionChanged notification from Clear().
    /// </summary>
    public void Dispose()
    {
        _clipboardEraser.Dispose();
        _selectionDelayCts?.Cancel();
        _selectionDelayCts?.Dispose();
        _selectionDelayCts = null;
        if (_allSecrets != null)
        {
            foreach (var item in _allSecrets) item.Dispose();
            _allSecrets = null!;
        }
        if (_filteredSecrets != null)
        {
            // FilteredSecrets is a subset of AllSecrets, so its elements were already Disposed via AllSecrets
            _filteredSecrets = null!;
        }
        if (_filterComboItems != null)
        {
            _filterComboItems = null!;
        }
        if (_rows != null)
        {
            foreach (var row in _rows) row.Dispose();
            _rows = null!;
        }
        _selectedSecret = null;   // Direct backing-field assignment (zero notifications)
        _gen1Slot?.Dispose(); _gen1Slot = null;
        _gen2Slot?.Dispose(); _gen2Slot = null;
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
    [ObservableProperty] public partial bool IsRevealed { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanJumpToSecretList))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial int? FilterCategoryCode { get; set; }
    [ObservableProperty] public partial bool FilterFavoritesOnly { get; set; }
    [ObservableProperty] public partial bool FilterDeletedOnly { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilteredFavorites))]
    public partial int FilteredFavoritesCount { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilteredDeleted))]
    public partial int FilteredDeletedCount { get; set; }
    public bool HasFilteredFavorites => FilteredFavoritesCount > 0;
    public bool HasFilteredDeleted   => FilteredDeletedCount   > 0;

    // Drives disabling the search box / category filter when there is nothing to search or filter
    // (e.g. after permanently deleting the last item from TimeMachine itself, which - unlike
    // SecretsPage's empty state - lands the user back on this same page with nothing left to show).
    // See TimeMachinePage.xaml. AllSecrets is a plain ObservableCollection<T> (mutated via Add/Clear
    // in LoadAsync, not reassigned), so this needs an explicit OnPropertyChanged - its own
    // CollectionChanged doesn't propagate to unrelated properties automatically.
    public bool HasAnySecrets => AllSecrets.Count > 0;
    partial void OnFilteredDeletedCountChanged(int value)
    {
        if (value == 0 && FilterDeletedOnly)
            FilterDeletedOnly = false;
    }
    [ObservableProperty] public partial string CurrentHeader { get; set; } = "-";
    [ObservableProperty] public partial string Gen1Header { get; set; } = "-";
    [ObservableProperty] public partial string Gen2Header { get; set; } = "-";
    [ObservableProperty] public partial bool HasGen1 { get; set; }
    [ObservableProperty] public partial bool HasGen2 { get; set; }
    [ObservableProperty] public partial bool IsDeleted { get; set; }

    // Restoring while a draft (SecretDrafts) exists would leave the pre-restore draft layered on top of the
    // post-restore Gen0 baseline. Locking the buttons eliminates that ambiguous merge state entirely.
    public bool CanRestoreGen1    => HasGen1 && !IsDeleted && SelectedSecret?.HasDraft != true;
    public bool CanRestoreGen2    => HasGen2 && !IsDeleted && SelectedSecret?.HasDraft != true;
    // Drives the explanatory notice shown next to the (grayed-out) restore buttons - jumping into
    // Time Machine while a draft is in progress is now always allowed (SecretsViewModel.CanJumpToTimeMachine),
    // so this tells the user why generation swap specifically is unavailable rather than leaving it unexplained.
    // Gated on HasGen1 (mirroring CanRestoreGen1's own condition minus the draft check): if there's no
    // Gen1 to begin with, discarding the draft wouldn't make restore possible anyway, so the draft
    // isn't the actual blocker and the notice would be misleading.
    public bool IsRestoreBlockedByDraft => HasGen1 && !IsDeleted && SelectedSecret?.HasDraft == true;
    // Gated on !IsBusy too: UndeleteAsync/other async operations update SelectedSecret only after
    // their awaits complete, so without this guard the button stays clickable mid-operation and can
    // jump using a stale SelectedSecret from before the operation finished.
    public bool CanJumpToSecretList => SelectedSecret != null && !IsDeleted && !IsBusy;

    partial void OnHasGen1Changed(bool value)
    {
        OnPropertyChanged(nameof(CanRestoreGen1));
        OnPropertyChanged(nameof(IsRestoreBlockedByDraft));
    }
    partial void OnHasGen2Changed(bool value) => OnPropertyChanged(nameof(CanRestoreGen2));
    partial void OnIsDeletedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRestoreGen1));
        OnPropertyChanged(nameof(CanRestoreGen2));
        OnPropertyChanged(nameof(CanJumpToSecretList));
        OnPropertyChanged(nameof(IsRestoreBlockedByDraft));
    }
    [ObservableProperty] public partial string DeletedInfoMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsFavorite { get; set; }

    private Secret? _currentEntity;
    private byte[]? _gen1Enc;
    private DateTime? _gen1At;
    private HistorySlotContent? _gen1Slot;
    private byte[]? _gen2Enc;
    private DateTime? _gen2At;
    private HistorySlotContent? _gen2Slot;

    public bool NeedsReload { get; set; }

    // ── Lifecycle state ──────────────────────────────────────────────────
    // IsActive = true: the page is in front and processes messages
    // IsActive = false: the page is not displayed. DB queries forbidden. Record changes via the NeedsReload flag.
    public bool IsActive { get; private set; }
    public void Resume() { NeedsReload = true; IsActive = true; }
    public void Pause()  => IsActive = false;

    public TimeMachineViewModel(
        SecretRepository secrets,
        StoredFileRepository storedFiles,
        ICryptoService crypto,
        AppSession session,
        IAppNotificationService notification,
        IDialogService dialog,
        FaviconService favicon,
        SecretHistoryRepository secretHistory,
        SecretDraftsRepository secretDrafts,
        IDispatcherService dispatcher,
        IAuditLogService auditLog,
        AutoBackupService autoBackup,
        ILogger<TimeMachineViewModel> logger)
    {
        _logger = logger;
        _secrets = secrets;
        _storedFiles = storedFiles;
        _crypto = crypto;
        _session = session;
        _notification = notification;
        _dialog = dialog;
        _favicon = favicon;
        _secretHistory = secretHistory;
        _secretDrafts = secretDrafts;
        _dispatcher = dispatcher;
        _auditLog = auditLog;
        _autoBackup = autoBackup;

        WeakReferenceMessenger.Default.Register<StorageChangedMessage>(this, (_, _) =>
        {
            NeedsReload = true;
        });
        WeakReferenceMessenger.Default.Register<SecretsImportedMessage>(this, (_, _) =>
        {
            NeedsReload = true;
        });
    }

    public async Task LoadAsync()
    {
        // Every LoadAsync (first load, a real background change, or a plain nav-menu revisit -
        // Resume() forces NeedsReload=true unconditionally) must land on no selection, matching
        // SecretsViewModel's unconditional EditingSecret=null. Without this, ApplyFilter's
        // filter-preservation logic below re-selected the previously viewed secret purely from
        // landing on the page, firing an unintended TimeMachineViewed audit entry with no user
        // action behind it, and leaving IsRevealed=true so the next auto-picked item's history
        // would render already unmasked.
        SelectedSecret = null;
        IsRevealed = false;
        IsBusy = true;
        try
        {
            FilterComboItems.Clear();
            FilterComboItems.Add(new FilterCategoryItem { Code = null, Name = LocalizationManager.Get("Common.All") });
            // Categories have no DB table - they're locale-defined presets (Categories.NN), enumerated
            // ascending by code, with the fixed Uncategorized (0) bucket appended last.
            foreach (var (code, name) in LocalizationManager.GetCategoryPresets())
                FilterComboItems.Add(new FilterCategoryItem { Code = code, Name = name });
            FilterComboItems.Add(new FilterCategoryItem { Code = 0, Name = LocalizationManager.Get("Categories.Uncategorized") });
            _validCategoryCodes = FilterComboItems.Where(c => c.Code.HasValue).Select(c => c.Code!.Value).ToHashSet();

            foreach (var existing in AllSecrets) existing.Dispose();
            AllSecrets.Clear();
            var all = await _secrets.GetAllIncludingDeletedAsync();
            var draftIds = await _secretDrafts.GetAllDraftIdsAsync();
            var key = _session.GetKey();
            // Per-record try-catch: one corrupted Secret's decrypt failure must not throw out of
            // LoadAsync entirely, which - reached via the async void Page.Loaded handler - would
            // otherwise crash the whole app rather than just hide that one record from the list.
            int decryptFailedCount = 0;
            foreach (var s in all)
            {
                SecurePlaintext? wsPlain = null, titlePlain = null;
                try
                {
                    wsPlain    = FieldCrypto.Open(s.Website, _crypto, key);
                    titlePlain = FieldCrypto.Open(s.Title,   _crypto, key);
                    var decWs = wsPlain != null ? Encoding.UTF8.GetString(wsPlain.Utf8) : null;
                    var secretItem = new TimeMachineSecretItem
                    {
                        Id = s.Id,
                        IsDeleted = s.DeletedAt.HasValue,
                        HasDraft = draftIds.Contains(s.Id),
                        CategoryNum = s.CategoryNum,
                        // Since IsExpired/IsExpiringSoon compare against the local calendar date (DateTime.Today),
                        // convert the DB's UTC instant to local time before storing it (same discipline as SecretsViewModel).
                        ExpiresAt = s.ExpiresAt?.ToLocalTime(),
                        IsFavorite = s.IsFavorite,
                        WebsiteDomain = ExtractDomain(decWs),
                        UpdatedAt = s.UpdatedAt,
                    };
                    if (titlePlain != null) secretItem.SetTitle(titlePlain.Utf8);
                    AllSecrets.Add(secretItem);
                }
                catch (CryptographicException)
                {
                    decryptFailedCount++;
                    _logger.LogWarning("[LoadAsync] Skipped Secret due to a decrypt failure. [Id={Id}]", s.Id);
                }
                finally
                {
                    wsPlain?.Dispose(); titlePlain?.Dispose();
                }
            }
            OnPropertyChanged(nameof(HasAnySecrets));
            ApplyFilter();
            _ = LoadFaviconsAsync(AllSecrets.ToList());
            if (_favicon.IsEnabled)
                _ = PrefetchAllFaviconsAsync();

            if (decryptFailedCount > 0)
            {
                _notification.Show(
                    LK.Common_ErrorPartialLoadTitle,
                    string.Format(LocalizationManager.Get("Common.ErrorPartialLoadText"), decryptFailedCount, all.Count),
                    NotificationSeverity.Warning, TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnFilterCategoryCodeChanged(int? value) => ApplyFilter();
    partial void OnFilterFavoritesOnlyChanged(bool value) => ApplyFilter();
    partial void OnFilterDeletedOnlyChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var keyword = SearchText?.Trim() ?? string.Empty;
        IEnumerable<TimeMachineSecretItem> matches = AllSecrets;

        if (!string.IsNullOrEmpty(keyword))
            matches = matches.Where(s =>
                MemoryExtensions.IndexOf(s.TitleSpan, keyword.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.UpdatedAtDisplay.Contains(keyword, StringComparison.Ordinal));

        if (FilterCategoryCode.HasValue)
            matches = matches.Where(s => EffectiveCategoryCode(s.CategoryNum) == FilterCategoryCode);

        var baseMatches = matches.ToList();
        FilteredFavoritesCount = baseMatches.Count(s => s.IsFavorite);
        FilteredDeletedCount   = baseMatches.Count(s => s.IsDeleted);

        IEnumerable<TimeMachineSecretItem> filtered = baseMatches;
        if (FilterFavoritesOnly) filtered = filtered.Where(s => s.IsFavorite);
        if (FilterDeletedOnly)   filtered = filtered.Where(s => s.IsDeleted);

        // No _session.LastSelectedTimeMachineId fallback here: that field only tracks the most
        // recent selection for AppSession's cross-lock memory-hygiene contract (see
        // AppSessionMemoryHygieneTests), it is not a selection to silently restore. Falling back to
        // it here reintroduces the LoadAsync().SelectedSecret=null reset above the moment any
        // search/category/favorites/deleted filter changes while nothing is selected.
        var prevId = SelectedSecret?.Id;
        FilteredSecrets.Clear();
        foreach (var s in filtered.OrderBy(s => s.DisplayTitle, StringComparer.CurrentCultureIgnoreCase))
            FilteredSecrets.Add(s);

        var restored = prevId.HasValue ? FilteredSecrets.FirstOrDefault(s => s.Id == prevId) : null;
        if (restored != null)
            SelectedSecret = restored;
        else if (prevId.HasValue && FilteredSecrets.Count > 0)
            SelectedSecret = FilteredSecrets[0];  // Fall back to the first item only when the previous selection was removed by the filter
        else
            SelectedSecret = null;
    }

    private void OnSelectedSecretChanged(TimeMachineSecretItem? value)
        => _ = HandleTreeSelectionAsync(value);

    private async Task HandleTreeSelectionAsync(TimeMachineSecretItem? value)
    {
        _selectionDelayCts?.Cancel();
        _selectionDelayCts?.Dispose();
        _selectionDelayCts = new CancellationTokenSource();
        var ct = _selectionDelayCts.Token;

        if (value == null)
        {
            // null (list clear) is immediate and lightweight, so no wait is needed
            await LoadSecretHistoryAsync(null, ct);
            return;
        }

        _session.LastSelectedTimeMachineId = value.Id;

        try
        {
            await Task.Delay(120, ct);
            IsBusy = true;
            await LoadSecretHistoryAsync(value, ct);
            _ = LogTimeMachineViewedAsync(value.Id, value.DisplayTitle);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal debounce cancellation from fast scrolling
        }
        catch (Exception ex)
        {
            _logger.LogError("[TimeMachine] Failed to load snapshot. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(3));
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsRevealedChanged(bool value)
    {
        foreach (var row in Rows)
            row.IsRevealed = value;
        if (value && SelectedSecret != null)
        {
            var id    = SelectedSecret.Id;
            var title = SelectedSecret.DisplayTitle;
            _ = LogRevealAsync(id, title);
        }
    }

    private async Task LogTimeMachineViewedAsync(int id, string title)
    {
        try
        {
            await _auditLog.LogAsync(
                AuditEventCode.TimeMachineViewed,
                new TimeMachineViewedPayload(id, title),
                _session.GetKey());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[TimeMachine] Failed to log TimeMachineViewed: {ExType}", ex.GetType().Name);
        }
    }

    private async Task LogRevealAsync(int id, string title)
    {
        try
        {
            await _auditLog.LogAsync(
                AuditEventCode.SecretViewed,
                new SecretViewedPayload(id, title),
                _session.GetKey());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[TimeMachine] Failed to log SecretViewed: {ExType}", ex.GetType().Name);
        }
    }

    private async Task LoadSecretHistoryAsync(TimeMachineSecretItem? item, CancellationToken ct = default)
    {
        if (item == null)
        {
            foreach (var row in Rows) row.Clear();
            Rows.Clear();
            _currentEntity = null;
            _gen1Enc = null; _gen1At = null; _gen1Slot?.Dispose(); _gen1Slot = null;
            _gen2Enc = null; _gen2At = null; _gen2Slot?.Dispose(); _gen2Slot = null;
            CurrentHeader = "-";
            HasGen1 = false;
            HasGen2 = false;
            Gen1Header = "-";
            Gen2Header = "-";
            IsDeleted = false;
            DeletedInfoMessage = string.Empty;
            return;
        }

        IsBusy = true;
        try
        {
            var entity = await _secrets.GetByIdAsync(item.Id);
            ct.ThrowIfCancellationRequested();
            if (entity == null) return;

            _currentEntity = entity;
            var key = _session.GetKey();

            // Loads and decrypts generations from the SecretHistory table
            (_gen1Enc, _gen1At, _gen2Enc, _gen2At) = await _secretHistory.GetSnapshotsAsync(entity.Id);
            ct.ThrowIfCancellationRequested();
            _gen1Slot?.Dispose(); _gen1Slot = null;
            _gen2Slot?.Dispose(); _gen2Slot = null;

            if (_gen1Enc != null)
            {
                byte[]? jsonBytes = null;
                try
                {
                    jsonBytes = _crypto.DecryptToPin(_gen1Enc, key.Span);
                    _gen1Slot = SnapshotSerializer.ReadToSlot(jsonBytes.AsSpan());
                }
                catch { }
                finally { if (jsonBytes != null) CryptographicOperations.ZeroMemory(jsonBytes); }
            }

            if (_gen2Enc != null)
            {
                byte[]? jsonBytes = null;
                try
                {
                    jsonBytes = _crypto.DecryptToPin(_gen2Enc, key.Span);
                    _gen2Slot = SnapshotSerializer.ReadToSlot(jsonBytes.AsSpan());
                }
                catch { }
                finally { if (jsonBytes != null) CryptographicOperations.ZeroMemory(jsonBytes); }
            }

            HasGen1 = _gen1Slot != null;
            HasGen2 = _gen2Slot != null;
            CurrentHeader = entity.UpdatedAt != default
                ? entity.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm")
                : "-";
            Gen1Header = _gen1At.HasValue ? _gen1At.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "-";
            Gen2Header = _gen2At.HasValue ? _gen2At.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "-";
            IsDeleted = entity.DeletedAt.HasValue;
            IsFavorite = entity.IsFavorite;
            // Shows when the item will be physically purged, not when it was deleted - the deletion
            // date itself is already recorded in the audit log (SecretSoftDeleted), so repeating it
            // here would be redundant. What the user actually needs here is the countdown.
            DeletedInfoMessage = entity.DeletedAt.HasValue
                ? string.Format(
                    LocalizationManager.Get("TimeMachine.Dialog.DeletedItemNotice"),
                    entity.DeletedAt.Value.AddDays(AppConstants.DeletedSecretRetentionDays).ToLocalTime().ToString("yyyy/MM/dd HH:mm"))
                : string.Empty;

            var customKeys = CollectCustomFieldKeys(entity, _gen1Slot, _gen2Slot, key);
            var overrides = DecryptLabelOverrides(entity.LabelOverrides, key);

            // Decrypt each of entity's encrypted fields into a temporary pinned buffer (no string is created)
            using var curTitleBuf  = new SecureCharBuffer();
            using var curUserIdBuf = new SecureCharBuffer();
            using var curPassBuf   = new SecureCharBuffer();
            using var curWebBuf    = new SecureCharBuffer();
            using var curEmailBuf  = new SecureCharBuffer();
            using var curNotesBuf  = new SecureCharBuffer();
            using var entityCfBuf  = new SecureCharBuffer();
            OpenToBuffer(entity.Title,    key, curTitleBuf);
            OpenToBuffer(entity.UserId,   key, curUserIdBuf);
            OpenToBuffer(entity.Password, key, curPassBuf);
            OpenToBuffer(entity.Website,  key, curWebBuf);
            OpenToBuffer(entity.Email,    key, curEmailBuf);
            OpenToBuffer(entity.Notes,    key, curNotesBuf);
            if (entity.CustomFields is { Length: > 0 })
            {
                byte[]? entityCfRaw = null;
                try
                {
                    entityCfRaw = _crypto.DecryptToPin(entity.CustomFields, key.Span);
                    if (entityCfRaw != null) FieldCrypto.FillBufferFromUtf8(entityCfBuf, entityCfRaw);
                }
                catch { }
                finally { if (entityCfRaw != null) CryptographicOperations.ZeroMemory(entityCfRaw); }
            }

            // Check whether TOTP is present via a span (no string is created)
            bool currentHasTotp;
            using (var totpSp = FieldCrypto.Open(entity.TotpSecret, _crypto, key))
                currentHasTotp = totpSp != null && !totpSp.Utf8.IsEmpty;

            foreach (var row in Rows) row.Clear();
            Rows.Clear();
            // Row order mirrors the edit screen (SecretsPage.xaml): Title/Category/Username/Password,
            // then SymbolSet directly under Password, TOTP, Website/Email/ExpiresAt, custom fields,
            // Notes, and attached files last (RecordExtrasControl: custom fields -> Notes -> attached files).
            AddRow(Label(overrides, "title",    "Common.Title"), false,
                curTitleBuf.Span,
                _gen1Slot != null ? _gen1Slot.TitleBuf.Span    : default,
                _gen2Slot != null ? _gen2Slot.TitleBuf.Span    : default);
            // CategoryNum is a plain int (not PII, not encrypted independently), so no decrypt/pin
            // buffer is needed - resolve the display name directly from FilterComboItems (already
            // populated by LoadAsync from the same category list). A code with no matching preset
            // (e.g. left over from before the 2026-08-17 preset renumbering) falls back to the same
            // Uncategorized (0) label, rather than rendering as a blank category.
            string? ResolveCategoryName(int? code) => code == null
                ? null
                : FilterComboItems.FirstOrDefault(c => c.Code == code)?.Name
                  ?? FilterComboItems.FirstOrDefault(c => c.Code == 0)?.Name;
            var curCatName  = ResolveCategoryName(entity.CategoryNum) ?? string.Empty;
            var gen1CatName = _gen1Slot != null ? ResolveCategoryName(_gen1Slot.CategoryNum) ?? string.Empty : null;
            var gen2CatName = _gen2Slot != null ? ResolveCategoryName(_gen2Slot.CategoryNum) ?? string.Empty : null;
            AddRow(LocalizationManager.Get("Secrets.CategorySelection"), false,
                curCatName.AsSpan(),
                gen1CatName != null ? gen1CatName.AsSpan() : default,
                gen2CatName != null ? gen2CatName.AsSpan() : default);
            AddRow(Label(overrides, "userId",   "Common.Username"), false,
                curUserIdBuf.Span,
                _gen1Slot != null ? _gen1Slot.UserIdBuf.Span   : default,
                _gen2Slot != null ? _gen2Slot.UserIdBuf.Span   : default);
            AddRow(Label(overrides, "password", "Common.Password"), true,
                curPassBuf.Span,
                _gen1Slot != null ? _gen1Slot.PasswordBuf.Span : default,
                _gen2Slot != null ? _gen2Slot.PasswordBuf.Span : default);
            // GeneratorSymbols isn't encrypted (Secret.GeneratorSymbols is a plain string, not PII - see
            // SecretsViewModel), so the current side is read directly without decryption.
            var curGenSym  = string.IsNullOrEmpty(entity.GeneratorSymbols) ? PasswordGenerator.DefaultSymbols : entity.GeneratorSymbols;
            var gen1GenSym = _gen1Slot != null
                ? (_gen1Slot.GenSymbolsBuf.IsEmpty ? PasswordGenerator.DefaultSymbols : new string(_gen1Slot.GenSymbolsBuf.Span))
                : null;
            var gen2GenSym = _gen2Slot != null
                ? (_gen2Slot.GenSymbolsBuf.IsEmpty ? PasswordGenerator.DefaultSymbols : new string(_gen2Slot.GenSymbolsBuf.Span))
                : null;
            AddRow(LocalizationManager.Get("Secrets.SymbolSet"), false,
                curGenSym.AsSpan(), gen1GenSym.AsSpan(), gen2GenSym.AsSpan());
            string TotpLabel(bool has) => has ? LocalizationManager.Get("Common.On") : LocalizationManager.Get("Common.Off");
            AddRow(LocalizationManager.Get("Common.TotpSecret"), false,
                TotpLabel(currentHasTotp).AsSpan(),
                _gen1Slot != null ? TotpLabel(_gen1Slot.HasTotpSecret).AsSpan() : default,
                _gen2Slot != null ? TotpLabel(_gen2Slot.HasTotpSecret).AsSpan() : default);
            AddRow(Label(overrides, "url",      "Common.Website"), false,
                curWebBuf.Span,
                _gen1Slot != null ? _gen1Slot.WebsiteBuf.Span  : default,
                _gen2Slot != null ? _gen2Slot.WebsiteBuf.Span  : default);
            AddRow(Label(overrides, "email", "Common.Email"), false,
                curEmailBuf.Span,
                _gen1Slot != null ? _gen1Slot.EmailBuf.Span : default,
                _gen2Slot != null ? _gen2Slot.EmailBuf.Span : default);
            AddRow(LocalizationManager.Get("Common.ExpiresAt"), false,
                (entity.ExpiresAt?.ToString("yyyy/MM/dd")).AsSpan(),
                FormatExpiresAt(_gen1Slot != null ? _gen1Slot.ExpiresAtBuf.Span : default).AsSpan(),
                FormatExpiresAt(_gen2Slot != null ? _gen2Slot.ExpiresAtBuf.Span : default).AsSpan());

            foreach (var (fieldId, label) in customKeys)
            {
                var cur = GetCustomValue(entityCfBuf.Span, fieldId, label);
                var g1  = GetCustomValue(_gen1Slot != null ? _gen1Slot.CustomFieldsBuf.Span : default, fieldId, label);
                var g2  = GetCustomValue(_gen2Slot != null ? _gen2Slot.CustomFieldsBuf.Span : default, fieldId, label);
                bool sensitive = IsCustomSensitive(entityCfBuf.Span, fieldId, label)
                              || IsCustomSensitive(_gen1Slot != null ? _gen1Slot.CustomFieldsBuf.Span : default, fieldId, label);
                AddRow(label, sensitive, cur.AsSpan(), g1.AsSpan(), g2.AsSpan());
            }

            AddRow(Label(overrides, "notes",    "Common.Notes"), false,
                curNotesBuf.Span,
                _gen1Slot != null ? _gen1Slot.NotesBuf.Span    : default,
                _gen2Slot != null ? _gen2Slot.NotesBuf.Span    : default);

            ct.ThrowIfCancellationRequested();
            var currentFileIds = (await _secrets.GetFileLinksAsync(entity.Id)).ToHashSet();
            var gen1FileIds    = _gen1Slot?.FileIds != null ? new HashSet<int>(_gen1Slot.FileIds) : [];
            var gen2FileIds    = _gen2Slot?.FileIds != null ? new HashSet<int>(_gen2Slot.FileIds) : [];
            var (curFiles, gen1Files, gen2Files) = await BuildAttachmentThumbnailsAsync(currentFileIds, gen1FileIds, gen2FileIds, key);
            // Highlight even when the count matches if the ID sets differ (detects a swap like ABC→ABD)
            AddAttachmentRow(LocalizationManager.Get("Common.AttachedFiles"), curFiles, gen1Files, gen2Files,
                gen1Differs: !currentFileIds.SetEquals(gen1FileIds),
                gen2Differs: !gen1FileIds.SetEquals(gen2FileIds));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Dictionary<string, string>? DecryptLabelOverrides(byte[]? encrypted, DekScope key)
    {
        if (encrypted is not { Length: > 0 }) return null;
        byte[]? jsonBytes = null;
        try
        {
            jsonBytes = _crypto.DecryptToPin(encrypted, key.Span);
            return JsonSerializer.Deserialize(jsonBytes.AsSpan(), TimeMachineJsonContext.Default.DictionaryStringString);
        }
        catch { return null; }
        finally { if (jsonBytes != null) CryptographicOperations.ZeroMemory(jsonBytes); }
    }

    private static string Label(Dictionary<string, string>? overrides, string overrideKey, string localeKey)
        => overrides != null && overrides.TryGetValue(overrideKey, out var v) && !string.IsNullOrEmpty(v)
            ? v
            : LocalizationManager.Get(localeKey);

    private TimeMachineRowViewModel AddRow(string label, bool sensitive, ReadOnlySpan<char> current, ReadOnlySpan<char> gen1, ReadOnlySpan<char> gen2)
    {
        var row = new TimeMachineRowViewModel
        {
            FieldLabel   = label,
            IsSensitive  = sensitive,
            Gen1HasValue = HasGen1,
            Gen2HasValue = HasGen2,
            IsRevealed   = IsRevealed,
        };
        row.SetCurrent(current);
        row.SetGen1(gen1);
        row.SetGen2(gen2);
        Rows.Add(row);
        return row;
    }

    private void AddAttachmentRow(string label,
        List<CompareFileThumbnail> current, List<CompareFileThumbnail> gen1, List<CompareFileThumbnail> gen2,
        bool gen1Differs, bool gen2Differs)
    {
        Rows.Add(new TimeMachineRowViewModel
        {
            FieldLabel       = label,
            IsAttachmentRow  = true,
            Gen1HasValue     = HasGen1,
            Gen2HasValue     = HasGen2,
            CurrentFiles     = current,
            Gen1Files        = gen1,
            Gen2Files        = gen2,
            ForceGen1Changed = gen1Differs,
            ForceGen2Changed = gen2Differs,
        });
    }

    // Fetches and decrypts attached-file thumbnails for all three sides in one round trip (the union
    // of Current/Gen1/Gen2 FileIds), so a file present on multiple sides isn't decrypted twice.
    private async Task<(List<CompareFileThumbnail> Current, List<CompareFileThumbnail> Gen1, List<CompareFileThumbnail> Gen2)>
        BuildAttachmentThumbnailsAsync(HashSet<int> currentIds, HashSet<int> gen1Ids, HashSet<int> gen2Ids, DekScope key)
    {
        var unionIds = currentIds.Union(gen1Ids).Union(gen2Ids).ToArray();
        if (unionIds.Length == 0) return ([], [], []);

        var files = await _storedFiles.GetByIdsAsync(unionIds, key);
        var byId = new Dictionary<int, CompareFileThumbnail>();
        try
        {
            foreach (var f in files)
            {
                var thumb = !f.IsQuarantined && f.ThumbnailBlob != null ? _crypto.DecryptToPin(f.ThumbnailBlob, key.Span) : null;
                byId[f.Id] = new CompareFileThumbnail
                {
                    FileId         = f.Id,
                    FileName       = Encoding.UTF8.GetString(f.FileName),
                    ContentType    = f.ContentTypeCode,
                    ThumbnailBytes = thumb,
                    IsQuarantined  = f.IsQuarantined,
                };
            }
        }
        finally
        {
            foreach (var f in files) CryptographicOperations.ZeroMemory(f.FileName);
        }

        List<CompareFileThumbnail> Map(HashSet<int> ids) =>
            ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        return (Map(currentIds), Map(gen1Ids), Map(gen2Ids));
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (_currentEntity == null) return;
        await ToggleFavoriteForIdAsync(_currentEntity.Id);
    }

    public async Task ToggleFavoriteForItemAsync(TimeMachineSecretItem item)
        => await ToggleFavoriteForIdAsync(item.Id);

    private async Task ToggleFavoriteForIdAsync(int id)
    {
        var listItem = AllSecrets.FirstOrDefault(s => s.Id == id);
        var newValue = !(listItem?.IsFavorite ?? false);
        await _secrets.SetFavoriteAsync(id, newValue);
        if (listItem != null) listItem.IsFavorite = newValue;
        if (_currentEntity?.Id == id)
        {
            _currentEntity.IsFavorite = newValue;
            IsFavorite = newValue;
        }

        // Notify other pages (SecretsPage, etc.) so their cached copy of this secret's IsFavorite
        // doesn't go stale - without this, SecretsPage's EditingSecret can keep the pre-toggle value,
        // which later shows up as a spurious "changed" field in SaveDraftAsync's Gen0 comparison.
        WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
    }

    [RelayCommand]
    private void CopyValue(TimeMachineCopyParam? param)
    {
        if (string.IsNullOrEmpty(param?.Value)) return;
        ClipboardHelper.SetText(param.Value);
        _clipboardEraser.ScheduleClear(param.Value.AsSpan());
        if (SelectedSecret != null)
            _ = LogValueCopiedAsync(SelectedSecret.Id, param.FieldLabel, param.GenerationLabel, SelectedSecret.DisplayTitle);
    }

    private async Task LogValueCopiedAsync(int targetId, string fieldLabel, string generationLabel, string name)
    {
        try
        {
            await _auditLog.LogAsync(
                AuditEventCode.TimeMachineValueCopiedToClipboard,
                new TimeMachineValueCopiedToClipboardPayload(targetId, fieldLabel, generationLabel, name),
                _session.GetKey());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[TimeMachine] Failed to log TimeMachineValueCopiedToClipboard. [{ExType}]", ex.GetType().Name);
        }
    }

    [RelayCommand]
    private async Task UndeleteAsync()
    {
        if (_currentEntity == null || !_currentEntity.DeletedAt.HasValue) return;

        IsBusy = true;
        try
        {
            var restoredId    = _currentEntity.Id;
            var restoredTitle = SelectedSecret?.DisplayTitle ?? string.Empty;
            await _secrets.UndeleteAsync(restoredId);
            _autoBackup.MarkContentChanged();
            try { await _auditLog.LogAsync(AuditEventCode.SecretUndeleted, new SecretUndeletedPayload(restoredId, restoredTitle), _session.GetKey()); }
            catch (Exception ex) { _logger.LogError("Audit log failed for undelete. [{ExType}]", ex.GetType().Name); }
            WeakReferenceMessenger.Default.Send(new SecretRestoredMessage(restoredId));
            _notification.Show(LK.TimeMachine_SuccessRestoreComplete,
                string.Format(LocalizationManager.GetById(LK.TimeMachine_SuccessItemRestored), restoredTitle),
                NotificationSeverity.Success, TimeSpan.FromSeconds(3));
            await LoadAsync();
            var restoredItem = FilteredSecrets.FirstOrDefault(s => s.Id == restoredId);
            if (restoredItem != null) SelectedSecret = restoredItem;
        }
        catch (Exception ex)
        {
            _logger.LogError("UndeleteAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PermanentDeleteAsync()
    {
        if (_currentEntity == null || !_currentEntity.DeletedAt.HasValue) return;

        var titleKey = _session.GetKey();
        using var titleBuf = new SecureCharBuffer();
        using (var titlePlain = FieldCrypto.Open(_currentEntity.Title, _crypto, titleKey))
            if (titlePlain != null) FieldCrypto.FillBufferFromUtf8(titleBuf, titlePlain.Utf8);
        bool confirmed = await _dialog.ConfirmDestructiveAsync(
            LocalizationManager.Get("Common.DeleteConfirm"),
            string.Format(LocalizationManager.Get("TimeMachine.Dialog.PurgeConfirmText"), titleBuf.IsEmpty ? string.Empty : new string(titleBuf.Span)),
            EraseToolGlyph,
            LocalizationManager.Get("Common.PurgePermanently"),
            defaultToCancel: true);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var dek          = _session.GetKey();
            var deletedId    = _currentEntity.Id;
            var deletedTitle = titleBuf.IsEmpty ? string.Empty : new string(titleBuf.Span);

            // Full deletion (SecretFileLinks + SecretHistory[cascade] + Secrets). Deliberately does
            // not touch StoredFiles: purging a secret only removes its references to any attached
            // files (SecretFileLinks), never the files themselves. StoredFiles is Gallery's
            // independently-owned pool (see the StoredFile class doc comment) - a file this secret
            // stops referencing simply becomes unlinked, still visible and manageable in Gallery.
            // Physically deleting a file's bytes is exclusively GalleryViewModel.DeleteSelectedAsync's
            // job (the only path with a destructive confirmation dialog and an audit-log entry
            // naming the file).
            await _secrets.HardDeleteAsync(deletedId);
            _autoBackup.MarkContentChanged();
            try { await _auditLog.LogAsync(AuditEventCode.SecretPermanentlyDeleted, new SecretPermanentlyDeletedPayload(deletedId, deletedTitle), dek); }
            catch (Exception ex) { _logger.LogError("Audit log failed for permanent delete. [{ExType}]", ex.GetType().Name); }

            await LoadAsync();
            SelectedSecret = null;
        }
        catch (Exception ex)
        {
            _logger.LogError("PermanentDeleteAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreGen1Async()
    {
        if (_currentEntity == null || _gen1Slot == null || SelectedSecret?.HasDraft == true) return;
        await RestoreFromAsync(0);
    }

    [RelayCommand]
    private async Task RestoreGen2Async()
    {
        if (_currentEntity == null || _gen2Slot == null || SelectedSecret?.HasDraft == true) return;
        await RestoreFromAsync(1);
    }

    private async Task RestoreFromAsync(int historyIndex)
    {
        var slot = historyIndex == 0 ? _gen1Slot : _gen2Slot;
        if (_currentEntity == null || slot == null) return;

        int targetGen = historyIndex + 1;
        var preview = new TimeMachineSwapPreview(
            CurrentHeader, Gen1Header, HasGen2 ? Gen2Header : null, targetGen);
        bool confirmed = await _dialog.ConfirmSwapAsync(LocalizationManager.Get("TimeMachine.Dialog.GenerationSwap"), preview);
        if (!confirmed) return;

        bool wasDeleted = _currentEntity.DeletedAt.HasValue;
        IsBusy = true;
        try
        {
            var key = _session.GetKey();

            // Pointer shuffle: just swaps SlotOrder (zero slot writes)
            await _secretHistory.SwapSlotOrderAsync(_currentEntity.Id, historyIndex);

            // Write the restored data to Gen0 (full-equivalence principle: restore all fields from the snapshot)
            _currentEntity.Title    = !slot.TitleBuf.IsEmpty    ? FieldCrypto.Seal(slot.TitleBuf.Span,    _crypto, key) : _currentEntity.Title;
            _currentEntity.UserId   = !slot.UserIdBuf.IsEmpty   ? FieldCrypto.Seal(slot.UserIdBuf.Span,   _crypto, key) : null;
            _currentEntity.Password = !slot.PasswordBuf.IsEmpty ? FieldCrypto.Seal(slot.PasswordBuf.Span, _crypto, key) : null;
            _currentEntity.Website  = !slot.WebsiteBuf.IsEmpty  ? FieldCrypto.Seal(slot.WebsiteBuf.Span,  _crypto, key) : null;
            _currentEntity.Email    = !slot.EmailBuf.IsEmpty ? FieldCrypto.Seal(slot.EmailBuf.Span, _crypto, key) : null;
            _currentEntity.Notes    = !slot.NotesBuf.IsEmpty    ? FieldCrypto.Seal(slot.NotesBuf.Span,    _crypto, key) : null;
            _currentEntity.ExpiresAt = !slot.ExpiresAtBuf.IsEmpty
                ? DateTime.Parse(slot.ExpiresAtBuf.Span, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : null;
            _currentEntity.CustomFields   = !slot.CustomFieldsBuf.IsEmpty   ? FieldCrypto.Seal(slot.CustomFieldsBuf.Span,   _crypto, key) : null;
            _currentEntity.TotpSecret     = !slot.TotpSecretBuf.IsEmpty     ? FieldCrypto.Seal(slot.TotpSecretBuf.Span,     _crypto, key) : null;
            _currentEntity.LabelOverrides = !slot.LabelOverridesBuf.IsEmpty ? FieldCrypto.Seal(slot.LabelOverridesBuf.Span, _crypto, key) : null;
            if (slot.CategoryNum.HasValue)
                _currentEntity.CategoryNum = slot.CategoryNum.Value;
            _currentEntity.IsFavorite = slot.IsFavorite;
            _currentEntity.DeletedAt = null;
            if (!slot.TimestampBuf.IsEmpty && DateTime.TryParse(slot.TimestampBuf.Span, null, System.Globalization.DateTimeStyles.RoundtripKind, out var genDt))
                _currentEntity.UpdatedAt = genDt;

            await _secrets.UpdatePreservingTimestampAsync(_currentEntity);
            _autoBackup.MarkContentChanged();
            try
            {
                var restoredTitle = SelectedSecret?.DisplayTitle ?? string.Empty;
                await _auditLog.LogAsync(
                    AuditEventCode.TimeMachineRestored,
                    new TimeMachineRestoredPayload(_currentEntity.Id, targetGen == 1 ? "Gen1" : "Gen2"),
                    key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[RestoreFrom] Failed to log TimeMachineRestored: {ExType}", ex.GetType().Name);
            }

            // Restore file links (excluding those already deleted from the gallery).
            // slot.FileIds is null both when the snapshot never had attachments and when
            // SnapshotSerializer.ReadToSlot collapsed an empty array to null (see its FFileIds
            // branch) - either way it means "this generation has zero attachments", not "leave
            // the current links untouched". Skipping the update here left Gen0's SecretFileLinks
            // pointing at the pre-restore files, so after restoring to a file-less generation the
            // new Gen0 and the newly-pushed Gen1 (the old Gen0 snapshot) both still referenced the
            // same attachment (violates the full-equivalence restore principle).
            var restoreFileIds = slot.FileIds ?? [];
            var validFiles = restoreFileIds.Length > 0
                ? await _storedFiles.GetByIdsAsync(restoreFileIds, key)
                : [];
            await _secrets.UpdateFileLinksAsync(_currentEntity.Id, validFiles.Select(f => f.Id));

            // After all fields are committed, disconnect the slot reference and release the buffer
            if (historyIndex == 0) { _gen1Slot?.Dispose(); _gen1Slot = null; }
            else                   { _gen2Slot?.Dispose(); _gen2Slot = null; }

            if (wasDeleted)
                WeakReferenceMessenger.Default.Send(new SecretRestoredMessage(_currentEntity.Id));
            else
                WeakReferenceMessenger.Default.Send(new SecretDataUpdatedMessage(_currentEntity.Id));
            _notification.Show(LK.Common_SuccessSaveComplete, LK.Common_SuccessSaveComplete, NotificationSeverity.Success, TimeSpan.FromSeconds(3));

            var item = SelectedSecret;
            await LoadAsync();
            SelectedSecret = FilteredSecrets.FirstOrDefault(s => s.Id == item?.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError("RestoreFromAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenToBuffer(byte[]? cipher, DekScope key, SecureCharBuffer buf)
    {
        using var sp = FieldCrypto.Open(cipher, _crypto, key);
        if (sp != null) FieldCrypto.FillBufferFromUtf8(buf, sp.Utf8);
    }

    private List<(int FieldId, string Label)> CollectCustomFieldKeys(Secret entity, HistorySlotContent? gen1, HistorySlotContent? gen2, DekScope key)
    {
        var result   = new List<(int, string)>();
        var seenIds  = new HashSet<int>();
        var seenLbls = new HashSet<string>();
        void Collect(ReadOnlySpan<char> cfSpan)
        {
            if (cfSpan.IsEmpty) return;
            try
            {
                var items = JsonSerializer.Deserialize(cfSpan, TimeMachineJsonContext.Default.ListCustomFieldSnapshot);
                if (items == null) return;
                foreach (var cf in items)
                {
                    if (cf.FieldId > 0)
                    {
                        if (seenIds.Add(cf.FieldId)) result.Add((cf.FieldId, cf.Label));
                    }
                    else if (!string.IsNullOrEmpty(cf.Label) && seenLbls.Add(cf.Label))
                    {
                        result.Add((0, cf.Label));
                    }
                }
            }
            catch { }
        }
        if (entity.CustomFields is { Length: > 0 })
        {
            byte[]? raw = null;
            try
            {
                raw = _crypto.DecryptToPin(entity.CustomFields, key.Span);
                if (raw != null)
                {
                    using var cfKeyBuf = new SecureCharBuffer();
                    FieldCrypto.FillBufferFromUtf8(cfKeyBuf, raw);
                    Collect(cfKeyBuf.Span);
                }
            }
            catch { }
            finally { if (raw != null) CryptographicOperations.ZeroMemory(raw); }
        }
        if (gen1 != null) Collect(gen1.CustomFieldsBuf.Span);
        if (gen2 != null) Collect(gen2.CustomFieldsBuf.Span);
        return result;
    }

    private string? GetCustomValue(ReadOnlySpan<char> cfSpan, int fieldId, string label)
    {
        if (cfSpan.IsEmpty) return null;
        try
        {
            var items = JsonSerializer.Deserialize(cfSpan, TimeMachineJsonContext.Default.ListCustomFieldSnapshot);
            var match = fieldId > 0
                ? items?.FirstOrDefault(c => c.FieldId == fieldId)
                : items?.FirstOrDefault(c => c.Label == label);
            return match?.Value;
        }
        catch { return null; }
    }

    private bool IsCustomSensitive(ReadOnlySpan<char> cfSpan, int fieldId, string label)
    {
        if (cfSpan.IsEmpty) return false;
        try
        {
            var items = JsonSerializer.Deserialize(cfSpan, TimeMachineJsonContext.Default.ListCustomFieldSnapshot);
            var match = fieldId > 0
                ? items?.FirstOrDefault(c => c.FieldId == fieldId)
                : items?.FirstOrDefault(c => c.Label == label);
            return match?.IsPassword ?? false;
        }
        catch { return false; }
    }

    private static string? FormatExpiresAt(ReadOnlySpan<char> iso) =>
        !iso.IsEmpty && DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToString("yyyy/MM/dd") : null;

    // Invoked fire-and-forget (`_ = PrefetchAllFaviconsAsync()`); a lock landing mid-fetch makes the next
    // DB access throw InvalidOperationException ("No active vault DB is set"). Catch it here instead of
    // letting it become a silent UnobservedTaskException (same pattern as SecretsViewModel).
    private async Task PrefetchAllFaviconsAsync()
    {
        try
        {
            var key = _session.GetKey();
            var domains = AllSecrets
                .Select(s => s.WebsiteDomain)
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var domain in domains)
                await _favicon.PrefetchAsync(domain!, key);
            await LoadFaviconsAsync(AllSecrets.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[PrefetchAllFaviconsAsync] Favicon prefetch stopped due to an error. [{ExType}]", ex.GetType().Name);
        }
    }

    private async Task LoadFaviconsAsync(List<TimeMachineSecretItem> items)
    {
        var key = _session.GetKey();
        foreach (var item in items.Where(i => !string.IsNullOrEmpty(i.WebsiteDomain)))
        {
            // One domain's cache lookup failing (DB lock, transient I/O) must not stop every
            // remaining item in this batch from getting its favicon.
            try
            {
                var bytes = await _favicon.GetCachedAsync(item.WebsiteDomain!, key);
                if (bytes != null)
                    await SetItemFaviconOnUiAsync(item, bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[LoadFaviconsAsync] Skipped a domain due to an error. [{ExType}]", ex.GetType().Name);
            }
        }
    }

    private Task SetItemFaviconOnUiAsync(TimeMachineSecretItem item, byte[] data)
        => _dispatcher.EnqueueAsync(async () =>
        {
            try
            {
                using var ms = new System.IO.MemoryStream(data);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                item.FaviconSource = bmp;
            }
            catch { /* Ignore favicon fetch failures */ }
        });

    private static string? ExtractDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private record CustomFieldSnapshot(string Label, string Value, bool IsPassword, int FieldId = 0);

    [JsonSerializable(typeof(List<CustomFieldSnapshot>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    private partial class TimeMachineJsonContext : JsonSerializerContext { }
}
