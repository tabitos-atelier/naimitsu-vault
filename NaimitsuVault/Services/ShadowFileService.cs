// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Manages shadow files under data/.
/// WriteAll(): on normal completion, duplicates the nkdb + active vault.
///
/// A shadow that WriteAll() supersedes is deleted only when it is a plain generation change: its
/// content lives on in the file it was copied from. Any other shadow that nothing refers to is moved
/// to an orphaned_* folder (OrphanedFileEvacuator), never deleted - "unreferenced" can also mean that
/// the unified DB itself was replaced and no longer knows the vaults that are still on disk.
/// </summary>
internal sealed class ShadowFileService(
    IVaultConnectionProvider connectionProvider,
    ILogger<ShadowFileService> logger)
{
    // ── Magic constants (identified via PRAGMA user_version) ────────────────────────────
    internal const uint NKDB_ORIGIN   = 0x4E4B4F52u; // NKOR
    internal const uint NKDB_SHADOW   = 0x4E4B5348u; // NKSH
    internal const uint VAULT_ORIGIN  = 0x564C4F52u; // VLOR
    internal const uint VAULT_SHADOW  = 0x564C5348u; // VLSH

    // The main nkdb file is identified by a fixed name. The shadow uses a random 48-character name + the NKDB_SHADOW magic.
    private const string NkdbFileName = "NaimitsuVault.nkdb";

    // ── Instance methods ──────────────────────────────────────────────────

    /// <summary>
    /// Updates the shadow of the nkdb and active vault. Called from ShellWindow.Closed.
    /// Catches all exceptions and only logs them.
    /// </summary>
    /// <param name="currentVaultDbNumber">
    /// AppSession.CurrentVaultDbNumber (plaintext PK). If provided, the target VaultRegistries row is
    /// pinned by DbNumber. Only falls back to guessing via LastAccessedAt when null.
    /// </param>
    /// <param name="dataDir">Test-only override. Production callers always omit this (defaults to the real data/ folder).</param>
    public void WriteAll(int? currentVaultDbNumber = null, string? dataDir = null)
    {
        dataDir ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

        // Ordering invariant: the active vault's shadow must be created and its ShadowFileKey
        // committed to the LIVE nkdb *before* the nkdb shadow is taken, and any now-orphaned vault
        // shadow must only be deleted *after* the new nkdb shadow exists. Otherwise the nkdb shadow
        // (or, in a narrower crash window, the not-yet-replaced previous nkdb shadow) can end up
        // referencing a vault shadow file that has already been physically deleted - if the live
        // nkdb later corrupts and self-heals from that shadow, the restored ShadowFileKey then
        // points nowhere, so a subsequent vault-side corruption can no longer be recovered
        // (TryRecoverVaultFromShadowAsync finds no file). Every step below preserves the invariant
        // that any shadow/live file still on disk at any point only ever references vault shadow
        // files that are also still on disk.

        // active vault shadow: random 48-character name + VAULT_SHADOW magic. Unlike the nkdb (always
        // exactly one), multiple vaults (1-3) can each have their own shadow, so an orphan can only be
        // told apart from a legitimate other vault's shadow by checking VaultRegistries: any
        // VAULT_SHADOW-tagged file that no vault's ShadowFileKey currently points to is unreferenced
        // by construction. Of those, only the shadow this vault pointed to until now is deleted (it is
        // the previous generation of the shadow written below); the rest are moved aside.
        var evacuator = new OrphanedFileEvacuator(dataDir, logger);
        var vaultPath = connectionProvider.ActiveVaultDbPath;
        string? newVaultShadowPath = null;
        string? previousVaultShadowName = null;
        if (vaultPath != null && File.Exists(vaultPath))
        {
            try
            {
                previousVaultShadowName = ReadCurrentShadowFileName(dataDir, currentVaultDbNumber);
                newVaultShadowPath = WriteShadow(vaultPath, dataDir, VAULT_SHADOW);
                if (newVaultShadowPath != null)
                    UpdateShadowFileKeyInNkdb(newVaultShadowPath, dataDir, currentVaultDbNumber);
            }
            catch (Exception ex)
            {
                logger.LogWarning("ShadowFileService.WriteAll: vault shadow write failed (non-fatal). [{ExType}]", ex.GetType().Name);
            }
        }

        // nkdb shadow: random 48-character name + NKDB_SHADOW magic. Only one nkdb ever exists, so
        // every OTHER file already carrying this magic is superseded - handle all of them, not just
        // when exactly one candidate is found. A single abrupt process kill (crash, debugger stop,
        // task kill) during a past WriteShadow() can leave more than one such file; the old "only
        // when unambiguous" rule then gave up forever and let them pile up indefinitely.
        // A superseded shadow is deleted only if every registration it holds is still in the live
        // nkdb. One that knows a vault the live nkdb does not (the live nkdb was replaced, e.g. by an
        // empty one that then went through first-time setup) may be the last copy of that
        // registration, so it is moved aside instead.
        // Taken after the vault shadow above, so it captures the live nkdb's just-updated
        // ShadowFileKey rather than a stale reference to the vault shadow this cycle is about to retire.
        try
        {
            var nkdbPath = Path.Combine(dataDir, NkdbFileName);
            if (File.Exists(nkdbPath))
            {
                var oldShadowCandidates = FindByMagic(dataDir, NKDB_SHADOW);
                var newShadowPath = WriteShadow(nkdbPath, dataDir, NKDB_SHADOW);
                if (newShadowPath != null)
                {
                    foreach (var old in oldShadowCandidates)
                    {
                        if (old == newShadowPath) continue;
                        if (IsRegistrationCarriedOver(old, nkdbPath))
                            DeleteFileWithSidecars(old);
                        else
                            evacuator.TryEvacuate(old);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("ShadowFileService.WriteAll: nkdb shadow write failed (non-fatal). [{ExType}]", ex.GetType().Name);
        }

        // Unreferenced vault-shadow cleanup: deliberately runs last, after the nkdb shadow above has
        // already captured the updated ShadowFileKey - so any vault shadow removed here can no
        // longer be referenced by either the live nkdb or its current shadow.
        if (newVaultShadowPath != null)
        {
            try
            {
                // null means the referenced-name set could not be built reliably (see below) - skip
                // the scan entirely rather than treating every legitimate other vault's shadow as
                // unreferenced.
                var referenced = GetAllReferencedVaultShadowFileNames(dataDir);
                if (referenced != null)
                {
                    foreach (var candidate in FindByMagic(dataDir, VAULT_SHADOW))
                    {
                        var candidateName = Path.GetFileName(candidate);
                        if (referenced.Contains(candidateName)) continue;
                        if (candidateName == previousVaultShadowName)
                            DeleteFileWithSidecars(candidate);
                        else
                            evacuator.TryEvacuate(candidate);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("ShadowFileService.WriteAll: unreferenced vault shadow cleanup failed (non-fatal). [{ExType}]", ex.GetType().Name);
            }
        }
    }

    private string? WriteShadow(string originPath, string dataDir, uint shadowMagic)
    {
        var tmpName = GenerateBase64UrlName();
        var tmpPath = Path.Combine(dataDir, tmpName);
        try
        {
            // Phase 1: guarantee the connection is closed the moment the backup completes
            using (var src = new SqliteConnection($"Data Source={originPath}"))
            using (var dst = new SqliteConnection($"Data Source={tmpPath}"))
            {
                src.Open();
                dst.Open();
                src.BackupDatabase(dst);
            } // The handle on tmpPath is 100% released here

            // Phase 2: write PRAGMA user_version after the handle is released
            SetPragmaUserVersion(tmpPath, shadowMagic);

            // Phase 3: clear the pool before performing the Move (eliminates handle contention)
            SqliteConnection.ClearAllPools();

            var shadowName = GenerateBase64UrlName();
            var shadowPath = Path.Combine(dataDir, shadowName);
            File.Move(tmpPath, shadowPath, overwrite: true);
            return shadowPath;
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    // All vault shadow file names (across every vault row, not just the currently active one)
    // that VaultRegistries.ShadowFileKey still points to. A VAULT_SHADOW-magic file whose name is
    // NOT in this set is unreferenced by any vault (see WriteAll for what happens to it), regardless
    // of which vault it used to belong to or whether that other vault is currently unlocked.
    // Returns null (rather than falling back to an empty set) when VaultRegistries could not be read
    // reliably. The empty-set fallback used to make WriteAll's orphan scan treat every legitimate
    // other vault's shadow file as unreferenced and delete it - the same fail-unsafe direction fixed
    // in AuthService.MultiVault.EnterVaultCoreAsync's orphan file GC (see BuildRegisteredHashesAsync).
    private HashSet<string>? GetAllReferencedVaultShadowFileNames(string dataDir)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var nkdbPath = Path.Combine(dataDir, NkdbFileName);
        if (!File.Exists(nkdbPath)) return names;
        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource  = nkdbPath,
                Mode        = SqliteOpenMode.ReadOnly,
                ForeignKeys = false,
            };
            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ShadowFileKey FROM VaultRegistries WHERE ShadowFileKey IS NOT NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader[0] is byte[] key)
                {
                    var fileName = KeyToFileName(key);
                    if (fileName != null) names.Add(fileName);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("ShadowFileService.GetAllReferencedVaultShadowFileNames: could not read VaultRegistries, skipping orphan shadow scan (non-fatal). [{ExType}]", ex.GetType().Name);
            return null;
        }
        return names;
    }

    // The shadow file name the target vault row points to right now, i.e. before this WriteAll()
    // replaces it. Selects the row the same way UpdateShadowFileKeyInNkdb does. null when there is
    // none or it cannot be read - the caller then deletes nothing and moves every unreferenced
    // vault shadow aside.
    private static string? ReadCurrentShadowFileName(string dataDir, int? dbNumber)
    {
        var nkdbPath = Path.Combine(dataDir, NkdbFileName);
        if (!File.Exists(nkdbPath)) return null;
        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource  = nkdbPath,
                Mode        = SqliteOpenMode.ReadOnly,
                ForeignKeys = false,
            };
            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = dbNumber.HasValue
                ? "SELECT ShadowFileKey FROM VaultRegistries WHERE DbNumber = @db"
                : "SELECT ShadowFileKey FROM VaultRegistries ORDER BY LastAccessedAt DESC LIMIT 1";
            if (dbNumber.HasValue)
            {
                var dbParam = cmd.CreateParameter();
                dbParam.ParameterName = "@db";
                dbParam.Value         = dbNumber.Value;
                cmd.Parameters.Add(dbParam);
            }
            return cmd.ExecuteScalar() is byte[] key ? KeyToFileName(key) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteFileWithSidecars(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(path + "-shm"); } catch { }
        try { File.Delete(path + "-wal"); } catch { }
    }

    private void UpdateShadowFileKeyInNkdb(string shadowPath, string dataDir, int? dbNumber)
    {
        var nkdbPath = Path.Combine(dataDir, NkdbFileName);
        if (!File.Exists(nkdbPath)) return;

        var shadowFileName = Path.GetFileName(shadowPath);
        var shadowKey      = FileNameToKey(shadowFileName);
        if (shadowKey == null) return;

        try
        {
            using var conn = new SqliteConnection($"Data Source={nkdbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = dbNumber.HasValue
                ? "UPDATE VaultRegistries SET ShadowFileKey = @key WHERE DbNumber = @db"
                : @"
                    UPDATE VaultRegistries
                    SET ShadowFileKey = @key
                    WHERE DbNumber = (
                        SELECT DbNumber FROM VaultRegistries
                        ORDER BY LastAccessedAt DESC
                        LIMIT 1
                    )";
            var p = cmd.CreateParameter();
            p.ParameterName = "@key";
            p.Value         = shadowKey;
            p.SqliteType    = SqliteType.Blob;
            cmd.Parameters.Add(p);
            if (dbNumber.HasValue)
            {
                var dbParam = cmd.CreateParameter();
                dbParam.ParameterName = "@db";
                dbParam.Value         = dbNumber.Value;
                cmd.Parameters.Add(dbParam);
            }
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            logger.LogWarning("ShadowFileService: VaultRegistries.ShadowFileKey UPDATE failed. [{ExType}]", ex.GetType().Name);
        }
    }

    // ── Registration comparison ─────────────────────────────────────────────

    private const string SlotEntryPrefix  = "slot|";
    private const string VaultEntryPrefix = "vault|";

    /// <summary>
    /// Reads what a unified DB file (the live nkdb or one of its shadows) knows about vaults: the
    /// K_shared slots (UnifiedMetadata 0x0001-0x0003) and each VaultRegistries row's DbNumber +
    /// EncryptedPayload. LastAccessedAt and ShadowFileKey are left out because ordinary use changes them.
    /// A missing table counts as "no entries" (a file that was created but never initialized);
    /// null means the file could not be read.
    /// </summary>
    internal static HashSet<string>? ReadRegistrations(string dbPath)
    {
        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource  = dbPath,
                Mode        = SqliteOpenMode.ReadOnly,
                ForeignKeys = false,
                // The caller may move or delete this file right afterwards.
                Pooling     = false,
            };
            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();

            var entries = new HashSet<string>(StringComparer.Ordinal);
            if (TableExists(conn, "UnifiedMetadata"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT ConfigKey, ConfigValue FROM UnifiedMetadata WHERE ConfigKey BETWEEN {UnifiedMetadataKey.KSharedSlot_1} AND {UnifiedMetadataKey.KSharedSlot_3}";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    entries.Add($"{SlotEntryPrefix}{reader.GetInt64(0)}|{EncodeValue(reader.GetValue(1))}");
            }
            if (TableExists(conn, "VaultRegistries"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT DbNumber, EncryptedPayload FROM VaultRegistries";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    entries.Add($"{VaultEntryPrefix}{reader.GetInt64(0)}|{EncodeValue(reader.GetValue(1))}");
            }
            return entries;
        }
        catch
        {
            return null;
        }
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    private static string EncodeValue(object value) => value switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        DBNull       => string.Empty,
        _            => "s:" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    // True only when both files could be read and the live nkdb still holds every registration the
    // shadow does. Anything else (a difference, or a read failure on either side) is "not carried
    // over", which makes the caller keep the shadow.
    private static bool IsRegistrationCarriedOver(string shadowPath, string nkdbPath)
    {
        var shadowEntries = ReadRegistrations(shadowPath);
        if (shadowEntries == null) return false;
        if (shadowEntries.Count == 0) return true;
        var liveEntries = ReadRegistrations(nkdbPath);
        return liveEntries != null && shadowEntries.IsSubsetOf(liveEntries);
    }

    private static bool HasKSharedSlot(HashSet<string> entries)
    {
        foreach (var entry in entries)
            if (entry.StartsWith(SlotEntryPrefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    // ── Unregistered nkdb detection ─────────────────────────────────────────

    internal enum UnregisteredNkdbRecovery
    {
        /// <summary>Nothing to do: the nkdb has a registration, or no shadow has one.</summary>
        NotApplicable,
        /// <summary>The nkdb was moved aside and replaced by the shadow.</summary>
        Restored,
        /// <summary>The shadow could not be copied over the nkdb; the nkdb was put back.</summary>
        RestoreFailed,
        /// <summary>Two or more shadows hold a registration, so the right one cannot be chosen.</summary>
        Ambiguous,
    }

    /// <summary>
    /// Handles an nkdb that is healthy as a file but holds no K_shared slot (the state that leads to
    /// first-time setup) while a healthy nkdb shadow under data/ does hold one. That combination means
    /// the nkdb was replaced by a fresh one - for example by copying a newly extracted app folder,
    /// already launched once, over an existing installation. Setup would then register a new vault
    /// and leave the existing ones unknown to the app, so the shadow is restored instead. The
    /// replaced nkdb is moved to an orphaned_* folder, not deleted.
    /// </summary>
    internal static async Task<UnregisteredNkdbRecovery> TryRecoverUnregisteredNkdbAsync(
        string dataDir, string nkdbPath, ILogger logger)
    {
        var liveEntries = ReadRegistrations(nkdbPath);
        if (liveEntries == null || HasKSharedSlot(liveEntries))
            return UnregisteredNkdbRecovery.NotApplicable;

        var registeredShadows = new List<string>();
        foreach (var candidate in FindByMagic(dataDir, NKDB_SHADOW))
        {
            if (!await IsShadowHealthyAsync(candidate, NKDB_SHADOW)) continue;
            var entries = ReadRegistrations(candidate);
            if (entries != null && HasKSharedSlot(entries))
                registeredShadows.Add(candidate);
        }

        if (registeredShadows.Count == 0)
            return UnregisteredNkdbRecovery.NotApplicable;
        if (registeredShadows.Count >= 2)
        {
            logger.LogWarning("The nkdb holds no vault registration and {Count} shadows do. Cannot choose one.", registeredShadows.Count);
            return UnregisteredNkdbRecovery.Ambiguous;
        }

        SqliteConnection.ClearAllPools();
        var evacuator = new OrphanedFileEvacuator(dataDir, logger);
        if (!evacuator.TryEvacuate(nkdbPath))
            return UnregisteredNkdbRecovery.NotApplicable;

        if (await TryRestoreFromShadowAsync(registeredShadows[0], nkdbPath, NKDB_ORIGIN))
        {
            logger.LogInformation("The nkdb held no vault registration while its shadow did. Restored the nkdb from the shadow.");
            return UnregisteredNkdbRecovery.Restored;
        }

        evacuator.TryMoveBack(nkdbPath);
        logger.LogWarning("The nkdb holds no vault registration and restoring it from the shadow failed.");
        return UnregisteredNkdbRecovery.RestoreFailed;
    }

    // ── Static utilities ────────────────────────────────────────────────────

    /// <summary>
    /// Checks that the specified file exists, is at least as large as the SQLite header, and that
    /// PRAGMA quick_check returns "ok". Magic-agnostic (unlike IsShadowHealthyAsync) - used to detect
    /// corruption in fixed-identity origin files (nkdb, active vault) where the magic itself does not
    /// need to be verified.
    /// </summary>
    public static async Task<bool> IsFileHealthyAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length < 100) return false;
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource  = filePath,
                Mode        = SqliteOpenMode.ReadOnly,
                ForeignKeys = false,
            };
            await using var conn = new SqliteConnection(csb.ToString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            var result = await cmd.ExecuteScalarAsync();
            return result is string s && s.Equals("ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks that the specified file's PRAGMA user_version matches expectedMagic
    /// and that PRAGMA quick_check returns "ok".
    /// </summary>
    public static async Task<bool> IsShadowHealthyAsync(string filePath, uint expectedMagic)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            var magic = await ReadMagicAsync(filePath);
            if (magic != expectedMagic) return false;

            var csb = new SqliteConnectionStringBuilder
            {
                DataSource  = filePath,
                Mode        = SqliteOpenMode.ReadOnly,
                ForeignKeys = false,
            };
            await using var conn = new SqliteConnection(csb.ToString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            var result = await cmd.ExecuteScalarAsync();
            return result is string s && s.Equals("ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Overwrite-copies the shadow onto the primary file (atomic: .tmp -> rename).
    /// On success, rewrites targetPath's PRAGMA user_version to originMagic.
    /// </summary>
    public static async Task<bool> TryRestoreFromShadowAsync(
        string shadowPath, string targetPath, uint originMagic)
    {
        if (!File.Exists(shadowPath)) return false;
        var tmpPath = targetPath + ".restore.tmp";
        try
        {
            await Task.Run(() => File.Copy(shadowPath, tmpPath, overwrite: true));
            SetPragmaUserVersion(tmpPath, originMagic);
            SqliteConnection.ClearAllPools();
            File.Move(tmpPath, targetPath, overwrite: true);
            return true;
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            return false;
        }
    }

    /// <summary>
    /// Scans under data/ and returns the path list of 48-character files matching the specified magic.
    /// </summary>
    public static IReadOnlyList<string> FindByMagic(string dataDir, uint magic)
    {
        if (!Directory.Exists(dataDir)) return [];

        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(dataDir))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 48) continue;
            if (!IsBase64UrlName(name)) continue;

            try
            {
                var header = ReadHeader64(path);
                if (header == null || header.Length < 64) continue;
                if (header[0] != 0x53 || header[1] != 0x51 || header[2] != 0x4C) continue;
                uint uv = ((uint)header[60] << 24)
                        | ((uint)header[61] << 16)
                        | ((uint)header[62] << 8)
                        |  (uint)header[63];
                if (uv == magic) result.Add(path);
            }
            catch { }
        }
        return result;
    }

    // ── Internal helpers ─────────────────────────────────────────────────────────

    internal static async Task<uint?> ReadMagicAsync(string filePath)
    {
        try
        {
            var header = await Task.Run(() => ReadHeader64(filePath));
            if (header == null || header.Length < 64) return null;
            return ((uint)header[60] << 24)
                 | ((uint)header[61] << 16)
                 | ((uint)header[62] << 8)
                 |  (uint)header[63];
        }
        catch { return null; }
    }

    private static byte[]? ReadHeader64(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < 64) return null;
        var buf = new byte[64];
        // Stream.Read is allowed to return fewer bytes than requested even when more are available
        // (fragmented I/O, load). ReadExactly loops until the buffer is full or throws, so a healthy
        // shadow/vault file is never misjudged as unreadable from a short read alone.
        fs.ReadExactly(buf);
        return buf;
    }

    internal static void SetPragmaUserVersion(string dbPath, uint version)
    {
        SqliteConnection.ClearAllPools();
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version}";
        cmd.ExecuteNonQuery();
    }

    internal static string GenerateBase64UrlName()
    {
        var bytes = new byte[36];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static bool IsBase64UrlName(string name)
    {
        foreach (char c in name)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == '-' || c == '_')
                continue;
            return false;
        }
        return true;
    }

    internal static byte[]? FileNameToKey(string fileName)
    {
        if (fileName.Length != 48) return null;
        var standardB64 = fileName.Replace('-', '+').Replace('_', '/');
        var buf = new byte[36];
        return Convert.TryFromBase64String(standardB64, buf, out int written) && written == 36
            ? buf
            : null;
    }

    internal static string? KeyToFileName(byte[] key)
    {
        if (key.Length != 36) return null;
        return Convert.ToBase64String(key).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
