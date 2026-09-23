// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Common;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using FC = NaimitsuVault.Common.FileTypeCode;

namespace NaimitsuVault.ViewModels;

/// <summary>
/// One row of the gallery.
/// FileName is persistently stored in a SecureCharBuffer (POH-pinned char[]);
/// the XAML-binding getter creates a transient new string(Span) on each access.
/// ThumbnailData is a pinned array from DecryptToPin / GC.AllocateArray(pinned).
/// Dispose() ZeroMemories the filename buffer and ThumbnailData.
/// </summary>
public partial class FileItem : ObservableObject, IDisposable
{
    public int Id { get; set; }

    private readonly SecureCharBuffer _fileNameBuf = new();

    // For XAML binding — persisted in a SecureCharBuffer (POH-pinned)
    public string FileName
    {
        get => _fileNameBuf.ToDisplayString();
        set { _fileNameBuf.SetFromSpan(value.AsSpan()); OnPropertyChanged(); }
    }

    // Allocation-free span access — for filter search / duplicate check
    internal ReadOnlySpan<char> FileNameSpan => _fileNameBuf.Span;

    // Copies directly from UTF-8 bytes (no string / Encoding.GetString created)
    public void SetFileNameFromUtf8(ReadOnlySpan<byte> utf8) => FieldCrypto.FillBufferFromUtf8(_fileNameBuf, utf8);

    [ObservableProperty] public partial byte[]? ThumbnailData { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime FileModifiedAt { get; set; }

    public int ContentType { get; set; }
    public DateTime? CertExpiresAt { get; set; }
    public int? DaysUntilExpiry { get; set; }

    /// <summary>Soft-deleted (in the trash). Mirrors StoredFile.DeletedAt.HasValue.</summary>
    [ObservableProperty] public partial bool IsDeleted { get; set; }

    /// <summary>ContentType/FileName-extension mismatch detected on load (suspected tampering or corruption).</summary>
    public bool IsQuarantined { get; set; }

    public bool HasExpiryAlert => DaysUntilExpiry.HasValue && DaysUntilExpiry.Value <= AppConstants.CertExpirationWarnDays;
    public bool IsExpired       => DaysUntilExpiry.HasValue && DaysUntilExpiry.Value < 0;
    public bool IsExpiringSoon  => HasExpiryAlert && !IsExpired;

    public string FileTypeGlyph =>
        FC.IsCertOrKeyCategory(ContentType) ? "\uEB95" :
        ContentType is >= 4000 and <= 5999 ? "\uF000" :
        FC.IsArchive(ContentType)          ? "\uE8B3" :
        "\uE160";

    public long FileSize { get; set; }

    public string FileSizeDisplay => FileSize switch
    {
        >= 1024 * 1024 => $"{FileSize / 1024.0 / 1024.0:F1} MB",
        >= 1024        => $"{FileSize / 1024.0:F1} KB",
        _              => $"{FileSize} B"
    };

    public string CreatedAtDisplay => CreatedAt.ToString("yyyy/MM/dd HH:mm");
    // Empty string when FileModifiedAt is DateTime.MinValue.ToLocalTime() (fallback for legacy data)
    public string FileModifiedAtDisplay => FileModifiedAt.Year > 1 ? FileModifiedAt.ToString("yyyy/MM/dd HH:mm") : string.Empty;

    public void Dispose()
    {
        _fileNameBuf.Dispose();
        var thumb = ThumbnailData;
        ThumbnailData = null; // Detach the XAML binding first
        if (thumb is { Length: > 0 })
            CryptographicOperations.ZeroMemory(thumb.AsSpan());
        GC.SuppressFinalize(this);
    }
}

public partial class GalleryViewModel : ObservableObject, IDisposable
{
    private readonly StoredFileRepository _storedFiles;
    private readonly SecretRepository _secrets;
    private readonly SecretDraftsRepository _secretDrafts;
    private readonly ICryptoService _crypto;
    private readonly AppSession _session;
    private readonly IAppNotificationService _notification;
    private readonly IDialogService _dialog;
    private readonly IFilePickerService _filePicker;
    private readonly IWindowService _windowService;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<GalleryViewModel> _logger;
    private readonly ProfileService _profileService;
    private readonly FaviconService _favicon;
    private readonly AvatarService _avatar;
    private readonly IDispatcherService _dispatcher;
    private readonly AutoBackupService _autoBackup;

    // Delete (trash can) - matches the destructive toolbar buttons
    private const string DeleteGlyph = "\uE74D";
    private const string EraseToolGlyph = "\uE75C";

    private readonly List<FileItem> _allItems = [];
    private CancellationTokenSource? _selectionDelayCts;

    // Throttles the full-table ContentType repair scan (see LoadAsync): re-checking on literally
    // every Gallery visit is wasteful when the user is just clicking around the nav pane, but never
    // re-checking after the session's first load leaves any mid-session drift stuck until relock.
    private DateTime? _lastContentTypeRepairAt;

    /// <summary>Test-only hook: simulates the repair throttle window having already elapsed.</summary>
    internal void ResetContentTypeRepairThrottleForTests() => _lastContentTypeRepairAt = null;

    /// <summary>Count of StoredFiles excluded from this load because IsQuarantined was set (see DashboardViewModel).</summary>
    public int QuarantinedCount { get; private set; }

    // ── Gallery right pane: unified link list (read-only) — Profile row first when linked, then secrets ──
    public ObservableCollection<GalleryLinkItem> LinkItems { get; } = [];
    public bool LinkHasItems => LinkItems.Count > 0;
    private CancellationTokenSource _linkLoadCts = new();

    // Flag that detects file additions/deletions from other pages and triggers a reload on next display
    public bool NeedsReload { get; set; }

