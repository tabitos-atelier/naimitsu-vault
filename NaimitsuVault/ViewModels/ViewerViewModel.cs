// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.Logging;
using Windows.UI;
using NaimitsuVault.Common;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using FC = NaimitsuVault.Common.FileTypeCode;

namespace NaimitsuVault.ViewModels;

public partial class ViewerViewModel : ObservableObject, IDisposable
{
    private readonly StoredFileRepository _storedFiles;
    private readonly SecretRepository _secrets;
    private readonly SecretDraftsRepository _secretDrafts;
    private readonly ICryptoService _crypto;
    private readonly AppSession _session;
    private readonly ProfileService _profileService;
    private readonly IAppNotificationService _notification;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<ViewerViewModel> _logger;
    private readonly FaviconService _favicon;
    private readonly AvatarService _avatar;
    private readonly IDispatcherService _dispatcher;

    // ── Fields for file display ──────────────────────────────────────────────

    [ObservableProperty] public partial byte[]? FileBytes { get; set; }
    [ObservableProperty] public partial bool IsImage { get; set; }
    [ObservableProperty] public partial bool IsPdf { get; set; }
    [ObservableProperty] public partial bool IsCert { get; set; }
    [ObservableProperty] public partial bool IsText { get; set; }
    [ObservableProperty] public partial bool IsJson { get; set; }
    [ObservableProperty] public partial bool IsXml    { get; set; }
    [ObservableProperty] public partial bool IsConfig { get; set; }
    [ObservableProperty] public partial bool IsMarkdown { get; set; }
    [ObservableProperty] public partial bool IsSql { get; set; }
    [ObservableProperty] public partial CertInfo? CertData { get; set; }
    [ObservableProperty] public partial ServiceAccountInfo? ServiceAccountData { get; set; }
    [ObservableProperty] public partial bool IsArchive { get; set; }
    [ObservableProperty] public partial bool IsQuarantined { get; set; }
    [ObservableProperty] public partial IReadOnlyList<ArchiveEntryInfo> ArchiveEntries { get; set; } = [];
    [ObservableProperty] public partial int ArchiveTotalFileCount { get; set; }
    [ObservableProperty] public partial string FileSizeDisplay { get; set; } = string.Empty;
    public int CurrentFileId { get; private set; }

    /// <summary>Whether the currently-loaded file is soft-deleted (in the Gallery trash). Set from
    /// StoredFile.DeletedAt in LoadFileAsync.</summary>
    [ObservableProperty] public partial bool IsCurrentFileDeleted { get; set; }

    /// <summary>Local-time "purged on {date}" message (DeletedAt + the retention period), shown in
    /// place of the link pane for a soft-deleted file. Empty when the file isn't deleted.</summary>
    [ObservableProperty] public partial string DeletedNoticeText { get; set; } = string.Empty;

    /// <summary>Hides the right pane's link-editing controls (profile pin, search, secret list)
    /// entirely rather than merely disabling them. A soft-deleted file already had every link
    /// severed by StoredFileRepository.SoftDeleteAsync, so re-linking it from the viewer would
    /// silently resurrect a link the user just chose to remove - and greyed-out controls with
    /// nothing left to act on read as broken UI rather than "this is intentional". WindowEx forbids
    /// x:Bind+Converter, so Visibility is computed here instead of in XAML.</summary>
    public Visibility LinkPaneVisibility => IsCurrentFileDeleted ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DeletedNoticeVisibility => IsCurrentFileDeleted ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsCurrentFileDeletedChanged(bool value)
    {
        OnPropertyChanged(nameof(LinkPaneVisibility));
        OnPropertyChanged(nameof(DeletedNoticeVisibility));
    }

    // Emergency access code recovery is always a restricted read-only session; exporting a decrypted
    // file to disk isn't a DB write, but it's still a data-exfiltration path the restricted mode should block.
    public bool CanExport => !_session.IsReadOnlyRestricted;

    private readonly SecureCharBuffer _textContentBuf = new();
    private readonly SecureCharBuffer _fileNameBuf    = new();

    public string FileName => _fileNameBuf.ToDisplayString();

    public string GetTextContent() => _textContentBuf.ToDisplayString();

    internal ReadOnlySpan<char> TextContentSpan => _textContentBuf.Span;

    // ── Right pane: link management (viewer-only, immediate toggle) ──────────────────────

    // Master list (not exposed to the UI)
    private readonly List<LinkableSecretItem> _linkAllItems = [];
    private readonly SemaphoreSlim _linkLock = new(1, 1);
    private CancellationTokenSource _linkLoadCts = new();

    // Profile row
    [ObservableProperty] public partial bool LinkIsProfileLinked { get; set; } = false;
    // true = dropped in the Profile screen via D&D but not yet saved (shows pencil icon)
    [ObservableProperty] public partial bool LinkIsProfileDraft { get; set; } = false;

    // WindowEx forbids x:Bind+Converter, so Visibility is computed in the ViewModel
    public Visibility LinkProfileDraftIconVisibility
        => LinkIsProfileDraft ? Visibility.Visible : Visibility.Collapsed;

