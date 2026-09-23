// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
namespace NaimitsuVault.ViewModels;

public partial class UnlockViewModel : ObservableObject
{
    private readonly IAuthService _auth;
    private readonly AppSession _session;

    public bool IsSetupMode { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    public partial int PasswordLength { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    public partial int ConfirmLength { get; set; }

    internal Func<char[]>? GetPasswordChars { get; set; }
    internal Func<char[]>? GetConfirmChars  { get; set; }

    /// <summary>Called when multiple vaults match after password verification or Hello authentication. Returns the selected DB number, or null on cancel.</summary>
    internal Func<IReadOnlyList<int>, Task<int?>>? SelectVaultAsync { get; set; }

    /// <summary>Called when a Windows Hello registration cannot be used on this device/account. Returns true if the user confirmed deletion.</summary>
    internal Func<Task<bool>>? ConfirmInvalidateHelloAsync { get; set; }

    [ObservableProperty] public partial bool IsWindowsHelloAvailable { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnlockWithWindowsHelloCommand))]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    public bool IsNotBusy => !IsBusy;

    // Argon2id runs off the UI thread now, so the UI stays live while a password unlock is in flight.
    // Closing the window cancels this token so a derivation that finishes afterwards is discarded
    // (wiped) instead of being written into the session of a window nobody is looking at any more.
    private readonly CancellationTokenSource _authCts = new();
    private readonly ILogger<UnlockViewModel> _logger;

    /// <summary>Cancels any in-flight password unlock. Called when the window is closed without a successful unlock.</summary>
    internal void CancelPendingAuth() => _authCts.Cancel();

    public bool IsUnlockMode => !IsSetupMode;

    public bool IsSuccess { get; private set; }
    public event EventHandler? Succeeded;

    public UnlockViewModel(IAuthService auth, bool isSetupMode, AppSession session, ILogger<UnlockViewModel> logger)
    {
        _logger = logger;
        _auth = auth;
        _session = session;
        IsSetupMode = isSetupMode;

        if (!IsSetupMode)
        {
            _ = CheckWindowsHelloAvailabilityAsync();
        }
    }

    private async Task CheckWindowsHelloAvailabilityAsync()
    {
        try
        {
            IsWindowsHelloAvailable = await _auth.IsWindowsHelloConfiguredForUnlockAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError("[UnlockViewModel] An exception occurred while getting Windows Hello status. [{ExType}]", ex.GetType().Name);
            IsWindowsHelloAvailable = false;
        }
    }


    private bool CanExecute => !IsBusy && (IsSetupMode
        ? PasswordLength >= 8 && PasswordLength == ConfirmLength
        : PasswordLength >= 8);

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        // CanExecute already covers Execute(); this also covers a direct ExecuteAsync call.
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        char[]? passwordChars = null;
        char[]? confirmChars  = null;
        try
        {
            passwordChars = GetPasswordChars?.Invoke() ?? [];
            if (IsSetupMode) confirmChars = GetConfirmChars?.Invoke() ?? [];

            bool success = IsSetupMode
                ? await SetupAsync(passwordChars, confirmChars!)
                : await UnlockAsync(passwordChars);
            if (success)
            {
                IsSuccess = true;
                ScrubSensitiveProperties();
                Succeeded?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            if (passwordChars != null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(passwordChars.AsSpan()));
            if (confirmChars  != null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(confirmChars.AsSpan()));
            IsBusy = false;
        }
    }

