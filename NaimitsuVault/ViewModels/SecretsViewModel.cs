// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using NaimitsuVault.Common;
using NaimitsuVault.Helpers;
using FC = NaimitsuVault.Common.FileTypeCode;
using Windows.System;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace NaimitsuVault.ViewModels;

public class FilterCategoryItem
{
    public int? Code { get; init; }
    public string Name { get; init; } = string.Empty;
}

public partial class CategoryGroupViewModel : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
    public int Code { get; set; }
    public ObservableCollection<SecretListItemViewModel> Secrets { get; } = [];
}

public partial class SecretListItemViewModel : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsFavorite { get; set; }
    [ObservableProperty] public partial DateTime? ExpiresAt { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFavicon))]
    public partial Microsoft.UI.Xaml.Media.Imaging.BitmapImage? FaviconSource { get; set; }
    public int Id { get; set; }
    public int? CategoryNum { get; set; }
    public DateTime UpdatedAt { get; set; }
    [ObservableProperty] public partial string? WebsiteDomain { get; set; }
    [ObservableProperty] public partial bool HasDraft { get; set; }
    public bool IsExpired      => ExpiresAt.HasValue && ExpiresAt.Value.Date < DateTime.Today;
    public bool IsExpiringSoon => ExpiresAt.HasValue && !IsExpired && ExpiresAt.Value.Date < DateTime.Today.AddDays(AppConstants.SecretPasswordWarnDays);
    public bool HasFavicon     => FaviconSource != null;

    partial void OnExpiresAtChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(IsExpired));
        OnPropertyChanged(nameof(IsExpiringSoon));
    }
}

public partial class CustomFieldModel : ObservableObject, IDisposable
{
    public int FieldId { get; set; }
    [ObservableProperty] public partial string Label { get; set; } = string.Empty;
    [ObservableProperty] public partial CustomFieldType FieldType { get; set; } = CustomFieldType.Text;
    [ObservableProperty][JsonIgnore] public partial bool IsRevealed { get; set; }

    // Read-only projections of FieldType, kept so XAML bindings (Visibility="{Binding IsPassword, ...}")
    // and other call sites that only ever read these don't need to switch on FieldType directly.
    [JsonIgnore] public bool IsPassword => FieldType == CustomFieldType.Password;
    [JsonIgnore] public bool IsUrl      => FieldType == CustomFieldType.Url;
    [JsonIgnore] public bool IsDate     => FieldType == CustomFieldType.Date;

    // The edit-session value for IsPassword=true fields (and, once revealed/enlarged, any field) is
    // held in a SecureCharBuffer (ZeroMemory-capable). Assignments before that switch fall into
    // _valuePlain. _useSecureBuffer - not "is the buffer non-empty right now" - is what decides which
    // one is authoritative: a SecureCharBuffer legitimately reports IsEmpty for a zero-length value
    // too (e.g. a field revealed before anything was typed into it), so inferring the mode from
    // emptiness would make Value's setter fall back to _valuePlain right after migrating and never
    // write through to the buffer again - PasswordToken would then stay null forever and the
    // large-preview control would never start following live input for a field that started empty.
    private readonly SecureCharBuffer _valueBuffer = new();
    private string _valuePlain = string.Empty;
    private bool _useSecureBuffer;

    public string Value
    {
        get => _useSecureBuffer ? new string(_valueBuffer.Span) : _valuePlain;
        set
        {
            var valSpan = (value ?? string.Empty).AsSpan();
            var currentSpan = _useSecureBuffer ? _valueBuffer.Span : _valuePlain.AsSpan();
            if (MemoryExtensions.Equals(currentSpan, valSpan, StringComparison.Ordinal)) return;
            if (_useSecureBuffer)
                _valueBuffer.SetFromSpan(valSpan);
            else
                _valuePlain = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateValue));
            OnPropertyChanged(nameof(HasValue));
            OnPropertyChanged(nameof(PasswordToken));
        }
    }

    /// <summary>
    /// Token that passes a POH-pinned buffer reference to PasswordLargePreviewControl.
    /// Returns a new instance on every getter call to force DependencyProperty change detection.
    /// The token itself carries no sensitive data (only a reference to the SecureCharBuffer).
    /// </summary>
    public PasswordToken? PasswordToken
        => _useSecureBuffer ? new PasswordToken(_valueBuffer) : null;

    /// <summary>
    /// Receives a char[] span directly from PasswordBox via SecurePasswordHelper and
    /// copies it into a SecureCharBuffer (no string is created).
    /// </summary>
    internal void SetValueDirect(ReadOnlySpan<char> chars)
    {
        var currentSpan = _useSecureBuffer ? _valueBuffer.Span : _valuePlain.AsSpan();
        if (MemoryExtensions.Equals(currentSpan, chars, StringComparison.Ordinal)) return;
        _valueBuffer.SetFromSpan(chars);
        _valuePlain = string.Empty;                 // Release the old string copy
        _useSecureBuffer = true;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(DateValue));
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(PasswordToken));
    }

    public void Dispose() => _valueBuffer.Dispose();

    partial void OnFieldTypeChanged(CustomFieldType value)
    {
        OnPropertyChanged(nameof(IsPassword));
        OnPropertyChanged(nameof(IsUrl));
        OnPropertyChanged(nameof(IsDate));
        NotifyShowState();
    }
    partial void OnIsRevealedChanged(bool value)
    {
        // When IsRevealed=true, switch to the secure buffer (migrating any existing _valuePlain
        // content) and enable PasswordToken. Switch unconditionally, even when there's nothing typed
        // yet - a field revealed/enlarged while still empty must still land in _useSecureBuffer mode
        // so the *first* keystroke writes through to the buffer instead of _valuePlain (see the field
        // comments above Value for why inferring the mode from buffer emptiness doesn't work here).
        if (value && !_useSecureBuffer)
        {
            if (!string.IsNullOrEmpty(_valuePlain))
            {
                _valueBuffer.SetFromSpan(_valuePlain.AsSpan());
                _valuePlain = string.Empty;
            }
            _useSecureBuffer = true;
        }
        NotifyShowState();
    }
    private void NotifyShowState()
    {
        OnPropertyChanged(nameof(ShowAsText));
        OnPropertyChanged(nameof(ShowAsTextPlain));
        OnPropertyChanged(nameof(ShowAsPasswordRevealed));
        OnPropertyChanged(nameof(ShowAsPasswordBox));
        OnPropertyChanged(nameof(ShowAsUrl));
        OnPropertyChanged(nameof(ShowAsPassword));
        OnPropertyChanged(nameof(ShowAsDate));
        OnPropertyChanged(nameof(PasswordToken));
    }

    [JsonIgnore] public bool ShowAsDate             => IsDate;
    [JsonIgnore] public bool ShowAsText             => !IsDate && (!IsPassword || IsRevealed);
    [JsonIgnore] public bool ShowAsTextPlain        => ShowAsText && !IsUrl && !IsPassword;
    [JsonIgnore] public bool ShowAsPasswordRevealed => IsPassword && IsRevealed;
    [JsonIgnore] public bool ShowAsUrl              => IsUrl && !IsDate;
    [JsonIgnore] public bool ShowAsPassword         => !IsDate && IsPassword && !IsRevealed;
    // PasswordBox is always displayed regardless of IsRevealed (switched via PasswordRevealMode)
    [JsonIgnore] public bool ShowAsPasswordBox      => !IsDate && IsPassword;
    [JsonIgnore] public bool HasValue              => !string.IsNullOrEmpty(Value);

    [JsonIgnore]
    public DateTimeOffset? DateValue
    {
        get => DateTimeOffset.TryParse(Value, out var d) ? d : null;
        set
        {
            Value = value?.DateTime.ToString("yyyy-MM-dd") ?? string.Empty;
            OnPropertyChanged();
        }
    }
}

public partial class SecretEditModel : ObservableObject, IDisposable
{
    public static string DefaultLabelTitle    => LocalizationManager.Get("Common.Title");
    public static string DefaultLabelUserId   => LocalizationManager.Get("Common.Username");
    public static string DefaultLabelPassword => LocalizationManager.Get("Common.Password");
    public static string DefaultLabelUrl      => LocalizationManager.Get("Common.Website");
    public static string DefaultLabelEmail => LocalizationManager.Get("Common.Email");
    public static string DefaultLabelNotes    => LocalizationManager.Get("Common.Notes");

    // ── POH-pinned SecureCharBuffer (subject to ZeroMemory) ─────────────────────
    internal readonly SecureCharBuffer PasswordBuf   = new();
    internal readonly SecureCharBuffer NotesBuf      = new();
    internal readonly SecureCharBuffer TotpSecretBuf = new();
    internal readonly SecureCharBuffer TitleBuf      = new();
    internal readonly SecureCharBuffer UserIdBuf     = new();
    internal readonly SecureCharBuffer WebsiteBuf    = new();
    internal readonly SecureCharBuffer EmailBuf   = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    public partial int Id { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryComboSelection))]
    public partial int? CategoryNum { get; set; }

    // Display-only fallback for the category ComboBox: a CategoryNum left over from before the
    // 2026-08-17 preset renumbering (e.g. the old 4-digit codes like 1100) no longer matches any
    // preset, which would otherwise leave the ComboBox showing nothing selected. This shows
    // "Uncategorized" instead, without touching the underlying CategoryNum - saving unrelated
    // fields never rewrites it, only an explicit ComboBox selection change does (via the setter).
    public int CategoryComboSelection
    {
        get
        {
            var code = CategoryNum ?? 0;
            return code == 0 || LocalizationManager.GetCategoryPresets().Any(p => p.Code == code) ? code : 0;
        }
        set => CategoryNum = value;
    }

    // Title / UserId / Website / Email: the [ObservableProperty] string version was removed.
    // SecureCharBuffer backing + manual properties eliminate PII residing on the movable heap.
    public string Title
    {
        get => TitleBuf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(TitleBuf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            TitleBuf.SetFromSpan(value.AsSpan());
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanTotpToggle));
        }
    }
    public ReadOnlySpan<char> TitleSpan => TitleBuf.Span;

    public string? UserId
    {
        get => UserIdBuf.IsEmpty ? null : UserIdBuf.ToDisplayString();
        set
        {
            var valSpan = value.AsSpan();
            if (MemoryExtensions.Equals(UserIdBuf.Span, valSpan, StringComparison.Ordinal)) return;
            UserIdBuf.SetFromSpan(valSpan);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanTotpToggle));
            OnPropertyChanged(nameof(UserIdToken));
        }
    }

    /// <summary>Token that passes a POH-pinned buffer reference to UserIdLargePreviewControl (see PasswordToken).</summary>
    public PasswordToken? UserIdToken
        => UserIdBuf.IsEmpty ? null : new PasswordToken(UserIdBuf);

    public string? Website
    {
        get => WebsiteBuf.IsEmpty ? null : WebsiteBuf.ToDisplayString();
        set
        {
            var valSpan = value.AsSpan();
            if (MemoryExtensions.Equals(WebsiteBuf.Span, valSpan, StringComparison.Ordinal)) return;
            WebsiteBuf.SetFromSpan(valSpan);
            OnPropertyChanged();
        }
    }

    public string? Email
    {
        get => EmailBuf.IsEmpty ? null : EmailBuf.ToDisplayString();
        set
        {
            var valSpan = value.AsSpan();
            if (MemoryExtensions.Equals(EmailBuf.Span, valSpan, StringComparison.Ordinal)) return;
            EmailBuf.SetFromSpan(valSpan);
            OnPropertyChanged();
        }
    }

    public string? Password
    {
        get => PasswordBuf.IsEmpty ? null : PasswordBuf.ToDisplayString();
        set
        {
            if (MemoryExtensions.Equals(PasswordBuf.Span, value.AsSpan(), StringComparison.Ordinal)) return;
            PasswordBuf.SetFromSpan(value.AsSpan());
            if (value?.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanTotpToggle));
            OnPropertyChanged(nameof(PasswordToken));
            if (PasswordBuf.IsEmpty)
            {
                _cachedStrengthScore = -1;
                OnPropertyChanged(nameof(PasswordStrengthLevel));
                OnPropertyChanged(nameof(PasswordStrengthText));
                OnPropertyChanged(nameof(HasPasswordStrength));
            }
        }
    }

    /// <summary>
    /// Token that passes a POH-pinned buffer reference to PasswordLargePreviewControl.
    /// Returns a new instance on every getter call to force DependencyProperty change detection.
    /// </summary>
    public PasswordToken? PasswordToken
        => PasswordBuf.IsEmpty ? null : new PasswordToken(PasswordBuf);

    /// <summary>Sets directly from a char[] span from the password generator / slot restore (no string is created).</summary>
    internal void SetPasswordDirect(ReadOnlySpan<char> chars)
    {
        PasswordBuf.SetFromSpan(chars);
        OnPropertyChanged(nameof(Password));
        OnPropertyChanged(nameof(CanTotpToggle));
        OnPropertyChanged(nameof(PasswordToken));
    }

    /// <summary>Sets directly from a char[] span from slot restore (no string is created).</summary>
    internal void SetNotesDirect(ReadOnlySpan<char> chars)
    {
        NotesBuf.SetFromSpan(chars);
        OnPropertyChanged(nameof(Notes));
    }

    /// <summary>Sets directly from a char[] span from slot restore (no string is created).</summary>
    internal void SetTotpSecretDirect(ReadOnlySpan<char> chars)
    {
        TotpSecretBuf.SetFromSpan(chars);
        OnPropertyChanged(nameof(TotpSecret));
        OnPropertyChanged(nameof(HasTotp));
        OnPropertyChanged(nameof(CanTotpToggle));
    }

    public string? Notes
    {
        get => NotesBuf.IsEmpty ? null : NotesBuf.ToDisplayString();
        set
        {
            var valSpan = value.AsSpan();
            if (valSpan.IndexOf('\r') < 0)
            {
                if (MemoryExtensions.Equals(NotesBuf.Span, valSpan, StringComparison.Ordinal)) return;
                NotesBuf.SetFromSpan(valSpan);
                OnPropertyChanged();
                return;
            }

            // WinUI 3's multiline TextBox (AcceptsReturn="True") inserts a bare CR on Enter, not
            // CRLF/LF. Normalize to LF at the point of entry so exported JSON/CSV are consistent.
            // Pre-existing DB records that already contain a bare CR are left untouched (掟①: no
            // silent rewrite of stored data).
            var normalized = GC.AllocateArray<char>(valSpan.Length, pinned: true);
            try
            {
                int len = NormalizeLineEndings(valSpan, normalized);
                var normSpan = normalized.AsSpan(0, len);
                if (MemoryExtensions.Equals(NotesBuf.Span, normSpan, StringComparison.Ordinal)) return;
                NotesBuf.SetFromSpan(normSpan);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(normalized.AsSpan()));
            }
            OnPropertyChanged();
        }
    }

    // Collapses CR and CRLF to LF. dst must be at least src.Length long (output is never longer than input).
    // Internal (not private) so VaultImportExportHelper.BuildSecret can apply the same normalization
    // to imported Notes text - without it, an imported value containing \r\n round-trips through the
    // Notes TextBox's own CR-only internal representation on first view and mismatches the raw Gen0
    // baseline, spuriously flagging the item as dirty and autosaving a no-op draft.
    internal static int NormalizeLineEndings(ReadOnlySpan<char> src, Span<char> dst)
    {
        int len = 0;
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            if (c == '\r')
            {
                dst[len++] = '\n';
                if (i + 1 < src.Length && src[i + 1] == '\n') i++;
            }
            else
            {
                dst[len++] = c;
            }
        }
        return len;
    }

    [ObservableProperty] public partial bool IsFavorite { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreatedAtDisplay))]
    public partial DateTime CreatedAt { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatedAtDisplay))]
    public partial DateTime UpdatedAt { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExpiry))]
    public partial DateTimeOffset? ExpiresAt { get; set; }
    public bool HasExpiry => ExpiresAt.HasValue;
    [ObservableProperty] public partial bool HasExpiryAlert { get; set; }
    [ObservableProperty] public partial bool IsExpired { get; set; }

    partial void OnExpiresAtChanged(DateTimeOffset? value) => UpdateExpiryAlert();

    private void UpdateExpiryAlert()
    {
        if (!ExpiresAt.HasValue) { HasExpiryAlert = false; IsExpired = false; return; }
        var expiry = ExpiresAt.Value.Date;
        HasExpiryAlert = expiry < DateTime.Today.AddDays(AppConstants.SecretPasswordWarnDays);
        IsExpired      = expiry < DateTime.Today;
    }

    /// <summary>Symbol set specific to this item (persisted per item). Empty → Secure by Default auto-reinjects DefaultSymbols.</summary>
    [ObservableProperty] public partial string GenSymbols { get; set; } = PasswordGenerator.DefaultSymbols;

    partial void OnGenSymbolsChanged(string value)
    {
        var filtered = new string(value.Where(c => PasswordGenerator.AllowedSymbols.Contains(c)).ToArray());
        if (filtered != value) { GenSymbols = filtered; return; }
        if (string.IsNullOrEmpty(filtered)) GenSymbols = PasswordGenerator.DefaultSymbols;
    }

    public string CreatedAtDisplay => CreatedAt != default ? CreatedAt.ToString("yyyy/MM/dd HH:mm") : string.Empty;
    public string UpdatedAtDisplay => UpdatedAt != default ? UpdatedAt.ToString("yyyy/MM/dd HH:mm") : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGen1Date))]
    public partial string Gen1Date { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGen2Date))]
    public partial string Gen2Date { get; set; } = string.Empty;

    public bool HasGen1Date => !string.IsNullOrEmpty(Gen1Date);
    public bool HasGen2Date => !string.IsNullOrEmpty(Gen2Date);

    /// <summary>
    /// TOTP secret (Base32 plaintext) only. Setting this uses RFC 6238 defaults (6 digits / 30s / SHA1);
    /// use <see cref="SetTotpConfig"/> to persist a full otpauth:// configuration (custom digits/period/algorithm).
    /// null = 2FA not configured.
    /// </summary>
    public string? TotpSecret
    {
        get => TotpSecretBuf.IsEmpty ? null : TotpCalculator.TryUnpackToConfig(TotpSecretBuf.Span)?.Secret;
        set => SetTotpConfig(string.IsNullOrEmpty(value) ? null : new TotpCalculator.TotpConfig(value));
    }

    /// <summary>Sets the full TOTP configuration (secret + digits/period/algorithm). null clears 2FA.</summary>
    internal void SetTotpConfig(TotpCalculator.TotpConfig? config)
    {
        TotpSecretBuf.SetFromSpan(config == null ? ReadOnlySpan<char>.Empty : TotpCalculator.Pack(config).AsSpan());
        OnPropertyChanged(nameof(TotpSecret));
        OnPropertyChanged(nameof(HasTotp));
        OnPropertyChanged(nameof(CanTotpToggle));
    }

    public bool HasTotp => !TotpSecretBuf.IsEmpty;
    public bool CanTotpToggle => HasTotp || (!TitleBuf.IsEmpty && !UserIdBuf.IsEmpty && !PasswordBuf.IsEmpty);

    [ObservableProperty] public partial string LabelTitle { get; set; }    = LocalizationManager.Get("Common.Title");
    [ObservableProperty] public partial string LabelUserId { get; set; }   = LocalizationManager.Get("Common.Username");
    [ObservableProperty] public partial string LabelPassword { get; set; } = LocalizationManager.Get("Common.Password");
    [ObservableProperty] public partial string LabelUrl { get; set; }      = LocalizationManager.Get("Common.Website");
    [ObservableProperty] public partial string LabelEmail { get; set; } = LocalizationManager.Get("Common.Email");
    [ObservableProperty] public partial string LabelNotes { get; set; }    = LocalizationManager.Get("Common.Notes");

    public ObservableCollection<FileItem> AttachedFiles { get; } = [];
    public ObservableCollection<CustomFieldModel> CustomFields { get; } = [];

    public bool IsNew => Id == 0;

    // zxcvbn score cache (-1 = not yet evaluated). Updated only via SetStrengthScore().
    private int _cachedStrengthScore = -1;

    public int PasswordStrengthLevel => _cachedStrengthScore < 0 ? 0 : _cachedStrengthScore;

    public string PasswordStrengthText => PasswordStrengthLevel switch
    {
        0 => LocalizationManager.Get("Common.StrengthVeryWeak"),
        1 => LocalizationManager.Get("Common.StrengthWeak"),
        2 => LocalizationManager.Get("Common.StrengthFair"),
        3 => LocalizationManager.Get("Common.StrengthStrong"),
        4 => LocalizationManager.Get("Common.StrengthVeryStrong"),
        _ => string.Empty,
    };

    /// <summary>
    /// Returns true only when the buffer has data and the score has been evaluated.
    /// </summary>
    public bool HasPasswordStrength => !PasswordBuf.IsEmpty && _cachedStrengthScore >= 0;

    /// <summary>
    /// The single update point for strength, called from SecretsViewModel.
    /// Fires PropertyChanged for the score, text, and display flags all at once.
    /// </summary>
    internal void SetStrengthScore(int score)
    {
        _cachedStrengthScore = score;
        OnPropertyChanged(nameof(PasswordStrengthLevel));
        OnPropertyChanged(nameof(PasswordStrengthText));
        OnPropertyChanged(nameof(HasPasswordStrength));
    }

    public void Dispose()
    {
        PasswordBuf.Dispose();
        NotesBuf.Dispose();
        TotpSecretBuf.Dispose();
        TitleBuf.Dispose();
        UserIdBuf.Dispose();
        WebsiteBuf.Dispose();
        EmailBuf.Dispose();
        foreach (var cf in CustomFields) cf.Dispose();
        CustomFields.Clear();
        foreach (var f in AttachedFiles) f.Dispose();
        AttachedFiles.Clear();
    }

}

