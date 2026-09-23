// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.ViewModels;

public sealed partial class RestoreViewModel : ObservableObject
{
    private readonly IRestoreAuthService _auth;
    private readonly string              _localDataDir;
    private readonly ILogger<RestoreViewModel> _logger;
    private string                       _backupSourceDir = string.Empty;

    // ── State properties ────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool IsNotBusy => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
        RestoreCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupNkdb))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial string BackupNkdbPath { get; set; } = string.Empty;

    public bool HasBackupNkdb => !string.IsNullOrEmpty(BackupNkdbPath) && File.Exists(BackupNkdbPath);

    /// <summary>Fired on successful recovery. App.xaml.cs calls Restart() or TransitionToShell().</summary>
    public event EventHandler? RestoreSucceeded;

    public RestoreViewModel(IRestoreAuthService auth, ILogger<RestoreViewModel> logger)
    {
        _logger = logger;
        _auth         = auth;
        _localDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
    }

    // ── Backup file selection ──────────────────────────────────────────────

    public void LoadBackupPath(string path)
    {
        BackupNkdbPath   = path;
        _backupSourceDir = Path.GetDirectoryName(path) ?? string.Empty;
        StatusMessage    = string.Empty;
        IsError          = false;
        OnPropertyChanged(nameof(HasBackupNkdb));
    }

    // ── Step 2: pre-copy file validation ─────────────────────────────────────────

    /// <summary>
    /// Validates every backup file before anything else happens. Called directly from the
    /// code-behind's restore button handler, before the backup authentication dialog is shown.
    /// </summary>
    public async Task<bool> ValidateAsync()
    {
        StatusMessage = string.Empty;
        IsError        = false;

        try
        {
            await _auth.ValidateBackupFilesAsync(BackupNkdbPath, _backupSourceDir);
            return true;
        }
        catch (RestoreValidationException ex)
        {
            _logger.LogError("[ValidateAsync] Backup file failed validation; restore aborted. File={File}", ex.FileName);
            SetStatus(string.Format(LocalizationManager.Get("Restore.ErrorInvalidBackupFileAborted"), ex.FileName), isError: true);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError("[ValidateAsync] Validation failed. [{ExType}]", ex.GetType().Name);
            SetStatus($"{LocalizationManager.Get("Common.Error")}: {LocalizationManager.Get("Common.GeneralError")}", isError: true);
            return false;
        }
    }

    // ── Step 3: backup authentication gate ─────────────────────────

    /// <summary>
    /// Verifies the password against the backup unified database. Called directly from the
    /// code-behind's backup authentication dialog. The recovered K_shared is only a proof of
    /// ownership here - the restore copy itself is password-independent - so it is zeroed and
    /// discarded immediately rather than kept for later use.
    /// </summary>
    public async Task<bool> AuthenticateAsync(char[] password)
    {
        if (string.IsNullOrEmpty(BackupNkdbPath)) return false;

        StatusMessage = string.Empty;
        IsError        = false;

        try
        {
            var kShared = await _auth.AuthenticateBackupNkdbAsync(BackupNkdbPath, password);
            if (kShared == null)
            {
                SetStatus(LocalizationManager.Get("Common.ErrorPasswordMismatch"), isError: true);
                return false;
            }

            CryptographicOperations.ZeroMemory(kShared);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("[AuthenticateAsync] Backup authentication failed. [{ExType}]", ex.GetType().Name);
            SetStatus($"{LocalizationManager.Get("Common.Error")}: {LocalizationManager.Get("Common.GeneralError")}", isError: true);
            return false;
        }
    }

    // ── Step 4: first guard trigger condition ─────────────────────────

    /// <summary>Whether the destructive overwrite-confirmation dialog needs to be shown at all.</summary>
    public bool HasExistingLocalData() => _auth.HasExistingLocalData(_localDataDir);

    // ── Step 5-7: archive, overwrite copy, Windows Hello invalidation ────────────────────

    [RelayCommand(CanExecute = nameof(CanRestore))]
    public async Task RestoreAsync()
    {
        IsBusy        = true;
        StatusMessage = string.Empty;
        IsError       = false;

        try
        {
            var (count, nkdbCopied) = await _auth.RestoreAllFromBackupAsync(BackupNkdbPath, _backupSourceDir, _localDataDir);

            if (count > 0)
            {
                // If copying the unified database itself fails, the local VaultRegistries remain
                // in their old state from before the intended swap, so reading DbNumber from it
                // would not match what was actually restored (misattribution). Record only on copy success.
                if (nkdbCopied)
                {
                    var dbNumbers = await _auth.GetLocalVaultDbNumbersAsync();
                    RestoreAuditMarker.Write(_localDataDir, AuditEventCode.RestoreExecuted, dbNumbers);
                }
                SetStatus(string.Format(LocalizationManager.Get("Common.SuccessSaveComplete"), count), isError: false);
                RestoreSucceeded?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                SetStatus(LocalizationManager.Get("Restore.NoFilesFoundAlert"), isError: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("[RestoreAsync] Restore failed. [{ExType}]", ex.GetType().Name);
            SetStatus($"{LocalizationManager.Get("Common.Error")}: {LocalizationManager.Get("Common.GeneralError")}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRestore() => !IsBusy && HasBackupNkdb;

    private void SetStatus(string msg, bool isError)
    {
        IsError       = isError;
        StatusMessage = msg;
    }
}
