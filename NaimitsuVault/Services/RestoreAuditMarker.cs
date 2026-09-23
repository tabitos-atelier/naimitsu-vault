// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;

namespace NaimitsuVault.Services;

/// <summary>
/// Marker file that delays writing the RestoreExecuted audit log entry until the vault DEK is
/// established. Follows the same pattern as AutoBackupService.FailureFlagPath: using a flag file
/// to carry state notifications across a process restart.
///
/// Since restore is an operation tied to specific vaults, the target DbNumbers are recorded, and
/// the audit log is only written once each specific vault's DbNumber is actually unlocked (unlike
/// a vault-independent counter such as FailedLoginAttempts, recording it all under "the first
/// vault unlocked" would misattribute the event, so that is not done).
///
/// DbNumber, event code, and execution time are all operational data that cannot be used to infer
/// a person's secrets or behavior, so they are kept in plaintext (out of scope for the zero-knowledge principle).
/// </summary>
internal static class RestoreAuditMarker
{
    private const string FileName = "pending_restore_audit.flag";

    public readonly record struct PendingEntry(AuditEventCode Code, long TimestampUnix, IReadOnlyList<int> DbNumbers);

    private static string GetPath(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>Call immediately after RestoreAllFromBackupAsync succeeds. Writes nothing if dbNumbers is empty.</summary>
    public static void Write(string dataDir, AuditEventCode code, IReadOnlyList<int> dbNumbers)
    {
        if (dbNumbers.Count == 0) return;
        WriteCore(dataDir, code, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), dbNumbers);
    }

    public static PendingEntry? TryRead(string dataDir)
    {
        try
        {
            var path = GetPath(dataDir);
            if (!File.Exists(path)) return null;

            var lines = File.ReadAllLines(path);
            if (lines.Length < 3) return null;
            if (!int.TryParse(lines[0], out var codeInt)) return null;
            if (!long.TryParse(lines[1], out var ts)) return null;

            var dbNumbers = lines[2]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var n) ? n : (int?)null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .ToList();
            if (dbNumbers.Count == 0) return null;

            return new PendingEntry((AuditEventCode)codeInt, ts, dbNumbers);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes currentDbNumber from the target list and rewrites the file. The original execution
    /// time (TimestampUnix) is carried over. Deletes the marker file once the list becomes empty.
    /// </summary>
    public static void RemoveAndRewrite(string dataDir, PendingEntry entry, int currentDbNumber)
    {
        var remaining = entry.DbNumbers.Where(n => n != currentDbNumber).ToList();
        if (remaining.Count == 0)
        {
            Delete(dataDir);
            return;
        }
        WriteCore(dataDir, entry.Code, entry.TimestampUnix, remaining);
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
            // Audit logging isn't an essential feature, so ignore deletion failures (e.g. another process holding a lock).
        }
    }

    private static void WriteCore(string dataDir, AuditEventCode code, long timestampUnix, IReadOnlyList<int> dbNumbers)
    {
        try
        {
            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
            var csv = string.Join(",", dbNumbers);
            File.WriteAllText(GetPath(dataDir), $"{(int)code}\n{timestampUnix}\n{csv}");
        }
        catch
        {
            // Audit logging isn't an essential feature, so a write failure never affects whether restore itself succeeds.
        }
    }
}
