// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Services.Interfaces;

public interface IAuthService
{
    // ── Setup/Unlock ──────────────────────────────────────
    Task<bool> IsSetupRequiredAsync();
    Task SetupAsync(ReadOnlySpan<char> masterPassword, string dataDir, CancellationToken ct = default);
    Task<bool> UnlockAsync(ReadOnlySpan<char> masterPassword);

    // ── Master password change ────────────────────────────────────────
    Task<bool> ChangeMasterPasswordAsync(ReadOnlySpan<char> oldPassword,
                                         ReadOnlySpan<char> newPassword);
    Task<bool> ResetMasterPasswordWithHelloAsync(ReadOnlySpan<char> newPassword);

    // ── Windows Hello ─────────────────────────────────────────────────
    Task<bool> IsWindowsHelloSupportedAsync();
    Task<bool> IsWindowsHelloEnabledAsync();
    Task<bool> IsWindowsHelloConfiguredForUnlockAsync();
    Task EnableWindowsHelloAsync();
    Task DisableWindowsHelloAsync();
    Task InvalidateWindowsHelloEverywhereAsync(string dataDir);

    /// <summary>
    /// Authenticates via Windows Hello and derives K_shared from it.
    /// Returns the vault numbers (1-3) that are registered and decryptable with the resulting K_shared,
    /// not the key itself.
    /// </summary>
    Task<List<int>> AcquireKSharedWithHelloAsync();

    // ── Multi-vault ──────────────────────────────────────────────────

    /// <summary>
    /// Derives K_shared from <paramref name="password"/>.
    /// Returns the vault numbers (1-3) that are registered and decryptable with the resulting K_shared,
    /// not the key itself.
    /// The Argon2id derivation runs off the calling thread. If <paramref name="ct"/> is
    /// cancelled, or the session was locked/unlocked in the meantime, the derived key is wiped instead of
    /// being written to the session and an <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    Task<List<int>> AcquireKSharedAsync(ReadOnlySpan<char> password, CancellationToken ct = default);
    /// <summary>
    /// Enters the vault. Same cancellation contract as <see cref="AcquireKSharedAsync"/>: the derived DEK is
    /// never written to the session once the token is cancelled or the session state has changed.
    /// </summary>
    Task<bool> EnterVaultAsync(int vaultNumber, ReadOnlySpan<char> password, string dataDir, CancellationToken ct = default);
    Task<bool> EnterVaultWithHelloAsync(int vaultNumber, string dataDir);
    Task<bool> CreateNewVaultAsync(int vaultNumber, ReadOnlySpan<char> password, string dataDir);
    Task<List<int>> GetAvailableVaultNumbersAsync();
    Task<List<int>> GetRegisteredVaultNumbersAsync();

    // ── Emergency access code ────────────────────────────────────────────
    Task<bool> HasEmergencyAccessCodeAsync();
    Task<(string QrPayload, string SuggestedFileName, byte[] WrappedDek)> GenerateEmergencyAccessCodeAsync(
        ReadOnlySpan<char> pin);
    Task CommitEmergencyAccessCodeAsync(byte[] wrappedDek);
    Task RevokeEmergencyAccessCodeAsync();

    // ── Emergency access unlock ────────────────────────────────────────
    Task<EmergencyAccessResult> EmergencyAccessUnlockAsync(
        ReadOnlySpan<char> qrPayload,
        ReadOnlySpan<char> pin,
        string dataDir,
        CancellationToken ct = default);
}
