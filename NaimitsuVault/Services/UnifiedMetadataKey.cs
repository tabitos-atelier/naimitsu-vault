// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

/// <summary>
/// Integer ConfigKey constants for the UnifiedMetadata table of the unified DB (NaimitsuVault.nkdb).
///
/// Hex code scheme:
///   0x0001-0x0003 ... K_shared key-wrap slots (96-byte blob) - up to 3 vaults
///   0x000A        ... KSharedHello (DPAPI-protected)
///   0x0101-0x0103 ... Plaintext app-wide settings (UTF-8 / JSON)
/// </summary>
internal static class UnifiedMetadataKey
{
    // ── 0x00xx: K_shared protection group ─────────────────────────────────────────────
    /// <summary>K_shared key-wrap slot for vault 1 (96-byte blob).</summary>
    internal const int KSharedSlot_1 = 0x0001;
    /// <summary>K_shared key-wrap slot for vault 2 (96-byte blob).</summary>
    internal const int KSharedSlot_2 = 0x0002;
    /// <summary>K_shared key-wrap slot for vault 3 (96-byte blob).</summary>
    internal const int KSharedSlot_3 = 0x0003;

    /// <summary>DPAPI-protected blob of K_shared obtained via Windows Hello.</summary>
    internal const int KSharedHello = 0x000A;

    // 2026-09-01: removed KSharedSecretGeneral (0x000B) - dead constant with zero production
    // read/write sites. The actual backup destination folder is stored as plaintext in
    // GeneralSettings.AutoBackupFolder (see General below), not as a K_shared-encrypted blob.

    /// <summary>Computes the K_shared key-wrap slot code for vault N (N = 1-3).</summary>
    internal static int KSharedSlotForVault(int dbNumber)
    {
        if (dbNumber < 1 || dbNumber > 3)
            throw new ArgumentOutOfRangeException(nameof(dbNumber), "Vault number must be within the range 1-3.");
        return dbNumber;
    }

    // ── 0x01xx: Plaintext app-wide settings ──────────────────────────────────────────
    /// <summary>Plaintext JSON (GeneralSettings). Common settings such as theme, AutoLock, capture protection, etc.</summary>
    internal const int General = 0x0101;
    /// <summary>Plaintext text "ja" | "en" | "custom". UI locale.</summary>
    internal const int Locale = 0x0102;
    /// <summary>Plaintext JSON (LocaleData). User-defined custom locale.</summary>
    internal const int CustomLocale = 0x0103;
}
