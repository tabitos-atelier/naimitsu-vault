// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Tests for ShadowFileService's static utility methods (TC-SH-01..03).
///
/// Verifies FindByMagic / IsShadowHealthyAsync / TryRestoreFromShadowAsync against real
/// SQLite files.
/// Confirms that a 48-character anonymized file carrying the NKDB_SHADOW magic functions
/// as the unified DB's shadow.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class SelfHealingTests : IDisposable
{
    private readonly string _tempDir;

    public SelfHealingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"naimitsu_sh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-SH-01: FindByMagic correctly distinguishes NKDB_SHADOW / VAULT_ORIGIN / VAULT_SHADOW
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void FindByMagic_ReturnsOnlyMatchingMagic()
    {
        // Arrange: generate one file for each magic value
        var nkdbShadowPath  = Path.Combine(_tempDir, ShadowFileService.GenerateBase64UrlName());
        var vaultOriginPath = Path.Combine(_tempDir, ShadowFileService.GenerateBase64UrlName());
        var vaultShadowPath = Path.Combine(_tempDir, ShadowFileService.GenerateBase64UrlName());

        CreateSqliteWithMagic(nkdbShadowPath,  ShadowFileService.NKDB_SHADOW);
        CreateSqliteWithMagic(vaultOriginPath, ShadowFileService.VAULT_ORIGIN);
        CreateSqliteWithMagic(vaultShadowPath, ShadowFileService.VAULT_SHADOW);

        // Act
        var nkdbShadowMatches  = ShadowFileService.FindByMagic(_tempDir, ShadowFileService.NKDB_SHADOW);
        var vaultOriginMatches = ShadowFileService.FindByMagic(_tempDir, ShadowFileService.VAULT_ORIGIN);
        var vaultShadowMatches = ShadowFileService.FindByMagic(_tempDir, ShadowFileService.VAULT_SHADOW);

        // Assert
        Assert.Single(nkdbShadowMatches);
        Assert.Equal(nkdbShadowPath, nkdbShadowMatches[0]);

        Assert.Single(vaultOriginMatches);
        Assert.Equal(vaultOriginPath, vaultOriginMatches[0]);

        Assert.Single(vaultShadowMatches);
        Assert.Equal(vaultShadowPath, vaultShadowMatches[0]);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-SH-02: IsShadowHealthyAsync correctly judges healthy vs. mismatched NKDB_SHADOW magic
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task IsShadowHealthyAsync_ReturnsTrue_ForHealthyNkdbShadow()
    {
        // Arrange: a 48-character file carrying the NKDB_SHADOW magic (same format as the unified DB shadow)
        var shadowPath = Path.Combine(_tempDir, ShadowFileService.GenerateBase64UrlName());
        CreateSqliteWithMagic(shadowPath, ShadowFileService.NKDB_SHADOW);

        // Act
        bool healthy = await ShadowFileService.IsShadowHealthyAsync(shadowPath, ShadowFileService.NKDB_SHADOW);

        // Assert
        Assert.True(healthy);
    }

    [Fact]
    public async Task IsShadowHealthyAsync_ReturnsFalse_ForWrongMagic()
    {
        // Arrange: verify a file carrying NKDB_ORIGIN against NKDB_SHADOW (a mismatch)
        var path = Path.Combine(_tempDir, ShadowFileService.GenerateBase64UrlName());
        CreateSqliteWithMagic(path, ShadowFileService.NKDB_ORIGIN);

        // Act
        bool healthy = await ShadowFileService.IsShadowHealthyAsync(path, ShadowFileService.NKDB_SHADOW);

        // Assert
        Assert.False(healthy);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-SH-03: TryRestoreFromShadowAsync rewrites the magic from NKDB_SHADOW to NKDB_ORIGIN
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TryRestoreFromShadowAsync_RestoresNkdbShadowAndRewritesMagic()
    {
        // Arrange: generate a shadow carrying the NKDB_SHADOW magic
        var shadowPath = Path.Combine(_tempDir, ShadowFileService.GenerateBase64UrlName());
        var targetPath = Path.Combine(_tempDir, "NaimitsuVault.nkdb"); // fixed-name target
        CreateSqliteWithMagic(shadowPath, ShadowFileService.NKDB_SHADOW);

        // Act
        bool restored = await ShadowFileService.TryRestoreFromShadowAsync(
            shadowPath, targetPath, ShadowFileService.NKDB_ORIGIN);

        // Assert
        Assert.True(restored);
        Assert.True(File.Exists(targetPath));

        // After restoration, it must have been rewritten to NKDB_ORIGIN
        var magic = await ShadowFileService.ReadMagicAsync(targetPath);
        Assert.Equal(ShadowFileService.NKDB_ORIGIN, magic);

        // The shadow must not be deleted (TryRestore is read-only)
        Assert.True(File.Exists(shadowPath));
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static void CreateSqliteWithMagic(string path, uint magic)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {magic}; CREATE TABLE IF NOT EXISTS _dummy (id INTEGER PRIMARY KEY);";
        cmd.ExecuteNonQuery();
    }
}
