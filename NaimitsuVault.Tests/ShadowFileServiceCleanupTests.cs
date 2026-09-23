// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// ShadowFileService.WriteAll() orphan-cleanup tests (TC-SHI-05..06).
///
/// Before this fix, orphan cleanup only ran when exactly one old-shadow candidate existed
/// (nkdb side) or by comparing against a single DB-read "old path" (vault side). Once an abrupt
/// process kill (crash, debugger stop, task kill) left more than one file carrying the shadow
/// magic, WriteAll() gave up on cleanup permanently - every future successful call added yet
/// another shadow without ever clearing the backlog (reproduced for real: 3 VLSH-magic files
/// found under a single-vault dev data/ folder on 2026-08-20). These tests confirm the fix:
/// WriteAll() now deletes every stale candidate it finds, regardless of how many accumulated.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class ShadowFileServiceCleanupTests : IDisposable
{
    private readonly string _testDir;
    private readonly ShadowFileService _service;
    private readonly StubVaultConnectionProvider _connectionProvider;

    private const string NkdbName = "NaimitsuVault.nkdb";

    public ShadowFileServiceCleanupTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"naimitsu_shadow_cleanup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _connectionProvider = new StubVaultConnectionProvider();
        _service = new ShadowFileService(_connectionProvider, NullLogger<ShadowFileService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    // ── TC-SHI-05 ─────────────────────────────────────────────────────────────
    // Two orphaned NKDB_SHADOW files (simulating two past abrupt-kill events) both get deleted
    // by a single successful WriteAll() call, leaving exactly the freshly-written shadow.

    [Fact]
    public async Task WriteAll_MultipleOrphanedNkdbShadows_AllDeletedOnNextSuccessfulWrite()
    {
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateMinimalSqliteFile(nkdbPath);

        var orphan1 = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        var orphan2 = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateSqliteWithMagic(orphan1, ShadowFileService.NKDB_SHADOW);
        CreateSqliteWithMagic(orphan2, ShadowFileService.NKDB_SHADOW);
        // Simulate leftover WAL sidecars from an interrupted write on one of the orphans.
        await File.WriteAllBytesAsync(orphan1 + "-shm", [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(orphan1 + "-wal", [4, 5, 6], TestContext.Current.CancellationToken);

        Assert.Equal(2, ShadowFileService.FindByMagic(_testDir, ShadowFileService.NKDB_SHADOW).Count);

        _connectionProvider.ActiveVaultDbPath = null; // isolate to the nkdb-shadow path only
        _service.WriteAll(dataDir: _testDir);

        var remaining = ShadowFileService.FindByMagic(_testDir, ShadowFileService.NKDB_SHADOW);
        Assert.Single(remaining);
        Assert.DoesNotContain(orphan1, remaining);
        Assert.DoesNotContain(orphan2, remaining);
        Assert.False(File.Exists(orphan1 + "-shm"));
        Assert.False(File.Exists(orphan1 + "-wal"));
    }

    // ── TC-SHI-06 ─────────────────────────────────────────────────────────────
    // A vault shadow that VaultRegistries no longer references (whether it was the previously-
    // current one, now superseded, or a plain leftover orphan) is deleted; only the freshly-written
    // shadow survives, and VaultRegistries.ShadowFileKey correctly points to it afterward.

    [Fact]
    public async Task WriteAll_UnreferencedVaultShadows_AreDeletedAndRegistryPointsToNewShadow()
    {
        var nkdbPath  = Path.Combine(_testDir, NkdbName);
        var vaultPath = Path.Combine(_testDir, "vault_origin_for_test.db");
        CreateMinimalSqliteFile(vaultPath);

        var staleReferenced = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        var pureOrphan       = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateSqliteWithMagic(staleReferenced, ShadowFileService.VAULT_SHADOW);
        CreateSqliteWithMagic(pureOrphan, ShadowFileService.VAULT_SHADOW);

        CreateVaultRegistriesWithShadowKey(nkdbPath, dbNumber: 1, shadowFileName: Path.GetFileName(staleReferenced));

        Assert.Equal(2, ShadowFileService.FindByMagic(_testDir, ShadowFileService.VAULT_SHADOW).Count);

        _connectionProvider.ActiveVaultDbPath = vaultPath;
        _service.WriteAll(currentVaultDbNumber: 1, dataDir: _testDir);

        var remaining = ShadowFileService.FindByMagic(_testDir, ShadowFileService.VAULT_SHADOW);
        Assert.Single(remaining);
        Assert.DoesNotContain(staleReferenced, remaining);
        Assert.DoesNotContain(pureOrphan, remaining);

        // VaultRegistries.ShadowFileKey now points to the surviving (new) shadow.
        var referencedName = await ReadShadowFileKeyAsFileNameAsync(nkdbPath, dbNumber: 1);
        Assert.Equal(Path.GetFileName(remaining[0]), referencedName);
    }

    // ── TC-SHI-07 ─────────────────────────────────────────────────────────────
    // Regression coverage for the fail-unsafe fallback fixed alongside AuthService.MultiVault's
    // orphan file GC: if VaultRegistries can't be read (e.g. the table doesn't exist /
    // a transient error), GetAllReferencedVaultShadowFileNames used to fall back to an empty set,
    // which made every legitimate vault shadow look "unreferenced" and get deleted. It must now
    // return null and make WriteAll skip the deletion scan entirely, leaving all existing shadow
    // files (including real orphans) untouched.

    [Fact]
    public void WriteAll_VaultRegistriesUnreadable_SkipsDeletionScan_ExistingShadowsSurvive()
    {
        var nkdbPath  = Path.Combine(_testDir, NkdbName);
        var vaultPath = Path.Combine(_testDir, "vault_origin_for_test.db");
        CreateMinimalSqliteFile(nkdbPath); // nkdb exists, but with no VaultRegistries table at all
        CreateMinimalSqliteFile(vaultPath);

        var preExisting = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateSqliteWithMagic(preExisting, ShadowFileService.VAULT_SHADOW);

        _connectionProvider.ActiveVaultDbPath = vaultPath;
        _service.WriteAll(currentVaultDbNumber: 1, dataDir: _testDir);

        // The freshly-written shadow was added, but the pre-existing one (which would look like an
        // orphan under the old empty-set fallback) must still be present - the scan never ran.
        var remaining = ShadowFileService.FindByMagic(_testDir, ShadowFileService.VAULT_SHADOW);
        Assert.Contains(preExisting, remaining);
        Assert.True(remaining.Count >= 2);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CreateMinimalSqliteFile(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS _dummy (id INTEGER PRIMARY KEY);";
        cmd.ExecuteNonQuery();
    }

    private static void CreateSqliteWithMagic(string path, uint magic)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {magic}; CREATE TABLE IF NOT EXISTS _dummy (id INTEGER PRIMARY KEY);";
        cmd.ExecuteNonQuery();
    }

    private static void CreateVaultRegistriesWithShadowKey(string nkdbPath, int dbNumber, string shadowFileName)
    {
        using var conn = new SqliteConnection($"Data Source={nkdbPath}");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS VaultRegistries (
                    DbNumber INTEGER PRIMARY KEY,
                    LastAccessedAt INTEGER,
                    EncryptedPayload BLOB NOT NULL,
                    ShadowFileKey BLOB
                );";
            create.ExecuteNonQuery();
        }
        var key = ShadowFileService.FileNameToKey(shadowFileName)!;
        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO VaultRegistries (DbNumber, EncryptedPayload, ShadowFileKey) VALUES (@db, @payload, @key)";
        insert.Parameters.AddWithValue("@db", dbNumber);
        insert.Parameters.AddWithValue("@payload", new byte[1]);
        var keyParam = insert.CreateParameter();
        keyParam.ParameterName = "@key";
        keyParam.Value         = key;
        keyParam.DbType        = System.Data.DbType.Binary;
        insert.Parameters.Add(keyParam);
        insert.ExecuteNonQuery();
    }

    private static async Task<string?> ReadShadowFileKeyAsFileNameAsync(string nkdbPath, int dbNumber)
    {
        await using var conn = new SqliteConnection($"Data Source={nkdbPath}");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ShadowFileKey FROM VaultRegistries WHERE DbNumber = @db";
        cmd.Parameters.AddWithValue("@db", dbNumber);
        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is byte[] key ? ShadowFileService.KeyToFileName(key) : null;
    }
}

internal sealed class StubVaultConnectionProvider : IVaultConnectionProvider
{
    public string? ActiveVaultDbPath { get; set; }
    public void SetActiveVault(string path) => ActiveVaultDbPath = path;
    public void ClearActiveVault() => ActiveVaultDbPath = null;
}
