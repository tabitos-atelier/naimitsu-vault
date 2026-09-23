// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

/// <summary>
/// 0xABCD scheme:
///   A×0x1000 = major category (0x1=View, 0x3=Config/setting change, 0x4=Auth, 0x5=Save, 0x6=Delete/purge,
///                            0x7=Critical operation, 0x8–0xE=reserved, 0xF=system autonomous)
///   B×0x0100 = target        (0=Unlock, 1=Secret, 2=TimeMachine, 3=Gallery,
///                            4=Viewer (unused, reserved. File viewing is already folded into Gallery via FileViewed=0x1300),
///                            5=Profile, 6=Category (unused, reserved. The Categories feature was removed
///                            2026-08-17 - categories are now locale-defined presets with no DB table
///                            or audit trail), 7=Audit log, 8=Settings, F=Other)
///   CD       = subtype       (0x00–0xFF)
/// Parsing: major category = code &amp; 0xF000 / target = code &amp; 0x0F00
/// </summary>
public enum AuditEventCode
{
    // ── View (0x1000) ────────────────────────────────────────────────────
    SecretViewed      = 0x1100,   // View × Secret × 00
    TimeMachineViewed = 0x1200,   // View × TimeMachine × 00
    FileViewed        = 0x1300,   // View × Gallery × 00
    ProfileViewed     = 0x1500,   // View × Profile × 00

    // ── Config/setting change (0x3000) ──────────────────────────────────
    MasterPasswordChanged          = 0x3000,   // Config change × Unlock × 00
    AutoBackupSettingChanged       = 0x3800,   // Config change × Settings × 00
    AutoLockSettingChanged         = 0x3801,   // Config change × Settings × 01
    ScreenCaptureProtectionChanged = 0x3802,   // Config change × Settings × 02
    WindowsHelloChanged            = 0x3803,   // Config change × Settings × 03
    FaviconAutoFetchChanged        = 0x3804,   // Config change × Settings × 04

    // ── Auth (0x4000) ────────────────────────────────────────────────────
    AuthFailed    = 0x4000,   // Auth × Unlock × 00 (Payload = NULL)
    AuthSucceeded = 0x4001,   // Auth × Unlock × 01, records successful vault authentication

    // ── Save (0x5000) ────────────────────────────────────────────────────
    SecretSaved             = 0x5100,   // Save × Secret × 00
    TimeMachineRestored     = 0x5200,   // Save × TimeMachine × 00
    FileAdded               = 0x5300,   // Save × Gallery × 00
    ProfileSaved            = 0x5500,   // Save × Profile × 00

    // ── Delete/purge (0x6000) ────────────────────────────────────────────
    SecretSoftDeleted        = 0x6100,   // Delete × Secret × 00
    SecretUndeleted          = 0x6101,   // Delete × Secret × 01
    SecretPermanentlyDeleted = 0x6102,   // Delete × Secret × 02
    SecretAutoPurgedByExpiry = 0x6103,   // Delete × Secret × 03
    TimeMachineSlotDeleted   = 0x6200,   // Delete × TimeMachine × 00
    TimeMachineGenRotated    = 0x6201,   // Delete × TimeMachine × 01
    FileSoftDeleted          = 0x6300,   // Delete × Gallery × 00
    FileUndeleted            = 0x6301,   // Delete × Gallery × 01
    FilePermanentlyDeleted   = 0x6302,   // Delete × Gallery × 02
        // Subtype order (Soft/Undelete/Permanent/AutoPurge) deliberately mirrors the Secret group
        // above (SecretSoftDeleted/SecretUndeleted/SecretPermanentlyDeleted/SecretAutoPurgedByExpiry) -
        // both are the same delete-lifecycle shape, just targeting a different entity.
    FileAutoPurgedByExpiry   = 0x6303,   // Delete × Gallery × 03 - deliberately not shared with
        // SecretAutoPurgedByExpiry (0x6103): that code's target nibble (B=1) is fixed to Secret by this
        // enum's own banding scheme, so a File-side automatic purge gets its own code instead of a
        // reused Secret-targeted one.

