// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

/// <summary>
/// Per-domain favicon cache.
/// Domain uses the raw 32-byte HMAC-SHA256(DEK, domainUtf8) as the PK (no plaintext domain name is stored).
/// </summary>
public class FaviconCache
{
    /// <summary>Raw 32-byte HMAC-SHA256(DEK, domain). PK. Column name stays "Domain" (see VaultDbContext).</summary>
    public byte[] DomainHmac { get; set; } = [];

    /// <summary>AES-256-GCM encrypted favicon PNG binary (nonce[12]+tag[16]+cipher).</summary>
    public byte[] PngData { get; set; } = [];

    /// <summary>Fetch time (UTC). Stored by SQLite as INTEGER (Unix seconds) via Value Converter.</summary>
    public DateTime FetchedAt { get; set; }
}
