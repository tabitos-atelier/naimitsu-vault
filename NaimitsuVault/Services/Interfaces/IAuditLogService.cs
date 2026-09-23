// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Services.Interfaces;

public interface IAuditLogService
{
    /// <summary>
    /// Logs an event for which the DEK is available (write path).
    /// The payload is stored AES-256-GCM encrypted with the DEK.
    /// <para>
    /// Implementation discipline: complete the encryption that uses <c>dek.Span</c> before any <c>await</c>.
    /// <c>DekScope</c> is a <c>readonly struct</c> so it can be held across an async state machine, but
    /// the <c>Span</c> property (a <c>ref struct</c>) cannot cross an <c>await</c> as a local variable.
    /// Completing encryption synchronously first therefore naturally satisfies this rule.
    /// </para>
    /// </summary>
    Task LogAsync(
        AuditEventCode code,
        AuditPayload? payload,
        DekScope dek,
        CancellationToken ct = default);

    /// <summary>
    /// Dedicated to events with no DEK available (authentication failure). Recorded with Payload = NULL.
    /// Uses a raw SqliteCommand that bypasses the EF Core guard.
    /// </summary>
    Task LogAuthFailedAsync(CancellationToken ct = default);

    /// <summary>
    /// For DashboardPage section 4: returns the most recent <paramref name="count"/> entries ordered by CreatedAt descending.
    /// Decrypts the payload to build the event summary text (read path).
    /// <para>
    /// Note: <c>dek</c> is used across multiple <c>await</c>s.
    /// If <c>Lock()</c> is called during the read, <c>dek.Span</c> returns a zeroed span,
    /// falling back to a decryption-failure display (shows a deleted-item placeholder).
    /// No secret data leaks (fail-safe design).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AuditLogDisplayItem>> GetRecentAsync(
        int count,
        DekScope dek,
        CancellationToken ct = default);

    /// <summary>
    /// For AuditLogPage: returns all entries with CreatedAt at or after <paramref name="since"/> (read path).
    /// <para>Same cross-await design as <see cref="GetRecentAsync"/>.</para>
    /// </summary>
    Task<IReadOnlyList<AuditLogDisplayItem>> GetAllAfterAsync(
        DateTimeOffset since,
        DekScope dek,
        CancellationToken ct = default);

    /// <summary>
    /// 180-day rotation. Self-logs an AuditLogsPurged event after running.
    /// Skips self-logging if <paramref name="purgeLogDek"/> is null (e.g. at startup while still locked).
    /// </summary>
    Task PurgeOldLogsAsync(DekScope? purgeLogDek = null, CancellationToken ct = default);

    /// <summary>
    /// Whether the audit log has any entries at all. No DEK needed (a plain row-count query,
    /// nothing is decrypted) - used to decide whether the AuditLog nav item / dashboard
    /// jump-link have anything to show.
    /// </summary>
    Task<bool> HasAnyLogsAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes the authentication-failure count accumulated in memory before unlock back to the vault DB.
    /// Call right after vault entry completes (while the DEK is available).
    /// </summary>
    Task FlushPreAuthFailuresAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// In case a restore ran while no vault DEK was available, writes back the pending
    /// RestoreExecuted event stashed in <see cref="RestoreAuditMarker"/>.
    /// Only records it and removes it from the target list when <paramref name="currentDbNumber"/>
    /// is included in the marker's target DbNumbers (unlike the vault-independent
    /// <see cref="FlushPreAuthFailuresAsync"/>, the target is restricted here to avoid misattributing
    /// the event to an unrelated vault).
    /// Call right after vault entry completes (while the DEK is available).
    /// </summary>
    Task FlushPendingRestoreAuditAsync(DekScope dek, int currentDbNumber, string dataDir, CancellationToken ct = default);

}
