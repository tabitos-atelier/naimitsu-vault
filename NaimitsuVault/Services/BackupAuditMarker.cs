// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using NaimitsuVault.Models;

namespace NaimitsuVault.Services;

/// <summary>
/// Marker file that delays writing an automatic backup audit entry (AutoBackupExecuted / AutoBackupFailed /
/// AutoBackupSkippedFolderMissing) when the app exits while locked - re-authentication cancelled, or the
/// database failing to initialize after unlock. The DEK is already zeroed on those paths, so the entry
/// cannot be sealed there; it is written by IAuditLogService.FlushPendingBackupAuditAsync the next time
/// any vault is unlocked.
///
/// Kept separate from RestoreAuditMarker so that a pending restore entry and a pending backup entry do
/// not overwrite each other. Unlike RestoreAuditMarker it holds no vault numbers: the automatic backup
/// copies every vault at once and does not belong to one specific vault.
///
/// The event code and exit time cannot be used to infer a person's secrets or behavior, so they are kept
/// in plaintext (out of scope for the zero-knowledge principle, the same judgment as RestoreAuditMarker).
/// </summary>
internal static class BackupAuditMarker
{
    private const string FileName = "pending_backup_audit.flag";

    public readonly record struct PendingEntry(AuditEventCode Code, long TimestampUnix);

    private static string GetPath(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>Records the result of an exit-time automatic backup. Overwrites any older pending entry.</summary>
    public static void Write(string dataDir, AuditEventCode code)
    {
        try
        {
            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
            File.WriteAllText(GetPath(dataDir), $"{(int)code}\n{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        }
        catch
        {
            // Audit logging isn't an essential feature; a write failure must never block the exit.
        }
    }

    public static PendingEntry? TryRead(string dataDir)
    {
        try
        {
            var path = GetPath(dataDir);
            if (!File.Exists(path)) return null;

            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) return null;
            if (!int.TryParse(lines[0], out var codeInt)) return null;
            if (!long.TryParse(lines[1], out var ts)) return null;

            var code = (AuditEventCode)codeInt;
            if (code is not (AuditEventCode.AutoBackupExecuted or AuditEventCode.AutoBackupFailed
                or AuditEventCode.AutoBackupSkippedFolderMissing))
                return null;

            return new PendingEntry(code, ts);
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string dataDir)
    {
        try
        {
            var path = GetPath(dataDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Audit logging isn't an essential feature, so ignore deletion failures.
        }
    }
}
