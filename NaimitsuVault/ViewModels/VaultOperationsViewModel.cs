// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace NaimitsuVault.ViewModels;

public partial class VaultOperationsViewModel : ObservableObject, IDisposable
{
    private const string DeleteGlyph = "\uE74D";

    private readonly IDbContextFactory<AppDbContext> _vaultFactory;
    private readonly IAuthService _auth;
    private readonly IVaultConnectionProvider _connectionProvider;
    private readonly SecretRepository _secrets;
    private readonly SecretHistoryRepository _secretHistory;
    private readonly ICryptoService _crypto;
    private readonly AppSession _session;
    private readonly IAppNotificationService _notification;
    private readonly IDialogService _dialog;
    private readonly ILockService _appService;
    private readonly AutoBackupService _autoBackup;
    private readonly IFilePickerService _filePicker;
    private readonly IAuditLogService _auditLog;
    private readonly IdleTimeoutService _autoLock;

    // ── Sensitive buffers (zero-cleared by Dispose / ClearSensitiveInputBuffers) ────

    private readonly SecureCharBuffer _oldPasswordBuf = new();
    private readonly SecureCharBuffer _newPasswordBuf = new();
    private readonly SecureCharBuffer _confirmPwBuf   = new();
    private readonly SecureCharBuffer _eacPinBuf      = new();
    private readonly SecureCharBuffer _eacPinCfmBuf   = new();
    private readonly ILogger<VaultOperationsViewModel> _logger;

    private List<int> _availableVaultNumbers = [];

