// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Helpers;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Files under data/ that nothing refers to are moved to data/orphaned_*/ instead of being deleted,
/// and an nkdb that lost its vault registration is restored from its shadow at startup (TC-OFE-01..15).
///
/// Background: copying a freshly extracted app folder that had already been launched once over an
/// existing portable installation replaced NaimitsuVault.nkdb with an empty one. Going through
/// first-time setup in that state made every existing vault look unregistered, and the cleanup that
/// ran on exit and on the next unlock deleted the old nkdb shadow, the old vault shadows, and the old
/// vault DB files without asking. TC-OFE-15 replays that sequence end to end.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class OrphanedFileEvacuationTests : IDisposable
{
    private readonly string _testDir;
    private readonly StubVaultConnectionProvider _connectionProvider = new();
    private readonly ShadowFileService _service;

    private const string NkdbName = "NaimitsuVault.nkdb";

    public OrphanedFileEvacuationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"naimitsu_orphaned_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _service = new ShadowFileService(_connectionProvider, NullLogger<ShadowFileService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    // ── TC-OFE-01 ─────────────────────────────────────────────────────────────
    // A file and its -wal/-shm sidecars land in one orphaned_* folder under their original names.

    [Fact]
    public void TryEvacuate_MovesFileAndSidecarsIntoOneFolder()
    {
        var file = Path.Combine(_testDir, "some_file");
        File.WriteAllBytes(file, [1]);
        File.WriteAllBytes(file + "-wal", [2]);
        File.WriteAllBytes(file + "-shm", [3]);

        var evacuator = NewEvacuator();
        Assert.True(evacuator.TryEvacuate(file));

        var folder = Path.Combine(_testDir, evacuator.FolderName!);
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(file + "-wal"));
        Assert.False(File.Exists(file + "-shm"));
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(folder, "some_file")));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(folder, "some_file-wal")));
        Assert.Equal([3], File.ReadAllBytes(Path.Combine(folder, "some_file-shm")));
        Assert.Equal(1, evacuator.MovedCount);
    }

    // ── TC-OFE-02 ─────────────────────────────────────────────────────────────
    // Every file moved through one evacuator shares the folder; a second evacuator created within
    // the same second gets a suffixed folder instead of reusing the first one.

    [Fact]
    public void TryEvacuate_OneFolderPerBatch_SecondBatchGetsItsOwnFolder()
    {
        var a = Path.Combine(_testDir, "a");
        var b = Path.Combine(_testDir, "b");
        var c = Path.Combine(_testDir, "c");
        File.WriteAllBytes(a, [1]);
        File.WriteAllBytes(b, [1]);
        File.WriteAllBytes(c, [1]);

        var first = NewEvacuator();
        first.TryEvacuate(a);
        first.TryEvacuate(b);
        var second = NewEvacuator();
        second.TryEvacuate(c);

        Assert.Equal(2, Directory.GetFiles(Path.Combine(_testDir, first.FolderName!)).Length);
        Assert.NotEqual(first.FolderName, second.FolderName);
        Assert.Single(Directory.GetFiles(Path.Combine(_testDir, second.FolderName!)));
    }

    // ── TC-OFE-03 ─────────────────────────────────────────────────────────────
    // A file that cannot be moved stays where it is, and no empty folder is left behind.

    [Fact]
    public void TryEvacuate_WhenMoveFails_FileStaysAndNoEmptyFolderRemains()
    {
        var file = Path.Combine(_testDir, "locked_file");
        File.WriteAllBytes(file, [1]);

        var evacuator = NewEvacuator();
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(evacuator.TryEvacuate(file));
        }

        Assert.True(File.Exists(file));
        Assert.Null(evacuator.FolderName);
        Assert.Empty(Directory.GetDirectories(_testDir));
    }

    // ── TC-OFE-04 ─────────────────────────────────────────────────────────────
    // The folder name uses the Gregorian calendar whatever the current culture is.

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    public void TryEvacuate_FolderNameIsCultureInvariant(string cultureName)
    {
        var file = Path.Combine(_testDir, "culture_file");
        File.WriteAllBytes(file, [1]);

        var original = CultureInfo.CurrentCulture;
        string? folderName;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            var evacuator = NewEvacuator();
            evacuator.TryEvacuate(file);
            folderName = evacuator.FolderName;
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        var expectedDate = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        Assert.Matches(new Regex(@"^orphaned_\d{8}_\d{6}$"), folderName!);
        Assert.StartsWith("orphaned_" + expectedDate[..4], folderName);
    }

    // ── TC-OFE-05 ─────────────────────────────────────────────────────────────
    // Unregistered vault DB files are moved; registered ones, the nkdb, and non-vault files stay.
    // A second scan finds nothing: files inside orphaned_* are not scanned again.

    [Fact]
    public void EvacuateUnregisteredVaultFiles_MovesOnlyUnregisteredVaultFiles()
    {
        var (registeredPath, registeredHash) = NewVaultFile();
        var (unregisteredPath, _)            = NewVaultFile();
        File.WriteAllBytes(unregisteredPath + "-wal", [9]);
        var nkdbPath   = Path.Combine(_testDir, NkdbName);
        var shadowPath = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateNkdb(nkdbPath);
        CreateSqliteWithMagic(shadowPath, ShadowFileService.VAULT_SHADOW);

        var folderName = OrphanedFileEvacuator.EvacuateUnregisteredVaultFiles(
            _testDir, [registeredHash], NullLogger.Instance);

        Assert.NotNull(folderName);
        var folder = Path.Combine(_testDir, folderName);
        Assert.True(File.Exists(Path.Combine(folder, Path.GetFileName(unregisteredPath))));
        Assert.True(File.Exists(Path.Combine(folder, Path.GetFileName(unregisteredPath) + "-wal")));
        Assert.False(File.Exists(unregisteredPath));
        Assert.True(File.Exists(registeredPath));
        Assert.True(File.Exists(nkdbPath));
        Assert.True(File.Exists(shadowPath));

        Assert.Null(OrphanedFileEvacuator.EvacuateUnregisteredVaultFiles(
            _testDir, [registeredHash], NullLogger.Instance));
        Assert.Single(Directory.GetDirectories(_testDir));
    }

    // ── TC-OFE-06 ─────────────────────────────────────────────────────────────
    // WriteAll(): the shadow the vault pointed to until now is a generation change and is deleted;
    // any other unreferenced vault shadow is moved aside.

    [Fact]
    public void WriteAll_PreviousVaultShadowDeleted_OtherUnreferencedVaultShadowEvacuated()
    {
        var nkdbPath  = Path.Combine(_testDir, NkdbName);
        var vaultPath = Path.Combine(_testDir, "vault_origin_for_test.db");
        CreateSqliteWithMagic(vaultPath, 0);

        var previous = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        var foreign  = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateSqliteWithMagic(previous, ShadowFileService.VAULT_SHADOW);
        CreateSqliteWithMagic(foreign, ShadowFileService.VAULT_SHADOW);
        CreateNkdb(nkdbPath, registries: [(1, [0x01], Path.GetFileName(previous))]);

        _connectionProvider.ActiveVaultDbPath = vaultPath;
        _service.WriteAll(currentVaultDbNumber: 1, dataDir: _testDir);

        Assert.False(File.Exists(previous));
        Assert.Empty(FindRecursively(Path.GetFileName(previous)));
        Assert.False(File.Exists(foreign));
        Assert.Single(FindRecursively(Path.GetFileName(foreign)));
        Assert.Single(ShadowFileService.FindByMagic(_testDir, ShadowFileService.VAULT_SHADOW));
    }

    // ── TC-OFE-07 ─────────────────────────────────────────────────────────────
    // WriteAll(): an old nkdb shadow whose registrations are all still in the live nkdb is deleted.

    [Fact]
    public void WriteAll_OldNkdbShadowWithSameRegistrations_IsDeleted()
    {
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateNkdb(nkdbPath, slots: [(1, [0xAA])], registries: [(1, [0x01], null)]);
        var oldShadow = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateNkdb(oldShadow, slots: [(1, [0xAA])], registries: [(1, [0x01], null)], magic: ShadowFileService.NKDB_SHADOW);

        _connectionProvider.ActiveVaultDbPath = null;
        _service.WriteAll(dataDir: _testDir);

        Assert.Empty(FindRecursively(Path.GetFileName(oldShadow)));
        Assert.Single(ShadowFileService.FindByMagic(_testDir, ShadowFileService.NKDB_SHADOW));
        Assert.Empty(Directory.GetDirectories(_testDir));
    }

    // ── TC-OFE-08 ─────────────────────────────────────────────────────────────
    // WriteAll(): an old nkdb shadow that knows a vault the live nkdb does not is moved aside.

    [Fact]
    public void WriteAll_OldNkdbShadowWithLostRegistration_IsEvacuated()
    {
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateNkdb(nkdbPath, slots: [(1, [0xCC])], registries: [(1, [0x03], null)]);
        var oldShadow = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateNkdb(oldShadow, slots: [(1, [0xAA]), (2, [0xBB])], registries: [(1, [0x01], null), (2, [0x02], null)],
            magic: ShadowFileService.NKDB_SHADOW);

        _connectionProvider.ActiveVaultDbPath = null;
        _service.WriteAll(dataDir: _testDir);

        Assert.False(File.Exists(oldShadow));
        Assert.Single(FindRecursively(Path.GetFileName(oldShadow)));
        Assert.Single(ShadowFileService.FindByMagic(_testDir, ShadowFileService.NKDB_SHADOW));
    }

    // ── TC-OFE-09 ─────────────────────────────────────────────────────────────
    // WriteAll(): an old nkdb shadow that cannot be read is kept (moved aside), not deleted.

    [Fact]
    public void WriteAll_UnreadableOldNkdbShadow_IsEvacuatedNotDeleted()
    {
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateNkdb(nkdbPath, slots: [(1, [0xAA])], registries: [(1, [0x01], null)]);
        var oldShadow = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        File.WriteAllBytes(oldShadow, BuildHeaderOnlyFile(ShadowFileService.NKDB_SHADOW));

        _connectionProvider.ActiveVaultDbPath = null;
        _service.WriteAll(dataDir: _testDir);

        Assert.False(File.Exists(oldShadow));
        Assert.Single(FindRecursively(Path.GetFileName(oldShadow)));
    }

    // ── TC-OFE-10 ─────────────────────────────────────────────────────────────
    // Startup: an nkdb with no K_shared slot plus one healthy shadow that has one -> the shadow is
    // restored over the nkdb and the replaced nkdb is kept in orphaned_*.

    [Fact]
    public async Task TryRecoverUnregisteredNkdb_OneRegisteredShadow_RestoresAndKeepsReplacedNkdb()
    {
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateNkdb(nkdbPath);
        var shadow = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateNkdb(shadow, slots: [(1, [0xAA]), (2, [0xBB])], registries: [(1, [0x01], null), (2, [0x02], null)],
            magic: ShadowFileService.NKDB_SHADOW);

        var result = await ShadowFileService.TryRecoverUnregisteredNkdbAsync(_testDir, nkdbPath, NullLogger.Instance);

        Assert.Equal(ShadowFileService.UnregisteredNkdbRecovery.Restored, result);
        var restored = ShadowFileService.ReadRegistrations(nkdbPath);
        Assert.NotNull(restored);
        Assert.Equal(4, restored.Count);
        Assert.True(File.Exists(shadow));
        // The replaced (empty) nkdb sits in the orphaned_* folder.
        var kept = Assert.Single(Directory.GetFiles(_testDir, NkdbName, SearchOption.AllDirectories), p => p != nkdbPath);
        Assert.Empty(ShadowFileService.ReadRegistrations(kept)!);
    }

    // ── TC-OFE-11 ─────────────────────────────────────────────────────────────
    // Startup: no shadow, or only shadows without a K_shared slot -> nothing happens (a genuine
    // first launch proceeds to first-time setup).

    [Fact]
    public async Task TryRecoverUnregisteredNkdb_NoRegisteredShadow_DoesNothing()
    {
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateNkdb(nkdbPath);
        var emptyShadow = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateNkdb(emptyShadow, magic: ShadowFileService.NKDB_SHADOW);

        var result = await ShadowFileService.TryRecoverUnregisteredNkdbAsync(_testDir, nkdbPath, NullLogger.Instance);

        Assert.Equal(ShadowFileService.UnregisteredNkdbRecovery.NotApplicable, result);
        Assert.Empty(ShadowFileService.ReadRegistrations(nkdbPath)!);
        Assert.Empty(Directory.GetDirectories(_testDir));
    }

    // ── TC-OFE-12 ─────────────────────────────────────────────────────────────
    // Startup: an nkdb that already has a K_shared slot is left alone even if a shadow differs.

    [Fact]
    public async Task TryRecoverUnregisteredNkdb_NkdbAlreadyRegistered_DoesNothing()
    {
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateNkdb(nkdbPath, slots: [(1, [0xCC])], registries: [(1, [0x03], null)]);
        var shadow = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateNkdb(shadow, slots: [(1, [0xAA])], registries: [(1, [0x01], null)], magic: ShadowFileService.NKDB_SHADOW);

        var result = await ShadowFileService.TryRecoverUnregisteredNkdbAsync(_testDir, nkdbPath, NullLogger.Instance);

        Assert.Equal(ShadowFileService.UnregisteredNkdbRecovery.NotApplicable, result);
        Assert.Contains("slot|1|CC", ShadowFileService.ReadRegistrations(nkdbPath)!);
        Assert.Empty(Directory.GetDirectories(_testDir));
    }

    // ── TC-OFE-13 ─────────────────────────────────────────────────────────────
    // Startup: two shadows with a K_shared slot -> ambiguous; nothing is moved or overwritten.

    [Fact]
    public async Task TryRecoverUnregisteredNkdb_TwoRegisteredShadows_IsAmbiguousAndTouchesNothing()
    {
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateNkdb(nkdbPath);
        foreach (byte marker in new byte[] { 0xAA, 0xBB })
        {
            var shadow = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
            CreateNkdb(shadow, slots: [(1, [marker])], registries: [(1, [marker], null)], magic: ShadowFileService.NKDB_SHADOW);
        }

        var result = await ShadowFileService.TryRecoverUnregisteredNkdbAsync(_testDir, nkdbPath, NullLogger.Instance);

        Assert.Equal(ShadowFileService.UnregisteredNkdbRecovery.Ambiguous, result);
        Assert.Empty(ShadowFileService.ReadRegistrations(nkdbPath)!);
        Assert.Empty(Directory.GetDirectories(_testDir));
        Assert.Equal(2, ShadowFileService.FindByMagic(_testDir, ShadowFileService.NKDB_SHADOW).Count);
    }

    // ── TC-OFE-14 ─────────────────────────────────────────────────────────────
    // Startup: an nkdb file without the tables yet (created but never initialized) counts as
    // unregistered, so a registered shadow next to it is restored.

    [Fact]
    public async Task TryRecoverUnregisteredNkdb_NkdbWithoutTables_IsTreatedAsUnregistered()
    {
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateSqliteWithMagic(nkdbPath, 0);
        var shadow = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateNkdb(shadow, slots: [(1, [0xAA])], registries: [(1, [0x01], null)], magic: ShadowFileService.NKDB_SHADOW);

        var result = await ShadowFileService.TryRecoverUnregisteredNkdbAsync(_testDir, nkdbPath, NullLogger.Instance);

        Assert.Equal(ShadowFileService.UnregisteredNkdbRecovery.Restored, result);
        Assert.Contains("slot|1|AA", ShadowFileService.ReadRegistrations(nkdbPath)!);
    }

    // ── TC-OFE-15 ─────────────────────────────────────────────────────────────
    // The incident, replayed without the startup check having caught it: the nkdb was replaced by an
    // empty one and first-time setup registered a new vault 1. After the exit-time shadow write and
    // the next unlock's scan, every file of the two original vaults (their DB files, their shadows,
    // and the old nkdb shadow that still knows them) must still exist somewhere under data/.

    [Fact]
    public void ReplacedNkdbThenSetup_ExitAndNextUnlock_KeepEveryOriginalFile()
    {
        // The original installation: two vaults, their shadows, and an nkdb shadow that knows both.
        var (oldVault1, _) = NewVaultFile();
        var (oldVault2, _) = NewVaultFile();
        var oldVaultShadow1 = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        var oldVaultShadow2 = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateSqliteWithMagic(oldVaultShadow1, ShadowFileService.VAULT_SHADOW);
        CreateSqliteWithMagic(oldVaultShadow2, ShadowFileService.VAULT_SHADOW);
        var oldNkdbShadow = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        CreateNkdb(oldNkdbShadow,
            slots: [(1, [0xAA]), (2, [0xBB])],
            registries: [(1, [0x01], Path.GetFileName(oldVaultShadow1)), (2, [0x02], Path.GetFileName(oldVaultShadow2))],
            magic: ShadowFileService.NKDB_SHADOW);

        // The replaced nkdb after first-time setup: it only knows the new vault 1.
        var (newVault, newVaultHash) = NewVaultFile();
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateNkdb(nkdbPath, slots: [(1, [0xCC])], registries: [(1, [0x03], null)]);

        string[] originals = [oldVault1, oldVault2, oldVaultShadow1, oldVaultShadow2, oldNkdbShadow];

        // App exit.
        _connectionProvider.ActiveVaultDbPath = newVault;
        _service.WriteAll(currentVaultDbNumber: 1, dataDir: _testDir);
        // Next unlock.
        OrphanedFileEvacuator.EvacuateUnregisteredVaultFiles(_testDir, [newVaultHash], NullLogger.Instance);

        foreach (var original in originals)
            Assert.Single(FindRecursively(Path.GetFileName(original)));
        // The new vault keeps working from data/ itself.
        Assert.True(File.Exists(newVault));
        Assert.True(File.Exists(nkdbPath));
        // None of the originals is left at the top level, where it could be mistaken for live state.
        foreach (var original in originals)
            Assert.False(File.Exists(original));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private OrphanedFileEvacuator NewEvacuator() => new(_testDir, NullLogger.Instance);

    private string[] FindRecursively(string fileName) =>
        Directory.GetFiles(_testDir, fileName, SearchOption.AllDirectories);

    // A vault DB file name is Base64Url("nkdb" + 32-byte FileHash); the registered-hash set holds
    // the standard Base64 of the FileHash.
    private (string Path, string FileHash) NewVaultFile()
    {
        var raw = new byte[36];
        "nkdb"u8.CopyTo(raw);
        System.Security.Cryptography.RandomNumberGenerator.Fill(raw.AsSpan(4));
        var name = Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var path = Path.Combine(_testDir, name);
        CreateSqliteWithMagic(path, 0);
        return (path, Convert.ToBase64String(raw[4..]));
    }

    private static void CreateSqliteWithMagic(string path, uint magic)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {magic}; CREATE TABLE IF NOT EXISTS _dummy (id INTEGER PRIMARY KEY);";
        cmd.ExecuteNonQuery();
    }

    // A unified DB file (the live nkdb, or a shadow when magic is NKDB_SHADOW) with the two tables
    // the app reads registrations from.
    private static void CreateNkdb(
        string path,
        (int Key, byte[] Value)[]? slots = null,
        (int DbNumber, byte[] Payload, string? ShadowFileName)[]? registries = null,
        uint magic = 0)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            create.CommandText = $@"
                PRAGMA user_version = {magic};
                CREATE TABLE UnifiedMetadata (ConfigKey INTEGER PRIMARY KEY, ConfigValue BLOB NOT NULL);
                CREATE TABLE VaultRegistries (
                    DbNumber INTEGER PRIMARY KEY,
                    LastAccessedAt INTEGER,
                    EncryptedPayload BLOB NOT NULL,
                    ShadowFileKey BLOB
                );";
            create.ExecuteNonQuery();
        }
        foreach (var (key, value) in slots ?? [])
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO UnifiedMetadata (ConfigKey, ConfigValue) VALUES (@key, @value)";
            insert.Parameters.AddWithValue("@key", key);
            insert.Parameters.AddWithValue("@value", value);
            insert.ExecuteNonQuery();
        }
        foreach (var (dbNumber, payload, shadowFileName) in registries ?? [])
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO VaultRegistries (DbNumber, EncryptedPayload, ShadowFileKey) VALUES (@db, @payload, @key)";
            insert.Parameters.AddWithValue("@db", dbNumber);
            insert.Parameters.AddWithValue("@payload", payload);
            insert.Parameters.AddWithValue("@key",
                shadowFileName == null ? DBNull.Value : ShadowFileService.FileNameToKey(shadowFileName)!);
            insert.ExecuteNonQuery();
        }
    }

    // 100 bytes that pass FindByMagic's header check (SQLite signature + user_version at offset 60)
    // but are not a readable database.
    private static byte[] BuildHeaderOnlyFile(uint magic)
    {
        var bytes = new byte[100];
        "SQLite format 3\0"u8.CopyTo(bytes);
        bytes[60] = (byte)(magic >> 24);
        bytes[61] = (byte)(magic >> 16);
        bytes[62] = (byte)(magic >> 8);
        bytes[63] = (byte)magic;
        return bytes;
    }
}
