// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Services.Interfaces;

public interface ISecurityContext
{
    // ── State query ──────────────────────────────────────────────────
    bool HasKShared { get; }
    /// <summary>True while the active vault's DEK is set. Cleared by <see cref="Lock"/>.</summary>
    bool IsUnlocked { get; }

    // ── Active vault ─────────────────────────────────────────────────
    int? CurrentVaultDbNumber { get; set; }
    int? DisplayedVaultNumber { get; set; }
    int? LastEnteredVaultDbNumber { get; set; }
    void ResetPerVaultSelectionState();

    // ── Session flags ────────────────────────────────────────────────
    bool IsReadOnlyRestricted { get; set; }
    int  FailedLoginAttempts { get; set; }
    bool UnifiedDbAutoRecovered { get; set; }
    bool VaultDbAutoRecovered { get; set; }
    int? VaultDbAutoRecoveredNumber { get; set; }
    bool VaultDbUnrecoverable { get; set; }
    int? VaultDbUnrecoverableNumber { get; set; }

    // ── PII ──────────────────────────────────────────────────────────
    byte[]? AvatarBytes { get; set; }

    // ── K_shared access ──────────────────────────────────────────────
    KSharedScope GetKShared();
    void SetKShared(ReadOnlySpan<byte> key);

    // ── vault_DEK access ─────────────────────────────────────────────
    /// <summary>Returns the active vault's DEK (not K_shared - see <see cref="GetKShared"/>).</summary>
    DekScope GetKey();
    /// <summary>Sets the active vault's DEK (not K_shared - see <see cref="SetKShared"/>).</summary>
    void SetKey(ReadOnlySpan<byte> key);

    // ── Lock ─────────────────────────────────────────────────────────
    void Lock();
}
