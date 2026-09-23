// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;

namespace NaimitsuVault.ViewModels;

/// <summary>Sent from the viewer when an image link is added or removed.</summary>
public record FileLinkChangedMessage(int FileId);

/// <summary>Sent when a deleted secret is restored from Time Machine.</summary>
public record SecretRestoredMessage(int SecretId);

/// <summary>Sent when a Time Machine restore rewrites data for an existing (non-deleted) secret.</summary>
public record SecretDataUpdatedMessage(int SecretId);

/// <summary>Sent when secrets are bulk-added via plaintext import.</summary>
public record SecretsImportedMessage;

/// <summary>Sent when storage size changes due to an added image.</summary>
public record StorageChangedMessage;

/// <summary>Cross-page jump: navigate to the given tag's page and, if needed, select a specific item.</summary>
public record NavigateToTagMessage(string Tag, int? JumpSecretId = null);

/// <summary>
/// Broadcasts a WindowCaptureProtectionEnabled change to ViewerWindow.
/// Fired from AppSettingsViewModel.OnWindowCaptureProtectionEnabledChanged.
/// </summary>
public sealed record CaptureProtectionChangedMessage(bool Enabled);

/// <summary>
/// Sent from DashboardPage to ShellWindow when the DB integrity check detects a problem.
/// Delta: number of new warnings detected this time (+1 or +2).
/// ShellWindow adds this to the badge counter.
/// </summary>
public sealed record DbIntegrityWarningMessage(int Delta);

/// <summary>
/// Broadcasts a FontFamily change to all open windows.
/// Fired from AppSettingsViewModel.OnFontFamilyChanged. FontFamily is null when reset to the system default.
/// </summary>
public sealed record FontFamilyChangedMessage(string? FontFamily);

/// <summary>
/// Broadcasts a favicon auto-fetch toggle change so an already-loaded SecretsViewModel starts
/// fetching immediately instead of waiting for the next LoadAsync (e.g. app restart).
/// Fired from AppSettingsViewModel.OnIsFaviconAutoFetchEnabledChanged.
/// </summary>
public sealed record FaviconAutoFetchToggledMessage(bool Enabled);

/// <summary>
/// Sent by AuditLogService immediately after it successfully writes a new entry, from every write
/// path (LogAsync, LogAuthFailedAsync, FlushPreAuthFailuresAsync, FlushPendingRestoreAuditAsync).
/// Audit entries are written from over a dozen scattered call sites (viewing a secret, copying a
/// field, changing a setting, etc.) that have nothing else in common, so this is the one choke
/// point that can notify listeners without every call site remembering to do so itself.
/// EventLevel mirrors AuditLog.EventLevel (0=normal, 1=notice, 2=critical) - carried along so a
/// listener can react to severity without a second round-trip to decrypt the entry.
/// </summary>
public sealed record AuditLogWrittenMessage(AuditEventCode Code, int EventLevel);
