// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

/// <summary>
/// Integer ConfigKey constants for the VaultMetadata table of the vault DB ([48-character Base64Url]).
///
/// Hex code scheme:
///   0x1001-0x1005 ... Self-protected key material (no vault_DEK needed; protected directly by KEK / DPAPI / RK)
///   0x1101-0x11FF ... AES-256-GCM encrypted blobs (vault_DEK required; strictly subject to zero-knowledge)
/// </summary>
internal static class VaultMetadataKey
{
    // ── 0x10xx: Self-protected key material ───────────────────────────────────────────
    /// <summary>
    /// { Salt, WrappedDek } JSON.
    /// WrappedDek = AES-256-GCM(vault_DEK, vault_KEK) - Base64 of a 60-byte binary in the form nonce(12B)+cipher(32B)+tag(16B).
    /// </summary>
    internal const int Auth = 0x1001;

    /// <summary>Base64 of vault_DEK protected with DPAPI(CurrentUser). Used for Windows Hello integration.</summary>
    internal const int VaultDEKHello = 0x1002;

    /// <summary>JSON of vault_DEK wrapped with the EAC key. Emergency access code.</summary>
    internal const int EmergencyAccessCode = 0x1003;

    /// <summary>
    /// AES-256-GCM(vault_DEK, K_shared) 60-byte blob.
    /// The K_shared escrow slot within the vault DB.
    /// Written retroactively on normal login. Updated after recovery and after a password change.
    /// </summary>
    internal const int KSharedEac = 0x1004;

    /// <summary>
    /// AES-256-GCM(vault_DEK, UTF8(JSON{"OriginalDbNumber":N})) blob.
    /// N=1-3: the original DbNumber of the real vault (fully restores the original number even after a backup-less rebuild).
    /// Written by: CreateNewVaultCoreAsync.
    /// Read by: AuthService.cs's emergency-access fallback (TrySetVaultNumberFromIndexAsync),
    /// right after vault_DEK is obtained. Not read by the restore flow (Services/AuthService.Restore.cs).
    /// </summary>
    internal const int VaultIndex = 0x1005;

    // ── 0x1101-0x11FF: AES-256-GCM encrypted blobs (vault_DEK required; subject to zero-knowledge) ─
    /// <summary>AES-256-GCM(ProfileSnapshot JSON, vault_DEK). The current committed profile.</summary>
    internal const int UserProfile_TwinA = 0x1101;

    /// <summary>AES-256-GCM(ProfileSnapshot JSON, vault_DEK). The TwinB draft.</summary>
    internal const int UserProfile_TwinB = 0x1102;

    /// <summary>AES-256-GCM(JPEG bytes, vault_DEK). The current committed avatar image.</summary>
    internal const int AvatarImage_TwinA = 0x1103;

    /// <summary>
    /// AES-256-GCM(JPEG bytes, vault_DEK). The TwinB draft avatar.
    /// An empty ConfigValue marks "the draft explicitly clears the avatar" (distinct from row-absent,
    /// which means the draft never touched the avatar).
    /// </summary>
    internal const int AvatarImage_TwinB = 0x1104;

    // 2026-09-01: removed BackupConfig (0x1103, later renumbered to 0x1105 during a reordering pass) -
    // dead constant with zero production read/write sites. The actual backup destination folder is
    // stored as plaintext in GeneralSettings.AutoBackupFolder (UnifiedMetadataKey.General), not here.
}
