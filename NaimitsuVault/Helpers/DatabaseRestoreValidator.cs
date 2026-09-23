// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using System.IO;
using System.Linq;

namespace NaimitsuVault.Helpers;

/// <summary>
/// Validation logic before a DB restore (3 lines of defense).
/// Called from AuthService.Restore. Exposed as internal for testing.
/// </summary>
internal static class DatabaseRestoreValidator
{
    // First line of defense: SQLite magic header check
    // Reads 16 bytes via stackalloc and validates with zero heap allocation.
    // A file lock by another process results in an IOException → falls through to the generic catch (reported via Common.GeneralError).
    internal static void ValidateSqliteHeader(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = fs.Read(header);
        if (read < 16 || !header.SequenceEqual("SQLite format 3\0"u8))
            throw new FormatException("The SQLite magic header does not match.");
    }

    // Second line of defense (connection) + third line of defense (schema/integrity)
    // static because no instance state is needed.
    // Mode=ReadOnly: no writes occur to the source file.
    // Even if a WAL file exists, a ReadOnly connection still succeeds and only references committed
    // data (ReadOnly mode never performs a WAL checkpoint write).
    // isUnifiedDb selects the required-table set: the unified DB (NaimitsuVault.nkdb) has no Secrets
    // table (UnifiedMetadata + VaultRegistries only) - it is a structurally different schema from a vault DB
    // (Secrets + VaultMetadata), not just a smaller one. Passing a vault path with isUnifiedDb=true, or
    // vice versa, always fails table validation regardless of file health.
    // No default value on purpose: every call site must consciously state which schema it expects,
    // rather than silently inheriting "vault DB" and re-introducing the self-destructing bug this
    // parameter was added to fix (a unified DB backup always fails vault-DB table validation).
    internal static async Task ValidateSqliteDatabaseAsync(string path, bool isUnifiedDb)
    {
        // Backup files run in WAL mode (like every DB in this app). Mode=ReadOnly alone still
        // makes SQLite create -wal/-shm sidecar files next to the source the moment a connection
        // opens (to attach the WAL index for reading) - and closing the connection afterward does
        // not remove them, since that requires a checkpoint (a write operation). The backup source
        // must be left byte-for-byte untouched, so the immutable=1 URI parameter is required here:
        // it tells SQLite the file will never change, skipping the WAL/locking machinery entirely.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(path).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync(); // A corrupted binary structure throws SqliteException

        await using var cmd = conn.CreateCommand();

        // Confirm the required tables exist.
        // Vault DB: Secrets, VaultMetadata (SecretHistory, StoredFiles, etc. can be created later via
        // migration, so they are not required). Unified DB: UnifiedMetadata, VaultRegistries.
        // The required-table count is derived from the array length rather than hardcoded, so that
        // adding a table to either set here cannot silently desync from the threshold below.
        string[] requiredTables = isUnifiedDb
            ? ["UnifiedMetadata", "VaultRegistries"]
            : ["Secrets", "VaultMetadata"];
        var tableNameList = string.Join(",", requiredTables.Select(t => $"'{t}'"));
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ({tableNameList});";
        var tableCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        if (tableCount < requiredTables.Length)
            throw new FormatException($"Required tables are missing (found: {tableCount}/{requiredTables.Length}).");

        // PRAGMA integrity_check(1): sufficient to stop as soon as the first anomaly is found
        cmd.CommandText = "PRAGMA integrity_check(1);";
        var integrityResult = await cmd.ExecuteScalarAsync() as string;
        if (integrityResult != "ok")
            throw new FormatException($"Detected internal B-tree corruption: {integrityResult}");
    }

    /// <summary>
    /// Runs all three lines of defense (header, schema, integrity) against a single file and
    /// normalizes any failure into a <see cref="RestoreValidationException"/> carrying the file
    /// name, so restore-flow callers get one uniform abort signal regardless of which check failed.
    /// </summary>
    internal static async Task ValidateOrThrowAsync(string path, bool isUnifiedDb)
    {
        try
        {
            ValidateSqliteHeader(path);
            await ValidateSqliteDatabaseAsync(path, isUnifiedDb);
        }
        catch (Exception ex) when (ex is FormatException or SqliteException)
        {
            throw new RestoreValidationException(Path.GetFileName(path), ex);
        }
    }
}

/// <summary>
/// Thrown when a backup file fails header/schema/integrity validation during restore.
/// Carries the offending file's name so the caller can report which file caused the abort.
/// </summary>
internal sealed class RestoreValidationException(string fileName, Exception inner)
    : Exception(inner.Message, inner)
{
    public string FileName { get; } = fileName;
}
