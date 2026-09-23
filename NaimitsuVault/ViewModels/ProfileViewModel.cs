// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using NaimitsuVault.Common;
using NaimitsuVault.Helpers;
using FC = NaimitsuVault.Common.FileTypeCode;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using Windows.Storage.Streams;
using Windows.System;

namespace NaimitsuVault.ViewModels;

public partial class ProfileViewModel : ObservableObject, IDisposable,
    NaimitsuVault.Services.Interfaces.ISessionSaveTracker
{
    private readonly ProfileService _profileService;
    private readonly IAppNotificationService _notification;
    private readonly StoredFileRepository _storedFiles;
    private readonly ICryptoService _crypto;
    private readonly AppSession _session;
    private readonly IWindowService _windowService;
    private readonly AvatarService _avatarService;
    private readonly IAuditLogService _auditLog;
    private readonly AutoBackupService _autoBackup;
    private readonly ILogger<ProfileViewModel> _logger;
    private readonly NaimitsuVault.Services.SessionLockGuard _lockGuard;
    private readonly ClipboardAutoEraser _clipboardEraser = new();

    /// <summary>The currently running long-running save task. Referenced by LockAsync via SessionTaskRegistry.</summary>
    public Task? CurrentSaveTask { get; private set; }

    private bool _profileLoaded;

    // Discipline 2: zombie-VM suppression flag.
    // IsActive = true: the page is in front and processes messages immediately.
    // IsActive = false: another page is shown. DB queries and thumbnail decryption are forbidden.
    // No NeedsReload counterpart is needed: ProfilePage.Loaded runs LoadAsync() on every revisit,
    // so a change notification that arrives while inactive is picked up by that reload anyway.
    public bool IsActive { get; private set; }
    public void Resume() => IsActive = true;
    public void Pause()  => IsActive = false;

    // Tracks whether a real edit happened since the last load/commit, independent of HasDraft (which
    // reflects whether TwinB currently differs from TwinA). Gates the page's blanket LostFocus-driven
    // AutoSaveDraftAsync calls (ProfilePage.xaml.cs) so an untouched action - most notably an unedited
    // placeholder field from AddCustomField - can't get swept into a draft merely because focus later
    // left some unrelated control. Mirrors SecretsViewModel's _isDirty/HasUnsavedChanges.
    private bool _isDirty;
    public bool HasUnsavedChanges => _isDirty;

    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
    private CancellationTokenSource? _saveDraftCts;
    private DateTime? _createdAt;

    // Set around the collection mutation inside AddCustomField/RemoveCustomField below, so the
    // CollectionChanged handler can tell an explicit "+"/trash-can command apart from the
    // Remove+Add pair WinUI 3's ListView.CanReorderItems raises for a drag-and-drop reorder (it
    // never calls ObservableCollection.Move).
    private bool _explicitCustomFieldAdd;
    private bool _explicitCustomFieldRemove;

    // Syncs AppSession.PendingProfileImageIds with the ProfileFiles collection.
    // Must always be called after changes in LoadAsync, AddFilesAsync, RemoveFile, and SaveAsync.
    private void SyncPendingProfileImageIds()
    {
        _session.PendingProfileImageIds.Clear();
        foreach (var img in ProfileFiles)
            _session.PendingProfileImageIds.Add(img.Id);
    }

    // Reverse direction of SyncPendingProfileImageIds: reconciles ProfileFiles to match
    // AppSession.PendingProfileImageIds after it was mutated externally (ViewerWindow's
    // ToggleProfileLinkAsync, on link/unlink). A no-op when the two are already in sync.
    private async Task RefreshFilesFromPendingAsync()
    {
        try
        {
            var targetIds  = new HashSet<int>(_session.PendingProfileImageIds);
            var currentIds = ProfileFiles.Select(f => f.Id).ToHashSet();
            if (targetIds.SetEquals(currentIds)) return;

            foreach (var stale in ProfileFiles.Where(f => !targetIds.Contains(f.Id)).ToList())
            {
                ProfileFiles.Remove(stale);
                stale.Dispose();
            }

            var toAddIds = targetIds.Except(currentIds).ToList();
            if (toAddIds.Count > 0)
            {
                var key  = _session.GetKey();
                var imgs = await _storedFiles.GetByIdsAsync(toAddIds, key);
                try
                {
                    foreach (var img in imgs)
                        ProfileFiles.Add(BuildProfileFileItem(img, key));
                }
                finally
                {
                    foreach (var img in imgs) CryptographicOperations.ZeroMemory(img.FileName);
                }
            }

            HasDraft = await _profileService.HasDraftAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError("RefreshFilesFromPendingAsync failed. [{ExType}]", ex.GetType().Name);
        }
    }

    // Pending avatar state: not persisted until SaveAsync commits it (so Discard can restore it)
    private byte[]? _pendingAvatarBytes;
    private bool    _pendingAvatarCleared;

    private static readonly HashSet<string> _dirtyCustomFieldProps = new(StringComparer.Ordinal)
    {
        nameof(CustomFieldModel.Label),
        nameof(CustomFieldModel.Value),
        nameof(CustomFieldModel.IsPassword),
        nameof(CustomFieldModel.IsUrl),
        nameof(CustomFieldModel.IsDate),
    };

    private void OnCustomFieldPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_profileLoaded) return;
        if (e.PropertyName != null && !_dirtyCustomFieldProps.Contains(e.PropertyName)) return;
        _isDirty = true;
        _ = AutoSaveDraftAsync("CustomFieldChanged");
    }

    // ── Non-PII [ObservableProperty] ──────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool HasDraft { get; set; }

    // ── PII fields: SecureCharBuffer (POH-pinned) ───────────────────────
    // On every keystroke, the TwoWay binding engine creates a WinRT HSTRING → CLR string and
    // passes it as the setter's value argument. Right after copying it into POH via SetFromSpan,
    // ZeroStringInternals zero-clears value's internal buffer to minimize the residual window.
    // The getter creates a temporary string only for display/copy (SetFromSpan ZeroMemories the old buffer first).

    private readonly SecureCharBuffer _nameBuf               = new();
    private readonly SecureCharBuffer _nicknameBuf           = new();
    private readonly SecureCharBuffer _emailBuf              = new();
    private readonly SecureCharBuffer _postalCodeBuf         = new();
    private readonly SecureCharBuffer _address1Buf           = new();
    private readonly SecureCharBuffer _address2Buf           = new();
    private readonly SecureCharBuffer _mobilePhoneBuf        = new();
    private readonly SecureCharBuffer _homePhoneBuf          = new();
    private readonly SecureCharBuffer _identityItem1Buf      = new();
    private readonly SecureCharBuffer _identityItem2Buf      = new();
    private readonly SecureCharBuffer _identityItem3Buf      = new();
    private readonly SecureCharBuffer _notesBuf              = new();

    public string Name
    {
        get => _nameBuf.ToDisplayString();
        set
        {
            // Equality guard: without this, a TwoWay x:Bind chain through a custom DependencyProperty
            // control (e.g. RecordExtrasControl) can ping-pong an unchanged value back and forth
            // forever, since OnPropertyChanged() re-triggers the binding regardless of whether the
            // value actually changed (this crashed with a StackOverflowException on the Notes field).
            // The guard also protects against a subtler corruption: TextBox.Text (unlike
            // PasswordBox.Password) returns the SAME string instance on every read rather than a
            // fresh copy, so a redundant no-op set (e.g. a second LostFocus-driven read of the same
            // TextBox) would otherwise still reach ZeroStringInternals below and zero out the exact
            // string object the TextBox is still displaying, visibly blanking the field.
            if (MemoryExtensions.Equals(_nameBuf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _nameBuf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
        }
    }

    public string Nickname
    {
        get => _nicknameBuf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_nicknameBuf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _nicknameBuf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
        }
    }

    public string Email
    {
        get => _emailBuf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_emailBuf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _emailBuf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
        }
    }

    public string PostalCode
    {
        get => _postalCodeBuf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_postalCodeBuf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _postalCodeBuf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
        }
    }

    public string Address1
    {
        get => _address1Buf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_address1Buf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _address1Buf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
        }
    }

    public string Address2
    {
        get => _address2Buf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_address2Buf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _address2Buf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
        }
    }

    public string MobilePhone
    {
        get => _mobilePhoneBuf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_mobilePhoneBuf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _mobilePhoneBuf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
        }
    }

    public string HomePhone
    {
        get => _homePhoneBuf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_homePhoneBuf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _homePhoneBuf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
        }
    }

    public string IdentityItem1
    {
        get => _identityItem1Buf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_identityItem1Buf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _identityItem1Buf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsId1ExpiryEnabled));
            OnPropertyChanged(nameof(IdentityItem1Token));
        }
    }

    public string IdentityItem2
    {
        get => _identityItem2Buf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_identityItem2Buf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _identityItem2Buf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsId2ExpiryEnabled));
            OnPropertyChanged(nameof(IdentityItem2Token));
        }
    }

    public string IdentityItem3
    {
        get => _identityItem3Buf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_identityItem3Buf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _identityItem3Buf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsId3ExpiryEnabled));
            OnPropertyChanged(nameof(IdentityItem3Token));
        }
    }

    /// <summary>Tokens that pass a POH-pinned buffer reference to each identity item's UserIdLargePreviewControl (see SecretEditModel.UserIdToken).</summary>
    public PasswordToken? IdentityItem1Token => _identityItem1Buf.IsEmpty ? null : new PasswordToken(_identityItem1Buf);
    public PasswordToken? IdentityItem2Token => _identityItem2Buf.IsEmpty ? null : new PasswordToken(_identityItem2Buf);
    public PasswordToken? IdentityItem3Token => _identityItem3Buf.IsEmpty ? null : new PasswordToken(_identityItem3Buf);

    // ── Identity item labels (non-PII: custom field names, not values) ─────────────
    public static string DefaultLabelIdentityItem1 => ProfileEditModel.DefaultLabelIdentityItem1;
    public static string DefaultLabelIdentityItem2 => ProfileEditModel.DefaultLabelIdentityItem2;
    public static string DefaultLabelIdentityItem3 => ProfileEditModel.DefaultLabelIdentityItem3;

    [ObservableProperty] public partial string LabelIdentityItem1 { get; set; } = ProfileEditModel.DefaultLabelIdentityItem1;
    [ObservableProperty] public partial string LabelIdentityItem2 { get; set; } = ProfileEditModel.DefaultLabelIdentityItem2;
    [ObservableProperty] public partial string LabelIdentityItem3 { get; set; } = ProfileEditModel.DefaultLabelIdentityItem3;

    public string Notes
    {
        get => _notesBuf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(_notesBuf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            _notesBuf.SetFromSpan(value.AsSpan());
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
        }
    }

    // ── Dates and alerts (non-PII) ──────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasId1Expiry))]
    public partial DateTimeOffset? Id1Expiry { get; set; }
    [ObservableProperty] public partial bool HasId1Alert { get; set; }
    [ObservableProperty] public partial bool IsId1Expired { get; set; }
    public bool HasId1Expiry       => Id1Expiry.HasValue;
    public bool IsId1ExpiryEnabled => !_identityItem1Buf.IsEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasId2Expiry))]
    public partial DateTimeOffset? Id2Expiry { get; set; }
    [ObservableProperty] public partial bool HasId2Alert { get; set; }
    [ObservableProperty] public partial bool IsId2Expired { get; set; }
    public bool HasId2Expiry       => Id2Expiry.HasValue;
    public bool IsId2ExpiryEnabled => !_identityItem2Buf.IsEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasId3Expiry))]
    public partial DateTimeOffset? Id3Expiry { get; set; }
    [ObservableProperty] public partial bool HasId3Alert { get; set; }
    [ObservableProperty] public partial bool IsId3Expired { get; set; }
    public bool HasId3Expiry       => Id3Expiry.HasValue;
    public bool IsId3ExpiryEnabled => !_identityItem3Buf.IsEmpty;

    partial void OnId1ExpiryChanged(DateTimeOffset? value) => UpdateId1Alert();

    private void UpdateId1Alert()
    {
        if (!Id1Expiry.HasValue) { HasId1Alert = false; IsId1Expired = false; return; }
        var expiry = Id1Expiry.Value.Date;
        HasId1Alert  = expiry < DateTime.Today.AddDays(AppConstants.ProfileIdLicenseWarnDays);
        IsId1Expired = expiry < DateTime.Today;
    }

    partial void OnId2ExpiryChanged(DateTimeOffset? value) => UpdateId2Alert();

    private void UpdateId2Alert()
    {
        if (!Id2Expiry.HasValue) { HasId2Alert = false; IsId2Expired = false; return; }
        var expiry = Id2Expiry.Value.Date;
        HasId2Alert  = expiry < DateTime.Today.AddDays(AppConstants.ProfileIdLicenseWarnDays);
        IsId2Expired = expiry < DateTime.Today;
    }

    partial void OnId3ExpiryChanged(DateTimeOffset? value) => UpdateId3Alert();

    private void UpdateId3Alert()
    {
        if (!Id3Expiry.HasValue) { HasId3Alert = false; IsId3Expired = false; return; }
        var expiry = Id3Expiry.Value.Date;
        HasId3Alert  = expiry < DateTime.Today.AddDays(AppConstants.ProfilePassportWarnDays);
        IsId3Expired = expiry < DateTime.Today;
    }

    [ObservableProperty] public partial string CreatedAtText { get; set; } = "";
    [ObservableProperty] public partial string UpdatedAtText { get; set; } = "";
    [ObservableProperty] public partial BitmapImage? AvatarSource { get; set; }

    public bool HasAvatar => AvatarSource != null;
    public string Id1ExpiryTooltip => string.Format(LocalizationManager.Get("Common.ExpiryAlarmNotice"), 60);
    public string Id2ExpiryTooltip => string.Format(LocalizationManager.Get("Common.ExpiryAlarmNotice"), 60);
    public string Id3ExpiryTooltip => string.Format(LocalizationManager.Get("Common.ExpiryAlarmNotice"), 90);

    public ObservableCollection<FileItem> ProfileFiles { get; } = [];
    public ObservableCollection<CustomFieldModel> CustomFields { get; } = [];

    public ProfileViewModel(
        ProfileService profileService,
        IAppNotificationService notification,
        StoredFileRepository storedFiles,
        ICryptoService crypto,
        AppSession session,
        IWindowService windowService,
        AvatarService avatarService,
        IAuditLogService auditLog,
        ILogger<ProfileViewModel> logger,
        NaimitsuVault.Services.SessionLockGuard lockGuard,
        NaimitsuVault.Services.SessionTaskRegistry taskRegistry,
        AutoBackupService autoBackup)
    {
        _profileService = profileService;
        _notification   = notification;
        _storedFiles    = storedFiles;
        _crypto         = crypto;
        _session        = session;
        _windowService  = windowService;
        _avatarService  = avatarService;
        _auditLog       = auditLog;
        _logger         = logger;
        _lockGuard      = lockGuard;
        _autoBackup     = autoBackup;
        taskRegistry.Register(this);   // Rev7: self-registers upon instantiation
        CustomFields.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (CustomFieldModel cf in e.NewItems)
                    cf.PropertyChanged += OnCustomFieldPropertyChanged;
            if (e.OldItems != null)
                foreach (CustomFieldModel cf in e.OldItems)
                    cf.PropertyChanged -= OnCustomFieldPropertyChanged;
            if (!_profileLoaded) return;
            switch (e.Action)
            {
                // Kept for completeness - some path might one day raise a real Move - but WinUI 3's
                // ListView.CanReorderItems never actually does (see the Remove/Add cases below).
                case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                    CurrentSaveTask = TrySaveCustomFieldOrderOnlyAsync();
                    break;

                // WinUI 3's ListView.CanReorderItems implements a drag-and-drop reorder as
                // Remove-then-Insert against the bound ObservableCollection, never as a single Move -
                // this Remove is the reorder's first half, with the field not yet re-inserted at its
                // new position. Reacting to it as a real deletion (autosaving a draft) captures a
                // transient collection state that's missing the moved field. A real deletion goes
                // through RemoveCustomFieldCommand, which sets _explicitCustomFieldRemove around the
                // mutation.
                case System.Collections.Specialized.NotifyCollectionChangedAction.Remove when !_explicitCustomFieldRemove:
                    break;

                // A bare Add via the "+" button (AddCustomFieldCommand, flagged by
                // _explicitCustomFieldAdd) must not enter draft mode on its own - the new field is
                // still an empty placeholder indistinguishable from "nothing changed" in the TwinA
                // comparison. OnCustomFieldPropertyChanged (subscribed above) saves the draft once
                // the user actually edits the field's Label/Value.
                case System.Collections.Specialized.NotifyCollectionChangedAction.Add when _explicitCustomFieldAdd:
                    break;

                // An Add that isn't from AddCustomFieldCommand is the reorder's second half (see the
                // Remove case above) - the collection now reflects its final order, so this is the
                // right moment to try the direct TwinA commit if the model is fully clean, or do
                // nothing otherwise. Never enters draft mode for this: a
                // mid-draft reorder must not bleed into the next autosave, and
                // TrySaveCustomFieldOrderOnlyAsync itself re-checks the clean-state condition before
                // writing anything.
                case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                    CurrentSaveTask = TrySaveCustomFieldOrderOnlyAsync();
                    break;

                // A real deletion (RemoveCustomFieldCommand) still saves immediately - removing an
                // existing field is itself a meaningful change.
                default:
                    _isDirty = true;
                    _ = AutoSaveDraftAsync("CustomFieldsCollectionChanged");
                    break;
            }
        };

        // Marks a real edit dirty for HasUnsavedChanges (see its declaration above). Filtered to
        // _profileLoaded so LoadAsync's own field assignments (which happen before it flips to true)
        // never register as a user edit, and to a denylist of computed/display-only properties that
        // get re-notified as a side effect of a real property changing (or of LoadAsync/SaveAsync
        // updating display state) rather than representing an edit themselves.
        PropertyChanged += (_, e) =>
        {
            if (!_profileLoaded) return;
            if (e.PropertyName is nameof(IsBusy) or nameof(HasDraft) or nameof(CreatedAtText) or nameof(UpdatedAtText)
                or nameof(AvatarSource) or nameof(HasAvatar)
                or nameof(HasId1Expiry) or nameof(HasId2Expiry) or nameof(HasId3Expiry)
                or nameof(HasId1Alert) or nameof(HasId2Alert) or nameof(HasId3Alert)
                or nameof(IsId1Expired) or nameof(IsId2Expired) or nameof(IsId3Expired))
                return;
            _isDirty = true;
        };

        // Reflects a profile link toggled from the viewer (TwinB write + PendingProfileImageIds
        // update) without waiting for the next page navigation to re-run LoadAsync.
        WeakReferenceMessenger.Default.Register<StorageChangedMessage>(this, (_, _) =>
        {
            if (!IsActive) return;
            if (_profileLoaded) _ = RefreshFilesFromPendingAsync();
        });
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _clipboardEraser.Dispose();
        _nameBuf.Dispose(); _nicknameBuf.Dispose();    _emailBuf.Dispose();
        _postalCodeBuf.Dispose(); _address1Buf.Dispose(); _address2Buf.Dispose();
        _mobilePhoneBuf.Dispose(); _homePhoneBuf.Dispose(); _identityItem1Buf.Dispose();
        _identityItem2Buf.Dispose(); _identityItem3Buf.Dispose(); _notesBuf.Dispose();
        foreach (var cf in CustomFields) cf.Dispose();
        CustomFields.Clear();
        foreach (var f in ProfileFiles) f.Dispose();
        ProfileFiles.Clear();
        if (_pendingAvatarBytes != null)
        {
            CryptographicOperations.ZeroMemory(_pendingAvatarBytes.AsSpan());
            _pendingAvatarBytes = null;
        }
        GC.SuppressFinalize(this);
    }

    public async Task LoadAsync()
    {
        _profileLoaded = false;
        _isDirty = false;
        IsBusy = true;
        try
        {
            // Wait for any save already in flight (e.g. a DateChanged/LostFocus-triggered fire-and-forget
            // AutoSaveDraftAsync from the very action that also triggered this reload) before reading from
            // the DB below. Otherwise a page-cache reload (navigate away and back) can race the background
            // write to TwinB and read stale pre-write data, making a just-entered field appear blank until
            // the next reload picks up the completed write.
            var pending = CurrentSaveTask;
            if (pending != null)
            {
                try { await pending; }
                catch (Exception ex) { _logger.LogWarning("[Profile] Awaited in-flight save ended with an exception. [{ExType}]", ex.GetType().Name); }
            }

            var dek  = _session.GetKey();
            var data = await _profileService.LoadProfileAsync(dek);
            HasDraft = await _profileService.HasDraftAsync();

            Name               = data.Name           ?? "";
            Nickname           = data.Nickname       ?? "";
            Email              = data.Email          ?? "";
            PostalCode         = data.PostalCode     ?? "";
            Address1           = data.Address1       ?? "";
            Address2           = data.Address2       ?? "";
            MobilePhone        = data.MobilePhone    ?? "";
            HomePhone          = data.HomePhone      ?? "";
            IdentityItem1      = data.IdentityItem1  ?? "";
            Id1Expiry          = data.Id1Expiry.HasValue ? (DateTimeOffset?)new DateTimeOffset(data.Id1Expiry.Value.ToLocalTime()) : null;
            IdentityItem2      = data.IdentityItem2  ?? "";
            Id2Expiry          = data.Id2Expiry.HasValue ? (DateTimeOffset?)new DateTimeOffset(data.Id2Expiry.Value.ToLocalTime()) : null;
            IdentityItem3      = data.IdentityItem3  ?? "";
            Id3Expiry          = data.Id3Expiry.HasValue ? (DateTimeOffset?)new DateTimeOffset(data.Id3Expiry.Value.ToLocalTime()) : null;
            Notes              = data.Notes          ?? "";
            LabelIdentityItem1 = data.IdentityItem1Label;
            LabelIdentityItem2 = data.IdentityItem2Label;
            LabelIdentityItem3 = data.IdentityItem3Label;
            _createdAt          = data.CreatedAt;
            CreatedAtText       = FormatDate(data.CreatedAt);
            UpdatedAtText       = ParseIsoDisplay(data.Timestamp.AsSpan());

            // data's PII strings are decrypt-only throwaways (never bound to a live control), fully
            // consumed by the assignments above. Zero them unconditionally here: the property setters'
            // own ZeroStringInternals call is skipped whenever the reloaded value is unchanged from
            // what's already buffered (their early-return guards against zeroing a live TextBox.Text
            // reference on a redundant same-value set), which would otherwise leave a fresh unprotected
            // copy behind on every reload that doesn't happen to change anything.
            data.ZeroPii();

            foreach (var cf in CustomFields)
            {
                cf.PropertyChanged -= OnCustomFieldPropertyChanged;
                cf.Dispose();
            }
            CustomFields.Clear();
            var loadedCfs = data.CustomFields ?? [];
            int nextCfId  = loadedCfs.Count > 0 ? loadedCfs.Max(f => f.FieldId) : 0;
            foreach (var cf in loadedCfs)
            {
                int id = cf.FieldId != 0 ? cf.FieldId : ++nextCfId;
                CustomFields.Add(new CustomFieldModel
                {
                    FieldId = id, Label = cf.Label, Value = cf.Value,
                    FieldType = cf.FieldType,
                });
            }

            foreach (var f in ProfileFiles) f.Dispose();
            ProfileFiles.Clear();
            var key    = _session.GetKey();
            var ids    = await _storedFiles.GetProfileFileLinksAsync();
            var gen0Id = new HashSet<int>(ids);
            var imgs   = await _storedFiles.GetByIdsAsync(ids, key);
            try
            {
                foreach (var img in imgs)
                    ProfileFiles.Add(BuildProfileFileItem(img, key));
            }
            finally
            {
                // Immediately wipe the plaintext filename array that StoredFileRepository allocates on each decrypt
                foreach (var img in imgs) CryptographicOperations.ZeroMemory(img.FileName);
            }

            // Pre-inject FileIds recorded in the draft (TwinB) into PendingProfileImageIds.
            // This is the discipline that lets "dropped but unsaved file links" resurrect in the
            // right pane even after a restart. Since LoadProfileAsync prefers TwinB, data.FileIds
            // holds the draft's file IDs.
            foreach (var fid in data.FileIds)
                _session.PendingProfileImageIds.Add(fid);

            // Restore dropped but unsaved files even when redisplayed from the page cache.
            // Refetch IDs remaining in PendingProfileImageIds that are not yet reflected in TwinA (confirmed) and add them to ProfileFiles.
            var pendingOnlyIds = _session.PendingProfileImageIds.Except(gen0Id).ToList();
            if (pendingOnlyIds.Count > 0)
            {
                var pendingImgs = await _storedFiles.GetByIdsAsync(pendingOnlyIds, key);
                try
                {
                    foreach (var img in pendingImgs)
                        ProfileFiles.Add(BuildProfileFileItem(img, key));
                }
                finally
                {
                    // Immediately wipe the plaintext filename array that StoredFileRepository allocates on each decrypt
                    foreach (var img in pendingImgs) CryptographicOperations.ZeroMemory(img.FileName);
                }
            }

            SyncPendingProfileImageIds(); // Sync the final state of TwinA (confirmed) ∪ pending

            // Reset pending avatar state before rebuilding it from the DB (guards against stale
            // state if this VM instance is reused from the page cache across navigations).
            if (_pendingAvatarBytes != null)
            {
                CryptographicOperations.ZeroMemory(_pendingAvatarBytes.AsSpan());
                _pendingAvatarBytes = null;
            }
            _pendingAvatarCleared = false;

            byte[]? avatarBytes;
            var draftAvatar = await _avatarService.LoadDraftRawAsync(key);
            if (draftAvatar != null)
            {
                // TwinB avatar row exists: empty = the draft explicitly cleared it, non-empty = the draft's avatar
                if (draftAvatar.Length > 0)
                {
                    _pendingAvatarBytes = draftAvatar;
                    avatarBytes         = draftAvatar;
                }
                else
                {
                    _pendingAvatarCleared = true;
                    avatarBytes           = null;
                }
            }
            else
            {
                avatarBytes = _session.AvatarBytes ?? await _avatarService.LoadAsync(key);
            }
            AvatarSource = await BytesToBitmapImageAsync(avatarBytes);
            OnPropertyChanged(nameof(HasAvatar));

            _profileLoaded = true;
            // Re-check right before logging: the user may have navigated away while the load above
            // (DB round-trips, field decryption, avatar/thumbnail decoding) was still in flight.
            if (_session.LastActivePageTag != "profile") return;
            try
            {
                await _auditLog.LogAsync(AuditEventCode.ProfileViewed, null, _session.GetKey());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Profile] Failed to log ProfileViewed. [{ExType}]", ex.GetType().Name);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // reason: a short caller-supplied tag (e.g. "LosingFocus") identifying which UI trigger requested
    // the save, so a production log can show WHY a draft write was attempted, not just that one was.
    public async Task AutoSaveDraftAsync(string reason)
    {
        Task? saveOp = null;
        try
        {
            // Gate 1: blocks new tasks after the barrier. Inside the try so that a fire-and-forget
            // caller hitting a locked session gets the same logged cancellation as a task that was
            // already in flight, instead of an unobserved OperationCanceledException.
            _lockGuard.ThrowIfLocked();
            if (!_profileLoaded) return;
            if (_session.IsReadOnlyRestricted) return;

            saveOp = AutoSaveDraftCoreAsync(reason);
            CurrentSaveTask = saveOp;
            await saveOp;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[SaveDraft] reason={Reason}: cancelled/blocked by session lock", reason);
        }
        // AutoSaveDraftAsync is routinely called fire-and-forget (`_ = AutoSaveDraftAsync(...)`),
        // so a non-cancellation failure (SQLite lock contention, etc.) surfacing during a CTS race
        // must not become a silent UnobservedTaskException that leaves the draft unsaved.
        catch (Exception ex)
        {
            _logger.LogWarning("[SaveDraft] reason={Reason}: failed to save draft. [{ExType}]", reason, ex.GetType().Name);
        }
        finally
        {
            if (saveOp != null && CurrentSaveTask == saveOp) CurrentSaveTask = null;
        }
    }

    private async Task AutoSaveDraftCoreAsync(string reason)
    {
        _lockGuard.ThrowIfLocked();  // Gate 1 (repeated at the core entry point)
        // Cancel the previous pending task so only the latest call executes
        _saveDraftCts?.Cancel();
        _saveDraftCts = new CancellationTokenSource();
        var ct = _saveDraftCts.Token;
        try
        {
            await _saveSemaphore.WaitAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            // Read the latest model after acquiring the semaphore (last-write-wins saves the latest state)
            ct.ThrowIfCancellationRequested();
            _lockGuard.ThrowIfLocked();  // Gate 2 [final line of defense]
            var dek   = _session.GetKey();
            var model = BuildEditModel();
            _lockGuard.ThrowIfLocked();

            // NoOp draft: if the current in-memory state is semantically identical to TwinA (the
            // committed profile), discard rather than write a phantom draft that shows the pencil
            // icon with nothing to actually show in the compare dialog (mirrors
            // SecretsViewModel.SaveDraftCoreAsync's NoOp draft check).
            // The avatar is checked separately (via the pending-avatar flags) because it's excluded
            // from ProfileEditModel/IsSameAsTwinA — without this, changing only the avatar left
            // HasDraft stuck at false since the text-field comparison alone saw no difference.
            using var twinA = await _profileService.GetTwinASnapshotAsync(dek);
            // BuildEditModelFromSnapshot decrypts fresh, throwaway PII strings purely for this
            // comparison (never bound to a live control) — zero them once the comparison is done.
            var twinAModel = twinA != null ? _profileService.BuildEditModelFromSnapshot(twinA) : null;
            string? mismatchReason = twinAModel != null ? FindFirstMismatch(model, twinAModel) : "NoTwinA";
            twinAModel?.ZeroPii();
            bool avatarUnchanged = _pendingAvatarBytes == null && !_pendingAvatarCleared;
            if (mismatchReason == null && avatarUnchanged)
            {
                await _profileService.DiscardDraftAsync();
                await _avatarService.DiscardAvatarDraftAsync();
                HasDraft = false;
                _isDirty = false;
                _logger.LogInformation("[SaveDraft] reason={Reason}: NoOp, draft discarded", reason);
                return;
            }

            // The I/O phase uses CancellationToken.None (prevents corruption from a partial write)
            await _profileService.SaveDraftAsync(model, dek);
            if (_pendingAvatarBytes != null)
                await _avatarService.SaveDraftAsync(_pendingAvatarBytes, dek);
            else if (_pendingAvatarCleared)
                await _avatarService.SaveDraftAsync([], dek);
            HasDraft = true;
            _logger.LogInformation("[SaveDraft] reason={Reason}: draft written (mismatch={MismatchReason}, avatarChanged={AvatarChanged})",
                reason, mismatchReason ?? "None", !avatarUnchanged);
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    // Direct commit for a pure custom-field reorder. Only writes when
    // the model is fully clean (no existing draft) and every field already existed in TwinA - a
    // reorder that also carries an unedited placeholder field from the "+" button is left untouched
    // instead (in-memory order only, reverts on reload). Unlike Secrets there's no TimeMachine
    // generation to skip here - Profile has no history concept, so this simply commits to TwinA the
    // same way a manual Save would, just without the UI-facing side effects (notification, audit log).
    private async Task TrySaveCustomFieldOrderOnlyAsync()
    {
        try
        {
            _lockGuard.ThrowIfLocked();
            if (!_profileLoaded) return;
            if (_session.IsReadOnlyRestricted) return;
            if (HasDraft || HasUnsavedChanges) return;

            var dek = _session.GetKey();
            using var twinA = await _profileService.GetTwinASnapshotAsync(dek);
            if (twinA == null) return;
            var twinAModel = _profileService.BuildEditModelFromSnapshot(twinA);
            try
            {
                var existingFieldIds = twinAModel.CustomFields.Select(f => f.FieldId).ToHashSet();
                if (!CustomFields.All(cf => existingFieldIds.Contains(cf.FieldId))) return;

                _lockGuard.ThrowIfLocked();  // final line of defense, immediately before the write
                var model = BuildEditModel();
                await _profileService.CommitProfileAsync(model, dek);
                _autoBackup.MarkContentChanged();
                _logger.LogInformation("[CustomFieldOrder] Profile: order-only direct commit");
            }
            finally
            {
                twinAModel.ZeroPii();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[CustomFieldOrder] cancelled/blocked by session lock");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[CustomFieldOrder] failed. [{ExType}]", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Vault-wide self-heal for the profile's TwinA/TwinB singleton draft: discards TwinB when it is
    /// byte-for-byte identical to TwinA (both PII fields and avatar), mirroring
    /// SecretsViewModel.CleanUpNoOpDraftsAsync. Meant to be called once per session, right after unlock,
    /// alongside the dashboard's other startup diagnostics (DashboardViewModel.TryRunInitialScanAsync) -
    /// so a stray draft left behind by e.g. a crash mid-edit self-heals proactively instead of leaving
    /// the pencil icon (HasDraft) stuck on until the user next edits the profile.
    /// Returns true if a stray draft was discarded.
    /// </summary>
    internal async Task<bool> CleanUpNoOpDraftAsync(CancellationToken ct = default)
    {
        if (_session.IsReadOnlyRestricted) return false;

        SecureProfileSnapshot twinA;
        SecureProfileSnapshot? twinB;
        var key = _session.GetKey();
        try
        {
            if (!await _profileService.HasDraftAsync()) return false;
            (twinA, twinB) = await _profileService.GetCompareDataAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[CleanUpNoOpDraft] failed to load TwinA/TwinB. [{ExType}]", ex.GetType().Name);
            return false;
        }

        try
        {
            if (twinB == null) return false;
            ct.ThrowIfCancellationRequested();

            var twinAModel = _profileService.BuildEditModelFromSnapshot(twinA);
            var twinBModel = _profileService.BuildEditModelFromSnapshot(twinB);
            string? mismatchReason = FindFirstMismatch(twinBModel, twinAModel);
            twinAModel.ZeroPii();
            twinBModel.ZeroPii();
            if (mismatchReason != null) return false;

            byte[]? committedAvatar = null, draftAvatar = null;
            try
            {
                draftAvatar = await _avatarService.LoadDraftRawAsync(key);
                // No TwinB avatar row = the draft never touched the avatar (see AvatarService.LoadDraftRawAsync).
                bool avatarNoOp = draftAvatar == null;
                if (!avatarNoOp)
                {
                    committedAvatar = await _avatarService.LoadCommittedRawAsync(key);
                    avatarNoOp = (committedAvatar ?? []).AsSpan().SequenceEqual(draftAvatar);
                }
                if (!avatarNoOp) return false;
            }
            finally
            {
                if (committedAvatar is { Length: > 0 }) CryptographicOperations.ZeroMemory(committedAvatar);
                if (draftAvatar is { Length: > 0 }) CryptographicOperations.ZeroMemory(draftAvatar);
            }

            await _profileService.DiscardDraftAsync();
            await _avatarService.DiscardAvatarDraftAsync();
            HasDraft = false;
            _logger.LogInformation("[CleanUpNoOpDraft] stray NoOp profile draft discarded");
            try { await _auditLog.LogAsync(AuditEventCode.NoOpDraftDiscarded, new NoOpDraftDiscardedPayload(1), key); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogWarning("[CleanUpNoOpDraft] Failed to log NoOpDraftDiscarded. [{ExType}]", ex.GetType().Name); }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[CleanUpNoOpDraft] skipped, comparison failed. [{ExType}]", ex.GetType().Name);
            return false;
        }
        finally
        {
            twinA.Dispose();
            twinB?.Dispose();
        }
    }

    internal ProfileEditModel BuildEditModel() => new()
    {
        Name               = Name,
        Nickname           = Nickname,
        Email              = Email,
        PostalCode         = PostalCode,
        Address1           = Address1,
        Address2           = Address2,
        MobilePhone        = MobilePhone,
        HomePhone          = HomePhone,
        IdentityItem1      = IdentityItem1,
        Id1Expiry          = Id1Expiry?.UtcDateTime,
        IdentityItem2      = IdentityItem2,
        Id2Expiry          = Id2Expiry?.UtcDateTime,
        IdentityItem3      = IdentityItem3,
        Id3Expiry          = Id3Expiry?.UtcDateTime,
        Notes              = Notes,
        IdentityItem1Label = LabelIdentityItem1,
        IdentityItem2Label = LabelIdentityItem2,
        IdentityItem3Label = LabelIdentityItem3,
        CustomFields       = CustomFields.Select(cf =>
            new ProfileCustomFieldData(cf.Label, cf.Value, cf.FieldType, cf.FieldId)).ToList(),
        CreatedAt           = _createdAt,
        FileIds            = ProfileFiles.Select(i => i.Id).ToList(),
    };

    // Avatar bytes are intentionally excluded: they're managed separately via AvatarService/AppSession
    // and were never part of TwinB's snapshot, so they don't factor into whether TwinB is a no-op.
    // Returns the name of the first field found to differ from TwinA, or null if identical. The field
    // NAME (never the value) lets the caller log why a draft write/discard decision was made without
    // ever exposing PII - field names are fixed schema identifiers, not secret content.
    private static string? FindFirstMismatch(ProfileEditModel draft, ProfileEditModel twinA)
    {
        if (draft.Name               != twinA.Name)               return "Name";
        if (draft.Nickname           != twinA.Nickname)           return "Nickname";
        if (draft.Email              != twinA.Email)              return "Email";
        if (draft.PostalCode         != twinA.PostalCode)         return "PostalCode";
        if (draft.Address1           != twinA.Address1)           return "Address1";
        if (draft.Address2           != twinA.Address2)           return "Address2";
        if (draft.MobilePhone        != twinA.MobilePhone)        return "MobilePhone";
        if (draft.HomePhone          != twinA.HomePhone)          return "HomePhone";
        if (draft.IdentityItem1      != twinA.IdentityItem1)      return "IdentityItem1";
        if (draft.Id1Expiry          != twinA.Id1Expiry)          return "Id1Expiry";
        if (draft.IdentityItem2      != twinA.IdentityItem2)      return "IdentityItem2";
        if (draft.Id2Expiry          != twinA.Id2Expiry)          return "Id2Expiry";
        if (draft.IdentityItem3      != twinA.IdentityItem3)      return "IdentityItem3";
        if (draft.Id3Expiry          != twinA.Id3Expiry)          return "Id3Expiry";
        if (draft.Notes              != twinA.Notes)              return "Notes";
        if (draft.IdentityItem1Label != twinA.IdentityItem1Label) return "IdentityItem1Label";
        if (draft.IdentityItem2Label != twinA.IdentityItem2Label) return "IdentityItem2Label";
        if (draft.IdentityItem3Label != twinA.IdentityItem3Label) return "IdentityItem3Label";
        if (!draft.FileIds.ToHashSet().SetEquals(twinA.FileIds))  return "FileIds";
        if (!CustomFieldsEqual(draft.CustomFields, twinA.CustomFields)) return "CustomFields";
        return null;
    }

    // ProfileCustomFieldData is a record, so SequenceEqual after sorting gives an order-insensitive
    // structural comparison for free.
    private static bool CustomFieldsEqual(List<ProfileCustomFieldData> a, List<ProfileCustomFieldData> b) =>
        a.Count == b.Count && a.OrderBy(f => f.FieldId).SequenceEqual(b.OrderBy(f => f.FieldId));

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var dek = _session.GetKey();

            // Persist the pending avatar change (cancellable via Discard only until reaching this point)
            if (_pendingAvatarBytes != null)
            {
                await _avatarService.SaveAsync(_pendingAvatarBytes, dek);
                // AppSession.AvatarBytes's setter has already ZeroMemoried _pendingAvatarBytes
                _pendingAvatarBytes = null;
                await _avatarService.DiscardAvatarDraftAsync();
            }
            else if (_pendingAvatarCleared)
            {
                await _avatarService.DeleteAvatarAsync();
                _pendingAvatarCleared = false;
                await _avatarService.DiscardAvatarDraftAsync();
            }

            var model = BuildEditModel();
            model.CreatedAt ??= DateTime.UtcNow;
            await _profileService.CommitProfileAsync(model, dek);
            await _storedFiles.UpdateProfileFileLinksAsync(ProfileFiles.Select(i => i.Id));
            SyncPendingProfileImageIds(); // Save complete: sync TwinA (confirmed) and ProfileFiles to a matching state
            WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
            HasDraft = false;
            _isDirty = false;

            var now = DateTime.UtcNow;
            _createdAt ??= now;
            UpdatedAtText = FormatDate(now);
            if (string.IsNullOrEmpty(CreatedAtText)) CreatedAtText = FormatDate(_createdAt);

            var displayName = !string.IsNullOrWhiteSpace(model.Nickname) ? model.Nickname
                            : !string.IsNullOrWhiteSpace(model.Name)     ? model.Name : null;
            WeakReferenceMessenger.Default.Send(new DisplayNameChangedMessage(displayName));
            _notification.Show(LK.Common_SuccessSaveComplete, LK.Common_SuccessSaveComplete, NotificationSeverity.Success, TimeSpan.FromSeconds(3));
            _autoBackup.MarkContentChanged();
            try
            {
                await _auditLog.LogAsync(AuditEventCode.ProfileSaved, null, _session.GetKey());
            }
            catch (Exception ex2)
            {
                _logger.LogWarning("[Profile] Failed to log ProfileSaved. [{ExType}]", ex2.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("SaveAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy && HasDraft && !_session.IsReadOnlyRestricted;

    [RelayCommand(CanExecute = nameof(HasDraft))]
    private async Task CompareAsync()
    {
        // The caller (ProfilePage.xaml.cs) builds the dialog, so nothing happens here.
        await Task.CompletedTask;
    }

    [RelayCommand]
    public async Task AddFilesAsync(string[] filePaths)
    {
        if (_session.IsReadOnlyRestricted) return;
        IsBusy = true;
        int failedCount = 0;
        try
        {
            var key = _session.GetKey();
            foreach (var path in filePaths)
            {
                if (!File.Exists(path)) continue;

                byte[]? data          = null;
                byte[]? thumbnailData = null;
                try
                {
                    // (1) Read directly into a pinned array (never passes through the movable heap)
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Length <= 0) continue;
                    data = GC.AllocateArray<byte>(checked((int)fileInfo.Length), pinned: true);
                    await using (var fsr = new FileStream(
                            path, FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: 1, FileOptions.Asynchronous))
                        await fsr.ReadExactlyAsync(data.AsMemory());

                    var fileHash = HMACSHA256.HashData(key.Span, SHA256.HashData(data));
                    var existing = await _storedFiles.FindByHashAsync(fileHash, key);

                    if (existing != null)
                    {
                        if (!ProfileFiles.Any(i => i.Id == existing.Id))
                        {
                            // Non-thumbnailable files (certs, text, generic binaries) must still be
                            // added - the UI falls back to FileTypeGlyph when ThumbnailData is null.
                            var thumb = existing.ThumbnailBlob != null
                                ? _crypto.DecryptToPin(existing.ThumbnailBlob, key.Span) : null;
                            var fi = new FileItem { Id = existing.Id, ContentType = existing.ContentTypeCode, ThumbnailData = thumb };
                            fi.SetFileNameFromUtf8(existing.FileName);
                            ProfileFiles.Add(fi);
                        }
                    }
                    else
                    {
                        var ext         = Path.GetExtension(path.AsSpan());
                        var contentType = FC.FromExtension(ext) is var ct && ct != 0 ? ct : FC.OctetStream;
                        bool isPdf      = FC.IsPdf(contentType);
                        // Only images/PDFs can produce a thumbnail (matches GalleryViewModel.AddFilesAsync).
                        // Calling this unconditionally throws for e.g. a certificate, since it tries to
                        // decode arbitrary bytes as an image.
                        if (FC.CanCreateThumbnail(contentType))
                            thumbnailData = await ImageHelper.CreateThumbnailAsync(data, 200, isPdf);

                        var (encOrig, encThumb) = await Task.Run(() => (
                            _crypto.Encrypt(data, key.Span),
                            thumbnailData != null ? _crypto.Encrypt(thumbnailData, key.Span) : null));

                        // (2) Filename: pinned UTF-8 byte array → copied into both StoredFile and FileItem
                        var fileNameSpan = Path.GetFileName(path.AsSpan());
                        int fnByteCount  = Encoding.UTF8.GetByteCount(fileNameSpan);
                        var fnBytes      = GC.AllocateArray<byte>(fnByteCount > 0 ? fnByteCount : 1, pinned: true);
                        try
                        {
                            Encoding.UTF8.GetBytes(fileNameSpan, fnBytes.AsSpan());

                            var newImage = new StoredFile
                            {
                                FileName        = fnBytes,
                                ContentTypeCode = contentType,
                                FileSize      = data.Length,
                                FileHash      = fileHash,
                                FileModifiedAt = fileInfo.LastWriteTimeUtc,
                                OriginalBlob  = encOrig,
                                ThumbnailBlob = encThumb
                            };
                            var newId = await _storedFiles.AddAsync(newImage, key);
                            _autoBackup.MarkContentChanged();
                            try
                            {
                                await _auditLog.LogAsync(AuditEventCode.FileAdded, new FileAddedPayload(newId, Path.GetFileName(path)), key);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("[AddFilesAsync] Failed to log FileAdded. [{ExType}]", ex.GetType().Name);
                            }

                            // (3) Pinned copy for thumbnail display (FileItem.Dispose ZeroMemories it)
                            byte[]? pinnedThumb = null;
                            if (thumbnailData != null)
                            {
                                pinnedThumb = GC.AllocateArray<byte>(thumbnailData.Length, pinned: true);
                                thumbnailData.AsSpan().CopyTo(pinnedThumb.AsSpan());
                            }

                            var fi = new FileItem { Id = newId, ContentType = contentType, ThumbnailData = pinnedThumb };
                            fi.SetFileNameFromUtf8(fnBytes);
                            ProfileFiles.Add(fi);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(fnBytes.AsSpan());
                        }
                    }
                }
                // A failure on one file (corrupt image, locked by another process, etc.) must not
                // abort the remaining files in this drop/picker batch - skip just this one.
                catch (Exception ex)
                {
                    failedCount++;
                    _logger.LogWarning("[AddFilesAsync] Skipped a file due to an error. [{ExType}]", ex.GetType().Name);
                }
                finally
                {
                    if (data          != null) CryptographicOperations.ZeroMemory(data.AsSpan());
                    if (thumbnailData != null) CryptographicOperations.ZeroMemory(thumbnailData.AsSpan());
                }
            }
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
        if (failedCount > 0)
        {
            _notification.Show(LK.Common_Warning,
                string.Format(LocalizationManager.Get("Common.InfoFilesAddFailed"), failedCount),
                NotificationSeverity.Warning, TimeSpan.FromSeconds(5));
        }
        SyncPendingProfileImageIds(); // Reflect ProfileFiles immediately after D&D
        if (_profileLoaded) _ = AutoSaveDraftAsync("FilesDropped");
        WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
    }

    [RelayCommand]
    private void AddCustomField(string? type)
    {
        int nextId = CustomFields.Count > 0 ? CustomFields.Max(f => f.FieldId) + 1 : 1;
        _explicitCustomFieldAdd = true;
        try
        {
            var fieldType = CustomFieldTypeExtensions.FromCommandParameter(type);
            CustomFields.Add(new CustomFieldModel
            {
                FieldId   = nextId,
                Label     = fieldType.GetDefaultLabel(),
                FieldType = fieldType,
            });
        }
        finally
        {
            _explicitCustomFieldAdd = false;
        }
    }

    [RelayCommand]
    private void RemoveCustomField(CustomFieldModel? field)
    {
        if (field == null) return;
        _explicitCustomFieldRemove = true;
        try
        {
            CustomFields.Remove(field);
            field.Dispose();
        }
        finally
        {
            _explicitCustomFieldRemove = false;
        }
    }

    [RelayCommand]
    private async Task OpenCustomFieldUrlAsync(CustomFieldModel? field)
    {
        var raw = field?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return;
        var candidate = raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? raw : "https://" + raw;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _notification.Show(LK.Common_Error, string.Format(LocalizationManager.GetById(LK.Common_ErrorBrowserOpenFailed), raw), NotificationSeverity.Error, TimeSpan.FromSeconds(3));
            return;
        }
        try
        {
            bool success = await Launcher.LaunchUriAsync(uri);
            if (!success)
                _notification.Show(LK.Common_Error, string.Format(LocalizationManager.GetById(LK.Common_ErrorBrowserOpenFailed), raw), NotificationSeverity.Error, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError("OpenCustomFieldUrlAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(3));
        }
    }

    [RelayCommand]
    public void RemoveFile(FileItem? item)
    {
        if (item == null) return;
        ProfileFiles.Remove(item);
        item.Dispose();
        SyncPendingProfileImageIds(); // Reflect ProfileFiles immediately after removal
        if (_profileLoaded) _ = AutoSaveDraftAsync("FileRemoved");
    }

    // Shared by every profile field's copy button - fieldKey is a fixed internal identifier (XAML
    // CommandParameter literal), mapped here to the field's current display label (IdentityItem1-3
    // use LabelIdentityItem1-3, which reflect the user's own renaming) so the audit trail matches
    // what's actually on screen.
    [RelayCommand]
    private void CopyField(string? fieldKey)
    {
        var (value, label) = fieldKey switch
        {
            "Name"          => (Name,          LocalizationManager.Get("Profile.Name")),
            "Nickname"      => (Nickname,       LocalizationManager.Get("Profile.Nickname")),
            "Email"         => (Email,          LocalizationManager.Get("Common.Email")),
            "PostalCode"    => (PostalCode,     LocalizationManager.Get("Profile.PostalCode")),
            "Address1"      => (Address1,       LocalizationManager.Get("Profile.AddressLine1")),
            "Address2"      => (Address2,       LocalizationManager.Get("Profile.AddressLine2")),
            "MobilePhone"   => (MobilePhone,    LocalizationManager.Get("Profile.MobilePhone")),
            "HomePhone"     => (HomePhone,      LocalizationManager.Get("Profile.HomePhone")),
            "IdentityItem1" => (IdentityItem1,  LabelIdentityItem1),
            "IdentityItem2" => (IdentityItem2,  LabelIdentityItem2),
            "IdentityItem3" => (IdentityItem3,  LabelIdentityItem3),
            _               => (null, string.Empty),
        };
        if (string.IsNullOrEmpty(value)) return;
        ClipboardHelper.SetText(value);
        _clipboardEraser.ScheduleClear(value.AsSpan());
        _ = LogFieldCopiedAsync(label);
    }

    [RelayCommand]
    private void CopyCustomField(CustomFieldModel? field)
    {
        if (string.IsNullOrEmpty(field?.Value)) return;
        ClipboardHelper.SetText(field.Value);
        _clipboardEraser.ScheduleClear(field.Value.AsSpan());
        _ = LogFieldCopiedAsync(field.Label);
    }

    private async Task LogFieldCopiedAsync(string fieldLabel)
    {
        try
        {
            await _auditLog.LogAsync(
                AuditEventCode.ProfileFieldCopiedToClipboard,
                new ProfileFieldCopiedToClipboardPayload(fieldLabel),
                _session.GetKey());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[ProfileViewModel] Failed to log ProfileFieldCopiedToClipboard. [{ExType}]", ex.GetType().Name);
        }
    }

    public void OpenViewer(int fileId) => _windowService.OpenViewer(fileId);

    public async Task SetAvatarFromBytesAsync(byte[] croppedBytes)
    {
        IsBusy = true;
        var localCopy   = croppedBytes.ToArray();
        CryptographicOperations.ZeroMemory(croppedBytes.AsSpan());

        var displayCopy = localCopy.ToArray();
        // pending is kept alive until SaveAsync commits it, so it isn't wiped in finally (managed via an ownership flag)
        var pendingCopy = localCopy.ToArray();
        bool pendingOwned = false;
        try
        {
            // Discard the old pending bytes before replacing them with the new ones
            if (_pendingAvatarBytes != null)
            {
                CryptographicOperations.ZeroMemory(_pendingAvatarBytes.AsSpan());
                _pendingAvatarBytes = null;
            }
            _pendingAvatarBytes  = pendingCopy;
            _pendingAvatarCleared = false;
            pendingOwned = true; // Prevents a double wipe in finally

            AvatarSource = await BytesToBitmapImageAsync(displayCopy);
            OnPropertyChanged(nameof(HasAvatar));

            // Pass a disposable buffer for the ShellWindow notification (wiping is left to the receiver)
            var msgCopy = displayCopy.ToArray();
            WeakReferenceMessenger.Default.Send(new AvatarChangedMessage(msgCopy));

            await AutoSaveDraftAsync("AvatarSet");
        }
        catch (Exception ex)
        {
            _logger.LogError("SetAvatarFromBytesAsync failed. [{ExType}]", ex.GetType().Name);
            if (_pendingAvatarBytes != null)
            {
                CryptographicOperations.ZeroMemory(_pendingAvatarBytes.AsSpan());
                _pendingAvatarBytes = null;
            }
            pendingOwned = true; // already cleared
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(localCopy.AsSpan());
            CryptographicOperations.ZeroMemory(displayCopy.AsSpan());
            if (!pendingOwned) CryptographicOperations.ZeroMemory(pendingCopy.AsSpan());
            IsBusy = false;
        }
    }

    [RelayCommand] private void ClearId1Expiry() { Id1Expiry = null; _ = AutoSaveDraftAsync("ExpiryCleared"); }
    [RelayCommand] private void ClearId2Expiry() { Id2Expiry = null; _ = AutoSaveDraftAsync("ExpiryCleared"); }
    [RelayCommand] private void ClearId3Expiry() { Id3Expiry = null; _ = AutoSaveDraftAsync("ExpiryCleared"); }

    [RelayCommand]
    private async Task ClearAvatarAsync()
    {
        if (_pendingAvatarBytes != null)
        {
            CryptographicOperations.ZeroMemory(_pendingAvatarBytes.AsSpan());
            _pendingAvatarBytes = null;
        }
        _pendingAvatarCleared = true;
        AvatarSource = null;
        OnPropertyChanged(nameof(HasAvatar));
        WeakReferenceMessenger.Default.Send(new AvatarChangedMessage(null));
        await AutoSaveDraftAsync("AvatarCleared");
    }

    /// <summary>
    /// Packs the current state and the draft into a CompareRowItem list for the comparison dialog.
    /// Decrypts the encrypted BLOB directly into a ProfileSecureSnapshot (a set of SecureCharBuffers)
    /// and copies it into CompareRowItem (POH-pinned) without ever materializing a string.
    /// </summary>
    internal async Task<(List<CompareRowItem> Items, string TwinBSavedAt, string TwinAUpdatedAt,
        byte[]? Gen0AvatarBytes, byte[]? DraftAvatarBytes, bool AvatarChanged,
        List<CompareFileThumbnail> Gen0Files, List<CompareFileThumbnail> DraftFiles)> BuildCompareItemsAsync()
    {
        var dek = _session.GetKey();
        var (twinAEnc, twinBEnc) = await _profileService.GetCompareRawAsync();

        using var snapTwinA = twinAEnc != null ? DecryptToSnapshot(twinAEnc, dek) : new ProfileSecureSnapshot();
        using var snapTwinB = twinBEnc != null ? DecryptToSnapshot(twinBEnc, dek) : null;

        var twinBSavedAt  = ParseIsoDisplay(snapTwinB != null ? snapTwinB.Timestamp.Span : ReadOnlySpan<char>.Empty);
        var twinAUpdatedAt = ParseIsoDisplay(snapTwinA.Timestamp.Span);

        var items = new List<CompareRowItem>();

        // Copies the span directly into CompareRowItem (POH-pinned). No string is created
        void AddRow(string label, SecureCharBuffer g0buf, SecureCharBuffer? dDbuf, bool sensitive, bool labelDiffer = false)
        {
            var g0    = g0buf.Span;
            var dD    = dDbuf != null ? dDbuf.Span : ReadOnlySpan<char>.Empty;
            bool diff = !MemoryExtensions.Equals(g0, dD, StringComparison.Ordinal);
            items.Add(new CompareRowItem(label, g0, dD, diff, sensitive, labelDiffer));
        }

        // Date fields: convert the ISO string to display format before copying
        void AddDate(string label, SecureCharBuffer g0buf, SecureCharBuffer? dDbuf)
        {
            string g0str = ParseIsoDisplay(g0buf.Span);
            string dDstr = ParseIsoDisplay(dDbuf != null ? dDbuf.Span : ReadOnlySpan<char>.Empty);
            bool diff    = !string.Equals(g0str, dDstr, StringComparison.Ordinal);
            items.Add(new CompareRowItem(label, g0str.AsSpan(), dDstr.AsSpan(), diff, false));
        }

        AddRow(LocalizationManager.Get("Profile.Name"), snapTwinA.Name,               snapTwinB?.Name,        false);
        AddRow(LocalizationManager.Get("Profile.Nickname"), snapTwinA.Nickname,       snapTwinB?.Nickname,    false);
        AddRow(LocalizationManager.Get("Common.Email"), snapTwinA.Email,              snapTwinB?.Email,       false);
        AddRow(LocalizationManager.Get("Profile.PostalCode"), snapTwinA.PostalCode,   snapTwinB?.PostalCode,  false);
        AddRow(LocalizationManager.Get("Profile.AddressLine1"), snapTwinA.Address1,   snapTwinB?.Address1,    false);
        AddRow(LocalizationManager.Get("Profile.AddressLine2"), snapTwinA.Address2,   snapTwinB?.Address2,    false);
        AddRow(LocalizationManager.Get("Profile.MobilePhone"), snapTwinA.MobilePhone, snapTwinB?.MobilePhone, false);
        AddRow(LocalizationManager.Get("Profile.HomePhone"), snapTwinA.HomePhone,     snapTwinB?.HomePhone,   false);
        // Resolves each identity item's effective label independently for gen0 (committed) and draft,
        // so a label-only edit (value unchanged) can still be flagged via labelDiffer below - otherwise
        // AddRow's own value-diff check would leave the row looking completely unchanged.
        string g0Label1 = string.IsNullOrEmpty(snapTwinA.IdentityItem1Label) ? ProfileEditModel.DefaultLabelIdentityItem1 : snapTwinA.IdentityItem1Label;
        string dLabel1  = snapTwinB == null ? g0Label1
            : string.IsNullOrEmpty(snapTwinB.IdentityItem1Label) ? ProfileEditModel.DefaultLabelIdentityItem1 : snapTwinB.IdentityItem1Label;
        AddRow(dLabel1, snapTwinA.IdentityItem1, snapTwinB?.IdentityItem1, false, labelDiffer: snapTwinB != null && dLabel1 != g0Label1);
        AddDate("", snapTwinA.Id1Expiry, snapTwinB?.Id1Expiry);

        string g0Label2 = string.IsNullOrEmpty(snapTwinA.IdentityItem2Label) ? ProfileEditModel.DefaultLabelIdentityItem2 : snapTwinA.IdentityItem2Label;
        string dLabel2  = snapTwinB == null ? g0Label2
            : string.IsNullOrEmpty(snapTwinB.IdentityItem2Label) ? ProfileEditModel.DefaultLabelIdentityItem2 : snapTwinB.IdentityItem2Label;
        AddRow(dLabel2, snapTwinA.IdentityItem2, snapTwinB?.IdentityItem2, false, labelDiffer: snapTwinB != null && dLabel2 != g0Label2);
        AddDate("", snapTwinA.Id2Expiry, snapTwinB?.Id2Expiry);

        string g0Label3 = string.IsNullOrEmpty(snapTwinA.IdentityItem3Label) ? ProfileEditModel.DefaultLabelIdentityItem3 : snapTwinA.IdentityItem3Label;
        string dLabel3  = snapTwinB == null ? g0Label3
            : string.IsNullOrEmpty(snapTwinB.IdentityItem3Label) ? ProfileEditModel.DefaultLabelIdentityItem3 : snapTwinB.IdentityItem3Label;
        AddRow(dLabel3, snapTwinA.IdentityItem3, snapTwinB?.IdentityItem3, false, labelDiffer: snapTwinB != null && dLabel3 != g0Label3);
        AddDate("", snapTwinA.Id3Expiry, snapTwinB?.Id3Expiry);

        // CustomFields (user-defined, variable length) use ProfileCustomFieldData's string fields.
        // Ordered before Notes to match the edit screen (RecordExtrasControl: custom fields, then Notes).
        var twinACfs  = snapTwinA.CustomFields  ?? [];
        var twinBCfs  = snapTwinB?.CustomFields ?? [];
        int maxRows   = Math.Max(twinACfs.Count, twinBCfs.Count);
        for (int i = 0; i < maxRows; i++)
        {
            var g = i < twinACfs.Count ? twinACfs[i] : null;
            var d = i < twinBCfs.Count ? twinBCfs[i] : null;
            var label    = d?.Label ?? g?.Label ?? $"#{i + 1}";
            bool rowDiff = g?.Value != d?.Value || g?.Label != d?.Label || g?.FieldType != d?.FieldType;
            string gVal = g?.FieldType == CustomFieldType.Date ? DateOnlyDisplay(g.Value.AsSpan()) : (g?.Value ?? "");
            string dVal = d?.FieldType == CustomFieldType.Date ? DateOnlyDisplay(d.Value.AsSpan()) : (d?.Value ?? "");
            // A newly-added field (g == null) has no committed label to compare against, so it's
            // always treated as label-changed - by this point a label-only edit is the only way an
            // all-empty-value draft field could exist at all (an untouched placeholder field never
            // gets auto-saved into a draft).
            bool cfLabelDiffer = g?.Label != d?.Label;
            items.Add(new CompareRowItem(label, gVal.AsSpan(), dVal.AsSpan(), rowDiff,
                g?.FieldType == CustomFieldType.Password || d?.FieldType == CustomFieldType.Password,
                cfLabelDiffer));
        }

        AddRow(LocalizationManager.Get("Common.Notes"), snapTwinA.Notes, snapTwinB?.Notes, false);

        // Attached files (TwinA = confirmed, TwinB = draft): like the avatar below, rendered as
        // thumbnails outside the CompareRowItem text-diff machinery rather than as a plain count row.
        var twinAFileIds = await _storedFiles.GetProfileFileLinksAsync();
        var gen0Files    = await BuildAttachmentThumbnailsAsync(twinAFileIds, dek);
        var draftFiles   = ProfileFiles.Select(fi => new CompareFileThumbnail
        {
            FileId         = fi.Id,
            FileName       = fi.FileName,
            ContentType    = fi.ContentType,
            ThumbnailBytes = fi.ThumbnailData,
            IsQuarantined  = fi.IsQuarantined,
        }).ToList();

        // Avatar: excluded from the TwinA/TwinB profile blob (see BuildEditModel), so it's compared
        // via AvatarService's own TwinB draft row instead of the CompareRowItem text-diff machinery.
        var gen0Avatar  = await _avatarService.LoadCommittedRawAsync(dek);
        var draftAvatar = await _avatarService.LoadDraftRawAsync(dek);
        bool avatarChanged = draftAvatar != null;
        byte[]? shownDraftAvatar = draftAvatar == null ? gen0Avatar // no draft row: draft mirrors committed
            : draftAvatar.Length > 0 ? draftAvatar                 // draft sets a new avatar
            : null;                                                // draft explicitly clears the avatar

        return (items, twinBSavedAt, twinAUpdatedAt, gen0Avatar, shownDraftAvatar, avatarChanged, gen0Files, draftFiles);
    }

    // Fetches and decrypts the committed (TwinA) side's attached-file thumbnails for the compare
    // dialog. The draft (TwinB) side doesn't need this: ProfileFiles already holds fully-loaded
    // FileItems in memory (it's the live editing state), so it's mapped directly by the caller.
    private async Task<List<CompareFileThumbnail>> BuildAttachmentThumbnailsAsync(List<int> fileIds, DekScope key)
    {
        if (fileIds.Count == 0) return [];

        var files = await _storedFiles.GetByIdsAsync(fileIds, key);
        try
        {
            return files.Select(f => new CompareFileThumbnail
            {
                FileId         = f.Id,
                FileName       = Encoding.UTF8.GetString(f.FileName),
                ContentType    = f.ContentTypeCode,
                ThumbnailBytes = !f.IsQuarantined && f.ThumbnailBlob != null ? _crypto.DecryptToPin(f.ThumbnailBlob, key.Span) : null,
                IsQuarantined  = f.IsQuarantined,
            }).ToList();
        }
        finally
        {
            // Immediately wipe the plaintext filename array that StoredFileRepository allocates on each decrypt
            foreach (var f in files) CryptographicOperations.ZeroMemory(f.FileName);
        }
    }

    /// <summary>
    /// Builds a FileItem for a profile attachment tile. A quarantined StoredFile (ContentType/FileName-extension
    /// mismatch — suspected tampering or corruption) is still shown, so the attachment stays visible instead of
    /// silently vanishing, but its thumbnail is never decrypted since ContentType can't be trusted to interpret it.
    /// </summary>
    private FileItem BuildProfileFileItem(StoredFile img, DekScope key)
    {
        var fi = new FileItem
        {
            Id            = img.Id,
            ContentType   = img.ContentTypeCode,
            IsQuarantined = img.IsQuarantined,
            ThumbnailData = img.IsQuarantined || img.ThumbnailBlob == null ? null : _crypto.DecryptToPin(img.ThumbnailBlob, key.Span)
        };
        fi.SetFileNameFromUtf8(img.FileName);
        return fi;
    }

    public async Task DiscardDraftAndReloadAsync()
    {
        // Discard the pending avatar (wipe without persisting)
        if (_pendingAvatarBytes != null)
        {
            CryptographicOperations.ZeroMemory(_pendingAvatarBytes.AsSpan());
            _pendingAvatarBytes = null;
        }
        _pendingAvatarCleared = false;

        // Discard also cancels dropped but unsaved file links.
        // Since LoadAsync rebuilds from TwinA (confirmed), PendingProfileImageIds must be cleared beforehand.
        _session.PendingProfileImageIds.Clear();

        // Serialized against AutoSaveDraftCoreAsync: without this, a save still mid-flight from
        // before this discard was invoked could write to the draft concurrently with this method's
        // own DiscardDraftAsync call. Same fix/rationale as SecretsViewModel.DiscardDraftWithGcAsync.
        _saveDraftCts?.Cancel();
        await _saveSemaphore.WaitAsync();
        try
        {
            await _profileService.DiscardDraftAsync();
            await _avatarService.DiscardAvatarDraftAsync();
        }
        finally
        {
            _saveSemaphore.Release();
        }
        HasDraft = false;
        await LoadAsync();

        // Reset the ShellWindow navigation avatar to its pre-discard state
        var msgCopy = _session.AvatarBytes?.ToArray();
        WeakReferenceMessenger.Default.Send(new AvatarChangedMessage(msgCopy));
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Decrypts an encrypted BLOB directly into a pinned array, scans it with Utf8JsonReader,
    /// and returns a ProfileSecureSnapshot. The decrypted buffer is zero-cleared immediately in a finally block.
    /// </summary>
    private ProfileSecureSnapshot DecryptToSnapshot(byte[] encrypted, DekScope dek)
    {
        int plainLen = encrypted.Length - ICryptoService.AeadOverhead;
        if (plainLen <= 0) return new ProfileSecureSnapshot();
        var pinned = GC.AllocateArray<byte>(plainLen, pinned: true);
        try
        {
            _crypto.Decrypt(encrypted, dek.Span, pinned.AsSpan());
            return ScanToSecureSnapshot(pinned.AsSpan());
        }
        catch
        {
            return new ProfileSecureSnapshot();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinned.AsSpan());
        }
    }

    /// <summary>
    /// Scans a decrypted UTF-8 JSON span with Utf8JsonReader and streams it into a ProfileSecureSnapshot.
    /// Never creates a string (copies directly into a pinned SecureCharBuffer via ArrayPool + CopyString).
    /// </summary>
    private static ProfileSecureSnapshot ScanToSecureSnapshot(ReadOnlySpan<byte> json)
    {
        var snap   = new ProfileSecureSnapshot();
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return snap;

        // Fix: removed the PropertyName restriction from the while condition; just keep calling Read until the stream ends
        while (reader.Read())
        {
            // Only process when the token is a "property name"; braces {}, arrays, and other structural tokens pass through naturally
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if      (reader.ValueTextEquals("Timestamp"u8))          { reader.Read(); FillBuf(ref reader, snap.Timestamp); }
            else if (reader.ValueTextEquals("Name"u8))               { reader.Read(); FillBuf(ref reader, snap.Name); }
            else if (reader.ValueTextEquals("Nickname"u8))           { reader.Read(); FillBuf(ref reader, snap.Nickname); }
            else if (reader.ValueTextEquals("Email"u8))              { reader.Read(); FillBuf(ref reader, snap.Email); }
            else if (reader.ValueTextEquals("PostalCode"u8))         { reader.Read(); FillBuf(ref reader, snap.PostalCode); }
            else if (reader.ValueTextEquals("Address1"u8))           { reader.Read(); FillBuf(ref reader, snap.Address1); }
            else if (reader.ValueTextEquals("Address2"u8))           { reader.Read(); FillBuf(ref reader, snap.Address2); }
            else if (reader.ValueTextEquals("MobilePhone"u8))        { reader.Read(); FillBuf(ref reader, snap.MobilePhone); }
            else if (reader.ValueTextEquals("HomePhone"u8))          { reader.Read(); FillBuf(ref reader, snap.HomePhone); }
            // Legacy key aliases (NationalIdentifier/IdCardExpiry/LicenseNumber/LicenseExpiry/PassportNumber/
            // PassportExpiry) so profiles saved before the IdentityItem1-3 rename keep loading correctly.
            else if (reader.ValueTextEquals("IdentityItem1"u8) || reader.ValueTextEquals("NationalIdentifier"u8)) { reader.Read(); FillBuf(ref reader, snap.IdentityItem1); }
            else if (reader.ValueTextEquals("Id1Expiry"u8)     || reader.ValueTextEquals("IdCardExpiry"u8))       { reader.Read(); FillBuf(ref reader, snap.Id1Expiry); }
            else if (reader.ValueTextEquals("IdentityItem2"u8) || reader.ValueTextEquals("LicenseNumber"u8))      { reader.Read(); FillBuf(ref reader, snap.IdentityItem2); }
            else if (reader.ValueTextEquals("Id2Expiry"u8)     || reader.ValueTextEquals("LicenseExpiry"u8))      { reader.Read(); FillBuf(ref reader, snap.Id2Expiry); }
            else if (reader.ValueTextEquals("IdentityItem3"u8) || reader.ValueTextEquals("PassportNumber"u8))     { reader.Read(); FillBuf(ref reader, snap.IdentityItem3); }
            else if (reader.ValueTextEquals("Id3Expiry"u8)     || reader.ValueTextEquals("PassportExpiry"u8))     { reader.Read(); FillBuf(ref reader, snap.Id3Expiry); }
            else if (reader.ValueTextEquals("IdentityItem1Label"u8)) { reader.Read(); snap.IdentityItem1Label = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
            else if (reader.ValueTextEquals("IdentityItem2Label"u8)) { reader.Read(); snap.IdentityItem2Label = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
            else if (reader.ValueTextEquals("IdentityItem3Label"u8)) { reader.Read(); snap.IdentityItem3Label = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
            else if (reader.ValueTextEquals("Notes"u8))              { reader.Read(); FillBuf(ref reader, snap.Notes); }
            else if (reader.ValueTextEquals("CreatedAt"u8))           { reader.Read(); FillBuf(ref reader, snap.CreatedAt); }
            else if (reader.ValueTextEquals("CustomFields"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                    snap.CustomFields = JsonSerializer.Deserialize(
                        ref reader, ProfileJsonContext.Default.ListProfileCustomFieldData);
            }
        }
        return snap;
    }

    /// <summary>
    /// Copies the current string token from a Utf8JsonReader into a SecureCharBuffer via ArrayPool.
    /// Never creates a string.
    /// </summary>
    private static void FillBuf(ref Utf8JsonReader reader, SecureCharBuffer buf)
    {
        if (reader.TokenType == JsonTokenType.Null) return;
        if (reader.TokenType != JsonTokenType.String) { reader.Skip(); return; }
        int maxLen = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
        var rented = ArrayPool<char>.Shared.Rent(maxLen > 0 ? maxLen : 1);
        try
        {
            int n = reader.CopyString(rented.AsSpan());
            buf.SetFromSpan(rented.AsSpan(0, n));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(rented.AsSpan()));
            ArrayPool<char>.Shared.Return(rented, clearArray: false);
        }
    }

    /// <summary>
    /// A snapshot that stores all fields of the decrypted profile JSON in SecureCharBuffers.
    /// Used exclusively by BuildCompareItemsAsync. Dispose zero-clears all buffers.
    /// </summary>
    private sealed class ProfileSecureSnapshot : IDisposable
    {
        internal readonly SecureCharBuffer Timestamp          = new();
        internal readonly SecureCharBuffer Name               = new();
        internal readonly SecureCharBuffer Nickname           = new();
        internal readonly SecureCharBuffer Email              = new();
        internal readonly SecureCharBuffer PostalCode         = new();
        internal readonly SecureCharBuffer Address1           = new();
        internal readonly SecureCharBuffer Address2           = new();
        internal readonly SecureCharBuffer MobilePhone        = new();
        internal readonly SecureCharBuffer HomePhone          = new();
        internal readonly SecureCharBuffer IdentityItem1      = new();
        internal readonly SecureCharBuffer Id1Expiry          = new();
        internal readonly SecureCharBuffer IdentityItem2      = new();
        internal readonly SecureCharBuffer Id2Expiry          = new();
        internal readonly SecureCharBuffer IdentityItem3      = new();
        internal readonly SecureCharBuffer Id3Expiry          = new();
        internal readonly SecureCharBuffer Notes              = new();
        internal readonly SecureCharBuffer CreatedAt           = new();
        internal List<ProfileCustomFieldData>? CustomFields;
        // Identity item labels - not PII (custom field names, not values), no ZeroMemory needed.
        internal string? IdentityItem1Label;
        internal string? IdentityItem2Label;
        internal string? IdentityItem3Label;

        public void Dispose()
        {
            Timestamp.Dispose();      Name.Dispose();           Nickname.Dispose();
            Email.Dispose();          PostalCode.Dispose();      Address1.Dispose();
            Address2.Dispose();       MobilePhone.Dispose();     HomePhone.Dispose();
            IdentityItem1.Dispose();  Id1Expiry.Dispose();       IdentityItem2.Dispose();
            Id2Expiry.Dispose();      IdentityItem3.Dispose();   Id3Expiry.Dispose();
            Notes.Dispose();          CreatedAt.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private static async Task<BitmapImage?> BytesToBitmapImageAsync(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        using var ms = new MemoryStream(bytes);
        var ras = ms.AsRandomAccessStream();
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(ras);
        return bmp;
    }

    private static string FormatDate(DateTime? dt) =>
        dt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "";

    private static string ParseIsoDisplay(ReadOnlySpan<char> s) =>
        !s.IsEmpty && DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "";

    private static string DateOnlyDisplay(ReadOnlySpan<char> s) =>
        !s.IsEmpty && DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("yyyy/MM/dd") : new string(s);
}
