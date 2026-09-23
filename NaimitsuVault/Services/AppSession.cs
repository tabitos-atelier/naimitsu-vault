// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Authenticated session. Holds the two-tier keys (K_shared + vault_DEK) in GC-pinned fixed buffers.
/// <para>
/// K_shared  : used exclusively to encrypt/decrypt the unified DB's VaultRegistries.EncryptedPayload. Set on successful unlock.
/// vault_DEK : used exclusively to encrypt/decrypt all vault DB tables. Set on vault entry.
/// </para>
/// </summary>
public sealed class AppSession : ISecurityContext
{
    private const int KeySize = 32;

    // K_shared: unified DB protection key (fixed-address buffer)
    private readonly byte[] _pinnedKShared = GC.AllocateArray<byte>(KeySize, pinned: true);
    private bool _kSharedSet;

    // vault_DEK: vault DB protection key (fixed-address buffer)
    private readonly byte[] _pinnedKey = GC.AllocateArray<byte>(KeySize, pinned: true);
    private bool _keySet;

    // Avatar image PII (reallocated each time due to variable length, but always pinned)
    private byte[]? _pinnedAvatar;

    public bool IsUnlocked  => _keySet;
    public bool HasKShared  => _kSharedSet;

    /// <summary>Currently active vault number (1-3, null = none selected).</summary>
    public int? CurrentVaultDbNumber { get; set; }

    /// <summary>Vault number shown in the UI. Same value as CurrentVaultDbNumber.</summary>
    public int? DisplayedVaultNumber { get; set; }

    /// <summary>
    /// Vault number most recently entered via EnterVaultCoreAsync, preserved across locks (unlike
    /// CurrentVaultDbNumber, which Lock() clears). Used solely to detect an actual switch to a
    /// different vault vs. a same-vault re-unlock after auto-lock, so per-vault selection state
    /// (see ResetPerVaultSelectionState) is reset only on a real switch and RestoreOnUnlock keeps
    /// working across an ordinary lock/unlock cycle.
    /// </summary>
    public int? LastEnteredVaultDbNumber { get; set; }

    /// <summary>Last selected secret ID, preserved across locks (secret list page).</summary>
    public int? LastSelectedSecretId { get; set; }

    /// <summary>Last selected secret ID, preserved across locks (time machine page).</summary>
    public int? LastSelectedTimeMachineId { get; set; }

    /// <summary>Last selected file ID, preserved across locks (gallery page).</summary>
    public int? LastSelectedFileId { get; set; }

    /// <summary>Tag of the last active page, preserved across locks.</summary>
    public string? LastActivePageTag { get; set; }

    /// <summary>Item ID that the next page load should select when jumping between pages.</summary>
    public int? PendingJumpSecretId { get; set; }

    /// <summary>
    /// Set of file IDs currently being edited (drag-and-drop added, unsaved) on the profile screen.
    /// </summary>
    public HashSet<int> PendingProfileImageIds { get; } = [];

    // ── DB integrity warnings (PRAGMA quick_check) ──────────────────────────────────
    /// <summary>True if the unified DB's PRAGMA quick_check returned anything other than "ok". Preserved across locks.</summary>
    public bool UnifiedDbIntegrityWarning { get; internal set; } = false;

    /// <summary>True if the vault DB's PRAGMA quick_check returned anything other than "ok". Preserved across locks.</summary>
    public bool VaultDbIntegrityWarning { get; internal set; } = false;

    /// <summary>
    /// Indicates that unified DB initialization failed at startup and the app is frozen in a corrupted state.
    /// Used by UnlockWindow to decide whether to show the freeze display. Reset after successful recovery.
    /// </summary>
    public bool IsUnifiedDbCorrupted { get; set; } = false;

    /// <summary>
    /// Indicates that Route A (restricted read-only mode) is active.
    /// When true: physical SQLite writes, automatic backups, and export re-authentication are all suppressed.
    /// Reset on lock.
    /// </summary>
    public bool IsReadOnlyRestricted { get; set; } = false;

    // ── Auto-recovery flags (for dashboard InfoBar + audit log entries) ──────────────
    /// <summary>True if the unified DB was auto-repaired from a shadow file at startup. Cleared at the start of UnlockAsync.</summary>
    public bool UnifiedDbAutoRecovered { get; set; } = false;

    /// <summary>True if the vault DB was auto-repaired from a shadow file during UnlockAsync. Cleared at the start of UnlockAsync.</summary>
    public bool VaultDbAutoRecovered { get; set; } = false;

    /// <summary>The vault number that was auto-repaired. Only valid when VaultDbAutoRecovered == true.</summary>
    public int? VaultDbAutoRecoveredNumber { get; set; }