    // ── Bindable properties ─────────────────────────────────────────────

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanIncludeTotpInExport))]
    public partial bool HasSecrets { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanIncludeTotpInExport))]
    public partial string ExportFormat { get; set; } = "JSON";
    [ObservableProperty] public partial bool ExportIncludeTotp { get; set; }

    // CSV export/import has no totpSecret column at all (see VaultImportExportHelper.WriteCsvToStreamAsync
    // and ImportFromCsvAsync), so the toggle would silently do nothing while still looking actionable.
    public bool CanIncludeTotpInExport => HasSecrets && ExportFormat == "JSON";

    partial void OnExportFormatChanged(string value)
    {
        if (value != "JSON") ExportIncludeTotp = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmergencyAccessCodeError))]
    public partial string? EmergencyAccessCodeErrorMessage { get; set; }
    public bool HasEmergencyAccessCodeError => !string.IsNullOrEmpty(EmergencyAccessCodeErrorMessage);

    [ObservableProperty] public partial bool HasEmergencyAccessCode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddVaultError))]
    public partial string? AddVaultErrorMessage { get; set; }
    public bool HasAddVaultError => !string.IsNullOrEmpty(AddVaultErrorMessage);

    [ObservableProperty] public partial ObservableCollection<string> AvailableVaultLabels { get; set; } = [];
    [ObservableProperty] public partial int SelectedNewVaultLabelIndex { get; set; } = 0;
    public bool CanAddVault => AvailableVaultLabels.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPasswordError))]
    public partial string? PasswordErrorMessage { get; set; }
    public bool HasPasswordError => !string.IsNullOrEmpty(PasswordErrorMessage);

    // ── Password box (getter = empty, setter → POH buffer) ──────────────

    public string OldPassword
    {
        get => string.Empty;
        set
        {
            if (!MemoryExtensions.SequenceEqual(_oldPasswordBuf.Span, value.AsSpan()))
            { _oldPasswordBuf.SetFromSpan(value.AsSpan()); OnPropertyChanged(); ChangePasswordCommand.NotifyCanExecuteChanged(); }
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
        }
    }
    public string NewPassword
    {
        get => string.Empty;
        set
        {
            if (!MemoryExtensions.SequenceEqual(_newPasswordBuf.Span, value.AsSpan()))
            {
                _newPasswordBuf.SetFromSpan(value.AsSpan()); OnPropertyChanged();
                ChangePasswordCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanResetPasswordWithHelloFields));
            }
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
        }
    }
    public string ConfirmNewPassword
    {
        get => string.Empty;
        set
        {
            if (!MemoryExtensions.SequenceEqual(_confirmPwBuf.Span, value.AsSpan()))
            {
                _confirmPwBuf.SetFromSpan(value.AsSpan()); OnPropertyChanged();
                ChangePasswordCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanResetPasswordWithHelloFields));
            }
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
        }
    }
    public string EacPin
    {
        get => string.Empty;
        set
        {
            if (!MemoryExtensions.SequenceEqual(_eacPinBuf.Span, value.AsSpan()))
            { _eacPinBuf.SetFromSpan(value.AsSpan()); OnPropertyChanged(); CreateEmergencyAccessCodeCommand.NotifyCanExecuteChanged(); }
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
        }
    }
    public string EacPinConfirm
    {
        get => string.Empty;
        set
        {
            if (!MemoryExtensions.SequenceEqual(_eacPinCfmBuf.Span, value.AsSpan()))
            { _eacPinCfmBuf.SetFromSpan(value.AsSpan()); OnPropertyChanged(); CreateEmergencyAccessCodeCommand.NotifyCanExecuteChanged(); }
            if (value.Length > 0) SecurePasswordHelper.ZeroStringInternals(value);
        }
    }

    // ── Events / callbacks ─────────────────────────────────────────────

    public event EventHandler? PasswordChangeSucceeded;
    public event EventHandler? EmergencyAccessCodeCreated;

    /// <summary>New vault password input dialog. Confirm → (pw, confirm); cancel → null.</summary>
    internal Func<Task<(char[] pw, char[] confirm)?>>? AskNewVaultPasswordAsync { get; set; }

    /// <summary>
    /// Delegate that lets SettingsPage control BackupProgressWindow's modal display.
    /// Takes (message, work) and executes work() while blocking ShellWindow input and showing BackupProgressWindow.
    /// </summary>
    internal Func<string, Func<Task>, Task>? RunWithBackupProgressAsync { get; set; }

    public VaultOperationsViewModel(
        IDbContextFactory<AppDbContext> vaultFactory,
        IAuthService auth,
        IVaultConnectionProvider connectionProvider,
        SecretRepository secrets,
        SecretHistoryRepository secretHistory,
        ICryptoService crypto,
        AppSession session,
        IAppNotificationService notification,
        IDialogService dialog,
        ILockService appService,
        AutoBackupService autoBackup,
        IFilePickerService filePicker,
        IAuditLogService auditLog,
        IdleTimeoutService autoLock,
        ILogger<VaultOperationsViewModel> logger)
    {
        _logger = logger;
        _vaultFactory = vaultFactory;
        _auth = auth;
        _connectionProvider = connectionProvider;
        _secrets = secrets;
        _secretHistory = secretHistory;
        _crypto = crypto;
        _session = session;
        _notification = notification;
        _dialog = dialog;
        _appService = appService;
        _autoBackup = autoBackup;
        _filePicker = filePicker;
        _auditLog = auditLog;
        _autoLock = autoLock;
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (value) _autoLock.Stop();
        else       _autoLock.ResetTimer();
    }

    // ── Lifecycle ────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        OldPassword = "";
        NewPassword = "";
        ConfirmNewPassword = "";
        PasswordErrorMessage = null;

        if (_session.IsUnlocked)
        {
            HasEmergencyAccessCode = await _auth.HasEmergencyAccessCodeAsync();
            await using var vdb = await _vaultFactory.CreateDbContextAsync();
            HasSecrets = await vdb.Secrets.AnyAsync(s => s.DeletedAt == null);
            await LoadAvailableVaultsAsync();
        }
        else
        {
            HasEmergencyAccessCode = false;
            HasSecrets = false;
        }
    }

    public async Task LoadAvailableVaultsAsync()
    {
        _availableVaultNumbers = await _auth.GetAvailableVaultNumbersAsync();
        AvailableVaultLabels.Clear();
        foreach (var n in _availableVaultNumbers)
            AvailableVaultLabels.Add($"{n}");
        SelectedNewVaultLabelIndex = 0;
        OnPropertyChanged(nameof(CanAddVault));
    }

    /// <summary>
    /// Zero-clears sensitive input buffers on lock. Also called from Dispose() (idempotent).
    /// Uses SecureCharBuffer.Dispose() rather than Zero(): Dispose() additionally zeroes any cached
    /// ToDisplayString() result and suppresses the finalizer, and remains safe to keep using the
    /// buffer afterward (SetFromSpan reallocates unconditionally - Dispose() sets no "disposed" flag).
    /// </summary>
    public void ClearSensitiveInputBuffers()
    {
        _oldPasswordBuf.Dispose();
        _newPasswordBuf.Dispose();
        _confirmPwBuf.Dispose();
        _eacPinBuf.Dispose();
        _eacPinCfmBuf.Dispose();
    }

    public void Dispose()
    {
        ClearSensitiveInputBuffers();
    }

    // ── Audit log ──────────────────────────────────────────────────────────

    private async Task LogSettingAsync(AuditEventCode code, AuditPayload? payload)
    {
        try { await _auditLog.LogAsync(code, payload, _session.GetKey()); }
        catch (Exception ex) { _logger.LogWarning("[VaultOps] Failed to log: {ExType}", ex.GetType().Name); }
    }

    private async Task TryLogAuthFailAsync()
    {
        try { await _auditLog.LogAuthFailedAsync(); }
        catch (Exception ex) { _logger.LogWarning("[VaultOps] Failed to log auth failure: {ExType}", ex.GetType().Name); }
    }

    // ── Export ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportSecretsAsync()
    {
        if (_session.IsReadOnlyRestricted) return;
        IsBusy = true;
        try
        {
            bool confirmed = await _dialog.ConfirmWithIconAsync(
                LocalizationManager.Get("VaultSettings.Dialog.ExportPlaintextWarning"),
                LocalizationManager.Get("VaultSettings.Dialog.PlaintextExportDangerAlert") + "\n\n" + LocalizationManager.Get("Common.WarningAutoLockNotice"),
                "\uE896",
                LocalizationManager.Get("Common.Export"),
                destructive: true,
                defaultToCancel: true);
            if (!confirmed) return;

            using var auth = await _dialog.ConfirmMasterAuthAsync(LocalizationManager.Get("VaultSettings.Dialog.AuthRequiredToProceed"));
            if (auth == null) return;

            bool verified;
            if (auth.IsHelloVerified)
            {
                verified = true;
            }
            else
            {
                try { verified = await _auth.UnlockAsync(auth.PasswordChars!.Span); }
                catch (Exception ex) { _logger.LogWarning("An exception occurred in UnlockAsync. Treating it as an authentication failure. [{ExType}]", ex.GetType().Name); verified = false; }
            }

            if (!verified)
            {
                _ = TryLogAuthFailAsync();
                _notification.Show(LK.Common_ErrorAuthFailed, LK.Common_ErrorIncorrectPassword, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
                return;
            }

            var ext = ExportFormat == "JSON" ? "json" : "csv";
            using var pathBuf = await _filePicker.SaveAsync(
                $"Naimitsu_vlt{_session.DisplayedVaultNumber}_export_{DateTime.Now:yyyyMMdd_HHmmss}",
                [(string.Format(LocalizationManager.Get("Common.FileFilter"), ExportFormat, ext), $".{ext}")]);
            if (pathBuf == null) return;

            var key = _session.GetKey();
            var all = await _secrets.GetAllAsync();

            int written = 0, skipped = 0;
            var failedIds = new List<int>();
            var guardedRecords = new List<(int Id, string Title)>();
            var runner = RunWithBackupProgressAsync;
            if (runner != null)
            {
                await runner(
                    LocalizationManager.Get("Common.SafeWritingLock"),
                    async () =>
                    {
                        (written, skipped, failedIds, guardedRecords) = ExportFormat == "JSON"
                            ? await WriteJsonAsync(all, key, pathBuf)
                            : await WriteCsvAsync(all, key, pathBuf);
                    });
            }
            else
            {
                (written, skipped, failedIds, guardedRecords) = ExportFormat == "JSON"
                    ? await WriteJsonAsync(all, key, pathBuf)
                    : await WriteCsvAsync(all, key, pathBuf);
            }

            await LogSettingAsync(AuditEventCode.PlaintextExportExecuted, new PlaintextExportExecutedPayload(ExportFormat, written));
            foreach (var failedId in failedIds)
                await LogSettingAsync(AuditEventCode.ExportItemFailed, new ExportItemFailedPayload(failedId));
            foreach (var (id, title) in guardedRecords)
                await LogSettingAsync(AuditEventCode.CsvFormulaGuardApplied, new CsvFormulaGuardAppliedPayload(id, title));

            // Export locks immediately afterward (see the loop below), so a toast notification
            // would be destroyed with the old window before the user could read it. A skip only
            // ever happens here, so it must be a blocking dialog shown before that lock - this is
            // the one and only chance to tell the user some records could not be exported.
            if (skipped > 0)
            {
                await _dialog.ShowInfoAsync(
                    LocalizationManager.Get("VaultSettings.Dialog.ExportPartialSuccessTitle"),
                    string.Format(LocalizationManager.Get("VaultSettings.Dialog.ExportPartialSuccessText"), skipped, all.Count));
            }
            _appService.LockAfterDataWrite();
        }
        catch (Exception ex)
        {
            _logger.LogError("ExportSecretsAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<(int written, int skipped, List<int> failedIds, List<(int Id, string Title)> guardedRecords)> WriteJsonAsync(List<Secret> secrets, DekScope key, SecureCharBuffer pathBuf)
    {
        await using var fs = new FileStream(new string(pathBuf.Span), FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, FileOptions.Asynchronous);
        var (written, skipped, failedIds) = await VaultImportExportHelper.WriteJsonToStreamAsync(secrets, _crypto, key, fs, ExportIncludeTotp);
        // The CSV formula-injection guard is CSV-specific (JSON is never opened by spreadsheet
        // software), so this list is always empty here - kept only so both formats share one tuple
        // shape for the ExportFormat ternary below.
        return (written, skipped, failedIds, []);
    }

    private async Task<(int written, int skipped, List<int> failedIds, List<(int Id, string Title)> guardedRecords)> WriteCsvAsync(List<Secret> secrets, DekScope key, SecureCharBuffer pathBuf)
    {
        await using var fs = new FileStream(new string(pathBuf.Span), FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, FileOptions.Asynchronous);
        return await VaultImportExportHelper.WriteCsvToStreamAsync(secrets, _crypto, key, fs);
    }

    // ── Import ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ImportSecretsAsync()
    {
        if (_session.IsReadOnlyRestricted) return;
        IsBusy = true;
        byte[]? rawBytes = null;
        ReadOnlyMemory<byte> utf8Data = default;
        try
        {
            var ext = ExportFormat == "JSON" ? "json" : "csv";
            using var pathBuf = await _filePicker.OpenAsync(
                [(string.Format(LocalizationManager.Get("Common.FileFilter"), ExportFormat, ext), $".{ext}")]);
            if (pathBuf == null) return;

            rawBytes = await File.ReadAllBytesAsync(new string(pathBuf.Span));
            utf8Data = ImportEncodingNormalizer.NormalizeToUtf8(rawBytes);

            bool confirmed = await _dialog.ConfirmWithIconAsync(
                LocalizationManager.Get("VaultSettings.Dialog.ImportPlaintextConfirm"),
                LocalizationManager.Get("VaultSettings.Dialog.PlaintextImportConfirmText"),
                "\uE898",
                LocalizationManager.Get("Common.Import"),
                defaultToCancel: true);
            if (!confirmed) return;

            using var auth = await _dialog.ConfirmMasterAuthAsync(LocalizationManager.Get("VaultSettings.Dialog.AuthRequiredToProceed"));
            if (auth == null) return;

            bool verified;
            if (auth.IsHelloVerified)
            {
                verified = true;
            }
            else
            {
                try { verified = await _auth.UnlockAsync(auth.PasswordChars!.Span); }
                catch (Exception ex) { _logger.LogWarning("An exception occurred in UnlockAsync. Treating it as an authentication failure. [{ExType}]", ex.GetType().Name); verified = false; }
            }

            if (!verified)
            {
                _ = TryLogAuthFailAsync();
                _notification.Show(LK.Common_ErrorAuthFailed, LK.Common_ErrorIncorrectPassword, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
                return;
            }

            using var busy = await _dialog.ShowBusyAsync(LocalizationManager.Get("Common.PleaseWait"));
            var key = _session.GetKey();
            var allSecrets = await _secrets.GetAllAsync();
            var existingTitles = allSecrets
                .Select(s => { using var sp = FieldCrypto.Open(s.Title, _crypto, key); return sp != null ? Encoding.UTF8.GetString(sp.Utf8) : string.Empty; })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Categories have no DB table - valid codes are whichever presets the active locale
            // defines, plus 0 (uncategorized).
            var validCodes = LocalizationManager.GetCategoryPresets().Select(p => p.Code).ToHashSet();
            validCodes.Add(0);

            var (total, renamed, recategorized, failed) = ExportFormat == "JSON"
                ? await ImportFromJsonAsync(utf8Data, key, existingTitles, validCodes)
                : await ImportFromCsvAsync(utf8Data, key, existingTitles, validCodes);

            var msg = string.Format(LocalizationManager.Get("VaultSettings.SuccessImportedCountReport"), total);
            if (renamed > 0 || recategorized > 0)
                msg += string.Format(LocalizationManager.Get("VaultSettings.SuccessImportDetailsReport"), renamed, recategorized);
            if (failed > 0)
                msg += string.Format(LocalizationManager.Get("VaultSettings.SuccessImportFailedCountReport"), failed);

            HasSecrets = true;
            if (total > 0) _autoBackup.MarkContentChanged();
            _notification.Show(LK.Common_ImportSuccess, msg, NotificationSeverity.Success, TimeSpan.FromSeconds(6));
            await LogSettingAsync(AuditEventCode.PlaintextImportExecuted, new PlaintextImportExecutedPayload(ExportFormat, total));
            WeakReferenceMessenger.Default.Send(new SecretsImportedMessage());
        }
        catch (DecoderFallbackException)
        {
            _logger.LogError("ImportSecretsAsync: ANSI encoding rejected.");
            _notification.Show(LK.Common_Error, LocalizationManager.Get("Common.ErrorInvalidEncoding"), NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogError("ImportSecretsAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            // rawBytes/utf8Data hold the entire import file's plaintext (every secret's password,
            // userId, notes, etc.) - utf8Data may alias rawBytes (BOM-stripped slice or the
            // no-BOM-UTF-8 fast path) or be a distinct converted array (UTF-16 source), so both are
            // zeroed independently; zeroing an already-zeroed overlapping range is harmless.
            if (rawBytes != null) CryptographicOperations.ZeroMemory(rawBytes.AsSpan());
            if (MemoryMarshal.TryGetArray(utf8Data, out var utf8Seg) && utf8Seg.Array != null)
                CryptographicOperations.ZeroMemory(utf8Seg.Array.AsSpan(utf8Seg.Offset, utf8Seg.Count));
            IsBusy = false;
        }
    }

    private async Task<(int total, int renamed, int recategorized, int failed)> ImportFromJsonAsync(
        ReadOnlyMemory<byte> utf8Data, DekScope key, HashSet<string> existingTitles, HashSet<int> validCodes)
    {
        if (!MemoryMarshal.TryGetArray(utf8Data, out var seg))
            throw new InvalidOperationException("Memory buffer is not array-backed.");
        using var ms = new MemoryStream(seg.Array!, seg.Offset, seg.Count, writable: false);
        int total = 0, renamed = 0, recategorized = 0, failed = 0, ordinal = 0;

        await foreach (var dto in JsonSerializer.DeserializeAsyncEnumerable(ms, ImportJsonContext.Default.ImportSecretDto))
        {
            ordinal++;
            if (dto == null) continue;

            var rawTitle = dto.Title?.Trim() ?? string.Empty;
            var resolvedTitle = VaultImportExportHelper.ResolveDuplicateTitle(rawTitle, existingTitles);
            if (!string.Equals(resolvedTitle, rawTitle, StringComparison.Ordinal)) renamed++;

            var (categoryNum, wasMapped) = VaultImportExportHelper.MapCategory(dto.Category, validCodes);
            if (wasMapped) recategorized++;

            string? customFieldsJson = dto.CustomFields.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? dto.CustomFields.GetRawText()
                : null;

            try
            {
                var entity = VaultImportExportHelper.BuildSecret(
                    _crypto,
                    resolvedTitle, categoryNum,
                    dto.UserId, dto.Password, dto.Website, dto.Email, dto.Notes, customFieldsJson,
                    dto.CreatedAt, dto.UpdatedAt, key, dto.IsFavorite, dto.ExpiresAt,
                    dto.TotpSecret, dto.TotpDigits ?? 6, dto.TotpPeriod ?? 30,
                    dto.TotpAlgorithm ?? TotpCalculator.DefaultAlgorithm);

                await _secrets.AddAsync(entity);
                await PushImportSnapshotAsync(entity, key);
                total++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Skip this record and continue with the rest, mirroring the export path's
                // skip-and-continue behavior - one bad record must not abort an otherwise-good import.
                // The record's own title can't be trusted as an identifier (the failure may be why
                // it's unreadable), so only the 1-based ordinal position in the file is logged.
                failed++;
                _logger.LogWarning("[ImportFromJson] Ordinal={Ordinal}: item failed to import. [{ExType}]", ordinal, ex.GetType().Name);
                await LogSettingAsync(AuditEventCode.ImportItemFailed, new ImportItemFailedPayload(ExportFormat, ordinal));
            }
            finally
            {
                // Always zero-clear plaintext PII, even if BuildSecret/AddAsync/PushImportSnapshotAsync throws
                if (!string.IsNullOrEmpty(rawTitle)) SecurePasswordHelper.ZeroStringInternals(rawTitle);
                if (dto.Title != null && !ReferenceEquals(dto.Title, rawTitle) && dto.Title.Length > 0)
                    SecurePasswordHelper.ZeroStringInternals(dto.Title);
                if (!ReferenceEquals(resolvedTitle, rawTitle) && resolvedTitle.Length > 0)
                    SecurePasswordHelper.ZeroStringInternals(resolvedTitle);
                if (dto.UserId     is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(dto.UserId);
                if (dto.Password   is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(dto.Password);
                if (dto.Website    is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(dto.Website);
                if (dto.Email      is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(dto.Email);
                if (dto.Notes      is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(dto.Notes);
                if (dto.TotpSecret is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(dto.TotpSecret);
                if (customFieldsJson is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(customFieldsJson);
            }
        }
        return (total, renamed, recategorized, failed);
    }

    private async Task<(int total, int renamed, int recategorized, int failed)> ImportFromCsvAsync(
        ReadOnlyMemory<byte> utf8Data, DekScope key, HashSet<string> existingTitles, HashSet<int> validCodes)
    {
        if (!MemoryMarshal.TryGetArray(utf8Data, out var seg))
            throw new InvalidOperationException("Memory buffer is not array-backed.");
        using var ms = new MemoryStream(seg.Array!, seg.Offset, seg.Count, writable: false);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        int total = 0, renamed = 0, recategorized = 0, failed = 0, ordinal = 0;

        await foreach (var fields in VaultImportExportHelper.ParseCsvRecordsAsync(reader))
        {
            ordinal++;
            // Ignore blank lines and delimiter-only lines (e.g. trailing rows added by spreadsheet apps).
            // A row with an empty Title but data in another column is still imported, so no record is lost.
            if (fields.Count < 1 || VaultImportExportHelper.IsBlankCsvRecord(fields)) continue;
            var title = fields[0].Trim();

            var resolvedTitle = VaultImportExportHelper.ResolveDuplicateTitle(title, existingTitles);
            if (resolvedTitle != title) renamed++;

            int? categoryRaw = fields.Count > 1 && int.TryParse(fields[1], out var c) ? c : (int?)null;
            var (categoryNum, wasMapped) = VaultImportExportHelper.MapCategory(categoryRaw, validCodes);
            if (wasMapped) recategorized++;

            string? userId       = fields.Count > 2  ? VaultImportExportHelper.NullIfEmpty(fields[2])  : null;
            string? password     = fields.Count > 3  ? VaultImportExportHelper.NullIfEmpty(fields[3])  : null;
            string? website      = fields.Count > 4  ? VaultImportExportHelper.NullIfEmpty(fields[4])  : null;
            string? email     = fields.Count > 5  ? VaultImportExportHelper.NullIfEmpty(fields[5])  : null;
            string? notes        = fields.Count > 6  ? VaultImportExportHelper.NullIfEmpty(fields[6])  : null;
            string? customFields = fields.Count > 7  ? VaultImportExportHelper.NullIfEmpty(fields[7])  : null;

            DateTime? createdAt = null, updatedAt = null;
            if (fields.Count > 8  && DateTime.TryParseExact(fields[8],  "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var ca)) createdAt = ca;
            if (fields.Count > 9  && DateTime.TryParseExact(fields[9],  "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var ua)) updatedAt = ua;
            bool isFavorite = fields.Count > 10 && bool.TryParse(fields[10], out var fav) && fav;
            DateTime? expiresAt = null;
            if (fields.Count > 11 && DateTime.TryParseExact(fields[11], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var ea)) expiresAt = ea;

            try
            {
                var entity = VaultImportExportHelper.BuildSecret(
                    _crypto,
                    resolvedTitle, categoryNum,
                    userId, password, website, email, notes, customFields,
                    createdAt, updatedAt, key, isFavorite, expiresAt);

                await _secrets.AddAsync(entity);
                await PushImportSnapshotAsync(entity, key);
                total++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Skip this record and continue with the rest, mirroring the export path's
                // skip-and-continue behavior - one bad record must not abort an otherwise-good import.
                // The record's own title can't be trusted as an identifier (the failure may be why
                // it's unreadable), so only the 1-based ordinal position in the file is logged.
                failed++;
                _logger.LogWarning("[ImportFromCsv] Ordinal={Ordinal}: item failed to import. [{ExType}]", ordinal, ex.GetType().Name);
                await LogSettingAsync(AuditEventCode.ImportItemFailed, new ImportItemFailedPayload(ExportFormat, ordinal));
            }
            finally
            {
                // Always zero-clear plaintext PII, even if BuildSecret/AddAsync/PushImportSnapshotAsync throws
                if (title.Length > 0) SecurePasswordHelper.ZeroStringInternals(title);
                if (!ReferenceEquals(resolvedTitle, title) && resolvedTitle.Length > 0)
                    SecurePasswordHelper.ZeroStringInternals(resolvedTitle);
                if (userId       is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(userId);
                if (password     is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(password);
                if (website      is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(website);
                if (email     is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(email);
                if (notes        is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(notes);
                if (customFields is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(customFields);
                // Exhaustively zero-clear fields' raw elements (including copies produced by Trim() etc. that became distinct instances from the individual variables above).
                foreach (var f in fields)
                    if (f is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(f);
            }
        }
        return (total, renamed, recategorized, failed);
    }

    private async Task PushImportSnapshotAsync(Secret entity, DekScope key)
    {
        using var pinnedBuf = new PinnedBufferWriter();
        using (var w = new Utf8JsonWriter(pinnedBuf))
            SnapshotSerializer.WriteEntity(w, entity, _crypto, key, []);
        var encBlob = _crypto.Encrypt(pinnedBuf.WrittenSpan, key.Span);
        var (rotated, sacrificedSlotLabel) = await _secretHistory.PushAsync(entity.Id, encBlob, entity.UpdatedAt);
        if (rotated)
        {
            await LogSettingAsync(AuditEventCode.TimeMachineGenRotated, new TimeMachineGenRotatedPayload(entity.Id));
            if (sacrificedSlotLabel != null)
                await LogSettingAsync(AuditEventCode.TimeMachineSlotDeleted, new TimeMachineSlotDeletedPayload(entity.Id, sacrificedSlotLabel));
        }
    }

    // ── Backup ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task BackupDatabaseAsync()
    {
        // Restricted read-only mode (emergency access code) prohibits writes, and automatic backup is
        // already suppressed there (AutoBackupService.IsReadOnlyRestricted). PerformUnifiedBackup itself does
        // not check that flag, so a manual backup must be refused here - with a warning, unlike the
        // silent guards elsewhere, because this is an explicit user action.
        if (_session.IsReadOnlyRestricted)
        {
            _notification.Show(LK.Common_Warning,
                LocalizationManager.Get("VaultSettings.WarningBackupBlockedReadOnlyAlert"),
                NotificationSeverity.Warning, TimeSpan.FromSeconds(5));
            return;
        }
        IsBusy = true;
        try
        {
            using var folderBuf = await _filePicker.OpenFolderAsync();
            if (folderBuf == null) return;

            var destFolder = new string(folderBuf.Span);

            bool backupSuccess = false;
            var runner = RunWithBackupProgressAsync;
            if (runner != null)
            {
                // Not Common.SafeWritingLock: that text promises an automatic lock on completion, which
                // only the plaintext export does. A manual backup leaves the session unlocked.
                await runner(
                    LocalizationManager.Get("Common.PleaseWait"),
                    async () => { backupSuccess = await Task.Run(() => _autoBackup.PerformUnifiedBackup(destFolder)); });
            }
            else
            {
                backupSuccess = await Task.Run(() => _autoBackup.PerformUnifiedBackup(destFolder));
            }

            if (!backupSuccess)
            {
                _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
                return;
            }

            try
            {
                if (File.Exists(AutoBackupService.FailureFlagPath))
                    File.Delete(AutoBackupService.FailureFlagPath);
            }
            catch { /* Ignore flag deletion failure */ }

            await LogSettingAsync(AuditEventCode.BackupExecuted, null);
            _notification.Show(LK.Common_SuccessSaveComplete,
                string.Format(LocalizationManager.GetById(LK.VaultSettings_SuccessBackupSavedToPath), destFolder),
                NotificationSeverity.Success, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            _logger.LogError("BackupDatabaseAsync failed. [{ExType}]", ex.GetType().Name);
            _notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Emergency access code ────────────────────────────────────────────────

    private bool CanCreateEmergencyAccessCode =>
        !_session.IsReadOnlyRestricted
        && _eacPinBuf.Span.Length == 4 && AllAsciiDigits(_eacPinBuf.Span)
        && _eacPinCfmBuf.Span.Length == 4 && AllAsciiDigits(_eacPinCfmBuf.Span);

    [RelayCommand(CanExecute = nameof(CanCreateEmergencyAccessCode))]
    private async Task CreateEmergencyAccessCodeAsync()
    {
        // Since CanExecute is only referenced by UI bindings, place the same internal guard as other
        // write methods as a defense against direct command invocation (structural block on restricted view mode)
        if (!CanCreateEmergencyAccessCode) return;
        EmergencyAccessCodeErrorMessage = null;

        if (!MemoryExtensions.SequenceEqual(_eacPinBuf.Span, _eacPinCfmBuf.Span))
        { EmergencyAccessCodeErrorMessage = LocalizationManager.Get("Common.ErrorPinMismatch"); return; }

        if (HasEmergencyAccessCode)
        {
            bool reissueConfirmed = await _dialog.ConfirmAsync(
                LocalizationManager.Get("VaultSettings.Dialog.ReissueEmergencyCodeConfirm"),
                LocalizationManager.Get("VaultSettings.Dialog.ReissueEmergencyCodeConfirmText"),
                defaultToCancel: true);
            if (!reissueConfirmed) return;
        }

        using var auth = await _dialog.ConfirmMasterAuthAsync(LocalizationManager.Get("VaultSettings.Dialog.AuthRequiredToProceed"));
        if (auth == null) return;

        bool verified;
        if (auth.IsHelloVerified)
        {
            verified = true;
        }
        else
        {
            try { verified = await _auth.UnlockAsync(auth.PasswordChars!.Span); }
            catch (Exception ex) { _logger.LogWarning("An exception occurred in UnlockAsync. Treating it as an authentication failure. [{ExType}]", ex.GetType().Name); verified = false; }
        }

        if (!verified)
        {
            _ = TryLogAuthFailAsync();
            _notification.Show(LK.Common_ErrorAuthFailed, LK.Common_ErrorIncorrectPassword, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
            return;
        }

        IsBusy = true;
        try
        {
            var (qrPayload, suggestedFileName, wrappedDek) = await _auth.GenerateEmergencyAccessCodeAsync(_eacPinBuf.Span);

            using var pathBuf = await _filePicker.SaveAsync(
                suggestedFileName,
                [(string.Format(LocalizationManager.Get("Common.FileFilter"), "PNG", "png"), ".png")]);
            if (pathBuf == null) return; // Cancelled: nothing committed yet, any existing code remains valid

            var pngBytes = await GenerateQrPngAsync(qrPayload);
            await File.WriteAllBytesAsync(new string(pathBuf.Span), pngBytes);

            await _auth.CommitEmergencyAccessCodeAsync(wrappedDek);

            EacPin = EacPinConfirm = "";
            HasEmergencyAccessCode = true;
            EmergencyAccessCodeCreated?.Invoke(this, EventArgs.Empty);
            await LogSettingAsync(AuditEventCode.EmergencyAccessCodeGenerated, null);
            _notification.Show(LK.Common_SuccessSaveComplete,
                string.Format(LocalizationManager.GetById(LK.VaultSettings_Dialog_QrCodeSavedToPath), new string(Path.GetFileName(pathBuf.Span))),
                NotificationSeverity.Success, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogError("CreateEmergencyAccessCodeAsync failed. [{ExType}]", ex.GetType().Name);
            EmergencyAccessCodeErrorMessage = LocalizationManager.Get("Common.GeneralError");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevokeEmergencyAccessCodeAsync()
    {
        if (_session.IsReadOnlyRestricted) return;

        bool confirmed = await _dialog.ConfirmDestructiveAsync(
            LocalizationManager.Get("Common.DeleteConfirm"),
            LocalizationManager.Get("VaultSettings.Dialog.DeleteEmergencyCodeConfirmText"),
            DeleteGlyph,
            LocalizationManager.Get("Common.Delete"),
            defaultToCancel: true);
        if (!confirmed) return;

        using var auth = await _dialog.ConfirmMasterAuthAsync(LocalizationManager.Get("VaultSettings.Dialog.AuthRequiredToProceed"));
        if (auth == null) return;

        bool verified;
        if (auth.IsHelloVerified)
        {
            verified = true;
        }
        else
        {
            try { verified = await _auth.UnlockAsync(auth.PasswordChars!.Span); }
            catch (Exception ex) { _logger.LogWarning("An exception occurred in UnlockAsync. Treating it as an authentication failure. [{ExType}]", ex.GetType().Name); verified = false; }
        }

        if (!verified)
        {
            _ = TryLogAuthFailAsync();
            _notification.Show(LK.Common_ErrorAuthFailed, LK.Common_ErrorIncorrectPassword, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
            return;
        }

        IsBusy = true;
        try
        {
            await _auth.RevokeEmergencyAccessCodeAsync();
            HasEmergencyAccessCode = false;
            await LogSettingAsync(AuditEventCode.EmergencyAccessCodeRevoked, null);
            _notification.Show(LK.Common_InfoDeleteComplete, LK.VaultSettings_Dialog_OldQrRevokedNotice, NotificationSeverity.Info, TimeSpan.FromSeconds(3));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task<byte[]> GenerateQrPngAsync(string content)
    {
        var writer = new ZXing.BarcodeWriterPixelData
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.QrCode.QrCodeEncodingOptions { Width = 400, Height = 400, Margin = 2 }
        };
        var pixelData = writer.Write(content);

        using var inMemStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, inMemStream);
        encoder.SetPixelData(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            (uint)pixelData.Width, (uint)pixelData.Height,
            96, 96, pixelData.Pixels);
        await encoder.FlushAsync();

        var dataReader = new Windows.Storage.Streams.DataReader(inMemStream.GetInputStreamAt(0));
        await dataReader.LoadAsync((uint)inMemStream.Size);
        var bytes = new byte[inMemStream.Size];
        dataReader.ReadBytes(bytes);
        return bytes;
    }

    // ── Password change ────────────────────────────────────────────────────

    private bool CanChangePassword =>
        _oldPasswordBuf.Span.Length >= 8 && _newPasswordBuf.Span.Length >= 8 && _confirmPwBuf.Span.Length >= 8;

    // Combined with AppSettingsVm.IsWindowsHelloEnabled (a different VM) via a code-behind
    // function bind in XAML, so it's exposed publicly rather than gating a RelayCommand directly.
    public bool CanResetPasswordWithHelloFields =>
        _newPasswordBuf.Span.Length >= 8 && _confirmPwBuf.Span.Length >= 8;

    [RelayCommand(CanExecute = nameof(CanChangePassword))]
    private async Task ChangePasswordAsync()
    {
        // Since CanExecute is only referenced by UI bindings, place the same internal guard as other
        // write methods as a defense against direct command invocation
        if (!CanChangePassword) return;
        PasswordErrorMessage = null;
        if (!MemoryExtensions.SequenceEqual(_newPasswordBuf.Span, _confirmPwBuf.Span))         { PasswordErrorMessage = LocalizationManager.Get("Common.ErrorPasswordMismatch"); return; }
        if (MemoryExtensions.SequenceEqual(_oldPasswordBuf.Span, _newPasswordBuf.Span))        { PasswordErrorMessage = LocalizationManager.Get("VaultSettings.Dialog.CannotReuseCurrentMasterPassword"); return; }

        IsBusy = true;
        try
        {
            var success = await _auth.ChangeMasterPasswordAsync(_oldPasswordBuf.Span, _newPasswordBuf.Span);
            OldPassword = NewPassword = ConfirmNewPassword = "";
            if (success)
            {
                PasswordChangeSucceeded?.Invoke(this, EventArgs.Empty);
                var message = LocalizationManager.GetById(LK.VaultSettings_Dialog_MasterPasswordChangedSuccessfully);
                if (HasEmergencyAccessCode)
                    message += "\n" + LocalizationManager.GetById(LK.VaultSettings_Dialog_EmergencyCodeRemainsValid);
                _notification.Show(LK.Common_SuccessSaveComplete, message, NotificationSeverity.Success, TimeSpan.FromSeconds(5));
                await LogSettingAsync(AuditEventCode.MasterPasswordChanged, null);
            }
            else
            {
                PasswordErrorMessage = LocalizationManager.Get("Common.ErrorIncorrectPassword");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("ChangePasswordAsync failed. [{ExType}]", ex.GetType().Name);
            PasswordErrorMessage = LocalizationManager.Get("Common.GeneralError");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResetPasswordWithHelloAsync()
    {
        // This command's enablement combines AppSettingsVm.IsWindowsHelloEnabled (a different VM)
        // with CanResetPasswordWithHelloFields via a code-behind function bind, so there's no
        // RelayCommand CanExecute here — guard the field-length condition as defense against
        // direct invocation instead.
        if (!CanResetPasswordWithHelloFields) return;
        PasswordErrorMessage = null;
        if (!MemoryExtensions.SequenceEqual(_newPasswordBuf.Span, _confirmPwBuf.Span)) { PasswordErrorMessage = LocalizationManager.Get("Common.ErrorPasswordMismatch"); return; }

        IsBusy = true;
        try
        {
            var success = await _auth.ResetMasterPasswordWithHelloAsync(_newPasswordBuf.Span);
            OldPassword = NewPassword = ConfirmNewPassword = "";
            if (success)
            {
                PasswordChangeSucceeded?.Invoke(this, EventArgs.Empty);
                var message = LocalizationManager.GetById(LK.VaultSettings_Dialog_PasswordResetViaHelloSuccessfully);
                if (HasEmergencyAccessCode)
                    message += "\n" + LocalizationManager.GetById(LK.VaultSettings_Dialog_EmergencyCodeRemainsValid);
                _notification.Show(LK.Common_SuccessSaveComplete, message, NotificationSeverity.Success, TimeSpan.FromSeconds(5));
                await LogSettingAsync(AuditEventCode.MasterPasswordChanged, null);
            }
            else
            {
                PasswordErrorMessage = LocalizationManager.Get("Common.ErrorHelloAuthFailed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("ResetPasswordWithHelloAsync failed. [{ExType}]", ex.GetType().Name);
            PasswordErrorMessage = LocalizationManager.Get("Common.GeneralError");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Add a new vault ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddVaultAsync()
    {
        if (_session.IsReadOnlyRestricted) return;
        AddVaultErrorMessage = null;
        IsBusy = true;
        char[]? pw = null;
        char[]? confirm = null;
        try
        {
            using var auth = await _dialog.ConfirmMasterAuthAsync(LocalizationManager.Get("VaultSettings.AuthRequiredToCreate"));
            if (auth == null) return;

            if (!auth.IsHelloVerified)
            {
                var matchingVaults = await _auth.AcquireKSharedAsync(auth.PasswordChars!.Span);
                if (matchingVaults.Count == 0)
                { AddVaultErrorMessage = LocalizationManager.Get("Common.ErrorIncorrectPassword"); return; }
            }

            var pwResult = await (AskNewVaultPasswordAsync?.Invoke() ?? Task.FromResult<(char[], char[])?>(null));
            if (pwResult == null) return;

            pw      = pwResult.Value.pw;
            confirm = pwResult.Value.confirm;

            if (pw.Length < 8)
            { AddVaultErrorMessage = LocalizationManager.Get("Common.ErrorPasswordTooShort"); return; }
            if (!CryptographicOperations.FixedTimeEquals(
                    MemoryMarshal.AsBytes(pw.AsSpan()),
                    MemoryMarshal.AsBytes(confirm.AsSpan())))
            { AddVaultErrorMessage = LocalizationManager.Get("Common.ErrorPasswordMismatch"); return; }

            int vaultNum = (_availableVaultNumbers.Count > SelectedNewVaultLabelIndex && SelectedNewVaultLabelIndex >= 0)
                ? _availableVaultNumbers[SelectedNewVaultLabelIndex] : 0;
            if (vaultNum == 0) { AddVaultErrorMessage = LocalizationManager.Get("Common.GeneralError"); return; }

            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            bool ok = await _auth.CreateNewVaultAsync(vaultNum, pw.AsSpan(), dataDir);
            if (ok)
            {
                _autoBackup.MarkContentChanged();
                // CreateNewVaultAsync already switched the session/DEK to the newly created vault on
                // success, so this writes into that NEW vault's own audit log (its first row ever) -
                // not the vault AddVaultAsync was invoked from. See AuditEventCode.VaultCreated.
                await LogSettingAsync(AuditEventCode.VaultCreated, null);
                await LoadAvailableVaultsAsync();
                _notification.Show(LK.VaultSettings_VaultManagement, LK.Common_SuccessSaveComplete, NotificationSeverity.Success, TimeSpan.FromSeconds(4));
                // CreateNewVaultAsync already switched session/DEK and ActiveVaultDbPath to the
                // newly created vault (see the comment above). The UI (SecretsPage etc.) still
                // shows the OLD vault's cached data, so any further edit would encrypt with the
                // new vault's DEK and write into the new vault's DB - cross-vault data corruption.
                // Force an immediate lock (same fail-safe used by ExportSecretsAsync) so the user
                // must re-unlock and every ViewModel reloads from a consistent session state.
                _appService.LockAfterDataWrite();
            }
            else { AddVaultErrorMessage = LocalizationManager.Get("Common.GeneralError"); }
        }
        catch (Exception ex)
        {
            _logger.LogError("AddVaultAsync failed. [{ExType}]", ex.GetType().Name);
            AddVaultErrorMessage = LocalizationManager.Get("Common.GeneralError");
        }
        finally
        {
            if (pw      != null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(pw.AsSpan()));
            if (confirm != null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(confirm.AsSpan()));
            IsBusy = false;
        }
    }

    private static bool AllAsciiDigits(ReadOnlySpan<char> span)
    {
        foreach (char c in span) if (c < '0' || c > '9') return false;
        return true;
    }
}

internal sealed record ImportSecretDto
{
    public string?      Title        { get; init; }
    public int?         Category     { get; init; }
    public string?      UserId       { get; init; }
    public string?      Password     { get; init; }
    public string?      Website      { get; init; }
    public string?      Email        { get; init; }
    public string?      Notes        { get; init; }
    public JsonElement  CustomFields { get; init; }
    public DateTime?    CreatedAt    { get; init; }
    public DateTime?    UpdatedAt    { get; init; }
    public bool         IsFavorite   { get; init; }
    public DateTime?    ExpiresAt    { get; init; }
    public string?      TotpSecret    { get; init; }
    public int?         TotpDigits    { get; init; }
    public int?         TotpPeriod    { get; init; }
    public string?      TotpAlgorithm { get; init; }
}

[JsonSerializable(typeof(List<ImportSecretDto>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ImportJsonContext : JsonSerializerContext { }
