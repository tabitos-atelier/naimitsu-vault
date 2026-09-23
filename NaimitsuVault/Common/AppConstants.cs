// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Common;

internal static class AppConstants
{
    internal const int MaxFileSizeMb = 20;
    internal const long MaxFileSizeBytes = (long)MaxFileSizeMb * 1024 * 1024;
    internal const int MaxFilesPerDrop = 10;

    // Expiration warning thresholds (days)
    internal const int SecretPasswordWarnDays   = 60; // Secret password expiration
    internal const int ProfileIdLicenseWarnDays = 60; // ID card / driver's license
    internal const int ProfilePassportWarnDays  = 90; // Passport

    // Single source of truth for certificate expiration warnings, shared by the viewer and the
    // gallery list. Set to 90 (approved) to match infrastructure industry standard (90-day notice).
    // Previously this was two separate constants kept manually equal; a past revert to a smaller
    // value like 30 in only one of them caused warning timing to diverge, producing an
    // inconsistency where the gallery showed an orange warning but the viewer still displayed
    // normally. Unifying into one constant makes that class of divergence impossible.
    internal const int CertExpirationWarnDays = 90;

    // Retention period for deleted secrets (days). Also reused as-is for deleted StoredFiles (gallery
    // attachments) — both follow the same 30-day grace period by design, so no separate constant exists.
    internal const int DeletedSecretRetentionDays = 30;

    // Retention period for audit log entries (days). Shared by AuditLogService's purge cutoff and the
    // window AuditLogViewModel loads for display, so the two cannot drift apart.
    internal const int AuditLogRetentionDays = 180;

    // Minimum interval between GalleryViewModel's full-table StoredFiles.ContentType repair scans.
    // Bounds how long a mid-session mismatch (e.g. a direct external DB edit) can stay quarantined
    // without requiring a full relock/restart, while avoiding a full decrypt-and-compare rescan on
    // every single Gallery page visit (nav-pane clicking triggers Page.Loaded -> LoadAsync each time).
    internal const int ContentTypeRepairThrottleMinutes = 5;
}