    /// <summary>
    /// True if the most recent vault entry attempt failed because the vault DB is missing/corrupted and no
    /// usable shadow was found (no shadow, ambiguous shadows, or shadow restore itself failed) - as opposed to
    /// a wrong password. Used by UnlockViewModel to show a dedicated message instead of "wrong password".
    /// Cleared at the start of EnterVaultCoreAsync.
    /// </summary>
    public bool VaultDbUnrecoverable { get; set; } = false;

    /// <summary>The vault number that could not be recovered. Only valid when VaultDbUnrecoverable == true.</summary>
    public int? VaultDbUnrecoverableNumber { get; set; }

    // ── Audit log locality — pre-auth failure counter ──────────────
    /// <summary>
    /// Number of authentication failures before unlock (ActiveVaultDbPath == null). Not persisted to DB.
    /// Flushed to the vault DB via FlushPreAuthFailuresAsync on successful entry, then reset to zero.
    /// </summary>
    public int FailedLoginAttempts { get; set; } = 0;

    /// <summary>
    /// Avatar image bytes (PII) cached in memory while unlocked.
    /// The setter zeroes the old buffer, copies the source into the pinned buffer, then zeroes the source.
    /// </summary>
    public byte[]? AvatarBytes
    {
        get => _pinnedAvatar;
        set
        {
            if (_pinnedAvatar != null)
            {
                CryptographicOperations.ZeroMemory(_pinnedAvatar);
                _pinnedAvatar = null;
            }
            if (value == null) return;

            _pinnedAvatar = GC.AllocateArray<byte>(value.Length, pinned: true);
            value.AsSpan().CopyTo(_pinnedAvatar.AsSpan());
            CryptographicOperations.ZeroMemory(value);
        }
    }

    // ── K_shared access ──────────────────────────────────────────────────

    public KSharedScope GetKShared()
    {
        if (!_kSharedSet)
            throw new InvalidOperationException("K_shared is not set. Call this only after unlocking.");
        return new KSharedScope(_pinnedKShared, KeySize);
    }

    public void SetKShared(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"K_shared must be {KeySize} bytes.", nameof(key));
        ClearKShared();
        key.CopyTo(_pinnedKShared.AsSpan());
        _kSharedSet = true;
    }

    private void ClearKShared()
    {
        if (_kSharedSet)
        {
            CryptographicOperations.ZeroMemory(_pinnedKShared);
            _kSharedSet = false;
        }
    }

    // ── vault_DEK access ─────────────────────────────────────────────────

    public DekScope GetKey()
    {
        if (!_keySet)
            throw new InvalidOperationException("Session is locked. Unlock with the master password.");
        return new DekScope(_pinnedKey, KeySize);
    }

    public void SetKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"DEK must be {KeySize} bytes.", nameof(key));
        // Zero only the DEK buffer. Do not call Lock() (must not destroy K_shared / CurrentVaultDbNumber)
        ClearKey();
        key.CopyTo(_pinnedKey.AsSpan());
        _keySet = true;
    }

    private void ClearKey()
    {
        if (_keySet)
        {
            CryptographicOperations.ZeroMemory(_pinnedKey);
            _keySet = false;
        }
    }

    /// <summary>
    /// Clears per-vault selection/pending state that must not survive a switch to a different vault
    /// (a stale ID here could resolve to an unrelated record in the newly entered vault). Called from
    /// AuthService.EnterVaultCoreAsync only when the incoming dbNumber differs from LastEnteredVaultDbNumber,
    /// so an ordinary same-vault re-unlock after auto-lock is left untouched and RestoreOnUnlock still works.
    /// </summary>
    public void ResetPerVaultSelectionState()
    {
        LastSelectedSecretId      = null;
        LastSelectedTimeMachineId = null;
        LastSelectedFileId        = null;
        PendingJumpSecretId       = null;
        PendingProfileImageIds.Clear();
    }

    // ── Lock operations ────────────────────────────────────────────────────────

    /// <summary>
    /// Synchronous lock (lightweight version, no UI thread needed). ZeroMemory only. Does not run a GC sweep.
    /// Called from LockCycleOrchestrator in App.cs. The full-sweep GC is run separately as a background
    /// task on the LockAsync() side of App.xaml.cs.
    /// </summary>
    public void Lock()
    {
        ClearKey();
        ClearKShared();
        CurrentVaultDbNumber  = null;
        DisplayedVaultNumber  = null;
        AvatarBytes = null;
        FailedLoginAttempts = 0;
        IsUnifiedDbCorrupted = false;
        IsReadOnlyRestricted = false;
        UnifiedDbAutoRecovered      = false;
        VaultDbAutoRecovered        = false;
        VaultDbAutoRecoveredNumber  = null;
        VaultDbUnrecoverable        = false;
        VaultDbUnrecoverableNumber  = null;
    }
}