    // Discipline 2: zombie-VM suppression flag
    public bool IsActive { get; private set; }
    public void Resume() => IsActive = true;
    public void Pause()  => IsActive = false;

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ItemCount))]
    [NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedTypeFilter { get; set; } = "all";

    partial void OnSelectedTypeFilterChanged(string value) => ApplyFilter();

    [ObservableProperty]
    public partial bool FilterExpiryOnly { get; set; }

    partial void OnFilterExpiryOnlyChanged(bool value)
    {
        if (value && FilterDeletedOnly) FilterDeletedOnly = false; // mutually exclusive
        ApplyFilter();
    }

    [ObservableProperty]
    public partial bool FilterDeletedOnly { get; set; }

    partial void OnFilterDeletedOnlyChanged(bool value)
    {
        if (value && FilterExpiryOnly) FilterExpiryOnly = false; // mutually exclusive
        ApplyFilter();
    }

    public bool HasAnyExpiryAlert => _allItems.Any(i => i.HasExpiryAlert);
    public bool HasAnyDeletedItem => _allItems.Any(i => i.IsDeleted);

    public bool HasSearchQuery => !string.IsNullOrEmpty(SearchQuery);

    public string FilterLabelAll      => LocalizationManager.Get("Common.All");
    public string FilterLabelImage    => string.Format(LocalizationManager.Get("Gallery.FilterImages"), "(png, jpg, gif...)");
    public string FilterLabelPdf      => string.Format(LocalizationManager.Get("Gallery.FilterPdf"), "(pdf)");
    public string FilterLabelCert     => string.Format(LocalizationManager.Get("Gallery.FilterCerts"), "(pfx, pem, id_*...)");
    public string FilterLabelConfig   => string.Format(LocalizationManager.Get("Gallery.FilterConfigs"), "(env, yaml, toml...)");
    public string FilterLabelJsonXml  => string.Format(LocalizationManager.Get("Gallery.FilterJsonXml"), "(json, xml, har)");
    public string FilterLabelMarkdown => string.Format(LocalizationManager.Get("Gallery.FilterMarkdown"), "(md)");
    public string FilterLabelDatabase => string.Format(LocalizationManager.Get("Gallery.FilterSql"), "(sql)");
    public string FilterLabelLicense  => string.Format(LocalizationManager.Get("Gallery.FilterLicense"), "(lic, license)");
    public string FilterLabelArchive  => string.Format(LocalizationManager.Get("Gallery.FilterArchive"), "(zip, 7z, tgz...)");

    public bool HasCertExpiryAlert    => _allItems.Any(i => !i.IsDeleted && i.DaysUntilExpiry.HasValue && i.DaysUntilExpiry.Value <= AppConstants.CertExpirationWarnDays);
    public bool HasCertExpiredAny     => _allItems.Any(i => !i.IsDeleted && i.DaysUntilExpiry.HasValue && i.DaysUntilExpiry.Value < 0);
    public int  CertExpiryAlertCount  => _allItems.Count(i => !i.IsDeleted && i.DaysUntilExpiry.HasValue && i.DaysUntilExpiry.Value <= AppConstants.CertExpirationWarnDays);

    public IEnumerable<FileItem> GetCertAlertItems()
        => _allItems.Where(i => !i.IsDeleted && i.DaysUntilExpiry.HasValue && i.DaysUntilExpiry.Value <= AppConstants.CertExpirationWarnDays);

    /// <summary>Loads certificate expiry data once if not yet loaded, when another VM needs it.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_allItems.Count > 0) return;
        await LoadAsync();
    }

    public string CertExpiryWarningText
    {
        get
        {
            var expired  = _allItems.Count(i => i.DaysUntilExpiry.HasValue && i.DaysUntilExpiry.Value < 0);
            var expiring = _allItems.Count(i => i.DaysUntilExpiry.HasValue && i.DaysUntilExpiry.Value >= 0 && i.DaysUntilExpiry.Value <= AppConstants.CertExpirationWarnDays);
            if (expired > 0 && expiring > 0)
                return string.Format(LocalizationManager.Get("Gallery.WarningCertMixedExpiryAlert"), expired, expiring, AppConstants.CertExpirationWarnDays);
            if (expired > 0)
                return string.Format(LocalizationManager.Get("Gallery.WarningCertExpiredCount"), expired);
            return string.Format(LocalizationManager.Get("Gallery.WarningCertExpiringSoonCount"), expiring, AppConstants.CertExpirationWarnDays);
        }
    }

    public ObservableCollection<FileItem> FileItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(LinkNoSelection))]
    [NotifyPropertyChangedFor(nameof(LinkSelectedButEmpty))]
    [NotifyPropertyChangedFor(nameof(IsGalleryWriteEnabled))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(PurgeSelectedCommand))]
    public partial FileItem? SelectedFile { get; set; }

    public bool HasSelection        => SelectedFile != null;
    public bool LinkNoSelection     => SelectedFile == null;
    public bool LinkSelectedButEmpty => SelectedFile != null && LinkItems.Count == 0;
    public bool IsGalleryWriteEnabled => HasSelection && !_session.IsReadOnlyRestricted;
    public string SelectedFileName => SelectedFile?.FileName ?? string.Empty;
    public string SelectedFileSizeDisplay => SelectedFile?.FileSizeDisplay ?? string.Empty;

    public bool IsEmpty => _allItems.Count == 0;
    public int ItemCount => FileItems.Count;

    public GalleryViewModel(
        StoredFileRepository storedFiles,
        SecretRepository secretRepository,
        SecretDraftsRepository secretDrafts,
        ICryptoService crypto,
        AppSession session,
        IAppNotificationService notification,
        IDialogService dialog,
        IFilePickerService filePicker,
        IWindowService windowService,
        IAuditLogService auditLog,
        ILogger<GalleryViewModel> logger,
        ProfileService profileService,
        FaviconService favicon,
        AvatarService avatar,
        IDispatcherService dispatcher,
        AutoBackupService autoBackup)
    {
        _storedFiles = storedFiles;
        _secrets = secretRepository;
        _secretDrafts = secretDrafts;
        _crypto = crypto;
        _session = session;
        _notification = notification;
        _dialog = dialog;
        _filePicker = filePicker;
        _windowService = windowService;
        _auditLog = auditLog;
        _logger = logger;
        _profileService = profileService;
        _favicon = favicon;
        _avatar = avatar;
        _dispatcher = dispatcher;
        _autoBackup = autoBackup;
        FileItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ItemCount));
        LinkItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(LinkHasItems));
            OnPropertyChanged(nameof(LinkSelectedButEmpty));
        };

        // Detect storage changes from other pages, set the reload flag, and rescan the right pane in real time
        WeakReferenceMessenger.Default.Register<StorageChangedMessage>(this, (_, _) =>
        {
            NeedsReload = true;
            // Skip DB queries while inactive (NeedsReload triggers a reload on next display)
            if (!IsActive) return;
            // If a file is currently selected, immediately flush the right-pane link info (sync with the viewer)
            if (SelectedFile != null)
                _ = LoadLinksAsync(SelectedFile.Id);
        });
    }

    partial void OnSelectedFileChanged(FileItem? value)
    {
        OnPropertyChanged(nameof(SelectedFileName));
        OnPropertyChanged(nameof(SelectedFileSizeDisplay));
        if (value == null)
            ClearLinks();
        else
            _ = HandleFileSelectionAsync(value);
        // LoadLinksAsync is consolidated entirely into GalleryGrid_SelectionChanged.
        // Do not call it from here (this eliminates duplicate DB query bursts from OnSelectedFileChanged).
    }

    private async Task HandleFileSelectionAsync(FileItem value)
    {
        _selectionDelayCts?.Cancel();
        _selectionDelayCts?.Dispose();
        _selectionDelayCts = new CancellationTokenSource();
        var ct = _selectionDelayCts.Token;

        try
        {
            await Task.Delay(120, ct);
            IsBusy = true;
            _session.LastSelectedFileId = value.Id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal debounce cancellation from fast scrolling
        }
        catch (Exception ex)
        {
            _logger.LogError("Gallery: failed to load file details. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(3));
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    private bool MatchesTypeFilter(FileItem item) => SelectedTypeFilter switch
    {
        "image"    => FC.IsImage(item.ContentType),
        "pdf"      => FC.IsPdf(item.ContentType),
        "cert"     => FC.IsCertOrKeyCategory(item.ContentType),
        "config"   => FC.IsConfigType(item.ContentType),
        "jsonxml"  => item.ContentType is FC.Json or FC.Xml,
        "markdown" => item.ContentType == FC.Markdown,
        "database" => item.ContentType == FC.Sql,
        "license"  => item.ContentType == FC.License,
        "archive"  => FC.IsArchive(item.ContentType),
        _          => true
    };

    private void ApplyFilter(int? restoreId = null)
    {
        var prevId = restoreId ?? SelectedFile?.Id;
        var q = SearchQuery.Trim(); // User input text (non-sensitive). Trim() minimizes string churn
        FileItems.Clear();
        IEnumerable<FileItem> source = _allItems;
        if (!string.IsNullOrEmpty(q))
            // Span comparison via MemoryExtensions.Contains — no string allocation for i.FileNameSpan
            source = source.Where(i =>
                MemoryExtensions.Contains(i.FileNameSpan, q.AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                i.CreatedAtDisplay.Contains(q, StringComparison.Ordinal) ||
                i.FileModifiedAtDisplay.Contains(q, StringComparison.Ordinal));
        if (SelectedTypeFilter != "all")
            source = source.Where(MatchesTypeFilter);
        if (FilterExpiryOnly)
            source = source.Where(i => i.HasExpiryAlert);
        source = FilterDeletedOnly ? source.Where(i => i.IsDeleted) : source.Where(i => !i.IsDeleted);
        foreach (var item in source)
            FileItems.Add(item);
        SelectedFile = prevId.HasValue ? FileItems.FirstOrDefault(i => i.Id == prevId) : null;
    }

    public async Task LoadAsync()
    {
        var prevId = SelectedFile?.Id ?? _session.LastSelectedFileId;
        SelectedFile = null;
        SearchQuery = string.Empty;
        SelectedTypeFilter = "all";
        FilterExpiryOnly = false;
        FilterDeletedOnly = false;

        IsBusy = true;
        try
        {
            var key = _session.GetKey();
            // Throttled, not gated to "once per session" or "every load": a mismatch introduced
            // mid-session (e.g. direct external DB edit) must clear without requiring a full
            // relock/restart, but repeatedly clicking around the nav pane must not re-trigger a
            // full decrypt-and-compare scan of every StoredFile on each visit.
            var now = DateTime.UtcNow;
            if (_lastContentTypeRepairAt is null ||
                now - _lastContentTypeRepairAt.Value >= TimeSpan.FromMinutes(AppConstants.ContentTypeRepairThrottleMinutes))
            {
                _lastContentTypeRepairAt = now;
                var corrections = await _storedFiles.RepairContentTypeMismatchesAsync(key);
                // One audit log entry per repaired file (not an aggregate count), so the exact
                // file and its old/new ContentType can be identified and its correction verified.
                foreach (var c in corrections)
                {
                    try
                    {
                        await _auditLog.LogAsync(
                            AuditEventCode.FileContentTypeRepaired,
                            new FileContentTypeRepairedPayload(c.Id, c.OldContentType, c.NewContentType, c.FileName),
                            key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to write audit log 0xF300 for StoredFile(Id={Id}) ContentType repair (non-fatal). [{ExType}]", c.Id, ex.GetType().Name);
                    }
                }
            }
            var allImages = await _storedFiles.GetAllIncludingDeletedAsync(key);

            // Clear the UI collection first, then Dispose (ZeroMemory) the old FileItems
            FileItems.Clear();
            foreach (var old in _allItems) old.Dispose();
            _allItems.Clear();

            QuarantinedCount = 0;
            int failedCount = 0;
            try
            {
                foreach (var img in allImages)
                {
                    if (img.IsQuarantined)
                    {
                        // ContentType/FileName-extension mismatch (suspected tampering or corruption).
                        // Excluded from the normal listing; see AttachedFiles handling in SecretsViewModel
                        // for the per-secret "problem" placeholder shown for a quarantined attachment.
                        QuarantinedCount++;
                        continue;
                    }

                    // One file's decode/decrypt failure must not stop every remaining file in the
                    // gallery from being listed.
                    try
                    {
                        var item = new FileItem
                        {
                            Id = img.Id,
                            ContentType = img.ContentTypeCode,
                            CreatedAt = img.CreatedAt.ToLocalTime(),
                            FileModifiedAt = img.FileModifiedAt.ToLocalTime(),
                            FileSize = img.FileSize,
                            IsDeleted = img.DeletedAt.HasValue
                        };
                        item.SetFileNameFromUtf8(img.FileName); // Copy pinned plaintext bytes → SecureCharBuffer

                        if (img.ThumbnailBlob != null)
                        {
                            var decrypted = _crypto.DecryptToPin(img.ThumbnailBlob, key.Span);
                            if (decrypted != null)
                                item.ThumbnailData = decrypted; // Keep the pinned array from DecryptToPin as-is
                        }
                        if (img.OriginalBlob != null && FC.IsX509ParseCandidate(item.ContentType))
                        {
                            byte[]? certBytes = null;
                            try
                            {
                                certBytes = _crypto.DecryptToPin(img.OriginalBlob, key.Span);
                                if (certBytes != null)
                                {
                                    var certInfo = CertificateHelper.TryParse(certBytes, item.ContentType);
                                    if (certInfo != null)
                                    {
                                        item.CertExpiresAt = certInfo.NotAfter;
                                        item.DaysUntilExpiry = certInfo.DaysUntilExpiry;
                                    }
                                }
                            }
                            finally
                            {
                                if (certBytes != null) CryptographicOperations.ZeroMemory(certBytes.AsSpan());
                            }
                        }
                        _allItems.Add(item);
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        _logger.LogWarning("[LoadAsync] Skipped a file due to an error. [{ExType}]", ex.GetType().Name);
                    }
                }
            }
            finally
            {
                // Immediately wipe the plaintext filename array (an unpinned copy) that StoredFileRepository allocates on each decrypt
                foreach (var img in allImages)
                    CryptographicOperations.ZeroMemory(img.FileName);
            }

            ApplyFilter(prevId);
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasAnyDeletedItem));
            NotifyCertExpiryChanged();

            if (failedCount > 0)
            {
                _notification.Show(LK.Common_Warning,
                    string.Format(LocalizationManager.Get("Common.InfoFilesLoadFailed"), failedCount),
                    NotificationSeverity.Warning, TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("LoadAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddFilesAsync(string[] filePaths)
    {
        if (_session.IsReadOnlyRestricted) return;
        IsBusy = true;
        var key = _session.GetKey();
        int successCount = 0;
        int skipCount = 0;
        int unsupportedCount = 0;
        int salvagedCount = 0;
        try
        {
            foreach (var path in filePaths)
            {
                // Path.GetFileName(ReadOnlySpan<char>) — no intermediate string created
                ReadOnlySpan<char> fileNameSpan = Path.GetFileName(path.AsSpan());
                var contentType = FC.FromFileName(fileNameSpan); // Already accepts ReadOnlySpan<char>
                if (contentType == 0) { unsupportedCount++; continue; }
                bool isPdf   = FC.IsPdf(contentType);

                byte[]? pinnedFileNameBytes = null;
                byte[]? originalData = null;
                byte[]? thumbnailData = null;
                try
                {
                    // (1) Encode the filename into a GC-immovable pinned UTF-8 array (no string involved)
                    int fnByteCount = Encoding.UTF8.GetByteCount(fileNameSpan);
                    pinnedFileNameBytes = GC.AllocateArray<byte>(fnByteCount, pinned: true);
                    Encoding.UTF8.GetBytes(fileNameSpan, pinnedFileNameBytes.AsSpan());

                    // (2) Read the file content directly into a GC-immovable pinned array
                    var fileInfo = new FileInfo(path);
                    originalData = GC.AllocateArray<byte>(checked((int)fileInfo.Length), pinned: true);
                    await using (var fsr = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous))
                        await fsr.ReadExactlyAsync(originalData.AsMemory());

                    // (3) Hash-based duplicate check. FindByHashAsync matches regardless of DeletedAt,
                    // so a soft-deleted file with the same content is salvaged (restored) instead of
                    // being skipped as a duplicate or re-inserted as a new row.
                    var fileHash = HMACSHA256.HashData(key.Span, SHA256.HashData(originalData));
                    var existing = await _storedFiles.FindByHashAsync(fileHash, key);
                    if (existing != null)
                    {
                        if (existing.DeletedAt.HasValue)
                        {
                            await _storedFiles.UndeleteAsync(existing.Id);
                            try
                            {
                                _autoBackup.MarkContentChanged();
                                await _auditLog.LogAsync(AuditEventCode.FileUndeleted, new FileUndeletedPayload(existing.Id, Path.GetFileName(path)), key);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("[AddFilesAsync] Failed to log FileUndeleted (salvage). [{ExType}]", ex.GetType().Name);
                            }
                            var salvagedItem = _allItems.FirstOrDefault(i => i.Id == existing.Id);
                            if (salvagedItem != null) salvagedItem.IsDeleted = false;
                            salvagedCount++;
                        }
                        else
                        {
                            skipCount++;
                        }
                        continue;
                    }

                    // (4) Generate thumbnail (movable array — ZeroMemory in finally, create a pinned copy for display)
                    if (FC.CanCreateThumbnail(contentType))
                        thumbnailData = await ImageHelper.CreateThumbnailAsync(originalData, 250, isPdf);

                    // (5) Encrypt
                    var fileModifiedAt = File.GetLastWriteTimeUtc(path);
                    var (encOrig, encThumb) = await Task.Run(() => (
                        _crypto.Encrypt(originalData, key.Span),
                        thumbnailData != null ? _crypto.Encrypt(thumbnailData, key.Span) : null));

                    // (6) Create StoredFile (FileName passes the pinned array as-is)
                    var newImage = new StoredFile
                    {
                        FileName = pinnedFileNameBytes, // Wiped in finally after being encrypted and saved inside AddAsync
                        ContentTypeCode = contentType,
                        FileSize = originalData.Length,
                        FileHash = fileHash,
                        FileModifiedAt = fileModifiedAt,
                        OriginalBlob = encOrig,
                        ThumbnailBlob = encThumb
                    };
                    var newId = await _storedFiles.AddAsync(newImage, key);
                    try
                    {
                        _autoBackup.MarkContentChanged();
                        await _auditLog.LogAsync(AuditEventCode.FileAdded, new FileAddedPayload(newId, Path.GetFileName(path)), key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("[AddFilesAsync] Failed to log FileAdded. [{ExType}]", ex.GetType().Name);
                    }

                    // (7) Create FileItem — filename copied into SecureCharBuffer via SetFileNameFromUtf8
                    var fileItem = new FileItem
                    {
                        Id = newId,
                        ContentType = contentType,
                        CreatedAt = DateTime.Now,
                        FileModifiedAt = fileModifiedAt.ToLocalTime(),
                        FileSize = originalData.Length,
                    };
                    fileItem.SetFileNameFromUtf8(pinnedFileNameBytes);

                    // (8) Pinned copy for thumbnail display (the movable thumbnailData is ZeroMemoried in finally)
                    if (thumbnailData is { Length: > 0 })
                    {
                        var pinnedThumb = GC.AllocateArray<byte>(thumbnailData.Length, pinned: true);
                        thumbnailData.AsSpan().CopyTo(pinnedThumb);
                        fileItem.ThumbnailData = pinnedThumb;
                    }

                    if (FC.IsX509ParseCandidate(contentType))
                    {
                        var certInfo = CertificateHelper.TryParse(originalData, contentType);
                        if (certInfo != null)
                        {
                            fileItem.CertExpiresAt = certInfo.NotAfter;
                            fileItem.DaysUntilExpiry = certInfo.DaysUntilExpiry;
                        }
                    }
                    _allItems.Insert(0, fileItem);

                    // (9) Filter match check — span comparison (no string allocation)
                    var q = SearchQuery.Trim();
                    bool matchesText = string.IsNullOrEmpty(q) ||
                        MemoryExtensions.Contains(fileItem.FileNameSpan, q.AsSpan(), StringComparison.OrdinalIgnoreCase);
                    if (matchesText && MatchesTypeFilter(fileItem) && !FilterDeletedOnly)
                        FileItems.Insert(0, fileItem);

                    successCount++;
                }
                finally
                {
                    if (pinnedFileNameBytes != null) CryptographicOperations.ZeroMemory(pinnedFileNameBytes.AsSpan());
                    if (originalData != null) CryptographicOperations.ZeroMemory(originalData.AsSpan());
                    if (thumbnailData != null) CryptographicOperations.ZeroMemory(thumbnailData.AsSpan());
                }
            }

            OnPropertyChanged(nameof(IsEmpty));

            if (salvagedCount > 0)
            {
                ApplyFilter(); // salvaged items were only flipped in _allItems, not yet reflected in FileItems
                OnPropertyChanged(nameof(HasAnyDeletedItem));
                _notification.Show(LK.Common_SuccessSaveComplete, LK.Gallery_InfoAutoUndeletedDuplicate, NotificationSeverity.Info, TimeSpan.FromSeconds(3));
                WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
                NeedsReload = false;
            }

            if (successCount > 0)
            {
                NotifyCertExpiryChanged();
                _notification.Show(LK.Common_SuccessSaveComplete, string.Format(LocalizationManager.GetById(LK.Gallery_SuccessImportedCount), successCount, skipCount), NotificationSeverity.Success, TimeSpan.FromSeconds(3));
                WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
                NeedsReload = false; // This change is already reflected in the list
            }
            else if (skipCount > 0)
                _notification.Show(LK.Common_Warning, string.Format(LocalizationManager.GetById(LK.Gallery_InfoAlreadyRegistered), skipCount), NotificationSeverity.Info, TimeSpan.FromSeconds(3));

            if (unsupportedCount > 0)
                _notification.Show(LK.Common_Warning, string.Format(LocalizationManager.GetById(LK.Gallery_InfoUnsupportedSkipped), unsupportedCount), NotificationSeverity.Info, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            _logger.LogError("AddFilesAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

    private bool CanDeleteFile() => IsGalleryWriteEnabled && SelectedFile?.IsDeleted != true;
    private bool CanRestoreOrPurgeFile() => IsGalleryWriteEnabled && SelectedFile?.IsDeleted == true;

    /// <summary>Soft-deletes the selected file (moves it to the trash). Atomically severs
    /// SecretFileLinks/ProfileFileLinks (see StoredFileRepository.SoftDeleteAsync); the file body
    /// itself is untouched until PurgeSelectedAsync or the 30-day auto-purge.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteFile))]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedFile == null) return;

        // The confirmation dialog's whole point is to warn that secret/profile links will be
        // severed - skip it when there's nothing to sever (the dialog would otherwise warn about a
        // consequence that doesn't apply, for no benefit over the notification shown afterward).
        if (await _storedFiles.IsFileInUseAsync(SelectedFile.Id))
        {
            bool confirmed = await _dialog.ConfirmDestructiveAsync(
                LocalizationManager.Get("Common.DeleteConfirm"),
                LocalizationManager.Get("Gallery.Dialog.SoftDeleteConfirmText"),
                DeleteGlyph,
                LocalizationManager.Get("Common.Delete"),
                defaultToCancel: true);
            if (!confirmed) return;
        }

        var target = SelectedFile;
        IsBusy = true;
        try
        {
            await _storedFiles.SoftDeleteAsync(target.Id);
            try
            {
                _autoBackup.MarkContentChanged();
                await _auditLog.LogAsync(AuditEventCode.FileSoftDeleted, new FileSoftDeletedPayload(target.Id, target.FileName), _session.GetKey());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[DeleteSelectedAsync] Failed to log FileSoftDeleted. [{ExType}]", ex.GetType().Name);
            }
            target.IsDeleted = true;
            ApplyFilter();
            OnPropertyChanged(nameof(HasAnyDeletedItem));
            NotifyCertExpiryChanged();
            // A deleted file has every link severed already and no reason to still be open; leaving
            // its viewer open would let the user re-link it via the viewer's own right pane.
            _windowService.CloseViewer(target.Id);
            _notification.Show(LK.Common_InfoDeleteComplete, LK.Common_InfoDeleteComplete, NotificationSeverity.Info, TimeSpan.FromSeconds(3));
            WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
            NeedsReload = false; // This change is already reflected in the list
        }
        catch (Exception ex)
        {
            _logger.LogError("DeleteSelectedAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Restores a soft-deleted file. No confirmation dialog (matches TimeMachineViewModel.UndeleteAsync).
    /// Links are deliberately not restored - the file comes back unlinked (see StoredFileRepository.UndeleteAsync).</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreOrPurgeFile))]
    private async Task RestoreSelectedAsync()
    {
        if (SelectedFile == null) return;

        var target = SelectedFile;
        IsBusy = true;
        try
        {
            await _storedFiles.UndeleteAsync(target.Id);
            try
            {
                _autoBackup.MarkContentChanged();
                await _auditLog.LogAsync(AuditEventCode.FileUndeleted, new FileUndeletedPayload(target.Id, target.FileName), _session.GetKey());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[RestoreSelectedAsync] Failed to log FileUndeleted. [{ExType}]", ex.GetType().Name);
            }
            target.IsDeleted = false;
            ApplyFilter();
            OnPropertyChanged(nameof(HasAnyDeletedItem));
            if (FilterDeletedOnly && !HasAnyDeletedItem) FilterDeletedOnly = false;
            NotifyCertExpiryChanged();
            _notification.Show(LK.Gallery_SuccessFileUndeleted, LK.Gallery_SuccessFileUndeleted, NotificationSeverity.Success, TimeSpan.FromSeconds(3));
            WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
            NeedsReload = false;
        }
        catch (Exception ex)
        {
            _logger.LogError("RestoreSelectedAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Permanently deletes a soft-deleted file's body (irreversible). Enabled only from the
    /// trash view - matches TimeMachineViewModel.PermanentDeleteAsync's confirm-then-purge shape.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreOrPurgeFile))]
    private async Task PurgeSelectedAsync()
    {
        if (SelectedFile == null) return;

        bool confirmed = await _dialog.ConfirmDestructiveAsync(
            LocalizationManager.Get("Common.DeleteConfirm"),
            LocalizationManager.Get("Gallery.Dialog.PurgeFileConfirmText"),
            EraseToolGlyph,
            LocalizationManager.Get("Common.PurgePermanently"),
            defaultToCancel: true);
        if (!confirmed) return;

        int deletedIdx = FileItems.IndexOf(SelectedFile);
        var toDelete = SelectedFile;

        IsBusy = true;
        try
        {
            await _storedFiles.DeleteAsync(toDelete.Id);
            try
            {
                _autoBackup.MarkContentChanged();
                await _auditLog.LogAsync(AuditEventCode.FilePermanentlyDeleted, new FilePermanentlyDeletedPayload(toDelete.Id, toDelete.FileName), _session.GetKey());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[PurgeSelectedAsync] Failed to log FilePermanentlyDeleted. [{ExType}]", ex.GetType().Name);
            }
            _allItems.Remove(toDelete);
            FileItems.Remove(toDelete);
            SelectedFile = FileItems.Count > 0 ? FileItems[Math.Max(0, deletedIdx - 1)] : null;
            toDelete.Dispose(); // ZeroMemory the SecureCharBuffer + ThumbnailData
            // The file body no longer exists; a still-open viewer would fail on export or show stale data.
            _windowService.CloseViewer(toDelete.Id);
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasAnyDeletedItem));
            if (FilterDeletedOnly && !HasAnyDeletedItem) FilterDeletedOnly = false;
            NotifyCertExpiryChanged();
            _notification.Show(LK.Common_InfoDeleteComplete, LK.Common_InfoDeleteComplete, NotificationSeverity.Info, TimeSpan.FromSeconds(3));
            WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
            NeedsReload = false; // This change is already reflected in the list
        }
        catch (Exception ex)
        {
            _logger.LogError("PurgeSelectedAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsGalleryWriteEnabled))]
    private async Task SaveSelectedAsync()
    {
        if (SelectedFile == null) return;

        byte[]? decrypted = null;
        IsBusy = true;
        try
        {
            var img = await _storedFiles.GetByIdAsync(SelectedFile.Id, _session.GetKey());
            if (img?.OriginalBlob == null)
            {
                _notification.Show(LK.Common_Error, LK.Common_ErrorFileDataNotFound, NotificationSeverity.Error, TimeSpan.FromSeconds(4));
                return;
            }
            decrypted = _crypto.DecryptToPin(img.OriginalBlob, _session.GetKey().Span);
            if (decrypted == null)
            {
                _notification.Show(LK.Common_Error, LK.Common_ErrorDecryptionFailed, NotificationSeverity.Error, TimeSpan.FromSeconds(4));
                return;
            }

            // Path.GetExtension(ReadOnlySpan<char>) — no string is materialized
            var rawExt = Path.GetExtension(SelectedFile.FileNameSpan);
            (string, string)[] filters;
            if (rawExt.IsEmpty)
            {
                filters = [("*", "*")];
            }
            else
            {
                var extWithoutDot = rawExt.TrimStart('.').ToString(); // The extension string is a UI string for the OS file dialog (non-sensitive)
                filters = [(string.Format(LocalizationManager.Get("Common.FileFilter"), extWithoutDot.ToUpperInvariant(), extWithoutDot), $".{extWithoutDot}")];
            }

            // FileName's getter creates a transient string for XAML compatibility, but it is discarded after being used as the OS dialog's suggested name
            using var pathBuf = await _filePicker.SaveAsync(SelectedFile.FileName, filters);
            if (pathBuf == null) return;

            // File.WriteAllBytesAsync has no ReadOnlySpan<char> path overload in this SDK,
            // so a temporary method-scoped string is created. pathBuf.Dispose() (end of using) wipes the original SecureCharBuffer.
            // path is never stored in any field or long-lived variable (eligible for GC after the method returns).
            await File.WriteAllBytesAsync(new string(pathBuf.Span), decrypted);

            // The notification uses only the trailing filename portion of the path (excludes the OS username)
            _notification.Show(LK.Common_SuccessSaveComplete,
                string.Format(LocalizationManager.GetById(LK.Common_SuccessExportedToPath), Path.GetFileName(pathBuf.Span).ToString()),
                NotificationSeverity.Success, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError("SaveSelectedAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (decrypted != null) CryptographicOperations.ZeroMemory(decrypted.AsSpan());
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenViewer(FileItem? imageItem)
    {
        if (imageItem == null) return;
        try
        {
            await _auditLog.LogAsync(
                AuditEventCode.FileViewed,
                new FileViewedPayload(imageItem.Id, imageItem.FileName),
                _session.GetKey());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[OpenViewer] Failed to log FileViewed. [{ExType}]", ex.GetType().Name);
        }
        _windowService.OpenViewer(imageItem.Id);
    }

    // ── Gallery right pane: link loading ──────────────────────────────────────

    public async Task LoadLinksAsync(int fileId)
    {
        _linkLoadCts.Cancel();
        _linkLoadCts = new CancellationTokenSource();
        var ct = _linkLoadCts.Token;
        ClearLinks();

        try
        {
            ct.ThrowIfCancellationRequested();
            var dek = _session.GetKey();

            // ── Hybrid extraction of Gen0 (confirmed links) and the draft's links ──
            var gen0Ids = (await _storedFiles.GetSecretsByFileIdAsync(fileId))
                .Select(s => s.Id).ToHashSet();
            ct.ThrowIfCancellationRequested();

            var draftFileIds = await _secretDrafts.GetSecretIdsWithDraftContainingFileAsync(fileId, dek, ct);
            var allLinkedIds = gen0Ids.Union(draftFileIds).ToHashSet();
            ct.ThrowIfCancellationRequested();

            // Accurate IsDraftLink determination: combine draft presence with FileIds differences
            var allDraftIds = await _secretDrafts.GetAllDraftIdsAsync();
            ct.ThrowIfCancellationRequested();

            var allSecrets = await _secrets.GetAllAsync();
            ct.ThrowIfCancellationRequested();

            var secretItems = new List<GalleryLinkItem>(allLinkedIds.Count);
            var domainsByItem = new Dictionary<GalleryLinkItem, string?>();
            foreach (var s in allSecrets)
            {
                if (!allLinkedIds.Contains(s.Id)) continue;
                if (s.Title is not { Length: > 0 }) continue;
                byte[]? titleBytes = _crypto.DecryptToPin(s.Title, dek.Span);
                if (titleBytes == null) continue;
                bool slotHasFile = draftFileIds.Contains(s.Id);
                bool gen0HasFile  = gen0Ids.Contains(s.Id);
                bool isDraft = allDraftIds.Contains(s.Id) && (slotHasFile != gen0HasFile);
                GalleryLinkItem item;
                try   { item = new GalleryLinkItem(s.Id, titleBytes, isDraft); }
                finally { CryptographicOperations.ZeroMemory(titleBytes.AsSpan()); }
                secretItems.Add(item);

                if (s.Website is { Length: > 0 })
                {
                    using var wsPlain = FieldCrypto.Open(s.Website, _crypto, dek);
                    if (wsPlain != null) domainsByItem[item] = ExtractDomainFromUtf8(wsPlain.Utf8);
                }
            }
            ct.ThrowIfCancellationRequested();

            // Profile link state: mirrors ViewerViewModel.LoadLinksAsync exactly. The TwinB draft,
            // when present, is read directly from the DB and is the sole source of truth (not a
            // union with Gen0 - a union would keep showing "linked" after an unlink-via-draft, since
            // Gen0 still has the old link until Save is pressed). This intentionally does NOT use
            // _session.PendingProfileImageIds: that set is populated only once
            // ProfileViewModel.LoadAsync() has actually run this session, so a file pinned solely via
            // ViewerWindow's toggle (a draft-only write) silently failed to show here at all until the
            // user happened to visit the Profile page first.
            var profileIds = (await _storedFiles.GetProfileFileLinksAsync()).ToHashSet();
            ct.ThrowIfCancellationRequested();
            bool isProfileInGen0 = profileIds.Contains(fileId);

            var (_, twinBEnc) = await _profileService.GetCompareRawAsync();
            ct.ThrowIfCancellationRequested();
            bool hasProfileDraft  = twinBEnc != null;
            bool isProfileInDraft = false;
            if (hasProfileDraft)
            {
                using var twinBSnap = _profileService.DecryptSnapshot(twinBEnc!, dek);
                isProfileInDraft = twinBSnap?.FileIds?.Contains(fileId) ?? false;
            }
            bool isProfileLinked = hasProfileDraft ? isProfileInDraft : isProfileInGen0;
            bool isProfileDraft  = hasProfileDraft && (isProfileInDraft != isProfileInGen0);

            // All cancellation checkpoints have passed — commit atomically so a cancelled load
            // never leaves a partially-populated list visible (ClearLinks() already ran above).
            GalleryLinkItem? profileItem = null;
            if (isProfileLinked)
            {
                profileItem = new GalleryLinkItem(LocalizationManager.Get("Shell.Navi.Profile"), isProfileDraft);
                LinkItems.Add(profileItem);
            }
            foreach (var item in secretItems.OrderBy(x => x.DisplayTitle, StringComparer.CurrentCultureIgnoreCase))
                LinkItems.Add(item);

            // Icons load after the row text is already visible: the favicon fetch is a network call
            // and the avatar decode isn't needed for the list to be usable, so neither should block it.
            _ = LoadLinkIconsAsync(profileItem, domainsByItem, dek, ct);
        }
        catch (OperationCanceledException) { /* Normal cancellation */ }
        catch (Exception ex)
        {
            _logger.LogError("GalleryViewModel: failed to load links FileId={FileId} [{ExType}]", fileId, ex.GetType().Name);
        }
    }

    // domainsByItem is keyed by reference identity (GalleryLinkItem has no Equals/GetHashCode
    // override) - each row constructed in LoadLinksAsync is a distinct instance, which is exactly
    // what's wanted here.
    private async Task LoadLinkIconsAsync(GalleryLinkItem? profileItem, Dictionary<GalleryLinkItem, string?> domainsByItem, DekScope dek, CancellationToken ct)
    {
        try
        {
            if (profileItem != null)
            {
                // LoadCommittedRawAsync doesn't touch AppSession.AvatarBytes, so this buffer is
                // exclusively ours to wipe once the decode below is done with it.
                byte[]? avatarBytes = null;
                try
                {
                    avatarBytes = await _avatar.LoadCommittedRawAsync(dek);
                    ct.ThrowIfCancellationRequested();
                    if (avatarBytes is { Length: > 0 })
                        await SetIconOnUiAsync(profileItem, avatarBytes);
                }
                finally
                {
                    if (avatarBytes != null) CryptographicOperations.ZeroMemory(avatarBytes.AsSpan());
                }
            }

            if (_favicon.IsEnabled)
            {
                var distinctDomains = domainsByItem.Values
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var domain in distinctDomains)
                {
                    ct.ThrowIfCancellationRequested();
                    await _favicon.PrefetchAsync(domain!, dek);
                }
            }

            foreach (var (item, domain) in domainsByItem)
            {
                if (string.IsNullOrEmpty(domain)) continue;
                ct.ThrowIfCancellationRequested();
                var bytes = await _favicon.GetCachedAsync(domain, dek);
                if (bytes == null) continue;
                await SetIconOnUiAsync(item, bytes);
            }
        }
        catch (OperationCanceledException) { /* Superseded by a newer LoadLinksAsync call */ }
        // Invoked as fire-and-forget (`_ = LoadLinkIconsAsync(...)`) - any other exception here
        // must not become a silent UnobservedTaskException.
        catch (Exception ex)
        {
            _logger.LogWarning("GalleryViewModel: failed to load link icons. [{ExType}]", ex.GetType().Name);
        }
    }

    private Task SetIconOnUiAsync(GalleryLinkItem item, byte[] data)
        => _dispatcher.EnqueueAsync(async () =>
        {
            try
            {
                using var ms = new MemoryStream(data);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                item.IconSource = bmp;
            }
            catch { /* Ignore network unavailability / decode failures */ }
        });

    private static string? ExtractDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    // Extracts the domain by creating a temporary string from a UTF-8 byte span.
    // The created URL string becomes GC-eligible immediately after ExtractDomain returns (never persisted).
    private static string? ExtractDomainFromUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return null;
        int charCount = Encoding.UTF8.GetCharCount(utf8);
        if (charCount == 0) return null;
        if (charCount <= 512)
        {
            Span<char> chars = stackalloc char[charCount];
            Encoding.UTF8.GetChars(utf8, chars);
            return ExtractDomain(new string(chars));
        }
        return ExtractDomain(Encoding.UTF8.GetString(utf8));
    }

    public void ClearLinks()
    {
        foreach (var item in LinkItems) item.Dispose();
        LinkItems.Clear();
    }

    [RelayCommand]
    private void NavigateToLinkItem(GalleryLinkItem? item)
    {
        if (item == null) return;
        WeakReferenceMessenger.Default.Send(item.IsProfile
            ? new NavigateToTagMessage("profile")
            : new NavigateToTagMessage("secrets", item.SecretId));
    }

    private void NotifyCertExpiryChanged()
    {
        if (FilterExpiryOnly && !HasCertExpiryAlert)
            FilterExpiryOnly = false;
        OnPropertyChanged(nameof(HasCertExpiryAlert));
        OnPropertyChanged(nameof(HasCertExpiredAny));
        OnPropertyChanged(nameof(CertExpiryWarningText));
        OnPropertyChanged(nameof(HasAnyExpiryAlert));
        OnPropertyChanged(nameof(CertExpiryAlertCount));
    }

    /// <summary>
    /// Called when the scope is Disposed on lock.
    /// SelectedFile = null → OnSelectedFileChanged → ClearLinks() clears the link data.
    /// FileItem.Dispose() ZeroMemories the SecureCharBuffer (FileName) and the ThumbnailData pinned array.
    /// </summary>
    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _selectionDelayCts?.Cancel();
        _selectionDelayCts?.Dispose();
        _selectionDelayCts = null;
        _linkLoadCts.Cancel();
        _linkLoadCts.Dispose();
        SelectedFile = null;  // → OnSelectedFileChanged → ClearLinks()
        SearchQuery   = string.Empty;
        FileItems.Clear();
        foreach (var old in _allItems) old.Dispose();
        _allItems.Clear();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A row in the gallery's right-pane unified link list (read-only) — either the fixed
/// Profile row or a linked secret. Secret titles are stored in a SecureCharBuffer
/// (POH-pinned) and physically wiped on Dispose; the Profile row's label is a static
/// localized string (not secret data), so it needs no such handling.
/// </summary>
public sealed partial class GalleryLinkItem : ObservableObject, IDisposable
{
    public bool IsProfile { get; }
    public int SecretId { get; }

    // Unconfirmed link originating from the draft (true = shows pencil icon)
    public bool IsDraftLink { get; }

    private readonly SecureCharBuffer? _titleBuf;
    private readonly string? _staticTitle;

    // Favicon (regular secret) or the account avatar (Profile row) - loaded asynchronously after
    // construction, so it must be observable for the row's Image to pick it up once it arrives.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    [NotifyPropertyChangedFor(nameof(ShowSquareIcon))]
    [NotifyPropertyChangedFor(nameof(ShowRoundIcon))]
    public partial Microsoft.UI.Xaml.Media.Imaging.BitmapImage? IconSource { get; set; }
    public bool HasIcon => IconSource != null;

    // Favicon (secret rows) stays square; the Profile row's avatar is rounded to match
    // the ViewerWindow link pane's Profile row (Ellipse-clipped).
    public bool ShowSquareIcon => HasIcon && !IsProfile;
    public bool ShowRoundIcon => HasIcon && IsProfile;

    public GalleryLinkItem(int secretId, ReadOnlySpan<byte> utf8Title, bool isDraftLink)
    {
        IsProfile = false;
        SecretId = secretId;
        IsDraftLink = isDraftLink;
        _titleBuf = new SecureCharBuffer();
        FieldCrypto.FillBufferFromUtf8(_titleBuf, utf8Title);
    }

    public GalleryLinkItem(string staticTitle, bool isDraftLink)
    {
        IsProfile = true;
        IsDraftLink = isDraftLink;
        _staticTitle = staticTitle;
    }

    public string DisplayTitle => IsProfile ? _staticTitle! : _titleBuf!.ToDisplayString();

    public void Dispose() => _titleBuf?.Dispose();
}