    // Not available while another unlock is in flight. With the UI thread no longer blocked by
    // Argon2id, nothing else would stop the user starting a second, concurrent unlock here.
    private bool CanUnlockWithWindowsHello => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUnlockWithWindowsHello))]
    private async Task UnlockWithWindowsHelloAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

            // Step 1: Hello authentication + acquire K_shared
            var registeredVaults = await _auth.AcquireKSharedWithHelloAsync();
            if (registeredVaults.Count == 0)
            {
                ErrorMessage = _session.VaultDbUnrecoverable
                    ? string.Format(LocalizationManager.Get("Unlock.ErrorVaultDbCorrupted"), _session.VaultDbUnrecoverableNumber)
                    : LocalizationManager.Get("Common.ErrorHelloAuthFailed");
                return;
            }

            // At least one vault is usable, but the scan may have silently dropped a different vault
            // along the way (AcquireKSharedWithHelloAsync excludes it rather than failing the whole
            // scan) for one of two reasons - surface whichever applies now, before EnterVaultWithHelloAsync
            // clears both flags at its own top for the vault the user actually enters:
            //   - Unrecoverable: no usable shadow was found. Shares the exact same corruption check
            //     with the password path, so it would fail identically there too - not Hello-only.
            //   - Auto-recovered but still excluded: the shadow that fixed it predates when Hello was
            //     enabled on that vault (or otherwise lacks VaultDEKHello), so the restored file
            //     legitimately has no Hello registration anymore. Without this notice the user has no
            //     way to learn why that vault's Hello option vanished.
            if (_session.VaultDbUnrecoverable)
            {
                ErrorMessage = string.Format(LocalizationManager.Get("Unlock.ErrorVaultDbCorrupted"), _session.VaultDbUnrecoverableNumber);
            }
            else if (_session.VaultDbAutoRecovered)
            {
                ErrorMessage = string.Format(LocalizationManager.Get("Common.InfoVaultDbAutoRecovered"), _session.VaultDbAutoRecoveredNumber);
            }

            // Step 2: Vault selection (dialog if multiple; number 0 is permanently forbidden)
            int vaultNum;
            if (registeredVaults.Count == 1 || SelectVaultAsync == null)
            {
                vaultNum = registeredVaults[0];
            }
            else
            {
                var chosen = await SelectVaultAsync(registeredVaults);
                if (chosen == null) return; // Cancelled (no error)
                vaultNum = chosen.Value;
            }

            // Step 3: Enter the vault's DEK via Hello
            if (await _auth.EnterVaultWithHelloAsync(vaultNum, dataDir))
            {
                IsSuccess = true;
                ScrubSensitiveProperties();
                Succeeded?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ErrorMessage = _session.VaultDbUnrecoverable
                    ? string.Format(LocalizationManager.Get("Unlock.ErrorVaultDbCorrupted"), _session.VaultDbUnrecoverableNumber)
                    : LocalizationManager.Get("Common.ErrorHelloAuthFailed");
            }
        }
        catch (WindowsHelloProfileMismatchException)
        {
            ErrorMessage = LocalizationManager.Get("Common.ErrorHelloProfileMismatch");
            if (ConfirmInvalidateHelloAsync != null && await ConfirmInvalidateHelloAsync())
            {
                var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                await _auth.InvalidateWindowsHelloEverywhereAsync(dataDir);
                IsWindowsHelloAvailable = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("[UnlockViewModel] UnlockWithWindowsHello failed. [{ExType}]", ex.GetType().Name);
            ErrorMessage = LocalizationManager.Get("Common.GeneralError");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> SetupAsync(char[] passwordChars, char[] confirmChars)
    {
        if (passwordChars.Length < 8) { ErrorMessage = LocalizationManager.Get("Common.ErrorPasswordTooShort"); return false; }
        if (!passwordChars.SequenceEqual(confirmChars)) { ErrorMessage = LocalizationManager.Get("Common.ErrorPasswordMismatch"); return false; }
        try
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            await _auth.SetupAsync(passwordChars.AsSpan(), dataDir, _authCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            // The window was closed during setup: not an error. The service has already wiped the
            // derived keys and released K_shared.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError("[UnlockViewModel] SetupAsync failed. [{ExType}]", ex.GetType().Name);
            ErrorMessage = LocalizationManager.Get("Common.GeneralError");
            return false;
        }
    }

    private async Task<bool> UnlockAsync(char[] passwordChars)
    {
        try
        {
            bool success;
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            var ct = _authCts.Token;
            var matchingVaults = await _auth.AcquireKSharedAsync(passwordChars.AsSpan(), ct);
            if (matchingVaults.Count == 0)
            {
                success = false;
            }
            else if (matchingVaults.Count == 1 || SelectVaultAsync == null)
            {
                success = await _auth.EnterVaultAsync(matchingVaults[0], passwordChars.AsSpan(), dataDir, ct);
            }
            else
            {
                var chosen = await SelectVaultAsync(matchingVaults);
                if (chosen == null) return false;
                success = await _auth.EnterVaultAsync(chosen.Value, passwordChars.AsSpan(), dataDir, ct);
            }
            if (!success)
            {
                ErrorMessage = _session.VaultDbUnrecoverable
                    ? string.Format(LocalizationManager.Get("Unlock.ErrorVaultDbCorrupted"), _session.VaultDbUnrecoverableNumber)
                    : LocalizationManager.Get("Common.ErrorPasswordMismatch");
            }
            return success;
        }
        catch (OperationCanceledException)
        {
            // The window was closed (or the session changed) while deriving. Not an error and not a
            // wrong password: no message, and the service has already wiped the derived key.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError("[UnlockViewModel] UnlockAsync failed. [{ExType}]", ex.GetType().Name);
            ErrorMessage = LocalizationManager.Get("Common.GeneralError");
            return false;
        }
    }

    private void ScrubSensitiveProperties()
    {
        PasswordLength = 0;
        ConfirmLength  = 0;
        GetPasswordChars = null;
        GetConfirmChars  = null;
        SelectVaultAsync = null;
        ConfirmInvalidateHelloAsync = null;
    }

}
