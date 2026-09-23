// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// "DB corruption / backup scenarios" - all 7 cases plus derived cases (TC-DBC-08, TC-DBC-09).
/// Phase 1: in-memory SQLite verification (TC-DBC-01..TC-DBC-03)
/// Phase 2: file-based SQLite integration tests (TC-DBC-04..TC-DBC-06) + NUL-byte transparency check (TC-DBC-07)
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class DbCorruptionTests
{
    // ── TC-DBC-01 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SqlitePragmaIntegrityCheck_CleanDb_ReturnsOk()
    {
        // Arrange - an in-memory DB whose schema has already been established via TestDb.Create()
        using var db  = TestDb.Create();
        using var ctx = db.Factory.CreateDbContext();

        // Act
        var conn = ctx.Database.GetDbConnection();
        conn.Open();
        using var cmd   = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result      = cmd.ExecuteScalar()?.ToString();

        // Assert
        Assert.Equal("ok", result);
    }

    // ── TC-DBC-02 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RestoreWithDifferentDek_DecryptionFails_ThrowsCryptographicException()
    {
        // Arrange - attempt to decrypt with dekB data that was encrypted with dekA
        var svc   = new CryptoService(NullLogger<CryptoService>.Instance);
        var dekA  = new byte[32];
        var dekB  = new byte[32];
        RandomNumberGenerator.Fill(dekA);
        RandomNumberGenerator.Fill(dekB);

        var plain  = new byte[32];
        RandomNumberGenerator.Fill(plain);
        var cipher = svc.Encrypt(plain, dekA);
        var output = new byte[plain.Length];

        // Act & Assert - a different DEK causes a GCM auth tag mismatch
        Assert.ThrowsAny<CryptographicException>(() => svc.Decrypt(cipher, dekB, output));
    }

    // ── TC-DBC-03 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void WalMode_OnInMemoryDb_PragmaReturnsSupportedMode()
    {
        // Arrange
        using var db  = TestDb.Create();
        using var ctx = db.Factory.CreateDbContext();

        // Act
        var conn = ctx.Database.GetDbConnection();
        conn.Open();
        using var cmd   = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        var result      = cmd.ExecuteScalar()?.ToString();

        // Assert - an in-memory DB doesn't support WAL, so it returns "memory" (a file DB returns "wal")
        Assert.True(result == "wal" || result == "memory",
            $"Unexpected journal_mode: '{result}'");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Phase 2: file-based SQLite integration tests
    // ════════════════════════════════════════════════════════════════════════

    // ── TC-DBC-04 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void WalMode_OnFileBasedDb_ReturnsWal()
    {
        // Arrange - create an actual file-based SQLite DB as a temp file
        var tmpPath = Path.GetTempFileName();
        try
        {
            using var conn = new SqliteConnection($"Data Source={tmpPath}");
            conn.Open();

            // Act
            using var cmd   = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            var result      = cmd.ExecuteScalar()?.ToString();

            // Assert - WAL is enabled for file-based SQLite
            Assert.Equal("wal", result);
        }
        finally
        {
            // Explicitly release the pool, since the SQLite connection pool would otherwise keep holding the file lock
            SqliteConnection.ClearAllPools();
            File.Delete(tmpPath);
            // Also delete the WAL journal files
            var wal = tmpPath + "-wal";
            var shm = tmpPath + "-shm";
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);
        }
    }

    // ── TC-DBC-05 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IntegrityCheck_OnFileBasedDb_ReturnsOk()
    {
        // Arrange - establish the EF Core schema on a file-based DB and run integrity_check
        var tmpPath = Path.GetTempFileName();
        try
        {
            var connStr = $"Data Source={tmpPath}";
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connStr)
                .Options;
            using (var ctx = new AppDbContext(options, new SessionGenerationGuardStub()))
                ctx.Database.EnsureCreated();

            // Act
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            using var cmd   = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result      = cmd.ExecuteScalar()?.ToString();

            // Assert
            Assert.Equal("ok", result);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(tmpPath);
        }
    }

    // ── TC-DBC-06 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Restore_FileCopy_ReplacesDbWithBackup()
    {
        // Arrange - prepare the original DB and its "backup" as separate files (direct SQLite manipulation)
        var dbPath      = Path.GetTempFileName();
        var backupPath  = Path.GetTempFileName();
        var tmpRestPath = dbPath + ".restore_tmp";
        try
        {
            // Write "original" into the original DB
            WriteMarkerDb(dbPath,     "original");
            WriteMarkerDb(backupPath, "from_backup");

            // Act - release all pool locks before manipulating files
            SqliteConnection.ClearAllPools();
            File.Copy(backupPath, tmpRestPath, overwrite: true);
            File.Delete(dbPath);
            File.Move(tmpRestPath, dbPath);

            // Assert - the backup's value can be read after the restore
            var val = ReadMarkerDb(dbPath);
            Assert.Equal("from_backup", val);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { dbPath, backupPath, tmpRestPath })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // TC-DBC-08: 30-day retention period boundary-value defense test
    // ════════════════════════════════════════════════════════════════════════

    // ── TC-DBC-08 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [30-day retention period, boundary-value defense]
    /// Proves at the boundary that CleanupDeletedSecretsAsync physically deletes only records
    /// where "more than 30 days have passed since deletion", while records from "29 days ago"
    /// are strictly retained.
    ///
    /// Preliminary analysis results:
    /// (1) AppDbContext has no HasQueryFilter -> IgnoreQueryFilters() is unnecessary
    /// (2) There is no IsDeleted flag -> the only condition is DeletedAt != null
    /// </summary>
    [Fact]
    public async Task CleanupDeletedSecrets_RetentionPeriod30Days_BoundaryValidation()
    {
        // Arrange - establish a uniquely named in-memory DB space for the test
        using var db = TestDb.Create();
        await using var ctx = db.Factory.CreateDbContext();
        var now = DateTime.UtcNow;

        // (1) An entry deleted 31 days ago (more than 30 days: a cleanup target per spec)
        var secretExpired = new Secret { Title = new byte[28], CreatedAt = now, UpdatedAt = now, DeletedAt = now.AddDays(-31) };
        // (2) An entry deleted 29 days ago (under 30 days: a retention target per spec)
        var secretAlive   = new Secret { Title = new byte[28], CreatedAt = now, UpdatedAt = now, DeletedAt = now.AddDays(-29) };

        ctx.Secrets.AddRange(secretExpired, secretAlive);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act - run the actual cleanup process
        var initializer = new DatabaseInitializer(
            db.Factory,
            new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(),
            NullLogger<DatabaseInitializer>.Instance);
        await initializer.CleanupDeletedSecretsAsync();

        // Assert - no HasQueryFilter -> IgnoreQueryFilters is unnecessary; check the raw data with a normal ToListAsync
        await using var verifyCtx = db.Factory.CreateDbContext();
        var remaining = await verifyCtx.Secrets.ToListAsync(TestContext.Current.CancellationToken);

        // The 31-days-ago entry must be reliably physically erased, and the 29-days-ago entry must absolutely remain
        Assert.DoesNotContain(remaining, s => s.Id == secretExpired.Id);
        Assert.Contains(remaining, s => s.Id == secretAlive.Id);
    }

    // TC-DBC-06 helper: directly creates and reads a simple test-only key-value table
    private static void WriteMarkerDb(string path, string value)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var create = conn.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS marker (val TEXT NOT NULL);";
        create.ExecuteNonQuery();
        using var insert = conn.CreateCommand();
        insert.CommandText = $"INSERT INTO marker VALUES ('{value}');";
        insert.ExecuteNonQuery();
    }

    private static string? ReadMarkerDb(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd   = conn.CreateCommand();
        cmd.CommandText = "SELECT val FROM marker LIMIT 1;";
        return cmd.ExecuteScalar()?.ToString();
    }

    // ════════════════════════════════════════════════════════════════════════
    // TC-DBC-07: DB storage behavior for NUL bytes
    // ════════════════════════════════════════════════════════════════════════

    // ── TC-DBC-07 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// NaimitsuVault's Secret table stores every string field as byte[] (BLOB) - the raw
    /// post-encryption byte sequence. Encrypting a JSON string that contains a NUL byte still
    /// stores it as a BLOB, so the classic TEXT-column NUL-byte problem never occurs.
    ///
    /// The metadata tables' ConfigValue column is TEXT-typed, but since it never holds user-controlled data (only
    /// fixed values like "Standard"/"ja"), there is no path for a NUL byte to enter it.
    /// </summary>
    [Fact]
    public async Task AllSecretStringFieldsAreBlob_NulByteIsTransparent()
    {
        // Arrange - encrypt plaintext containing a NUL byte and store it in a Secret
        using var db     = TestDb.Create();
        var svc          = new CryptoService(NullLogger<CryptoService>.Instance);
        var rawKey       = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(rawKey);
        var dek          = new DekScope(rawKey, 32);

        var nullPayload  = new byte[] { (byte)'a', 0x00, (byte)'b' }; // a\0b
        var encrypted    = svc.Encrypt(nullPayload, dek.Span);        // BLOB (NUL is irrelevant here)

        await using var ctx = db.Factory.CreateDbContext();
        ctx.Secrets.Add(new Secret
        {
            Title    = encrypted,   // stored as a byte[] BLOB
            Notes    = encrypted,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act - reload
        var loaded = await ctx.Secrets.FindAsync(
            [ctx.Secrets.Local.First().Id], TestContext.Current.CancellationToken);

        // Assert - the BLOB fully preserves the NUL byte
        Assert.NotNull(loaded);
        Assert.Equal(encrypted, loaded.Title);

        // Assert - the NUL byte is preserved even after decryption
        var decrypted = new byte[nullPayload.Length];
        svc.Decrypt(loaded.Title!, dek.Span, decrypted);
        Assert.Equal(nullPayload, decrypted);
        // -> BLOB-based encrypted storage has no NUL-byte problem
    }

    // ════════════════════════════════════════════════════════════════════════
    // TC-DBC-09: whether DatabaseInitializer actually sets journal_mode=WAL
    // ════════════════════════════════════════════════════════════════════════

    // ── TC-DBC-09 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-DBC-03/TC-DBC-04 only issue PRAGMA journal_mode=WAL directly on a raw SqliteConnection
    /// from the test side, verifying SQLite's own behavior - they never exercise
    /// DatabaseInitializer.InitializeVaultDbAsync's initialization code path (which internally
    /// calls ApplyVaultPragmasAsync -> SetWalAndAutoVacuumAsync). This test actually runs the
    /// production initialization code path and directly confirms that the resulting file-based
    /// vault DB ends up with journal_mode=WAL.
    /// </summary>
    [Fact]
    public async Task InitializeVaultDbAsync_NewFileDb_JournalModeIsWal()
    {
        // Arrange
        var tmpPath = Path.GetTempFileName();
        try
        {
            var initializer = new DatabaseInitializer(
                factory: null!,
                unifiedFactory: new ExplodingUnifiedDbContextFactory(),
                sessionGuard: new SessionGenerationGuardStub(),
                logger: NullLogger<DatabaseInitializer>.Instance);

            // Act - actually run the production code path for creating a new vault DB
            await initializer.InitializeVaultDbAsync(tmpPath);

            using var conn = new SqliteConnection($"Data Source={tmpPath}");
            conn.Open();
            using var cmd   = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            var result      = cmd.ExecuteScalar()?.ToString();

            // Assert
            Assert.Equal("wal", result);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(tmpPath);
            var wal = tmpPath + "-wal";
            var shm = tmpPath + "-shm";
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);
        }
    }
}