    partial void OnLinkIsProfileDraftChanged(bool value)
        => OnPropertyChanged(nameof(LinkProfileDraftIconVisibility));

    // Committed avatar thumbnail shown next to the pin/unpin toggle - loaded asynchronously
    // (decode is a WinRT call), so it must be observable for the row's Image to pick it up.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkProfileAvatarVisibility))]
    public partial Microsoft.UI.Xaml.Media.Imaging.BitmapImage? LinkProfileAvatarSource { get; set; }
    public Visibility LinkProfileAvatarVisibility
        => LinkProfileAvatarSource != null ? Visibility.Visible : Visibility.Collapsed;

    // Discipline 2: zombie-VM suppression flag (ViewerWindow is always displayed, so IsActive matches the Window's lifetime)
    public bool IsActive { get; private set; }
    public void Resume() => IsActive = true;
    public void Pause()  => IsActive = false;

    // ─── Theme state (injected from the View's ActualThemeChanged; the ViewModel never determines it itself) ──
    // ViewerWindow.xaml.cs hooks root.ActualThemeChanged and sets IsLightTheme.
    [ObservableProperty] public partial bool IsLightTheme { get; set; } = false;

    partial void OnIsLightThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(LinkProfileForeground));
        OnPropertyChanged(nameof(LinkFilterToggleForeground));
    }

    private static readonly SolidColorBrush _fallbackAccent = new(Color.FromArgb(255, 0, 102, 204));
    // Ensures visibility on a light-mode white background. Dark colors like #444444 read as "black" — forbidden (a recurring known issue)
    private static readonly SolidColorBrush _fallbackGrey   = new(Color.FromArgb(255, 144, 144, 144));

    // WindowEx forbids x:Bind+Converter (SetConverterLookupRoot requires a FrameworkElement).
    // Keep Brush/string computation in the ViewModel and x:Bind without a converter.
    private Brush GetThemedBrush(bool active)
    {
        if (!active)
            return Application.Current.Resources.TryGetValue("NvInactiveBrush", out var b) && b is Brush br
                ? br : _fallbackGrey;
        // In light mode, reject the system accent (light blue) and return royal blue directly
        if (IsLightTheme)
            return _fallbackAccent;
        return Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var ab) && ab is Brush abr
            ? abr : _fallbackAccent;
    }

    public Brush LinkProfileForeground => GetThemedBrush(LinkIsProfileLinked);
    public string LinkProfileToggleTooltip => LocalizationManager.Get(LinkIsProfileLinked ? "Common.RemoveLink" : "Common.AddLink");
    // Linked: Pinned (E840)  /  Unlinked: Unpin (E77A)
    public string LinkProfileToggleGlyph => LinkIsProfileLinked ? "\uE840" : "\uE77A";

    partial void OnLinkIsProfileLinkedChanged(bool value)
    {
        OnPropertyChanged(nameof(LinkProfileForeground));
        OnPropertyChanged(nameof(LinkProfileToggleTooltip));
        OnPropertyChanged(nameof(LinkProfileToggleGlyph));
    }

    // Foreground based on the filter toggle state (WindowEx constraint: Converter forbidden, so computed in the ViewModel)
    public Brush LinkFilterToggleForeground => GetThemedBrush(LinkFilterLinkedOnly);
    // Both filter ON/OFF states use the same pin icon (E840)
    public string LinkFilterToggleGlyph => "\uE840";

    // Shows only the linked count (no denominator needed)
    public string LinkLinkedCountDisplay => $"{_linkAllItems.Count(x => x.IsLinked)}";

    // Filter toggle command
    [RelayCommand]
    private void ToggleLinkedFilter() => LinkFilterLinkedOnly = !LinkFilterLinkedOnly;

    // Filter state (partial methods trigger ApplyLinkFilter)
    [ObservableProperty] public partial string LinkSearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial int? LinkFilterCategory { get; set; } = null;
    [ObservableProperty] public partial bool LinkFilterLinkedOnly { get; set; } = false;

    // Display list (XAML binding target)
    public ObservableCollection<LinkableSecretItem> LinkFilteredItems { get; } = [];

    // Selected row (for J/K navigation, Ctrl+5 toggle)
    [ObservableProperty] public partial LinkableSecretItem? LinkSelectedItem { get; set; } = null;

    // File date/time display (set in LoadFileAsync)
    [ObservableProperty] public partial string LinkCreatedAtDisplay { get; set; } = string.Empty;
    [ObservableProperty] public partial string LinkFileModifiedAtDisplay { get; set; } = string.Empty;

    // Filter change → auto re-filter
    partial void OnLinkSearchTextChanged(string value) => ApplyLinkFilter();
    partial void OnLinkFilterCategoryChanged(int? value) => ApplyLinkFilter();
    partial void OnLinkFilterLinkedOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(LinkFilterToggleForeground));
        OnPropertyChanged(nameof(LinkFilterToggleGlyph));
        OnPropertyChanged(nameof(LinkLinkedCountDisplay));
        ApplyLinkFilter();
    }

    // ── DI ──────────────────────────────────────────────────────────────────

    public ViewerViewModel(
        StoredFileRepository storedFiles,
        SecretRepository secretRepository,
        SecretDraftsRepository secretDrafts,
        ICryptoService cryptoService,
        AppSession appSession,
        ProfileService profileService,
        IAppNotificationService notification,
        IAuditLogService auditLog,
        ILogger<ViewerViewModel> logger,
        FaviconService favicon,
        AvatarService avatar,
        IDispatcherService dispatcher)
    {
        _storedFiles = storedFiles;
        _secrets = secretRepository;
        _secretDrafts = secretDrafts;
        _crypto = cryptoService;
        _session = appSession;
        _profileService = profileService;
        _notification = notification;
        _auditLog = auditLog;
        _logger = logger;
        _favicon = favicon;
        _avatar = avatar;
        _dispatcher = dispatcher;

        // Reflects D&D or link changes from other pages (secrets, profile) in real time.
        // Fire-and-forget: stashed in _pendingSelfReloadTask so tests can await it deterministically
        // instead of polling (this also fires for the viewer's own toggle commands, since they send
        // StorageChangedMessage themselves and this same VM is still an active recipient).
        WeakReferenceMessenger.Default.Register<StorageChangedMessage>(this, (_, _) =>
        {
            if (!IsActive) return;
            if (CurrentFileId > 0)
                _pendingSelfReloadTask = LoadLinksAsync(CurrentFileId);
        });
    }

    private Task? _pendingSelfReloadTask;

    /// <summary>
    /// Test-only hook: awaits the reload triggered by this viewer's own StorageChangedMessage
    /// handler, if one is currently pending. Task.CompletedTask if none was triggered.
    /// </summary>
    internal Task WaitForPendingSelfReloadAsync() => _pendingSelfReloadTask ?? Task.CompletedTask;

    // ── File loading ──────────────────────────────────────────────────────

    public async Task LoadFileAsync(int fileId, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var file = await _storedFiles.GetByIdAsync(fileId, _session.GetKey());
            if (file == null)
            {
                _logger.LogWarning("File ID {FileId} not found.", fileId);
                return;
            }

            CurrentFileId = fileId;
            IsCurrentFileDeleted = file.DeletedAt.HasValue;
            DeletedNoticeText = file.DeletedAt.HasValue
                ? string.Format(
                    LocalizationManager.Get("Gallery.DeletedItemPurgeNotice"),
                    file.DeletedAt.Value.AddDays(AppConstants.DeletedSecretRetentionDays).ToLocalTime().ToString("yyyy/MM/dd HH:mm"))
                : string.Empty;

            _fileNameBuf.Dispose();
            if (file.FileName != null)
            {
                FieldCrypto.FillBufferFromUtf8(_fileNameBuf, file.FileName);
                CryptographicOperations.ZeroMemory(file.FileName);
            }
            OnPropertyChanged(nameof(FileName));

            FileSizeDisplay = FormatFileSize(file.FileSize);
            var localCreatedAt       = file.CreatedAt.ToLocalTime();
            var localModifiedAt     = file.FileModifiedAt.ToLocalTime();
            LinkCreatedAtDisplay      = localCreatedAt.Year > 1 ? localCreatedAt.ToString("yyyy/MM/dd HH:mm") : string.Empty;
            LinkFileModifiedAtDisplay = localModifiedAt.Year > 1 ? localModifiedAt.ToString("yyyy/MM/dd HH:mm") : string.Empty;
            IsImage    = false;
            IsPdf      = false;
            IsCert     = false;
            IsText     = false;
            IsJson     = false;
            IsXml      = false;
            IsConfig   = false;
            IsMarkdown = false;
            IsSql      = false;
            IsArchive  = false;
            CertData           = null;
            ServiceAccountData = null;
            ArchiveEntries     = [];
            ArchiveTotalFileCount = 0;
            _textContentBuf.Dispose();

            // ContentType/FileName-extension mismatch (suspected tampering or corruption): refuse to interpret
            // OriginalBlob using an untrusted ContentType. The bytes themselves already passed AEAD authentication,
            // so this only blocks type-specific rendering (image/cert/pdf/... parsing), not the raw file's integrity.
            IsQuarantined = file.IsQuarantined;
            if (IsQuarantined)
            {
                // Defensive: clear any stale bytes from a previously displayed file in this same window.
                var stale = FileBytes;
                FileBytes = null;
                if (stale != null) CryptographicOperations.ZeroMemory(stale.AsSpan());
                return;
            }

            if (file.OriginalBlob != null)
            {
                ct.ThrowIfCancellationRequested();

                var dek = _session.GetKey();
                byte[]? decryptedBytes = _crypto.DecryptToPin(file.OriginalBlob, dek.Span);
                if (decryptedBytes != null && decryptedBytes.Length > 0)
                {
                    bool transferredOwnership = false;
                    try
                    {
                        ct.ThrowIfCancellationRequested();

                        var contentType = file.ContentTypeCode;
                        if (FC.IsPdf(contentType))
                        {
                            IsPdf = true;
                            FileBytes = decryptedBytes;
                            transferredOwnership = true;
                        }
                        else if (FC.IsImage(contentType))
                        {
                            IsImage = true;
                            FileBytes = decryptedBytes;
                            transferredOwnership = true;
                        }
                        else
                        {
                            // A .key extension can be either PEM (text) or binary DER format, so
                            // rather than trusting the IsTextType determination based on the extension
                            // alone, treat the content as text only after verifying the actual data is
                            // valid UTF-8 (otherwise fall back to binary instead of showing mojibake;
                            // binary is not previewable).
                            if (FC.IsTextType(contentType) && System.Text.Unicode.Utf8.IsValid(decryptedBytes))
                            {
                                IsText = true;
                                if (contentType == FC.Json)
                                {
                                    IsJson = true;
                                    CertificateHelper.PrettyPrintJsonToBuffer(decryptedBytes, _textContentBuf);
                                }
                                else
                                {
                                    if (contentType == FC.Xml)        IsXml      = true;
                                    else if (FC.IsConfigType(contentType)) IsConfig = true;
                                    else if (contentType == FC.Markdown)  IsMarkdown = true;
                                    else if (contentType == FC.Sql)       IsSql      = true;

                                    FieldCrypto.FillBufferFromUtf8(_textContentBuf, decryptedBytes);
                                }
                            }
                            if (contentType == FC.Json)
                            {
                                ServiceAccountData = CertificateHelper.TryParseServiceAccount(decryptedBytes);
                            }
                            else if (contentType == FC.Zip)
                            {
                                var result = ArchiveHelper.TryListZipEntries(decryptedBytes);
                                if (result != null)
                                {
                                    IsArchive             = true;
                                    ArchiveEntries        = result.Entries;
                                    ArchiveTotalFileCount = result.TotalFileCount;
                                }
                            }
                            else
                            {
                                var certInfo = CertificateHelper.TryParse(decryptedBytes, contentType);
                                if (certInfo != null)
                                {
                                    IsCert = true;
                                    CertData = certInfo;
                                }
                            }
                            FileBytes = decryptedBytes;
                            transferredOwnership = true;
                        }
                    }
                    finally
                    {
                        if (!transferredOwnership)
                            CryptographicOperations.ZeroMemory(decryptedBytes.AsSpan());
                    }
                }
            }

            await LoadLinksAsync(fileId);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation. The decrypted bytes were already ZeroMemoried in the finally block above.
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to load file ID {FileId}. [{ExType}]", fileId, ex.GetType().Name);
            ClearSecretData();
        }
    }

    private static string FormatFileSize(long bytes) =>
        bytes switch
        {
            >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
            >= 1024         => $"{bytes / 1024.0:F1} KB",
            _               => $"{bytes} B"
        };

    public void ClearSecretData()
    {
        var bytes = FileBytes;
        FileBytes = null;
        if (bytes != null) CryptographicOperations.ZeroMemory(bytes.AsSpan());
        _textContentBuf.Dispose();
        _fileNameBuf.Dispose();
        OnPropertyChanged(nameof(FileName));
        CertData           = null;
        ServiceAccountData = null;
        IsArchive          = false;
        ArchiveEntries     = [];
        ArchiveTotalFileCount = 0;
    }

    // ── Link management ────────────────────────────────────────────────────────────

    private async Task LoadLinksAsync(int fileId)
    {
        _linkLoadCts.Cancel();
        _linkLoadCts = new CancellationTokenSource();
        var ct = _linkLoadCts.Token;

        // Warning: stash the selected ID before clearing (do not remove — a recurring known issue).
        // Since ClearLinksAsync sets LinkSelectedItem to null, reloading without stashing it first
        // causes the list to scroll back to the top after operations like Ctrl+5 (link toggle).
        // ViewerWindow.ViewModel_PropertyChanged is responsible for the ScrollIntoView after restoring it.
        int? prevSelectedId = LinkSelectedItem?.Id;

        await ClearLinksAsync();

        try
        {
            ct.ThrowIfCancellationRequested();
            var dek = _session.GetKey();

            // Hybrid extraction of Gen0 (confirmed links) and the draft's links
            var gen0Ids = (await _storedFiles.GetSecretsByFileIdAsync(fileId))
                .Select(s => s.Id).ToHashSet();
            ct.ThrowIfCancellationRequested();

            var draftFileIds = await _secretDrafts.GetSecretIdsWithDraftContainingFileAsync(fileId, dek, ct);
            ct.ThrowIfCancellationRequested();

            // Accurate IsDraftLink determination: combine draft presence with FileIds differences
            // Show the pencil only when Gen0's and the draft's FileIds differ (a match means a stable state)
            var allDraftIds = await _secretDrafts.GetAllDraftIdsAsync();
            ct.ThrowIfCancellationRequested();

            var allSecrets = await _secrets.GetAllAsync();
            ct.ThrowIfCancellationRequested();

            foreach (var s in allSecrets)
            {
                // Store all secrets in _linkAllItems and control display state via the IsLinked flag.
                // Do not add a filter here (the design stores all secrets in _linkAllItems and controls display via the IsLinked flag).
                if (s.Title is not { Length: > 0 }) continue;
                byte[]? titleBytes = _crypto.DecryptToPin(s.Title, dek.Span);
                if (titleBytes == null) continue;
                // isDraft: a draft exists, and "presence in the draft's FileIds" differs from "presence in Gen0's FileIds"
                // → being added (in the draft but not Gen0) or being removed (in Gen0 but not the draft)
                bool hasDraft    = allDraftIds.Contains(s.Id);
                bool slotHasFile = draftFileIds.Contains(s.Id);
                bool gen0HasFile = gen0Ids.Contains(s.Id);
                bool isDraft  = hasDraft && (slotHasFile != gen0HasFile);
                // When a draft exists, it is the source of truth for IsLinked (not a union with
                // Gen0) - otherwise removing a link via the draft still shows as linked because Gen0
                // still has it, snapping the pin back to "linked" on the very next reload.
                bool isLinked = hasDraft ? slotHasFile : gen0HasFile;
                string? domain = null;
                if (s.Website is { Length: > 0 })
                {
                    using var wsPlain = FieldCrypto.Open(s.Website, _crypto, dek);
                    if (wsPlain != null) domain = ExtractDomainFromUtf8(wsPlain.Utf8);
                }
                try   { _linkAllItems.Add(new LinkableSecretItem(s.Id, s.CategoryNum, titleBytes, isLinked, isDraft, domain)); }
                finally { CryptographicOperations.ZeroMemory(titleBytes.AsSpan()); }
            }

            _linkAllItems.Sort((a, b) =>
                StringComparer.CurrentCultureIgnoreCase.Compare(
                    a.GetOrCreateDisplayTitle(), b.GetOrCreateDisplayTitle()));

            ct.ThrowIfCancellationRequested();
            var profileIds = (await _storedFiles.GetProfileFileLinksAsync()).ToHashSet();
            ct.ThrowIfCancellationRequested();
            bool isProfileInTwinA = profileIds.Contains(fileId);

            // When a TwinB draft exists, it is the source of truth for LinkIsProfileLinked (not a
            // union with TwinA) - otherwise unlinking via the draft still shows as linked because
            // TwinA still has it, snapping the pin back to "linked" on the very next reload (the
            // same class of bug fixed for secrets' draft-vs-Gen0 union above).
            var (_, twinBEnc) = await _profileService.GetCompareRawAsync();
            ct.ThrowIfCancellationRequested();
            bool hasProfileDraft  = twinBEnc != null;
            bool isProfileInDraft = false;
            if (hasProfileDraft)
            {
                using var twinBSnap = _profileService.DecryptSnapshot(twinBEnc!, dek);
                isProfileInDraft = twinBSnap?.FileIds?.Contains(fileId) ?? false;
            }
            LinkIsProfileLinked = hasProfileDraft ? isProfileInDraft : isProfileInTwinA;
            LinkIsProfileDraft  = hasProfileDraft && (isProfileInDraft != isProfileInTwinA);

            ApplyLinkFilter();

            if (prevSelectedId.HasValue)
            {
                var restored = LinkFilteredItems.FirstOrDefault(i => i.Id == prevSelectedId.Value);
                if (restored != null) LinkSelectedItem = restored;
            }

            // Loads after the row text is already visible - the favicon fetch is a network call and
            // must not block the list from appearing. Covers the whole _linkAllItems set (not just
            // LinkFilteredItems) so toggling the "linked only" filter never needs a re-fetch.
            _ = LoadLinkFaviconsAsync(dek, ct);

            // Doesn't depend on fileId (it's always the same committed avatar), but reloading it here
            // keeps it in sync with any avatar change made elsewhere without a dedicated message listener.
            _ = LoadProfileAvatarAsync(dek, ct);
        }
        catch (OperationCanceledException) { /* Normal cancellation */ }
        catch (Exception ex)
        {
            _logger.LogError("ViewerViewModel: failed to load links FileId={FileId} [{ExType}]", fileId, ex.GetType().Name);
        }
    }

    private async Task LoadLinkFaviconsAsync(DekScope dek, CancellationToken ct)
    {
        try
        {
            // Snapshot before the first await: a newer LoadLinksAsync call can Clear() the live
            // _linkAllItems concurrently (this task only observes ct between awaits, not mid-await),
            // and enumerating a List<T> that was Clear()'d out from under it throws
            // InvalidOperationException. Mirrors SecretsViewModel.PrefetchAllFaviconsAsync's
            // _searchIndex.ToList() snapshot for the same reason.
            var items = _linkAllItems.ToList();

            if (_favicon.IsEnabled)
            {
                var distinctDomains = items
                    .Select(i => i.WebsiteDomain)
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var domain in distinctDomains)
                {
                    ct.ThrowIfCancellationRequested();
                    await _favicon.PrefetchAsync(domain!, dek);
                }
            }

            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.WebsiteDomain)) continue;
                ct.ThrowIfCancellationRequested();
                var bytes = await _favicon.GetCachedAsync(item.WebsiteDomain, dek);
                if (bytes == null) continue;
                await SetFaviconOnUiAsync(item, bytes);
            }
        }
        catch (OperationCanceledException) { /* Superseded by a newer LoadLinksAsync call */ }
        // Invoked as fire-and-forget (`_ = LoadLinkFaviconsAsync(...)`). A lock landing mid-load (e.g. while
        // waiting on a favicon HTTP fetch) makes the next DB access throw InvalidOperationException
        // ("No active vault DB"); log it rather than let it become a silent UnobservedTaskException.
        catch (Exception ex)
        {
            _logger.LogWarning("ViewerViewModel: failed to load link favicons. [{ExType}]", ex.GetType().Name);
        }
    }

    private async Task LoadProfileAvatarAsync(DekScope dek, CancellationToken ct)
    {
        // LoadCommittedRawAsync doesn't touch AppSession.AvatarBytes, so this buffer is exclusively
        // ours to wipe once the decode below is done with it.
        byte[]? avatarBytes = null;
        try
        {
            avatarBytes = await _avatar.LoadCommittedRawAsync(dek);
            ct.ThrowIfCancellationRequested();
            if (avatarBytes is not { Length: > 0 })
            {
                await _dispatcher.EnqueueAsync(() => { LinkProfileAvatarSource = null; return Task.CompletedTask; });
                return;
            }
            await _dispatcher.EnqueueAsync(async () =>
            {
                try
                {
                    using var ms = new MemoryStream(avatarBytes);
                    var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                    LinkProfileAvatarSource = bmp;
                }
                catch { /* Ignore decode failures */ }
            });
        }
        catch (OperationCanceledException) { /* Superseded by a newer LoadLinksAsync call */ }
        // Invoked as fire-and-forget (`_ = LoadProfileAvatarAsync(...)`); same lock-mid-load race as
        // LoadLinkFaviconsAsync above - log instead of a silent UnobservedTaskException.
        catch (Exception ex)
        {
            _logger.LogWarning("ViewerViewModel: failed to load profile avatar. [{ExType}]", ex.GetType().Name);
        }
        finally
        {
            if (avatarBytes != null) CryptographicOperations.ZeroMemory(avatarBytes.AsSpan());
        }
    }

    private Task SetFaviconOnUiAsync(LinkableSecretItem item, byte[] data)
        => _dispatcher.EnqueueAsync(async () =>
        {
            try
            {
                using var ms = new MemoryStream(data);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                item.FaviconSource = bmp;
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

    [RelayCommand]
    private async Task ToggleLinkAsync(LinkableSecretItem item, CancellationToken ct = default)
    {
        // UI hides these controls via LinkPaneVisibility, but this guard is kept here too as defense
        // against direct command invocation (same pattern as GalleryViewModel's write commands).
        if (_session.IsReadOnlyRestricted || IsCurrentFileDeleted) return;
        await _linkLock.WaitAsync(ct);
        HistorySlotContent? slot = null;
        byte[]? firstPlain = null;
        bool succeeded = false;
        try
        {
            var dek = _session.GetKey();

            // ── Fetch the draft; if absent, synthesize an initial snapshot from the Gen0 entity ──
            var (draftEnc, _) = await _secretDrafts.GetDraftAsync(item.Id);
            if (draftEnc != null)
            {
                firstPlain = _crypto.DecryptToPin(draftEnc, dek.Span);
                if (firstPlain == null)
                {
                    // A corrupted/undecryptable existing draft silently blocked every future toggle
                    // for this secret with no error and nothing in the log. Surface it instead.
                    _logger.LogError("[ToggleLinkAsync] Failed to decrypt existing draft for SecretId={SecretId}.", item.Id);
                    _notification.Show(
                        LK.Common_GeneralError, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(3));
                    return;
                }
                slot = SnapshotSerializer.ReadToSlot(firstPlain.AsSpan());
            }
            else
            {
                var secret = await _secrets.GetByIdAsync(item.Id);
                if (secret == null) return;
                var gen0FileIds = (await _secrets.GetFileLinksAsync(item.Id)).ToArray();
                using var buf0 = new PinnedBufferWriter();
                using (var w0 = new Utf8JsonWriter(buf0))
                    SnapshotSerializer.WriteEntity(w0, secret, _crypto, dek, gen0FileIds);
                slot = SnapshotSerializer.ReadToSlot(buf0.WrittenSpan);
            }

            // ── Edit FileIds ──
            var ids = (slot.FileIds ?? []).ToHashSet();
            if (item.IsLinked) ids.Remove(CurrentFileId);
            else               ids.Add(CurrentFileId);
            slot.FileIds = [..ids];

            // ── Re-encrypt the edited snapshot and save it to the draft ──
            using var buf1 = new PinnedBufferWriter();
            using (var w1 = new Utf8JsonWriter(buf1))
                SnapshotSerializer.WriteFromSlot(w1, slot);
            byte[] encBlob = _crypto.Encrypt(buf1.WrittenSpan, dek.Span);
            await _secretDrafts.SaveDraftAsync(item.Id, encBlob, DateTime.UtcNow);

            // ── Update UI state in-place (do not call ApplyLinkFilter() here) ────────────
            // Warning: calling ApplyLinkFilter() here runs LinkFilteredItems.Clear(), which causes
            //    the TwoWay binding to write LinkSelectedItem back to null.
            //    If LoadLinksAsync then runs with prevSelectedId=null, the selection cannot be
            //    restored, and the list scrolls back to the top (a recurring known issue).
            //    As with secrets and Time Machine, rewrite the item directly and leave list
            //    reconstruction to StorageChangedMessage → LoadLinksAsync.
            item.IsLinked = !item.IsLinked;

            // Re-evaluate IsDraftLink: is there a difference from Gen0 (SecretFileLinks)?
            var gen0Ids = (await _secrets.GetFileLinksAsync(item.Id)).ToHashSet();
            bool gen0HasFile = gen0Ids.Contains(CurrentFileId);
            item.IsDraftLink = item.IsLinked != gen0HasFile;

            // Only update the count display immediately (substitute for ApplyLinkFilter)
            OnPropertyChanged(nameof(LinkLinkedCountDisplay));

            succeeded = true; // Send happens after _linkLock.Release() (avoids deadlock)
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("[ToggleLinkAsync] Failed for SecretId={SecretId}. [{ExType}]", item.Id, ex.GetType().Name);
            _notification.Show(
                LK.Common_GeneralError, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (firstPlain != null) CryptographicOperations.ZeroMemory(firstPlain.AsSpan());
            slot?.Dispose();
            _linkLock.Release(); // Release the lock first
        }
        // Send after releasing the lock: avoids a deadlock from the viewer's own
        // StorageChangedMessage handler chaining LoadLinksAsync → ClearLinksAsync → _linkLock.WaitAsync()
        if (succeeded)
        {
            // Lets SecretsViewModel refresh EditingSecret.AttachedFiles immediately if the
            // toggled secret happens to be open for editing (otherwise it stays stale until
            // the page is reloaded).
            WeakReferenceMessenger.Default.Send(new FileLinkChangedMessage(CurrentFileId));
            WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
        }
    }

    [RelayCommand]
    private async Task ToggleProfileLinkAsync(CancellationToken ct = default)
    {
        if (_session.IsReadOnlyRestricted || IsCurrentFileDeleted) return;
        await _linkLock.WaitAsync(ct);
        bool succeeded = false;
        try
        {
            var dek = _session.GetKey();

            // ── Get SecureProfileSnapshot, preferring TwinB over TwinA ──
            var (twinAEnc, twinBEnc) = await _profileService.GetCompareRawAsync();
            var baseEnc = twinBEnc ?? twinAEnc;
            if (baseEnc == null) return;

            // Do the FileIds extraction, model construction, and save all within a using scope, wiping PII immediately
            ProfileEditModel model;
            List<int> updatedFileIds;
            using (var snapshot = _profileService.DecryptSnapshot(baseEnc, dek))
            {
                if (snapshot == null) return;

                // ── Edit FileIds ──
                var ids = (snapshot.FileIds ?? []).ToHashSet();
                if (LinkIsProfileLinked) ids.Remove(CurrentFileId);
                else                     ids.Add(CurrentFileId);
                updatedFileIds = ids.ToList();

                // Convert to EditModel (char[] → string occurs here, but stays contained within the using scope)
                model = _profileService.BuildEditModelFromSnapshot(snapshot);
                model.FileIds = updatedFileIds;
            } // snapshot.Dispose() → ZeroMemory the PII char[]

            // ── Save the edited EditModel to TwinB ──
            try
            {
                await _profileService.SaveDraftAsync(model, dek);
            }
            finally
            {
                model.ZeroPii(); // model's PII strings were decrypted solely to attach FileIds and re-encrypt; no longer needed - must run even if SaveDraftAsync throws
            }

            // ── Sync PendingProfileImageIds in memory ──
            if (!LinkIsProfileLinked) _session.PendingProfileImageIds.Add(CurrentFileId);
            else                      _session.PendingProfileImageIds.Remove(CurrentFileId);

            LinkIsProfileLinked = !LinkIsProfileLinked;

            // ── Re-evaluate IsDraftLink: is there a difference from TwinA's FileIds? ──
            bool twinAHasLink;
            using (var twinASnap = twinAEnc != null ? _profileService.DecryptSnapshot(twinAEnc, dek) : null)
                twinAHasLink = twinASnap?.FileIds?.Contains(CurrentFileId) ?? false;
            LinkIsProfileDraft = LinkIsProfileLinked != twinAHasLink;

            succeeded = true; // Send happens after _linkLock.Release() (avoids deadlock)
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("[ToggleProfileLinkAsync] Failed for FileId={FileId}. [{ExType}]", CurrentFileId, ex.GetType().Name);
            _notification.Show(
                LK.Common_GeneralError, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(3));
        }
        finally
        {
            _linkLock.Release(); // Release the lock first
        }
        // Send after releasing the lock: avoids a deadlock from the viewer's own
        // StorageChangedMessage handler chaining LoadLinksAsync → ClearLinksAsync → _linkLock.WaitAsync()
        if (succeeded)
            WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
    }

    private void ApplyLinkFilter()
    {
        var keyword = LinkSearchText.AsSpan();
        LinkFilteredItems.Clear();
        foreach (var item in _linkAllItems)
        {
            if (!keyword.IsEmpty &&
                !MemoryExtensions.Contains(item.TitleSpan, keyword, StringComparison.CurrentCultureIgnoreCase))
                continue;
            if (LinkFilterCategory.HasValue && item.CategoryNum != LinkFilterCategory)
                continue;
            if (LinkFilterLinkedOnly && !item.IsLinked)
                continue;
            LinkFilteredItems.Add(item);
        }
        // Reflected immediately for both toggle and filter changes
        OnPropertyChanged(nameof(LinkLinkedCountDisplay));
    }

    // CRITICAL: Deadlock risk. Do NOT call ClearLinksAsync() from Dispose().
    // Dispose must execute synchronously using _linkLock.Wait().
    // Call ClearLinksAsync only from LoadLinksAsync (Cancel is the caller's responsibility).
    public async Task ClearLinksAsync()
    {
        await _linkLock.WaitAsync();
        try
        {
            LinkFilteredItems.Clear();
            foreach (var item in _linkAllItems) item.Dispose();
            _linkAllItems.Clear();
            LinkIsProfileLinked = false;
            LinkIsProfileDraft  = false;
            LinkSearchText = string.Empty;
            LinkFilterCategory = null;
            LinkFilterLinkedOnly = false;
            LinkSelectedItem = null;
        }
        finally { _linkLock.Release(); }
    }

    // ── Export ───────────────────────────────────────────────────────────────

    /// <summary>Called by ViewerWindow's Export_Click code-behind after the save picker succeeds.</summary>
    public async Task LogFileExportedAsync()
    {
        try
        {
            await _auditLog.LogAsync(AuditEventCode.FileExported, new FileExportedPayload(CurrentFileId, FileName), _session.GetKey());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Export_Click] Failed to log FileExported. [{ExType}]", ex.GetType().Name);
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        ClearSecretData();

        // CRITICAL: Deadlock risk. Do NOT call ClearLinksAsync() from Dispose().
        // Dispose must execute synchronously using _linkLock.Wait().
        _linkLoadCts.Cancel();
        _linkLoadCts.Dispose();
        bool acquired = _linkLock.Wait(TimeSpan.FromSeconds(2));
        try
        {
            LinkFilteredItems.Clear();
            foreach (var item in _linkAllItems) item.Dispose();
            _linkAllItems.Clear();
        }
        finally
        {
            if (acquired) _linkLock.Release();
            _linkLock.Dispose();
        }
        GC.SuppressFinalize(this);
    }

} // end ViewerViewModel

/// <summary>A link row item in the viewer's right pane. Only IsLinked is mutable; an ObservableObject.</summary>
public sealed partial class LinkableSecretItem : ObservableObject, IDisposable
{
    public int Id { get; }
    public int? CategoryNum { get; }
    public string? WebsiteDomain { get; }

    // Unconfirmed link originating from the draft (true = shows pencil icon). Needs a setter too, since it is re-evaluated after toggling.
    public bool IsDraftLink { get; set; }

    private readonly SecureCharBuffer _titleBuf = new();

    [ObservableProperty] public partial bool IsLinked { get; set; }

    // Loaded asynchronously after construction (favicon fetch is a network call), so it must be
    // observable for the row's Image to pick it up once it arrives.
    // Exposes Visibility directly rather than a bool + BoolToVisibilityConverter: ViewerWindow is a
    // WindowEx, and x:Bind's generated SetConverterLookupRoot(this) requires a FrameworkElement, so
    // x:Bind+Converter throws CS1503 anywhere in this file's XAML, including inside a DataTemplate.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FaviconVisibility))]
    public partial Microsoft.UI.Xaml.Media.Imaging.BitmapImage? FaviconSource { get; set; }
    public Visibility FaviconVisibility => FaviconSource != null ? Visibility.Visible : Visibility.Collapsed;

    // Linked: Pinned (E840)  /  Unlinked: Unpin (E77A)
    public string ToggleGlyph   => IsLinked ? "\uE840" : "\uE77A";
    public string ToggleTooltip => LocalizationManager.Get(IsLinked ? "Common.RemoveLink" : "Common.AddLink");

    partial void OnIsLinkedChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleGlyph));
        OnPropertyChanged(nameof(ToggleTooltip));
    }

    public LinkableSecretItem(int id, int? categoryNum, ReadOnlySpan<byte> utf8Title, bool isLinked, bool isDraftLink = false, string? webSiteDomain = null)
    {
        Id = id;
        CategoryNum = categoryNum;
        IsLinked = isLinked;
        IsDraftLink = isDraftLink;
        WebsiteDomain = webSiteDomain;
        FieldCrypto.FillBufferFromUtf8(_titleBuf, utf8Title);
    }

    public string GetOrCreateDisplayTitle() => _titleBuf.ToDisplayString();
    public ReadOnlySpan<char> TitleSpan => _titleBuf.Span;

    public void Dispose()
    {
        _titleBuf.Dispose();
        GC.SuppressFinalize(this);
    }
}
