// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Helpers;
using NaimitsuVault.Repositories;

namespace NaimitsuVault.Tests;

/// <summary>
/// Unit tests for DatabaseRestoreValidator's 3 lines of defense.
/// 1st line: ValidateSqliteHeader (magic header inspection)
/// 2nd line: ValidateSqliteDatabaseAsync (SQLite connection — binary structure verification)
/// 3rd line: ValidateSqliteDatabaseAsync (schema consistency + PRAGMA integrity_check)
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class DbRestoreValidatorTests : IDisposable
{
    // Tracks a unique temp file per test and deletes it afterward
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _tempFiles)
        {
            TryDelete(f);
            TryDelete(f + "-wal");
            TryDelete(f + "-shm");
        }
    }

    private string TempPath()
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── 1st line of defense: ValidateSqliteHeader ─────────────────────────────────────

    [Fact]
    public void ValidateSqliteHeader_ValidSqliteFile_DoesNotThrow()
    {
        // Arrange — create a minimal SQLite file (the magic header gets written)
        var path = TempPath();
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // Act & Assert — no exception for a valid SQLite file
        DatabaseRestoreValidator.ValidateSqliteHeader(path);
    }

    [Fact]
    public void ValidateSqliteHeader_TextFile_ThrowsFormatException()
    {
        // Arrange — a text file (no SQLite header)
        var path = TempPath();
        File.WriteAllText(path, "This is not a SQLite database file.");

        // Act & Assert — FormatException from the 1st line of defense
        Assert.Throws<FormatException>(() => DatabaseRestoreValidator.ValidateSqliteHeader(path));
    }

    [Fact]
    public void ValidateSqliteHeader_EmptyFile_ThrowsFormatException()
    {
        // Arrange — a 0-byte file
        var path = TempPath();
        File.WriteAllBytes(path, []);

        // Act & Assert — FormatException due to insufficient size
        Assert.Throws<FormatException>(() => DatabaseRestoreValidator.ValidateSqliteHeader(path));
    }

    [Fact]
    public void ValidateSqliteHeader_ShortFile_ThrowsFormatException()
    {
        // Arrange — 15 bytes (fewer than 16 bytes)
        var path = TempPath();
        File.WriteAllBytes(path, new byte[15]);

        // Act & Assert
        Assert.Throws<FormatException>(() => DatabaseRestoreValidator.ValidateSqliteHeader(path));
    }

    [Fact]
    public void ValidateSqliteHeader_CorrectHeaderWrongByte16_ThrowsFormatException()
    {
        // Arrange — the first 15 bytes are correct, but byte 16 is wrong
        var path = TempPath();
        var header = new byte[16];
        "SQLite format 3"u8.CopyTo(header);
        header[15] = 0xFF; // not \0
        File.WriteAllBytes(path, header);

        // Act & Assert
        Assert.Throws<FormatException>(() => DatabaseRestoreValidator.ValidateSqliteHeader(path));
    }

    [Fact]
    public void ValidateSqliteHeader_RandomBinaryFile_ThrowsFormatException()
    {
        // Arrange — random binary (not a valid SQLite header)
        var path = TempPath();
        var random = new byte[1024];
        new Random(42).NextBytes(random);
        File.WriteAllBytes(path, random);

        // Act & Assert
        Assert.Throws<FormatException>(() => DatabaseRestoreValidator.ValidateSqliteHeader(path));
    }

    // ── 2nd/3rd lines of defense: ValidateSqliteDatabaseAsync ───────────────────────────

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_ValidNaimitsuDb_DoesNotThrow()
    {
        // Arrange — establish the schema via EF Core (including Categories / Secrets / VaultMetadata)
        var path = TempPath();
        var connStr = $"Data Source={path}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connStr)
            .Options;
        using (var ctx = new AppDbContext(options, new SessionGenerationGuardStub()))
            ctx.Database.EnsureCreated();
        SqliteConnection.ClearAllPools();

        // Act & Assert — must pass the 2nd and 3rd lines of defense
        await DatabaseRestoreValidator.ValidateSqliteDatabaseAsync(path, isUnifiedDb: false);
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_MissingAllTables_ThrowsFormatException()
    {
        // Arrange — an empty SQLite with no tables at all
        var path = TempPath();
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open(); // only generates the file (no CREATE TABLE)
        }
        SqliteConnection.ClearAllPools();

        // Act & Assert — FormatException due to missing tables
        await Assert.ThrowsAsync<FormatException>(
            () => DatabaseRestoreValidator.ValidateSqliteDatabaseAsync(path, isUnifiedDb: false));
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_PartialTables_ThrowsFormatException()
    {
        // Arrange — only Secrets (no VaultMetadata)
        var path = TempPath();
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Secrets (Id INTEGER PRIMARY KEY);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // Act & Assert — FormatException because not both required tables are present
        await Assert.ThrowsAsync<FormatException>(
            () => DatabaseRestoreValidator.ValidateSqliteDatabaseAsync(path, isUnifiedDb: false));
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_AllRequiredTablesPresent_DoesNotThrow()
    {
        // Arrange — manually create only the 2 required tables (Secrets / VaultMetadata)
        // (must pass even without SecretHistory / StoredFiles)
        var path = TempPath();
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Secrets       (Id INTEGER PRIMARY KEY);
                CREATE TABLE VaultMetadata (ConfigKey TEXT PRIMARY KEY);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // Act & Assert — passes once both required tables are present (absence of SecretHistory etc. is tolerated)
        await DatabaseRestoreValidator.ValidateSqliteDatabaseAsync(path, isUnifiedDb: false);
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_ValidUnifiedDb_WithIsUnifiedDbTrue_DoesNotThrow()
    {
        // Arrange — the unified DB (NaimitsuVault.nkdb) schema: UnifiedMetadata + VaultRegistries, no Secrets
        var path = TempPath();
        var connStr = $"Data Source={path}";
        var options = new DbContextOptionsBuilder<UnifiedDbContext>()
            .UseSqlite(connStr)
            .Options;
        using (var ctx = new UnifiedDbContext(options))
            ctx.Database.EnsureCreated();
        SqliteConnection.ClearAllPools();

        // Act & Assert — must pass when told this is the unified DB
        await DatabaseRestoreValidator.ValidateSqliteDatabaseAsync(path, isUnifiedDb: true);
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_ValidUnifiedDb_WithIsUnifiedDbFalse_ThrowsFormatException()
    {
        // Regression test: a genuinely healthy unified DB backup (UnifiedMetadata + VaultRegistries, no
        // Secrets table by design) must NOT be validated against the vault-DB table set. Before the
        // isUnifiedDb parameter existed, RestoreAllFromBackupAsync ran this exact check against
        // every valid NaimitsuVault.nkdb backup and always failed (tableCount=1, only UnifiedMetadata
        // matches 'Secrets','VaultMetadata'), aborting every restore-all run unconditionally.
        // isUnifiedDb has no default value specifically so a future call site can't silently repeat
        // this mistake by omission - it can still repeat it by passing the wrong literal, which this
        // test documents as the failure mode to watch for in review.
        var path = TempPath();
        var connStr = $"Data Source={path}";
        var options = new DbContextOptionsBuilder<UnifiedDbContext>()
            .UseSqlite(connStr)
            .Options;
        using (var ctx = new UnifiedDbContext(options))
            ctx.Database.EnsureCreated();
        SqliteConnection.ClearAllPools();

        // Act & Assert — the vault-DB table set (Secrets, VaultMetadata), which a unified DB never satisfies
        await Assert.ThrowsAsync<FormatException>(
            () => DatabaseRestoreValidator.ValidateSqliteDatabaseAsync(path, isUnifiedDb: false));
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_DifferentAppSqlite_ThrowsFormatException()
    {
        // Arrange — a SQLite file from a different app (no Secrets table)
        var path = TempPath();
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Users    (Id INTEGER PRIMARY KEY);
                CREATE TABLE Products (Id INTEGER PRIMARY KEY);
                CREATE TABLE Orders   (Id INTEGER PRIMARY KEY);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // Act & Assert — not a naimitsu schema → FormatException
        await Assert.ThrowsAsync<FormatException>(
            () => DatabaseRestoreValidator.ValidateSqliteDatabaseAsync(path, isUnifiedDb: false));
    }

    // ── 1st + 2nd lines of defense combined: binary corruption after the header passes ────────────────────────

    [Fact]
    public async Task ValidateSqliteHeader_ThenBinaryCorrupt_HeaderPassesButDbFails()
    {
        // Arrange — a correct SQLite header for only the first 16 bytes, garbage for the rest
        var path = TempPath();
        var bytes = new byte[4096];
        "SQLite format 3\0"u8.CopyTo(bytes);
        new Random(99).NextBytes(bytes.AsSpan(16)); // corrupt the rest with random data
        File.WriteAllBytes(path, bytes);

        // Act — the 1st line of defense passes (the header itself is correct)
        DatabaseRestoreValidator.ValidateSqliteHeader(path); // no throw

        // The 2nd line of defense (OpenAsync) or the 3rd line must raise SqliteException / FormatException
        await Assert.ThrowsAnyAsync<Exception>(
            () => DatabaseRestoreValidator.ValidateSqliteDatabaseAsync(path, isUnifiedDb: false));
    }
}
