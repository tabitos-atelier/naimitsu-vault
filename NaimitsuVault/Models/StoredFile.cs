// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.ComponentModel.DataAnnotations.Schema;

namespace NaimitsuVault.Models;

/// <summary>
/// File resource pool entity (StoredFile).
/// Managed independently of Secrets and referenced N:M with Secret via SecretFileLinks (junction table).
/// </summary>
public class StoredFile
{
    public int Id { get; set; }

    /// <summary>File name (AES-256-GCM encrypted BLOB).</summary>
    public byte[] FileName { get; set; } = [];

    /// <summary>File type code (<see cref="NaimitsuVault.Common.FileTypeCode"/> constant). Mapped directly to a SQLite INTEGER.
    /// Column name stays "ContentType" (see VaultDbContext) - not a MIME type string, despite the name.</summary>
    public int ContentTypeCode { get; set; }

    /// <summary>Original file size (for duplicate detection)</summary>
    public long FileSize { get; set; }

    /// <summary>Raw 32-byte HMAC-SHA256(DEK, SHA256(rawBytes)) (defends against known-file identification attacks).</summary>
    public byte[] FileHash { get; set; } = [];

    /// <summary>Encrypted original file data body (nonce[12] + tag[16] + ciphertext)</summary>
    public byte[]? OriginalBlob { get; set; }

    /// <summary>Encrypted thumbnail data (speeds up list display)</summary>
    public byte[]? ThumbnailBlob { get; set; }

    /// <summary>Original file's last-modified time (UTC). Stored by SQLite as INTEGER (Unix seconds) via Value Converter.</summary>
    public DateTime FileModifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete timestamp (UTC). NULL means alive. Mirrors Secret.DeletedAt.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Set in memory by StoredFileRepository.Decrypt() when the decrypted FileName's extension
    /// does not match ContentTypeCode (suspected tampering or corruption). Never persisted: the row's
    /// ContentTypeCode is left untouched in the DB so the anomaly stays visible on every future read.
    /// </summary>
    [NotMapped]
    public bool IsQuarantined { get; set; }
}
