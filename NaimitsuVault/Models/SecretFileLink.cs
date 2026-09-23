// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

/// <summary>
/// Link table entity associating a Secret with a stored file resource (StoredFile).
/// </summary>
public class SecretFileLink
{
    public int SecretId { get; set; }
    public int FileId { get; set; }

    // No surrogate key; uses the composite primary key (SecretId, FileId)
}
