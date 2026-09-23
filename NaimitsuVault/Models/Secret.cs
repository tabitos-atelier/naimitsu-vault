// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

/// <summary>
/// Secret entity (Secret).
/// Every string field is stored as a raw AES-256-GCM BLOB (nonce[12]+tag[16]+ciphertext).
/// </summary>
public class Secret
{
    public int Id { get; set; }

    /// <summary>
    /// A category preset code defined by the active locale (see LocalizationManager.GetCategoryPresets),
    /// or 0 for uncategorized. Categories have no DB table - this is a plain int with no FK constraint.
    /// </summary>
    public int? CategoryNum { get; set; }

    // --- Encrypted BLOB fields ---

    /// <summary>Required, encrypted. Searched via an in-memory index.</summary>
    public byte[] Title { get; set; } = [];

    public byte[]? UserId { get; set; }
    public byte[]? Password { get; set; }

    /// <summary>TOTP secret (encrypted BLOB). null = 2FA not configured.</summary>
    public byte[]? TotpSecret { get; set; }

    public byte[]? Email { get; set; }
    public byte[]? Website { get; set; }
    public byte[]? Notes { get; set; }
    public byte[]? CustomFields { get; set; }

    /// <summary>Display-label override dictionary JSON for standard fields (AES-256-GCM encrypted BLOB).</summary>
    public byte[]? LabelOverrides { get; set; }

    // --- Password generator settings (per-item, plaintext, out of zero-knowledge scope) ---

    /// <summary>Symbol set specific to this item. null/empty → uses PasswordGenerator.DefaultSymbols.</summary>
    /// <remarks>
    /// Intentionally not made a byte[] encrypted field. This is non-sensitive data that cannot be used to
    /// infer a site name or behavior, and encrypting it would add DEK-decryption overhead to the password
    /// generator's synchronous pipeline.
    /// </remarks>
    public string? GeneratorSymbols { get; set; }

    // --- Flags/timestamps (stored as INTEGER via Value Converter) ---

    /// <summary>Favorite flag. Saved immediately without changing UpdatedAt.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>Password expiration (UTC). null = not set.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete timestamp (UTC). null = still active. Physically deleted after 30 days.</summary>
    public DateTime? DeletedAt { get; set; }
}
