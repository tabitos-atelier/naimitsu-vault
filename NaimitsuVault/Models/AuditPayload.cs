// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text.Json.Serialization;

namespace NaimitsuVault.Models;

public abstract record AuditPayload;

// ── View (0x1000) ────────────────────────────────────────────────────────
// ProfileViewed (0x1500) → no Payload

// Name is a title/filename snapshot taken at the moment of the event, sealed inside the same
// AES-256-GCM encrypted Payload blob as everything else. Without it, the summary text would have to
// re-resolve the current title from the Secrets/StoredFiles table every time the log is displayed -
// once the item is permanently deleted, older log rows (view/save/soft-delete/etc.) would
// retroactively lose their name and become unreadable ("(deleted) was viewed" etc.), defeating the
// purpose of an audit trail. Nullable for backward compatibility with rows logged before this field
// existed (BuildDisplayItemsAsync falls back to the live-lookup path when Name is null).
public record SecretViewedPayload(int TargetId, string? Name = null)
    : AuditPayload;

public record TimeMachineViewedPayload(int TargetId, string? Name = null)
    : AuditPayload;

public record FileViewedPayload(int TargetId, string? Name = null)
    : AuditPayload;

// ── Config/setting change (0x3000) ────────────────────────────────────────
// MasterPasswordChanged (0x3000) → no Payload

public record AutoBackupSettingChangedPayload(bool Enabled, string? Folder = null)
    : AuditPayload;

public record AutoLockSettingChangedPayload(bool Enabled, int? TimeoutMinutes = null)
    : AuditPayload;

public record ScreenCaptureProtectionChangedPayload(bool Enabled)
    : AuditPayload;

public record WindowsHelloChangedPayload(bool Enabled)
    : AuditPayload;

public record FaviconAutoFetchChangedPayload(bool Enabled)
    : AuditPayload;

// ── Auth (0x4000) ────────────────────────────────────────────────────────
// AuthFailed (0x4000) → Payload = NULL (DEK not yet unwrapped)
// AuthSucceeded (0x4001) → no Payload

// ── Save (0x5000) ────────────────────────────────────────────────────────
// ProfileSaved (0x5500) → no Payload

public record SecretSavedPayload(int TargetId, bool IsNew, string? Name = null)
    : AuditPayload;

public record TimeMachineRestoredPayload(int TargetId, [property: JsonPropertyName("SlotLabel")] string GenerationLabel)
    : AuditPayload;

public record FileAddedPayload(int TargetId, string? Name = null)
    : AuditPayload;

// ── Delete/purge (0x6000) ──────────────────────────────────────────────────

public record SecretSoftDeletedPayload(int TargetId, string? Name = null)
    : AuditPayload;

public record SecretUndeletedPayload(int TargetId, string? Name = null)
    : AuditPayload;

public record SecretPermanentlyDeletedPayload(int TargetId, string? Name = null)
    : AuditPayload;

public record SecretAutoPurgedByExpiryPayload(int PurgedCount)
    : AuditPayload;

public record TimeMachineSlotDeletedPayload(int TargetId, [property: JsonPropertyName("SlotLabel")] string SlotName)
    : AuditPayload;

public record TimeMachineGenRotatedPayload(int TargetId)
    : AuditPayload;

public record FileSoftDeletedPayload(int TargetId, string? Name = null)
    : AuditPayload;

public record FileUndeletedPayload(int TargetId, string? Name = null)
    : AuditPayload;

public record FilePermanentlyDeletedPayload(int TargetId, string? Name = null)
    : AuditPayload;

// Distinct from SecretAutoPurgedByExpiryPayload (Secret-only) - pairs with AuditEventCode.FileAutoPurgedByExpiry (0x6303).
public record FileAutoPurgedByExpiryPayload(int PurgedCount)
    : AuditPayload;