public partial class SecretsViewModel : ObservableObject, IUnsavedChangesGuard, IDisposable,
    NaimitsuVault.Services.Interfaces.ISessionSaveTracker
{
    private readonly ClipboardAutoEraser _clipboardEraser = new();
    private readonly SecretRepository _secrets;
    private readonly StoredFileRepository _storedFiles;
    private readonly ICryptoService _crypto;
    private readonly AppSession _session;
    private readonly ProfileService _profileService;
    private readonly IAppNotificationService _notification;
    private readonly IDialogService _dialog;
    private readonly FaviconService _favicon;
    private readonly SecretHistoryRepository _secretHistory;
    private readonly SecretDraftsRepository _secretDrafts;
    private readonly NaimitsuVault.Services.SessionLockGuard _lockGuard;
    private readonly IDispatcherService _dispatcher;
    private readonly IPasswordEvaluationService _evaluator;
    private readonly IAuditLogService _auditLog;
    private readonly AutoBackupService _autoBackup;

    // Codes currently defined by LocalizationManager.GetCategoryPresets() (+ 0 for Uncategorized),
    // captured once per LoadAsync(). A Secret.CategoryNum not in this set (e.g. a code left over
    // from before the 2026-08-17 preset renumbering) is treated as Uncategorized for filtering -
    // see EffectiveCategoryCode - without ever rewriting the stored value.
    private HashSet<int> _validCategoryCodes = [];

    private int EffectiveCategoryCode(int? categoryNum) =>
        categoryNum.HasValue && _validCategoryCodes.Contains(categoryNum.Value) ? categoryNum.Value : 0;

    internal Task _lastSelectionTask = Task.CompletedTask;
    internal Task WaitForSelectionTaskAsync() => _lastSelectionTask;

    // ── Lifecycle state ──────────────────────────────────────────────────
    public bool IsActive { get; private set; }
    public bool NeedsReload { get; set; }
    public void Resume() => IsActive = true;
    public void Pause()  => IsActive = false;

    /// <summary>The currently running long-running save task. Referenced by LockAsync via SessionTaskRegistry.</summary>
    public Task? CurrentSaveTask { get; private set; }

    private bool _isDirty;
    private bool _fileOnlyChanges = true;
    private HashSet<int> _gen0FileIds = [];
    private bool _isRebuildingFilter;
    private CancellationTokenSource? _selectionDelayCts;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    // Serializes SaveDraftCoreAsync's actual write critical section (distinct from _saveLock, which
    // guards AutoSaveOnNavigateAsync's own reentrancy and already calls SaveDraftAsync while held —
    // sharing one semaphore between the two would deadlock).
    private readonly SemaphoreSlim _saveDraftSemaphore = new(1, 1);
    // Coalesces overlapping SaveDraftAsync callers (fire-and-forget from LosingFocus vs. awaited from
    // the compare-draft button etc.): a new call cancels any earlier one still waiting on the
    // semaphore, so only the most recent request's (freshest) EditingSecret state actually gets saved.
    private CancellationTokenSource? _saveDraftCts;
    private readonly int _strengthDebounceMs = 300;
    private CancellationTokenSource? _strengthCts;
    private bool _suppressStrengthDebounce;
    internal Task? LastStrengthTask;
    private SecretEditModel? _subscribedModel;
    public bool HasUnsavedChanges => _isDirty;

    // ─── TOTP ────────────────────────────────────────────────────────────────
    [NotifyPropertyChangedFor(nameof(TotpCodeDisplay))]
    [ObservableProperty] public partial string TotpCode { get; set; } = string.Empty;
    [ObservableProperty] public partial int    TotpSecondsRemaining { get; set; } = 30;
    [ObservableProperty] public partial bool   TotpCodeIsExpiring { get; set; }
    [ObservableProperty] public partial string TotpAdjacentLabel { get; set; } = string.Empty;
    // Display-only grouping (e.g. "123 456"). Copy/clipboard must keep using the raw TotpCode -
    // see TotpCalculator.FormatCodeForDisplay.
    public string TotpCodeDisplay => TotpCalculator.FormatCodeForDisplay(TotpCode);
    public string TotpSecondsLabel => $"{TotpSecondsRemaining}s";
    public bool HasTotp => EditingSecret?.HasTotp == true;
    private DispatcherTimer? _totpTimer;

    public ObservableCollection<CategoryGroupViewModel> CategoryItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial object? SelectedListItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditingSecret))]
    public partial SecretEditModel? EditingSecret { get; set; }

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    public partial string SearchText { get; set; } = string.Empty;

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    // Notes is excluded from the search index (decrypted on demand upon selection).
    // Title/UserId/Url/Email are held in a SecureCharBuffer (POH-pinned).
    private sealed class SearchEntry : IDisposable
    {
        public int Id { get; }
        public int? CategoryNum { get; }
        public DateTime UpdatedAt { get; }
        public DateTime? ExpiresAt { get; }
        public string? WebsiteDomain { get; }  // domain only (non-sensitive)
        public bool IsFavorite { get; set; }   // Changed directly by the favorite toggle
        public bool HasDraft { get; set; }     // Changed directly by the draft flag

        private readonly SecureCharBuffer _titleBuf   = new();
        private readonly SecureCharBuffer _userIdBuf  = new();
        private readonly SecureCharBuffer _urlBuf     = new();
        private readonly SecureCharBuffer _emailBuf    = new();

        // For XAML / SecretListItemViewModel construction. Creates a temporary string on each access.
        public string DisplayTitle => _titleBuf.ToDisplayString();

        // For span-based search in ApplySearch (allocation-free)
        public ReadOnlySpan<char> TitleSpan   => _titleBuf.Span;
        public ReadOnlySpan<char> UserIdSpan  => _userIdBuf.Span;
        public ReadOnlySpan<char> UrlSpan     => _urlBuf.Span;
        public ReadOnlySpan<char> EmailSpan    => _emailBuf.Span;

        // Constructed from a UTF-8 byte span from LoadAsync / a messenger handler
        internal SearchEntry(int id, int? categoryNum,
            ReadOnlySpan<byte> titleUtf8, ReadOnlySpan<byte> userIdUtf8,
            ReadOnlySpan<byte> urlUtf8,   ReadOnlySpan<byte> emailUtf8,
            DateTime updateAt, bool isFavorite, DateTime? expiresAt,
            string? webSiteDomain, bool hasDraft)
        {
            Id = id; CategoryNum = categoryNum; UpdatedAt = updateAt;
            IsFavorite = isFavorite; ExpiresAt = expiresAt;
            WebsiteDomain = webSiteDomain; HasDraft = hasDraft;
            FieldCrypto.FillBufferFromUtf8(_titleBuf,  titleUtf8);
            FieldCrypto.FillBufferFromUtf8(_userIdBuf, userIdUtf8);
            FieldCrypto.FillBufferFromUtf8(_urlBuf,    urlUtf8);
            FieldCrypto.FillBufferFromUtf8(_emailBuf,   emailUtf8);
        }

        // Constructed from a char span from SaveSecretAsync / AddSecretAsync
        internal SearchEntry(int id, int? categoryNum,
            ReadOnlySpan<char> titleSpan, ReadOnlySpan<char> userIdSpan,
            ReadOnlySpan<char> urlSpan,   ReadOnlySpan<char> emailSpan,
            DateTime updateAt, bool isFavorite, DateTime? expiresAt,
            string? webSiteDomain, bool hasDraft)
        {
            Id = id; CategoryNum = categoryNum; UpdatedAt = updateAt;
            IsFavorite = isFavorite; ExpiresAt = expiresAt;
            WebsiteDomain = webSiteDomain; HasDraft = hasDraft;
            _titleBuf.SetFromSpan(titleSpan);
            _userIdBuf.SetFromSpan(userIdSpan);
            _urlBuf.SetFromSpan(urlSpan);
            _emailBuf.SetFromSpan(emailSpan);
        }

        public void Dispose()
        {
            _titleBuf.Dispose(); _userIdBuf.Dispose();
            _urlBuf.Dispose();   _emailBuf.Dispose();
        }
    }

    private List<SearchEntry> _searchIndex = [];

    // Persistent pool of display items keyed by secret Id, reused across ApplySearch rebuilds
    // (filter/search/category changes) so that UI-only state set after construction — chiefly
    // FaviconSource, which nothing re-populates once assigned — survives a filter toggle instead of
    // being discarded along with a freshly `new`'d instance. Mirrors TimeMachineViewModel's
    // AllSecrets/FilteredSecrets split, where FilteredSecrets re-adds the same object references.
    private readonly Dictionary<int, SecretListItemViewModel> _itemPool = new();
    private readonly ILogger<SecretsViewModel> _logger;

    // Returns the pooled display item for this SearchEntry, creating it on first sight and otherwise
    // refreshing its mutable fields in place. FaviconSource is deliberately left untouched here — it's
    // populated separately by LoadFaviconsAsync/PrefetchAndUpdateNodeAsync and must persist across calls.
    private SecretListItemViewModel GetOrCreatePoolItem(SearchEntry s)
    {
        if (_itemPool.TryGetValue(s.Id, out var item))
        {
            item.Title         = s.DisplayTitle;
            item.CategoryNum   = s.CategoryNum;
            item.UpdatedAt      = s.UpdatedAt;
            item.IsFavorite    = s.IsFavorite;
            item.ExpiresAt     = s.ExpiresAt;
            item.WebsiteDomain = s.WebsiteDomain;
            item.HasDraft      = s.HasDraft;
            return item;
        }
        item = new SecretListItemViewModel
        {
            Id = s.Id, Title = s.DisplayTitle, CategoryNum = s.CategoryNum, UpdatedAt = s.UpdatedAt,
            IsFavorite = s.IsFavorite, ExpiresAt = s.ExpiresAt, WebsiteDomain = s.WebsiteDomain, HasDraft = s.HasDraft,
        };
        _itemPool[s.Id] = item;
        return item;
    }

    // Same pool as GetOrCreatePoolItem(SearchEntry) above, but keyed off the edit model used by
    // RefreshListItem (AddSecretAsync/SaveSecretCoreAsync) instead of a rebuilt SearchEntry. Routing
    // RefreshListItem's "not yet in the list" branches through the shared pool (rather than `new`ing an
    // unregistered instance) is required for PrefetchAndUpdateNodeAsync's post-save _itemPool.TryGetValue
    // lookup to find the node - otherwise a freshly added/edited secret's favicon fetches into
    // FaviconCache correctly but never reaches the bound UI item until the next full LoadAsync.
    private SecretListItemViewModel GetOrCreatePoolItem(SecretEditModel m, string? domain)
    {
        // Title must be a fresh copy, never m.Title itself: that getter returns TitleBuf's cached
        // display string, and m (a SecretEditModel) gets Dispose()'d whenever selection moves on -
        // which zeroes that exact string in place (SecureCharBuffer.Dispose -> ZeroStringInternals).
        // This item's Title lives on in FilteredSecrets/CategoryItems long after m is gone, so sharing
        // the reference would blank or corrupt the list entry out from under the still-visible ListView.
        var freshTitle = new string(m.TitleBuf.Span);
        if (_itemPool.TryGetValue(m.Id, out var item))
        {
            item.Title       = freshTitle;
            item.CategoryNum = m.CategoryNum;
            item.IsFavorite  = m.IsFavorite;
            item.ExpiresAt   = m.ExpiresAt?.DateTime;
            if (item.WebsiteDomain != domain)
            {
                item.WebsiteDomain = domain;
                item.FaviconSource = null;
            }
            return item;
        }
        item = new SecretListItemViewModel
        {
            Id = m.Id, Title = freshTitle, CategoryNum = m.CategoryNum,
            IsFavorite = m.IsFavorite, ExpiresAt = m.ExpiresAt?.DateTime, WebsiteDomain = domain,
        };
        _itemPool[m.Id] = item;
        return item;
    }

    // Password generator UI state (non-persistent; always reset on panel expand / item switch)
    [ObservableProperty] public partial int  PwdLength    { get; set; } = 20;
    [ObservableProperty] public partial bool PwdUseUpper  { get; set; } = true;
    [ObservableProperty] public partial bool PwdUseLower  { get; set; } = true;
    [ObservableProperty] public partial bool PwdUseDigits { get; set; } = true;
    [ObservableProperty] public partial bool PwdUseSymbols { get; set; } = true;

    [ObservableProperty] public partial bool CanGeneratePassword { get; set; } = true;

    private void UpdateCanGeneratePassword()
    {
        CanGeneratePassword = PwdUseUpper || PwdUseLower || PwdUseDigits || PwdUseSymbols;
        GeneratePasswordCommand.NotifyCanExecuteChanged();
    }

    partial void OnPwdUseUpperChanged(bool value)   => UpdateCanGeneratePassword();
    partial void OnPwdUseLowerChanged(bool value)   => UpdateCanGeneratePassword();
    partial void OnPwdUseDigitsChanged(bool value)  => UpdateCanGeneratePassword();
    partial void OnPwdUseSymbolsChanged(bool value) => UpdateCanGeneratePassword();

    partial void OnPwdLengthChanged(int value)
    {
        if (value < 8) PwdLength = 8;
        else if (value > 64) PwdLength = 64;
    }

    internal void ResetGeneratorDefaults()
    {
        PwdLength    = 20;
        PwdUseUpper  = true;
        PwdUseLower  = true;
        PwdUseDigits = true;
        PwdUseSymbols = true;
    }

    public bool HasSelection => SelectedListItem != null;
    public bool HasEditingSecret => EditingSecret != null;
    public string ExpiryDateTooltip => string.Format(LocalizationManager.Get("Common.ExpiryAlarmNotice"), AppConstants.SecretPasswordWarnDays);

    public ObservableCollection<CategoryGroupViewModel> CategoryComboItems { get; } = [];
    public ObservableCollection<FilterCategoryItem> FilterComboItems { get; } = [];
    public ObservableCollection<SecretListItemViewModel> FilteredSecrets { get; } = [];

    [ObservableProperty] public partial string SecretCountLabel { get; set; } = string.Empty;
    [NotifyPropertyChangedFor(nameof(HasAnySecrets))]
    [ObservableProperty] public partial string TotalCountLabel { get; set; } = "0";
    private int _totalCount;

    // Drives disabling the search box / category filter when there is nothing to search or filter
    // yet (e.g. right after first launch) - see SecretsPage.xaml.
    public bool HasAnySecrets => _totalCount > 0;

    [ObservableProperty] public partial bool HasExpiryAlert { get; set; }
    [ObservableProperty] public partial int  ExpiryAlertCount { get; set; }
    // Unlike HasExpiryAlert/ExpiryAlertCount (expired + expiring-soon combined), this is true only
    // when at least one secret is already past due - drives ShellWindow's title bar red-vs-gold.
    [ObservableProperty] public partial bool HasExpiredPastDue { get; set; }

    private void UpdateExpiryAlert()
    {
        var today = DateTime.Today;
        int expired      = _searchIndex.Count(e => e.ExpiresAt.HasValue && e.ExpiresAt.Value.Date < today);
        int expiringSoon = _searchIndex.Count(e => e.ExpiresAt.HasValue && e.ExpiresAt.Value.Date >= today && e.ExpiresAt.Value.Date < today.AddDays(AppConstants.SecretPasswordWarnDays));
        ExpiryAlertCount  = expired + expiringSoon;
        HasExpiryAlert    = ExpiryAlertCount > 0;
        HasExpiredPastDue = expired > 0;
    }

    private void UpdateCountLabel()
    {
        var filtered = FilteredSecrets.Count;
        SecretCountLabel = filtered == _totalCount
            ? $"{_totalCount}"
            : $"{filtered} / {_totalCount}";
        TotalCountLabel = $"{_totalCount}";
    }

    [ObservableProperty] public partial int? FilterCategoryCode { get; set; }
    partial void OnFilterCategoryCodeChanged(int? value) => _ = HandleFilterCategoryChangeAsync(value);

    [ObservableProperty] public partial bool FilterFavoritesOnly { get; set; }
    partial void OnFilterFavoritesOnlyChanged(bool value)
    {
        var prevId = (SelectedListItem as SecretListItemViewModel)?.Id;
        ApplySearch(SearchText);
        SelectedListItem = prevId.HasValue ? FilteredSecrets.FirstOrDefault(n => n.Id == prevId) : null;
    }

    [ObservableProperty] public partial bool FilterExpiredOnly { get; set; }
    partial void OnFilterExpiredOnlyChanged(bool value)
    {
        var prevId = (SelectedListItem as SecretListItemViewModel)?.Id;
        ApplySearch(SearchText);
        SelectedListItem = prevId.HasValue ? FilteredSecrets.FirstOrDefault(n => n.Id == prevId) : null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilteredFavorites))]
    public partial int FilteredFavoritesCount { get; set; }
    public bool HasFilteredFavorites => FilteredFavoritesCount > 0;
    partial void OnFilteredFavoritesCountChanged(int value)
    {
        if (value == 0 && FilterFavoritesOnly)
            FilterFavoritesOnly = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilteredExpired))]
    public partial int FilteredExpiredCount { get; set; }
    public bool HasFilteredExpired => FilteredExpiredCount > 0;
    partial void OnFilteredExpiredCountChanged(int value)
    {
        if (value == 0 && FilterExpiredOnly)
            FilterExpiredOnly = false;
    }

    // Whether FilteredExpiredCount includes at least one secret that's actually past due (not just
    // within the warning window) - drives the expiry filter button's red-vs-gold color, same
    // "isExpired takes precedence" rule as GetExpiryAlertForeground for the form's own expiry label.
    [ObservableProperty] public partial bool HasFilteredExpiredPastDue { get; set; }


    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSecretCommand))]
    public partial bool EditingSecretHasDraft { get; set; }

    [RelayCommand]
    private async Task DiscardCurrentDraftAsync()
    {
        if (EditingSecret == null) return;
        var id = EditingSecret.Id;
        await DiscardDraftGuardedAsync(id);
        // Update this first since LoadSecretAsync references _searchIndex
        var si = _searchIndex.FindIndex(s => s.Id == id);
        if (si >= 0) _searchIndex[si].HasDraft = false;
        var flatNode = FilteredSecrets.FirstOrDefault(n => n.Id == id);
        if (flatNode != null) flatNode.HasDraft = false;
        FilteredDraftCount = FilteredSecrets.Count(n => n.HasDraft);
        // Restore the right pane by overwriting it with Gen0
        await LoadSecretAsync(id);
    }

    [ObservableProperty] public partial bool FilterDraftOnly { get; set; }
    partial void OnFilterDraftOnlyChanged(bool value)
    {
        var prevId = (SelectedListItem as SecretListItemViewModel)?.Id;
        ApplySearch(SearchText);
        SelectedListItem = prevId.HasValue ? FilteredSecrets.FirstOrDefault(n => n.Id == prevId) : null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilteredDraft))]
    public partial int FilteredDraftCount { get; set; }
    public bool HasFilteredDraft => FilteredDraftCount > 0;
    partial void OnFilteredDraftCountChanged(int value)
    {
        if (value == 0 && FilterDraftOnly)
            FilterDraftOnly = false;
    }

    public SecretsViewModel(
        SecretRepository secrets,
        StoredFileRepository storedFiles,
        ICryptoService crypto,
        AppSession session,
        ProfileService profileService,
        IAppNotificationService notification,
        IDialogService dialog,
        FaviconService favicon,
        SecretHistoryRepository secretHistory,
        SecretDraftsRepository secretDrafts,
        NaimitsuVault.Services.SessionLockGuard lockGuard,
        NaimitsuVault.Services.SessionTaskRegistry taskRegistry,
        IDispatcherService dispatcher,
        IPasswordEvaluationService evaluator,
        IAuditLogService auditLog,
        AutoBackupService autoBackup,
        ILogger<SecretsViewModel> logger,
        int strengthDebounceMs = 300)
    {
        _logger = logger;
        _strengthDebounceMs = strengthDebounceMs;
        _auditLog = auditLog;
        _autoBackup = autoBackup;
        _secrets = secrets;
        _storedFiles = storedFiles;
        _crypto = crypto;
        _session = session;
        _profileService = profileService;
        _notification = notification;
        _dialog = dialog;
        _favicon = favicon;
        _secretHistory = secretHistory;
        _secretDrafts = secretDrafts;
        _lockGuard = lockGuard;
        _dispatcher = dispatcher;
        _evaluator = evaluator;
        taskRegistry.Register(this);   // Rev7: self-registers upon instantiation (uninstantiated VMs are never registered)

        // Refresh the attached files list when a link is removed in the viewer.
        // Deliberately NOT gated on IsActive: EditingSecret stays live (and auto-save-eligible via
        // AutoSaveOnNavigateAsync/SaveDraftAsync) even while the Secrets page isn't the visible tab.
        // Skipping this while inactive left EditingSecret.AttachedFiles stale, so a later auto-save's
        // "semantically identical to Gen0" check silently discarded the very draft write the viewer
        // toggle had just made (Gen0 and the stale snapshot both still had the file).
        WeakReferenceMessenger.Default.Register<FileLinkChangedMessage>(this, (_, msg) =>
        {
            if (EditingSecret?.AttachedFiles.Any(i => i.Id == msg.FileId) == true)
                _pendingFileLinkRefreshTask = RefreshAttachedFilesAsync(EditingSecret.Id);
        });

        // Pinpoint-update the HasDraft flag after a link toggle (draft write) in the viewer
        WeakReferenceMessenger.Default.Register<StorageChangedMessage>(this, (_, _) =>
        {
            if (!IsActive) { NeedsReload = true; return; }
            _pendingDraftFlagsRefreshTask = RefreshDraftFlagsAsync();
        });

        // Fully reload the list and count after a plaintext import completes (favicon fetching is handled inside LoadAsync)
        WeakReferenceMessenger.Default.Register<SecretsImportedMessage>(this, (_, _) =>
        {
            if (!IsActive) { NeedsReload = true; return; }
            _ = LoadAsync();
        });

        // Add to the list when a deleted secret is restored from Time Machine
        WeakReferenceMessenger.Default.Register<SecretRestoredMessage>(this, (_, msg) =>
        {
            if (!IsActive) { NeedsReload = true; return; }
            _ = HandleSecretRestoredAsync(msg);
        });

        // Pinpoint-sync when a Time Machine restore rewrites data for an existing (non-deleted) secret
        WeakReferenceMessenger.Default.Register<SecretDataUpdatedMessage>(this, (_, msg) =>
        {
            if (!IsActive) { NeedsReload = true; return; }
            _ = HandleSecretDataUpdatedAsync(msg);
        });

        // Turning the favicon setting ON should fetch immediately for the already-loaded list, not
        // wait for the next LoadAsync (app restart) - PrefetchAllFaviconsAsync only reads _searchIndex,
        // which LoadAsync has already populated by the time this page can be active.
        WeakReferenceMessenger.Default.Register<FaviconAutoFetchToggledMessage>(this, (_, msg) =>
        {
            if (!msg.Enabled) return;
            if (!IsActive) { NeedsReload = true; return; }
            _ = PrefetchAllFaviconsAsync();
        });
    }

    private Task? _pendingFileLinkRefreshTask;
    private Task? _pendingDraftFlagsRefreshTask;

    /// <summary>
    /// Test-only hook: awaits the fire-and-forget refreshes triggered by FileLinkChangedMessage/
    /// StorageChangedMessage (a viewer-side link toggle), if any are currently pending.
    /// </summary>
    internal async Task WaitForPendingLinkRefreshesAsync()
    {
        if (_pendingFileLinkRefreshTask   != null) await _pendingFileLinkRefreshTask;
        if (_pendingDraftFlagsRefreshTask != null) await _pendingDraftFlagsRefreshTask;
    }

    // ── Message handler delegate methods ──────────────────────────────────────

    private async Task HandleSecretRestoredAsync(SecretRestoredMessage msg)
    {
        if (!IsActive) return;
        var s = await _secrets.GetByIdAsync(msg.SecretId);
        if (s == null || !IsActive) return;
        var key = _session.GetKey();
        SearchEntry newEntry;
        using (var titleSp1 = FieldCrypto.Open(s.Title,    _crypto, key))
        using (var wsSp1    = FieldCrypto.Open(s.Website,  _crypto, key))
        using (var userSp1  = FieldCrypto.Open(s.UserId,   _crypto, key))
        using (var emailSp1  = FieldCrypto.Open(s.Email, _crypto, key))
        {
            var domain = wsSp1 != null ? ExtractDomainFromUtf8(wsSp1.Utf8) : null;
            newEntry = new SearchEntry(
                s.Id, s.CategoryNum,
                titleSp1 != null ? titleSp1.Utf8 : ReadOnlySpan<byte>.Empty,
                userSp1  != null ? userSp1.Utf8  : ReadOnlySpan<byte>.Empty,
                wsSp1    != null ? wsSp1.Utf8    : ReadOnlySpan<byte>.Empty,
                emailSp1  != null ? emailSp1.Utf8  : ReadOnlySpan<byte>.Empty,
                s.UpdatedAt.ToLocalTime(),
                s.IsFavorite,
                s.ExpiresAt?.ToLocalTime(),
                domain,
                hasDraft: false);
        }
        if (!IsActive) { newEntry.Dispose(); return; }
        _totalCount++;
        _searchIndex.Add(newEntry);
        await _dispatcher.EnqueueAsync(() =>
        {
            if (!IsActive) return Task.CompletedTask;
            ApplySearch(SearchText);  // Runs the ObservableCollection's Add/Clear on the UI thread
            return Task.CompletedTask;
        });
    }

    private async Task HandleSecretDataUpdatedAsync(SecretDataUpdatedMessage msg)
    {
        if (!IsActive) return;
        var s = await _secrets.GetByIdAsync(msg.SecretId);
        if (s == null || !IsActive) return;
        var key = _session.GetKey();
        string? domain;
        SearchEntry newEntry;
        using (var titleSp2 = FieldCrypto.Open(s.Title,    _crypto, key))
        using (var wsSp2    = FieldCrypto.Open(s.Website,  _crypto, key))
        using (var userSp2  = FieldCrypto.Open(s.UserId,   _crypto, key))
        using (var emailSp2  = FieldCrypto.Open(s.Email, _crypto, key))
        {
            domain   = wsSp2 != null ? ExtractDomainFromUtf8(wsSp2.Utf8) : null;
            newEntry = new SearchEntry(
                s.Id, s.CategoryNum,
                titleSp2 != null ? titleSp2.Utf8 : ReadOnlySpan<byte>.Empty,
                userSp2  != null ? userSp2.Utf8  : ReadOnlySpan<byte>.Empty,
                wsSp2    != null ? wsSp2.Utf8    : ReadOnlySpan<byte>.Empty,
                emailSp2  != null ? emailSp2.Utf8  : ReadOnlySpan<byte>.Empty,
                s.UpdatedAt.ToLocalTime(),
                s.IsFavorite,
                s.ExpiresAt?.ToLocalTime(),
                domain,
                hasDraft: false);
        }
        if (!IsActive) { newEntry.Dispose(); return; }

        // Dispose the existing entry in _searchIndex and pinpoint-replace it (safe on ThreadPool)
        var si = _searchIndex.FindIndex(e => e.Id == msg.SecretId);
        if (si >= 0)
        {
            var oldEntry = _searchIndex[si];
            _searchIndex[si] = newEntry;
            oldEntry.Dispose();
        }
        else
        {
            newEntry.Dispose();
        }

        // UI update (property setters are thread-agnostic: see Appendix B)
        if (!IsActive) return;
        var flatNode = FilteredSecrets.FirstOrDefault(n => n.Id == msg.SecretId);
        if (flatNode != null && si >= 0)
        {
            flatNode.Title         = _searchIndex[si].DisplayTitle;
            flatNode.IsFavorite    = s.IsFavorite;
            flatNode.ExpiresAt     = s.ExpiresAt?.ToLocalTime();
            flatNode.WebsiteDomain = domain;
            flatNode.UpdatedAt      = s.UpdatedAt.ToLocalTime();
        }

        foreach (var catNode in CategoryItems)
        {
            var treeNode = catNode.Secrets.FirstOrDefault(x => x.Id == msg.SecretId);
            if (treeNode != null && si >= 0)
            {
                treeNode.Title         = _searchIndex[si].DisplayTitle;
                treeNode.ExpiresAt     = s.ExpiresAt?.ToLocalTime();
                treeNode.WebsiteDomain = domain;
                break;
            }
        }

        UpdateExpiryAlert();
        var todayM = DateTime.Today;
        var warnM  = todayM.AddDays(AppConstants.SecretPasswordWarnDays);
        FilteredExpiredCount     = FilteredSecrets.Count(x => x.ExpiresAt.HasValue && x.ExpiresAt.Value.Date < warnM);
        HasFilteredExpiredPastDue = FilteredSecrets.Any(x => x.ExpiresAt.HasValue && x.ExpiresAt.Value.Date < todayM);
        FilteredFavoritesCount   = FilteredSecrets.Count(x => x.IsFavorite);
        FilteredDraftCount       = FilteredSecrets.Count(x => x.HasDraft);
    }

    /// <summary>
    /// Called when the scope is Disposed on lock.
    /// EditingSecret = null → OnEditingSecretChanging runs oldValue.Dispose(), ZeroMemory-ing the SecureCharBuffer.
    /// Also Disposes every SearchEntry in _searchIndex, physically wiping pinned buffers such as plaintext titles.
    /// </summary>
    public void Dispose()
    {
        _clipboardEraser.Dispose();
        _selectionDelayCts?.Cancel();
        _selectionDelayCts?.Dispose();
        _selectionDelayCts = null;
        _strengthCts?.Cancel();
        _strengthCts?.Dispose();
        _strengthCts = null;
        _saveLock.Dispose();
        EditingSecret    = null;   // → OnEditingSecretChanging → oldValue.Dispose() + SubscribeEditingSecret(null)
        SelectedListItem = null;

        foreach (var e in _searchIndex) e.Dispose();
        _searchIndex = [];
        _itemPool.Clear();

        CategoryItems.Clear();
        FilteredSecrets.Clear();
        CategoryComboItems.Clear();
        FilterComboItems.Clear();

        _totalCount = 0;
        _isDirty    = false;

        WeakReferenceMessenger.Default.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Fetches the latest set of SecretIds with a draft and pinpoint-updates only the HasDraft flag.
    /// Call this when a full LoadAsync rerun isn't needed, e.g. after a link toggle in the viewer.
    /// </summary>
    private async Task RefreshDraftFlagsAsync()
    {
        try
        {
            if (!IsActive) return;
            var draftIds = await _secretDrafts.GetAllDraftIdsAsync();  // ThreadPool
            if (!IsActive) return;

            // Sync _searchIndex's HasDraft to the latest state (List operation: thread-safe)
            foreach (var entry in _searchIndex)
                entry.HasDraft = draftIds.Contains(entry.Id);

            // UI property updates happen inside EnqueueAsync (runs ObservableCollection property changes on the UI thread)
            await _dispatcher.EnqueueAsync(() =>
            {
                if (!IsActive) return Task.CompletedTask;
                foreach (var node in FilteredSecrets)
                    node.HasDraft = draftIds.Contains(node.Id);
                foreach (var catNode in CategoryItems)
                    foreach (var secret in catNode.Secrets)
                        secret.HasDraft = draftIds.Contains(secret.Id);
                FilteredDraftCount = FilteredSecrets.Count(n => n.HasDraft);
                if (EditingSecret != null)
                    EditingSecretHasDraft = draftIds.Contains(EditingSecret.Id);
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("RefreshDraftFlagsAsync: failed to update HasDraft. [{ExType}]", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Resolves the effective attached-file IDs for a secret: the draft (if present) takes priority
    /// over Gen0, mirroring LoadSecretAsync's own draft-first precedence. Without this, a link
    /// toggled in the viewer (which only ever writes the draft) would appear unchanged here because
    /// Gen0's SecretFileLinks table was never touched.
    /// </summary>
    private async Task<int[]> GetEffectiveFileIdsAsync(int secretId, DekScope key)
    {
        var (draftEncBlob, _) = await _secretDrafts.GetDraftAsync(secretId);
        if (draftEncBlob != null)
        {
            int draftPlainLen = draftEncBlob.Length - ICryptoService.AeadOverhead;
            if (draftPlainLen > 0)
            {
                var draftPlain = GC.AllocateArray<byte>(draftPlainLen, pinned: true);
                try
                {
                    _crypto.Decrypt(draftEncBlob, key.Span, draftPlain);
                    var slot = SnapshotSerializer.ReadToSlot(draftPlain.AsSpan());
                    try
                    {
                        if (slot.SecretId != 0) return slot.FileIds ?? [];
                    }
                    finally { slot?.Dispose(); }
                }
                catch { /* Fall back to Gen0 for a corrupted snapshot */ }
                finally { CryptographicOperations.ZeroMemory(draftPlain.AsSpan()); }
            }
        }
        return (await _secrets.GetFileLinksAsync(secretId)).ToArray();
    }

    // Deliberately not gated on IsActive: EditingSecret stays live (and auto-save-eligible) even
    // while the Secrets page isn't the visible tab, so its attached-file list must stay in sync
    // regardless of page visibility (see the FileLinkChangedMessage registration for why).
    // Invoked from a FileLinkChangedMessage handler as fire-and-forget (_pendingFileLinkRefreshTask
    // is only ever awaited by the test-only WaitForPendingLinkRefreshesAsync hook) - a decrypt/DB
    // failure here must not become a silent UnobservedTaskException that leaves AttachedFiles stale.
    private async Task RefreshAttachedFilesAsync(int secretId)
    {
        try
        {
            if (EditingSecret == null || EditingSecret.Id != secretId) return;

            var key = _session.GetKey();
            var imageIds = await GetEffectiveFileIdsAsync(secretId, key);  // ThreadPool
            if (EditingSecret == null || EditingSecret.Id != secretId) return;

            if (imageIds.Length == 0)
            {
                await _dispatcher.EnqueueAsync(() =>
                {
                    var old = EditingSecret?.AttachedFiles.ToList();
                    EditingSecret?.AttachedFiles.Clear();
                    if (old != null) foreach (var f in old) f.Dispose();
                    MarkHasDraft(secretId);
                    return Task.CompletedTask;
                });
                return;
            }

            var imgs = await _storedFiles.GetByIdsAsync(imageIds, key);  // ThreadPool
            if (EditingSecret == null || EditingSecret.Id != secretId) return;

            List<FileItem> items;
            try
            {
                items = imgs.Select(img =>
                {
                    var thumb = DecryptThumbnail(img.ThumbnailBlob, key);
                    var fi = new FileItem { Id = img.Id, ContentType = img.ContentTypeCode, ThumbnailData = thumb };
                    fi.SetFileNameFromUtf8(img.FileName);
                    return fi;
                }).ToList();
            }
            finally
            {
                // Immediately wipe the plaintext filename array that StoredFileRepository allocates on each decrypt
                foreach (var img in imgs) CryptographicOperations.ZeroMemory(img.FileName);
            }

            await _dispatcher.EnqueueAsync(() =>
            {
                if (EditingSecret == null || EditingSecret.Id != secretId)
                    return Task.CompletedTask;
                var old = EditingSecret.AttachedFiles.ToList();
                EditingSecret.AttachedFiles.Clear();
                foreach (var o in old) o.Dispose();
                foreach (var fi in items) EditingSecret.AttachedFiles.Add(fi);
                MarkHasDraft(secretId);
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[RefreshAttachedFilesAsync] Failed to refresh attached files. [{ExType}]", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Turns on the pencil (HasDraft) flag for the given secret immediately, synchronously with the
    /// attached-file refresh. Reaching here (via RefreshAttachedFilesAsync) always means a link toggle
    /// in the viewer just wrote a fresh draft for this secret, so HasDraft=true is always correct -
    /// no need to wait for the separate StorageChangedMessage-driven RefreshDraftFlagsAsync full scan.
    /// Must be called on the UI thread (i.e. from inside _dispatcher.EnqueueAsync).
    /// </summary>
    private void MarkHasDraft(int secretId)
    {
        var si = _searchIndex.FindIndex(s => s.Id == secretId);
        if (si >= 0) _searchIndex[si].HasDraft = true;
        var flatNode = FilteredSecrets.FirstOrDefault(n => n.Id == secretId);
        if (flatNode != null) flatNode.HasDraft = true;
        foreach (var catNode in CategoryItems)
        {
            var catSecret = catNode.Secrets.FirstOrDefault(n => n.Id == secretId);
            if (catSecret != null) catSecret.HasDraft = true;
        }
        FilteredDraftCount = FilteredSecrets.Count(n => n.HasDraft);
        if (EditingSecret != null && EditingSecret.Id == secretId)
            EditingSecretHasDraft = true;
    }

    partial void OnEditingSecretChanging(SecretEditModel? oldValue, SecretEditModel? newValue)
    {
        _strengthCts?.Cancel();
        oldValue?.Dispose();
    }

    partial void OnEditingSecretChanged(SecretEditModel? value)
    {
        _logger.LogInformation("[EditingSecret] → Id={Id}", value == null ? "null" : value.Id);
        SubscribeEditingSecret(value);
        RestartTotpTimer(value?.TotpSecretBuf);
        OnPropertyChanged(nameof(HasTotp));
    }

    private void SubscribeEditingSecret(SecretEditModel? model)
    {
        if (_subscribedModel != null)
        {
            _subscribedModel.PropertyChanged -= OnEditingModelPropertyChanged;
            _subscribedModel.CustomFields.CollectionChanged -= OnEditingModelCollectionChanged;
            foreach (var cf in _subscribedModel.CustomFields)
                cf.PropertyChanged -= OnCustomFieldPropertyChanged;
        }
        _subscribedModel = model;
        _isDirty = false;
        _fileOnlyChanges = true;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        if (model != null)
        {
            model.PropertyChanged += OnEditingModelPropertyChanged;
            model.CustomFields.CollectionChanged += OnEditingModelCollectionChanged;
            foreach (var cf in model.CustomFields)
                cf.PropertyChanged += OnCustomFieldPropertyChanged;
        }
    }

    private void OnEditingModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Computed/display-only properties re-notified by SetStrengthScore() (called right after
        // LoadSecretAsync subscribes, to evaluate an already-loaded password's strength) are not user
        // edits and must not mark the model dirty - otherwise opening any secret with a password
        // already set flips HasUnsavedChanges before any interaction, and the very next focus-out on
        // any field silently creates a draft.
        if (e.PropertyName is nameof(SecretEditModel.PasswordStrengthLevel)
            or nameof(SecretEditModel.PasswordStrengthText)
            or nameof(SecretEditModel.HasPasswordStrength))
            return;

        _isDirty = true;
        _fileOnlyChanges = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        if (e.PropertyName == nameof(SecretEditModel.TotpSecret))
        {
            OnPropertyChanged(nameof(HasTotp));
            RestartTotpTimer(EditingSecret?.TotpSecretBuf);
        }
        if (e.PropertyName == nameof(SecretEditModel.Password) && !_suppressStrengthDebounce)
            ScheduleStrengthDebounce();
    }

    private void ScheduleStrengthDebounce()
    {
        _strengthCts?.Cancel();
        _strengthCts?.Dispose();
        var cts = new CancellationTokenSource();
        _strengthCts = cts;
        var em = EditingSecret;
        LastStrengthTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_strengthDebounceMs, cts.Token);
                if (em == null || em.PasswordBuf.IsEmpty) return;
                var score = _evaluator.EvaluateStrength(em.PasswordBuf.Span);
                await _dispatcher.EnqueueAsync(() =>
                {
                    if (!cts.IsCancellationRequested && em == EditingSecret) em.SetStrengthScore(score);
                    return Task.CompletedTask;
                });
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    private void OnEditingModelCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
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
            // new position. Reacting to it as a real deletion (dirtying the model / autosaving a
            // draft) captures a transient collection state that's missing the moved field, which is
            // exactly the bug this case exists to prevent. A real deletion goes through
            // RemoveCustomFieldCommand, which sets _explicitCustomFieldRemove around the mutation.
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove when !_explicitCustomFieldRemove:
                break;

            // A bare Add via the "+" button (AddCustomFieldCommand, flagged by
            // _explicitCustomFieldAdd) must not dirty the model on its own - the new field is still
            // an empty placeholder indistinguishable from "nothing changed" in the Gen0 comparison.
            // OnCustomFieldPropertyChanged (subscribed below) dirties the model once the user
            // actually edits the field's Label/Value.
            case System.Collections.Specialized.NotifyCollectionChangedAction.Add when _explicitCustomFieldAdd:
                break;

            // An Add that isn't from AddCustomFieldCommand is the reorder's second half (see the
            // Remove case above) - the collection now reflects its final order, so this is the
            // right moment to try the direct, TimeMachine-skipping commit if the model is fully
            // clean, or do nothing otherwise. Never dirty the model for this: a mid-draft reorder
            // must not bleed into the next autosave, and TrySaveCustomFieldOrderOnlyAsync itself
            // re-checks the clean-state condition before writing anything.
            case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                CurrentSaveTask = TrySaveCustomFieldOrderOnlyAsync();
                break;

            // A real deletion (RemoveCustomFieldCommand) still dirties immediately - removing an
            // existing field is itself a meaningful change.
            default:
                _isDirty = true;
                _fileOnlyChanges = false;
                OnPropertyChanged(nameof(HasUnsavedChanges));
                break;
        }
        if (e.NewItems != null)
            foreach (CustomFieldModel cf in e.NewItems)
                cf.PropertyChanged += OnCustomFieldPropertyChanged;
        if (e.OldItems != null)
            foreach (CustomFieldModel cf in e.OldItems)
                cf.PropertyChanged -= OnCustomFieldPropertyChanged;
    }

    // Direct, TimeMachine-skipping commit for a pure custom-field reorder. Only writes when
    // the model is fully clean (no unsaved edits, no existing draft) and
    // every field already existed in Gen0 - a reorder that also carries an unedited placeholder
    // field from the "+" button, or one made mid-draft, is left untouched instead (in-memory order
    // only, reverts on reload). Skips SecretHistory (no world rotation) and SecretDrafts entirely:
    // a reorder alone isn't a meaningful content change worth a TimeMachine generation.
    private async Task TrySaveCustomFieldOrderOnlyAsync()
    {
        try
        {
            if (EditingSecret == null) return;
            if (_session.IsReadOnlyRestricted) return;
            if (HasUnsavedChanges || EditingSecretHasDraft) return;

            _lockGuard.ThrowIfLocked();
            var key = _session.GetKey();
            var existing = await _secrets.GetByIdAsync(EditingSecret.Id);
            if (existing == null) return; // not-yet-committed new item: nothing to reorder against

            HashSet<int> existingFieldIds;
            if (existing.CustomFields is { Length: > 0 })
            {
                using var cfSp = FieldCrypto.Open(existing.CustomFields, _crypto, key);
                var existingCfs = cfSp != null
                    ? JsonSerializer.Deserialize(cfSp.Utf8, SecretListJsonContext.Default.ListCustomFieldModel)
                    : null;
                existingFieldIds = existingCfs?.Select(f => f.FieldId).ToHashSet() ?? [];
            }
            else
            {
                existingFieldIds = [];
            }

            if (!EditingSecret.CustomFields.All(f => existingFieldIds.Contains(f.FieldId))) return;

            _lockGuard.ThrowIfLocked();  // final line of defense, immediately before the write
            existing.CustomFields = EditingSecret.CustomFields.Count > 0
                ? FieldCrypto.SealJson(EditingSecret.CustomFields.ToList(), SecretListJsonContext.Default.ListCustomFieldModel, _crypto, key)
                : null;
            existing.UpdatedAt = DateTime.UtcNow;
            await _secrets.UpdateAsync(existing);
            EditingSecret.UpdatedAt = existing.UpdatedAt.ToLocalTime();
            // The UpdatedAt assignment above flips _isDirty via OnEditingModelPropertyChanged (it
            // isn't in that handler's PasswordStrengthLevel-style exclusion list, nor should it be -
            // it's a real data property, not a display-only one). This method only ever runs from a
            // clean state to begin with, so restore that instead of leaving a phantom dirty flag.
            _isDirty = false;
            OnPropertyChanged(nameof(HasUnsavedChanges));
            _autoBackup.MarkContentChanged();
            _logger.LogInformation("[CustomFieldOrder] SecretId={Id}: order-only direct commit (no TimeMachine push)", EditingSecret.Id);
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
        if (e.PropertyName != null && !_dirtyCustomFieldProps.Contains(e.PropertyName)) return;
        _isDirty = true;
        _fileOnlyChanges = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    public async Task GuardedSaveAsync() => await SaveSecretAsync();

    public void DiscardChanges()
    {
        _isDirty = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    internal async Task AutoSaveOnNavigateAsync()
    {
        // Wait for any save already in flight (e.g. a LosingFocus-triggered fire-and-forget
        // SaveDraftAsync from the very click that also triggered this navigation) before checking
        // HasUnsavedChanges below. Otherwise navigation can proceed while that background write to
        // the draft is still running, and a later operation elsewhere on the same SecretDrafts row
        // (e.g. TimeMachine's permanent delete) can race it.
        var pending = CurrentSaveTask;
        if (pending != null)
        {
            try { await pending; }
            catch (Exception ex) { _logger.LogWarning("[AutoSaveOnNavigate] Awaited in-flight save ended with an exception. [{ExType}]", ex.GetType().Name); }
        }

        if (!HasUnsavedChanges || EditingSecret == null) return;

        await _saveLock.WaitAsync();
        try
        {
            // Re-check after acquiring the lock (in case state changed after a previous save completed)
            if (!HasUnsavedChanges || EditingSecret == null) return;
            // Never auto-save to the DB. Save to the draft and leave Gen0 (the DB-confirmed state) untouched.
            await SaveDraftAsync("AutoSaveOnNavigate");
            DiscardChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError("[AutoSaveOnNavigate] Automatic draft save failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(3));
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task LoadAsync()
    {
        _logger.LogInformation("[LoadAsync] Starting");
        EditingSecret = null;
        SelectedListItem = null;

        IsBusy = true;
        try
        {
            var key = _session.GetKey();
            var allSecrets = await _secrets.GetAllAsync();
            var draftIds = await _secretDrafts.GetAllDraftIdsAsync();

            _totalCount = allSecrets.Count;
            foreach (var e in _searchIndex) e.Dispose();
            // Cleared alongside _searchIndex: a stale pooled item's FaviconSource must not survive
            // into a reload for a different vault, where the same Id could denote a different secret.
            _itemPool.Clear();
            // Per-record try-catch (not a single Select().ToList()): one corrupted Secret's decrypt
            // failure must not throw out of LoadAsync entirely, which - reached via the async void
            // Page.Loaded handler - would otherwise crash the whole app rather than just hide that
            // one record from the list.
            var searchIndex = new List<SearchEntry>(allSecrets.Count);
            int decryptFailedCount = 0;
            foreach (var s in allSecrets)
            {
                SecurePlaintext? tSp = null, wSp = null, uSp = null, mSp = null;
                try
                {
                    tSp = FieldCrypto.Open(s.Title,   _crypto, key);
                    wSp = FieldCrypto.Open(s.Website, _crypto, key);
                    uSp = FieldCrypto.Open(s.UserId,  _crypto, key);
                    mSp = FieldCrypto.Open(s.Email,   _crypto, key);
                    // Notes is excluded from the index (decrypted on demand upon selection)
                    var domain = wSp != null ? ExtractDomainFromUtf8(wSp.Utf8) : null;
                    searchIndex.Add(new SearchEntry(
                        s.Id, s.CategoryNum,
                        tSp != null ? tSp.Utf8 : ReadOnlySpan<byte>.Empty,
                        uSp != null ? uSp.Utf8 : ReadOnlySpan<byte>.Empty,
                        wSp != null ? wSp.Utf8 : ReadOnlySpan<byte>.Empty,
                        mSp != null ? mSp.Utf8 : ReadOnlySpan<byte>.Empty,
                        s.UpdatedAt.ToLocalTime(),
                        s.IsFavorite,
                        s.ExpiresAt?.ToLocalTime(),
                        domain,
                        draftIds.Contains(s.Id)));
                }
                catch (CryptographicException)
                {
                    decryptFailedCount++;
                    _logger.LogWarning("[LoadAsync] Skipped Secret due to a decrypt failure. [Id={Id}]", s.Id);
                }
                finally
                {
                    tSp?.Dispose(); wSp?.Dispose(); uSp?.Dispose(); mSp?.Dispose();
                }
            }
            _searchIndex = searchIndex;

            CategoryItems.Clear();
            CategoryComboItems.Clear();
            FilterComboItems.Clear();
            FilterComboItems.Add(new FilterCategoryItem { Code = null, Name = LocalizationManager.Get("Common.All") });

            // Categories have no DB table - they're locale-defined presets (Categories.NN), enumerated
            // ascending by code, with the fixed Uncategorized (0) bucket appended last.
            foreach (var (code, name) in LocalizationManager.GetCategoryPresets())
            {
                var group = new CategoryGroupViewModel { Code = code, Name = name };
                CategoryItems.Add(group);
                CategoryComboItems.Add(group);
                FilterComboItems.Add(new FilterCategoryItem { Code = code, Name = name });
            }
            var uncategorizedName = LocalizationManager.Get("Categories.Uncategorized");
            var uncategorizedGroup = new CategoryGroupViewModel { Code = 0, Name = uncategorizedName };
            CategoryItems.Add(uncategorizedGroup);
            CategoryComboItems.Add(uncategorizedGroup);
            FilterComboItems.Add(new FilterCategoryItem { Code = 0, Name = uncategorizedName });

            _validCategoryCodes = CategoryItems.Select(c => c.Code).ToHashSet();

            ApplySearch(SearchText);
            _ = LoadFaviconsAsync(_searchIndex.ToList());
            if (_favicon.IsEnabled)
                _ = PrefetchAllFaviconsAsync();

            UpdateExpiryAlert();

            if (decryptFailedCount > 0)
            {
                _notification.Show(
                    LK.Common_ErrorPartialLoadTitle,
                    string.Format(LocalizationManager.Get("Common.ErrorPartialLoadText"), decryptFailedCount, _totalCount),
                    NotificationSeverity.Warning, TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedListItemChanged(object? value)
    {
        _lastSelectionTask = HandleListSelectionAsync(value);
        _ = _lastSelectionTask;
    }

    private async Task HandleListSelectionAsync(object? newValue)
    {
        // Ignore while the filter is being rebuilt (FilteredSecrets.Clear/Add)
        if (_isRebuildingFilter) return;

        // Selection restore after a filter switch: no reload needed if it's the same item
        if (newValue is SecretListItemViewModel sameNode && EditingSecret?.Id == sameNode.Id)
        {
            _session.LastSelectedSecretId = sameNode.Id;
            return;
        }

        // Debounce: cancel the previous one on rapid clicks → only the last selection executes
        _selectionDelayCts?.Cancel();
        _selectionDelayCts?.Dispose();
        _selectionDelayCts = new CancellationTokenSource();
        var ct = _selectionDelayCts.Token;

        try
        {
            await Task.Delay(120, ct);

            IsBusy = true;
            await AutoSaveOnNavigateAsync();

            if (newValue is SecretListItemViewModel node)
            {
                _session.LastSelectedSecretId = node.Id;
                await LoadSecretAsync(node.Id, ct);
                if (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await _auditLog.LogAsync(
                            AuditEventCode.SecretViewed,
                            new SecretViewedPayload(node.Id, node.Title),
                            _session.GetKey());
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning("[HandleListSelection] Failed to log SecretViewed. [{ExType}]", ex.GetType().Name);
                    }
                }
            }
            else if (newValue is CategoryGroupViewModel)
                EditingSecret = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal debounce cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError("[HandleListSelection] Load failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(3));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task HandleFilterCategoryChangeAsync(int? newCode)
    {
        // Clear the right pane if editing a secret in a different category
        if (EditingSecret != null && newCode.HasValue && EditingSecret.CategoryNum != newCode)
        {
            await AutoSaveOnNavigateAsync();
            EditingSecret = null;
            SelectedListItem = null;
        }
        ApplySearch(SearchText);
    }

    internal async Task LoadSecretAsync(int id, CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var key = _session.GetKey();
            SecretEditModel? em = null;

            // Draft takes priority: if SecretId != 0, restore completely without reading Gen0
            var (draftEncBlob, _) = await _secretDrafts.GetDraftAsync(id);
            ct.ThrowIfCancellationRequested();
            bool loadedFromSlot = false;
            if (draftEncBlob != null)
            {
                int draftPlainLen = draftEncBlob.Length - ICryptoService.AeadOverhead;
                if (draftPlainLen > 0)
                {
                    var draftPlain = GC.AllocateArray<byte>(draftPlainLen, pinned: true);
                    try
                    {
                        _crypto.Decrypt(draftEncBlob, key.Span, draftPlain);
                        var slot = SnapshotSerializer.ReadToSlot(draftPlain.AsSpan());
                        try
                        {
                            if (slot.SecretId != 0)
                            {
                                // Self-heal: a draft byte-for-byte identical to Gen0 carries no
                                // information the user hasn't already confirmed (e.g. left behind by
                                // a bug that spuriously marked the model dirty on mere focus, with no
                                // real edit). Reuses the exact same comparison
                                // GetDraftCompareDataAsync already performs before showing the compare
                                // dialog, just triggered here so a stray draft clears itself the
                                // moment the item is viewed, instead of requiring that dialog to be
                                // opened manually to discover and discard it.
                                var gen0Entity = await _secrets.GetByIdAsync(id);
                                bool isNoOpDraft = gen0Entity != null && await IsDraftNoOpAsync(slot, gen0Entity, key);

                                if (isNoOpDraft && gen0Entity != null)
                                {
                                    await _secretDrafts.DiscardDraftAsync(id);
                                    _logger.LogInformation("[LoadSecret] SecretId={Id}: stray NoOp draft discarded on view", id);
                                    try { await _auditLog.LogAsync(AuditEventCode.NoOpDraftDiscarded, new NoOpDraftDiscardedPayload(1), key); }
                                    catch (Exception auditEx) when (auditEx is not OperationCanceledException)
                                    { _logger.LogWarning("[LoadSecret] Failed to log NoOpDraftDiscarded. [{ExType}]", auditEx.GetType().Name); }
                                    var si0 = _searchIndex.FindIndex(x => x.Id == id);
                                    if (si0 >= 0) _searchIndex[si0].HasDraft = false;
                                    var flatNode0 = FilteredSecrets.FirstOrDefault(n => n.Id == id);
                                    if (flatNode0 != null) flatNode0.HasDraft = false;
                                    FilteredDraftCount = FilteredSecrets.Count(n => n.HasDraft);
                                    em = await BuildEditModelFromEntityAsync(gen0Entity, key);
                                }
                                else
                                {
                                    em = await BuildEditModelFromSlotAsync(slot, key);
                                    loadedFromSlot = true;
                                }
                            }
                        }
                        finally { slot?.Dispose(); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* Fall back to Gen0 for a corrupted snapshot */ }
                    finally { CryptographicOperations.ZeroMemory(draftPlain.AsSpan()); }
                }
            }

            // Fall back to Gen0 (no draft or decryption failed)
            if (em == null)
            {
                ct.ThrowIfCancellationRequested();
                var s = await _secrets.GetByIdAsync(id);
                if (s == null) return;
                em = await BuildEditModelFromEntityAsync(s, key);
            }

            ct.ThrowIfCancellationRequested();
            await ApplyGenDatesAsync(em, id);
            _gen0FileIds = (await _secrets.GetFileLinksAsync(id)).ToHashSet();
            EditingSecret = em;
            ResetGeneratorDefaults();
            // Err on the safe side when loaded from a draft, since it may include text changes too
            if (loadedFromSlot) _fileOnlyChanges = false;
            EditingSecretHasDraft = _searchIndex.FirstOrDefault(x => x.Id == id)?.HasDraft ?? false;
            // On-demand strength evaluation (triggered on expand)
            if (!em.PasswordBuf.IsEmpty)
            {
                var score = _evaluator.EvaluateStrength(em.PasswordBuf.Span);
                em.SetStrengthScore(score);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<SecretEditModel> BuildEditModelFromSlotAsync(HistorySlotContent slot, DekScope key)
    {
        var em = new SecretEditModel
        {
            Id          = slot.SecretId,
            CategoryNum = slot.CategoryNum,
            IsFavorite  = slot.IsFavorite,
            CreatedAt    = !slot.CreatedAtBuf.IsEmpty
                && DateTime.TryParse(slot.CreatedAtBuf.Span, null, System.Globalization.DateTimeStyles.RoundtripKind, out var createDt)
                ? createDt.ToLocalTime() : default,
            UpdatedAt    = !slot.TimestampBuf.IsEmpty
                && DateTime.TryParse(slot.TimestampBuf.Span, null, System.Globalization.DateTimeStyles.RoundtripKind, out var updateDt)
                ? updateDt.ToLocalTime() : default,
            ExpiresAt   = !slot.ExpiresAtBuf.IsEmpty
                && DateTime.TryParse(slot.ExpiresAtBuf.Span, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expDt)
                ? new DateTimeOffset(expDt.ToLocalTime().Date) : null,
            GenSymbols  = slot.GenSymbolsBuf.IsEmpty
                ? PasswordGenerator.DefaultSymbols
                : new string(slot.GenSymbolsBuf.Span),
        };
        // PII text fields: copy directly into SecureCharBuffer, no intermediate string created
        em.TitleBuf.SetFromSpan(slot.TitleBuf.Span);
        em.UserIdBuf.SetFromSpan(slot.UserIdBuf.Span);
        em.WebsiteBuf.SetFromSpan(slot.WebsiteBuf.Span);
        em.EmailBuf.SetFromSpan(slot.EmailBuf.Span);

        // Sensitive fields go SecureCharBuffer → SecureCharBuffer, never through a string
        em.SetPasswordDirect(slot.PasswordBuf.Span);
        em.SetNotesDirect(slot.NotesBuf.Span);
        em.SetTotpSecretDirect(slot.TotpSecretBuf.Span);

        if (!slot.CustomFieldsBuf.IsEmpty)
        {
            var cfs = JsonSerializer.Deserialize(slot.CustomFieldsBuf.Span, SecretListJsonContext.Default.ListCustomFieldModel);
            if (cfs != null)
            {
                int nextId = cfs.Max(f => (int?)f.FieldId) ?? 0;
                foreach (var cf in cfs)
                {
                    if (cf.FieldId == 0) cf.FieldId = ++nextId;
                    em.CustomFields.Add(cf);
                }
            }
        }

        if (!slot.LabelOverridesBuf.IsEmpty)
        {
            int bc = Encoding.UTF8.GetByteCount(slot.LabelOverridesBuf.Span);
            var loBytes = ArrayPool<byte>.Shared.Rent(bc);
            try
            {
                Encoding.UTF8.GetBytes(slot.LabelOverridesBuf.Span, loBytes.AsSpan());
                ApplyLabelOverrides(loBytes.AsSpan(0, bc), em);
            }
            finally { ArrayPool<byte>.Shared.Return(loBytes); }
        }

        var imageIds = slot.FileIds ?? [];
        if (imageIds.Length > 0)
        {
            var imgs = await _storedFiles.GetByIdsAsync(imageIds, key);
            try
            {
                foreach (var img in imgs)
                    em.AttachedFiles.Add(BuildAttachedFileItem(img, key));
            }
            finally
            {
                // Immediately wipe the plaintext filename array that StoredFileRepository allocates on each decrypt
                foreach (var img in imgs) CryptographicOperations.ZeroMemory(img.FileName);
            }
        }

        return em;
    }

    private async Task<SecretEditModel> BuildEditModelFromEntityAsync(Secret s, DekScope key)
    {
        using var titleSp  = FieldCrypto.Open(s.Title,     _crypto, key);
        using var userSp   = FieldCrypto.Open(s.UserId,    _crypto, key);
        using var passSp   = FieldCrypto.Open(s.Password,  _crypto, key);
        using var wsSp     = FieldCrypto.Open(s.Website,   _crypto, key);
        using var emailSp   = FieldCrypto.Open(s.Email,     _crypto, key);
        using var notesSp  = FieldCrypto.Open(s.Notes,     _crypto, key);
        using var totpSp   = FieldCrypto.Open(s.TotpSecret,_crypto, key);
        var em = new SecretEditModel
        {
            Id          = s.Id,
            CategoryNum = s.CategoryNum,
            IsFavorite  = s.IsFavorite,
            CreatedAt    = s.CreatedAt.ToLocalTime(),
            UpdatedAt    = s.UpdatedAt.ToLocalTime(),
            ExpiresAt   = s.ExpiresAt.HasValue ? (DateTimeOffset?)new DateTimeOffset(s.ExpiresAt.Value.ToLocalTime().Date) : null,
            GenSymbols  = string.IsNullOrEmpty(s.GeneratorSymbols) ? PasswordGenerator.DefaultSymbols : s.GeneratorSymbols,
        };
        // No intermediate string for any field: copy UTF-8 bytes directly into SecureCharBuffer
        if (titleSp != null) FieldCrypto.FillBufferFromUtf8(em.TitleBuf,      titleSp.Utf8);
        if (userSp  != null) FieldCrypto.FillBufferFromUtf8(em.UserIdBuf,     userSp.Utf8);
        if (wsSp    != null) FieldCrypto.FillBufferFromUtf8(em.WebsiteBuf,    wsSp.Utf8);
        if (emailSp  != null) FieldCrypto.FillBufferFromUtf8(em.EmailBuf,   emailSp.Utf8);
        if (passSp  != null) FieldCrypto.FillBufferFromUtf8(em.PasswordBuf,   passSp.Utf8);
        if (notesSp != null) FieldCrypto.FillBufferFromUtf8(em.NotesBuf,      notesSp.Utf8);
        if (totpSp  != null) FieldCrypto.FillBufferFromUtf8(em.TotpSecretBuf, totpSp.Utf8);

        if (s.LabelOverrides != null && s.LabelOverrides.Length > 0)
        {
            using var loSp = FieldCrypto.Open(s.LabelOverrides, _crypto, key);
            if (loSp != null) ApplyLabelOverrides(loSp.Utf8, em);
        }

        if (s.CustomFields != null && s.CustomFields.Length > 0)
        {
            using var cfSp = FieldCrypto.Open(s.CustomFields, _crypto, key);
            if (cfSp != null)
            {
                var cfs = JsonSerializer.Deserialize(cfSp.Utf8, SecretListJsonContext.Default.ListCustomFieldModel);
                if (cfs != null)
                {
                    int nextId = cfs.Max(f => (int?)f.FieldId) ?? 0;
                    foreach (var cf in cfs)
                    {
                        if (cf.FieldId == 0) cf.FieldId = ++nextId;
                        em.CustomFields.Add(cf);
                    }
                }
            }
        }

        var imageIds = await _secrets.GetFileLinksAsync(s.Id);
        if (imageIds.Any())
        {
            var imgs = await _storedFiles.GetByIdsAsync(imageIds, key);
            try
            {
                foreach (var img in imgs)
                    em.AttachedFiles.Add(BuildAttachedFileItem(img, key));
            }
            finally
            {
                // Immediately wipe the plaintext filename array that StoredFileRepository allocates on each decrypt
                foreach (var img in imgs) CryptographicOperations.ZeroMemory(img.FileName);
            }
        }

        return em;
    }

    private bool CanAddSecret() => !_session.IsReadOnlyRestricted;

    [RelayCommand(CanExecute = nameof(CanAddSecret))]
    private async Task AddSecretAsync()
    {
        // Since CanExecute is only referenced by UI bindings, place the same internal guard as other
        // write methods as a defense against direct command invocation (structural block on restricted view mode)
        if (_session.IsReadOnlyRestricted) return;

        // Stash the current edit content to the draft before opening a new record
        await AutoSaveOnNavigateAsync();

        var defaultCategory = FilterCategoryCode ?? CategoryItems.FirstOrDefault()?.Code;
        var key = _session.GetKey();
        var profile = await _profileService.LoadProfileAsync(key);

        // Pre-insert: instantiate the DB record first and fix its Id
        // '!' is a fixed sentinel character on the code side. Including it in a locale value risks accidental removal during translation/maintenance
        var defaultTitle = "!" + LocalizationManager.Get("Secrets.UntitledSecret");
        var tempModel = new SecretEditModel
        {
            CategoryNum = defaultCategory,
            Title       = defaultTitle,
            Email    = profile.Email,
        };
        IsBusy = true;
        try
        {
            var entity = BuildSecretEntity(tempModel, key);
            profile.ZeroPii(); // profile was loaded solely to prefill Email, now consumed into entity
            var newId  = await _secrets.AddAsync(entity);

            // Immediately commit the Gen0 snapshot to SecretHistory (the Time Machine starting point)
            await PushToTimeMachineAsync(entity, key, []);
            _autoBackup.MarkContentChanged();
            try
            {
                await _auditLog.LogAsync(
                    AuditEventCode.SecretSaved,
                    new SecretSavedPayload(newId, IsNew: true, defaultTitle),
                    key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("[AddSecret] Failed to log SecretSaved: {ExType}", ex.GetType().Name);
            }

            // Reflect immediately in the search index and ListView
            _searchIndex.Add(new SearchEntry(newId, defaultCategory,
                defaultTitle.AsSpan(),
                (profile.Email ?? string.Empty).AsSpan(),
                ReadOnlySpan<char>.Empty,
                (profile.Email ?? string.Empty).AsSpan(),
                entity.UpdatedAt.ToLocalTime(), false, null, null, hasDraft: false));

            var em = new SecretEditModel
            {
                Id          = newId,
                CategoryNum = defaultCategory,
                Title       = defaultTitle,
                Email    = profile.Email,
                CreatedAt    = entity.CreatedAt.ToLocalTime(),
                UpdatedAt    = entity.UpdatedAt.ToLocalTime(),
            };
            EditingSecret        = em;
            EditingSecretHasDraft = false;

            RefreshListItem(EditingSecret);
            _totalCount++;
            UpdateCountLabel();
            UpdateExpiryAlert();

            // Align SelectedListItem (HandleListSelectionAsync short-circuits on a matching EditingSecret.Id)
            var newNode = FilteredSecrets.FirstOrDefault(n => n.Id == newId);
            if (newNode != null) SelectedListItem = newNode;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveSecret() => EditingSecretHasDraft && !_session.IsReadOnlyRestricted;

    // Restoring while a draft (SecretDrafts) exists would leave the pre-restore draft layered on top of the
    // post-restore Gen0 baseline - TimeMachineViewModel.CanRestoreGen1/CanRestoreGen2 already block
    // that independently, so this only needs to gate on read-only mode, not draft state. Blocking the
    // jump itself was needlessly restrictive: viewing history while a draft is in progress is fine.
    public bool CanJumpToTimeMachine => !_session.IsReadOnlyRestricted;

    [RelayCommand(CanExecute = nameof(CanSaveSecret))]
    private async Task SaveSecretAsync()
    {
        // Since CanExecute is only referenced by UI bindings, place the same internal guard as other
        // write methods as a defense against direct command invocation (structural block on restricted view mode)
        if (_session.IsReadOnlyRestricted) return;

        _lockGuard.ThrowIfLocked();  // Gate 1: blocks new tasks after the barrier

        if (EditingSecret == null) return;
        if (string.IsNullOrWhiteSpace(EditingSecret.Title))
        {
            _notification.Show(LK.Common_Warning, LK.Secrets_WarningTitleRequired, NotificationSeverity.Warning, TimeSpan.FromSeconds(3));
            return;
        }

        var saveOperation = SaveSecretCoreAsync();
        CurrentSaveTask = saveOperation;
        try
        {
            await saveOperation;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SaveSecretAsync: cancelled/blocked by session lock");
        }
        finally
        {
            if (CurrentSaveTask == saveOperation) CurrentSaveTask = null;
        }
    }

    private async Task SaveSecretCoreAsync()
    {
        _lockGuard.ThrowIfLocked();  // Gate 1 (repeated at the core entry point)

        if (EditingSecret == null) return;

        IsBusy = true;
        try
        {
            var key = _session.GetKey();
            var existing = await _secrets.GetByIdAsync(EditingSecret.Id);
            if (existing != null)
            {
                var oldFileIds = (await _secrets.GetFileLinksAsync(EditingSecret.Id)).ToArray();
                bool hasFileChange = !oldFileIds.ToHashSet().SetEquals(EditingSecret.AttachedFiles.Select(i => i.Id).ToHashSet());
                bool needsPush = HasAnyChange(existing, EditingSecret, key) || hasFileChange;

                // First change save (SecretHistory row not yet created, or SlotOrder=0): record the pre-save state as Gen0 first
                // This produces 0→0x100→0x200_0x100, making Gen1 visible on the first save
                if (needsPush)
                {
                    var slotOrder = await _secretHistory.GetSlotOrderAsync(EditingSecret.Id);
                    if (slotOrder is null or 0)
                        await PushToTimeMachineAsync(existing, key, oldFileIds);
                }

                // 1. Confirmed commit: overwrite the Secrets table with the new state
                UpdateSecretEntity(existing, EditingSecret, key);
                _lockGuard.ThrowIfLocked();  // Gate 2 [final line of defense], immediately before SaveChangesAsync
                await _secrets.UpdateAsync(existing);
                var newFileIds = EditingSecret.AttachedFiles.Select(i => i.Id).ToArray();
                await _secrets.UpdateFileLinksAsync(EditingSecret.Id, newFileIds);
                _autoBackup.MarkContentChanged();
                try
                {
                    await _auditLog.LogAsync(
                        AuditEventCode.SecretSaved,
                        new SecretSavedPayload(EditingSecret.Id, IsNew: false, EditingSecret.Title),
                        key);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("[SaveSecretCore] Failed to log SecretSaved: {ExType}", ex.GetType().Name);
                }

                // 2. History rotation: push the new Gen0 snapshot after confirmation (never reverse this order).
                // PushAsync's internal transaction also atomically clears the SecretDrafts row for this
                // secret (see SecretHistoryRepository.PushAsync), so no separate discard call is needed here.
                // needsPush is expected to always be true when this method runs: CanSaveSecret requires
                // EditingSecretHasDraft, and SaveDraftCoreAsync's NoOp check never lets a draft persist
                // unless it is genuinely different from Gen0 (the same comparison HasAnyChange performs above).
                if (needsPush)
                    await PushToTimeMachineAsync(existing, key, newFileIds);
                _logger.LogInformation("[SaveSecret] SecretId={Id}: committed, draft discarded", EditingSecret.Id);

                EditingSecret.UpdatedAt = existing.UpdatedAt.ToLocalTime();
                await ApplyGenDatesAsync(EditingSecret, existing.Id);
                var si = _searchIndex.FindIndex(s => s.Id == EditingSecret.Id);
                var domain = ExtractDomain(EditingSecret.Website);
                if (si >= 0)
                {
                    var oldEntry = _searchIndex[si];
                    _searchIndex[si] = new SearchEntry(EditingSecret.Id, EditingSecret.CategoryNum,
                        EditingSecret.TitleBuf.Span,   EditingSecret.UserIdBuf.Span,
                        EditingSecret.WebsiteBuf.Span, EditingSecret.EmailBuf.Span,
                        EditingSecret.UpdatedAt, EditingSecret.IsFavorite,
                        EditingSecret.ExpiresAt?.DateTime, domain, hasDraft: false);
                    oldEntry.Dispose();
                }
                var flatNodeSaved = FilteredSecrets.FirstOrDefault(n => n.Id == EditingSecret.Id);
                if (flatNodeSaved != null) flatNodeSaved.HasDraft = false;
                FilteredDraftCount = FilteredSecrets.Count(n => n.HasDraft);
                EditingSecretHasDraft = false;
                RefreshListItem(EditingSecret);
                if (domain != null) _ = PrefetchAndUpdateNodeAsync(domain, EditingSecret.Id);
                UpdateExpiryAlert();
                _notification.Show(LK.Common_SuccessSaveComplete, LK.Common_SuccessSaveComplete, NotificationSeverity.Success, TimeSpan.FromSeconds(3));
                // Without this, TimeMachineViewModel's NeedsReload never gets set after a plain edit
                // save (only Delete/AddFiles/RemoveFile sent it), so revisiting Time Machine via the
                // ordinary nav menu (not a jump) could show stale data until some unrelated change
                // happened to trigger a reload first.
                WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
            }
            _isDirty = false;
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the session lock — no notification needed
            _logger.LogInformation("SaveSecretCoreAsync: interrupted by session lock");
        }
        catch (Exception ex)
        {
            _logger.LogError("SaveSecretCoreAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDeleteSecret() => !_session.IsReadOnlyRestricted;

    [RelayCommand(CanExecute = nameof(CanDeleteSecret))]
    private async Task DeleteSecretAsync()
    {
        // Since CanExecute is only referenced by UI bindings, place the same internal guard as other
        // write methods as a defense against direct command invocation (structural block on restricted view mode)
        if (_session.IsReadOnlyRestricted) return;
        if (EditingSecret == null) return;
        await DoSoftDeleteAsync(EditingSecret);
    }

    private async Task DoSoftDeleteAsync(SecretEditModel target)
    {
        var title = target.Title;
        IsBusy = true;
        try
        {
            _isDirty = false;
            await _secrets.SoftDeleteAsync(target.Id);
            _autoBackup.MarkContentChanged();
            try { await _auditLog.LogAsync(AuditEventCode.SecretSoftDeleted, new SecretSoftDeletedPayload(target.Id, title), _session.GetKey()); }
            catch (Exception ex) { _logger.LogError("Audit log failed for soft delete. [{ExType}]", ex.GetType().Name); }
            _totalCount--;
            var delIdx = _searchIndex.FindIndex(s => s.Id == target.Id);
            if (delIdx >= 0) { _searchIndex[delIdx].Dispose(); _searchIndex.RemoveAt(delIdx); }
            UpdateExpiryAlert();
            RemoveFromList(target.Id);
            EditingSecret = null;
            SelectedListItem = null;
            _notification.Show(LK.Common_InfoDeleteComplete,
                string.Format(LocalizationManager.GetById(LK.Common_InfoDeleteComplete), title),
                NotificationSeverity.Warning, TimeSpan.FromSeconds(4));
            WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(SecretListItemViewModel node)
    {
        if (_session.IsReadOnlyRestricted) return;
        await AutoSaveOnNavigateAsync();

        var newValue = !node.IsFavorite;
        await _secrets.SetFavoriteAsync(node.Id, newValue);
        node.IsFavorite = newValue;

        var si = _searchIndex.FindIndex(s => s.Id == node.Id);
        if (si >= 0)
            _searchIndex[si].IsFavorite = newValue;

        if (FilterFavoritesOnly && !newValue)
        {
            FilteredSecrets.Remove(node);
            foreach (var catNode in CategoryItems)
            {
                var cn = catNode.Secrets.FirstOrDefault(x => x.Id == node.Id);
                if (cn != null) { catNode.Secrets.Remove(cn); break; }
            }
            UpdateCountLabel();
        }
        FilteredFavoritesCount = FilteredSecrets.Count(x => x.IsFavorite);

        if (EditingSecret?.Id == node.Id)
        {
            EditingSecret.IsFavorite = newValue;
            // Favorite is saved immediately, so it isn't treated as dirty
            _isDirty = false;
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }

        // Notify other pages (TimeMachine, etc.) so their cached copy of this secret's IsFavorite
        // doesn't go stale - without this, TimeMachine keeps showing/comparing the pre-toggle value
        // until something else happens to trigger its reload.
        WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
    }

    partial void OnSearchTextChanged(string value) => ApplySearch(value);

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private void ApplySearch(string? keyword)
    {
        var kw = keyword?.Trim();
        IEnumerable<SearchEntry> matches;
        if (string.IsNullOrEmpty(kw))
        {
            matches = _searchIndex;
        }
        else
        {
            // Split tokens on both half-width (U+0020) and full-width (U+3000) spaces and AND-search them
            var tokens = kw.Split([' ', '　'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            matches = _searchIndex.Where(s =>
                tokens.All(t =>
                    SpanHit(s.TitleSpan,  t) ||
                    SpanHit(s.UserIdSpan, t) ||
                    SpanHit(s.UrlSpan,    t) ||
                    SpanHit(s.EmailSpan,   t) ||
                    s.UpdatedAt.ToString("yyyy/MM/dd").Contains(t, StringComparison.Ordinal)));
        }

        if (FilterCategoryCode.HasValue)
            matches = matches.Where(s => EffectiveCategoryCode(s.CategoryNum) == FilterCategoryCode);

        var baseList  = matches.ToList();
        var today     = DateTime.Today;
        var warnLimit = today.AddDays(AppConstants.SecretPasswordWarnDays);
        FilteredFavoritesCount   = baseList.Count(s => s.IsFavorite);
        FilteredExpiredCount     = baseList.Count(s => s.ExpiresAt.HasValue && s.ExpiresAt.Value.Date < warnLimit);
        HasFilteredExpiredPastDue = baseList.Any(s => s.ExpiresAt.HasValue && s.ExpiresAt.Value.Date < today);
        FilteredDraftCount       = baseList.Count(s => s.HasDraft);

        var filteredList = baseList.AsEnumerable();
        if (FilterFavoritesOnly) filteredList = filteredList.Where(s => s.IsFavorite);
        if (FilterExpiredOnly)   filteredList = filteredList.Where(s => s.ExpiresAt.HasValue && s.ExpiresAt.Value.Date < warnLimit);
        if (FilterDraftOnly)     filteredList = filteredList.Where(s => s.HasDraft);

        // OrderBy can't use a span key, so ToList first and compare via Sort
        var matchList = filteredList.ToList();
        matchList.Sort((a, b) => MemoryExtensions.CompareTo(a.TitleSpan, b.TitleSpan, StringComparison.CurrentCultureIgnoreCase));

        foreach (var catNode in CategoryItems)
        {
            catNode.Secrets.Clear();
            foreach (var s in matchList.Where(x => x.CategoryNum == catNode.Code))
                catNode.Secrets.Add(GetOrCreatePoolItem(s));
        }

        _isRebuildingFilter = true;
        FilteredSecrets.Clear();
        foreach (var s in matchList)
            FilteredSecrets.Add(GetOrCreatePoolItem(s));
        _isRebuildingFilter = false;
        UpdateCountLabel();
    }

    // Allocation-free search. keyword is a string capture; the span is a temporary reference within the lambda.
    internal static bool SpanHit(ReadOnlySpan<char> source, string keyword) =>
        !source.IsEmpty && MemoryExtensions.Contains(source, keyword.AsSpan(), StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanGeneratePassword))]
    private void GeneratePassword()
    {
        if (EditingSecret == null) return;
        var em        = EditingSecret;
        var symbols   = PwdUseSymbols ? em.GenSymbols : null;
        var pinnedBuf = GC.AllocateArray<char>(PwdLength, pinned: true);
        try
        {
            PasswordGenerator.Generate(pinnedBuf.AsSpan(), PwdUseUpper, PwdUseLower, PwdUseDigits, PwdUseSymbols, symbols);
            // Must evaluate before ZeroMemory
            var score = _evaluator.EvaluateStrength(pinnedBuf.AsSpan());
            _suppressStrengthDebounce = true;
            try   { em.SetPasswordDirect(pinnedBuf.AsSpan()); }
            finally { _suppressStrengthDebounce = false; }
            em.SetStrengthScore(score);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(pinnedBuf.AsSpan()));
        }
    }

    // Shared by the Title/UserId/Password/Website/Email copy buttons - fieldKey is a fixed
    // internal identifier (XAML CommandParameter literal), mapped here to the field's *current*
    // display label (LabelTitle/LabelUserId/etc., which reflects any LabelOverrides the user has
    // set) so the audit trail matches what the user actually sees on screen, never a generic default.
    [RelayCommand]
    private void CopyField(string? fieldKey)
    {
        if (EditingSecret == null || string.IsNullOrEmpty(fieldKey)) return;
        var (value, label) = fieldKey switch
        {
            "Title"    => (EditingSecret.Title,    EditingSecret.LabelTitle),
            "UserId"   => (EditingSecret.UserId,   EditingSecret.LabelUserId),
            "Password" => (EditingSecret.Password, EditingSecret.LabelPassword),
            "Website"  => (EditingSecret.Website,  EditingSecret.LabelUrl),
            "Email" => (EditingSecret.Email, EditingSecret.LabelEmail),
            _          => (null, string.Empty),
        };
        if (string.IsNullOrEmpty(value)) return;
        ClipboardHelper.SetText(value);
        _clipboardEraser.ScheduleClear(value.AsSpan());
        _ = LogFieldCopiedAsync(EditingSecret.Id, label, EditingSecret.Title);
    }

    private async Task LogFieldCopiedAsync(int targetId, string fieldLabel, string? name)
    {
        try
        {
            await _auditLog.LogAsync(
                AuditEventCode.SecretFieldCopiedToClipboard,
                new SecretFieldCopiedToClipboardPayload(targetId, fieldLabel, name),
                _session.GetKey());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[SecretsViewModel] Failed to log SecretFieldCopiedToClipboard. [{ExType}]", ex.GetType().Name);
        }
    }

    // Set around the collection mutation inside AddCustomField/RemoveCustomField below, so
    // OnEditingModelCollectionChanged can tell an explicit "+"/trash-can command apart from the
    // Remove+Add pair WinUI 3's ListView.CanReorderItems raises for a drag-and-drop reorder (it
    // never calls ObservableCollection.Move - see the Move/Remove/Add handling comment there).
    private bool _explicitCustomFieldAdd;
    private bool _explicitCustomFieldRemove;

    [RelayCommand]
    private void AddCustomField(string? type)
    {
        if (EditingSecret == null) return;
        int nextId = EditingSecret.CustomFields.Count > 0 ? EditingSecret.CustomFields.Max(f => f.FieldId) + 1 : 1;
        _explicitCustomFieldAdd = true;
        try
        {
            var fieldType = CustomFieldTypeExtensions.FromCommandParameter(type);
            EditingSecret.CustomFields.Add(new CustomFieldModel
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
            EditingSecret?.CustomFields.Remove(field);
            field.Dispose();
        }
        finally
        {
            _explicitCustomFieldRemove = false;
        }
    }

    [RelayCommand]
    private async Task OpenWebsiteAsync()
    {
        var raw = EditingSecret?.Website;
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (!TryBuildSafeUrl(raw, out var url))
        {
            _notification.Show(LK.Common_Error, string.Format(LocalizationManager.GetById(LK.Common_ErrorBrowserOpenFailed), raw), NotificationSeverity.Error, TimeSpan.FromSeconds(3));
            return;
        }
        try
        {
            bool success = await Launcher.LaunchUriAsync(new Uri(url));
            if (!success)
                _notification.Show(LK.Common_Error, string.Format(LocalizationManager.GetById(LK.Common_ErrorBrowserOpenFailed), raw), NotificationSeverity.Error, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError("OpenWebsiteAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(3));
        }
    }

    [RelayCommand]
    private void CopyCustomField(CustomFieldModel? field)
    {
        if (string.IsNullOrEmpty(field?.Value)) return;
        ClipboardHelper.SetText(field.Value);
        _clipboardEraser.ScheduleClear(field.Value.AsSpan());
        if (EditingSecret != null)
            _ = LogFieldCopiedAsync(EditingSecret.Id, field.Label, EditingSecret.Title);
    }

    [RelayCommand]
    private async Task OpenCustomFieldUrlAsync(CustomFieldModel? field)
    {
        var raw = field?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (!TryBuildSafeUrl(raw, out var url))
        {
            _notification.Show(LK.Common_Error, string.Format(LocalizationManager.GetById(LK.Common_ErrorBrowserOpenFailed), raw), NotificationSeverity.Error, TimeSpan.FromSeconds(3));
            return;
        }
        try
        {
            bool success = await Launcher.LaunchUriAsync(new Uri(url));
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
    public async Task AddFilesAsync(string[] filePaths)
    {
        if (EditingSecret == null) return;
        if (_session.IsReadOnlyRestricted) return;
        IsBusy = true;
        int failedCount = 0;
        try
        {
            var key = _session.GetKey();
            foreach (var path in filePaths)
            {
                if (!File.Exists(path)) continue;
                byte[]? data = null;
                byte[]? thumbnailData = null;
                try
                {
                    var fileInfo = new FileInfo(path);
                    data = GC.AllocateArray<byte>(checked((int)fileInfo.Length), pinned: true);
                    await using (var fsr = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous))
                        await fsr.ReadExactlyAsync(data.AsMemory());

                    var fileHash = HMACSHA256.HashData(key.Span, SHA256.HashData(data));
                    var existingImage = await _storedFiles.FindByHashAsync(fileHash, key);

                    if (existingImage != null)
                    {
                        if (!EditingSecret.AttachedFiles.Any(i => i.Id == existingImage.Id))
                        {
                            var fi = new FileItem
                            {
                                Id = existingImage.Id,
                                ContentType = existingImage.ContentTypeCode,
                                ThumbnailData = DecryptThumbnail(existingImage.ThumbnailBlob, key),
                            };
                            fi.SetFileNameFromUtf8(existingImage.FileName);
                            EditingSecret.AttachedFiles.Add(fi);
                        }
                    }
                    else
                    {
                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        var contentType = FC.FromExtension(ext) is var ct && ct != 0
                            ? ct : FC.OctetStream;
                        bool isPdf            = FC.IsPdf(contentType);
                        bool hasVisualContent = FC.CanCreateThumbnail(contentType);
                        thumbnailData = hasVisualContent
                            ? await ImageHelper.CreateThumbnailAsync(data, 200, isPdf)
                            : null;
                        var (encOrig, encThumb) = await Task.Run(() => (
                            _crypto.Encrypt(data, key.Span),
                            thumbnailData != null ? _crypto.Encrypt(thumbnailData, key.Span) : null));
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
                                FileSize        = data.Length,
                                FileHash        = fileHash,
                                FileModifiedAt  = fileInfo.LastWriteTimeUtc,
                                OriginalBlob    = encOrig,
                                ThumbnailBlob   = encThumb
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
                            // Pinned copy for thumbnail display (FileItem.Dispose ZeroMemories it)
                            byte[]? pinnedThumb = null;
                            if (thumbnailData != null)
                            {
                                pinnedThumb = GC.AllocateArray<byte>(thumbnailData.Length, pinned: true);
                                thumbnailData.AsSpan().CopyTo(pinnedThumb.AsSpan());
                            }

                            var fi = new FileItem { Id = newId, ContentType = contentType, ThumbnailData = pinnedThumb };
                            fi.SetFileNameFromUtf8(fnBytes.AsSpan(0, fnByteCount));
                            EditingSecret.AttachedFiles.Add(fi);
                        }
                        finally { CryptographicOperations.ZeroMemory(fnBytes.AsSpan()); }
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
        _isDirty = true;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        await SaveDraftAsync("AddFiles");
        WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
    }

    [RelayCommand]
    private void ClearExpiry()
    {
        if (EditingSecret != null)
            EditingSecret.ExpiresAt = null;
    }

    // ─── TOTP ────────────────────────────────────────────────────────────────

    // Accepting a SecureCharBuffer avoids calling the TotpSecret getter (which creates a string)
    private void RestartTotpTimer(SecureCharBuffer? totpBuf)
    {
        _totpTimer?.Stop();
        _totpTimer = null;

        if (totpBuf == null || totpBuf.IsEmpty)
        {
            TotpCode = string.Empty;
            TotpSecondsRemaining = 30;
            TotpCodeIsExpiring = false;
            OnPropertyChanged(nameof(TotpSecondsLabel));
            return;
        }

        RefreshTotpCode(totpBuf.Span);

        _totpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _totpTimer.Tick += (_, _) =>
        {
            // Direct access to the buffer reference. The span is used as a temporary value inside the lambda (not captured by the closure)
            var buf = EditingSecret?.TotpSecretBuf;
            if (buf == null || buf.IsEmpty) { _totpTimer?.Stop(); return; }
            RefreshTotpCode(buf.Span);
        };
        _totpTimer.Start();
    }

    // packed = "{Secret}{sep}{Digits}{sep}{Period}{sep}{Algorithm}" (TotpSecretBuf's plaintext payload,
    // see TotpCalculator.Pack/TryUnpack). Unpacked via span so the secret never becomes a heap string
    // just to compute a code.
    private void RefreshTotpCode(ReadOnlySpan<char> packed)
    {
        if (!TotpCalculator.TryUnpack(packed, out var secret, out var digits, out var period, out var algorithm))
        {
            TotpCode = string.Empty;
            TotpSecondsRemaining = 30;
            TotpCodeIsExpiring = false;
            OnPropertyChanged(nameof(TotpSecondsLabel));
            return;
        }
        try
        {
            var (code, rem) = TotpCalculator.Generate(secret, digits, period, algorithm);
            TotpCode = code;
            TotpSecondsRemaining = rem;
            TotpCodeIsExpiring = rem <= 5;
            OnPropertyChanged(nameof(TotpSecondsLabel));

            var prev = TotpCalculator.GenerateAtOffset(secret, -1, digits, period, algorithm);
            var next = TotpCalculator.GenerateAtOffset(secret, +1, digits, period, algorithm);
            TotpAdjacentLabel = string.Format(
                LocalizationManager.Get("Totp.TimeDriftCodes"),
                TotpCalculator.FormatCodeForDisplay(prev),
                TotpCalculator.FormatCodeForDisplay(next),
                period);
        }
        catch { /* Ignore an invalid secret */ }
    }

    [RelayCommand]
    private void CopyTotp()
    {
        if (string.IsNullOrEmpty(TotpCode)) return;
        ClipboardHelper.SetText(TotpCode);
        _clipboardEraser.ScheduleClear(TotpCode.AsSpan());
        if (EditingSecret != null)
            _ = LogFieldCopiedAsync(EditingSecret.Id, "TOTP", EditingSecret.Title);
    }

    private const string EraseToolGlyph = "\uE75C";

    // TOTP is a login-critical credential, so disabling it is treated with the same red/destructive
    // styling as an actual delete (ConfirmDestructiveAsync), not a neutral OK/Cancel confirm.
    // Wording is deliberately unified around "disable" (not "delete"/"discard", which this app's
    // UI already uses for soft-delete and draft-discard elsewhere) across title/body/button.
    internal Task<bool> ConfirmDisableTotpAsync() => _dialog.ConfirmDestructiveAsync(
        LocalizationManager.Get("Secrets.DisableTotpTooltip"),
        LocalizationManager.Get("Secrets.Dialog.DisableTotpConfirm"),
        EraseToolGlyph,
        LocalizationManager.Get("Secrets.Dialog.DisableTotpButton"),
        defaultToCancel: true);

    [RelayCommand]
    private async Task RemoveFile(FileItem? item)
    {
        if (item == null || EditingSecret == null) return;
        EditingSecret.AttachedFiles.Remove(item);
        item.Dispose();

        // Auto-discard if a file-only change reverts to the same value as Gen0 (turns off the pencil icon immediately)
        if (_fileOnlyChanges)
        {
            var currentIds = EditingSecret.AttachedFiles.Select(i => i.Id).ToHashSet();
            if (currentIds.SetEquals(_gen0FileIds))
            {
                await AutoDiscardCurrentDraftAsync(EditingSecret.Id);
                return;
            }
        }

        _isDirty = true;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        await SaveDraftAsync("RemoveFile");
        WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
    }

    private async Task AutoDiscardCurrentDraftAsync(int id)
    {
        await DiscardDraftGuardedAsync(id);
        var si = _searchIndex.FindIndex(s => s.Id == id);
        if (si >= 0) _searchIndex[si].HasDraft = false;
        var flatNode = FilteredSecrets.FirstOrDefault(n => n.Id == id);
        if (flatNode != null) flatNode.HasDraft = false;
        FilteredDraftCount = FilteredSecrets.Count(n => n.HasDraft);
        EditingSecretHasDraft = false;
        _isDirty = false;
        _fileOnlyChanges = true;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        WeakReferenceMessenger.Default.Send(new StorageChangedMessage());
    }

    private Secret BuildSecretEntity(SecretEditModel m, DekScope key)
    {
        var s = new Secret();
        UpdateSecretEntity(s, m, key);
        return s;
    }

    private void UpdateSecretEntity(Secret s, SecretEditModel m, DekScope key)
    {
        s.Title      = FieldCrypto.Seal(m.Title, _crypto, key);
        s.CategoryNum = m.CategoryNum;
        s.IsFavorite  = m.IsFavorite;
        s.UserId     = FieldCrypto.SealNullable(!string.IsNullOrEmpty(m.UserId)  ? m.UserId  : null, _crypto, key);
        s.Password   = m.PasswordBuf.IsEmpty   ? null : FieldCrypto.Seal(m.PasswordBuf.Span,   _crypto, key);
        s.Website    = FieldCrypto.SealNullable(!string.IsNullOrEmpty(m.Website) ? m.Website : null, _crypto, key);
        s.Email      = FieldCrypto.SealNullable(!string.IsNullOrEmpty(m.Email)? m.Email: null, _crypto, key);
        s.Notes      = m.NotesBuf.IsEmpty      ? null : FieldCrypto.Seal(m.NotesBuf.Span,      _crypto, key);
        s.ExpiresAt  = m.ExpiresAt?.UtcDateTime;
        s.TotpSecret = m.TotpSecretBuf.IsEmpty ? null : FieldCrypto.Seal(m.TotpSecretBuf.Span, _crypto, key);

        if (m.CustomFields.Any())
            s.CustomFields = FieldCrypto.SealJson(m.CustomFields.ToList(),
                SecretListJsonContext.Default.ListCustomFieldModel, _crypto, key);
        else
            s.CustomFields = null;

        var loJson = BuildLabelOverridesJson(m);
        s.LabelOverrides = loJson != null ? FieldCrypto.Seal(loJson, _crypto, key) : null;

        s.GeneratorSymbols = string.IsNullOrEmpty(m.GenSymbols) || m.GenSymbols == PasswordGenerator.DefaultSymbols
            ? null
            : m.GenSymbols;
    }

    private bool HasAnyChange(Secret existing, SecretEditModel m, DekScope key)
    {
        static string? N(string? s) => string.IsNullOrEmpty(s) ? null : s;

        if (existing.CategoryNum != m.CategoryNum) return true;
        if (existing.ExpiresAt   != m.ExpiresAt?.UtcDateTime) return true;
        using var tSp  = FieldCrypto.Open(existing.Title,        _crypto, key);
        if ((tSp != null ? Encoding.UTF8.GetString(tSp.Utf8) : null) != m.Title) return true;
        using var uSp  = FieldCrypto.Open(existing.UserId,       _crypto, key);
        if (N(uSp != null ? Encoding.UTF8.GetString(uSp.Utf8) : null) != N(m.UserId)) return true;
        using var pSp  = FieldCrypto.Open(existing.Password,     _crypto, key);
        if (N(pSp != null ? Encoding.UTF8.GetString(pSp.Utf8) : null) != N(m.Password)) return true;
        using var wSp  = FieldCrypto.Open(existing.Website,      _crypto, key);
        if (N(wSp != null ? Encoding.UTF8.GetString(wSp.Utf8) : null) != N(m.Website)) return true;
        using var mSp  = FieldCrypto.Open(existing.Email,        _crypto, key);
        if (N(mSp != null ? Encoding.UTF8.GetString(mSp.Utf8) : null) != N(m.Email)) return true;
        using var nSp  = FieldCrypto.Open(existing.Notes,        _crypto, key);
        if (N(nSp != null ? Encoding.UTF8.GetString(nSp.Utf8) : null) != N(m.Notes)) return true;

        var newCf = m.CustomFields.Any() ? JsonSerializer.Serialize(m.CustomFields.ToList(), RelaxedJsonContext.ListCustomFieldModel) : null;
        using var cfSp = FieldCrypto.Open(existing.CustomFields,  _crypto, key);
        if (N(cfSp != null ? Encoding.UTF8.GetString(cfSp.Utf8) : null) != N(newCf)) return true;

        // Compares the packed payload (Secret+Digits+Period+Algorithm), not m.TotpSecret (which now
        // exposes only the Secret portion) - otherwise this would always report a change whenever TOTP is configured.
        using var totpSp = FieldCrypto.Open(existing.TotpSecret,  _crypto, key);
        var currentTotpPacked = m.TotpSecretBuf.IsEmpty ? null : m.TotpSecretBuf.ToDisplayString();
        if (N(totpSp != null ? Encoding.UTF8.GetString(totpSp.Utf8) : null) != N(currentTotpPacked)) return true;

        var newLo = BuildLabelOverridesJson(m);
        using var loSp = FieldCrypto.Open(existing.LabelOverrides, _crypto, key);
        if (N(loSp != null ? Encoding.UTF8.GetString(loSp.Utf8) : null) != N(newLo)) return true;

        var existingGenSymbols = string.IsNullOrEmpty(existing.GeneratorSymbols) ? PasswordGenerator.DefaultSymbols : existing.GeneratorSymbols;
        if (existingGenSymbols != m.GenSymbols) return true;

        return false;
    }

    private static string? BuildLabelOverridesJson(SecretEditModel m)
    {
        var overrides = new Dictionary<string, string>();
        if (m.LabelTitle    != SecretEditModel.DefaultLabelTitle)    overrides["title"]    = m.LabelTitle;
        if (m.LabelUrl      != SecretEditModel.DefaultLabelUrl)      overrides["url"]      = m.LabelUrl;
        if (m.LabelUserId   != SecretEditModel.DefaultLabelUserId)   overrides["userId"]   = m.LabelUserId;
        if (m.LabelPassword != SecretEditModel.DefaultLabelPassword) overrides["password"] = m.LabelPassword;
        if (m.LabelEmail != SecretEditModel.DefaultLabelEmail) overrides["email"] = m.LabelEmail;
        if (m.LabelNotes    != SecretEditModel.DefaultLabelNotes)    overrides["notes"]    = m.LabelNotes;
        return overrides.Count > 0
            ? JsonSerializer.Serialize(overrides, RelaxedJsonContext.DictionaryStringString)
            : null;
    }

    /// <summary>
    /// Parses LabelOverrides' UTF-8 JSON directly with a Utf8JsonReader and applies it to EditModel.
    /// Since it never becomes a Dictionary, no heap allocation occurs for key strings.
    /// </summary>
    private static void ApplyLabelOverrides(ReadOnlySpan<byte> utf8Json, SecretEditModel em)
    {
        var reader = new Utf8JsonReader(utf8Json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isTitle    = reader.ValueTextEquals("title"u8);
            bool isUrl      = reader.ValueTextEquals("url"u8);
            bool isUserId   = reader.ValueTextEquals("userId"u8);
            bool isPassword = reader.ValueTextEquals("password"u8);
            bool isEmail = reader.ValueTextEquals("email"u8);
            bool isNotes    = reader.ValueTextEquals("notes"u8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) break;
            if      (isTitle)    em.LabelTitle    = reader.GetString()!;
            else if (isUrl)      em.LabelUrl      = reader.GetString()!;
            else if (isUserId)   em.LabelUserId   = reader.GetString()!;
            else if (isPassword) em.LabelPassword = reader.GetString()!;
            else if (isEmail) em.LabelEmail = reader.GetString()!;
            else if (isNotes)    em.LabelNotes    = reader.GetString()!;
        }
    }

    private async Task PushToTimeMachineAsync(Secret existing, DekScope dek, int[] fileIds)
    {
        using var pinnedBuf = new PinnedBufferWriter();
        using (var w = new Utf8JsonWriter(pinnedBuf))
            SnapshotSerializer.WriteEntity(w, existing, _crypto, dek, fileIds);
        var encBlob = _crypto.Encrypt(pinnedBuf.WrittenSpan, dek.Span);
        var (rotated, sacrificedSlotLabel) = await _secretHistory.PushAsync(existing.Id, encBlob, existing.UpdatedAt);
        if (rotated)
        {
            try
            {
                await _auditLog.LogAsync(AuditEventCode.TimeMachineGenRotated, new TimeMachineGenRotatedPayload(existing.Id), dek);
                if (sacrificedSlotLabel != null)
                    await _auditLog.LogAsync(AuditEventCode.TimeMachineSlotDeleted, new TimeMachineSlotDeletedPayload(existing.Id, sacrificedSlotLabel), dek);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[PushToTimeMachineAsync] Failed to log TimeMachine audit entries. [{ExType}]", ex.GetType().Name);
            }
        }
    }

    /// <summary>Returns the current EditingSecret's draft save time (SecretDrafts.SavedAt). null if there is no draft.</summary>
    public async Task<string?> GetCurrentDraftSavedAtAsync()
    {
        if (EditingSecret == null) return null;
        var (_, at) = await _secretDrafts.GetDraftAsync(EditingSecret.Id);
        return at?.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    }

    /// <summary>Decrypts the draft and returns data for the comparison dialog. Draft=null if there is no draft or decryption fails.</summary>
    internal async Task<(HistorySlotContent? Draft, string DraftAtDisplay, HistorySlotContent? Gen0, string Gen0AtDisplay,
        List<CompareFileThumbnail> DraftFiles, List<CompareFileThumbnail> Gen0Files)> GetDraftCompareDataAsync()
    {
        if (EditingSecret == null)
            return (null, string.Empty, null, string.Empty, [], []);

        var (encBlob, draftAt) = await _secretDrafts.GetDraftAsync(EditingSecret.Id);
        if (encBlob == null) return (null, string.Empty, null, string.Empty, [], []);

        var key = _session.GetKey();
        if (encBlob.Length <= ICryptoService.AeadOverhead) return (null, string.Empty, null, string.Empty, [], []);
        int plainLen = encBlob.Length - ICryptoService.AeadOverhead;
        var plainBytes = GC.AllocateArray<byte>(plainLen, pinned: true);
        HistorySlotContent? draft = null;
        try
        {
            _crypto.Decrypt(encBlob, key.Span, plainBytes);
            try { draft = SnapshotSerializer.ReadToSlot(plainBytes.AsSpan()); }
            catch { return (null, string.Empty, null, string.Empty, [], []); }
            if (draft.SecretId == 0) { draft.Dispose(); return (null, string.Empty, null, string.Empty, [], []); }
        }
        catch { return (null, string.Empty, null, string.Empty, [], []); }
        finally { CryptographicOperations.ZeroMemory(plainBytes.AsSpan()); }

        var draftDisplay = draftAt.HasValue
            ? draftAt.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : string.Empty;

        // Gen0 is serialized directly from the DB's Secrets table
        var existing = await _secrets.GetByIdAsync(EditingSecret.Id);
        if (existing == null)
        {
            var (draftOnlyFiles, _) = await BuildAttachmentThumbnailsAsync(draft.FileIds, null, key);
            return (draft, draftDisplay, null, string.Empty, draftOnlyFiles, []);
        }

        var fileIds = (await _secrets.GetFileLinksAsync(existing.Id)).ToArray();
        using var gen0Buf = new PinnedBufferWriter();
        using (var w = new Utf8JsonWriter(gen0Buf))
            SnapshotSerializer.WriteEntity(w, existing, _crypto, key, fileIds);
        var gen0 = SnapshotSerializer.ReadToSlot(gen0Buf.WrittenSpan);

        var gen0Display = existing.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");

        // Auto-discard when the draft is semantically identical to Gen0 (e.g. a false draft mistakenly generated right after import)
        if (FindFirstMismatch(draft, gen0) is null)
        {
            draft.Dispose();
            gen0.Dispose();
            await _secretDrafts.DiscardDraftAsync(EditingSecret.Id);
            _logger.LogInformation("[SaveDraft] SecretId={Id} reason=CompareDraftOpen: NoOp, draft discarded", EditingSecret.Id);
            try { await _auditLog.LogAsync(AuditEventCode.NoOpDraftDiscarded, new NoOpDraftDiscardedPayload(1), key); }
            catch (Exception auditEx) when (auditEx is not OperationCanceledException)
            { _logger.LogWarning("[SaveDraft] Failed to log NoOpDraftDiscarded. [{ExType}]", auditEx.GetType().Name); }
            var si = _searchIndex.FindIndex(s => s.Id == EditingSecret.Id);
            if (si >= 0)
            {
                _searchIndex[si].HasDraft = false;
                var flatNode = FilteredSecrets.FirstOrDefault(n => n.Id == EditingSecret.Id);
                if (flatNode != null) flatNode.HasDraft = false;
                FilteredDraftCount = FilteredSecrets.Count(n => n.HasDraft);
            }
            EditingSecretHasDraft = false;
            _notification.Show(LK.Common_SuccessSaveComplete, LK.Common_InfoDeleteComplete, NotificationSeverity.Success, TimeSpan.FromSeconds(2));
            return (null, string.Empty, null, string.Empty, [], []);
        }

        var (draftFiles, gen0Files) = await BuildAttachmentThumbnailsAsync(draft.FileIds, fileIds, key);
        return (draft, draftDisplay, gen0, gen0Display, draftFiles, gen0Files);
    }

    // Fetches and decrypts both sides' attached-file thumbnails for the compare dialog in one round
    // trip (the union of both FileIds arrays), so a file attached on both sides isn't decrypted twice.
    // Files that no longer exist (e.g. GC'd) are silently skipped rather than surfaced as an error -
    // the compare dialog is read-only, so a missing thumbnail just means one fewer row, not data loss.
    private async Task<(List<CompareFileThumbnail> Draft, List<CompareFileThumbnail> Gen0)> BuildAttachmentThumbnailsAsync(
        int[]? draftFileIds, int[]? gen0FileIds, DekScope key)
    {
        var unionIds = (draftFileIds ?? []).Concat(gen0FileIds ?? []).Distinct().ToArray();
        if (unionIds.Length == 0) return ([], []);

        var files = await _storedFiles.GetByIdsAsync(unionIds, key);
        var byId = new Dictionary<int, CompareFileThumbnail>();
        try
        {
            foreach (var f in files)
            {
                var thumb = f.IsQuarantined ? null : DecryptThumbnail(f.ThumbnailBlob, key);
                var name  = Encoding.UTF8.GetString(f.FileName);
                byId[f.Id] = new CompareFileThumbnail { FileId = f.Id, FileName = name, ContentType = f.ContentTypeCode, ThumbnailBytes = thumb, IsQuarantined = f.IsQuarantined };
            }
        }
        finally
        {
            // Immediately wipe the plaintext filename array that StoredFileRepository allocates on each decrypt
            foreach (var f in files) CryptographicOperations.ZeroMemory(f.FileName);
        }

        List<CompareFileThumbnail> Map(int[]? ids) =>
            (ids ?? []).Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        return (Map(draftFileIds), Map(gen0FileIds));
    }

    // Serialized against SaveDraftCoreAsync (the LosingFocus-driven autosave) via the same
    // semaphore: without this, a save that's still mid-flight when the user discards can write to
    // the draft concurrently with this method's own DiscardDraftAsync write. SQLite's WAL mode
    // serializes the writes rather than corrupting data, but the losing writer can surface as a
    // locked/busy error - reproducible only under rapid discard/edit/discard cycling, hence rare.
    //
    // Deliberately does not touch StoredFiles: a discarded draft only removes this secret's
    // reference to a file (SecretFileLinks), never the file itself. StoredFiles is Gallery's
    // independently-owned pool (see the StoredFile class doc comment) - a file an edit stops
    // referencing simply becomes unlinked, still visible and manageable in Gallery. Physically
    // deleting a file's bytes is exclusively GalleryViewModel.DeleteSelectedAsync's job (the only
    // path with a destructive confirmation dialog and an audit-log entry naming the file).
    private async Task DiscardDraftGuardedAsync(int id)
    {
        _saveDraftCts?.Cancel();
        await _saveDraftSemaphore.WaitAsync();
        try
        {
            await _secretDrafts.DiscardDraftAsync(id);
        }
        finally
        {
            _saveDraftSemaphore.Release();
        }
    }

    // Shared by LoadSecretAsync (single item) and CleanUpNoOpDraftsAsync (vault-wide scan): compares
    // an already-decrypted draft slot against Gen0 field-by-field via FindFirstMismatch. Fails safe
    // (returns false, i.e. "keep the draft") on any decrypt/parse error rather than risk discarding
    // a draft whose real content couldn't be verified.
    private async Task<bool> IsDraftNoOpAsync(HistorySlotContent draftSlot, Secret gen0Entity, DekScope key)
    {
        try
        {
            var fileIds = (await _secrets.GetFileLinksAsync(gen0Entity.Id)).ToArray();
            using var gen0Buf = new PinnedBufferWriter();
            using (var w = new Utf8JsonWriter(gen0Buf))
                SnapshotSerializer.WriteEntity(w, gen0Entity, _crypto, key, fileIds);
            using var gen0Slot = SnapshotSerializer.ReadToSlot(gen0Buf.WrittenSpan);
            return FindFirstMismatch(draftSlot, gen0Slot) is null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// Vault-wide self-heal: scans every SecretDrafts row and discards any that are byte-for-byte
    /// identical to Gen0 (e.g. left behind by a bug that spuriously marked a model dirty with no real
    /// edit - see the LoadSecretAsync equivalent for the per-item version of this same check). Meant
    /// to be called once per session, right after unlock, alongside the dashboard's other startup
    /// diagnostics (DashboardViewModel.TryRunInitialScanAsync) - so a stray draft anywhere in the
    /// vault self-heals proactively instead of only when its item happens to be opened.
    /// Returns the number of drafts discarded (0 on no-op or when read-only restricted).
    /// </summary>
    internal async Task<int> CleanUpNoOpDraftsAsync(CancellationToken ct = default)
    {
        if (_session.IsReadOnlyRestricted) return 0;

        int discarded = 0;
        var key = _session.GetKey();
        HashSet<int> draftIds;
        try { draftIds = await _secretDrafts.GetAllDraftIdsAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning("[CleanUpNoOpDrafts] failed to enumerate draft ids. [{ExType}]", ex.GetType().Name);
            return 0;
        }

        foreach (var id in draftIds)
        {
            ct.ThrowIfCancellationRequested();
            var (draftEncBlob, _) = await _secretDrafts.GetDraftAsync(id);
            if (draftEncBlob == null) continue;
            int plainLen = draftEncBlob.Length - ICryptoService.AeadOverhead;
            if (plainLen <= 0) continue;

            var draftPlain = GC.AllocateArray<byte>(plainLen, pinned: true);
            HistorySlotContent? slot = null;
            try
            {
                _crypto.Decrypt(draftEncBlob, key.Span, draftPlain);
                slot = SnapshotSerializer.ReadToSlot(draftPlain.AsSpan());
                if (slot.SecretId == 0) continue;

                var gen0Entity = await _secrets.GetByIdAsync(id);
                if (gen0Entity == null || !await IsDraftNoOpAsync(slot, gen0Entity, key)) continue;

                await _secretDrafts.DiscardDraftAsync(id);
                discarded++;
                _logger.LogInformation("[CleanUpNoOpDrafts] SecretId={Id}: stray NoOp draft discarded", id);
                var si = _searchIndex.FindIndex(x => x.Id == id);
                if (si >= 0) _searchIndex[si].HasDraft = false;
                var flatNode = FilteredSecrets.FirstOrDefault(n => n.Id == id);
                if (flatNode != null) flatNode.HasDraft = false;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Corrupted/unreadable draft: skip it, fail-safe (keep as-is) rather than discard on doubt
                _logger.LogWarning("[CleanUpNoOpDrafts] SecretId={Id}: skipped, comparison failed. [{ExType}]", id, ex.GetType().Name);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(draftPlain.AsSpan());
                slot?.Dispose();
            }
        }

        if (discarded > 0)
        {
            FilteredDraftCount = FilteredSecrets.Count(n => n.HasDraft);
            try { await _auditLog.LogAsync(AuditEventCode.NoOpDraftDiscarded, new NoOpDraftDiscardedPayload(discarded), key); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogWarning("[CleanUpNoOpDrafts] Failed to log NoOpDraftDiscarded. [{ExType}]", ex.GetType().Name); }
        }
        return discarded;
    }

    // Returns the name of the first field found to differ between the two slots, or null if they're
    // semantically identical. Returning the field NAME (not the value) lets callers log a useful trace
    // of *why* a draft write/discard decision was made without ever exposing secret content.
    private string? FindFirstMismatch(HistorySlotContent a, HistorySlotContent b)
    {
        if (a.CategoryNum != b.CategoryNum) return "CategoryNum";
        if (a.IsFavorite  != b.IsFavorite)  return "IsFavorite";
        if (!SpanEq(a.TitleBuf,      b.TitleBuf))      return "Title";
        if (!SpanEq(a.UserIdBuf,     b.UserIdBuf))      return "UserId";
        if (!SpanEq(a.PasswordBuf,   b.PasswordBuf))    return "Password";
        if (!SpanEq(a.WebsiteBuf,    b.WebsiteBuf))     return "Website";
        if (!SpanEq(a.EmailBuf,   b.EmailBuf))    return "Email";
        if (!SpanEq(a.NotesBuf,      b.NotesBuf))       return "Notes";
        if (!SpanEq(a.TotpSecretBuf, b.TotpSecretBuf))  return "TotpSecret";
        if (!SpanEq(a.ExpiresAtBuf,  b.ExpiresAtBuf))   return "ExpiresAt";
        // BuildLabelOverridesJson always emits keys in the same fixed order (title/url/userId/
        // password/email/notes), so a raw span compare is safe here - unlike CustomFields, whose
        // order the user can freely change (see AreCustomFieldsIdentical below). Without this check, a
        // label-only rename of a standard field (Username/Password/Website/Email) was invisible to
        // FindFirstMismatch: SaveDraftAsync's own NoOp check discarded the draft immediately as
        // "identical to Gen0" - the record never entered draft mode, unlike a custom field's label
        // edit (folded into the CustomFields JSON, which IS compared) - even though the compare
        // dialog already had full support for highlighting a changed label (ResolveLabel/AddRow),
        // it never got the chance to run because no draft ever survived to be compared.
        if (!SpanEq(a.LabelOverridesBuf, b.LabelOverridesBuf)) return "LabelOverrides";
        if (!GenSymbolsEq(a.GenSymbolsBuf, b.GenSymbolsBuf)) return "GenSymbols";
        // Order-insensitive: a fresh DB fetch (SecretRepository.GetFileLinksAsync has no ORDER BY) can
        // return the same attached-file set in a different order than the in-memory AttachedFiles list,
        // which SequenceEqual would wrongly treat as a real difference.
        if (!(a.FileIds ?? []).ToHashSet().SetEquals(b.FileIds ?? [])) return "FileIds";
        if (!AreCustomFieldsIdentical(a.CustomFieldsBuf, b.CustomFieldsBuf)) return "CustomFields";
        return null;

        static bool SpanEq(SecureCharBuffer x, SecureCharBuffer y)
            => MemoryExtensions.Equals(x.Span, y.Span, StringComparison.Ordinal);

        // SecretEditModel.GenSymbols falls back to PasswordGenerator.DefaultSymbols for display
        // whenever the stored value is null/empty (see LoadSecretAsync), while the Gen0 entity keeps
        // the raw null/empty as-is. Comparing the raw spans directly (like SpanEq) would therefore
        // always report a mismatch for a symbol set nobody ever customized. Normalize both sides
        // through the same fallback already used elsewhere (SaveSecretCoreAsync/HasUnsavedChanges)
        // before comparing.
        static bool GenSymbolsEq(SecureCharBuffer x, SecureCharBuffer y)
        {
            var xSpan = x.IsEmpty ? PasswordGenerator.DefaultSymbols.AsSpan() : x.Span;
            var ySpan = y.IsEmpty ? PasswordGenerator.DefaultSymbols.AsSpan() : y.Span;
            return MemoryExtensions.Equals(xSpan, ySpan, StringComparison.Ordinal);
        }
    }

    private bool AreCustomFieldsIdentical(SecureCharBuffer aBuf, SecureCharBuffer bBuf)
    {
        if (aBuf.IsEmpty && bBuf.IsEmpty) return true;
        if (aBuf.IsEmpty != bBuf.IsEmpty) return false;

        List<CustomFieldModel>? aCfs = null, bCfs = null;
        try { aCfs = JsonSerializer.Deserialize(aBuf.Span, SecretListJsonContext.Default.ListCustomFieldModel); } catch { }
        try { bCfs = JsonSerializer.Deserialize(bBuf.Span, SecretListJsonContext.Default.ListCustomFieldModel); } catch { }

        if (aCfs == null && bCfs == null) return true;
        if (aCfs == null || bCfs == null) return false;
        if (aCfs.Count != bCfs.Count) return false;

        // Normalize FieldId=0 into sequential numbers before comparing content
        NormalizeCfIds(aCfs);
        NormalizeCfIds(bCfs);

        return aCfs.OrderBy(f => f.FieldId).Zip(bCfs.OrderBy(f => f.FieldId)).All(p =>
            p.First.FieldId  == p.Second.FieldId &&
            p.First.Label    == p.Second.Label   &&
            p.First.Value    == p.Second.Value   &&
            p.First.FieldType == p.Second.FieldType);
    }

    // Returns the POH-pinned array from DecryptToPin as-is (no ToArray() copy): a copy would land on the
    // movable heap, where a GC compaction leaves an unreachable plaintext ghost at the old address.
    // FileItem.Dispose / the compare view ZeroMemory the returned array when done.
    private byte[]? DecryptThumbnail(byte[]? encBlob, DekScope key)
        => _crypto.DecryptToPin(encBlob, key.Span);

    /// <summary>
    /// Builds a FileItem for an attached-file tile. A quarantined StoredFile (ContentType/FileName-extension
    /// mismatch — suspected tampering or corruption) is still shown, so the attachment stays visible instead of
    /// silently vanishing, but its thumbnail is never decrypted since ContentType can't be trusted to interpret it.
    /// </summary>
    private FileItem BuildAttachedFileItem(StoredFile img, DekScope key)
    {
        var fi = new FileItem
        {
            Id             = img.Id,
            ContentType    = img.ContentTypeCode,
            IsQuarantined  = img.IsQuarantined,
            ThumbnailData  = img.IsQuarantined ? null : DecryptThumbnail(img.ThumbnailBlob, key)
        };
        fi.SetFileNameFromUtf8(img.FileName);
        return fi;
    }

    private static void NormalizeCfIds(List<CustomFieldModel> cfs)
    {
        int nextId = cfs.Max(f => (int?)f.FieldId) ?? 0;
        foreach (var cf in cfs)
            if (cf.FieldId == 0) cf.FieldId = ++nextId;
    }

    /// <summary>
    /// Automatically saves EditingSecret's current state to the draft (SecretDrafts) on focus-out.
    /// The caller (View) should invoke this from a field's focus-out event.
    /// </summary>
    // Public entry point: always starts a fresh SaveDraftCoreAsync, but the core coalesces overlapping
    // callers via _saveDraftCts + _saveDraftSemaphore so only the most recent request actually writes
    // (reading EditingSecret's state only after acquiring the semaphore, not at request time).
    // reason: a short caller-supplied tag (e.g. "LosingFocus") identifying which UI trigger requested
    // the save, so a production log can show WHY a draft write was attempted, not just that one was.
    public async Task SaveDraftAsync(string reason)
    {
        if (EditingSecret == null) return;
        if (_session.IsReadOnlyRestricted) return;

        var saveOp = SaveDraftCoreAsync(reason);
        CurrentSaveTask = saveOp;
        try
        {
            await saveOp;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[SaveDraft] reason={Reason}: cancelled/superseded by a newer draft save", reason);
        }
        // SaveDraftAsync is routinely called fire-and-forget (`_ = ViewModel.SaveDraftAsync(...)`),
        // so a non-cancellation failure (SQLite lock contention, etc.) surfacing during a CTS race
        // must not become a silent UnobservedTaskException that leaves the draft unsaved.
        catch (Exception ex)
        {
            _logger.LogWarning("[SaveDraft] reason={Reason}: failed to save draft. [{ExType}]", reason, ex.GetType().Name);
        }
        finally
        {
            if (CurrentSaveTask == saveOp) CurrentSaveTask = null;
        }
    }

    private async Task SaveDraftCoreAsync(string reason)
    {
        _saveDraftCts?.Cancel();
        _saveDraftCts = new CancellationTokenSource();
        var ct = _saveDraftCts.Token;
        try
        {
            await _saveDraftSemaphore.WaitAsync(ct);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            ct.ThrowIfCancellationRequested();
            if (EditingSecret == null) return;
            var key = _session.GetKey();

            using var cfBuf = new SecureCharBuffer();
            using var loBuf = new SecureCharBuffer();
            if (EditingSecret.CustomFields.Count > 0)
            {
                var cfStr = JsonSerializer.Serialize(EditingSecret.CustomFields.ToList(), RelaxedJsonContext.ListCustomFieldModel);
                cfBuf.SetFromSpan(cfStr.AsSpan());
                if (cfStr.Length > 0) SecurePasswordHelper.ZeroStringInternals(cfStr);
            }
            var loStr = BuildLabelOverridesJson(EditingSecret);
            if (loStr != null) loBuf.SetFromSpan(loStr.AsSpan());

            using var pinnedBuf = new PinnedBufferWriter();
            using (var writer = new Utf8JsonWriter(pinnedBuf))
                SnapshotSerializer.WriteEditModel(writer, EditingSecret, cfBuf.Span, loBuf.Span);

            // NoOp draft: if the current in-memory state is semantically identical to Gen0, auto-discard without writing the draft
            // For a new item (Id=0), Gen0 doesn't exist, so GetByIdAsync returns null, the comparison is skipped, and it saves directly
            var existing = await _secrets.GetByIdAsync(EditingSecret.Id);
            // Field NAME only (never the value) identifying why isSame came out false, for the write
            // log below. Field names are fixed schema identifiers, not secret content, so they're safe
            // to log even under the zero-knowledge policy - and the source is public anyway, so hiding
            // them behind an index would add no real confidentiality, only make the log harder to read.
            string mismatchReason = "NewItem";
            if (existing != null)
            {
                var fileIds = (await _secrets.GetFileLinksAsync(existing.Id)).ToArray();
                using var gen0Buf = new PinnedBufferWriter();
                using (var w = new Utf8JsonWriter(gen0Buf))
                    SnapshotSerializer.WriteEntity(w, existing, _crypto, key, fileIds);
                HistorySlotContent? draftSlot = null;
                HistorySlotContent? gen0Slot  = null;
                try
                {
                    try { draftSlot = SnapshotSerializer.ReadToSlot(pinnedBuf.WrittenSpan); } catch { }
                    try { gen0Slot  = SnapshotSerializer.ReadToSlot(gen0Buf.WrittenSpan); } catch { }
                    mismatchReason = draftSlot != null && gen0Slot != null
                        ? FindFirstMismatch(draftSlot, gen0Slot) ?? "None"
                        : "SlotParseFailure";
                }
                finally
                {
                    draftSlot?.Dispose();
                    gen0Slot?.Dispose();
                }

                if (mismatchReason == "None")
                {
                    // Zero difference → physically discard the existing draft and turn off the pencil icon
                    await _secretDrafts.DiscardDraftAsync(EditingSecret.Id);
                    _logger.LogInformation("[SaveDraft] SecretId={Id} reason={Reason}: NoOp, draft discarded", EditingSecret.Id, reason);
                    var si2 = _searchIndex.FindIndex(s => s.Id == EditingSecret.Id);
                    if (si2 >= 0) _searchIndex[si2].HasDraft = false;
                    var fn2 = FilteredSecrets.FirstOrDefault(n => n.Id == EditingSecret.Id);
                    if (fn2 != null) fn2.HasDraft = false;
                    FilteredDraftCount    = FilteredSecrets.Count(n => n.HasDraft);
                    EditingSecretHasDraft = false;
                    return;
                }
            }

            // Meaningful difference exists → encrypt and persist the draft
            byte[] encBlob = _crypto.Encrypt(pinnedBuf.WrittenSpan, key.Span);
            await _secretDrafts.SaveDraftAsync(EditingSecret.Id, encBlob, DateTime.UtcNow);
            _logger.LogInformation("[SaveDraft] SecretId={Id} reason={Reason}: draft written (mismatch={MismatchReason})", EditingSecret.Id, reason, mismatchReason);

            // Update the in-memory SearchEntry's HasDraft flag (lights up the ListView pencil icon)
            var si = _searchIndex.FindIndex(s => s.Id == EditingSecret.Id);
            if (si >= 0 && !_searchIndex[si].HasDraft)
            {
                _searchIndex[si].HasDraft = true;
                var flatNode = FilteredSecrets.FirstOrDefault(n => n.Id == EditingSecret.Id);
                if (flatNode != null) flatNode.HasDraft = true;
                FilteredDraftCount = FilteredSecrets.Count(n => n.HasDraft);
            }
            EditingSecretHasDraft = true;
        }
        finally
        {
            _saveDraftSemaphore.Release();
        }
    }

    private static void InsertSortedByTitle(ObservableCollection<SecretListItemViewModel> list, SecretListItemViewModel node)
    {
        var idx = list.TakeWhile(x => string.Compare(x.Title, node.Title, StringComparison.CurrentCultureIgnoreCase) < 0).Count();
        list.Insert(idx, node);
    }

    private void RefreshListItem(SecretEditModel m)
    {
        var newDomain = ExtractDomain(m.Website);
        // Never assign m.Title itself into a SecretListItemViewModel.Title below: that getter returns
        // TitleBuf's cached display string, which gets zeroed in place the next time m is Dispose()'d
        // (selection change, lock, etc.) - see the comment in GetOrCreatePoolItem(SecretEditModel, ...).
        var freshTitle = new string(m.TitleBuf.Span);
        // catNode.Secrets and FilteredSecrets hold references to the SAME pooled SecretListItemViewModel
        // instance per Id (see GetOrCreatePoolItem), so the "did the title change" check must be captured
        // once, before either branch below mutates .Title in place - otherwise whichever branch runs
        // first (the category-tree branch always runs before the flat-list branch) overwrites the shared
        // object's Title, and the second branch's own before/after comparison always reads "no change",
        // silently skipping its own re-sort even though the title did change.
        var oldTitle = _itemPool.TryGetValue(m.Id, out var pooledExisting) ? pooledExisting.Title : null;
        bool titleActuallyChanged = oldTitle == null || !string.Equals(oldTitle, freshTitle, StringComparison.CurrentCultureIgnoreCase);
        bool found = false;
        foreach (var catNode in CategoryItems)
        {
            var existing = catNode.Secrets.FirstOrDefault(x => x.Id == m.Id);
            if (existing != null)
            {
                found = true;
                existing.Title = freshTitle;
                existing.ExpiresAt = m.ExpiresAt?.DateTime;
                if (catNode.Code != m.CategoryNum)
                {
                    catNode.Secrets.Remove(existing);
                    var newCatNode = CategoryItems.FirstOrDefault(c => c.Code == m.CategoryNum);
                    if (newCatNode != null)
                        InsertSortedByTitle(newCatNode.Secrets, new SecretListItemViewModel { Id = m.Id, Title = freshTitle, CategoryNum = m.CategoryNum, ExpiresAt = m.ExpiresAt?.DateTime, WebsiteDomain = newDomain });
                }
                else
                {
                    if (existing.WebsiteDomain != newDomain)
                    {
                        existing.WebsiteDomain = newDomain;
                        existing.FaviconSource = null;
                    }
                    // Title changed: re-sort by removing and re-inserting at the correct alphabetical position
                    // (mutating existing.Title in place leaves it at its old sort position until the next full reload).
                    if (titleActuallyChanged)
                    {
                        catNode.Secrets.Remove(existing);
                        InsertSortedByTitle(catNode.Secrets, existing);
                    }
                }
                break;
            }
        }
        if (!found)
        {
            var targetCat = CategoryItems.FirstOrDefault(c => c.Code == m.CategoryNum);
            if (targetCat != null)
                InsertSortedByTitle(targetCat.Secrets, GetOrCreatePoolItem(m, newDomain));
        }

        var today2     = DateTime.Today;
        var warnLimit2 = today2.AddDays(AppConstants.SecretPasswordWarnDays);
        bool showInFilter = (!FilterCategoryCode.HasValue || FilterCategoryCode == m.CategoryNum)
                         && (!FilterFavoritesOnly || m.IsFavorite)
                         && (!FilterExpiredOnly   || (m.ExpiresAt.HasValue && m.ExpiresAt.Value.Date < warnLimit2));
        var flatExisting = FilteredSecrets.FirstOrDefault(x => x.Id == m.Id);
        if (flatExisting != null)
        {
            if (showInFilter)
            {
                flatExisting.Title = freshTitle;
                flatExisting.IsFavorite = m.IsFavorite;
                flatExisting.ExpiresAt = m.ExpiresAt?.DateTime;
                if (flatExisting.WebsiteDomain != newDomain)
                {
                    flatExisting.WebsiteDomain = newDomain;
                    flatExisting.FaviconSource = null;
                }
                // Same re-sort rationale as catNode.Secrets above.
                if (titleActuallyChanged)
                {
                    FilteredSecrets.Remove(flatExisting);
                    InsertSortedByTitle(FilteredSecrets, flatExisting);
                }
            }
            else FilteredSecrets.Remove(flatExisting);
        }
        else if (showInFilter)
        {
            InsertSortedByTitle(FilteredSecrets, GetOrCreatePoolItem(m, newDomain));
        }
        UpdateCountLabel();
        var todayR = DateTime.Today;
        var warnR  = todayR.AddDays(AppConstants.SecretPasswordWarnDays);
        FilteredExpiredCount     = FilteredSecrets.Count(x => x.ExpiresAt.HasValue && x.ExpiresAt.Value.Date < warnR);
        HasFilteredExpiredPastDue = FilteredSecrets.Any(x => x.ExpiresAt.HasValue && x.ExpiresAt.Value.Date < todayR);
        FilteredFavoritesCount   = FilteredSecrets.Count(x => x.IsFavorite);
    }

    private void RemoveFromList(int secretId)
    {
        foreach (var catNode in CategoryItems)
        {
            var node = catNode.Secrets.FirstOrDefault(x => x.Id == secretId);
            if (node != null) { catNode.Secrets.Remove(node); break; }
        }
        var flatNode = FilteredSecrets.FirstOrDefault(x => x.Id == secretId);
        if (flatNode != null) FilteredSecrets.Remove(flatNode);
        _itemPool.Remove(secretId);
        UpdateCountLabel();
        var todayR = DateTime.Today;
        var warnR  = todayR.AddDays(AppConstants.SecretPasswordWarnDays);
        FilteredExpiredCount     = FilteredSecrets.Count(x => x.ExpiresAt.HasValue && x.ExpiresAt.Value.Date < warnR);
        HasFilteredExpiredPastDue = FilteredSecrets.Any(x => x.ExpiresAt.HasValue && x.ExpiresAt.Value.Date < todayR);
        FilteredFavoritesCount   = FilteredSecrets.Count(x => x.IsFavorite);
    }

    private static bool TryBuildSafeUrl(string raw, out string url)
    {
        var candidate = raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? raw : "https://" + raw;

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            url = candidate;
            return true;
        }
        url = string.Empty;
        return false;
    }

    private async Task ApplyGenDatesAsync(SecretEditModel em, int secretId)
    {
        em.Gen1Date = string.Empty;
        em.Gen2Date = string.Empty;
        var (_, gen1At, _, gen2At) = await _secretHistory.GetSnapshotsAsync(secretId);
        if (gen1At.HasValue) em.Gen1Date = gen1At.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
        if (gen2At.HasValue) em.Gen2Date = gen2At.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    }

    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(List<CustomFieldModel>))]
    internal partial class SecretListJsonContext : JsonSerializerContext { }

    // Same source-generated type metadata as SecretListJsonContext.Default, but with the encoder
    // FieldCrypto.SealJson uses for the actual DB write - so JsonSerializer.Serialize calls made
    // outside of SealJson (change detection, LabelOverrides JSON, draft buffer staging) produce
    // byte-for-byte the same JSON the encrypted blob holds, instead of \uXXXX-escaping non-ASCII
    // via the default encoder and comparing unequal to what's actually stored.
    private static readonly SecretListJsonContext RelaxedJsonContext =
        new(new JsonSerializerOptions { Encoder = FieldCrypto.FullUnicodeEncoder });

    // ─── Favicon ───────────────────────────────────────────────────────────

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

    // Invoked fire-and-forget (`_ = PrefetchAllFaviconsAsync()`). The loop makes one HTTP fetch per
    // distinct domain, so it routinely outlives the seconds after a page load; a lock landing meanwhile
    // makes the next DB access throw InvalidOperationException ("No active vault DB is set"). Catch
    // it here instead of letting it become a silent UnobservedTaskException.
    private async Task PrefetchAllFaviconsAsync()
    {
        try
        {
            var key = _session.GetKey();
            var domains = _searchIndex
                .Select(s => s.WebsiteDomain)
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var domain in domains)
                await _favicon.PrefetchAsync(domain!, key);
            await LoadFaviconsAsync(_searchIndex.ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[PrefetchAllFaviconsAsync] Favicon prefetch stopped due to an error. [{ExType}]", ex.GetType().Name);
        }
    }

    // Iterates _searchIndex (every secret, unfiltered) rather than FilteredSecrets - an active
    // search/category/favorites filter at call time must not permanently exclude an item from ever
    // getting its favicon populated, since this only runs once per LoadAsync/PrefetchAllFaviconsAsync
    // and is never re-triggered just because the filter later changes. GetOrCreatePoolItem guarantees
    // a stable pooled instance exists for the target even if it isn't currently in FilteredSecrets.
    private async Task LoadFaviconsAsync(List<SearchEntry> entries)
    {
        var key = _session.GetKey();
        foreach (var entry in entries.Where(e => !string.IsNullOrEmpty(e.WebsiteDomain)))
        {
            // One domain's cache lookup failing (DB lock, transient I/O) must not stop every
            // remaining item in this batch from getting its favicon.
            try
            {
                var bytes = await _favicon.GetCachedAsync(entry.WebsiteDomain!, key);
                if (bytes == null) continue;
                await SetItemFaviconOnUiAsync(GetOrCreatePoolItem(entry), bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[LoadFaviconsAsync] Skipped a domain due to an error. [{ExType}]", ex.GetType().Name);
            }
        }
    }

    // Invoked fire-and-forget after a save (`_ = PrefetchAndUpdateNodeAsync(...)`); same lock-mid-fetch
    // race as PrefetchAllFaviconsAsync above.
    private async Task PrefetchAndUpdateNodeAsync(string domain, int secretId)
    {
        try
        {
            var key = _session.GetKey();
            await _favicon.PrefetchAsync(domain, key);
            var bytes = await _favicon.GetCachedAsync(domain, key);
            if (bytes == null) return;
            if (_itemPool.TryGetValue(secretId, out var node))
                await SetItemFaviconOnUiAsync(node, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[PrefetchAndUpdateNodeAsync] Favicon update stopped due to an error. [{ExType}]", ex.GetType().Name);
        }
    }

    private Task SetItemFaviconOnUiAsync(SecretListItemViewModel node, byte[] data)
        => _dispatcher.EnqueueAsync(async () =>
        {
            try
            {
                using var ms = new System.IO.MemoryStream(data);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                node.FaviconSource = bmp;
            }
            catch { /* Ignore network unavailability / decode failures */ }
        });
}

public static class PasswordGenerator
{
    public const string DefaultSymbols = "!@#$%^&*";
    public const string AllowedSymbols = "!@#$%^&*-_+=?.";
    private const string Upper  = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower  = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";

    /// <summary>
    /// Writes in-place into a POH-pinned Span&lt;char&gt; provided by the caller.
    /// Allocation-free since no raw char[] is returned (no return value).
    /// </summary>
    public static void Generate(Span<char> output, bool useUpper = true, bool useLower = true, bool useDigits = true, bool useSymbols = true, string? customSymbols = null)
    {
        if (output.IsEmpty) return;
        var symbols = string.IsNullOrEmpty(customSymbols) ? DefaultSymbols : customSymbols;
        var pool = string.Concat(
            useUpper   ? Upper   : "",
            useLower   ? Lower   : "",
            useDigits  ? Digits  : "",
            useSymbols ? symbols : "");
        if (pool.Length == 0)
            throw new ArgumentException("At least one character class must be enabled.");

        // Write at least one character from each enabled character class directly to the start of output
        int pos = 0;
        if (useUpper   && pos < output.Length) output[pos++] = PickOne(Upper);
        if (useLower   && pos < output.Length) output[pos++] = PickOne(Lower);
        if (useDigits  && pos < output.Length) output[pos++] = PickOne(Digits);
        if (useSymbols && symbols.Length > 0 && pos < output.Length) output[pos++] = PickOne(symbols);

        // Fill the rest randomly from the pool
        int rest = output.Length - pos;
        if (rest > 0)
        {
            var bytes = new byte[rest * 4];
            RandomNumberGenerator.Fill(bytes);
            for (int i = 0; i < rest; i++)
                output[pos + i] = pool[(int)(BitConverter.ToUInt32(bytes, i * 4) % (uint)pool.Length)];
            CryptographicOperations.ZeroMemory(bytes.AsSpan());
        }

        // Disperse the position of required characters via a Fisher-Yates shuffle
        var shuffleBytes = new byte[output.Length * 4];
        RandomNumberGenerator.Fill(shuffleBytes);
        for (int i = output.Length - 1; i > 0; i--)
        {
            int j = (int)(BitConverter.ToUInt32(shuffleBytes, i * 4) % (uint)(i + 1));
            (output[i], output[j]) = (output[j], output[i]);
        }
        CryptographicOperations.ZeroMemory(shuffleBytes.AsSpan());
    }

    private static char PickOne(string charset)
    {
        var bytes = new byte[4];
        RandomNumberGenerator.Fill(bytes);
        return charset[(int)(BitConverter.ToUInt32(bytes, 0) % (uint)charset.Length)];
    }
}
