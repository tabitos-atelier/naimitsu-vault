// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Helpers;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;

namespace NaimitsuVault.Tests;

/// <summary>
/// Helper methods for AuthService.Restore.cs.
///
/// Covers ScanVaultCandidates file name filtering, the AuthenticateBackupNkdbAsync backup
/// authentication gate, ValidateBackupFilesAsync / HasExistingLocalData (Rev.22 guards), and
/// RestoreAllFromBackupAsync's whole-tree overwrite copy (including the Windows Hello
/// invalidation step added in Rev.22).
///
/// Rev.22 removed the individual/selective restore path (Mode 2: BuildVaultMatrixAsync,
/// ReconstructUnifiedDbAsync, HasLocalKSharedConflictAsync, VerifyPasswordAgainstVaultAsync) -
/// its tests were removed along with the implementation.
/// </summary>
[Collection("SequentialLocale")]
public sealed class RestoreHelperTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _tempFiles = [];

    public RestoreHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"naimitsu_ur_{Guid.NewGuid():N}");
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

    /// <summary>Generates a valid vault file name using the "nkdb" prefix + 32 random bytes.</summary>
    private static string MakeValidVaultFileName()
    {
        var raw = new byte[36];
        "nkdb"u8.CopyTo(raw.AsSpan(0, 4));
        RandomNumberGenerator.Fill(raw.AsSpan(4));
        return Convert.ToBase64String(raw)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private string CreateTempFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, []);
        _tempFiles.Add(path);
        return path;
    }

    private async Task<string> CreateMinimalNkdbAsync(string fileName, int[]? dbNumbers = null)
    {
        var path = Path.Combine(_tempDir, fileName);
        _tempFiles.Add(path);
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode       = SqliteOpenMode.ReadWriteCreate,
        };
        await using var conn = new SqliteConnection(csb.ToString());
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        // Real backups are WAL-mode (the app runs every DB in WAL mode), so test fixtures must be
        // too - otherwise WAL-only side effects (like the -wal/-shm sidecar file bug) go unnoticed.
        cmd.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "CREATE TABLE IF NOT EXISTS UnifiedMetadata " +
            "  (ConfigKey INTEGER NOT NULL PRIMARY KEY, ConfigValue BLOB NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS VaultRegistries " +
            "  (DbNumber INTEGER NOT NULL PRIMARY KEY, LastAccessedAt INTEGER, EncryptedPayload BLOB);";
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        if (dbNumbers != null)
        {
            foreach (var n in dbNumbers)
            {
                await using var ins = conn.CreateCommand();
                ins.CommandText =
                    "INSERT INTO VaultRegistries (DbNumber, LastAccessedAt, EncryptedPayload) " +
                    "VALUES (@n, NULL, X'AABB')";
                ins.Parameters.AddWithValue("@n", n);
                await ins.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        // Checkpoint so the file is left in the same clean, single-file state a real backup
        // (SqliteConnection.BackupDatabase()) would produce - not mid-WAL with a live -wal file.
        await using (var ckpt = conn.CreateCommand())
        {
            ckpt.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await ckpt.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    /// <summary>
    /// Creates a minimal vault DB file that has only VaultMetadata[Auth=0x1001] and an empty
    /// Secrets table (required by DatabaseRestoreValidator's schema check).
    /// </summary>
    private async Task<string> CreateSeededVaultFileAsync(string dir, string fileName, string password)
    {
        var path = Path.Combine(dir, fileName);
        _tempFiles.Add(path);

        var saltPwd  = RandomNumberGenerator.GetBytes(32);
        var vaultKek = new byte[32];
        using (var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt                = saltPwd,
            DegreeOfParallelism = 4,
            MemorySize          = 65536,
            Iterations          = 3,
        })
        {
            argon2.GetBytes(32).AsSpan().CopyTo(vaultKek);
        }

        var vaultDek    = RandomNumberGenerator.GetBytes(32);
        var crypto      = new CryptoService(NullLogger<CryptoService>.Instance);
        var wrappedDek  = crypto.Encrypt(vaultDek, vaultKek);
        var authJson    =
            $"{{\"Salt\":\"{Convert.ToBase64String(saltPwd)}\",\"WrappedDek\":\"{Convert.ToBase64String(wrappedDek)}\"}}";

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode       = SqliteOpenMode.ReadWriteCreate,
        };
        await using var conn = new SqliteConnection(csb.ToString());
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        // WAL mode to match real vault files (see CreateMinimalNkdbAsync's comment).
        cmd.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "CREATE TABLE VaultMetadata (ConfigKey INTEGER NOT NULL PRIMARY KEY, ConfigValue BLOB NOT NULL);" +
            "CREATE TABLE Secrets (Id INTEGER PRIMARY KEY);" +
            $"INSERT INTO VaultMetadata (ConfigKey, ConfigValue) VALUES ({VaultMetadataKey.Auth}, @auth)";
        cmd.Parameters.AddWithValue("@auth", Encoding.UTF8.GetBytes(authJson));
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await using (var ckpt = conn.CreateCommand())
        {
            ckpt.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await ckpt.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    /// <summary>
    /// Builds the 96-byte BLOB for KSharedSlot_N (identical to the internal format in AuthService.MultiVault.cs).
    /// coarse_hash[4] + unified_salt[32] + AES-256-GCM(K_shared, K_master)[60].
    /// </summary>
    private static byte[] BuildKSharedSlotBlob(string password, byte[] unifiedSalt, byte[] kShared, CryptoService crypto)
    {
        var kMaster = new byte[32];
        AuthService.DeriveArgon2id(password, unifiedSalt, kMaster);
        var wrapData = crypto.Encrypt(kShared, kMaster);
        var coarse = SHA256.HashData(Encoding.UTF8.GetBytes(password[..Math.Min(3, password.Length)]))[..4];

        var blob = new byte[96];
        coarse.CopyTo(blob, 0);
        unifiedSalt.CopyTo(blob, 4);
        wrapData.CopyTo(blob, 36);
        return blob;
    }

    /// <summary>Builds a backup .nkdb whose UnifiedMetadata carries KSharedSlot_N entries for the given slots.</summary>
    private async Task<string> CreateNkdbWithKSharedSlotsAsync(
        string fileName, params (int SlotConfigKey, string Password, byte[] KShared)[] slots)
    {
        var path = Path.Combine(_tempDir, fileName);
        _tempFiles.Add(path);
        var crypto = new CryptoService(NullLogger<CryptoService>.Instance);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode       = SqliteOpenMode.ReadWriteCreate,
        };
        await using var conn = new SqliteConnection(csb.ToString());
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        // WAL mode to match real backups (see CreateMinimalNkdbAsync's comment).
        cmd.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "CREATE TABLE UnifiedMetadata (ConfigKey INTEGER NOT NULL PRIMARY KEY, ConfigValue BLOB NOT NULL);" +
            "CREATE TABLE VaultRegistries (DbNumber INTEGER NOT NULL PRIMARY KEY, LastAccessedAt INTEGER, EncryptedPayload BLOB);";
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        foreach (var (slotConfigKey, password, kShared) in slots)
        {
            var unifiedSalt = RandomNumberGenerator.GetBytes(32);
            var blob = BuildKSharedSlotBlob(password, unifiedSalt, kShared, crypto);
            await using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO UnifiedMetadata (ConfigKey, ConfigValue) VALUES (@k, @v)";
            ins.Parameters.AddWithValue("@k", slotConfigKey);
            ins.Parameters.AddWithValue("@v", blob);
            await ins.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var ckpt = conn.CreateCommand())
        {
            ckpt.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await ckpt.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    /// <summary>
    /// AuthenticateBackupNkdbAsync / ValidateBackupFilesAsync / HasExistingLocalData never touch
    /// the vault DB via EF Core (raw SQLite only) or the local unified DB at all, so both factories
    /// are made into exploding stubs to structurally prove that.
    /// </summary>
    private static AuthService MakeNoDbAuth()
        => new(
            new FaultInjectionDbContextFactory(
                new InvalidOperationException("vault DB (EF Core) was accessed - expected direct SQLite path only")),
            new ExplodingUnifiedDbContextFactory(),
            new NullConnectionProvider(),
            new CryptoService(NullLogger<CryptoService>.Instance),
            new AppSession(),
            new StubAuditLogService(),
            null!,
            NullLogger<AuthService>.Instance);

    /// <summary>
    /// RestoreAllFromBackupAsync (Rev.22) invalidates Windows Hello credentials after copying,
    /// which touches the local unified DB via EF Core, so a real DB is required. The vault DB
    /// factory remains an exploding stub, continuing to prove restore never touches vault files
    /// via anything other than direct file copy / raw SQLite.
    /// </summary>
    private static AuthService MakeFileAndLocalUnifiedDbAuth(IDbContextFactory<UnifiedDbContext> unifiedFactory)
        => new(
            new FaultInjectionDbContextFactory(
                new InvalidOperationException("vault DB (EF Core) was accessed - expected direct SQLite path only")),
            unifiedFactory,
            new NullConnectionProvider(),
            new CryptoService(NullLogger<CryptoService>.Instance),
            new AppSession(),
            new StubAuditLogService(),
            null!,
            NullLogger<AuthService>.Instance);

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // ScanVaultCandidates
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    // A file with a 48-character Base64Url name and the "nkdb" prefix is included as a candidate.

    [Fact]
    public void ScanVaultCandidates_ValidVaultFileName_IsIncluded()
    {
        var name = MakeValidVaultFileName();
        Assert.Equal(48, name.Length);
        CreateTempFile(name);

        var result = AuthService.ScanVaultCandidates(_tempDir);

        Assert.Single(result);
        Assert.Equal(name, Path.GetFileName(result[0]));
    }

    // Even at 48 characters, a file without the "nkdb" prefix is excluded.

    [Fact]
    public void ScanVaultCandidates_WrongPrefix_IsExcluded()
    {
        // "xxxx" prefix (4B) + 32B random -> "xxxx" != "nkdb"
        var raw = new byte[36];
        Encoding.ASCII.GetBytes("xxxx").CopyTo(raw, 0);
        RandomNumberGenerator.Fill(raw.AsSpan(4));
        var name = Convert.ToBase64String(raw)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(48, name.Length);
        CreateTempFile(name);

        var result = AuthService.ScanVaultCandidates(_tempDir);

        Assert.Empty(result);
    }

    // Files with 47 or 49 characters are excluded (length filter).

    [Fact]
    public void ScanVaultCandidates_WrongLength_IsExcluded()
    {
        CreateTempFile(new string('a', 47));
        CreateTempFile(new string('b', 49));

        var result = AuthService.ScanVaultCandidates(_tempDir);

        Assert.Empty(result);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // AuthenticateBackupNkdbAsync (Rev.22: the sole gate before a restore is allowed to proceed)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    // The correct password for any one of the (up to 3) KSharedSlot_N entries recovers K_shared,
    // regardless of which slot number it lives in.

    [Fact]
    public async Task AuthenticateBackupNkdbAsync_CorrectPasswordInSecondSlot_ReturnsMatchingKShared()
    {
        const string password = "correct-vault-2-password";
        var kShared = RandomNumberGenerator.GetBytes(32);
        var nkdbPath = await CreateNkdbWithKSharedSlotsAsync(
            "backup.nkdb",
            (UnifiedMetadataKey.KSharedSlot_1, "unrelated-password-1", RandomNumberGenerator.GetBytes(32)),
            (UnifiedMetadataKey.KSharedSlot_2, password, kShared));

        var auth = MakeNoDbAuth();
        var result = await auth.AuthenticateBackupNkdbAsync(nkdbPath, password.ToCharArray());

        Assert.NotNull(result);
        Assert.Equal(kShared, result);

        CryptographicOperations.ZeroMemory(kShared);
        CryptographicOperations.ZeroMemory(result);
    }

    // A password that matches none of the backup's KSharedSlot entries returns null.

    [Fact]
    public async Task AuthenticateBackupNkdbAsync_WrongPassword_ReturnsNull()
    {
        var nkdbPath = await CreateNkdbWithKSharedSlotsAsync(
            "backup.nkdb",
            (UnifiedMetadataKey.KSharedSlot_1, "the-real-password", RandomNumberGenerator.GetBytes(32)));

        var auth = MakeNoDbAuth();
        var result = await auth.AuthenticateBackupNkdbAsync(nkdbPath, "totally-wrong-password".ToCharArray());

        Assert.Null(result);
    }

    // A backup with no KSharedSlot entries at all (e.g. no vault was ever created) never authenticates.

    [Fact]
    public async Task AuthenticateBackupNkdbAsync_NoSlotsInBackup_ReturnsNull()
    {
        var nkdbPath = await CreateMinimalNkdbAsync("backup.nkdb");

        var auth = MakeNoDbAuth();
        var result = await auth.AuthenticateBackupNkdbAsync(nkdbPath, "any-password".ToCharArray());

        Assert.Null(result);
    }

    // Regression test for a real-world bug: the backup's own .nkdb runs in WAL mode (like every DB
    // in this app). Opening it with plain Mode=ReadOnly still makes SQLite create -wal/-shm sidecar
    // files next to it the instant a connection opens, and closing the connection (even via
    // SqliteConnection.ClearAllPools()) does not remove them - only a checkpoint (a write
    // operation) does. Left unfixed, every password attempt during the backup authentication gate
    // silently litters the user's backup folder with junk files. The fix is the immutable=1 URI
    // parameter in AuthenticateBackupNkdbAsync's connection string.

    [Fact]
    public async Task AuthenticateBackupNkdbAsync_WalModeBackup_LeavesNoSidecarFilesInBackupFolder()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        Directory.CreateDirectory(backupDir);

        const string password = "auth-password-1";
        var kShared  = RandomNumberGenerator.GetBytes(32);
        var nkdbPath = await CreateNkdbWithKSharedSlotsAsync(
            Path.Combine("backup", "backup.nkdb"),
            (UnifiedMetadataKey.KSharedSlot_1, password, kShared));

        var filesBefore = Directory.GetFiles(backupDir).Select(Path.GetFileName).OrderBy(x => x).ToList();

        var auth = MakeNoDbAuth();
        var result = await auth.AuthenticateBackupNkdbAsync(nkdbPath, password.ToCharArray());
        SqliteConnection.ClearAllPools();

        Assert.NotNull(result);
        var filesAfter = Directory.GetFiles(backupDir).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(filesBefore, filesAfter);

        CryptographicOperations.ZeroMemory(kShared);
        CryptographicOperations.ZeroMemory(result);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // ValidateBackupFilesAsync (Rev.22: extracted so it can run before the auth gate)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    [Fact]
    public async Task ValidateBackupFilesAsync_AllFilesValid_DoesNotThrow()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        Directory.CreateDirectory(backupDir);

        var fileName = MakeValidVaultFileName();
        await CreateSeededVaultFileAsync(backupDir, fileName, "password-A");
        var nkdbPath = await CreateMinimalNkdbAsync("backup.nkdb");

        var auth = MakeNoDbAuth();

        await auth.ValidateBackupFilesAsync(nkdbPath, backupDir);
        // No exception thrown -> pass
    }

    // Same regression as AuthenticateBackupNkdbAsync_WalModeBackup_LeavesNoSidecarFilesInBackupFolder,
    // but for the validation gate (DatabaseRestoreValidator.ValidateSqliteDatabaseAsync), which runs
    // even earlier in the pipeline and touches every backup file (vault files + the nkdb).

    [Fact]
    public async Task ValidateBackupFilesAsync_WalModeBackupFiles_LeavesNoSidecarFilesInBackupFolder()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        Directory.CreateDirectory(backupDir);

        var fileName = MakeValidVaultFileName();
        await CreateSeededVaultFileAsync(backupDir, fileName, "password-A");
        var nkdbPath = await CreateMinimalNkdbAsync(Path.Combine("backup", "backup.nkdb"));

        var filesBefore = Directory.GetFiles(backupDir).Select(Path.GetFileName).OrderBy(x => x).ToList();

        var auth = MakeNoDbAuth();
        await auth.ValidateBackupFilesAsync(nkdbPath, backupDir);
        SqliteConnection.ClearAllPools();

        var filesAfter = Directory.GetFiles(backupDir).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(filesBefore, filesAfter);
    }

    [Fact]
    public async Task ValidateBackupFilesAsync_CorruptVaultFile_ThrowsRestoreValidationException()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        Directory.CreateDirectory(backupDir);

        var fileName = MakeValidVaultFileName();
        var badPath  = Path.Combine(backupDir, fileName);
        await File.WriteAllBytesAsync(badPath, "not a sqlite file"u8.ToArray(), TestContext.Current.CancellationToken);
        _tempFiles.Add(badPath);
        var nkdbPath = await CreateMinimalNkdbAsync("backup.nkdb");

        var auth = MakeNoDbAuth();

        var ex = await Assert.ThrowsAsync<RestoreValidationException>(
            () => auth.ValidateBackupFilesAsync(nkdbPath, backupDir));
        Assert.Equal(fileName, ex.FileName);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // HasExistingLocalData (Rev.22: first guard's trigger condition)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    [Fact]
    public void HasExistingLocalData_MissingDirectory_ReturnsFalse()
    {
        var auth = MakeNoDbAuth();

        Assert.False(auth.HasExistingLocalData(Path.Combine(_tempDir, "nonexistent")));
    }

    [Fact]
    public void HasExistingLocalData_EmptyDirectory_ReturnsFalse()
    {
        var localDir = Path.Combine(_tempDir, "local");
        Directory.CreateDirectory(localDir);
        var auth = MakeNoDbAuth();

        Assert.False(auth.HasExistingLocalData(localDir));
    }

    [Fact]
    public void HasExistingLocalData_FilesPresent_ReturnsTrue()
    {
        var localDir = Path.Combine(_tempDir, "local");
        Directory.CreateDirectory(localDir);
        var seedPath = Path.Combine(localDir, "NaimitsuVault.nkdb");
        File.WriteAllBytes(seedPath, [1, 2, 3]);
        _tempFiles.Add(seedPath);
        var auth = MakeNoDbAuth();

        Assert.True(auth.HasExistingLocalData(localDir));
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // RestoreAllFromBackupAsync : whole-tree overwrite copy, no password required
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    // Copies multiple vault files plus the unified database wholesale, without password verification.

    [Fact]
    public async Task RestoreAllFromBackupAsync_CopiesAllVaultFilesAndNkdb_WithoutAnyPasswordCheck()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        var localDir  = Path.Combine(_tempDir, "local");
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(localDir);

        // Passwords differ between the two (confirms this is irrelevant since the copy doesn't verify)
        var fileNameA = MakeValidVaultFileName();
        var fileNameB = MakeValidVaultFileName();
        var backupPathA = await CreateSeededVaultFileAsync(backupDir, fileNameA, "password-A");
        var backupPathB = await CreateSeededVaultFileAsync(backupDir, fileNameB, "password-B");
        var nkdbPath = await CreateMinimalNkdbAsync("backup.nkdb", dbNumbers: [1, 2]);

        using var unifiedDb = TestUnifiedDb.Create();
        var auth = MakeFileAndLocalUnifiedDbAuth(unifiedDb.Factory);
        var (restoredCount, nkdbCopied) = await auth.RestoreAllFromBackupAsync(nkdbPath, backupDir, localDir);

        Assert.Equal(3, restoredCount); // 2 vault files + 1 nkdb
        Assert.True(nkdbCopied);
        Assert.Equal(File.ReadAllBytes(backupPathA), File.ReadAllBytes(Path.Combine(localDir, fileNameA)));
        Assert.Equal(File.ReadAllBytes(backupPathB), File.ReadAllBytes(Path.Combine(localDir, fileNameB)));
        Assert.True(File.Exists(Path.Combine(localDir, "NaimitsuVault.nkdb")));
    }

    // When the unified database file does not exist, only the vault files are copied.

    [Fact]
    public async Task RestoreAllFromBackupAsync_NkdbMissing_CopiesOnlyVaultFiles()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        var localDir  = Path.Combine(_tempDir, "local");
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(localDir);

        var fileName = MakeValidVaultFileName();
        await CreateSeededVaultFileAsync(backupDir, fileName, "password-A");

        using var unifiedDb = TestUnifiedDb.Create();
        var auth = MakeFileAndLocalUnifiedDbAuth(unifiedDb.Factory);
        var (restoredCount, nkdbCopied) = await auth.RestoreAllFromBackupAsync(
            Path.Combine(backupDir, "nonexistent.nkdb"), backupDir, localDir);

        Assert.Equal(1, restoredCount);
        Assert.False(nkdbCopied);
        Assert.True(File.Exists(Path.Combine(localDir, fileName)));
        Assert.False(File.Exists(Path.Combine(localDir, "NaimitsuVault.nkdb")));
    }

    // Pre-existing local files are archived to data/archived_yyyyMMdd_HHmmss/ (untouched, original
    // content) before the restore overwrites them, as a safety net against a mistaken restore.

    [Fact]
    public async Task RestoreAllFromBackupAsync_ExistingLocalFiles_ArchivesThemBeforeOverwrite()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        var localDir  = Path.Combine(_tempDir, "local");
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(localDir);

        // Pre-existing local state that must be preserved: an unrelated vault file plus the unified DB.
        var oldFileName = MakeValidVaultFileName();
        var oldContent  = "old-local-vault-content"u8.ToArray();
        var oldLocalPath = Path.Combine(localDir, oldFileName);
        await File.WriteAllBytesAsync(oldLocalPath, oldContent, TestContext.Current.CancellationToken);
        _tempFiles.Add(oldLocalPath);
        var oldNkdbContent = "old-local-nkdb-content"u8.ToArray();
        var oldLocalNkdbPath = Path.Combine(localDir, "NaimitsuVault.nkdb");
        await File.WriteAllBytesAsync(oldLocalNkdbPath, oldNkdbContent, TestContext.Current.CancellationToken);
        _tempFiles.Add(oldLocalNkdbPath);

        var newFileName = MakeValidVaultFileName();
        await CreateSeededVaultFileAsync(backupDir, newFileName, "password-A");
        var nkdbPath = await CreateMinimalNkdbAsync("backup.nkdb", dbNumbers: [1]);

        using var unifiedDb = TestUnifiedDb.Create();
        var auth = MakeFileAndLocalUnifiedDbAuth(unifiedDb.Factory);
        await auth.RestoreAllFromBackupAsync(nkdbPath, backupDir, localDir);

        // Discovered by enumeration (not a recomputed DateTime.Now string) so the test can't race
        // against the source's own DateTime.Now call across a second boundary.
        var archiveDir = Assert.Single(Directory.GetDirectories(localDir, "archived_*"));
        Assert.Equal(oldContent, File.ReadAllBytes(Path.Combine(archiveDir, oldFileName)));
        Assert.Equal(oldNkdbContent, File.ReadAllBytes(Path.Combine(archiveDir, "NaimitsuVault.nkdb")));

        // NaimitsuVault.nkdb has a fixed filename shared with the backup, so it's overwritten by
        // the restore itself. The unrelated vault file (a different random filename, absent from
        // the backup) is outside the copy set and is left alone on the live side - its
        // preservation comes entirely from the archive copy above.
        Assert.NotEqual(oldNkdbContent, File.ReadAllBytes(oldLocalNkdbPath));
        Assert.Equal(oldContent, File.ReadAllBytes(oldLocalPath));
    }

    // A fresh/empty local data/ (the common new-PC-migration case) has nothing to archive, so no
    // archived_* folder should be created.

    [Fact]
    public async Task RestoreAllFromBackupAsync_EmptyLocalDir_CreatesNoArchiveFolder()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        var localDir  = Path.Combine(_tempDir, "local");
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(localDir);

        var fileName = MakeValidVaultFileName();
        await CreateSeededVaultFileAsync(backupDir, fileName, "password-A");
        var nkdbPath = await CreateMinimalNkdbAsync("backup.nkdb", dbNumbers: [1]);

        using var unifiedDb = TestUnifiedDb.Create();
        var auth = MakeFileAndLocalUnifiedDbAuth(unifiedDb.Factory);
        await auth.RestoreAllFromBackupAsync(nkdbPath, backupDir, localDir);

        Assert.Empty(Directory.GetDirectories(localDir));
    }

    // Restoring twice in quick succession must not let the second archive clobber the first, even
    // when both calls land within the same wall-clock second (the common case for two back-to-back
    // calls in a fast test) and would otherwise compute the same archived_yyyyMMdd_HHmmss name.

    [Fact]
    public async Task RestoreAllFromBackupAsync_SecondRestoreImmediatelyAfterFirst_CreatesDistinctArchiveFolders()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        var localDir  = Path.Combine(_tempDir, "local");
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(localDir);

        // Seed local state so the first restore has something to archive.
        var seedPath = Path.Combine(localDir, "NaimitsuVault.nkdb");
        await File.WriteAllBytesAsync(seedPath, "seed"u8.ToArray(), TestContext.Current.CancellationToken);
        _tempFiles.Add(seedPath);

        var fileName = MakeValidVaultFileName();
        await CreateSeededVaultFileAsync(backupDir, fileName, "password-A");
        var nkdbPath = await CreateMinimalNkdbAsync("backup.nkdb", dbNumbers: [1]);

        using var unifiedDb = TestUnifiedDb.Create();
        var auth = MakeFileAndLocalUnifiedDbAuth(unifiedDb.Factory);

        // First restore archives the seeded state.
        await auth.RestoreAllFromBackupAsync(nkdbPath, backupDir, localDir);
        var firstArchiveDirs = Directory.GetDirectories(localDir, "archived_*");
        Assert.Single(firstArchiveDirs);

        // Second restore now has the first restore's result to archive too. Whether or not this
        // lands in the same second as the first call, it must produce a second, distinct folder
        // rather than overwriting/erroring on a name collision.
        await auth.RestoreAllFromBackupAsync(nkdbPath, backupDir, localDir);
        var secondArchiveDirs = Directory.GetDirectories(localDir, "archived_*");

        Assert.Equal(2, secondArchiveDirs.Length);
        Assert.Contains(firstArchiveDirs[0], secondArchiveDirs);
    }

    // Archived folders older than the newest 3 generations are deleted unconditionally (no
    // confirmation, no audit log - archived_* is an internal safety net, not user-authored data).

    [Fact]
    public async Task RestoreAllFromBackupAsync_MoreThanThreeExistingArchiveFolders_KeepsOnlyNewestThree()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        var localDir  = Path.Combine(_tempDir, "local");
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(localDir);

        // Pre-seed 4 already-existing archived_* folders, oldest to newest.
        var preExisting = new[]
        {
            "archived_20260101_000001",
            "archived_20260102_000001",
            "archived_20260103_000001",
            "archived_20260104_000001",
        };
        foreach (var name in preExisting)
            Directory.CreateDirectory(Path.Combine(localDir, name));

        // Seed local state so this restore itself also creates a new archive (a 5th folder).
        var seedPath = Path.Combine(localDir, "NaimitsuVault.nkdb");
        await File.WriteAllBytesAsync(seedPath, "seed"u8.ToArray(), TestContext.Current.CancellationToken);
        _tempFiles.Add(seedPath);

        var fileName = MakeValidVaultFileName();
        await CreateSeededVaultFileAsync(backupDir, fileName, "password-A");
        var nkdbPath = await CreateMinimalNkdbAsync("backup.nkdb", dbNumbers: [1]);

        using var unifiedDb = TestUnifiedDb.Create();
        var auth = MakeFileAndLocalUnifiedDbAuth(unifiedDb.Factory);
        await auth.RestoreAllFromBackupAsync(nkdbPath, backupDir, localDir);

        var remaining = Directory.GetDirectories(localDir, "archived_*")
            .Select(Path.GetFileName)
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(3, remaining.Count);
        // The newest 3 survive: the folder this restore just created, plus the 2 newest pre-existing ones.
        Assert.Contains("archived_20260104_000001", remaining);
        Assert.Contains("archived_20260103_000001", remaining);
        Assert.DoesNotContain("archived_20260102_000001", remaining);
        Assert.DoesNotContain("archived_20260101_000001", remaining);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // RestoreAllFromBackupAsync : Windows Hello invalidation (Rev.22)
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    [Fact]
    public async Task RestoreAllFromBackupAsync_ExistingWindowsHelloCredentials_InvalidatesThemAfterRestore()
    {
        var backupDir = Path.Combine(_tempDir, "backup");
        var localDir  = Path.Combine(_tempDir, "local");
        Directory.CreateDirectory(backupDir);
        Directory.CreateDirectory(localDir);

        var fileName = MakeValidVaultFileName();
        await CreateSeededVaultFileAsync(backupDir, fileName, "password-A");
        var nkdbPath = await CreateMinimalNkdbAsync("backup.nkdb", dbNumbers: [1]);

        using var unifiedDb = TestUnifiedDb.Create();
        await using (var udb = unifiedDb.Factory.CreateDbContext())
        {
            udb.Metadata.Add(new UnifiedMetadata
            {
                ConfigKey   = UnifiedMetadataKey.KSharedHello,
                ConfigValue = [1, 2, 3],
            });
            await udb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var auth = MakeFileAndLocalUnifiedDbAuth(unifiedDb.Factory);
        await auth.RestoreAllFromBackupAsync(nkdbPath, backupDir, localDir);

        await using var verifyUdb = unifiedDb.Factory.CreateDbContext();
        var stillExists = await verifyUdb.Metadata.AnyAsync(
            s => s.ConfigKey == UnifiedMetadataKey.KSharedHello, TestContext.Current.CancellationToken);
        Assert.False(stillExists);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // GetLocalVaultDbNumbersAsync : plaintext enumeration of local VaultRegistries.DbNumber
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    [Fact]
    public async Task GetLocalVaultDbNumbersAsync_ReturnsAllRegisteredDbNumbers()
    {
        var crypto  = new CryptoService(NullLogger<CryptoService>.Instance);
        var kShared = RandomNumberGenerator.GetBytes(32);

        using var unifiedDb = TestUnifiedDb.Create();
        await using (var udb = unifiedDb.Factory.CreateDbContext())
        {
            udb.VaultRegistries.Add(new VaultRegistry { DbNumber = 1, EncryptedPayload = crypto.Encrypt([1], kShared) });
            udb.VaultRegistries.Add(new VaultRegistry { DbNumber = 3, EncryptedPayload = crypto.Encrypt([2], kShared) });
            await udb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var auth = MakeFileAndLocalUnifiedDbAuth(unifiedDb.Factory);
        var dbNumbers = await auth.GetLocalVaultDbNumbersAsync();

        Assert.Equal([1, 3], dbNumbers.OrderBy(n => n));

        CryptographicOperations.ZeroMemory(kShared);
    }

    [Fact]
    public async Task GetLocalVaultDbNumbersAsync_NoRegistries_ReturnsEmpty()
    {
        using var unifiedDb = TestUnifiedDb.Create();
        var auth = MakeFileAndLocalUnifiedDbAuth(unifiedDb.Factory);

        Assert.Empty(await auth.GetLocalVaultDbNumbersAsync());
    }
}
