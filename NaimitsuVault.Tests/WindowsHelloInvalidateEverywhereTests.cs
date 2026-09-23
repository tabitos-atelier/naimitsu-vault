// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;

namespace NaimitsuVault.Tests;

/// <summary>
/// Covers AuthService.InvalidateWindowsHelloEverywhereAsync - the pre-unlock recovery path used
/// when Windows Hello fails with a profile mismatch (moved to a different PC/Windows account).
///
/// Unlike DisableWindowsHelloAsync (which requires an authenticated session: ActiveVaultDbPath +
/// CurrentVaultDbNumber), this method must work with neither, since it runs from
/// UnlockViewModel before any vault has been entered. It also must clear every vault file's
/// VaultDEKHello row, not just the unified DB's KSharedHello - AcquireKSharedWithHelloAsync's
/// hasDek check only tests row existence, so a stale-but-still-present VaultDEKHello row in an
/// unrelated vault would resurface in the Hello vault picker and fail again with the same
/// mismatch once Hello is re-enabled for any vault sharing K_shared.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class WindowsHelloInvalidateEverywhereTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _tempFiles = [];

    public WindowsHelloInvalidateEverywhereTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"naimitsu_helloinval_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _tempFiles)
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(f + suffix); } catch { }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Utilities ─────────────────────────────────────────────────────

    /// <summary>Generates a valid vault file name using the "nkdb" prefix + 32 random bytes (same pattern as RestoreHelperTests).</summary>
    private static string MakeValidVaultFileName()
    {
        var raw = new byte[36];
        "nkdb"u8.CopyTo(raw.AsSpan(0, 4));
        System.Security.Cryptography.RandomNumberGenerator.Fill(raw.AsSpan(4));
        return Convert.ToBase64String(raw)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>Creates a minimal vault file on disk with a VaultMetadata table, optionally seeded with a VaultDEKHello row.</summary>
    private async Task<string> CreateVaultFileAsync(string fileName, bool withHelloRow)
    {
        var path = Path.Combine(_tempDir, fileName);
        _tempFiles.Add(path);
        var csb = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate };
        await using var conn = new SqliteConnection(csb.ToString());
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE VaultMetadata (ConfigKey INTEGER NOT NULL PRIMARY KEY, ConfigValue BLOB NOT NULL);" +
            // An unrelated row that must survive - proves the DELETE is scoped to VaultDEKHello only.
            "INSERT INTO VaultMetadata (ConfigKey, ConfigValue) VALUES (9999, X'AABB');";
        if (withHelloRow)
            cmd.CommandText += $"INSERT INTO VaultMetadata (ConfigKey, ConfigValue) VALUES ({VaultMetadataKey.VaultDEKHello}, X'CCDD');";
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        SqliteConnection.ClearAllPools();
        return path;
    }

    private static async Task<bool> HasConfigKeyAsync(string vaultPath, int configKey)
    {
        var csb = new SqliteConnectionStringBuilder { DataSource = vaultPath, Mode = SqliteOpenMode.ReadOnly };
        await using var conn = new SqliteConnection(csb.ToString());
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM VaultMetadata WHERE ConfigKey = {configKey}";
        var count = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        return count > 0;
    }

    /// <summary>
    /// InvalidateWindowsHelloEverywhereAsync only deletes rows by ConfigKey (never decrypts
    /// anything), so the vault-scoped EF Core factory must never be touched - an exploding stub
    /// proves this structurally, mirroring RestoreHelperTests.MakeFileOnlyAuth.
    /// </summary>
    private static AuthService MakeAuth(IDbContextFactory<UnifiedDbContext> unifiedFactory)
        => new(
            new FaultInjectionDbContextFactory(
                new InvalidOperationException("vault DB (EF Core) was accessed - expected raw SQLite path only")),
            unifiedFactory,
            new NullConnectionProvider(),
            new IdentityCryptoService(),
            new AppSession(),
            new StubAuditLogService(),
            null!,
            NullLogger<AuthService>.Instance);

    // ── Tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidateWindowsHelloEverywhereAsync_ClearsKSharedHelloAndAllVaultDEKHelloRows()
    {
        using var unifiedDb = TestUnifiedDb.Create();
        await using (var udb = unifiedDb.Factory.CreateDbContext())
        {
            udb.Metadata.Add(new UnifiedMetadata { ConfigKey = UnifiedMetadataKey.KSharedHello, ConfigValue = "stale"u8.ToArray() });
            await udb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var vault1 = await CreateVaultFileAsync(MakeValidVaultFileName(), withHelloRow: true);
        var vault2 = await CreateVaultFileAsync(MakeValidVaultFileName(), withHelloRow: true);

        var auth = MakeAuth(unifiedDb.Factory);
        await auth.InvalidateWindowsHelloEverywhereAsync(_tempDir);

        await using var verifyUdb = unifiedDb.Factory.CreateDbContext();
        Assert.False(await verifyUdb.Metadata.AnyAsync(
            s => s.ConfigKey == UnifiedMetadataKey.KSharedHello, TestContext.Current.CancellationToken));

        Assert.False(await HasConfigKeyAsync(vault1, VaultMetadataKey.VaultDEKHello));
        Assert.False(await HasConfigKeyAsync(vault2, VaultMetadataKey.VaultDEKHello));
        // The unrelated row in each vault file must survive - proves the DELETE is scoped correctly.
        Assert.True(await HasConfigKeyAsync(vault1, 9999));
        Assert.True(await HasConfigKeyAsync(vault2, 9999));
    }

    [Fact]
    public async Task InvalidateWindowsHelloEverywhereAsync_VaultWithoutHelloRow_LeavesItUnaffected()
    {
        using var unifiedDb = TestUnifiedDb.Create();
        var vaultNoHello = await CreateVaultFileAsync(MakeValidVaultFileName(), withHelloRow: false);

        var auth = MakeAuth(unifiedDb.Factory);
        await auth.InvalidateWindowsHelloEverywhereAsync(_tempDir);

        Assert.False(await HasConfigKeyAsync(vaultNoHello, VaultMetadataKey.VaultDEKHello));
        Assert.True(await HasConfigKeyAsync(vaultNoHello, 9999));
    }

    [Fact]
    public async Task InvalidateWindowsHelloEverywhereAsync_NoUnifiedKSharedHelloRow_DoesNotThrow()
    {
        using var unifiedDb = TestUnifiedDb.Create();
        var auth = MakeAuth(unifiedDb.Factory);

        // Nothing to clear anywhere - must complete without error (e.g. re-triggering the dialog twice).
        await auth.InvalidateWindowsHelloEverywhereAsync(_tempDir);
    }

    [Fact]
    public async Task InvalidateWindowsHelloEverywhereAsync_EmptyDataDir_DoesNotThrow()
    {
        using var unifiedDb = TestUnifiedDb.Create();
        var auth = MakeAuth(unifiedDb.Factory);

        await auth.InvalidateWindowsHelloEverywhereAsync(_tempDir);
    }
}
