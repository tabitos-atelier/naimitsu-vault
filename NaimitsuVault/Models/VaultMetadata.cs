// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

/// <summary>
/// Table storing the vault DB's key-value entries: self-protected key material and encrypted user data
/// (profile, avatar) that do not warrant a dedicated table.
///
/// ConfigKey is a hex code defined in <see cref="NaimitsuVault.Services.VaultMetadataKey"/>.
/// Scheme:
///   0x1001-0x1005 … Self-protected key material (vault_DEK not required)
///   0x1101-0x11FF … AES-256-GCM encrypted BLOB (vault_DEK required)
/// </summary>
public class VaultMetadata
{
    /// <summary>Integer identifier code (PK) defined in <see cref="NaimitsuVault.Services.VaultMetadataKey"/>.</summary>
    public int ConfigKey { get; set; }

    /// <summary>Entry value. Either a UTF-8 byte sequence or an AES-256-GCM BLOB.</summary>
    public byte[] ConfigValue { get; set; } = [];
}