// ── Critical operation (0x7000) ───────────────────────────────────────────
// EmergencyAccessCodeExecuted  (0x7000) → no Payload
// EmergencyAccessCodeGenerated (0x7001) → no Payload
// EmergencyAccessCodeRevoked   (0x7002) → no Payload
// BackupExecuted (0x7F00)               → no Payload
// RestoreExecuted (0x7F01)              → no Payload

public record PlaintextImportExecutedPayload(string Format, int ItemCount)
    : AuditPayload;

public record PlaintextExportExecutedPayload(string Format, int ItemCount)
    : AuditPayload;

// Ordinal is the 1-based position of the failed record in the source file - the record's own
// title/name can't be trusted as an identifier since the same failure may be why it's unreadable.
public record ImportItemFailedPayload(string Format, int Ordinal)
    : AuditPayload;

public record ExportItemFailedPayload(int TargetId, string? Name = null)
    : AuditPayload;

// Name is populated directly from the already-decrypted title at export time (not left to the
// usual live-lookup fallback), since the record decrypted fine here - unlike ExportItemFailedPayload.
public record CsvFormulaGuardAppliedPayload(int TargetId, string? Name = null)
    : AuditPayload;

public record FileExportedPayload(int TargetId, string? Name = null)
    : AuditPayload;

// FieldLabel is the field's display name (e.g. "Password", "UserId", a custom field's Label, or
// "TotpCode") - never the copied/typed value itself. Name is the owning Secret's title snapshot,
// same TargetId/Name pattern as FileExportedPayload/CsvFormulaGuardAppliedPayload above.
public record SecretFieldCopiedToClipboardPayload(int TargetId, string FieldLabel, string? Name = null)
    : AuditPayload;

public record SecretAutoTypeExecutedPayload(int TargetId, string FieldLabel, string? Name = null)
    : AuditPayload;

// GenerationLabel distinguishes which generation the value came from ("Current"/"Gen1"/"Gen2") - not to
// be confused with TimeMachineSlotDeletedPayload.SlotName, which is a physical slot ("A"/"B"/"C").
public record TimeMachineValueCopiedToClipboardPayload(int TargetId, string FieldLabel, [property: JsonPropertyName("SlotLabel")] string GenerationLabel, string? Name = null)
    : AuditPayload;

// Profile is a per-vault singleton (no TargetId, matching ProfileSaved/ProfileViewed).
public record ProfileFieldCopiedToClipboardPayload(string FieldLabel)
    : AuditPayload;

// ── Settings (0x7800): Locale import + vault creation ─────────────────────────
// LocaleImportFailed (0x7801) → Payload = NULL (failure reason already shown via notification)

// DisplayName is the imported file's own declared display name (e.g. "Cyber Desert") - without it,
// the log entry can't tell which custom locale pack was imported when the user has tried several.
public record LocaleImportSucceededPayload(int PatchedCount, string? DisplayName = null)
    : AuditPayload;

public record LocaleImportKeyMissingPayload(string Key)
    : AuditPayload;

public record LocaleImportValueTooLongPayload(string Key)
    : AuditPayload;

public record LocaleImportPlaceholderBrokenPayload(string Key)
    : AuditPayload;

// VaultCreated (0x7805) → Payload = NULL (recorded as the new vault's own first log row; see AuditEventCode)

// ── System (0xF000) ────────────────────────────────────────────────────────

public record NoOpDraftDiscardedPayload(int DiscardedCount)
    : AuditPayload;

// One row per repaired StoredFile (see AuditEventCode.FileContentTypeRepaired), rather than an
// aggregate count, so a specific repaired file can be identified and its correction verified.
public record FileContentTypeRepairedPayload(int TargetId, int OldContentType, int NewContentType, string? Name = null)
    : AuditPayload;

public record AuditLogsPurgedPayload(int PurgedCount)
    : AuditPayload;

// UnifiedDbAutoRecovered (0xFF01) → no Payload (there is only one unified DB, so no
// disambiguating data is needed - the event code alone identifies what happened)

public record VaultDbAutoRecoveredPayload(int DbNumber)
    : AuditPayload;
