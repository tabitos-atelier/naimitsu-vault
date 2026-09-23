// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

/// <summary>
/// Table storing the unified DB's key-value entries: K_shared management slots and plaintext app-wide settings.
///
/// ConfigKey is a hex code defined in <see cref="NaimitsuVault.Services.UnifiedMetadataKey"/>.
/// Scheme:
///   0x0001-0x000A … K_shared management slots (key wrap, Hello)
///   0x0101-0x0103 … Plaintext app-wide settings
/// </summary>
public class UnifiedMetadata
{
    /// <summary>Integer identifier code (PK) defined in <see cref="NaimitsuVault.Services.UnifiedMetadataKey"/>.</summary>
    public int ConfigKey { get; set; }

    /// <summary>Entry value. Either a UTF-8 byte sequence or an AES-256-GCM BLOB.</summary>
    public byte[] ConfigValue { get; set; } = [];
}
