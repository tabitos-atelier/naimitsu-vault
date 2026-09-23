// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

/// <summary>
/// TimeMachine finalized history. One row per Secret (SecretId is the PK).
/// Holds finalized history in 3 fixed slots (A/B/C) only.
/// SlotOrder is a circular buffer tracking which slot is Gen0 (current), Gen1, or Gen2 (3 rotating slots).
/// Drafts live in the separate SecretDraft entity (see Models/SecretDraft.cs), split out from this
/// table because finalized history (low-frequency, written only on Save) and drafts (high-frequency,
/// written on every focus-out) have fundamentally different lifecycles.
/// </summary>
public class SecretHistory
{
    public int SecretId { get; set; }   // PK (one row per Secret)

    // Encrypted SecretSnapshot (finalized history) — raw AES-256-GCM BLOB
    public byte[]? SlotA { get; set; }
    public byte[]? SlotB { get; set; }
    public byte[]? SlotC { get; set; }

    // Snapshot capture time (UTC) — stored by SQLite as INTEGER (Unix seconds, nullable) via Value Converter
    public DateTime? SlotASavedAt { get; set; }
    public DateTime? SlotBSavedAt { get; set; }
    public DateTime? SlotCSavedAt { get; set; }

    /// <summary>
    /// Slot order. Stored directly in a SQLite INTEGER column as a packed int (no ValueConverter needed).
    /// bits[11:8]=Gen0 slot ID, bits[7:4]=Gen1, bits[3:0]=Gen2 (0=none, 1=A, 2=B, 3=C).
    /// Example: "C,B,A" → 0x321. 0 = no history.
    /// </summary>
    public int SlotOrder { get; set; }
}
