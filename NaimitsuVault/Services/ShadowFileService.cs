// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Manages shadow files under data/.
/// WriteAll(): on normal completion, duplicates the nkdb + active vault.
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
        // by construction and safe to delete regardless of which vault it used to belong to.
        var vaultPath = connectionProvider.ActiveVaultDbPath;
        string? newVaultShadowPath = null;
        if (vaultPath != null && File.Exists(vaultPath))
        {
            try
            {
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
        // every OTHER file already carrying this magic is by definition a stale orphan - delete all
        // of them, not just when exactly one candidate is found. A single abrupt process kill (crash,
        // debugger stop, task kill) during a past WriteShadow() can leave more than one such orphan;
        // the old "only delete when unambiguous" rule then gave up forever and let orphans pile up
        // indefinitely instead of ever catching up on a later clean exit.
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
                        if (old != newShadowPath)
                            DeleteFileWithSidecars(old);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("ShadowFileService.WriteAll: nkdb shadow write failed (non-fatal). [{ExType}]", ex.GetType().Name);
        }

        // Orphaned vault-shadow cleanup: deliberately runs last, after the nkdb shadow above has
        // already captured the updated ShadowFileKey - so any vault shadow deleted here can no
        // longer be referenced by either the live nkdb or its current shadow.
        if (newVaultShadowPath != null)
        {
            try
            {
                // null means the referenced-name set could not be built reliably (see below) - skip
                // the deletion scan entirely rather than treating every legitimate other vault's
                // shadow as an orphan.
                var referenced = GetAllReferencedVaultShadowFileNames(dataDir);
                if (referenced != null)
                {
                    foreach (var candidate in FindByMagic(dataDir, VAULT_SHADOW))
                        if (!referenced.Contains(Path.GetFileName(candidate)))
                            DeleteFileWithSidecars(candidate);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("ShadowFileService.WriteAll: vault shadow orphan cleanup failed (non-fatal). [{ExType}]", ex.GetType().Name);
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
    // NOT in this set is unreferenced by any vault and safe to delete (see WriteAll), regardless
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
