// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

/// <summary>
/// Unsaved draft staging buffer. One row per Secret at most (SecretId is the PK/FK), auto-saved on
/// every focus-out. A row's existence means a draft exists; its absence means no draft.
/// Physically separate from SecretHistory (finalized history in SlotA/B/C) because drafts are
/// written on every focus-out (high-frequency) while finalized history is written only on Save
/// (low-frequency) — the two have fundamentally different lifecycles.
/// </summary>
public class SecretDraft
{
    public int SecretId { get; set; }   // PK (one row per Secret, present only while a draft exists)

    /// <summary>Encrypted SecretSnapshot (draft) — raw AES-256-GCM BLOB.</summary>
    public byte[] SnapshotBlob { get; set; } = [];

    /// <summary>Draft auto-save time (UTC). Stored by SQLite as INTEGER (Unix seconds) via Value Converter.</summary>
    public DateTime SavedAt { get; set; }
}
