// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

/// <summary>
/// VaultRegistries table in the unified DB (NaimitsuVault.nkdb).
/// Holds registration info for vault DBs (numbers 1-3).
/// </summary>
public class VaultRegistry
{
    /// <summary>Vault number. PK. Integer 1-3.</summary>
    public int DbNumber { get; set; }

    /// <summary>
    /// Last access time (Unix seconds). UPDATEd on every access.
    /// Intentionally raw long? rather than DateTime? + a Value Converter (unlike Secret.ExpiresAt,
    /// StoredFile.DeletedAt, etc.): callers only ever write DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    /// and never need a DateTime view of this column, so the converter indirection buys nothing here.
    /// </summary>
    public long? LastAccessedAt { get; set; }

    /// <summary>
    /// Payload BLOB encrypted with AES-256-GCM under K_shared.
    /// ICryptoService format: nonce[12] + tag[16] + ciphertext (JSON: FileHash, Prefix3Hash, VaultSaltPwd, ...).
    /// </summary>
    public byte[] EncryptedPayload { get; set; } = [];

    /// <summary>
    /// The shadow file's 36-byte binary (decoded from the 48-character Base64Url file name).
    /// NULL if no shadow exists.
    /// </summary>
    public byte[]? ShadowFileKey { get; set; }
}