    // ── Critical operation (0x7000) ─────────────────────────────────────
    // Only user-initiated privileged/dangerous operations. System-autonomous processing must not appear here
    EmergencyAccessCodeExecuted  = 0x7000,   // Critical op × Unlock × 00
    EmergencyAccessCodeGenerated = 0x7001,   // Critical op × Unlock × 01
    EmergencyAccessCodeRevoked   = 0x7002,   // Critical op × Unlock × 02
        // Generated/Executed/Revoked are the same emergency-access-code lifecycle and now live
        // together under the Unlock target (previously Generated/Revoked sat under Other=0x7F00/01,
        // far from Executed=0x7000 despite being the same feature).
    PlaintextImportExecuted     = 0x7100,   // Critical op × Secret × 00
    PlaintextExportExecuted     = 0x7101,   // Critical op × Secret × 01
    ImportItemFailed            = 0x7102,   // Critical op × Secret × 02: one entry per record that
        // failed to import (decrypt/parse/insert error) within a PlaintextImportExecuted run. The
        // record's title can't be trusted to identify it (the failure may be why it's unreadable),
        // so this carries the 1-based ordinal position in the source file instead.
    ExportItemFailed             = 0x7103,   // Critical op × Secret × 03: one entry per record that
        // failed to export (decrypt error) within a PlaintextExportExecuted run. Carries TargetId
        // like the Delete/purge codes above; Name is left for BuildDisplayItemsAsync's usual live
        // fallback since the same decrypt failure that skipped the record may also block a snapshot.
    CsvFormulaGuardApplied       = 0x7104,   // Critical op × Secret × 04: one entry per record whose
        // CSV export had a leading apostrophe inserted into at least one field (Title/UserId/
        // Password/Website/Email/Notes/CustomFields) because that field's value began with
        // =/+/-/@ - a spreadsheet formula-injection trigger character. Unlike ExportItemFailed the
        // record decrypted fine, so Name is embedded directly (the already-decrypted title) rather
        // than left for the live-lookup fallback.
    SecretFieldCopiedToClipboard = 0x7105,   // Critical op × Secret × 05: one entry per field
        // (Title/UserId/Password/Website/Email/a custom field/the TOTP code) copied to the OS
        // clipboard from the Secrets page or the draft-vs-confirmed compare dialog. Carries the
        // field's label, never the copied value itself.
    SecretAutoTypeExecuted       = 0x7106,   // Critical op × Secret × 06: one entry per field
        // (UserId or Password) sent via DirectInjectionDialog/Win32AutoTypeService to an external
        // window. A more sensitive exfiltration path than clipboard copy (bypasses the clipboard
        // auto-eraser entirely), so it gets the same one-row-per-field treatment.
    TimeMachineValueCopiedToClipboard = 0x7200,   // Critical op × TimeMachine × 00: one entry per
        // historical field value copied to the OS clipboard from the TimeMachine page's generation
        // comparison rows (current/Gen1/Gen2).
    FileExported                = 0x7300,   // Critical op × Gallery × 00
    ProfileFieldCopiedToClipboard = 0x7500,   // Critical op × Profile × 00: one entry per profile
        // field (or custom field) copied to the OS clipboard, from the Profile page or its
        // draft-vs-confirmed compare dialog. Profile is a per-vault singleton, so no TargetId.
    LocaleImportSucceeded       = 0x7800,   // Critical op × Settings × 00: one entry per custom locale
        // import attempt that completed (whether or not any keys needed soft-patching).
    LocaleImportFailed          = 0x7801,   // Critical op × Settings × 01: one entry per custom locale
        // import attempt rejected outright at the encoding/JSON-structure gate (Payload = NULL - the
        // failure reason is already shown to the user via notification at the moment it happens).
    LocaleImportKeyMissing      = 0x7802,   // Critical op × Settings × 02: one entry per key absent
        // from an imported custom locale file, auto-filled from the built-in fallback (soft patch).
    LocaleImportValueTooLong    = 0x7803,   // Critical op × Settings × 03: one entry per imported
        // custom-locale value rejected for being abnormally long relative to the built-in fallback
        // (skipped, fallback value used instead - same soft-patch treatment as a missing key).
    LocaleImportPlaceholderBroken = 0x7804, // Critical op × Settings × 04: one entry per imported
        // custom-locale value that dropped a format placeholder (e.g. "{0}") required by the
        // built-in fallback, which would otherwise throw FormatException at display time (skipped,
        // fallback value used instead - same soft-patch treatment as a missing key).
    VaultCreated                 = 0x7805,   // Critical op × Settings × 05: recorded once, immediately
        // after a new vault finishes creating, into that NEW vault's own audit log (not the vault the
        // user was in when they triggered AddVaultAsync) - CreateNewVaultCoreAsync switches the active
        // session/DEK to the new vault the instant it succeeds, so this is simply the first row that
        // vault's own log ever gets. Payload = NULL (the fact of creation needs no further detail).
    BackupExecuted               = 0x7F00,   // Critical op × Other × 00
    RestoreExecuted              = 0x7F01,   // Critical op × Other × 01

    // ── System (0xF000) ─────────────────────────────────────────────────
    // Only backend-autonomous/automatic processing. User-initiated actions must not appear here
    NoOpDraftDiscarded = 0xF100,   // System × Secret/Profile × 00: one aggregate entry per discard pass
        // (see SecretsViewModel.LoadSecretAsync/CleanUpNoOpDraftsAsync/GetDraftCompareDataAsync and
        // ProfileViewModel.CleanUpNoOpDraftAsync) - a draft byte-for-byte identical to the confirmed
        // data was auto-discarded, e.g. left behind by a bug that spuriously marked the model dirty
        // with no real edit. Carries only a count (like SecretAutoPurgedByExpiry above), since the discarded
        // draft held no information beyond what the confirmed data already has.
    FileContentTypeRepaired = 0xF300,   // System × Gallery × 00: one entry per StoredFile row whose
        // ContentType was silently corrected back to match its FileName extension
        // (see StoredFileRepository.RepairContentTypeMismatchesAsync). One row per repaired file,
        // so the exact file (and its old/new ContentType) can be identified and investigated -
        // unlike UnifiedDbAutoRecovered/VaultDbAutoRecovered below, which carry no per-item detail.
    AuditLogsPurged        = 0xF700,   // System × Audit log × 00
    UnifiedDbAutoRecovered = 0xFF01,   // System × Other × 01: unified DB was silently restored from its shadow
    VaultDbAutoRecovered   = 0xFF02,   // System × Other × 02: a vault DB was silently restored from its shadow
    AutoBackupExecuted     = 0xFF03,   // System × Other × 03: the shutdown-time automatic backup created a
        // new generation. System-autonomous (unlike BackupExecuted=0x7F00, which is the user-initiated
        // manual backup), so it lives here rather than in the Critical operation band. Not raised when
        // the automatic backup was skipped because the staged snapshot was unchanged from the newest
        // existing generation, or when it was disabled/skipped/failed.
}
